using System;
using Dragonglass.Hud;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Nova.Telemetry;

// Live rotating 3-D part preview for the editor parts-list popup,
// composited into Nova's `<PunchThrough id="novaPartPreview">` slot via
// Dragonglass's native plugin.
//
// Pipeline (mirrors Dragonglass.Hud's PortraitCapture):
//   1. Off-world orthographic Camera on the "UIAdditional" layer
//      (same setup Dragonglass.Telemetry's PartIconCapture uses), driven
//      by manual `camera.Render()` per Update().
//   2. Active part's `iconPrefab` is instantiated under the camera,
//      `SetLayerRecursive` to "UIAdditional" so it never leaks into the
//      player's main view. Rotated around Y each frame.
//   3. Render → ReadPixels into a reusable Texture2D → push the raw
//      RGBA8 bytes to the native plugin via
//      DgHudNative_PushStreamFrame(idHash, w, h, ptr). The plugin
//      composites the texture under the chroma-keyed `<PunchThrough>`
//      rect.
//
// One stream, retargeted per hover. The native plugin holds a single
// GPU texture for the "novaPartPreview" id; SetActivePart swaps the
// prefab clone, ClearActivePart drops it and unregisters the stream so
// no leftover composite ghost survives mouseout.
//
// Editor-scoped: `KSPAddon(Startup.EditorAny, false)`. On scene exit
// the addon is destroyed; OnDestroy releases the camera/RT/clone and
// removes the stream. Re-entering the editor recreates everything
// fresh.
//
// Calls go through `Dragonglass.Hud.NativeBridge` rather than declaring
// our own [DllImport]. Mono's PInvoke resolver is per-assembly: it
// looks for the native library relative to the *declaring* DLL, so a
// [DllImport("DgHudNative")] in Nova.dll would search GameData/Nova/
// while the binary actually lives under GameData/Dragonglass_Hud/Plugins/.
// DG's NativeBridge has the library bound from the right context, so
// piggybacking on it gets us the correct dlopen() for free.
[KSPAddon(KSPAddon.Startup.EditorAny, false)]
public class NovaPartPreviewCapture : MonoBehaviour {
  private const string LogPrefix = "[Nova/PartPreview] ";

  // Physical pixels. Slot is 96 CSS px in the popup; at DPR=2 the
  // rect encodes as 192 physical, so the texture maps 1:1. At DPR=1
  // the native plugin downscales 192→96; minor cost, sharp enough.
  private const int Size = 192;
  private const string StreamId = "novaPartPreview";
  // ~one revolution every 8 s — slow enough to read structural detail,
  // fast enough that the rotation is unmistakable on first glance.
  private const float YawDegPerSec = 45f;

  public static NovaPartPreviewCapture Instance { get; private set; }

  private static readonly uint StreamIdHash = Fnv1a32(StreamId);

  private Camera _camera;
  private RenderTexture _rt;
  private Texture2D _readback;
  private GameObject _clone;
  private float _yaw;
  private int _layerId;
  private bool _started;
  // Push diagnostics: log once per active-part on the 1st and 60th push
  // so we can see in KSP.log whether the capture pipeline is running
  // and reaching the native plugin without spamming every frame.
  private int _pushCount;

  void Awake() {
    Instance = this;
  }

  void Start() {
    // "UIAdditional" is the layer stock KSP uses for its own part-icon
    // capture in PartListTooltipMasterController. Cull-masking the
    // camera to that layer (and parking the clone on it) means the
    // player's main camera never sees the spinning preview.
    _layerId = LayerMask.NameToLayer("UIAdditional");
    if (_layerId < 0) _layerId = LayerMask.NameToLayer("UI");
    if (_layerId < 0) {
      Debug.LogWarning(LogPrefix + "neither UIAdditional nor UI layer present; preview disabled");
      enabled = false;
      return;
    }

    // Camera config copied verbatim from stock's PartListTooltipMaster
    // Controller.CreateThumbnailCamera (~/dev/ksp-reference). Same
    // orthographicSize, same far clip, same off-world position, same
    // allowHDR=false. Stock uses the editor scene's existing
    // directional lights for shading; they're infinite-range, so an
    // off-world camera still picks them up.
    var camHost = new GameObject("NovaPartPreviewCamera");
    camHost.transform.position = new Vector3(0f, -2000f, -300f);
    DontDestroyOnLoad(camHost);
    _camera = camHost.AddComponent<Camera>();
    _camera.orthographic     = true;
    _camera.orthographicSize = 34f;
    _camera.farClipPlane     = 295f;
    _camera.cullingMask      = 1 << _layerId;
    _camera.clearFlags       = CameraClearFlags.Color;
    // Opaque dark navy matching the popup's interior (`--bg`).
    // The stream texture composites into the chroma-keyed CEF rect; a
    // transparent backdrop would let the KSP scene behind CEF show
    // through anywhere the part doesn't cover, so the backdrop has to
    // be opaque. Same approach stock's tooltip uses (its clear color
    // is set in the Unity inspector to a similar dark shade).
    _camera.backgroundColor  = new Color(4f / 255f, 7f / 255f, 16f / 255f, 1f);
    _camera.allowHDR         = false;
    _camera.enabled          = false;                     // manual Render only

    _rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32);
    _rt.Create();
    _camera.targetTexture = _rt;

    _readback = new Texture2D(Size, Size, TextureFormat.RGBA32, mipChain: false, linear: false);
    _started = true;
    Debug.Log(LogPrefix + "started; layer=" + LayerMask.LayerToName(_layerId)
        + " size=" + Size + " idHash=0x" + StreamIdHash.ToString("x8"));
  }

  /// <summary>
  /// Set the part whose `iconPrefab` should be rendered into the
  /// preview stream. `Update()` will pick it up next frame, render
  /// it, push the result to the native plugin, and (on the first
  /// successful push) notify `NovaPartInfoTopic` to flip its
  /// preview-ready state — only then does the topic emit a wire
  /// frame, so the UI never mounts `<PunchThrough>` against a
  /// missing texture.
  /// </summary>
  public void SetActivePart(AvailablePart ap) {
    if (!_started || ap == null || ap.iconPrefab == null) {
      DestroyClone();
      return;
    }
    DestroyClone();
    var clone = Instantiate(ap.iconPrefab);
    clone.name = "NovaPartPreview." + ap.name;
    clone.transform.SetParent(_camera.transform, worldPositionStays: false);
    clone.transform.localPosition = new Vector3(0f, 0f, 50f);
    clone.transform.localScale    = Vector3.one * 50f;
    SetLayerRecursive(clone, _layerId);
    clone.SetActive(true);
    _clone = clone;
    _yaw = 0f;
    _pushCount = 0;
    Debug.Log(LogPrefix + "SetActivePart: " + ap.name);
  }

  /// <summary>
  /// Stop rendering new frames. We DELIBERATELY don't call
  /// `DgHudNative_RemoveStream` here: removing the stream texture from
  /// the plugin's cache before the UI's `<PunchThrough>` rect leaves
  /// the DOM would leave a chroma rect with no texture, and the plugin
  /// would composite that as the chroma fill (visible magenta flash on
  /// close). The cached texture stays valid until `OnDestroy` runs at
  /// scene exit; while no rect references it, no compositing happens,
  /// so the cached bytes are effectively invisible. On the next hover
  /// the new part's frame replaces the cached texture under the same
  /// id, so there's no stale-content risk either.
  /// </summary>
  public void ClearActivePart() {
    DestroyClone();
  }

  void Update() {
    if (_clone == null || !_started) return;

    _yaw = (_yaw + Time.deltaTime * YawDegPerSec) % 360f;
    // -15° pitch matches the 3/4 view PartIconCapture uses for the
    // catalog icons, so a stationary frame here looks the same as the
    // static icon — animation is just the Y rotation.
    _clone.transform.localRotation = Quaternion.Euler(-15f, _yaw, 0f);
    _camera.Render();

    var prev = RenderTexture.active;
    RenderTexture.active = _rt;
    try {
      _readback.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
      _readback.Apply(updateMipmaps: false);
    } finally {
      RenderTexture.active = prev;
    }

    NativeArray<byte> raw = _readback.GetRawTextureData<byte>();
    unsafe {
      IntPtr ptr = (IntPtr)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(raw);
      try {
        NativeBridge.DgHudNative_PushStreamFrame(StreamIdHash, Size, Size, ptr);
        _pushCount++;
        if (_pushCount == 1 || _pushCount == 60) {
          Debug.Log(LogPrefix + "push #" + _pushCount + " idHash=0x"
              + StreamIdHash.ToString("x8") + " " + Size + "x" + Size);
        }
        // First successful push for this clone — the texture is now in
        // the plugin's cache, so the topic can release its pending
        // hover frame to the UI. The topic ignores the call if hover
        // has since been cleared, or if a later clone has reset the
        // counter again.
        if (_pushCount == 1) {
          NovaPartInfoTopic.NotifyPreviewReady();
        }
      } catch (DllNotFoundException) {
        Debug.LogWarning(LogPrefix + "DgHudNative not loaded; disabling preview capture");
        enabled = false;
      } catch (System.Exception ex) {
        Debug.LogWarning(LogPrefix + "push failed: " + ex.GetType().Name + " " + ex.Message);
        enabled = false;
      }
    }
  }

  void OnDestroy() {
    DestroyClone();
    try {
      NativeBridge.DgHudNative_RemoveStream(StreamIdHash);
    } catch (DllNotFoundException) {
      // Plugin missing — nothing to clean up native-side.
    }
    if (_rt != null) {
      _rt.Release();
      Destroy(_rt);
      _rt = null;
    }
    if (_readback != null) {
      Destroy(_readback);
      _readback = null;
    }
    if (_camera != null) {
      Destroy(_camera.gameObject);
      _camera = null;
    }
    if (Instance == this) Instance = null;
  }

  private void DestroyClone() {
    if (_clone != null) {
      // Immediate, not deferred — deferred Destroy would leave the
      // clone parented under the camera for an extra frame and the
      // next Render() would compose two parts (the old + the new).
      DestroyImmediate(_clone);
      _clone = null;
    }
  }

  private static void SetLayerRecursive(GameObject go, int layer) {
    go.layer = layer;
    var t = go.transform;
    for (int i = 0; i < t.childCount; i++) {
      SetLayerRecursive(t.GetChild(i).gameObject, layer);
    }
  }

  // FNV-1a 32-bit hash. Must match the C++ plugin and the TS encoder
  // byte-for-byte — they both hash the UTF-8 bytes with the same magic
  // constants. ASCII-only stream ids hash identically across all three.
  private static uint Fnv1a32(string s) {
    uint h = 0x811C9DC5u;
    var bytes = System.Text.Encoding.UTF8.GetBytes(s);
    for (int i = 0; i < bytes.Length; i++) {
      h ^= bytes[i];
      h *= 0x01000193u;
    }
    return h;
  }

}

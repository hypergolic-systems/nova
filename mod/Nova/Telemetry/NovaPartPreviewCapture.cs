using System;
using System.Runtime.InteropServices;
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
// The P/Invoke surface targets the same DgHudNative binary
// Dragonglass.Hud ships — `Dragonglass_Hud/Plugins/DgHudNative.{dylib,
// dll}`. Dragonglass.Hud's own `NativeBridge` is `internal`, so Nova
// declares its own DllImport wrappers for the two calls it needs
// (PushStreamFrame, RemoveStream). Both managed wrappers bind to the
// same native symbol — no conflict, no duplicate load.
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
      Debug.LogWarning(LogPrefix + "Neither UIAdditional nor UI layer present; preview disabled");
      enabled = false;
      return;
    }

    var camHost = new GameObject("NovaPartPreviewCamera");
    camHost.transform.position = new Vector3(0f, -2000f, -300f);
    DontDestroyOnLoad(camHost);
    _camera = camHost.AddComponent<Camera>();
    _camera.orthographic     = true;
    _camera.orthographicSize = 34f;
    _camera.nearClipPlane    = 0.1f;
    _camera.farClipPlane     = 295f;
    _camera.cullingMask      = 1 << _layerId;
    _camera.clearFlags       = CameraClearFlags.Color;
    _camera.backgroundColor  = new Color(0f, 0f, 0f, 0f); // transparent — keeps slot edges clean
    _camera.enabled          = false;                     // manual Render only

    _rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32);
    _rt.Create();
    _camera.targetTexture = _rt;

    _readback = new Texture2D(Size, Size, TextureFormat.RGBA32, mipChain: false, linear: false);
    _started = true;
  }

  /// <summary>
  /// Begin rendering the supplied `AvailablePart`'s prefab into the
  /// preview stream. Replaces any previously active part.
  /// </summary>
  public void SetActivePart(AvailablePart ap) {
    if (!_started || ap == null || ap.iconPrefab == null) return;
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
  }

  /// <summary>
  /// Stop pushing frames and drop the native plugin's stream so the
  /// compositor immediately stops drawing the slot.
  /// </summary>
  public void ClearActivePart() {
    DestroyClone();
    try {
      DgHudNative_RemoveStream(StreamIdHash);
    } catch (DllNotFoundException) {
      // No Dragonglass native plugin available — preview already won't
      // render, nothing to unregister. Swallow rather than spam.
    }
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
        DgHudNative_PushStreamFrame(StreamIdHash, Size, Size, ptr);
      } catch (DllNotFoundException) {
        // No native plugin — disable so we don't try every frame.
        Debug.LogWarning(LogPrefix + "DgHudNative not loaded; disabling preview capture");
        enabled = false;
      }
    }
  }

  void OnDestroy() {
    DestroyClone();
    try {
      DgHudNative_RemoveStream(StreamIdHash);
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

  // --- P/Invoke surface (subset of DgHudNative; Dragonglass.Hud's own
  // NativeBridge is `internal` so we declare the calls we need here.
  // Same Lib name → same dlopen() in Mono → no double-load.)

  private const string Lib = "DgHudNative";

  [DllImport(Lib)]
  private static extern void DgHudNative_PushStreamFrame(
    uint idHash, int width, int height, IntPtr rgbaBytes);

  [DllImport(Lib)]
  private static extern void DgHudNative_RemoveStream(uint idHash);
}

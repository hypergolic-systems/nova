using System;
using System.Collections.Generic;
using System.IO;
using Nova.Ffi.Generated;
using Nova.Persistence;
using UnityEngine;

namespace Nova.Ffi;

/// <summary>
/// Owns the singleton Rust <c>NovaWorld*</c> for the game session.
/// Vessels register themselves at load time and receive a
/// <see cref="NovaVesselHandle"/> they can use to read/write per-
/// component state through the shared arena.
///
/// Lifetime: KSPAddon.Startup.Instantly + once=true so the world
/// outlives scene transitions. Recreated on KSP exit naturally
/// because Unity tears down the GameObject and we free the world
/// pointer in <c>OnDestroy</c>.
/// </summary>
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public sealed unsafe class NovaWorldAddon : MonoBehaviour {
  public static NovaWorldAddon Instance { get; private set; }

  private NovaWorld* _world;
  private bool _initialized;

  // FFI vessel id (compact, monotonic) → handle. The vessel id we
  // hand to Rust is the proto's persistent_id, which is stable
  // across save/load.
  private readonly Dictionary<uint, NovaVesselHandle> _byVesselId = new();

  public NovaWorld* World => _world;

  private void Awake() {
    if (Instance != null && Instance != this) {
      Destroy(gameObject);
      return;
    }
    Instance = this;
    DontDestroyOnLoad(gameObject);

    try {
      _world = NovaNative.nova_world_create();
      _initialized = true;
      NovaLog.Log("NovaWorld created");
    } catch (Exception e) {
      NovaLog.LogError($"failed to create NovaWorld: {e.Message}");
    }
  }

  private void OnDestroy() {
    if (Instance != this) return;
    Instance = null;

    foreach (var h in _byVesselId.Values) h?.Dispose();
    _byVesselId.Clear();

    if (_world != null) {
      NovaNative.nova_world_destroy(_world);
      _world = null;
    }
    _initialized = false;
  }

  /// <summary>
  /// Push the prefab part database to Rust. Lazily called from
  /// <see cref="RegisterVessel"/> the first time it runs (by which
  /// time <c>PartLoader.LoadedPartsList</c> is fully populated).
  /// Idempotent within a session; re-call manually after a cfg
  /// reload to refresh.
  /// </summary>
  public void SetPartDatabase(byte[] dbBytes) {
    if (!_initialized || _world == null || dbBytes == null) return;
    fixed (byte* p = dbBytes) {
      int rc = NovaNative.nova_world_set_part_database(_world, p, (uint)dbBytes.Length);
      if (rc != 0) NovaLog.LogError($"nova_world_set_part_database rc={rc}");
    }
  }

  private bool _partDatabasePushed;
  private void EnsurePartDatabasePushed() {
    if (_partDatabasePushed) return;
    if (PartLoader.Instance == null || PartLoader.LoadedPartsList == null) return;
    var db = NovaVesselBuilder.BuildPartDatabase();
    using var ms = new MemoryStream();
    ProtoBuf.Serializer.Serialize(ms, db);
    SetPartDatabase(ms.ToArray());
    _partDatabasePushed = true;
    NovaLog.Log($"Pushed PartDatabase: {db.Parts.Count} entries");
  }

  /// <summary>
  /// Per-frame world tick. Called by <c>NovaVesselModule.FixedUpdate</c>
  /// — first vessel module to run each frame triggers the world advance.
  /// Idempotent within a frame (guarded by <c>_lastTickedUT</c>).
  /// </summary>
  private double _lastTickedUT = double.NegativeInfinity;
  public void Tick(double targetUT) {
    if (!_initialized || _world == null) return;
    if (targetUT <= _lastTickedUT) return;
    NovaNative.nova_world_tick(_world, targetUT);
    _lastTickedUT = targetUT;
  }

  /// <summary>
  /// Build a Rust-side vessel from serialized
  /// <c>Proto.VesselStructure</c> + <c>Proto.VesselState</c> bytes.
  /// The same proto pair already used for `.nvs` saves crosses the
  /// FFI verbatim — no separate ConfigNode walk on the Rust side.
  ///
  /// Returns the wrapped handle, or <c>null</c> on error. Disposes
  /// the prior handle for this <paramref name="vesselId"/> if any.
  /// </summary>
  public NovaVesselHandle RegisterVessel(uint vesselId, byte[] structureBytes, byte[] stateBytes, double initialUT) {
    if (!_initialized || _world == null) return null;
    if (structureBytes == null || stateBytes == null) return null;

    EnsurePartDatabasePushed();

    if (_byVesselId.TryGetValue(vesselId, out var existing)) {
      existing?.Dispose();
      _byVesselId.Remove(vesselId);
    }

    VesselHandle raw;
    fixed (byte* sp = structureBytes)
    fixed (byte* tp = stateBytes) {
      raw = NovaNative.nova_vessel_new(
          _world,
          sp, (uint)structureBytes.Length,
          tp, (uint)stateBytes.Length,
          initialUT);
    }
    if (raw.ArenaBase == null) {
      NovaLog.LogError("nova_vessel_new returned NULL handle");
      return null;
    }

    var handle = new NovaVesselHandle(_world, raw);
    _byVesselId[vesselId] = handle;
    return handle;
  }

  public void UnregisterVessel(uint vesselId) {
    if (_byVesselId.TryGetValue(vesselId, out var h)) {
      h?.Dispose();
      _byVesselId.Remove(vesselId);
    }
  }
}

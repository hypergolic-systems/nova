using System;
using System.Collections.Generic;
using Nova.Ffi.Generated;

namespace Nova.Ffi;

/// <summary>
/// C# wrapper around a single Rust-owned <see cref="VesselHandle"/>.
/// Holds the raw arena pointers + a slot lookup table, and exposes a
/// typed <see cref="GetState{T}"/> that returns a <c>ref T</c> view
/// directly into the arena memory. Part modules never see raw
/// pointers — only typed refs that read/write through the buffer
/// without copies.
///
/// Lifetime is tied to the underlying <c>FfiVessel</c> on the Rust
/// side: invoke <see cref="Dispose"/> (or rely on <c>Finalize</c>) to
/// trigger <c>nova_vessel_remove</c>. After dispose, every cached
/// <c>ref T</c> from this handle is invalid.
/// </summary>
public sealed unsafe class NovaVesselHandle : IDisposable {
  private readonly NovaWorld* _world;
  private VesselHandle _raw;
  // (part_id × kind tag) → byte pointer into the arena.
  private readonly Dictionary<(uint partId, uint kind), IntPtr> _slots;
  private bool _disposed;

  internal NovaVesselHandle(NovaWorld* world, VesselHandle raw) {
    _world = world;
    _raw = raw;

    _slots = new Dictionary<(uint, uint), IntPtr>((int)raw.SlotCount);
    for (int i = 0; i < raw.SlotCount; i++) {
      ComponentSlot slot = raw.SlotsPtr[i];
      IntPtr ptr = (IntPtr)(raw.ArenaBase + slot.StateOffset);
      _slots[(slot.PartId, slot.Kind)] = ptr;
    }
  }

  /// <summary>
  /// Vessel id (mirrors KSP's <c>vessel.id.GetHashCode()</c> or
  /// however the C# side keys vessels — exact mapping decided when
  /// <c>NovaVesselModule</c> wiring lands).
  /// </summary>
  public uint VesselId => _raw.VesselId;

  /// <summary>
  /// Typed <c>ref</c> into the arena slot for <c>(partFlightId,
  /// typeof(T))</c>. Reads and writes through the ref hit the buffer
  /// directly — no copies, no marshalling. Throws if no slot exists
  /// (caller violated the C#↔Rust schema agreement).
  /// </summary>
  public ref T GetState<T>(uint partFlightId) where T : unmanaged {
    uint kind = ComponentKindFor(typeof(T));
    if (!_slots.TryGetValue((partFlightId, kind), out IntPtr ptr)) {
      throw new InvalidOperationException(
        $"no {typeof(T).Name} slot on part {partFlightId}");
    }
    return ref *(T*)ptr;
  }

  /// <summary>
  /// True if a slot of type <typeparamref name="T"/> exists for the
  /// given part. Useful for part modules that want to gracefully
  /// no-op when the Rust simulator doesn't recognize their part
  /// (e.g. components ported in a later phase).
  /// </summary>
  public bool HasState<T>(uint partFlightId) where T : unmanaged {
    return _slots.ContainsKey((partFlightId, ComponentKindFor(typeof(T))));
  }

  /// <summary>
  /// Map a managed mirror struct type to its Rust-side
  /// <c>ComponentKind</c> tag. Stays in sync with
  /// <c>crates/nova-ksp/src/arena.rs::ComponentKind</c>.
  /// </summary>
  private static uint ComponentKindFor(Type t) {
    if (t == typeof(BatteryState)) return 1;
    if (t == typeof(CommandState)) return 2;
    throw new InvalidOperationException(
      $"unknown FFI state type {t.Name} — add it to NovaVesselHandle.ComponentKindFor");
  }

  public void Dispose() {
    if (_disposed) return;
    _disposed = true;
    NovaNative.nova_vessel_remove(_world, _raw.VesselId);
    _raw = default;
    _slots.Clear();
  }

  ~NovaVesselHandle() {
    Dispose();
  }
}

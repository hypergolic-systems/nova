using Nova.Ffi;
using Nova.Ffi.Generated;

namespace Nova.Components;

/// <summary>
/// Battery part module. Phase-1 of the Rust cutover: state lives
/// entirely in the nova-sim simulator (Rust). This C# stub is just
/// the KSP attachment point — real state reads come from the
/// vessel's <see cref="NovaVesselHandle"/>.
/// </summary>
public class NovaBatteryModule : NovaPartModule {

  /// <summary>
  /// Live battery state for this part, read directly from the Rust
  /// arena. Returns a default-zeroed struct (Capacity=0, Contents=0)
  /// if the handle isn't ready yet — caller should treat as "no
  /// state available" rather than "zero charge."
  /// </summary>
  public BatteryState GetState() {
    var vm = vessel?.FindVesselModuleImplementing<NovaVesselModule>();
    var h = vm?.Handle;
    if (h == null || part == null) return default;
    if (!h.HasState<BatteryState>(part.persistentId)) return default;
    return h.GetState<BatteryState>(part.persistentId);
  }
}

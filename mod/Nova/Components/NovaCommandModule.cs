using CommNet;
using Nova.Ffi;
using Nova.Ffi.Generated;

namespace Nova.Components;

public class NovaCommandModule : NovaPartModule, ICommNetControlSource {
  public override void OnAwake() {
    base.OnAwake();
    part.isControlSource = Vessel.ControlLevel.FULL;
  }

  /// <summary>
  /// LP-solved share (0..1) of the avionics idle draw for this
  /// command part — read directly from the Rust arena.
  /// </summary>
  public CommandState GetState() {
    var vm = vessel?.FindVesselModuleImplementing<NovaVesselModule>();
    var h = vm?.Handle;
    if (h == null || part == null) return default;
    if (!h.HasState<CommandState>(part.persistentId)) return default;
    return h.GetState<CommandState>(part.persistentId);
  }

  // ICommNetControlSource — tells CommNet this part provides command capability
  string ICommNetControlSource.name => part?.partInfo?.title ?? "Command";
  public void UpdateNetwork() { }
  // TODO: check actual crew presence — unmanned pod should return ProbeNone/ProbeFull
  public VesselControlState GetControlSourceState() => VesselControlState.KerbalFull;
  public bool IsCommCapable() => true;
}

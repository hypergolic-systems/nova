using CommNet;

namespace Nova.Components;

public class NovaCommandModule : NovaPartModule, ICommNetControlSource {
  public override void OnAwake() {
    base.OnAwake();
    part.isControlSource = Vessel.ControlLevel.FULL;
  }

  // ICommNetControlSource — tells CommNet this part provides command capability
  string ICommNetControlSource.name => part?.partInfo?.title ?? "Command";
  public void UpdateNetwork() { }
  // TODO: check actual crew presence — unmanned pod should return ProbeNone/ProbeFull
  public VesselControlState GetControlSourceState() => VesselControlState.KerbalFull;
  public bool IsCommCapable() => true;
}

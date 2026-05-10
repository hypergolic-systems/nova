using CommNet;
using Nova.Core.Components.Control;

namespace Nova.Components;

// Probe-core counterpart of NovaCommandModule. Reports as a probe-
// class control source (no crew) so KSP's vessel-control aggregator
// distinguishes it from a crewed pod, and registers the probe's
// configured SAS service tier directly with the part's PartValues
// event channels — same surface stock `ModuleSAS` uses, which lets us
// strip ModuleSAS from probe-core overrides without losing autopilot
// capability.
public class NovaProbeModule : NovaPartModule, ICommNetControlSource {

  // Single shared callback wired into both AutopilotSkill (kerbal-pilot
  // skill aggregator) and AutopilotSASSkill (SAS-pod tier aggregator).
  // Both channels reduce with `max`, so registering on both matches
  // stock ModuleSAS's behavior — the probe's tier always sets the
  // vessel-level floor whether or not a kerbal pilot also chimes in.
  private EventValueComparison<int>.OnEvent sasSkillCallback;

  public override void OnAwake() {
    base.OnAwake();
    // Stock pairs ControlLevel.FULL with VesselControlState.ProbeFull
    // — the *Probe vs Kerbal* distinction lives on the CommNet
    // VesselControlState we report, not on Part.isControlSource.
    part.isControlSource = Vessel.ControlLevel.FULL;
  }

  public override void OnStart(StartState state) {
    base.OnStart(state);
    if (state == StartState.Editor) return;
    if (vessel == null) return;

    var probe = ResolveProbe();
    if (probe == null) return;

    // Captured closure over the live Probe component — the part's
    // Components list is reused across OnStart and FixedUpdate, so the
    // capture stays valid until OnDestroy unregisters.
    sasSkillCallback = () => probe.SasLevel;
    part.PartValues.AutopilotSkill.Add(sasSkillCallback);
    part.PartValues.AutopilotSASSkill.Add(sasSkillCallback);

    // KSP only re-fires `partValues.Update()` (which actually computes
    // the AutopilotSkill / AutopilotSASSkill aggregate from registered
    // callbacks) when the part has crew OR `overrideSkillUpdate=true`.
    // The flag is set by stock from a hardcoded list of module names
    // (`overrideSkillUpdateModules = "ModuleSAS, ModuleWheelSteering"`,
    // Part.cs:1294 + 2147). Probe cores have no crew, and we strip
    // ModuleSAS, so without this the registered SAS skill stays at the
    // -1 default forever and the autopilot UI reads "No operational
    // SAS". Set the flag directly — same end state stock reaches via
    // the module-name match.
    part.overrideSkillUpdate = true;
  }

  private void OnDestroy() {
    if (sasSkillCallback == null || part?.PartValues == null) return;
    part.PartValues.AutopilotSkill.Remove(sasSkillCallback);
    part.PartValues.AutopilotSASSkill.Remove(sasSkillCallback);
    sasSkillCallback = null;
  }

  private Probe ResolveProbe() {
    if (Components == null) return null;
    foreach (var c in Components)
      if (c is Probe p) return p;
    return null;
  }

  // ICommNetControlSource — tells CommNet this part provides command
  // capability. Today we report ProbeFull unconditionally; signal-gated
  // downgrade to ProbePartial / ProbeNone is a follow-up once link
  // state feeds in here.
  string ICommNetControlSource.name => part?.partInfo?.title ?? "Probe";
  public void UpdateNetwork() { }
  public VesselControlState GetControlSourceState() => VesselControlState.ProbeFull;
  public bool IsCommCapable() => true;
}

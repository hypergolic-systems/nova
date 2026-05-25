using System.Linq;
using Nova.Communications;
using Nova.Core.Components.Communications;

namespace Nova.Components;

// KSP-side wrapper for an antenna part. The Antenna virtual component
// (TxPower, Gain, MaxRate, RefDistance) is built by ComponentFactory
// from the cfg; this module is responsible for keeping the component's
// `IsDeployed` flag in sync with the part's deploy animation.
//
// Stock antennas keep their `ModuleDeployableAntenna` (the player can
// still extend/retract via PAW); Nova reads the live `deployState` and
// gates transmission through the comms graph. Fixed antennas (no
// stock deploy module) stay permanently deployed.
//
// No ICommAntenna — Nova owns its own graph; stock CommNet routing
// is bypassed.
public class NovaAntennaModule : NovaPartModule {

  private Antenna antenna;
  private ModuleDeployableAntenna stockDeploy;
  private bool lastDeployed = true;

  public override void OnStart(StartState state) {
    base.OnStart(state);

    antenna = Components?.OfType<Antenna>().FirstOrDefault();
    if (antenna == null || state == StartState.Editor) return;

    stockDeploy = part.FindModuleImplementing<ModuleDeployableAntenna>();
    if (stockDeploy == null) {
      antenna.IsDeployed = true;
      lastDeployed = true;
      return;
    }

    if (antenna.LoadedFromSave) {
      // Save load: Nova proto is source of truth. Push antenna.IsDeployed
      // into the stock module so its `deployState` matches what the
      // player saved — Nova's save path doesn't run stock OnLoad, so
      // without this `deployState` stays at the prefab default and the
      // FixedUpdate below would overwrite the loaded IsDeployed value
      // with `false`, taking the antenna offline despite the visual
      // pose suggesting it's extended.
      SyncStockFromVirtual();
      lastDeployed = antenna.IsDeployed;
    } else {
      // Fresh launch: stock deployState carries the editor-time intent
      // (RETRACTED by default, matching stock convention that the
      // player has to extend deployable antennas after launch). Pull
      // the value rather than push, so we don't override the cfg
      // default with the virtual component's `true` default.
      lastDeployed = IsExtended(stockDeploy);
      antenna.IsDeployed = lastDeployed;
    }
  }

  // Called after `VirtualComponent.Load` in the matched-vessel quickload
  // path (NovaSaveLoader.ApplyVesselState). Stock OnStart already ran
  // with whatever pre-quickload deployState was sitting on the module;
  // re-sync after Load has updated antenna.IsDeployed to the saved
  // value.
  public override void OnNovaStateRestored() {
    if (antenna == null || stockDeploy == null) return;
    SyncStockFromVirtual();
    lastDeployed = antenna.IsDeployed;
    NovaCommunicationsAddon.Instance?.Network?.Invalidate();
  }

  public void FixedUpdate() {
    if (antenna == null || stockDeploy == null) return;
    var nowDeployed = IsExtended(stockDeploy);
    if (nowDeployed == lastDeployed) return;
    antenna.IsDeployed = nowDeployed;
    lastDeployed = nowDeployed;
    // Deploy state change is a topology event for the comm graph —
    // pairwise rates and the per-vessel path-to-home may all change.
    NovaCommunicationsAddon.Instance?.Network?.Invalidate();
  }

  // Push antenna.IsDeployed → stock deployState and re-run startFSM so
  // the animation pose snaps to the right end-frame. startFSM is what
  // stock OnStart calls to set animation normalizedTime per deployState
  // — invoking it again with the corrected state is the cheapest way
  // to get the visual back in sync without re-implementing the pose
  // logic ourselves.
  private void SyncStockFromVirtual() {
    var target = antenna.IsDeployed
        ? ModuleDeployablePart.DeployState.EXTENDED
        : ModuleDeployablePart.DeployState.RETRACTED;
    if (stockDeploy.deployState == target) return;
    stockDeploy.deployState = target;
    stockDeploy.startFSM();
  }

  private static bool IsExtended(ModuleDeployableAntenna m) =>
    m.deployState == ModuleDeployablePart.DeployState.EXTENDED;
}

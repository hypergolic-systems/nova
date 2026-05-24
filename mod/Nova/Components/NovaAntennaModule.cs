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
    if (stockDeploy != null) {
      lastDeployed = IsExtended(stockDeploy);
      antenna.IsDeployed = lastDeployed;
    } else {
      antenna.IsDeployed = true;
      lastDeployed = true;
    }
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

  private static bool IsExtended(ModuleDeployableAntenna m) =>
    m.deployState == ModuleDeployablePart.DeployState.EXTENDED;
}

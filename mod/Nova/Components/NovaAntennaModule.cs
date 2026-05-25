using System.Linq;
using Nova.Communications;
using Nova.Core.Components.Communications;

namespace Nova.Components;

// KSP-side wrapper for an antenna part. The Antenna virtual component
// (TxPower, Gain, MaxRate, RefDistance) is built by ComponentFactory
// from the cfg; this module is responsible for keeping the component's
// `IsDeployed` flag in sync with the part's deploy animation.
//
// Stock antennas keep their `ModuleDeployableAntenna` for animation
// drive (we can't strip it without re-implementing the deploy clip),
// but its PAW surface (Extend/Retract events, ExtendAction/RetractAction
// action-group bindings, status readout) is suppressed in OnStart —
// player-facing deploy lives on `setAntennaDeployed` via NovaPartTopic.
// Fixed antennas (no stock deploy module) stay permanently deployed.
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
    if (antenna == null) return;

    stockDeploy = part.FindModuleImplementing<ModuleDeployableAntenna>();
    // Hide the stock module's PAW surface in *every* scene (editor,
    // flight, EVA-unfocused) so the player never sees a duplicate
    // control alongside Nova's UI. Done before the editor early-return
    // because the PAW is visible in the VAB too.
    if (stockDeploy != null) HideStockPaw(stockDeploy);

    if (state == StartState.Editor) return;

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

  // Suppress every player-visible affordance the stock deploy module
  // exposes. We can't strip the module (it owns the deploy animation),
  // but Nova's UI is the canonical deploy surface — duplicate buttons
  // confuse the player and let them bypass Nova's `setAntennaDeployed`
  // op (skipping any side effects like network invalidation that go
  // through it). guiActive/guiActiveEditor/guiActiveUnfocused on events
  // hide them from the PAW in flight / editor / EVA respectively; the
  // .active flag controls fireability (kept untouched), so any non-PAW
  // caller (Harmony, action groups stripped by .active=false on the
  // action) can still invoke if needed. Stock startFSM rewrites
  // .active on transitions but never .guiActive, so this stays sticky
  // across animation state changes — SetUIWrite is only invoked from
  // ModuleDockingNode, irrelevant to free-floating antennas.
  private static void HideStockPaw(ModuleDeployableAntenna m) {
    HideEvent(m.Events["Extend"]);
    HideEvent(m.Events["Retract"]);
    HideEvent(m.Events["EventRepairExternal"]);
    HideAction(m.Actions["ExtendAction"]);
    HideAction(m.Actions["RetractAction"]);
    HideAction(m.Actions["ExtendPanelsAction"]);
    HideField(m.Fields["status"]);
  }

  private static void HideEvent(BaseEvent e) {
    if (e == null) return;
    e.guiActive = false;
    e.guiActiveEditor = false;
    e.guiActiveUnfocused = false;
    e.guiActiveUncommand = false;
  }

  private static void HideAction(BaseAction a) {
    if (a == null) return;
    a.active = false;
    a.activeEditor = false;
  }

  private static void HideField(BaseField f) {
    if (f == null) return;
    f.guiActive = false;
    f.guiActiveEditor = false;
    f.guiActiveUnfocused = false;
  }
}

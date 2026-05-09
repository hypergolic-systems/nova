using System;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components.Thermal;

// Heat consumer. Drains the vessel heat bus at a config-declared
// rate, modulated by atmospheric pressure (folding rads benefit from
// convection in atm; fixed panels are vac-balanced and don't). Active
// radiators consume EC per W of cooling; passive panels declare 0.
//
// Critical priority puts radiators ahead of any future non-Critical
// heat consumer. The bus has no Buffer for Heat (RTG buffers are
// private), so when no producer is offering, this device's Activity
// drops to 0 — no need for explicit shutoff logic.
public class Radiator : VirtualComponent {
  public double VacuumCoolingW;
  public double AtmCoolingW;
  public double EcPerWattCooling;
  public bool   IsDeployable;
  public bool   IsDeployed = true;

  internal Device device;

  public double MaxCoolingW => Math.Max(VacuumCoolingW, AtmCoolingW);

  public double CurrentMaxCoolingW {
    get {
      var atm = Vessel?.Context?.StaticPressureAtm ?? 0;
      return VacuumCoolingW
           + (AtmCoolingW - VacuumCoolingW) * Math.Min(1.0, atm);
    }
  }

  public double CurrentCoolingW =>
    device == null ? 0.0 : device.Activity * MaxCoolingW;

  public override VirtualComponent Clone() {
    return (Radiator)MemberwiseClone();
  }

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    var maxCool = MaxCoolingW;
    if (maxCool <= 0) return;
    var maxEc = maxCool * EcPerWattCooling;
    var inputs = maxEc > 0
        ? new[] { (Resource.Heat, maxCool), (Resource.ElectricCharge, maxEc) }
        : new[] { (Resource.Heat, maxCool) };
    device = systems.AddDevice(node,
        inputs: inputs,
        priority: ProcessFlowSystem.Priority.Critical);
    device.Demand = IsDeployed ? CurrentMaxCoolingW / maxCool : 0;
  }

  public override void OnPreSolve() {
    if (device == null) return;
    if (!IsDeployed) { device.Demand = 0; return; }
    var maxCool = MaxCoolingW;
    device.Demand = CurrentMaxCoolingW / maxCool;
  }

  public override void Save(PartState state) {
    state.Radiator = new RadiatorState { IsDeployed = IsDeployed };
  }

  public override void Load(PartState state) {
    if (state.Radiator == null) return;
    IsDeployed = state.Radiator.IsDeployed;
  }
}

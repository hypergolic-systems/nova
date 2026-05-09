using System.Linq;
using Nova.Core.Components.Propulsion;

namespace Nova.Components;

public class NovaTankModule : NovaPartModule, IPartMassModifier {

  private TankVolume tankVolume;

  internal TankVolume TankVolume => tankVolume;

  public override void OnStart(StartState state) {
    base.OnStart(state);
    tankVolume = Components.OfType<TankVolume>().First();
  }

  public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) {
    if (tankVolume == null) return 0f;
    var massKg = 0.0;
    foreach (var tank in tankVolume.Tanks)
      massKg += tank.Contents * tank.Resource.Density;
    return (float)(massKg / 1000.0);
  }

  public ModifierChangeWhen GetModuleMassChangeWhen() {
    if (tankVolume != null) {
      foreach (var tank in tankVolume.Tanks) {
        if (tank.Rate != 0) return ModifierChangeWhen.CONSTANTLY;
      }
    }
    return ModifierChangeWhen.FIXED;
  }
}

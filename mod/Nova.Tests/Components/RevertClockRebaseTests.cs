using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components;
using Nova.Core.Components.Propulsion;
using Nova.Core.Resources;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Tests.Components;

// Repros the post-revert "infinite fuel" failure: the live VirtualVessel
// survives revert (matched-vessel path), but Planetarium.UT jumps
// backward. Without rebasing the SimClock to the new UT, every
// Buffer.Contents lerp freezes at its baseline because subsequent
// Tick(targetTime=newUT) is a no-op (simulationTime > targetTime, the
// while loop never fires, Clock never advances). Engines fire on stale
// Activity, drain rate displays via Buffer.Rate, but Contents stays at
// Capacity forever.
[TestClass]
public class RevertClockRebaseTests {

  private static Engine MakeEngine(double thrust, double isp) {
    var e = new Engine();
    e.Initialize(thrust, isp, new List<(Resource, double)> { (Resource.RP1, 1) });
    return e;
  }

  private static TankVolume MakeTank(double capacity) {
    return new TankVolume {
      Volume = capacity,
      MaxRate = 10000,
      Tanks = {
        new Buffer { Resource = Resource.RP1, Capacity = capacity, Contents = capacity },
      },
    };
  }

  private static VirtualVessel BuildVessel(double initUT) {
    var vv = new VirtualVessel();
    var parts = new (uint id, string name, uint? parent, double mass, List<VirtualComponent> cs)[] {
      (1u, "tank",   (uint?)null, 1.0, new List<VirtualComponent> { MakeTank(1000) }),
      (2u, "engine", (uint?)1u,   1.0, new List<VirtualComponent> { MakeEngine(60, 300) }),
    };
    var parentMap = new Dictionary<uint, uint?>();
    foreach (var p in parts) {
      vv.AddPart(p.id, p.name, p.mass, p.cs);
      parentMap[p.id] = p.parent;
    }
    vv.UpdatePartTree(parentMap);
    vv.InitializeSolver(initUT);
    return vv;
  }

  // The fix: after RebaseClock(newUT < currentSimTime), Tick(newUT+dt)
  // must actually advance Clock so subsequent Contents reads reflect the
  // engine's drain.
  [TestMethod]
  public void RebaseClock_AllowsSubsequentTickToAdvanceLerp() {
    var vv = BuildVessel(initUT: 0);
    var tank = (TankVolume)vv.AllComponents().First(c => c is TankVolume);
    var engine = (Engine)vv.AllComponents().First(c => c is Engine);

    // Activate engine + drain a bit.
    engine.Active = true;
    engine.Throttle = 1.0;
    vv.Tick(60);   // 60 s of burn
    var afterBurn = tank.Tanks[0].Contents;
    Assert.IsTrue(afterBurn < 1000, $"Tank should drain over first burn; got {afterBurn}");

    // Simulate revert: snapshot "Tank full at UT=0" replayed at the
    // live vessel. Planetarium.UT jumps back to 0; SimClock is still
    // at 60. Without RebaseClock, the Contents setter would baseline
    // at the stale UT=60.
    vv.RebaseClock(newUT: 0);
    tank.Tanks[0].Contents = 1000;   // simulate Tank.Load restoring full

    // Engine state preserved by the snapshot. Burn for another 60 s
    // from the snapshot UT. If Clock weren't rebased, the lerp would
    // freeze and Contents would stay at 1000.
    vv.Tick(60);
    var afterRevertBurn = tank.Tanks[0].Contents;
    Assert.IsTrue(afterRevertBurn < 1000,
        $"After revert + 60 s burn, tank should have drained again; got {afterRevertBurn}");
  }

  // Without the fix: Tick after a rewound Planetarium UT is a no-op,
  // and Contents stays at the (post-Load) baseline forever.
  [TestMethod]
  public void WithoutRebase_StaleClockFreezesLerp() {
    var vv = BuildVessel(initUT: 0);
    var tank = (TankVolume)vv.AllComponents().First(c => c is TankVolume);
    var engine = (Engine)vv.AllComponents().First(c => c is Engine);

    engine.Active = true;
    engine.Throttle = 1.0;
    vv.Tick(60);

    // Skip the rebase, just set Contents (the broken-revert flow).
    tank.Tanks[0].Contents = 1000;

    // Tick to a UT < simulationTime — should be a no-op.
    vv.Tick(30);
    Assert.AreEqual(1000, tank.Tanks[0].Contents, 1e-9,
        "Without rebase, a backward Tick should not advance the lerp.");
  }
}

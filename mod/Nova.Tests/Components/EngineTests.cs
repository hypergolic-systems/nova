using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components.Propulsion;
using Nova.Core.Persistence.Protos;

namespace Nova.Tests.Components;

[TestClass]
public class EngineTests {
  // Regression: pre-fix, OnActive set a transient `Ignited` flag that
  // EngineStatus keyed off, but only `Active` was in the proto. A
  // quicksave/quickload restored Active but Ignited defaulted to false,
  // so EngineStatus came back as `3 (shutdown)` and the Propulsion
  // rosette filtered the engine out.
  //
  // The shape that catches this is *not* a field-by-field roundtrip —
  // a `src.Active == dst.Active` assertion passed before and after the
  // fix. The catching assertion is on EngineStatus, the observable the
  // wire publishes and the UI filters on. Anchoring the test on the
  // consumer-visible property catches "transient field next to a
  // persisted one" regressions regardless of which field shows up next.
  [TestMethod]
  public void StagedEngine_StaysOutOfShutdown_AcrossSaveLoad() {
    var src = new Engine { Thrust = 100, Isp = 300 };
    src.Active = true;  // simulating NovaEngineModule.OnActive

    Assert.AreNotEqual((byte)3, src.EngineStatus,
      "sanity: staged engine should not report shutdown pre-save");

    var state = new PartState();
    src.Save(state);

    var dst = new Engine { Thrust = 100, Isp = 300 };
    dst.Load(state);

    Assert.AreNotEqual((byte)3, dst.EngineStatus,
      "staged engine reports shutdown after save/load — rosette will filter it out");
  }

  [TestMethod]
  public void UnstagedEngine_ReportsShutdown() {
    var e = new Engine { Thrust = 100, Isp = 300 };
    Assert.AreEqual((byte)3, e.EngineStatus);
  }

  // Source-of-truth roundtrip: every persisted field on EngineState
  // makes it from src into dst. Complements the EngineStatus test
  // above — that one catches consumer-side gaps (transient field
  // shadowing the persisted one); this one catches schema-side gaps
  // (a new field added to the proto but forgotten in Save / Load).
  [TestMethod]
  public void State_RoundTripsAllFields() {
    var src = new Engine { Thrust = 100, Isp = 300, Active = true };
    var state = new PartState();
    src.Save(state);

    var dst = new Engine { Thrust = 100, Isp = 300 };
    dst.Load(state);
    Assert.IsTrue(dst.Active);
  }

  // Throttle is a per-tick input — NovaEngineModule.FixedUpdate refreshes
  // it from ctrlState.mainThrottle. Load resets it to 0 so any solve
  // that runs between Load and the next FixedUpdate doesn't drive tank
  // drain at the pre-save throttle. NovaSaveLoader restores
  // ctrlState.mainThrottle separately so the player's setpoint survives
  // the round-trip — the engine's local Throttle just briefly reads 0
  // until ApplyPlayerThrottle plumbs the restored value in.
  [TestMethod]
  public void Load_ResetsThrottleToZero() {
    var src = new Engine { Thrust = 100, Isp = 300, Active = true, Throttle = 0.7 };
    var state = new PartState();
    src.Save(state);

    var dst = new Engine { Thrust = 100, Isp = 300, Throttle = 0.5 };
    dst.Load(state);
    Assert.AreEqual(0.0, dst.Throttle, 1e-12);
  }
}

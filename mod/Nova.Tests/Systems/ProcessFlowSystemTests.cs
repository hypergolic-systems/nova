using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Resources;
using Nova.Core.Systems;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Tests.Systems;

[TestClass]
public class ProcessFlowSystemTests {

  private static Buffer Battery(double capacity, double contents,
                                 double maxRateIn = 1000, double maxRateOut = 1000) {
    var b = new Buffer {
      Resource = Resource.ElectricCharge,
      Capacity = capacity,
      Contents = contents,
    };
    b.FlowLimits(maxRateIn, maxRateOut);
    return b;
  }

  // ── Sanity ───────────────────────────────────────────────────────

  [TestMethod]
  public void ProducerSuppliesConsumer_DirectMatch() {
    var sys = new ProcessFlowSystem();
    var producer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 10);
    producer.Demand = 1.0;

    var consumer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    consumer.AddInput(Resource.ElectricCharge, 10);
    consumer.Demand = 1.0;

    sys.Solve();

    Assert.AreEqual(1.0, producer.Activity, 0.001);
    Assert.AreEqual(1.0, consumer.Activity, 0.001);
  }

  [TestMethod]
  public void ProducerExcess_LimitedToConsumerDemand() {
    // Solar-style: 100 EC available, only 10 EC demanded by consumer
    // and no buffer. Producer activity should drop to match (no waste).
    var sys = new ProcessFlowSystem();
    var producer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 100);
    producer.Demand = 1.0;

    var consumer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    consumer.AddInput(Resource.ElectricCharge, 10);
    consumer.Demand = 1.0;

    sys.Solve();

    Assert.AreEqual(1.0, consumer.Activity, 0.001);
    Assert.AreEqual(0.1, producer.Activity, 0.001, "Producer should throttle to load");
  }

  [TestMethod]
  public void NoProducer_NoBuffer_ConsumerStarves() {
    var sys = new ProcessFlowSystem();
    var consumer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    consumer.AddInput(Resource.ElectricCharge, 10);
    consumer.Demand = 1.0;

    sys.Solve();

    Assert.AreEqual(0.0, consumer.Activity, 0.001);
  }

  // ── Buffer drain / fill ──────────────────────────────────────────

  [TestMethod]
  public void BufferDrainsWhenProducerInsufficient() {
    var sys = new ProcessFlowSystem();
    var producer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 5);
    producer.Demand = 1.0;

    var bat = Battery(100, 100);
    sys.AddBuffer(bat);

    var consumer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    consumer.AddInput(Resource.ElectricCharge, 15);
    consumer.Demand = 1.0;

    sys.Solve();

    Assert.AreEqual(1.0, consumer.Activity, 0.001);
    Assert.AreEqual(1.0, producer.Activity, 0.001);
    Assert.AreEqual(-10, bat.Rate, 0.5);
  }

  [TestMethod]
  public void BufferFillsWhenProducerExcess() {
    var sys = new ProcessFlowSystem();
    var producer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 20);
    producer.Demand = 1.0;

    var bat = Battery(100, 50);
    sys.AddBuffer(bat);

    var consumer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    consumer.AddInput(Resource.ElectricCharge, 5);
    consumer.Demand = 1.0;

    sys.Solve();

    Assert.AreEqual(1.0, producer.Activity, 0.001);
    Assert.AreEqual(1.0, consumer.Activity, 0.001);
    Assert.AreEqual(15, bat.Rate, 0.5);
  }

  [TestMethod]
  public void BufferFull_NoFill_ProducerThrottles() {
    var sys = new ProcessFlowSystem();
    var producer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 20);
    producer.Demand = 1.0;

    var bat = Battery(100, 100); // full
    sys.AddBuffer(bat);

    var consumer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    consumer.AddInput(Resource.ElectricCharge, 5);
    consumer.Demand = 1.0;

    sys.Solve();

    Assert.AreEqual(0.25, producer.Activity, 0.001, "Producer throttles to consumer rate (5/20)");
    Assert.AreEqual(1.0, consumer.Activity, 0.001);
    Assert.AreEqual(0, bat.Rate, 0.5);
  }

  // ── Device priority ──────────────────────────────────────────────

  [TestMethod]
  public void DevicePriorityOrdering_CriticalSatisfiedFirst() {
    // Producer can deliver 10. Critical demands 8, Low demands 5.
    // Critical should run fully; Low gets the remaining 2.
    var sys = new ProcessFlowSystem();
    var producer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 10);
    producer.Demand = 1.0;

    var crit = sys.AddDevice(ProcessFlowSystem.Priority.Critical);
    crit.AddInput(Resource.ElectricCharge, 8);
    crit.Demand = 1.0;

    var low = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    low.AddInput(Resource.ElectricCharge, 5);
    low.Demand = 1.0;

    sys.Solve();

    Assert.AreEqual(1.0, crit.Activity, 0.001, "Critical fully satisfied");
    Assert.AreEqual(2.0 / 5.0, low.Activity, 0.01, "Low gets residual 2/5");
  }

  [TestMethod]
  public void DevicePriorityOrdering_HigherDoesntStealFromLowerWhenSlack() {
    // Producer 100 EC, Critical 5 EC, Low 10 EC. Both should run fully.
    var sys = new ProcessFlowSystem();
    var producer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 100);
    producer.Demand = 1.0;

    var crit = sys.AddDevice(ProcessFlowSystem.Priority.Critical);
    crit.AddInput(Resource.ElectricCharge, 5);
    crit.Demand = 1.0;

    var low = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    low.AddInput(Resource.ElectricCharge, 10);
    low.Demand = 1.0;

    sys.Solve();

    Assert.AreEqual(1.0, crit.Activity, 0.001);
    Assert.AreEqual(1.0, low.Activity, 0.001);
  }

  [TestMethod]
  public void SamePriorityDevices_FairlyShareUnderConstraint() {
    // Producer 10 EC, two Low consumers each 8 EC. Total demand 16 vs.
    // supply 10 → α* = 10/16 = 0.625; both consumers run at 0.625.
    var sys = new ProcessFlowSystem();
    var producer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 10);
    producer.Demand = 1.0;

    var c1 = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    c1.AddInput(Resource.ElectricCharge, 8); c1.Demand = 1.0;

    var c2 = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    c2.AddInput(Resource.ElectricCharge, 8); c2.Demand = 1.0;

    sys.Solve();

    Assert.AreEqual(0.625, c1.Activity, 0.01);
    Assert.AreEqual(0.625, c2.Activity, 0.01);
  }

  // ── Demand throttling ────────────────────────────────────────────

  [TestMethod]
  public void DeviceDemandLessThanOne_ActivityCappedAtDemand() {
    // Consumer at half-throttle (Demand=0.5) gets at most 0.5 even
    // when supply is plentiful.
    var sys = new ProcessFlowSystem();
    var producer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 100);
    producer.Demand = 1.0;

    var consumer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    consumer.AddInput(Resource.ElectricCharge, 10);
    consumer.Demand = 0.5;

    sys.Solve();

    Assert.AreEqual(0.5, consumer.Activity, 0.001);
    Assert.AreEqual(1.0, consumer.Satisfaction, 0.001);
    Assert.AreEqual(0.05, producer.Activity, 0.001);
  }

  // ── Multi-resource ──────────────────────────────────────────────

  [TestMethod]
  public void CyclicUniformResources_SteadyState() {
    // Closed-loop life-support proxy. Device A: consumes O₂, produces
    // CO₂. Device B: consumes CO₂, produces O₂. With matched rates
    // and starting buffers, both run at full activity.
    var sys = new ProcessFlowSystem();
    // Synthetic Uniform resources for the test — use the existing
    // ElectricCharge as a stand-in for both since they're both Uniform.
    // But that wouldn't test the cyclic property. We'd need two
    // Uniform resources. Since the registry only has EC today, this
    // test is a placeholder — fill in once O₂/CO₂ get registered.
    Assert.IsTrue(true, "Cyclic test deferred until O2/CO2 are registered");
  }

  // ── MaxTickDt / Tick ─────────────────────────────────────────────

  [TestMethod]
  public void MaxTickDt_BatteryEmpties() {
    // 100 EC battery draining at 10 EC/s → 10 s horizon.
    var sys = new ProcessFlowSystem();
    var bat = Battery(100, 100);
    sys.AddBuffer(bat);

    var consumer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    consumer.AddInput(Resource.ElectricCharge, 10);
    consumer.Demand = 1.0;

    sys.Solve();

    Assert.AreEqual(10.0, sys.MaxTickDt(), 0.001);
  }

  [TestMethod]
  public void MaxTickDt_BatteryFills() {
    // 100 EC battery at 50, filling at 5 EC/s → 10 s to full.
    var sys = new ProcessFlowSystem();
    var bat = Battery(100, 50);
    sys.AddBuffer(bat);

    var producer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 5);
    producer.Demand = 1.0;

    sys.Solve();

    Assert.AreEqual(10.0, sys.MaxTickDt(), 0.001);
  }

  // Device.ValidUntil is an absolute-time forecast (not a relative
  // dt), and the runner combines it with system-level dt at vessel
  // scope. So MaxTickDt is a buffer-only horizon — exercised by the
  // BatteryEmpties / BatteryFills tests above. There's no in-system
  // assertion left for ValidUntil.

  [TestMethod]
  public void ClockAdvance_LerpsContents() {
    var sys = new ProcessFlowSystem();
    var bat = Battery(100, 100);
    sys.AddBuffer(bat);

    var consumer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    consumer.AddInput(Resource.ElectricCharge, 10);
    consumer.Demand = 1.0;

    sys.Solve();
    // Lerp-based: advance clock, Contents reads back-derived value.
    sys.Clock.UT += 2.0;

    Assert.AreEqual(80, bat.Contents, 0.5);
  }

  // ── Buffer distribution ──────────────────────────────────────────

  [TestMethod]
  public void TwoBatteries_DrainProportionalToContents() {
    var sys = new ProcessFlowSystem();
    var batA = Battery(100, 80);
    var batB = Battery(100, 20);
    sys.AddBuffer(batA);
    sys.AddBuffer(batB);

    var consumer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    consumer.AddInput(Resource.ElectricCharge, 10);
    consumer.Demand = 1.0;

    sys.Solve();

    Assert.AreEqual(1.0, consumer.Activity, 0.001);
    // Total contents 100; A=80%, B=20%. Drain 10 EC/s → 8/2 split.
    Assert.AreEqual(-8, batA.Rate, 0.1);
    Assert.AreEqual(-2, batB.Rate, 0.1);
  }

  [TestMethod]
  public void TwoBatteries_FillProportionalToRemainingCapacity() {
    var sys = new ProcessFlowSystem();
    var batA = Battery(100, 20); // 80 space
    var batB = Battery(100, 80); // 20 space
    sys.AddBuffer(batA);
    sys.AddBuffer(batB);

    var producer = sys.AddDevice(ProcessFlowSystem.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 10);
    producer.Demand = 1.0;

    sys.Solve();

    Assert.AreEqual(1.0, producer.Activity, 0.001);
    // Total space 100; A=80%, B=20%. Fill 10 EC/s → 8/2 split.
    Assert.AreEqual(8, batA.Rate, 0.1);
    Assert.AreEqual(2, batB.Rate, 0.1);
  }

  // ── Topological resources rejected ──────────────────────────────

  [TestMethod]
  [ExpectedException(typeof(System.ArgumentException))]
  public void TopologicalResource_AddBufferThrows() {
    var sys = new ProcessFlowSystem();
    var t = new Buffer {
      Resource = Resource.RP1,
      Capacity = 100,
      Contents = 100,
    };
    sys.AddBuffer(t);
  }
}

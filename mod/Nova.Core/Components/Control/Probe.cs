using Nova.Core.Persistence;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components.Control;

// Probe cores — unmanned vessel control sources. Distinct from Command
// (the crewed-pod equivalent) because probes layer their own concerns
// on top of the basic flight-computer baseline:
//   - SAS service tier (stock SASServiceLevel 0..3)
//   - StoredCommands: a per-probe byte ledger that decays continuously,
//     is refilled from KSC over the comms graph, and is spent by
//     spacecraft control inputs (throttle, attitude) — when it runs
//     dry the vessel loses command authority.
//   - hibernation (future): warp-time low-power mode that stock ties
//     to ModuleCommand.hasHibernation; not modelled yet
//   - link gating (future): no signal → ProbePartial / ProbeNone
//
// EC behavior mirrors Command (idle draw + test-load helper).
public class Probe : VirtualComponent {
  public double IdleDraw;       // W — always-on flight-computer baseline
  public double TestLoadRate;   // W — debug load capacity, gated by TestLoadActive
  public int SasLevel;          // 0..3, stock SASServiceLevel

  public bool TestLoadActive;   // runtime; reset to false on each build

  internal Device idleDevice;
  internal Device testLoadDevice;

  public double IdleActivity => idleDevice?.Activity ?? 0;
  public double TestLoadActivity => testLoadDevice?.Activity ?? 0;

  // ── StoredCommands ledger ─────────────────────────────────────────
  // Bytes. Not a Resource (no registry entry, no Domain, not in any
  // solver) — purely component-local lerp state, same shape as
  // Resources/Buffer but private to the Probe.
  //
  //   NetRate    = CommandRefillBps − CommandDecayBps
  //   Bytes(ut)  = clamp(CommandBaselineBytes + NetRate × (ut − CommandBaselineUT),
  //                      0, CommandCapacityBytes)
  //
  // CommandRefillBps is rewritten by the comms wiring after every
  // CommunicationsNetwork.Solve from the matching Receive job's
  // AllocatedRateBps. Spending (control input) goes through
  // TrySpendCommands, which rebaselines and deducts.
  public double CommandCapacityBytes;   // config — max ledger fill
  public double CommandDecayBps;        // config — constant decay, bytes/s
  public double CommandReceiveRateBps;  // config — comms receive ceiling, bytes/s
  public double InputCostBps;           // config — bytes/s per unit input magnitude

  public double CommandBaselineBytes;   // lerp baseline (persisted)
  public double CommandBaselineUT;      // lerp anchor UT (persisted)
  public double CommandRefillBps;       // most recent comms allocation (runtime)

  private double CommandClockUT => Vessel?.Systems?.Clock?.UT ?? CommandBaselineUT;
  private double CommandNetRateBps => CommandRefillBps - CommandDecayBps;

  // Lerped, clamped bytes at the given UT.
  public double CommandsAt(double ut) {
    var projected = CommandBaselineBytes + CommandNetRateBps * (ut - CommandBaselineUT);
    if (projected < 0) return 0;
    if (projected > CommandCapacityBytes) return CommandCapacityBytes;
    return projected;
  }

  // Current bytes against the vessel's sim clock (BaselineUT in editor /
  // tests without a clock).
  public double CommandBytes => CommandsAt(CommandClockUT);

  public double CommandFillFraction =>
      CommandCapacityBytes > 1e-9 ? CommandBytes / CommandCapacityBytes : 0.0;

  // Capture lerped contents at `ut` as the new baseline.
  public void RebaselineCommands(double ut) {
    CommandBaselineBytes = CommandsAt(ut);
    CommandBaselineUT = ut;
  }

  // Comms wiring calls this after each network Solve with the Receive
  // job's allocated rate. Rebaseline first so the old rate stops
  // applying retroactively, then swap in the new inflow.
  public void SetCommandRefillRate(double bps) {
    var ut = CommandClockUT;
    RebaselineCommands(ut);
    CommandRefillBps = bps;
  }

  // Spend `bytes` of stored commands. Returns false (and leaves the
  // ledger untouched) when the live contents can't cover it.
  public bool TrySpendCommands(double bytes) {
    if (bytes <= 0) return true;
    var ut = CommandClockUT;
    var available = CommandsAt(ut);
    if (available < bytes) return false;
    CommandBaselineBytes = available - bytes;
    CommandBaselineUT = ut;
    return true;
  }

  public override VirtualComponent Clone() {
    return new Probe {
      IdleDraw = IdleDraw,
      TestLoadRate = TestLoadRate,
      SasLevel = SasLevel,
      TestLoadActive = TestLoadActive,
      CommandCapacityBytes = CommandCapacityBytes,
      CommandDecayBps = CommandDecayBps,
      CommandReceiveRateBps = CommandReceiveRateBps,
      InputCostBps = InputCostBps,
      CommandBaselineBytes = CommandBaselineBytes,
      CommandBaselineUT = CommandBaselineUT,
      CommandRefillBps = CommandRefillBps,
    };
  }

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    if (IdleDraw > 0) {
      // Avionics baseline at Priority.High — a probe with a starved
      // flight computer loses control authority just like a crewed
      // pod, so the LP satisfies it ahead of opportunistic loads.
      idleDevice = systems.AddDevice(node,
          inputs: new[] { (Resource.ElectricCharge, IdleDraw) },
          priority: ProcessFlowSystem.Priority.High);
      idleDevice.Demand = 1.0;
    }
    if (TestLoadRate > 0) {
      testLoadDevice = systems.AddDevice(node,
          inputs: new[] { (Resource.ElectricCharge, TestLoadRate) });
      testLoadDevice.Demand = TestLoadActive ? 1.0 : 0.0;
    }

    // Anchor the command-ledger lerp. ComponentFactory primes
    // CommandBaselineBytes to capacity; Load (if a save was restored)
    // overrides both baseline fields with a positive UT. So a
    // BaselineUT still at 0 means "fresh probe" — start the clock now.
    if (CommandBaselineUT <= 0)
      CommandBaselineUT = systems.Clock.UT;
  }

  public override void OnPreSolve() {
    if (testLoadDevice != null)
      testLoadDevice.Demand = TestLoadActive ? 1.0 : 0.0;
  }

  public override void Save(PartState state) {
    // Don't persist CommandRefillBps — the next comms Solve after load
    // recomputes it, and crediting a stale inflow across a reload gap
    // would over-fill. Reload sees decay only until the link resolves.
    state.Probe = new ProbeState {
      CommandBaselineBytes = CommandsAt(CommandClockUT),
      CommandBaselineUt = CommandClockUT,
    };
  }

  public override void Load(PartState state) {
    if (state.Probe == null) return;
    CommandBaselineBytes = state.Probe.CommandBaselineBytes;
    CommandBaselineUT = state.Probe.CommandBaselineUt;
    CommandRefillBps = 0;
  }
}

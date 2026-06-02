using System;
using System.Collections.Generic;
using System.Threading;
using Nova.Core.Components;
using Nova.Sim.Universe;

namespace Nova.Sim.Runtime;

// Drives a single VirtualVessel forward in time on a background
// thread, decoupled from the WebSocket telemetry server's emit
// cadence. Telemetry readers acquire `Lock` to snapshot vessel
// state while the tick is paused.
//
// Time model:
//   - `simUt` is the simulation's universal time; advanced by
//     wall-clock-dt × WarpFactor each tick.
//   - `WarpFactor` defaults to 1.0; can be set live via UDP eval.
//   - Tick rate target is 60 Hz; if the previous tick took longer
//     than the budget, the next runs immediately to keep up.
public sealed class SimRunner {
  public VirtualVessel Vessel { get; private set; }
  public SimVesselContext Context { get; }
  public string VesselName { get; set; }
  public string VesselGuid { get; set; }
  public double SimUt { get; private set; }
  public double MissionTime { get; private set; }
  public double LaunchTime { get; private set; }
  public double WarpFactor { get; set; } = 1.0;
  // Editor mode: load a craft and freeze its state. Ops mutate
  // components directly; no Vessel.Tick runs, so no LP solve, no
  // boiloff, no battery drain — what you see is the prefab loadout
  // until the player changes it. Mirrors the in-game VAB/SPH, where
  // PartModule.Components is populated from prefab config and no
  // NovaVesselModule.Virtual exists.
  public bool Editor { get; set; }
  // Snapshot of the crew aboard the active vessel as loaded from the
  // .nvs file. The sim doesn't simulate EVA / transfers, so this
  // doesn't change at runtime — the formatter reads it directly and
  // emits a static frame.
  public IReadOnlyList<Nova.Core.Persistence.Protos.Kerbal> Crew { get; set; }
      = Array.Empty<Nova.Core.Persistence.Protos.Kerbal>();
  public readonly object Lock = new object();

  // Diagnostic — append a synthetic kerbal to the runtime crew roster.
  // Useful when iterating on the CREW UI against a save whose active
  // vessel is uncrewed (probe missions, etc.). Invoke via kspcli:
  //   kspcli '$0.InjectTestCrew("<partId>", "Jebediah Kerman", "Pilot")'
  // The next NovaCrewRoster/<guid> emit will include the new entry.
  public void InjectTestCrew(string partId, string name, string trait) {
    var list = new List<Nova.Core.Persistence.Protos.Kerbal>(Crew);
    var k = new Nova.Core.Persistence.Protos.Kerbal();
    k.Name = name;
    k.Trait = trait;
    k.Gender = 0;
    k.Veteran = false;
    k.AssignedPartId = uint.TryParse(partId, out var pid) ? pid : 0u;
    list.Add(k);
    Crew = list;
  }

  private const double TickIntervalSec = 1.0 / 60.0;
  private Thread _thread;
  private volatile bool _running;
  private DateTime _lastWallClock;

  public SimRunner(VirtualVessel vessel, SimVesselContext context,
                   string vesselName, string vesselGuid,
                   double simUt, double missionTime, double launchTime) {
    Vessel = vessel;
    Context = context;
    Vessel.Context = context;
    VesselName = vesselName;
    VesselGuid = vesselGuid;
    SimUt = simUt;
    MissionTime = missionTime;
    LaunchTime = launchTime;
  }

  public void Start() {
    if (_running) return;
    _running = true;
    _lastWallClock = DateTime.UtcNow;
    _thread = new Thread(Loop) { IsBackground = true, Name = "Nova.Sim.Runner" };
    _thread.Start();
  }

  public void Stop() {
    _running = false;
    _thread?.Join(2000);
  }

  private void Loop() {
    while (_running) {
      var now = DateTime.UtcNow;
      double wallDt = (now - _lastWallClock).TotalSeconds;
      _lastWallClock = now;
      // Clamp to avoid catastrophic catch-up jumps after a long pause
      // (debugger, sleep, etc.). 0.5s real time → at most 0.5s sim time
      // per tick before warp.
      if (wallDt > 0.5) wallDt = 0.5;

      if (!Editor) {
        double simDt = wallDt * WarpFactor;
        lock (Lock) {
          SimUt += simDt;
          MissionTime += simDt;
          double target = SimUt;
          try {
            Vessel.Tick(target);
          } catch (Exception ex) {
            // Sim-side: log + continue. Bad ticks shouldn't kill the
            // background loop because the UI is still rendering on the
            // last good snapshot.
            Console.Error.WriteLine("[sim] tick error at UT=" + target + ": " + ex);
          }
        }
      }

      // Sleep the remainder of the tick budget. Negative → ran late,
      // loop immediately.
      double elapsed = (DateTime.UtcNow - now).TotalSeconds;
      double sleep = TickIntervalSec - elapsed;
      if (sleep > 0) Thread.Sleep((int)(sleep * 1000));
    }
  }
}

using System;
using System.IO;
using System.Threading;
using Nova.Sim.Config;
using Nova.Sim.Eval;
using Nova.Sim.Persistence;
using Nova.Sim.Runtime;
using Nova.Sim.Telemetry;
using Nova.Sim.Universe;

namespace Nova.Sim;

// Entry point for the Nova.Sim headless simulator.
//
// Usage:
//   nova-sim --ksp-path <KSP install>
//            (--craft <path.nvc> | --save <path.nvs>)
//            [--ws-port 9877] [--udp-port 9876] [--warp 1.0]
//
// The sim:
//   1. Walks $kspPath/GameData to build a stock-part database, then
//      overlays configs/overrides/ patches (Phase C).
//   2. Loads the .nvc (single craft) or .nvs (full save) into a
//      VirtualVessel via Nova.Core proto deserialization + the part DB
//      (Phase E).
//   3. Ticks VirtualVessel.Tick at ~60 Hz; sim UT advances by
//      dt × warp (Phase F).
//   4. Serves Dragonglass-compatible telemetry over WebSocket on
//      --ws-port (Phase G). Point ui/apps/nova/'s dev server at
//      ws://localhost:<port> to render the HUD.
//   5. Accepts kspcli-style C# expressions on --udp-port (Phase H)
//      for live introspection / control.
public static class Program {
  public static int Main(string[] args) {
    Options options;
    try {
      options = Options.Parse(args);
    } catch (ArgumentException ex) {
      Console.Error.WriteLine("nova-sim: " + ex.Message);
      Console.Error.WriteLine();
      Console.Error.WriteLine(Options.UsageText);
      return 2;
    }

    if (options.ShowHelp) {
      Console.WriteLine(Options.UsageText);
      return 0;
    }

    Console.WriteLine("nova-sim starting");
    Console.WriteLine("  ksp-path = " + options.KspPath);
    Console.WriteLine("  craft    = " + (options.CraftPath ?? "(none)"));
    Console.WriteLine("  save     = " + (options.SavePath ?? "(none)"));
    Console.WriteLine("  ws-port  = " + options.WsPort);
    Console.WriteLine("  udp-port = " + options.UdpPort);
    Console.WriteLine("  warp     = " + options.Warp);
    Console.WriteLine();

    try {
      Run(options);
      return 0;
    } catch (Exception ex) {
      Console.Error.WriteLine("nova-sim: " + ex.Message);
      Console.Error.WriteLine(ex.StackTrace);
      return 1;
    }
  }

  // Build the part database, hydrate the vessel from the .nvc/.nvs,
  // spin the sim runner + WS + UDP servers, then block on Ctrl-C.
  private static void Run(Options options) {
    var patchesRoot = ResolvePatchesRoot();
    Console.WriteLine("[part-db] loading from " + options.KspPath + " + overrides at " + patchesRoot);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var partDb = PartDatabase.Build(options.KspPath, patchesRoot);
    sw.Stop();
    Console.WriteLine("[part-db] " + partDb.Count + " parts (stock " + partDb.StockCount
        + ", patched " + partDb.PatchedCount + ", deleted " + partDb.DeletedCount
        + ") in " + sw.ElapsedMilliseconds + "ms");

    SimVesselLoader.LoadResult load;
    if (!string.IsNullOrEmpty(options.CraftPath)) {
      Console.WriteLine("[load] craft: " + options.CraftPath);
      load = SimVesselLoader.LoadCraft(options.CraftPath, partDb, simTime: 0);
    } else {
      Console.WriteLine("[load] save: " + options.SavePath);
      load = SimVesselLoader.LoadSave(options.SavePath, partDb);
    }
    Console.WriteLine("[load] vessel '" + load.VesselName + "' (" + load.VesselGuid + ") at UT=" + load.UniversalTime);

    // Editor mode is an explicit flag — `--edit` puts the runner in
    // a static VAB/SPH design pose (no LP solve, no boiloff, no UT
    // advance) regardless of which file type loaded the vessel. The
    // common case (LV-N reactor on a .nvc — testing flight-only state
    // machines) wants --craft *without* editor mode, so we don't infer
    // from extension. The flag gates both the runner tick body and
    // the telemetry server's scene emission so the UI mounts
    // EditorHud vs FlightHud accordingly.
    bool isEditor = options.Editor;

    var context = new SimVesselContext();
    var runner = new SimRunner(load.Vessel, context,
        load.VesselName, load.VesselGuid,
        load.UniversalTime, load.MissionTime, load.LaunchTime) {
      WarpFactor = options.Warp,
      Editor = isEditor,
    };
    runner.Start();

    var ws = new SimTelemetryServer(options.WsPort, runner, partDb, isEditor);
    ws.Start();

    var udp = new UdpEvalServer(options.UdpPort);
    udp.Start();
    // Expose the runner + part DB to eval scope as $0 / $1.
    int runnerRef = udp.RegisterRef(runner);
    int dbRef = udp.RegisterRef(partDb);
    Console.WriteLine("[udp] eval refs: $" + runnerRef + " = SimRunner, $" + dbRef + " = PartDatabase");

    Console.WriteLine();
    Console.WriteLine("nova-sim ready. Ctrl-C to stop.");

    // Block until process is killed. Console.CancelKeyPress doesn't
    // fire reliably under Mono on macOS — just wait forever; OS sigterm
    // will reap the process.
    Thread.Sleep(Timeout.Infinite);

    runner.Stop();
    ws.Dispose();
    udp.Dispose();
  }

  // Find configs/overrides/ relative to the running binary. The bin
  // lives at mod/Nova.Sim/build/Nova.Sim.exe; overrides at
  // <repo>/configs/overrides. Walk up until we find the configs dir.
  private static string ResolvePatchesRoot() {
    var binDir = AppDomain.CurrentDomain.BaseDirectory;
    var d = new DirectoryInfo(binDir);
    while (d != null) {
      var candidate = Path.Combine(d.FullName, "configs", "overrides");
      if (Directory.Exists(candidate)) return candidate;
      d = d.Parent;
    }
    throw new DirectoryNotFoundException(
        "could not locate configs/overrides/ relative to " + binDir);
  }
}

internal sealed class Options {
  public string KspPath;
  public string CraftPath;
  public string SavePath;
  public int WsPort = 9877;
  public int UdpPort = 9876;
  public double Warp = 1.0;
  public bool Editor = false;
  public bool ShowHelp;

  public const string UsageText =
@"nova-sim — headless Nova simulator.

Required:
  --ksp-path <dir>     KSP install directory. Reads GameData/Squad/Parts/.

Vessel source (exactly one of):
  --craft <path.nvc>   Load a single craft file.
  --save  <path.nvs>   Load a full save file.

Optional:
  --edit               Editor mode — static vessel pose, no LP solve,
                       no UT advance. Mounts EditorHud on the UI side.
                       Default: flight mode.
  --ws-port <n>        WebSocket telemetry port. Default: 9877.
  --udp-port <n>       UDP eval port. Default: 9876.
  --warp <factor>      Time-warp multiplier. Default: 1.0.
  -h, --help           Show this message.";

  public static Options Parse(string[] args) {
    var o = new Options();
    for (int i = 0; i < args.Length; i++) {
      string a = args[i];
      switch (a) {
        case "-h":
        case "--help":
          o.ShowHelp = true;
          return o;
        case "--ksp-path": o.KspPath  = RequireValue(args, ref i, a); break;
        case "--craft":    o.CraftPath = RequireValue(args, ref i, a); break;
        case "--save":     o.SavePath  = RequireValue(args, ref i, a); break;
        case "--edit":     o.Editor   = true; break;
        case "--ws-port":  o.WsPort    = int.Parse(RequireValue(args, ref i, a)); break;
        case "--udp-port": o.UdpPort   = int.Parse(RequireValue(args, ref i, a)); break;
        case "--warp":     o.Warp      = double.Parse(RequireValue(args, ref i, a),
                                          System.Globalization.CultureInfo.InvariantCulture); break;
        default:
          throw new ArgumentException("unknown argument '" + a + "'");
      }
    }

    if (o.ShowHelp) return o;

    if (string.IsNullOrEmpty(o.KspPath))
      throw new ArgumentException("--ksp-path is required");
    if (!Directory.Exists(o.KspPath))
      throw new ArgumentException("ksp-path directory not found: " + o.KspPath);

    if (string.IsNullOrEmpty(o.CraftPath) && string.IsNullOrEmpty(o.SavePath))
      throw new ArgumentException("must specify --craft or --save");
    if (!string.IsNullOrEmpty(o.CraftPath) && !string.IsNullOrEmpty(o.SavePath))
      throw new ArgumentException("--craft and --save are mutually exclusive");
    if (!string.IsNullOrEmpty(o.CraftPath) && !File.Exists(o.CraftPath))
      throw new ArgumentException("craft file not found: " + o.CraftPath);
    if (!string.IsNullOrEmpty(o.SavePath) && !File.Exists(o.SavePath))
      throw new ArgumentException("save file not found: " + o.SavePath);

    if (o.WsPort <= 0 || o.WsPort > 65535)
      throw new ArgumentException("--ws-port out of range: " + o.WsPort);
    if (o.UdpPort <= 0 || o.UdpPort > 65535)
      throw new ArgumentException("--udp-port out of range: " + o.UdpPort);
    if (o.Warp <= 0)
      throw new ArgumentException("--warp must be positive: " + o.Warp);

    return o;
  }

  private static string RequireValue(string[] args, ref int i, string flag) {
    if (i + 1 >= args.Length)
      throw new ArgumentException(flag + " requires a value");
    return args[++i];
  }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Fleck;
using Nova.Core.Telemetry;
using Nova.Core.Utils;
using Nova.Sim.Config;
using Nova.Sim.Runtime;
using Nova.Sim.Universe;

namespace Nova.Sim.Telemetry;

// WebSocket server speaking Dragonglass's telemetry wire protocol:
//
//   Client → Server:
//     {"op":"subscribe","topic":"<name>"}
//     {"op":"unsubscribe","topic":"<name>"}
//     {"topic":"<name>","op":"<custom>","args":[...]}   (forwarded to op handlers)
//
//   Server → Client:
//     {"topic":"<name>","data":<positional-array>}
//
// Per-topic publish is on demand: a 10 Hz dispatcher iterates every
// active subscription set across every connected client and emits a
// fresh frame using the matching Core formatter. Subscription state
// is tracked per connection; disconnect clears the connection's set.
//
// Supported topics today (sim emits the wire format end-to-end):
//   NovaScene
//   NovaTimewarp
//   NovaOrbit/<guid>
//   NovaComms/<guid>            (always emits "no-link" for now)
//   NovaVesselStructure/<guid>
//   NovaVesselState/<guid>      (no atmosphere/integrator: situation frozen at 5/Orbiting)
//
// Topics the UI may request but the sim doesn't yet emit are silently
// no-op'd: subscribe succeeds, no frames flow. Add formatters and a
// dispatch case here as new topics come online.
public sealed class SimTelemetryServer : IDisposable {
  private readonly int _port;
  private readonly SimRunner _runner;
  private readonly PartDatabase _partDb;
  private readonly bool _isEditor;
  private WebSocketServer _server;
  private Timer _publishTimer;

  private readonly ConcurrentDictionary<Guid, Connection> _connections =
      new ConcurrentDictionary<Guid, Connection>();

  private sealed class Connection {
    public IWebSocketConnection Socket;
    public readonly HashSet<string> Subscriptions = new HashSet<string>();
  }

  public SimTelemetryServer(int port, SimRunner runner, PartDatabase partDb, bool isEditor) {
    _port = port;
    _runner = runner;
    _partDb = partDb;
    _isEditor = isEditor;
  }

  public void Start() {
    FleckLog.Level = LogLevel.Warn;
    _server = new WebSocketServer("ws://0.0.0.0:" + _port);
    _server.Start(socket => {
      socket.OnOpen = () => {
        var c = new Connection { Socket = socket };
        _connections[socket.ConnectionInfo.Id] = c;
        Console.WriteLine("[ws] connect " + socket.ConnectionInfo.ClientIpAddress);
      };
      socket.OnClose = () => {
        _connections.TryRemove(socket.ConnectionInfo.Id, out _);
        Console.WriteLine("[ws] disconnect");
      };
      socket.OnMessage = msg => HandleClientMessage(socket, msg);
    });

    // 10Hz publish cadence — same as Dragonglass's persistent host.
    _publishTimer = new Timer(_ => PublishOnce(), null, 100, 100);
    _last = this;
    Console.WriteLine("[ws] listening on ws://0.0.0.0:" + _port);
  }

  public void Dispose() {
    _publishTimer?.Dispose();
    _server?.Dispose();
  }

  private void HandleClientMessage(IWebSocketConnection socket, string msg) {
    if (!_connections.TryGetValue(socket.ConnectionInfo.Id, out var conn)) return;

    if (!TryParseEnvelope(msg, out var env)) return;

    if (env.Op == "subscribe") {
      lock (conn.Subscriptions) conn.Subscriptions.Add(env.Topic);
      // Snapshot-on-subscribe: deliver one frame immediately so late
      // subscribers don't stare at defaults.
      PublishOneTo(conn, env.Topic);
      return;
    }
    if (env.Op == "unsubscribe") {
      lock (conn.Subscriptions) conn.Subscriptions.Remove(env.Topic);
      return;
    }
    // Custom op — route to the sim-side op dispatcher. Mirrors the
    // mod's NovaPartTopic.HandleOp surface but mutates VirtualComponents
    // directly (no PartModule layer). See SimOpDispatcher.
    SimOpDispatcher.Handle(env.Topic, env.Op, env.Args, _runner);
  }

  private struct Envelope {
    public string Topic;
    public string Op;
    public List<object> Args;
  }

  // Full JSON envelope decode. Needed because custom ops carry an
  // `args` array of arbitrary structure (booleans, numbers, nested
  // arrays — see SimOpDispatcher); the previous string-scan parser
  // only worked for `topic` and `op`. Mirrors Dragonglass's
  // TryParseEnvelope so the sim consumes the exact tree shape
  // HandleOp expects.
  private static bool TryParseEnvelope(string text, out Envelope env) {
    env = default;
    if (!(JsonReader.Parse(text) is Dictionary<string, object> root)) return false;
    if (!(root.TryGetValue("topic", out object t) && t is string topic)) return false;
    if (!(root.TryGetValue("op", out object o) && o is string op)) return false;
    List<object> args = null;
    if (root.TryGetValue("args", out object a)) {
      args = a as List<object>;
      if (args == null) return false;
    }
    env.Topic = topic;
    env.Op = op;
    env.Args = args ?? new List<object>();
    return true;
  }

  private void PublishOnce() {
    foreach (var kv in _connections) {
      var conn = kv.Value;
      List<string> topics;
      lock (conn.Subscriptions) topics = new List<string>(conn.Subscriptions);
      foreach (var topic in topics) PublishOneTo(conn, topic);
    }
  }

  private void PublishOneTo(Connection conn, string topic) {
    var sb = new StringBuilder();
    if (!TryBuildFrame(topic, sb)) return;
    try { conn.Socket.Send(sb.ToString()); }
    catch { /* socket may be closing; OnClose will clean up */ }
  }

  private bool TryBuildFrame(string topic, StringBuilder sb) {
    // Envelope: {"topic":"<name>","data":<...>}
    sb.Append("{\"topic\":");
    JsonWriter.WriteString(sb, topic);
    sb.Append(",\"data\":");

    bool ok = WriteTopicData(topic, sb);
    if (!ok) {
      sb.Clear();
      return false;
    }

    sb.Append('}');
    return true;
  }

  private bool WriteTopicData(string topic, StringBuilder sb) {
    if (topic == "NovaScene") {
      SceneFormatter.Write(sb, virtualScene: "");
      return true;
    }
    if (topic == "NovaTimewarp") {
      var rate = _runner.WarpFactor;
      TimewarpFormatter.Write(sb, rate, TimewarpFormatter.ModeRails);
      return true;
    }
    if (topic == "flight") {
      // Editor scene has no FlightGlobals — the in-game mod doesn't
      // emit `flight` here either. UI's useFlight() readers don't
      // subscribe in EditorHud, but suppress emission anyway so a
      // stray subscribe gets a clean silent ignore (frame absent →
      // useFlight().vesselId stays empty).
      if (_isEditor) return false;
      // DG's `flight` topic: vessel state for navball + readouts.
      // Sim emits the bare minimum — `vesselId` is what every
      // useFlight()-reading component keys on for "is there an
      // active vessel?". Altitudes / velocities / orientation are
      // zeroed (no flight integrator); the HUD's navball will park
      // at identity orientation, which is fine for parity testing.
      sb.Append('[');
      JsonWriter.WriteString(sb, _runner.VesselGuid);
      sb.Append(",").Append(_runner.Context.Altitude.ToString("G17", System.Globalization.CultureInfo.InvariantCulture));
      sb.Append(",").Append(_runner.Context.Altitude.ToString("G17", System.Globalization.CultureInfo.InvariantCulture));
      sb.Append(",[0,0,0],[0,0,0],"); // surface / orbital velocity
      sb.Append("0,false,false,");    // throttle, sas, rcs
      sb.Append("[0,0,0,1],");        // orientation quat (identity)
      sb.Append("[0,0,0],");          // angular velocity
      sb.Append("null,");             // target velocity
      sb.Append("0,");                // current thrust
      sb.Append("0");                 // speed mode
      sb.Append(']');
      return true;
    }
    if (topic == "game") {
      // DG's `game` singleton: [scene, activeVesselGuid|null, timewarpRate, mapActive].
      // The Hud router keys on scene to mount FlightHud / EditorHud.
      // In editor mode, in-game KSP reports activeVesselId=null because
      // FlightGlobals.fetch isn't live in VAB/SPH; we mirror that so
      // the editor UI doesn't see a phantom flight vessel.
      sb.Append('[');
      if (_isEditor) {
        JsonWriter.WriteString(sb, "EDITOR");
        sb.Append(",null,1,false");
      } else {
        JsonWriter.WriteString(sb, "FLIGHT");
        sb.Append(',');
        JsonWriter.WriteString(sb, _runner.VesselGuid);
        sb.Append(',');
        JsonWriter.WriteDouble(sb, _runner.WarpFactor);
        sb.Append(",false");
      }
      sb.Append(']');
      return true;
    }
    if (topic == "NovaScienceArchive") {
      // Sim has no archive yet — pass null; formatter emits empty-grid
      // rows that the UI renders as "no archived science yet".
      var bodies = new List<(string name, string parent)>();
      foreach (var b in BodyData.All) bodies.Add((b.Name, b.Parent ?? ""));
      ScienceArchiveFormatter.Write(sb, archive: null, bodies);
      return true;
    }

    if (TryPrefix(topic, "NovaOrbit/", out _)) {
      WriteOrbitFrame(sb);
      return true;
    }
    if (TryPrefix(topic, "NovaComms/", out _)) {
      WriteCommsFrame(sb);
      return true;
    }
    if (TryPrefix(topic, "NovaVesselStructure/", out _)) {
      WriteVesselStructureFrame(sb, shipIdOverride: null);
      return true;
    }
    if (TryPrefix(topic, "NovaVesselState/", out _)) {
      WriteVesselStateFrame(sb);
      return true;
    }
    if (TryPrefix(topic, "NovaCrewRoster/", out _)) {
      WriteCrewRosterFrame(sb);
      return true;
    }
    if (topic == "NovaEditorShipStructure/editor") {
      // Editor-side singleton. The in-game mod publishes this with a
      // fixed shipId="editor" (NovaEditorShipStructureTopic.cs:34);
      // we mirror that so the editor UI's hook keys match. Same
      // formatter, different first field.
      WriteVesselStructureFrame(sb, shipIdOverride: "editor");
      return true;
    }
    if (TryPrefix(topic, "NovaPart/", out var partIdStr)) {
      if (uint.TryParse(partIdStr, out var partId)) {
        WritePartFrame(sb, partId);
        return true;
      }
      return false;
    }
    if (TryPrefix(topic, "NovaStorage/", out var storagePartIdStr)) {
      if (uint.TryParse(storagePartIdStr, out var partId)) {
        WriteStorageFrame(sb, partId);
        return true;
      }
      return false;
    }
    if (TryPrefix(topic, "NovaScience/", out var sciencePartIdStr)) {
      if (uint.TryParse(sciencePartIdStr, out var partId)) {
        WriteScienceFrame(sb, partId);
        return true;
      }
      return false;
    }
    if (topic == "engines") {
      WriteEnginesFrame(sb);
      return true;
    }
    if (topic == "stage") {
      WriteStageFrame(sb);
      return true;
    }
    return false;
  }

  private static bool TryPrefix(string topic, string prefix, out string suffix) {
    suffix = null;
    if (!topic.StartsWith(prefix, StringComparison.Ordinal)) return false;
    suffix = topic.Substring(prefix.Length);
    return true;
  }

  private void WriteOrbitFrame(StringBuilder sb) {
    lock (_runner.Lock) {
      var ctx = _runner.Context;
      // Body-relative orbit elements: circular orbit at Altitude
      // around the body. ApA/PeA equal Altitude in a perfect circle.
      OrbitFormatter.Write(sb,
          vesselGuid: _runner.VesselGuid,
          bodyName: ctx.BodyName,
          apA: ctx.Altitude,
          peA: ctx.Altitude,
          eccentricity: 0,
          inclination: 0,
          period: ctx.OrbitPeriod,
          missionTime: _runner.MissionTime,
          launchTime: _runner.LaunchTime);
    }
  }

  private void WriteCommsFrame(StringBuilder sb) {
    // No comms in v1 of the sim — emit a clean "no link" frame so
    // the UI's LINK chip renders DARK rather than missing.
    CommsFormatter.Write(sb, _runner.VesselGuid,
        hasPath: false, bottleneckBps: 0,
        linkSnr: 0, linkRateBps: 0, linkMaxRateBps: 0,
        linkSnrFloor: 0, peerLabel: "",
        txActive: false, txRateBps: 0, txDeliveredBytes: 0, txTotalBytes: 0);
  }

  // Per-vessel crew roster, derived from the .nvs SaveFile.crew slice
  // captured at load time and filtered to the active vessel's persistent
  // id. The sim doesn't simulate EVA / crew transfer, so the snapshot
  // is static — no comparison logic needed, just translate the proto
  // Kerbal list into the wire format on each subscribe / republish.
  private void WriteCrewRosterFrame(StringBuilder sb) {
    var crew = _runner.Crew;
    var entries = new List<CrewRosterFormatter.KerbalEntry>(crew?.Count ?? 0);
    if (crew != null) {
      for (int i = 0; i < crew.Count; i++) {
        var k = crew[i];
        if (k == null || k.AssignedPartId == 0) continue;
        entries.Add(new CrewRosterFormatter.KerbalEntry {
          PartId    = k.AssignedPartId.ToString(),
          Name      = k.Name ?? "",
          TraitChar = CrewRosterFormatter.TraitChar(k.Trait),
          Gender    = k.Gender,
          Veteran   = k.Veteran,
        });
      }
    }
    CrewRosterFormatter.Write(sb, _runner.VesselGuid, entries);
  }

  // Synthetic NovaVesselState frame for the loaded VirtualVessel.
  // Wet mass via Σ Staging.ActiveNodes().Mass() — Nova.Core stores
  // part dry masses in kg (NovaSaveLoader and SimVesselLoader both
  // multiply prefab.mass × 1000 before AddPart), so Node.Mass() is
  // already SI; no further unit conversion. Part count via AllPartIds.
  // Situation pinned to Orbiting (5) because the sim has no flight
  // integrator to drive transitions — the UI's status pip stays
  // steady-green in sim mode. Crew is 0 (sim has no Kerbal model).
  private void WriteVesselStateFrame(StringBuilder sb) {
    string id, name, bodyName;
    double massKg;
    int partCount;
    const int situation = 5; // Vessel.Situations.Orbiting
    const int crewCount = 0;
    const int crewCapacity = 0;
    lock (_runner.Lock) {
      id = _runner.VesselGuid;
      name = _runner.VesselName;
      bodyName = _runner.Context?.BodyName ?? "";
      var virt = _runner.Vessel;
      double sumKg = 0;
      int n = 0;
      if (virt != null) {
        foreach (var _pid in virt.AllPartIds()) n++;
        if (virt.Systems?.Staging != null) {
          foreach (var node in virt.Systems.Staging.ActiveNodes()) sumKg += node.Mass();
        }
      }
      massKg = sumKg;
      partCount = n;
    }
    VesselStateFormatter.Write(sb, id, name, situation, bodyName,
        massKg, partCount, crewCount, crewCapacity);
  }

  // kspcli debug helper: builds a NovaVesselState frame for the
  // currently loaded vessel and returns it as a JSON string. Invoke
  // via:
  //   kspcli --udp 127.0.0.1:9877 eval 'Nova.Sim.Telemetry.SimTelemetryServer.DumpVesselState()'
  // Static so kspcli's evaluator can resolve it without a server
  // instance — uses the most-recently-started SimTelemetryServer.
  private static SimTelemetryServer _last;
  public static string DumpVesselState() {
    var srv = _last;
    if (srv == null) return "(no SimTelemetryServer instance)";
    var sb = new StringBuilder();
    srv.WriteVesselStateFrame(sb);
    return sb.ToString();
  }

  private void WriteVesselStructureFrame(StringBuilder sb, string shipIdOverride) {
    List<VesselStructureFormatter.PartEntry> parts;
    string name, id;
    lock (_runner.Lock) {
      name = _runner.VesselName;
      id = shipIdOverride ?? _runner.VesselGuid;
      var virt = _runner.Vessel;
      parts = new List<VesselStructureFormatter.PartEntry>();
      foreach (var partId in virt.AllPartIds()) {
        var internalName = virt.GetPartName(partId) ?? "";
        var title = ResolveTitle(internalName) ?? internalName;
        parts.Add(new VesselStructureFormatter.PartEntry {
          PartId = partId,
          InternalName = internalName,
          DisplayTitle = title,
          ParentId = virt.GetPartPartParentSafe(partId),
        });
      }
    }
    VesselStructureFormatter.Write(sb, id, name, parts);
  }

  private string ResolveTitle(string internalName) {
    var cfg = _partDb.Get(internalName);
    if (cfg == null) return null;
    var title = cfg.GetValue("title");
    return string.IsNullOrEmpty(title) ? null : title;
  }

  // Resolve a partId to its display title via PartDatabase. Mirrors
  // mod-side's AvailablePart.title lookup. Returns null when the part
  // isn't in the DB (shouldn't happen for parts hydrated through
  // SimVesselLoader, but guards against stale telemetry).
  private string ResolveTitleByPartId(uint partId) {
    lock (_runner.Lock) {
      var internalName = _runner.Vessel?.GetPartName(partId);
      return internalName != null ? ResolveTitle(internalName) : null;
    }
  }

  // ---- Per-part topics ---------------------------------------------

  private void WritePartFrame(StringBuilder sb, uint partId) {
    System.Collections.Generic.List<Nova.Core.Components.VirtualComponent> components;
    lock (_runner.Lock) {
      components = new System.Collections.Generic.List<Nova.Core.Components.VirtualComponent>();
      foreach (var c in _runner.Vessel.GetComponents(partId)) components.Add(c);
    }
    PartFormatter.Write(sb, partId, components);
  }

  private void WriteStorageFrame(StringBuilder sb, uint partId) {
    Nova.Core.Components.Science.DataStorage storage = null;
    double simNow;
    lock (_runner.Lock) {
      foreach (var c in _runner.Vessel.GetComponents(partId)) {
        if (c is Nova.Core.Components.Science.DataStorage s) { storage = s; break; }
      }
      simNow = _runner.SimUt;
    }
    StorageFormatter.Write(sb, partId, storage, simNow);
  }

  private void WriteScienceFrame(StringBuilder sb, uint partId) {
    var thermometers = new System.Collections.Generic.List<Nova.Core.Components.Science.Thermometer>();
    double simNow;
    lock (_runner.Lock) {
      foreach (var c in _runner.Vessel.GetComponents(partId)) {
        if (c is Nova.Core.Components.Science.Thermometer t) thermometers.Add(t);
      }
      simNow = _runner.SimUt;
    }
    // No atmosphere model yet: pass 0 K.
    ScienceFormatter.Write(sb, partId, thermometers, simNow, atmTempK: 0, ResolveTitleByPartId);
  }

  // ---- Engines + stage (DG-named topics) ---------------------------

  private void WriteEnginesFrame(StringBuilder sb) {
    var frames = new System.Collections.Generic.List<EngineFrame>();
    string vesselId;
    lock (_runner.Lock) {
      vesselId = _runner.VesselGuid;
      var virt = _runner.Vessel;
      var staging = virt.Systems?.Staging;
      foreach (var partId in virt.AllPartIds()) {
        foreach (var c in virt.GetComponents(partId)) {
          if (!(c is Nova.Core.Components.Propulsion.Engine engine)) continue;

          byte status = engine.EngineStatus;
          float throttle = status == 0
              ? System.Math.Min(1f, System.Math.Max(0f, (float)(engine.Throttle * engine.NormalizedOutput)))
              : 0f;

          var crossfeed = new System.Collections.Generic.List<string>();
          var props = new System.Collections.Generic.List<EnginePropellantFrame>();
          if (staging != null && engine.Node != null) {
            var reached = new System.Collections.Generic.HashSet<long>();
            foreach (var p in engine.Propellants) {
              foreach (var n in staging.ReachableNodes(engine.Node, p.Resource)) reached.Add(n.Id);
            }
            var ids = new System.Collections.Generic.List<long>(reached);
            ids.Sort();
            foreach (var id in ids) crossfeed.Add(id.ToString());

            foreach (var prop in engine.Propellants) {
              double amt = 0, cap = 0;
              foreach (var buf in staging.ReachableBuffers(engine.Node, prop.Resource)) {
                amt += buf.Contents;
                cap += buf.Capacity;
              }
              props.Add(new EnginePropellantFrame {
                Name = prop.Resource.Name,
                Abbreviation = prop.Resource.Abbreviation,
                Amount = amt,
                Capacity = cap,
              });
            }
          }

          frames.Add(new EngineFrame {
            Id = partId.ToString(),
            MapX = 0, MapY = 0, // sim has no engine-map geometry yet
            Status = status,
            Throttle = throttle,
            MaxThrust = (float)engine.Thrust,
            Isp = (float)engine.Isp,
            CrossfeedPartIds = crossfeed,
            Propellants = props,
          });
        }
      }
    }
    EngineFrameFormatter.Write(sb, vesselId, frames);
  }

  private void WriteStageFrame(StringBuilder sb) {
    var stages = new System.Collections.Generic.List<StageFrame>();
    string vesselId;
    int currentStage = -1;
    lock (_runner.Lock) {
      vesselId = _runner.VesselGuid;
      // v1 sim: no real staging concept yet — buckets parts by component
      // type so the staging stack shows what the craft contains even
      // without a defined firing order. Δv / TWR are zero placeholders;
      // proper sim staging arrives once DeltaVSimulation runs against
      // sim-defined stages (TODO).
      var virt = _runner.Vessel;
      var byKind = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<StagePartFrame>>();
      foreach (var partId in virt.AllPartIds()) {
        string kind = StageFrameFormatter.KindOther;
        foreach (var c in virt.GetComponents(partId)) {
          if (c is Nova.Core.Components.Propulsion.Engine) { kind = StageFrameFormatter.KindEngine; break; }
          if (c is Nova.Core.Components.Structural.Decoupler) { kind = StageFrameFormatter.KindDecoupler; break; }
        }
        if (kind == StageFrameFormatter.KindOther) continue;
        if (!byKind.TryGetValue(kind, out var bucket)) {
          bucket = new System.Collections.Generic.List<StagePartFrame>();
          byKind[kind] = bucket;
        }
        bucket.Add(new StagePartFrame {
          Kind = kind,
          PersistentId = partId.ToString(),
          IconName = kind == StageFrameFormatter.KindEngine ? "LIQUID_ENGINE" : "DECOUPLER_VERT",
          CousinsInStage = new System.Collections.Generic.List<string>(),
        });
      }
      // Single stage 0 holding everything for now.
      var parts = new System.Collections.Generic.List<StagePartFrame>();
      if (byKind.TryGetValue(StageFrameFormatter.KindDecoupler, out var decs)) parts.AddRange(decs);
      if (byKind.TryGetValue(StageFrameFormatter.KindEngine, out var engs)) parts.AddRange(engs);
      if (parts.Count > 0) {
        stages.Add(new StageFrame {
          Stage = 0,
          DeltaVActual = 0,
          TwrActual = 0,
          Parts = parts,
        });
      }
    }
    StageFrameFormatter.Write(sb, vesselId, currentStage, stages);
  }
}

// Tiny extension to expose GetPartParent in a null-safe form without
// reaching into VirtualVessel's internals from the sim.
internal static class VirtualVesselExt {
  public static uint? GetPartPartParentSafe(this Nova.Core.Components.VirtualVessel v, uint partId) {
    return v.GetPartParent(partId);
  }
}

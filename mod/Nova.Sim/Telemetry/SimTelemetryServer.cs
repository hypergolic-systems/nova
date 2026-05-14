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
//
// Topics the UI may request but the sim doesn't yet emit are silently
// no-op'd: subscribe succeeds, no frames flow. Add formatters and a
// dispatch case here as new topics come online.
public sealed class SimTelemetryServer : IDisposable {
  private readonly int _port;
  private readonly SimRunner _runner;
  private readonly PartDatabase _partDb;
  private WebSocketServer _server;
  private Timer _publishTimer;

  private readonly ConcurrentDictionary<Guid, Connection> _connections =
      new ConcurrentDictionary<Guid, Connection>();

  private sealed class Connection {
    public IWebSocketConnection Socket;
    public readonly HashSet<string> Subscriptions = new HashSet<string>();
  }

  public SimTelemetryServer(int port, SimRunner runner, PartDatabase partDb) {
    _port = port;
    _runner = runner;
    _partDb = partDb;
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
    Console.WriteLine("[ws] listening on ws://0.0.0.0:" + _port);
  }

  public void Dispose() {
    _publishTimer?.Dispose();
    _server?.Dispose();
  }

  private void HandleClientMessage(IWebSocketConnection socket, string msg) {
    if (!_connections.TryGetValue(socket.ConnectionInfo.Id, out var conn)) return;

    var (op, topic) = ParseEnvelope(msg);
    if (string.IsNullOrEmpty(topic)) return;

    if (op == "subscribe") {
      lock (conn.Subscriptions) conn.Subscriptions.Add(topic);
      // Snapshot-on-subscribe: deliver one frame immediately so late
      // subscribers don't stare at defaults.
      PublishOneTo(conn, topic);
    } else if (op == "unsubscribe") {
      lock (conn.Subscriptions) conn.Subscriptions.Remove(topic);
    }
    // Other custom ops not yet handled by the sim (would dispatch to a
    // per-topic handler analogous to NovaPartTopic.HandleOp).
  }

  // Minimal envelope parser. Avoids pulling in a JSON dependency for
  // just three known fields; falls back to (null,null) on shape
  // mismatch.
  private static (string op, string topic) ParseEnvelope(string s) {
    if (string.IsNullOrEmpty(s)) return (null, null);
    string op = ExtractString(s, "\"op\"");
    string topic = ExtractString(s, "\"topic\"");
    return (op, topic);
  }

  private static string ExtractString(string s, string key) {
    int idx = s.IndexOf(key, StringComparison.Ordinal);
    if (idx < 0) return null;
    idx = s.IndexOf(':', idx);
    if (idx < 0) return null;
    int q1 = s.IndexOf('"', idx);
    if (q1 < 0) return null;
    int q2 = s.IndexOf('"', q1 + 1);
    if (q2 < 0) return null;
    return s.Substring(q1 + 1, q2 - q1 - 1);
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
      // The sim is always "in flight" from the UI's perspective — the
      // Hud router needs scene="FLIGHT" to route to FlightHud.
      sb.Append('[');
      JsonWriter.WriteString(sb, "FLIGHT");
      sb.Append(',');
      JsonWriter.WriteString(sb, _runner.VesselGuid);
      sb.Append(',');
      JsonWriter.WriteDouble(sb, _runner.WarpFactor);
      sb.Append(',');
      sb.Append("false");
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
      WriteVesselStructureFrame(sb);
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
    // NovaEditorShipStructure: sim has no editor scene; silently skip.

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
        directSnr: 0, directRateBps: 0, directMaxRateBps: 0,
        directSnrFloor: 0, peerLabel: "",
        txActive: false, txRateBps: 0, txDeliveredBytes: 0, txTotalBytes: 0);
  }

  private void WriteVesselStructureFrame(StringBuilder sb) {
    List<VesselStructureFormatter.PartEntry> parts;
    string name, guid;
    lock (_runner.Lock) {
      name = _runner.VesselName;
      guid = _runner.VesselGuid;
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
    VesselStructureFormatter.Write(sb, guid, name, parts);
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

          byte status;
          if (engine.Ignited && engine.Flameout) status = 1;
          else if (engine.Ignited && engine.NormalizedOutput > 0) status = 0;
          else if (engine.Ignited) status = 4;
          else status = 3;
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

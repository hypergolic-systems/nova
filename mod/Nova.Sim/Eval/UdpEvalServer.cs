using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nova.Sim.Eval;

// UDP server accepting kspcli-style C# expressions. Each datagram is
// a UTF-8 request matching kspcli's wire format, so the `kspcli` CLI
// (~/dev/hgs/kspcli) drives the sim verbatim:
//
//   request:  "<id>\n<expr>"
//   response: "<id>\n+\n<body>"   on success, body = "$N = <rendered>"
//             "<id>\n-\n<error>"  on failure
//
// `<id>` is an opaque correlator (kspcli uses 12 hex chars). It's echoed
// verbatim so the client can match replies to outstanding requests and
// drop stale datagrams.
//
// The evaluator is shared across packets so `$N` refs persist; sim-defined
// globals (the runner, part DB) are exposed via the ref table at startup so
// agents reach them as e.g. `$0.Vessel`.
//
// Point kspcli-the-binary at the sim instead of in-game KSP with
// `KSPCLI_PORT=9877 kspcli eval '<expr>'`.
public sealed class UdpEvalServer : IDisposable {
  private readonly int _port;
  private readonly ExpressionEvaluator _evaluator;
  private UdpClient _client;
  private Thread _thread;
  private volatile bool _running;

  public ExpressionEvaluator Evaluator => _evaluator;

  public UdpEvalServer(int port) {
    _port = port;
    _evaluator = new ExpressionEvaluator();
  }

  public void Start() {
    _client = new UdpClient(new IPEndPoint(IPAddress.Loopback, _port));
    _running = true;
    _thread = new Thread(Loop) { IsBackground = true, Name = "Nova.Sim.Udp" };
    _thread.Start();
    Console.WriteLine("[udp] listening on udp://127.0.0.1:" + _port);
  }

  public void Dispose() {
    _running = false;
    _client?.Close();
    _thread?.Join(2000);
  }

  // Bind a name into the evaluator's runtime scope. The evaluator
  // resolves bare identifiers by reflection across loaded assemblies;
  // for our purposes we use `$0`, `$1`, … refs to expose specific
  // objects without polluting type lookups.
  public int RegisterRef(object value) => _evaluator.Store(value);

  private void Loop() {
    while (_running) {
      IPEndPoint sender;
      byte[] data;
      try {
        sender = new IPEndPoint(IPAddress.Any, 0);
        data = _client.Receive(ref sender);
      } catch (SocketException) {
        return;
      } catch (ObjectDisposedException) {
        return;
      }

      string id, expr;
      try {
        var text = Encoding.UTF8.GetString(data);
        var split = text.IndexOf('\n');
        if (split < 0) {
          Console.Error.WriteLine("[udp] malformed datagram from " + sender + ": no newline separator");
          continue;
        }
        id   = text.Substring(0, split);
        expr = text.Substring(split + 1);
      } catch (Exception ex) {
        Console.Error.WriteLine("[udp] undecodable datagram from " + sender + ": " + ex.Message);
        continue;
      }

      string body;
      bool ok;
      try {
        var value = _evaluator.Evaluate(expr);
        var handle = _evaluator.Store(value);
        body = "$" + handle + " = " + ExpressionEvaluator.Render(value);
        ok = true;
      } catch (Exception ex) {
        body = ex.Message;
        ok = false;
      }

      var reply = id + "\n" + (ok ? "+" : "-") + "\n" + body;
      try {
        var bytes = Encoding.UTF8.GetBytes(reply);
        _client.Send(bytes, bytes.Length, sender);
      } catch {
        // Best-effort; sender may have gone away.
      }
    }
  }
}

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nova.Sim.Eval;

// UDP server accepting kspcli-style C# expressions. Each datagram is
// a UTF-8 expression text; the server evaluates it against a shared
// ExpressionEvaluator (so `$N` refs persist across packets) and
// replies on the source endpoint with a UTF-8 text result.
//
// The expression runtime has root-namespace access to whatever's
// in the loaded assemblies — reflection resolves identifiers by
// short name. Sim-defined globals (the active runner, part DB,
// telemetry server) are exposed via the evaluator's reference table
// at startup so an agent can write e.g. `$0.Vessel` to reach the
// VirtualVessel.
//
// Wire format vs kspcli: kspcli wraps eval traffic in a protobuf
// Envelope/Fragment scheme. The sim simplifies to raw text since the
// data we exchange fits comfortably in a single MTU for most expressions,
// and the convenience of `nc -u localhost 9876` outweighs the
// over-MTU edge cases.
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
      IPEndPoint sender = null;
      byte[] data;
      try {
        sender = new IPEndPoint(IPAddress.Any, 0);
        data = _client.Receive(ref sender);
      } catch (SocketException) {
        // Socket closed during Dispose — exit loop.
        return;
      } catch (ObjectDisposedException) {
        return;
      }

      string expr = Encoding.UTF8.GetString(data).Trim();
      if (expr.Length == 0) continue;

      string reply;
      try {
        var value = _evaluator.Evaluate(expr);
        var handle = _evaluator.Store(value);
        reply = "$" + handle + " = " + ExpressionEvaluator.Summarize(value);
      } catch (Exception ex) {
        reply = "ERROR: " + ex.Message;
      }

      try {
        var bytes = Encoding.UTF8.GetBytes(reply);
        _client.Send(bytes, bytes.Length, sender);
      } catch {
        // Best-effort; sender may have gone away.
      }
    }
  }
}

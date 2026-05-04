using System;

namespace Nova.Core.Communications;

// Type-erased base so the network can hold a heterogeneous list of
// Broadcast<T> jobs and match them against Receive<T> at runtime.
// Two broadcasts with the same TKey and equal Key are part of the
// same global topic; the matching also requires the receive side's
// TKey to match (so Broadcast<int>(5) doesn't feed Receive<string>("5")).
public abstract class BroadcastJob : Job {

  // Source-side push ceiling (bytes/sec). The actual sum of allocated
  // outgoing rates across matching receivers never exceeds this.
  public double TargetRateBps { get; }

  // Cumulative bytes successfully transmitted (sum across all matching
  // receive flows over time). Useful for telemetry/debug.
  public long BytesSent { get; internal set; }

  internal abstract Type KeyType { get; }
  internal abstract object KeyAsObject { get; }

  protected BroadcastJob(Endpoint source, double targetRateBps) : base(source) {
    if (targetRateBps < 0) throw new ArgumentOutOfRangeException(nameof(targetRateBps));
    TargetRateBps = targetRateBps;
  }
}

// Typed pub/sub source. The network divides TargetRateBps among
// every matching Receive<TKey> (any sender → any receiver with the
// same key); each receiver's slice is further capped by its own
// MaxRateBps and by the path bandwidth from this source to it.
public class Broadcast<TKey> : BroadcastJob {

  public TKey Key { get; }
  public Endpoint Source => Endpoint;

  internal override Type KeyType => typeof(TKey);
  internal override object KeyAsObject => Key;

  public Broadcast(Endpoint source, TKey key, double targetRateBps)
      : base(source, targetRateBps) {
    Key = key;
  }
}

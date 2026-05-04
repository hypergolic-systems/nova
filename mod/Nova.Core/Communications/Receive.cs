using System;

namespace Nova.Core.Communications;

// Type-erased base. See BroadcastJob for matching semantics.
public abstract class ReceiveJob : Job {

  // Receive-side acceptance ceiling (bytes/sec). The allocator never
  // pushes more than this into the receiver, regardless of how much
  // matching broadcast traffic is available.
  public double MaxRateBps { get; }

  // Cumulative bytes received from matching broadcasts.
  public long BytesReceived { get; internal set; }

  internal abstract Type KeyType { get; }
  internal abstract object KeyAsObject { get; }

  protected ReceiveJob(Endpoint receiver, double maxRateBps) : base(receiver) {
    if (maxRateBps < 0) throw new ArgumentOutOfRangeException(nameof(maxRateBps));
    MaxRateBps = maxRateBps;
  }
}

// Typed pub/sub sink. Pulls from any active Broadcast<TKey> with an
// equal Key value; the actual rate is capped by MaxRateBps, by the
// broadcast's TargetRateBps share, and by the path bandwidth from
// each broadcast source to this receiver.
public class Receive<TKey> : ReceiveJob {

  public TKey Key { get; }
  public Endpoint Receiver => Endpoint;

  internal override Type KeyType => typeof(TKey);
  internal override object KeyAsObject => Key;

  public Receive(Endpoint receiver, TKey key, double maxRateBps)
      : base(receiver, maxRateBps) {
    Key = key;
  }
}

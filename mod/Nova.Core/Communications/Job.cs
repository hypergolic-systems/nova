using System;

namespace Nova.Core.Communications;

public enum JobStatus {
  Active,
  Completed,
  Cancelled,
}

// Base for all communications jobs. Subclasses model the shape of
// the demand (one-shot Packet, Broadcast<TKey>, Receive<TKey>); the
// Network owns the lifetime, the allocator owns the rate.
//
// `Endpoint` is the network endpoint the job was submitted to —
// transmitter for Packet/Broadcast, receiver for Receive. `Status`
// flips Active → Completed (Packet only, when bytes drained) or
// Active → Cancelled (any, via network.Cancel).
public abstract class Job {

  public string Id { get; } = Guid.NewGuid().ToString("N");
  public Endpoint Endpoint { get; }
  public JobStatus Status { get; internal set; } = JobStatus.Active;

  // Most recent allocator output: bytes/sec this job is currently
  // moving. For a Packet, the end-to-end rate (bottleneck of its
  // path); for a Broadcast, the total source-side push (sum of all
  // matching receive flows); for a Receive, this receiver's slice.
  public double AllocatedRateBps { get; internal set; }

  protected Job(Endpoint endpoint) {
    Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
  }
}

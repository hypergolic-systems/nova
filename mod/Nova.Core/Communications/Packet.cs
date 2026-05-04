using System;

namespace Nova.Core.Communications;

// One-shot byte transfer between two endpoints. Routes via the
// max-rate path through the current graph; Status flips to
// Completed once DeliveredBytes reaches TotalBytes.
public class Packet : Job {

  public Endpoint Source => Endpoint;
  public Endpoint Destination { get; }
  public long TotalBytes { get; }

  // Cumulative bytes successfully delivered. Advanced each Solve by
  // (AllocatedRateBps · dt), capped at TotalBytes.
  public long DeliveredBytes { get; internal set; }

  public long RemainingBytes => Math.Max(0, TotalBytes - DeliveredBytes);

  public Packet(Endpoint source, Endpoint destination, long totalBytes) : base(source) {
    if (destination == null) throw new ArgumentNullException(nameof(destination));
    if (totalBytes < 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));
    Destination = destination;
    TotalBytes = totalBytes;
  }
}

using System.Collections.Generic;

namespace Nova.Core.Communications;

// Allocator-side view of an edge: real Link or virtual broadcast
// budget. Capacity is fixed at construction; Used grows during
// water-filling. After allocation, Used is mirrored back to
// BackingLink.UsedBps (for real edges).
internal class AllocEdge {
  public double Capacity;
  public double Used;
  public Link BackingLink;
  public List<AllocFlow> Flows = new();
  public bool Saturated;
}

// One single-rate flow. Either a Packet flow (Packet set) or one
// (Broadcast, Receive) pair flow (Broadcast + Receive set). Path =
// the AllocEdges the flow traverses (real link edges, plus a virtual
// broadcast-budget edge for Broadcast flows).
internal class AllocFlow {
  public Packet Packet;
  public BroadcastJob Broadcast;
  public ReceiveJob Receive;
  public double Ceiling = double.PositiveInfinity;
  public List<AllocEdge> Edges = new();
  public double Rate;
  public bool Saturated;
}

// Max-min fair allocator over a multi-commodity flow graph.
// Iterative water-filling: each step, give every unsaturated flow
// the smallest increment that any of its edges (or its own ceiling)
// permits; saturate the flow/edge that hit the bound; repeat until
// nothing can grow.
internal static class BandwidthAllocator {

  public static void Allocate(List<AllocFlow> flows, List<AllocEdge> edges) {
    foreach (var e in edges) e.Used = 0;

    while (true) {
      bool anyUnsat = false;
      foreach (var f in flows) {
        if (!f.Saturated) { anyUnsat = true; break; }
      }
      if (!anyUnsat) break;

      double delta = double.PositiveInfinity;

      foreach (var f in flows) {
        if (f.Saturated) continue;
        var hr = f.Ceiling - f.Rate;
        if (hr < delta) delta = hr;
      }
      foreach (var e in edges) {
        if (e.Saturated) continue;
        int n = 0;
        foreach (var f in e.Flows) if (!f.Saturated) n++;
        if (n == 0) continue;
        var perFlow = (e.Capacity - e.Used) / n;
        if (perFlow < delta) delta = perFlow;
      }

      // No room to grow anywhere — freeze remaining unsaturated flows.
      if (delta <= 0 || double.IsPositiveInfinity(delta)) {
        foreach (var f in flows) f.Saturated = true;
        break;
      }

      foreach (var f in flows) {
        if (f.Saturated) continue;
        f.Rate += delta;
      }
      foreach (var e in edges) {
        if (e.Saturated) continue;
        int n = 0;
        foreach (var f in e.Flows) if (!f.Saturated) n++;
        if (n > 0) e.Used += delta * n;
      }

      const double Eps = 1e-12;
      foreach (var f in flows) {
        if (f.Saturated) continue;
        if (f.Rate + Eps >= f.Ceiling) {
          f.Rate = f.Ceiling;
          f.Saturated = true;
        }
      }
      foreach (var e in edges) {
        if (e.Saturated) continue;
        if (e.Used + Eps >= e.Capacity) {
          e.Used = e.Capacity;
          e.Saturated = true;
          // All flows still active on this edge are bottlenecked here.
          foreach (var f in e.Flows) {
            if (!f.Saturated) f.Saturated = true;
          }
        }
      }
    }

    foreach (var e in edges) {
      if (e.BackingLink != null) e.BackingLink.UsedBps = e.Used;
    }
  }
}

using System.Collections.Generic;

namespace Nova.Core.Communications;

// Max-bottleneck-path search through a directed comms graph.
// Distance to a node = max over reaching paths of the path's
// minimum-edge rate. Modified Dijkstra: relax with `min(dist[u], rate(u→v))`,
// pop with `argmax dist`. Linear-scan PQ — endpoint counts are small.
public static class MaxRatePath {

  // Returns the ordered Links along the chosen source→dest path.
  // Null if no positive-rate path exists, or if source == dest.
  public static List<Link> Find(GraphSnapshot graph, Endpoint source, Endpoint dest) {
    if (source == dest) return null;

    var adj = new Dictionary<Endpoint, List<Link>>();
    foreach (var link in graph.Links) {
      if (link.RateBps <= 0) continue;
      if (!adj.TryGetValue(link.From, out var outs)) {
        outs = new List<Link>();
        adj[link.From] = outs;
      }
      outs.Add(link);
    }

    var dist = new Dictionary<Endpoint, double> { [source] = double.PositiveInfinity };
    var prevEdge = new Dictionary<Endpoint, Link>();
    var visited = new HashSet<Endpoint>();

    while (true) {
      Endpoint u = null;
      double best = 0;
      foreach (var kv in dist) {
        if (visited.Contains(kv.Key)) continue;
        if (u == null || kv.Value > best) {
          u = kv.Key;
          best = kv.Value;
        }
      }
      if (u == null || best <= 0) break;

      visited.Add(u);
      if (u == dest) break;

      if (!adj.TryGetValue(u, out var outs)) continue;
      foreach (var link in outs) {
        if (visited.Contains(link.To)) continue;
        var bottleneck = best < link.RateBps ? best : link.RateBps;
        if (!dist.TryGetValue(link.To, out var existing) || bottleneck > existing) {
          dist[link.To] = bottleneck;
          prevEdge[link.To] = link;
        }
      }
    }

    if (!prevEdge.ContainsKey(dest)) return null;

    var path = new List<Link>();
    var cur = dest;
    while (cur != source) {
      var edge = prevEdge[cur];
      path.Add(edge);
      cur = edge.From;
    }
    path.Reverse();
    return path;
  }
}

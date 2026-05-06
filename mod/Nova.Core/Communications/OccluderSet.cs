using System;
using System.Collections.Generic;

namespace Nova.Core.Communications;

// Builds the per-link occluder set: which celestial bodies could
// potentially block the line of sight between two endpoints.
//
// Rule:
//   LCA = lowest common ancestor of the two endpoints' parent chains
//         in the SOI tree.
//   Penultimate-A = body on A's chain just below LCA, or LCA itself
//                   if A's PrimaryBody IS the LCA.
//   Penultimate-B = same for B.
//   Set = {LCA} ∪ subtree(penultA) if penultA ≠ LCA
//                ∪ subtree(penultB) if penultB ≠ LCA
//
// Worked examples:
//   KSC ↔ Kerbin-orbit:       {Kerbin}
//   Mun-orbit ↔ Kerbin-orbit: {Kerbin, Mun}
//   Kerbin ↔ Moho:            {Sun, Kerbin, Mun, Minmus, Moho}
//   Kerbin ↔ Laythe:          10 bodies (Sun + Kerbin SOI + Jool SOI)
//
// Endpoints with null PrimaryBody yield an empty set — the link is
// treated as always unblocked. This is the safe default for test
// fixtures that don't wire body context.
public static class OccluderSet {

  public static IReadOnlyList<Body> For(Endpoint a, Endpoint b) {
    if (a?.PrimaryBody == null || b?.PrimaryBody == null)
      return Array.Empty<Body>();

    var chainA = ChainToRoot(a.PrimaryBody);
    var chainB = ChainToRoot(b.PrimaryBody);
    var inB = new HashSet<Body>(chainB);

    int idxA = -1;
    Body lca = null;
    for (int i = 0; i < chainA.Count; i++) {
      if (inB.Contains(chainA[i])) {
        idxA = i;
        lca = chainA[i];
        break;
      }
    }
    if (lca == null) return Array.Empty<Body>();
    int idxB = chainB.IndexOf(lca);

    var penultA = idxA == 0 ? lca : chainA[idxA - 1];
    var penultB = idxB == 0 ? lca : chainB[idxB - 1];

    var result = new HashSet<Body> { lca };
    if (penultA != lca) AddSubtree(penultA, result);
    if (penultB != lca) AddSubtree(penultB, result);
    return new List<Body>(result);
  }

  private static List<Body> ChainToRoot(Body leaf) {
    var chain = new List<Body>();
    var cur = leaf;
    while (cur != null) {
      chain.Add(cur);
      cur = cur.Parent;
    }
    return chain;
  }

  private static void AddSubtree(Body root, HashSet<Body> set) {
    if (!set.Add(root)) return;
    foreach (var child in root.Children) AddSubtree(child, set);
  }
}

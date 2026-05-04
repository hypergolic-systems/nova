using System.Collections.Generic;

namespace Nova.Core.Communications;

// Steady-state graph produced by CommunicationsNetwork.Solve. Holds
// the directed-edge list plus the UT at which positions were
// evaluated. Read-only — callers receive a new snapshot from each
// solve.
public class GraphSnapshot {

  public IReadOnlyList<Link> Links { get; }
  public double SolvedUt { get; }

  public GraphSnapshot(IReadOnlyList<Link> links, double solvedUt) {
    Links = links;
    SolvedUt = solvedUt;
  }

  public static readonly GraphSnapshot Empty = new(new Link[0], double.NaN);
}

using System.Collections.Generic;

namespace Nova.Core.Science;

// Mod-load-time registry of experiment definitions. The mod side
// constructs one (passing in the populated AtmosphereLayers from configs)
// and assigns to Instance; tests build their own.
public class ExperimentRegistry {
  public static ExperimentRegistry Instance { get; set; }

  private readonly Dictionary<string, ExperimentDefinition> byId = new();

  public void Register(ExperimentDefinition exp) {
    byId[exp.Id] = exp;
  }

  public ExperimentDefinition Get(string id) =>
      byId.TryGetValue(id, out var e) ? e : null;

  public IEnumerable<ExperimentDefinition> All => byId.Values;

  public IEnumerable<ExperimentDefinition> Applicable(SubjectContext ctx) {
    foreach (var e in byId.Values)
      if (e.IsApplicable(ctx)) yield return e;
  }
}

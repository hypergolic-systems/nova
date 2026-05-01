namespace Nova.Core.Science;

// "Profile the atmosphere by transit." Applicable wherever an
// atmospheric layer is defined for the current (body, altitude). Subject
// = (body, layer). The instrument's PartModule edge-detects layer
// transitions and emits a fidelity-1.0 file each time the vessel
// crosses a boundary.
public class AtmosphericProfileExperiment : ExperimentDefinition {
  public const string ExperimentId = "atm-profile";

  private readonly AtmosphereLayers layers;

  public AtmosphericProfileExperiment(AtmosphereLayers layers) {
    this.layers = layers;
  }

  public override string Id => ExperimentId;

  public override bool IsApplicable(SubjectContext ctx) =>
      layers.LayerAt(ctx.BodyName, ctx.Altitude).HasValue;

  public override SubjectKey? ResolveSubject(SubjectContext ctx) {
    var layer = layers.LayerAt(ctx.BodyName, ctx.Altitude);
    return layer.HasValue
        ? new SubjectKey(ExperimentId, ctx.BodyName, layer.Value.Name)
        : (SubjectKey?)null;
  }
}

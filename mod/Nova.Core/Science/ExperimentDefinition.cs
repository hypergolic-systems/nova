namespace Nova.Core.Science;

// Stateless definition of an experiment. Concrete subclasses encode the
// applicability rules and subject-resolution logic. Registered once at
// mod-load with ExperimentRegistry.
public abstract class ExperimentDefinition {
  public abstract string Id { get; }

  // Is this experiment runnable in the given context?
  public abstract bool IsApplicable(SubjectContext ctx);

  // Resolve the subject this context observes, or null if not applicable.
  // ResolveSubject(ctx) is meaningful iff IsApplicable(ctx) is true.
  public abstract SubjectKey? ResolveSubject(SubjectContext ctx);
}

namespace Nova.Components;

public class NovaCrewModule : NovaPartModule {
  public override void OnStart(StartState state) {
    base.OnStart(state);
    part.crewTransferAvailable = false;
  }
}

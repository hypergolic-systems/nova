namespace Nova.Components;

// KSP-side wrapper for an antenna part. The actual Antenna data
// (TxPower, Gain, MaxRate, RefDistance) lives on the virtual
// component instantiated by ComponentFactory; NovaVesselModule
// discovers all Antenna components via Virtual and pushes the
// vessel endpoint into NovaCommunicationsAddon. No ICommAntenna —
// Nova owns its own graph, stock CommNet routing is bypassed.
public class NovaAntennaModule : NovaPartModule { }

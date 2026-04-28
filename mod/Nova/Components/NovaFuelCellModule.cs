namespace Nova.Components;

// Thin wrapper. The fuel cell is autonomous — VirtualVessel.UpdateFuelCellDevices
// drives Demand each tick from aggregate battery SoC, so the module
// doesn't need any per-frame KSP-side logic. Existence as a PartModule
// is what makes the part's components discoverable by NovaPartModule's
// OnStart and ComponentFactory.
public class NovaFuelCellModule : NovaPartModule {
}

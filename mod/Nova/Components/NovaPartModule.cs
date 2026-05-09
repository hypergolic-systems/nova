namespace Nova.Components;

/// <summary>
/// Base for every Nova-aware <see cref="PartModule"/>. The previous
/// version owned a list of <c>VirtualComponent</c> instances and a
/// <c>ComponentFactory</c>-driven editor/flight startup; that's all
/// gone with the C# simulator move to <c>_legacy/</c>.
///
/// The remaining surface is intentionally minimal: KSP needs a
/// concrete <c>PartModule</c> to attach to, configs reference these
/// classes by name, and FFI-aware subclasses (Battery, Command) read
/// state from <see cref="NovaVesselModule.Handle"/>.
/// </summary>
public class NovaPartModule : PartModule {
}

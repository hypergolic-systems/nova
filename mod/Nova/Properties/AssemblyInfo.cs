// KSP's AssemblyLoader honours [KSPAssemblyDependency] to reorder DLL
// loading so dependencies bind before consumers. Without these
// declarations, KSP scans GameData/*/Plugins alphabetically — Nova
// loads after Dragonglass (D < N, lucky) but BEFORE Waterfall (N < W,
// breaks) and the addon binder fails to resolve Waterfall, which
// type-loads Nova.dll itself and disables the entire mod.
//
// Neither Waterfall nor Dragonglass declare [KSPAssembly] in their
// own AssemblyInfo, so KSP falls back to using the DLL filename as
// the assembly name and 0.0.0 as the version. Using (0, 0, 0) here
// makes the version match a no-op — these declarations exist purely
// for load ordering, not version enforcement. Bumping a min-version
// would require the upstream mod to add a matching [KSPAssembly]
// declaration.

[assembly: KSPAssemblyDependency("Waterfall", 0, 0)]
[assembly: KSPAssemblyDependency("Dragonglass.Hud", 0, 0)]
[assembly: KSPAssemblyDependency("Dragonglass.Telemetry", 0, 0)]

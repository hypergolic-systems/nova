# CLAUDE.md

Guidance for Claude Code when working in the Nova repo.

## What this is

Nova is a KSP 1.x core mod that overhauls resource and vessel simulation to support true background multi-mission operations. Targets .NET Framework 4.8 (KSP 1.12.5's bundled Mono).

Nova was previously named "Hypergolic Systems" — HGS now refers to the umbrella mod-family brand of which Nova is a member. Sibling mods under HGS:
- **Dragonglass** (`~/dev/dragonglass`) — CEF + web UI for KSP. Owns all custom UI; Nova does not ship UI plumbing.
- **kspcli** (`~/dev/hgs/kspcli`) — agentic bridge: protobuf wire format, Rust CLI + KSPAddon listener. Replaces the old HGS Bridge/BridgeCLI.

## Build & run

```
just proto             # regenerate C# bindings from proto/nova.proto
just mod-build         # build everything (proto + Nova.Core, Nova, Nova.Tests)
just test              # run MSTest suite
just dist              # produce release/Nova.zip
just install ~/KSP_osx # build + install into a KSP directory
```

`mod-build` depends on `proto`, so a fresh checkout just needs `just mod-build`.

## Project layout

```
mod/
  Nova.sln
  Nova.Core/            # engine — no KSP/Unity refs, testable
  Nova/                 # KSP integration — KSPAddon, Harmony, behaviors
  Nova.Tests/           # MSTest, references Nova.Core only
configs/overrides/      # ModuleManager .cfg patches → GameData/Nova/Patches/
stubs/                  # KSP/Unity managed DLLs (KSP 1.12.5)
justfile
```

Two output assemblies, both shipped to `GameData/Nova/`:

- **`Nova.Core.dll`** — platform-agnostic simulation engine. Resource solver (OR-Tools GLOP LP), virtual component system, RCS solver, persistence file format. No KSP types.
- **`Nova.dll`** — KSP integration layer. KSPAddon, Harmony patches, PartModule subclasses, save/load builders. References `Nova.Core`.

The two-DLL split is intentional: it lets Nova.Core be tested in isolation and prevents accidental KSP coupling in the engine. Don't merge them.

## Namespaces

```
Nova.Core (engine)
  Nova.Core.Components{,.Control,.Crew,.Electrical,.Propulsion,.Structural}
  Nova.Core.Resources       # ResourceSolver, Resource, Buffer, DV/Shadow/Solar
  Nova.Core.Flight          # RcsSolver
  Nova.Core.Persistence     # NovaFileFormat
    Nova.Core.Persistence.Protos   # protobuf message types (generated from proto/nova.proto)
  Nova.Core.Utils           # Vec3d, Curve

Nova (mod)
  Nova                      # NovaMod (KSPAddon), HarmonyPatcher, NovaLog
  Nova.Components           # NovaXxxModule (parts), NovaVesselModule, ComponentFactory
  Nova.Patches              # Harmony patches on stock KSP
  Nova.Persistence          # KSP-side save/load builders
```

## Architecture

### Resource solver (`Nova.Core/Resources/`)

LP-based resource flow simulation. Each tick, `ResourceSolver.Solve()`:

1. Maximize consumer satisfaction across all consumers.
2. Pin satisfaction levels.
3. Maximize buffer fill with remaining capacity.
4. Minimize cost (prefer cheap producers).

`Topology` is a graph of `Node`s with `Edge`s; nodes hold `NodeResource` entries with LP variables and conservation constraints.

### Virtual component system (`Nova.Core/Components/`)

`VirtualComponent` is the base for all simulated part modules. `VirtualVessel` aggregates components per vessel. `Registry` discovers/instantiates types. State round-trips through `ConfigNode`-shaped adapters defined in Core.

### Adapter pattern

`Nova.Core` defines stub interfaces (`Logger`, `ConfigNode`, `Part`, `ShipConstruct`, `Planetarium`). `Nova` implements them against real KSP/Unity types. Tests inject fakes.

### KSP integration (`Nova/`)

- `NovaMod.cs` — KSPAddon entry point, singleton.
- `HarmonyPatcher.cs` — applies all patches at startup.
- `Patches/` — Harmony patches on `ProtoVessel`, `ShipConstruct`, save/load, etc.
- `Components/NovaPartModule.cs` + subclasses — KSP `PartModule`s wrapping virtual components.
- `Components/NovaVesselModule.cs` — VesselModule for vessel-level simulation.

### Patching strategy

KSP 1.x is dead; Squad will never ship another patch. The decompiled IL in `~/dev/ksp-reference/Assembly-CSharp/` is frozen.

- **Transpilers are fair game** — no upstream drift.
- **Reflection against private members is safe** — names won't change.
- **Copy-and-modify replacements are viable** — when stock logic mixes desired and undesired behavior.
- **Still prefer the lightest patch that works** — prefix/postfix when adding behavior, transpiler when tweaking, full replacement when shape conflicts.

### Persistence wire format (`proto/nova.proto`)

The `.hgc` (craft) and `.hgs` (save) file formats are defined by `proto/nova.proto`. `just proto` regenerates `mod/Nova.Core/Persistence/Protos/Generated/nova.cs` via the `protobuf-net.protogen` dotnet tool (versioned in `.config/dotnet-tools.json`). Generated files are gitignored.

Conventions:
- **Don't edit the generated `.cs`** — regenerate from the proto.
- **Field numbers are wire-compat** — never reuse a number; use `reserved` for retired fields.
- **Generated property names** follow protogen's pluralization (`repeated Kerbal crew` → `Crews`) and casing (`launch_id` → `LaunchId`). When a field name surprises you, check the generated file.
- **Repeated message types** become get-only `List<T>` (use collection-initializer `Foo = { … }` or `.AddRange(…)`); **repeated primitive types** become `T[]` (writable).

### Configs (`configs/overrides/`)

ModuleManager patches that strip stock modules and inject Nova's. Module names match C# class names (e.g., `name = NovaBatteryModule`). When renaming a `NovaXxxModule`, update the matching configs.

## Key dependencies

- **Google.OrTools 9.8.3296** + `osx-x64` native runtime — LP solver. The native `.dylib` ships in `GameData/Nova/` (justfile dist recipe).
- **Lib.Harmony 2.2.2** — runtime KSP patching. Bundled with `Nova.dll`.
- **protobuf-net 2.4.8** — binary serialization for `.hgs`/`.hgc` files. Bindings generated from `proto/nova.proto` by `protobuf-net.protogen`.
- **Moq 4.20.70** — mocking, tests only.

## Game introspection

Use `kspcli` for live game inspection / scripted scenes. From `~/dev/hgs/kspcli`:

```
just install ~/KSP_osx          # one-time, install kspcli mod
just run -- start
just run -- load_save default
just run -- vessels
just run -- eval <expression>
```

Don't add bridge/CLI commands inside Nova — they live in kspcli.

## Future work

- **SaveCLI** — the old HGS C# `SaveCLI` was dropped during migration. A Rust replacement is planned but not yet built.

## KSP API reference

`~/dev/ksp-reference/` contains the decompiled stock KSP code. Use it to look up signatures and behavior; never modify.

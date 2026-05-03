# CLAUDE.md

Guidance for Claude Code when working in the Nova repo.

## What this is

Nova is a KSP 1.x core mod that overhauls resource and vessel simulation to support true background multi-mission operations. Targets .NET Framework 4.8 (KSP 1.12.5's bundled Mono).

Nova was previously named "Hypergolic Systems" — HGS now refers to the umbrella mod-family brand of which Nova is a member. Sibling mods under HGS:
- **Dragonglass** (`~/dev/dragonglass`) — CEF + web UI for KSP. Owns all custom UI; Nova does not ship UI plumbing.
- **kspcli** (`~/dev/hgs/kspcli`) — agentic bridge: protobuf wire format, Rust CLI + KSPAddon listener. Replaces the old HGS Bridge/BridgeCLI.

## Build & run

```
just proto                      # regenerate C# bindings from proto/nova.proto
just sync-dragonglass-stubs     # vendor Dragonglass DLLs into stubs/dragonglass/
just mod-build                  # build everything (proto + Nova.Core, Nova, Nova.Tests)
just test                       # run MSTest suite
just ui-bootstrap               # symlink Dragonglass + npm install ui/
just ui-build                   # Vite library build → ui/apps/nova/dist/
just dist                       # produce release/Nova.zip (mod + UI)
just install ~/KSP_osx          # build + install into a KSP directory
```

`mod-build` depends on `proto`, so a fresh checkout just needs `just mod-build`. Anything that crosses into Dragonglass — `sync-dragonglass-stubs`, `ui-bootstrap`, and the targets that depend on them — requires `$DRAGONGLASS_PATH` set in the environment (e.g. `export DRAGONGLASS_PATH=~/dev/dragonglass`).

### Cross-repo commit ordering

CI checks out Dragonglass at `main` (sibling-checkout convention extended to GitHub Actions; see `.github/workflows/ci.yml`). When a Nova change depends on a Dragonglass change — new API surface, refactored types, importmap entries, anything the C# `<Reference>` or the UI typecheck/bundle pulls — the Dragonglass change must be pushed to `main` *before* Nova's CI run. Otherwise the sibling checkout pulls a Dragonglass tree without the new code and Nova's build fails. The intended workflow:

1. Land the Dragonglass-side change on Dragonglass `main`.
2. Push the matching Nova change.

If the Dragonglass change isn't ready to ship yet, hold the Nova-side commit until it is — both repos co-evolve in lockstep on `main`. There's no per-PR ref override yet; if you need one, plumb a `dragonglass-ref` workflow input and a matching `with: ref:` on the sibling `actions/checkout`.

## Project layout

```
mod/
  Nova.sln
  Nova.Core/            # engine — no KSP/Unity refs, testable
  Nova/                 # KSP integration — KSPAddon, Harmony, behaviors
  Nova.Tests/           # MSTest, references Nova.Core only
ui/                     # Svelte UI app, deployed to GameData/Nova/UI/
  apps/nova/            # @nova/app — Vite library build, single entry hud.ts
  packages/             # @nova/* shared libs (placeholder)
  external/dragonglass  # symlink → $DRAGONGLASS_PATH (gitignored)
crates/                 # Cargo workspace (members = crates/*)
  save-cli/             # nova-save-cli — inspector for .hgs / .hgc files
proto/nova.proto        # persistence schema, source-of-truth for both C# and Rust
configs/overrides/      # ModuleManager .cfg patches → GameData/Nova/Patches/
stubs/                  # KSP/Unity managed DLLs (KSP 1.12.5)
  dragonglass/          # vendored Dragonglass.{Hud,Telemetry}.dll
justfile
```

Two output assemblies, both shipped to `GameData/Nova/`:

- **`Nova.Core.dll`** — platform-agnostic simulation engine. Two-solver resource runtime (water-fill StagingFlowSystem for Topological resources, OR-Tools GLOP ProcessFlowSystem for Uniform resources), virtual component system, RCS solver, persistence file format. No KSP types.
- **`Nova.dll`** — KSP integration layer. KSPAddon, Harmony patches, PartModule subclasses, save/load builders. References `Nova.Core`.

The two-DLL split is intentional: it lets Nova.Core be tested in isolation and prevents accidental KSP coupling in the engine. Don't merge them.

## Namespaces

```
Nova.Core (engine)
  Nova.Core.Components{,.Control,.Crew,.Electrical,.Propulsion,.Structural}
  Nova.Core.Resources       # Resource, Buffer, DeltaVSimulation, Shadow/Solar
  Nova.Core.Systems         # StagingFlowSystem, ProcessFlowSystem, BackgroundSystem, VesselSystems
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

### Resource flow (`Nova.Core/Systems/`)

Two specialised solvers, partitioned by resource domain. Each implements `BackgroundSystem` (`Solve` / `Tick(dt)` / `MaxTickDt`); `VesselSystems` is the per-vessel container; `VirtualVessel.Tick` is the runner.

- **`StagingFlowSystem`** — water-fill for **Topological** resources (RP-1, LOX, LH₂, Hydrazine, Xenon). Owns the vessel topology graph (nodes, edges with `AllowedResources` / `UpOnlyResources` filters, drain priorities). Per Solve, per (DrainPriority, resource, connected component): drain pools proportionally to current Contents, clipped per-pool by `MaxRateOut`, recurse on the binding pool. Pure arithmetic — no LP, no degeneracy.

- **`ProcessFlowSystem`** — slim LP for **Uniform** resources (ElectricCharge today; O₂ / CO₂ / H₂O / heat tomorrow). Single vessel-wide pool per resource, no topology. Device-priority loop (Critical → High → Low) with `max α + ε·Σ activity`, then a lex-2 cleanup pass that minimises Σ supply + Σ fill to suppress LP-cycling artefacts.

The two domains never share a resource (Resource.Domain enum tags it at construction). Components register with one or both at `OnBuildSystems(VesselSystems, StagingFlowSystem.Node)` time.

For the LP envelope on the Process side, see `docs/lp_hygiene.md`.

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
- **No backwards compatibility.** Old proto bytes, old `.hgc`/`.hgs` files, old sidecar instances are all fair game to break. Field numbers can be reused freely; retired fields don't need to be `reserved`. Active-development mod, single user — re-saves and reinstalls are cheap, fallback/migration code in loaders is permanent overhead. If a schema change breaks an existing save, the right answer is "rebuild the craft," not a defensive load.
- **Generated property names** follow protogen's pluralization (`repeated Kerbal crew` → `Crews`) and casing (`launch_id` → `LaunchId`). When a field name surprises you, check the generated file.
- **Repeated message types** become get-only `List<T>` (use collection-initializer `Foo = { … }` or `.AddRange(…)`); **repeated primitive types** become `T[]` (writable).

### Configs (`configs/overrides/`)

ModuleManager patches that strip stock modules and inject Nova's. Module names match C# class names (e.g., `name = NovaBatteryModule`). When renaming a `NovaXxxModule`, update the matching configs.

## Key dependencies

- **Google.OrTools 9.8.3296** + `osx-x64` native runtime — LP solver. The native `.dylib` ships in `GameData/Nova/` (justfile dist recipe).
- **Lib.Harmony 2.2.2** — runtime KSP patching. Bundled with `Nova.dll`.
- **protobuf-net 2.4.8** — binary serialization for `.hgs`/`.hgc` files. Bindings generated from `proto/nova.proto` by `protobuf-net.protogen`.
- **Moq 4.20.70** — mocking, tests only.
- **Dragonglass** (`~/dev/dragonglass` via `$DRAGONGLASS_PATH`) — hard runtime dep. `Dragonglass.Telemetry.dll` for telemetry topic types; `Dragonglass.Hud.dll` for `SidecarHost.OverrideEntry`. DLLs vendored into `stubs/dragonglass/` by `just sync-dragonglass-stubs`. Nova does NOT ship them — Dragonglass installs them from its own deploy.

## UI (Dragonglass integration)

Nova's UI is a Svelte app under `ui/apps/nova/`, built via Vite library mode → `ui/apps/nova/dist/hud.js`, deployed to `GameData/Nova/UI/hud.js` by `just dist`. Dragonglass's CEF sidecar walks `GameData/*/UI/` at startup, exposes `@<modname>/<file>` import-map specifiers, and the synthesized shell imports whichever specifier its `--entry=` arg names.

`NovaUiOverrideAddon` (Startup.Instantly) calls `Dragonglass.Hud.SidecarHost.OverrideEntry("@nova/hud")` so the sidecar boots into Nova's UI instead of `@dragonglass/stock`. Dragonglass's `SidecarBootstrap` yields one frame between `Awake` and the actual sidecar spawn, so any `Startup.Instantly` addon can register before `--entry=` is frozen.

Nova's bundle externalizes `svelte`, `three`, `@threlte/core`, and every `@dragonglass/*` specifier — they resolve at runtime through Dragonglass's importmap to the same emitted runtime, giving Nova a shared Svelte runtime instance with stock and any other UI mod (per `~/dev/dragonglass/docs/mod-ui.md`). Don't bundle Svelte; doing so would break that sharing. The npm-workspace `external/dragonglass` symlink is used at build time *only* for typechecking and editor goto-definition — bundle output never includes Dragonglass code.

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

## save-cli (Rust)

`crates/save-cli/` produces the `nova-save-cli` binary — a stand-alone inspector for the binary `.hgs` / `.hgc` files Nova writes. It auto-detects file type from the HGS magic-byte header and prints the decoded proto tree to stdout.

```
just save-cli-build
just save-cli -- dump path/to/some.hgs
```

The build script (`crates/save-cli/build.rs`) compiles `proto/nova.proto` via `prost-build`, so the Rust types stay in lockstep with the C# bindings — `proto/nova.proto` is the single source of truth for both. When you add a field to the proto, both `just proto` (C# regen) and `cargo build` (Rust regen) need to run.

## KSP API reference

`~/dev/ksp-reference/` contains the decompiled stock KSP code. Use it to look up signatures and behavior; never modify.

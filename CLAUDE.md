# CLAUDE.md

Guidance for Claude Code when working in the Nova repo.

## What this is

Nova is a KSP 1.x core mod that overhauls resource and vessel simulation to support true background multi-mission operations. Targets .NET Framework 4.8 (KSP 1.12.5's bundled Mono).

Nova was previously named "Hypergolic Systems" — HGS now refers to the umbrella mod-family brand of which Nova is a member. Sibling mods under HGS:
- **Dragonglass** (`~/dev/dragonglass`) — CEF + web UI for KSP. Owns all custom UI; Nova does not ship UI plumbing.
- **kspcli** (`~/dev/hgs/kspcli`) — agentic bridge: protobuf wire format, Rust CLI + KSPAddon listener. Replaces the old HGS Bridge/BridgeCLI.

## What Nova replaces

Nova is a **core mod**. It replaces — not augments — stock KSP's simulation, persistence, and UI surfaces. When reaching for a stock idiom, first check that the corresponding Nova surface isn't the canonical one. The big ones:

- **Persistence — proto, not ConfigNode.** Nova writes its own `.nvs` (save) and `.nvc` (craft) files defined by `proto/nova.proto`. Stock KSP's `ConfigNode` save tree is bypassed. `[KSPField(isPersistant = true)]` on a `NovaXxxModule` field writes into a tree Nova never reads — it's dead weight for simulation state. Mutable runtime state lives on the `VirtualComponent`, persists via `Save(PartState)` / `Load(PartState)` into a per-component `XxxState` proto message (`Nova.Core/Components/Thermal/Radiator.cs` is the canonical example). Reserve `isPersistant = true` for the narrow cases where KSP *itself* still reads the value (e.g. an animation flag stock code paths reference).

- **UI — Dragonglass + topics, not PAW.** Player-facing controls do not use stock's Part Action Window. That means no `UI_Toggle`, no `UI_FloatRange`, no `[KSPEvent]` buttons, no `[KSPField]` with `guiActive*` / `guiName` for player-editable values. Per-part state is published via `Nova.Telemetry.NovaPartTopic` (positional JSON frames, single-char kind prefix); player actions come back through the same topic's `HandleOp` dispatcher (`ksp.send(NovaPartTopic(id), 'setXxx', args)` from Svelte). UI lives in `ui/apps/nova/`, mounted by `NovaUiOverrideAddon` overriding Dragonglass's boot specifier.

- **Resource flow, attitude, RCS, ΔV, staging.** Nova's solvers in `Nova.Core/Systems/` and `Nova.Core/Flight/` own these. Stock `ResourceManager`, `ModuleReactionWheel`'s built-in EC draw, the stock RCS solver, stock ΔV display — all replaced. See the Architecture section below.

A practical heuristic: if a sentence about Nova starts with "the stock KSP way is…", the answer is almost always "Nova doesn't go through that path." Trace to the Nova surface (`NovaPartTopic`, the relevant `VirtualComponent`, the proto message) before suggesting a stock-idiom solution.

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
  save-cli/             # nova-save-cli — inspector for .nvs / .nvc files
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

The `.nvc` (craft) and `.nvs` (save) file formats are defined by `proto/nova.proto`. `just proto` regenerates `mod/Nova.Core/Persistence/Protos/Generated/nova.cs` via the `protobuf-net.protogen` dotnet tool (versioned in `.config/dotnet-tools.json`). Generated files are gitignored.

Conventions:
- **Don't edit the generated `.cs`** — regenerate from the proto.
- **No backwards compatibility.** Old proto bytes, old `.nvc`/`.nvs` files, old sidecar instances are all fair game to break. Field numbers can be reused freely; retired fields don't need to be `reserved`. Active-development mod, single user — re-saves and reinstalls are cheap, fallback/migration code in loaders is permanent overhead. If a schema change breaks an existing save, the right answer is "rebuild the craft," not a defensive load.
- **Generated property names** follow protogen's pluralization (`repeated Kerbal crew` → `Crews`) and casing (`launch_id` → `LaunchId`). When a field name surprises you, check the generated file.
- **Repeated message types** become get-only `List<T>` (use collection-initializer `Foo = { … }` or `.AddRange(…)`); **repeated primitive types** become `T[]` (writable).

Per-component state flow (when adding a new mutable field that needs to survive save/load):
1. Add a field to the appropriate `XxxState` message in `proto/nova.proto` (or add a new state message and reference it from `PartState`).
2. `just proto` (regenerates the C# bindings; Rust regenerates on next `cargo build`).
3. Override `Save(PartState)` / `Load(PartState)` on the `VirtualComponent` to round-trip the field. The Radiator (`Nova.Core/Components/Thermal/Radiator.cs`) and ReactionWheel (`Nova.Core/Components/Propulsion/ReactionWheel.cs`) are the templates.
4. The `NovaXxxModule` PartModule reads/writes the field through its `Components` reference, not through a `KSPField`. KSP's `ConfigNode` save path is not in the loop.

### Telemetry & UI ops (`Nova.Telemetry/NovaPartTopic.cs`)

Player-facing controls flow through the topic, not the PAW. Two halves:

- **Outbound (mod → UI):** every part publishes a `NovaPart/<persistentId>` topic. The wire is a positional `[partId, [resourceFrames], [componentFrames]]` JSON array. Each component kind has a single-char prefix (`"S"` solar, `"B"` battery, `"W"` reaction wheel, `"L"` light, `"T"` tank, `"F"` fuel cell, `"C"` command, `"P"` probe, `"R"` rtg, `"X"` radiator…) and a fixed positional tuple — see the file header for the catalogue. New kind = new case in `TryWriteComponent` + matching tuple in `ui/.../nova-topics.ts`. Numbers are physical observables (watts, fractions, absolutes); never expose solver-internal `Activity`.
- **Inbound (UI → mod):** `HandleOp(op, args)` dispatches `ksp.send(NovaPartTopic(id), 'setXxx', value)` calls from the UI. Op names live alongside the wire-frame docs in the file header. Inside the handler: look up the relevant `NovaPartModule` on `_part`, validate, mutate the virtual component, `MarkDirty()`. Editor-only ops gate on `HighLogic.LoadedScene == GameScenes.EDITOR`. See `setRadiatorDeployed`, `setTankCustom` for the shape.

When wiring a new player control, the answer is *always* a topic op + a Svelte view. Reach for `[UI_Toggle]` / `[KSPEvent]` / `guiActiveEditor` only when targeting a non-player audience (e.g. debug-only fields).

### Configs (`configs/overrides/`)

ModuleManager patches that strip stock modules and inject Nova's. Module names match C# class names (e.g., `name = NovaBatteryModule`). When renaming a `NovaXxxModule`, update the matching configs.

## Key dependencies

- **Google.OrTools 9.8.3296** + `osx-x64` native runtime — LP solver. The native `.dylib` ships in `GameData/Nova/` (justfile dist recipe).
- **Lib.Harmony 2.2.2** — runtime KSP patching. Bundled with `Nova.dll`.
- **protobuf-net 2.4.8** — binary serialization for `.nvs`/`.nvc` files. Bindings generated from `proto/nova.proto` by `protobuf-net.protogen`.
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

## Headless simulator

`mod/Nova.Sim/` builds a console binary that hosts `Nova.Core` outside KSP. It loads a `.nvc` / `.nvs` proto file, runs the same `VirtualVessel` tick loop the in-game `NovaVesselModule` drives, and exposes Dragonglass-compatible telemetry over WebSocket plus kspcli-style C# eval over UDP. The Vite UI dev server can talk to it byte-for-byte the same as it talks to in-game Dragonglass — so UI iteration drops from "rebuild + restart KSP + load save" minutes to "vite HMR" seconds, and component / solver changes show up in the wire without round-tripping through the game.

```
just sim-build
just sim-run -- --ksp-path ~/KSP_osx --save ~/KSP_osx/saves/default/persistent.nvs
# ws://0.0.0.0:9887 (telemetry), udp://127.0.0.1:9886 (eval)
```

Defaults are picked to coexist with a running KSP — `8787` (Dragonglass in-game WS) and `9876` (kspcli UDP) are both untouched. See the [`nova-simulator`](.claude/skills/nova-simulator/SKILL.md) skill for the full reference: flags, topic-emission catalogue, UDP eval handles, UI dev workflow, what's *not* simulated (no flight integrator, no atmosphere model, no in-game UI ops yet).

## save-cli (Rust)

`crates/save-cli/` produces the `nova-save-cli` binary — a stand-alone inspector for the binary `.nvs` / `.nvc` files Nova writes. It auto-detects file type from the HGS magic-byte header and prints the decoded proto tree to stdout.

```
just save-cli-build
just save-cli -- dump path/to/some.nvs
```

The build script (`crates/save-cli/build.rs`) compiles `proto/nova.proto` via `prost-build`, so the Rust types stay in lockstep with the C# bindings — `proto/nova.proto` is the single source of truth for both. When you add a field to the proto, both `just proto` (C# regen) and `cargo build` (Rust regen) need to run.

## KSP API reference

`~/dev/ksp-reference/` contains the decompiled stock KSP code. Use it to look up signatures and behavior; never modify.

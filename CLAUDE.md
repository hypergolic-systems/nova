# CLAUDE.md

Guidance for Claude Code when working in the Nova repo.

## What this is

Nova is a KSP 1.x core mod that overhauls resource and vessel simulation to support true background multi-mission operations. KSP-side glue targets .NET Framework 4.8 (KSP 1.12.5's bundled Mono); the simulation engine itself is a Rust workspace.

**Architecture in one breath:** the simulator lives in Rust (`crates/nova-sim`). KSP loads a small C# integration layer (`mod/Nova/`) that calls into the Rust engine each physics frame through a `cdylib` FFI bridge (`crates/nova-ksp` → `libnova_ksp.dylib`). The legacy all-C# simulator (`mod/Nova.Core/_legacy/`) is retired and kept only as porting reference — don't extend it.

Nova was previously named "Hypergolic Systems" — HGS now refers to the umbrella mod-family brand of which Nova is a member. Sibling mods under HGS:
- **Dragonglass** (`~/dev/dragonglass`) — CEF + web UI for KSP. Owns all custom UI; Nova does not ship UI plumbing. Also owns the WebSocket telemetry broadcaster; Nova publishes its topics *through* it (see Telemetry below).
- **kspcli** (`~/dev/hgs/kspcli`) — agentic bridge: protobuf wire format, Rust CLI + KSPAddon listener. Replaces the old HGS Bridge/BridgeCLI.

## Build & run

```
just proto                      # regenerate C# proto bindings from proto/nova.proto
just sync-dragonglass-stubs     # vendor Dragonglass DLLs into stubs/dragonglass/
just nova-ksp-build             # cross-compile libnova_ksp.dylib (x86_64) + csbindgen → mod/Nova.Ffi.Generated/
just mod-build                  # build everything (proto + nova-ksp-build + Nova.sln)
just test                       # MSTest + cargo workspace tests (save-cli, nova-sim, nova-ksp)
just sim-test                   # cargo test -p nova-sim (fast inner loop on the engine)
just nova-ksp-test              # cargo test -p nova-ksp (FFI surface)
just ui-bootstrap               # symlink Dragonglass + npm install ui/
just ui-build                   # typecheck + Vite library build → ui/apps/nova/dist/hud.js
just dist                       # produce release/Nova.zip (mod + dylib + UI)
just install ~/KSP_osx          # build + install into a KSP directory
```

`mod-build` depends on `proto` and `nova-ksp-build`, so a fresh checkout just needs `just mod-build`. Anything that crosses into Dragonglass — `sync-dragonglass-stubs`, `ui-bootstrap`, and the targets that depend on them — requires `$DRAGONGLASS_PATH` set (e.g. `export DRAGONGLASS_PATH=~/dev/dragonglass`).

**Why the x86_64 cross-compile:** KSP 1.12.5 on macOS is an x86_64 binary running its bundled Mono under Rosetta 2. An arm64 `.dylib` is silently rejected at `DllImport` resolution time, so `nova-ksp-build` always passes `--target x86_64-apple-darwin`. The `dist` recipe copies the dylib from `target/x86_64-apple-darwin/release/` (with `.so`/`.dll` fallbacks for future cross-platform builds) into `GameData/Nova/` *next to* `Nova.dll` — Mono's `DllImport` searches alongside the managed assembly, not in a `Plugins/` subdir.

### Cross-repo commit ordering

CI checks out Dragonglass at `main` (sibling-checkout convention extended to GitHub Actions; see `.github/workflows/ci.yml`). When a Nova change depends on a Dragonglass change — new API surface, refactored types, importmap entries, anything the C# `<Reference>` or the UI typecheck/bundle pulls (e.g. `Json.WriteRaw`) — the Dragonglass change must be pushed to `main` *before* Nova's CI run. Otherwise the sibling checkout pulls a Dragonglass tree without the new code and Nova's build fails. The intended workflow:

1. Land the Dragonglass-side change on Dragonglass `main`.
2. Push the matching Nova change.

If the Dragonglass change isn't ready to ship yet, hold the Nova-side commit until it is — both repos co-evolve in lockstep on `main`. There's no per-PR ref override yet; if you need one, plumb a `dragonglass-ref` workflow input and a matching `with: ref:` on the sibling `actions/checkout`.

## Project layout

```
crates/                  # Cargo workspace (members = crates/*)
  nova-sim/              # the simulation engine — pure safe Rust, no FFI, no KSP/Unity
  nova-highs/            # safe wrapper around the HiGHS LP solver (C→Rust FFI quarantined here)
  nova-ksp/              # cdylib: extern "C" bridge between KSP (C# Nova.dll) and nova-sim.
                         #   build.rs runs csbindgen → mod/Nova.Ffi.Generated/*.cs
  nova-telemetry/        # topic registry + per-topic snapshot buffers + JSON serializers.
                         #   reads &World; never extern "C"
  save-cli/              # nova-save-cli — inspector for .nvs / .nvc files (prost-decoded proto tree)
mod/
  Nova.sln
  Nova.Core/            # now a thin shared lib: proto types, Resource enum, Vec3d/Curve.
    _legacy/            #   the retired all-C# simulator (VirtualVessel, the two C# solvers,
                        #   component classes, DeltaVSimulation, RCS, …). Excluded from the
                        #   build (`<Compile Remove="_legacy/**/*.cs" />`). Porting reference only.
  Nova/                 # KSP integration — KSPAddon, Harmony, PartModules, save/load builders.
    _legacy/            #   the C# telemetry topics + behaviors that depended on the old simulator
  Nova.Ffi.Generated/   # csbindgen output (gitignored .cs; produced by just nova-ksp-build)
  Nova.Tests/           # MSTest — references Nova.Core only (thin now; most engine tests are in crates/)
ui/                     # Svelte UI app, deployed to GameData/Nova/UI/
  apps/nova/            # @nova/app — Vite library build, single entry hud.ts
  packages/             # @nova/* shared libs (placeholder)
  external/dragonglass  # symlink → $DRAGONGLASS_PATH (gitignored)
proto/nova.proto        # persistence + FFI schema — source of truth for C# (protobuf-net) and Rust (prost)
configs/overrides/      # ModuleManager .cfg patches → GameData/Nova/Patches/
stubs/                  # KSP/Unity managed DLLs (KSP 1.12.5)
  dragonglass/          # vendored Dragonglass.{Hud,Telemetry}.dll (gitignored; just sync-dragonglass-stubs)
docs/lp_hygiene.md      # LP-envelope notes for ProcessFlowSystem
justfile
```

Shipped to `GameData/Nova/` by `just dist`:

- **`libnova_ksp.dylib`** — the Rust simulator + FFI surface. The actual engine.
- **`Nova.Ffi.Generated.dll`** — csbindgen-emitted `[DllImport]` declarations + `[StructLayout]` mirror structs. Must ship — without it Nova's type loading cascades into a `TypeLoadException` and Harmony never applies.
- **`Nova.dll`** — KSP integration layer. KSPAddon, Harmony patches, `PartModule`/`VesselModule` subclasses, save/load builders, the telemetry proxy. References `Nova.Core` + `Nova.Ffi.Generated`. Bundles `0Harmony.dll`.
- **`Nova.Core.dll`** — thin shared library: the protobuf-net proto types, the `Resource` enum, `Vec3d`/`Curve`. Bundles `protobuf-net.dll`. (Historically this was the whole simulation engine; that code now lives in Rust under `crates/nova-sim`, and the C# original is in `mod/Nova.Core/_legacy/`.)

## Architecture

### The simulator — `crates/nova-sim` (Rust)

Pure safe Rust, no FFI, no KSP/Unity dependencies. Authoring scenarios in tests goes through `World` — see `crates/nova-sim/tests/` and the `fixtures` module for stock-Kerbol scaffolding.

- **`World`** — owns `vessels: Vec<Vessel>`, the `Ephemeris` (Keplerian body tree), and the world-level `CommsSystem`. `World::tick(target_ut)` advances every vessel + the comms graph, interleaved at event boundaries.
- **`Vessel`** — `id: VesselId(u32)` (KSP's `persistentId`), `guid: String` (KSP's `Vessel.id` GUID — Rust *owns* this; see GUIDs below), `name`, `situation: Situation` (`Abstract` for editor/pre-launch | `Orbit { parent, orbit }`), `parts: Vec<Part>`, and an optional `VesselSystems` populated by `initialize_solver`.
- **`Part`** — `id: u32` (persistentId), `name` (KSP internal name), `display_title` (KSP `AvailablePart.title`, supplied by C#), `dry_mass_kg`, `parent: Option<u32>`, `components: Vec<Component>`.
- **`Component`** — closed enum: `Engine`, `TankVolume`, `Battery`, `Command`, `Comms`, `FuelCell`, `SolarPanel`. Each variant owns its runtime state + lifecycle (`on_build_systems` / `on_pre_solve` / `on_post_solve` / `valid_until`). Adding a port = one variant + a match arm in each dispatch in `components/mod.rs`. (ReactionWheel, Light, DataStorage, science instruments, RCS — not yet ported; they live in `mod/Nova.Core/_legacy/`.)

#### Resource flow — `crates/nova-sim/src/systems/`

Two specialised solvers, partitioned by resource domain. `VesselSystems` is the per-vessel container (holds both solvers + a shared `SimClock`); `Vessel::tick` is the runner, with `needs_solve` invalidation so re-solves only fire on state change (initial build, forecasted event firing, external mutation).

- **`StagingFlowSystem`** — water-fill for **Topological** resources (RP-1, LOX, LH₂, Hydrazine, Xenon). Owns the vessel topology graph (nodes, edges with allowed/up-only resource filters, drain priorities). Per Solve, per (DrainPriority, resource, connected component): drain pools proportionally to current Contents, clipped per-pool by `MaxRateOut`, recurse on the binding pool. Pure arithmetic — no LP, no degeneracy.
- **`ProcessFlowSystem`** — slim LP (HiGHS, via `nova-highs`) for **Uniform** resources (ElectricCharge today; O₂ / CO₂ / H₂O / heat tomorrow). Single vessel-wide pool per resource, no topology. Device-priority loop (Critical → High → Low) with `max α + ε·Σ activity`, then a lex-2 cleanup pass that minimises Σ supply + Σ fill to suppress LP-cycling artefacts. For the LP envelope, see `docs/lp_hygiene.md`.

`Buffer` stores `(baseline_contents, baseline_ut, rate)` and computes `Contents` lazily against the shared `SimClock` — simulation cost scales with rate-change events, not physics ticks.

### FFI bridge — `crates/nova-ksp` (cdylib)

`nova-ksp` is the *only* crate that touches `extern "C"`, `#[repr(C)]`, prost, csbindgen, or unsafe pointer arithmetic. `nova-sim` stays idiomatic and FFI-free.

- **`NovaWorld`** — opaque handle returned to C#. Owns the `nova-sim::World`, the per-vessel `FfiVessel` map (mirror arenas), the prefab `PartDatabase` (`name → NovaPart` proto, pushed at startup via `nova_world_set_part_database`), and the telemetry `TopicRegistry`. `nova_world_create` / `nova_world_destroy` bracket its lifetime.
- **Per-vessel mirror arena.** Each `nova_vessel_new` allocates one `Box<[u8]>` sized to fit one `#[repr(C)]` state struct per (part, component-kind) pair (`arena.rs` builds the slot manifest). After `nova_world_tick`, `FfiVessel::write_outputs` mirrors canonical nova-sim state into the arena. Pointers into the arena are stable for the vessel's lifetime. C# reads via `NovaVesselHandle.GetState<T>(partFlightId)` → `ref T` directly into arena memory (`Unsafe.AsRef`), no copies.
- **`#[repr(C)]` state structs** (`state/`, PascalCase fields so csbindgen emits idiomatic C#). Bidirectional by convention — Rust-owned output fields, C#-owned input fields (e.g. throttle, not yet exercised). `BatteryState`, `CommandState` today.
- **Proto-driven vessel creation.** C# serialises `Proto.VesselStructure` + `Proto.VesselState` (the same protos `.nvs` saves use) and hands the bytes to `nova_vessel_new`. nova-ksp decodes via prost, walks the part tree, consults the prefab `PartDatabase` to decide which arena slots to allocate, runs `initialize_solver`, and returns a `VesselHandle` (arena pointers + slot manifest + the assigned GUID).
- **csbindgen.** `build.rs` runs csbindgen against the `extern "C"` items + every `#[repr(C)]` struct it's told about, emitting `mod/Nova.Ffi.Generated/NovaNative.g.cs` (`[DllImport]` bindings + `[StructLayout]` structs). Generated files are gitignored; `mod-build` regenerates them. When you add a new extern fn or state struct, list its source file in `build.rs`'s csbindgen input list.

Tick loop: `NovaVesselModule.FixedUpdate` → `NovaWorldAddon.Tick(now)` → `nova_world_tick` (advance the sim, mirror arenas, refresh subscribed telemetry topics).

### Telemetry — `crates/nova-telemetry` + `mod/Nova/Telemetry/`

The simulator owns its wire formats. `nova-telemetry` is a pure-Rust crate: a `TopicRegistry` keyed by wire-level topic name, with one stable per-topic buffer each (`[version: u64 | len: u32 | cap: u32 | payload]`, 8 KB). `nova_world_tick` calls `TopicRegistry::refresh(&world)`, which serialises every subscribed topic into a scratch `Vec<u8>`, diffs against the live payload, and only swaps + bumps `version` on change.

`nova-ksp` exposes `nova_topic_subscribe(world, name) -> *const u8` (returns the buffer pointer, valid until `nova_topic_unsubscribe` drops the refcount to 0) and `nova_topic_unsubscribe`.

On the C# side (`mod/Nova/Telemetry/`):
- `NovaTopicProxy` — a MonoBehaviour on Dragonglass's telemetry host GameObject. Listens to `Dragonglass.Telemetry.SubscriptionBus`; for any `nova/*` subscribe it routes (by registered prefix) to a concrete `NovaProxiedTopicBase` subclass.
- `NovaProxiedTopicBase` (+ concrete per-key-type subclasses `NovaPartProxiedTopic` / `NovaVesselStructureProxiedTopic` — Unity rejects generic MonoBehaviours, so each wire-key type gets a non-generic class) — a `Dragonglass.Telemetry.Topic` that subscribes via FFI, stashes the buffer `byte*`, polls the version word in `Update()` (two pointer derefs — no per-frame FFI, no allocs), marks the broadcaster dirty on change, and splices the payload via `Json.WriteRaw` in `WriteData`.
- `NovaTopicFamilies` — the one C# file that knows wire-name shapes (`nova/part/{persistentId}` → uint, `nova/vessel-structure/{guid}` → Guid). Adding a topic family = a Rust serializer + a prefix branch in `nova-telemetry::topics::serialize` + one `RegisterFamily` call + one (mostly-empty) subclass. Nothing else in C# touches the bytes.

Wire formats live in `crates/nova-telemetry/src/topics/`. `nova/part/{id}` emits `[partId, [componentFrames]]` where each frame is a single-char-prefixed positional array (`B` Battery, `C` Command, `F` FuelCell, `S` SolarPanel) that self-describes its buffer state — there is no separate "resources" slot. `nova/vessel-structure/{guid}` emits `[guid, name, [[partId, name, title, parentId, [kindChars]]]]` — no wire-level tag taxonomy; views filter parts by which component kinds they care about. The legacy C# topics (Engine, Part, Science, Storage, Comms, Orbit, …) are in `mod/Nova/_legacy/Telemetry/` and migrate one family at a time as their components port to nova-sim.

### Vessel spawning — Rust-first

KSP only ever creates a `Vessel` that the simulator already knows about. The flow:
1. Player launches a craft / a save loads → `NovaCraftLoader` / `NovaSaveLoader` builds the canonical proto pair, pre-generates part `persistentId`s, and calls `NovaWorldAddon.RegisterVessel(...)` — Rust creates the `nova-sim::Vessel` first.
2. KSP then instantiates the `Vessel` GameObject with the *same* `persistentId` (stock `ShipConstruction.cs:470: vessel.persistentId = ship.persistentId`).
3. `NovaVesselModule.OnLoadVessel` looks up its Rust handle by `persistentId`. No handle = a bug in the spawn path (it errors loudly); there is no C#-builds-a-vessel fallback.

`NovaVesselModule` is thin: handle lookup, periodic `nova_world_tick`, one-shot orbit push (`Abstract` → `Orbit` once `orbitDriver` is wired), and a periodic `[FfiState]` debug log line.

### Vessel GUIDs — Rust-owned

`Vessel.id` (the KSP GUID) is assigned by the simulator, not KSP. `nova_vessel_new` mints a fresh v4 UUID when the proto carries none (VAB launches), or uses the persisted one (save loads). The `VesselHandle` exposes it; `NovaVesselHandle.Guid` decodes it once; and `Patches/VesselGuidPatch.cs` — a Harmony postfix on the internal 13-arg `ShipConstruction.AssembleForLaunch` overload — overrides KSP's `Guid.NewGuid()` with the simulator's value, so KSP, the simulator, and the UI's `flight.vesselId` all agree on identity. (`AssembleForLaunch` has two overloads; the patch must specify the full arg-type list or Harmony throws "ambiguous match" and the patch class — and everything after it — fails to apply.)

### KSP integration (`mod/Nova/`)

- `NovaMod.cs` — KSPAddon entry point, singleton.
- `HarmonyPatcher.cs` — applies all patches at startup. Some patches (internal nested types like `LoadGameDialog.PlayerProfileInfo`, `VesselSpawnDialog.VesselDataItem`) are applied manually via `AccessTools.Inner` + `harmony.Patch` because `[HarmonyPatch]` attributes can't reach them.
- `Ffi/NovaWorldAddon.cs` — owns the `NovaWorld*` for the session (KSPAddon.Startup.Instantly + once). `RegisterVessel`, `LookupHandle`, `Tick`, `SetVesselOrbit`, `SubscribeTopic`/`UnsubscribeTopic`, `SetPartDatabase`.
- `Ffi/NovaVesselHandle.cs` — C# wrapper over a `VesselHandle`: slot lookup table + `GetState<T>(partFlightId)` → `ref T` into the arena + the assigned `Guid`.
- `Components/NovaXxxModule.cs` — KSP `PartModule`s. They read their state via `vesselModule.Handle.GetState<XxxState>(part.flightID)` — no per-module simulation logic.
- `Components/NovaVesselModule.cs` — see "Vessel spawning" above.
- `Telemetry/` — see "Telemetry" above.
- `Patches/` — Harmony patches on `ShipConstruction`, save/load, asteroid/comet spawning (killed entirely — nothing spawns that the simulator doesn't drive), etc.
- `Persistence/` — `NovaCraftLoader`, `NovaSaveLoader`, `NovaVesselBuilder` (builds the proto pair from live KSP parts; `BuildPartDatabase` from `PartLoader`).

### Patching strategy

KSP 1.x is dead; Squad will never ship another patch. The decompiled source in `~/dev/ksp-reference/source/Assembly-CSharp/` is frozen.

- **Transpilers are fair game** — no upstream drift.
- **Reflection against private members is safe** — names won't change.
- **Copy-and-modify replacements are viable** — when stock logic mixes desired and undesired behavior.
- **Still prefer the lightest patch that works** — prefix/postfix when adding behavior, transpiler when tweaking, full replacement when shape conflicts. When a method has overloads, give `[HarmonyPatch]` the full argument-type array or Harmony's "ambiguous match" exception aborts the whole patch class.

### Persistence wire format (`proto/nova.proto`)

`proto/nova.proto` is the single source of truth for: the `.nvc`/`.nvs` file formats, the C#↔Rust vessel-creation payloads (`VesselStructure` / `VesselState` / `PartDatabase`), and the `nova-save-cli` decoder. `just proto` regenerates `mod/Nova.Core/Persistence/Protos/Generated/nova.cs` via `protobuf-net.protogen` (versioned in `.config/dotnet-tools.json`); `cargo build` regenerates the Rust side via `prost-build` in `nova-ksp/build.rs` and `save-cli/build.rs`. Generated files are gitignored. **When you add a proto field, both `just proto` (C# regen) and `cargo build` (Rust regen) need to run.**

Conventions:
- **Don't edit the generated `.cs`** — regenerate from the proto.
- **No backwards compatibility.** Old proto bytes, old `.nvc`/`.nvs` files, old sidecar instances are all fair game to break. Field numbers can be reused freely; retired fields don't need to be `reserved`. Active-development mod, single user — re-saves and reinstalls are cheap; fallback/migration code in loaders is permanent overhead. If a schema change breaks an existing save, the right answer is "rebuild the craft", not a defensive load.
- **Generated property names** follow protogen's pluralization (`repeated Kerbal crew` → `Crews`) and casing (`launch_id` → `LaunchId`). When a field name surprises you, check the generated file.
- **Repeated message types** become get-only `List<T>` (use collection-initializer `Foo = { … }` or `.AddRange(…)`); **repeated primitive types** become `T[]` (writable).

### Configs (`configs/overrides/`)

ModuleManager patches. Two jobs: strip stock modules whose behavior nova-sim now owns (`!MODULE[ModuleGenerator] {}` on the RTG, etc. — stock modules calling stock `Vessel.RequestResource` NRE against the gutted resource graph), and inject Nova's (`name = NovaBatteryModule`). Module names match the C# `NovaXxxModule` class names — when renaming, update the matching configs.

## Key dependencies

**Rust (`crates/`):**
- **HiGHS** (vendored by `highs-sys`, wrapped by `nova-highs`) — LP solver for `ProcessFlowSystem`. The C→Rust FFI is quarantined inside `nova-highs`.
- **prost / prost-build 0.13** — proto codegen for the Rust side (`nova-ksp`, `save-cli`).
- **csbindgen 1.x** — build-dep of `nova-ksp`; emits the C# `[DllImport]`/`[StructLayout]` bindings.
- **uuid 1.x** (v4) — vessel GUID minting in `nova-ksp`.

**C# (`mod/`):**
- **Lib.Harmony 2.2.x** — runtime KSP patching. Bundled with `Nova.dll`.
- **protobuf-net 2.4.x** — binary serialization for `.nvs`/`.nvc`. Bindings via `protobuf-net.protogen`. Bundled with `Nova.Core.dll`.
- **System.Runtime.CompilerServices.Unsafe** — `Unsafe.AsRef<T>` for the `ref T`-into-arena reads.
- **Moq** — mocking, tests only.
- **Dragonglass** (`~/dev/dragonglass` via `$DRAGONGLASS_PATH`) — hard runtime dep. `Dragonglass.Telemetry.dll` for `Topic` / `SubscriptionBus` / `Json.WriteRaw`; `Dragonglass.Hud.dll` for `SidecarHost.OverrideEntry`. DLLs vendored into `stubs/dragonglass/` by `just sync-dragonglass-stubs`. Nova does NOT ship them — Dragonglass installs them from its own deploy.

(OR-Tools is gone — it was the LP solver for the retired C# `ProcessFlowSystem`; the Rust port uses HiGHS.)

## UI (Dragonglass integration)

Nova's UI is a Svelte app under `ui/apps/nova/`, built via Vite library mode → `ui/apps/nova/dist/hud.js`, deployed to `GameData/Nova/UI/hud.js` by `just dist`. Dragonglass's CEF sidecar walks `GameData/*/UI/` at startup, exposes `@<modname>/<file>` import-map specifiers, and the synthesized shell imports whichever specifier its `--entry=` arg names.

`NovaUiOverrideAddon` (Startup.Instantly) calls `Dragonglass.Hud.SidecarHost.OverrideEntry("@nova/hud")` so the sidecar boots into Nova's UI instead of `@dragonglass/stock`. Dragonglass's `SidecarBootstrap` yields one frame between `Awake` and the actual sidecar spawn, so any `Startup.Instantly` addon can register before `--entry=` is frozen.

Nova's bundle externalizes `svelte`, `three`, `@threlte/core`, and every `@dragonglass/*` specifier — they resolve at runtime through Dragonglass's importmap to the same emitted runtime, giving Nova a shared Svelte runtime instance with stock and any other UI mod (per `~/dev/dragonglass/docs/mod-ui.md`). Don't bundle Svelte; doing so would break that sharing. The npm-workspace `external/dragonglass` symlink is used at build time *only* for typechecking and editor goto-definition — bundle output never includes Dragonglass code.

**Telemetry consumption.** The UI subscribes to topics via `@dragonglass/telemetry`. Nova-specific topics live in `ui/apps/nova/src/telemetry/`: `nova-topics.ts` (wire tuple types + decoders), `use-nova-parts.svelte.ts` (`useNovaParts(vesselId)` — subscribes to every part on the vessel via `nova/vessel-structure/{guid}` + `nova/part/{id}` and joins struct + state; views filter by `componentKinds`), etc. The in-browser dev simulator (`src/sim/nova-sim.ts`) intercepts the `nova/*` topic names and emits canned frames so PWR/RES/SCI views render without KSP. Other Nova topics (Science, Storage, Orbit, Comms, Timewarp) still use the legacy upper-case wire names + shapes and aren't served yet — their hooks are stubbed empty until those families port to nova-telemetry.

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

`crates/save-cli/` produces the `nova-save-cli` binary — a stand-alone inspector for the binary `.nvs` / `.nvc` files Nova writes. Auto-detects file type from the HGS magic-byte header and prints the prost-decoded proto tree to stdout.

```
just save-cli-build
just save-cli -- dump path/to/some.nvs
```

`crates/save-cli/build.rs` compiles `proto/nova.proto` via `prost-build`, so the Rust types stay in lockstep with the C# bindings.

## KSP API reference

`~/dev/ksp-reference/source/Assembly-CSharp/` contains the decompiled stock KSP code. Use it to look up signatures and behavior; never modify.

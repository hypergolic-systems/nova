---
name: nova-simulator
description: Run Nova.Sim — the headless Nova simulator binary at `mod/Nova.Sim/` — to host Nova.Core outside KSP, load a `.nvc` / `.nvs` file, emit Dragonglass-compatible telemetry over WebSocket, and accept kspcli-style C# expressions over UDP. Use whenever iterating on the UI, debugging component state against a real save, or running solver-level regression scenarios without paying KSP cold-start cost. Build / run via `just sim-build` and `just sim-run`.
---

# Nova.Sim

`Nova.Sim` is a `net48` console binary that hosts `Nova.Core` outside KSP. It loads a craft (`.nvc`) or full save (`.nvs`), runs the same `VirtualVessel` tick loop the in-game `NovaVesselModule` uses, and exposes:

- **WebSocket telemetry** on `ws://0.0.0.0:9887` (default) — same wire as Dragonglass's in-game broadcaster, so the Vite UI dev server speaks to it unmodified.
- **UDP eval** on `udp://127.0.0.1:9877` (default) — same kspcli expression language vendored into `Nova.Sim/Eval/ExpressionEvaluator.cs`, with pre-registered refs `$0` = `SimRunner`, `$1` = `PartDatabase`.

Source: `mod/Nova.Sim/`. Builds out-of-process; never link from `Nova.dll`.

## When to reach for it

- **UI iteration**: `just sim-run` + `just ui-dev` + browser at `http://localhost:5173/?ws=ws://localhost:9887` → Svelte HMR against real solver state. Round-trip is seconds, not minutes.
- **Save-file regression**: load a known `.nvs`, tick N seconds, eval a buffer level — reproducible across runs.
- **Component debugging outside KSP**: tweak a `Nova.Core` field, see the effect on the live wire without redeploying to KSP.

Don't reach for it when:
- You need KSP flight integrator (real vessel position / velocity / orientation, atmospheric drag, aerodynamics). The sim parks at identity orientation and zero velocity.
- You're testing in-game UI ops (`setSolarDeployed`, `setTankCustom`, …). Those handlers live mod-side and don't yet have a sim implementation.
- Pure `Nova.Core` unit testing — use `mod/Nova.Tests/` instead (no part DB, no WS, no UI).

## Prereqs

- KSP installed at `~/KSP_osx` (or wherever, via `--ksp-path`). The sim walks `<ksp>/GameData/*/Parts/` for stock part configs and `<ksp>/GameData/Squad/Localization/dictionary.cfg` for `#autoLOC_*` resolution.
- For Nova-specific part data, `configs/overrides/**/*.cfg` from this repo gets layered on top (a minimal `@PART[name]` / `!MODULE[]` / `!RESOURCE[]` applier — Nova doesn't use the full ModuleManager surface). Resolved at startup.
- A `.nvc` or `.nvs` to load. Inspect with `just save-cli dump <path>` first to check what's in it.

## Build & run

```
just sim-build                      # → mod/Nova.Sim/build/Nova.Sim.exe
just sim-run -- --ksp-path ~/KSP_osx \
                 --save ~/KSP_osx/saves/default/persistent.nvs
# or:
just sim-run -- --ksp-path ~/KSP_osx \
                 --craft ~/KSP_osx/saves/default/Ships/VAB/MyShip.nvc
```

Flags:
- `--ksp-path <dir>` — required. Reads stock parts + localization from here.
- `--craft <path>` / `--save <path>` — required, mutually exclusive. Save's active vessel becomes the simulated one.
- `--ws-port <n>` — default `9887`. WebSocket telemetry server.
- `--udp-port <n>` — default `9877`. UDP eval server.
- `--warp <factor>` — default `1.0`. Sim UT advances by `wall_dt × warp` per tick.

Startup logs the part-DB build (`358 parts (stock 358, patched 159, deleted 0)`), the loaded vessel (`vessel 'Tanks I' (<guid>) at UT=...`), and both listener addresses. If you see `save file contains no vessels`, the save legitimately has none — use a different save or build a craft in KSP first.

## Port collisions

Defaults are picked to coexist with a running KSP on the same machine:
- Dragonglass's in-game WS broadcaster: `ws://localhost:8787` (see `TelemetryAddon.Port` in `~/dev/dragonglass/mod/Dragonglass.Telemetry/src/TelemetryAddon.cs`).
- kspcli's in-game listener: `udp://localhost:9876` (see `KSPCLI_PORT` env in the [kspcli](./kspcli) skill).
- Nova.Sim's WS: `9887` — distinct from DG's `8787`.
- Nova.Sim's UDP: `9877` — distinct from kspcli's `9876`.

Override with `--ws-port` / `--udp-port` if you're running multiple sim instances on the same host.

## UI development against the sim

In two terminals:

```
# 1. sim
just sim-run -- --ksp-path ~/KSP_osx --save <path>.nvs

# 2. vite dev server (http://localhost:5173)
just ui-dev
```

Then open `http://localhost:5173/?ws=ws://localhost:9887`. Dragonglass's `getKsp()` (`ui/packages/telemetry/src/svelte/context.ts`) auto-bootstraps a `DragonglassTelemetry(url)` from the `?ws=` query param. The Nova dev entry (`ui/apps/nova/src/dev.ts`) only installs `NovaSimulatedKsp` (the JS fixture) when `?ws=` is absent.

Vite HMR works — edit a Svelte component, save, the browser reloads against live sim state. The sim itself keeps running; only the page reconnects.

## UDP eval

The sim speaks kspcli's wire format byte-for-byte, so the `kspcli` CLI drives it directly — point it at port `9877` instead of `9876`:

```
KSPCLI_PORT=9877 kspcli '$0.Vessel.AllPartIds().Count()'
# → $5 = 4 :: Int32

KSPCLI_PORT=9877 kspcli '$0.SimUt'
# → $6 = 21656.49 :: Double
```

Pre-registered handles:
- `$0` — `Nova.Sim.Runtime.SimRunner` (the active runner). Reach `.Vessel`, `.Context`, `.SimUt`, `.MissionTime`, `.LaunchTime`, `.WarpFactor`, `.VesselName`, `.VesselGuid`.
- `$1` — `Nova.Sim.Config.PartDatabase`. `.Get(partName) → ConfigNode`, `.Count`, `.Names`.

Subsequent `$N` references work the same as kspcli — each eval result is stored and back-referenced. The expression language and built-in LINQ operators are identical to kspcli's (the evaluator is vendored from `~/dev/hgs/kspcli/mod/Eval/ExpressionEvaluator.cs`); see the [kspcli](./kspcli) skill for the full reference.

Raw-wire form (UTF-8 text, single datagram): request `<id>\n<expr>`, reply `<id>\n+\n<body>` on success or `<id>\n-\n<error>` on failure. The id is an opaque correlator the client uses to drop stale replies; `kspcli` generates it and matches it transparently.

Shell-quote `$N` references with single quotes so your shell doesn't substitute them.

## Topics emitted

The sim speaks Dragonglass's wire envelope (`{op:"subscribe",topic:"<name>"}` / `{topic:"<name>",data:<positional-array>}`) for both Nova-owned and DG-owned topic names:

| Topic | Source on sim side |
|---|---|
| `game` | Hardcoded `scene="FLIGHT"` so the Hud router picks FlightHud; activeVesselId from `SimRunner.VesselGuid`. |
| `flight` | Minimum-viable: `vesselId` from runner, `altitude` from `SimVesselContext.Altitude`, everything else zero/identity (no flight integrator). |
| `engines` | One `EngineFrame` per `Engine` component, with `StagingFlowSystem.ReachableBuffers` driving propellant totals. `MapX/MapY = 0` — no engine-map geometry yet. |
| `stage` | Single stage 0 holding all engines + decouplers; Δv / TWR = 0 (no `DeltaVSimulation` against sim-defined stages yet). |
| `NovaScene` / `NovaTimewarp` | Constants. |
| `NovaOrbit/<guid>` | Circular orbit at `SimVesselContext.Altitude` around `BodyName`. ApA = PeA = Altitude. |
| `NovaComms/<guid>` | Hardcoded no-link frame — sim has no comms model; UI renders DARK. |
| `NovaVesselStructure/<guid>` | Walks `VirtualVessel.AllPartIds()`; titles via `PartDatabase` (`#autoLOC_*` resolved). |
| `NovaPart/<id>` / `NovaStorage/<id>` / `NovaScience/<id>` | Sourced from `VirtualVessel.GetComponents(partId)`. Atmospheric temp = 0 in `NovaScience` (no atmosphere model). |
| `NovaScienceArchive` | Bodies from `BodyData.All`; `archive` passed as `null` — formatter renders an empty grid. |

**Not emitted**: `NovaEditorShipStructure` — the sim has no editor scene; the UI silently no-ops on missing frames. All wire format is shared with the in-game emitter via the `Nova.Core.Telemetry/<X>Formatter` classes — the bytes are byte-for-byte identical to what KSP would send for the same `VirtualVessel`.

## Source layout

```
mod/Nova.Sim/
├── Program.cs              — CLI arg parsing + service boot
├── Config/
│   ├── KspConfigParser.cs  — brace-matching .cfg → Nova.Core.Utils.ConfigNode
│   ├── ModuleManagerLite.cs — minimal @PART / !MODULE / !RESOURCE applier
│   ├── PartDatabase.cs     — stock walk + override apply + localization
│   └── Localization.cs     — Squad/Localization/dictionary.cfg loader
├── Universe/
│   ├── BodyData.cs         — hardcoded Kerbol-system catalogue
│   ├── OrbitMath.cs        — Kepler propagator (Newton-Raphson on E)
│   └── SimVesselContext.cs — IVesselContext impl (default: 100km Kerbin)
├── Components/
│   └── SimComponentFactory.cs — mirrors mod/Nova/Components/ComponentFactory.cs
├── Persistence/
│   └── SimVesselLoader.cs  — .nvc/.nvs proto → VirtualVessel.FromExistingParts
├── Runtime/
│   └── SimRunner.cs        — 60 Hz tick loop on a background thread
├── Telemetry/
│   └── SimTelemetryServer.cs — Fleck WS server + topic dispatch
└── Eval/
    ├── ExpressionEvaluator.cs — vendored from kspcli, text-output variant
    └── UdpEvalServer.cs    — raw-UTF8 UDP listener
```

The two-DLL split (Nova.Core vs Nova) still holds — `Nova.Sim` only references `Nova.Core`, never `Nova.dll`. That's why solver semantics are bit-identical between sim and game.

## Related skills

- [kspcli](./kspcli) — for live game state, including the expression language reference shared with the sim's UDP eval.
- [ksp-reference](./ksp-reference) — when you need to look up a stock KSP type the sim's part config or telemetry consumer depends on.

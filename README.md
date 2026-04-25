# Nova

> ⚠️ **Early development.** Nova is pre-release and under active, breaking churn. Only the **macOS / Apple Silicon** path is implemented today — Intel Macs, Linux, and Windows are not yet supported. Expect sharp edges.

Welcome to KSP Nova! This is an ambitious project for KSP 1.x to overhaul the game's core mechanics and unlock its full potential.

Nova starts with a redesign of the core spacecraft simulation engine, with a focus on the spacecraft as a cohesive set of systems. Multi-vessel simulation is a core feature, so the system scales from small suborbital rockets to large space stations or even colonies (potentially).

Nova emphasizes science gathering over time. Missions and experiments build on each other to increase your understanding of the universe, and unlock new capabilities along the way.

Science also drives simulation capabilities, which play a core role in your space program. Initially, exploratory missions are shots in the dark, but as you gather information about the planets, your ability to simulate future missions and validate designs grows. The most ambitious missions should always be pushing the limits and carry that element of risk and excitement.

Supporting these shifts is an overhaul of the game's UI, leveraging [Dragonglass](https://github.com/hypergolic-systems/dragonglass) to replace stock's UI with a from-scratch modern interface.

## Build

Requires `dotnet`, `cargo`, `just`, and a copy of KSP 1.12.5 (for `just install`).

```sh
just mod-build         # build the C# mod (Nova.Core + Nova + Nova.Tests)
just test              # run the test suite
just save-cli-build    # build the Rust save-file inspector
just dist              # produce release/Nova.zip
just install ~/KSP_osx # build + install into a KSP directory
```

## Layout

- `mod/` — C# (Nova.Core, Nova, Nova.Tests).
- `crates/save-cli/` — Rust binary (`nova-save-cli`) for inspecting `.hgs` / `.hgc` files.
- `proto/nova.proto` — persistence schema, single source of truth for both C# and Rust bindings.
- `configs/overrides/` — ModuleManager patches that swap stock modules for Nova's.
- `stubs/` — stripped KSP/Unity managed DLLs used as build references.

## License

MIT — see [LICENSE](LICENSE).

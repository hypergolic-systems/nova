# Nova

A Kerbal Space Program 1.x core mod that overhauls resource and vessel simulation to support persistent, true background multi-mission operations. The resource flow is solved as a linear program (Google OR-Tools GLOP) over a graph of virtual components, so consumption, storage and production stay consistent across the entire game state — not just the active vessel.

Nova is part of the **HGS** mod family. Sibling mods:

- [Dragonglass](../dragonglass) — CEF + web UI for KSP. Owns custom UI delivery.
- [kspcli](../hgs/kspcli) — agentic bridge for scripted KSP introspection.

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

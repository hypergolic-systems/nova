# Nova — top-level build orchestration

set positional-arguments

# Sibling Dragonglass checkout. Required for stub sync, UI build, and
# anything that crosses repo boundaries. Set in your shell rc:
#   export DRAGONGLASS_PATH=~/dev/dragonglass
dragonglass_path := env_var_or_default('DRAGONGLASS_PATH', '')

default:
    @just --list

# --- Cross-repo (~/dev/dragonglass via $DRAGONGLASS_PATH) ---

# Validate $DRAGONGLASS_PATH points at a real Dragonglass checkout.
# Used as a dependency by sync-dragonglass-stubs and ui-bootstrap.
_dragonglass-check:
    #!/usr/bin/env bash
    set -euo pipefail
    if [ -z "{{dragonglass_path}}" ]; then
        echo "error: DRAGONGLASS_PATH not set" >&2
        exit 1
    fi
    if [ ! -d "{{dragonglass_path}}/ui/packages/instruments" ]; then
        echo "error: DRAGONGLASS_PATH={{dragonglass_path}} doesn't look like a Dragonglass checkout" >&2
        exit 1
    fi

# Copy fresh Dragonglass DLLs into stubs/dragonglass/. Run after
# Dragonglass-side changes to refresh Nova's build references.
sync-dragonglass-stubs: _dragonglass-check
    mkdir -p stubs/dragonglass
    cp "{{dragonglass_path}}/mod/Dragonglass.Hud/build/Dragonglass.Hud.dll" stubs/dragonglass/
    cp "{{dragonglass_path}}/mod/Dragonglass.Telemetry/build/Dragonglass.Telemetry.dll" stubs/dragonglass/
    @echo "Synced Dragonglass DLLs from {{dragonglass_path}}"

# --- UI (ui/) ---

# One-time setup: symlink Dragonglass into ui/external/, then npm install
# the workspace. Re-run after switching DRAGONGLASS_PATH.
ui-bootstrap: _dragonglass-check
    mkdir -p ui/external
    ln -sfn "{{dragonglass_path}}" ui/external/dragonglass
    cd ui && npm install

# Vite library-mode build → ui/apps/nova/dist/hud.js (deployed to
# GameData/Nova/UI/ by `just dist`). Typechecks first so build never
# ships a bundle that wouldn't pass CI.
ui-build: ui-bootstrap ui-typecheck
    cd ui && npm run build

# Vite dev server (handy for iterating on Hud.svelte standalone;
# real integration test still requires `just install` + KSP launch).
ui-dev: ui-bootstrap
    cd ui && npm run dev

# Project-graph TS check (root `npm run typecheck` is `tsc -b`) plus
# svelte-check on each workspace — the latter catches Svelte template
# errors and unused-class-field-style checks that `tsc -b` skips.
ui-typecheck: ui-bootstrap
    cd ui && npm run typecheck && npm run typecheck --workspaces --if-present

# --- Proto (proto/ → mod/Nova.Core/Persistence/Protos/Generated/) ---

# Regenerate C# proto bindings via protobuf-net's protogen.
# Output is gitignored — every fresh checkout must run this before build.
proto:
    mkdir -p mod/Nova.Core/Persistence/Protos/Generated
    rm -f mod/Nova.Core/Persistence/Protos/Generated/*.cs
    dotnet tool restore
    dotnet protogen --csharp_out=mod/Nova.Core/Persistence/Protos/Generated --proto_path=proto '+oneof=enum' nova.proto

# --- C# (mod/) ---

mod-build config="Release": proto nova-ksp-build
    cd mod && dotnet build Nova.sln -c {{config}}

mod-clean:
    cd mod && dotnet clean Nova.sln
    rm -rf mod/Nova.Core/build mod/Nova/build mod/Nova.Tests/bin mod/Nova.Tests/obj

# Run all test suites — C# (mod/) + Rust (crates/).
test config="Release": (mod-build config) save-cli-test sim-test nova-ksp-test
    cd mod && dotnet test Nova.sln -c {{config}} --no-build

# C#-only tests.
mod-test config="Release": (mod-build config)
    cd mod && dotnet test Nova.sln -c {{config}} --no-build

# --- Rust (crates/) ---

# Build the save-cli binary in release mode.
save-cli-build:
    cargo build --release -p nova-save-cli

# Run save-cli (forward args, e.g. `just save-cli dump some.nvs`).
save-cli *args: save-cli-build
    target/release/nova-save-cli "$@"

# Run save-cli's Rust tests (smoke tests for the dump pipeline).
save-cli-test:
    cargo test --release -p nova-save-cli

# Install nova-save-cli to ~/.cargo/bin/ for use anywhere on PATH.
save-cli-install:
    cargo install --path crates/save-cli

# Build the nova-sim simulation engine (pure Rust, no FFI yet).
sim-build:
    cargo build --release -p nova-sim

# Run nova-sim's tests — unit + standalone scenario tests that
# exercise the simulator without KSP or FFI.
sim-test:
    cargo test --release -p nova-sim

# Build the nova-ksp FFI cdylib + regenerate C# bindings into
# mod/Nova.Ffi.Generated/. The C# build (mod-build) pulls those
# bindings in via Nova.Ffi.Generated.csproj.
#
# Cross-compile to x86_64-apple-darwin: KSP 1.12.5 ships an x86_64-only
# binary that runs under Rosetta 2 on Apple Silicon, so any cdylib it
# loads via DllImport must match. Bare `cargo build` defaults to the
# host arch (arm64 on Apple Silicon) and silently fails to load.
nova-ksp-build:
    cargo build --release -p nova-ksp --target x86_64-apple-darwin

# Run nova-ksp's FFI smoke tests. Tests build native — the cross-compiled
# cdylib in target/x86_64-apple-darwin/ is for KSP only.
nova-ksp-test:
    cargo test --release -p nova-ksp

# --- Release packaging ---

# Stage GameData/Nova/ into a zip for distribution. Includes:
#   Nova.Core.dll + Nova.dll
#   0Harmony.dll (KSP doesn't bundle Harmony)
#   protobuf-net.dll (proto serialization on the C# side)
#   libnova_ksp.dylib (Rust simulator FFI bridge)
#   ModuleManager Patches/
#   UI/ — Vite-built Dragonglass UI bundle (hud.js + chunks)
dist: (mod-build "Release") ui-build nova-ksp-build
    #!/usr/bin/env bash
    set -euo pipefail
    stage=$(mktemp -d)
    root="$stage/GameData/Nova"
    mkdir -p "$root/Patches" "$root/UI"

    # Managed assemblies
    cp mod/Nova.Core/build/Nova.Core.dll        "$root/"
    cp mod/Nova/build/Nova.dll                  "$root/"
    cp mod/Nova/build/Nova.Ffi.Generated.dll    "$root/"
    cp mod/Nova/build/0Harmony.dll              "$root/"
    cp mod/Nova.Core/build/protobuf-net.dll     "$root/" 2>/dev/null || true

    # Native nova-ksp cdylib (Rust simulator FFI bridge). Lives next
    # to Nova.dll so Mono's DllImport name resolution finds it without
    # any DYLD/LD_LIBRARY_PATH gymnastics. Sourced from the cross-
    # compiled target dir to match KSP's x86_64 arch (see nova-ksp-build).
    cp target/x86_64-apple-darwin/release/libnova_ksp.dylib "$root/" 2>/dev/null \
      || cp target/release/libnova_ksp.so "$root/" 2>/dev/null \
      || cp target/release/nova_ksp.dll "$root/" 2>/dev/null \
      || echo "warning: nova-ksp cdylib not found" >&2

    # ModuleManager config overrides
    cp -R configs/overrides/. "$root/Patches/"

    # UI bundle — Dragonglass importmap exposes these as @nova/<file>
    cp -R ui/apps/nova/dist/. "$root/UI/"

    cp LICENSE "$root/"

    mkdir -p release
    out="{{justfile_directory()}}/release/Nova.zip"
    rm -f "$out"
    cd "$stage" && zip -qr "$out" GameData/
    rm -rf "$stage"
    echo "Built → release/Nova.zip"

# Build and install into a KSP directory.
#   just install ~/KSP_osx
install ksp_path: dist
    #!/usr/bin/env bash
    set -euo pipefail
    ksp="{{ksp_path}}"
    if [ ! -d "$ksp" ]; then
        echo "error: KSP directory not found: $ksp" >&2
        exit 1
    fi
    rm -rf "$ksp/GameData/Nova"
    unzip -qo release/Nova.zip -d "$ksp"
    echo "Installed → $ksp/GameData/Nova"

# --- All ---

build: mod-build save-cli-build sim-build nova-ksp-build

check: build test

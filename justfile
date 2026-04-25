# Nova — top-level build orchestration

default:
    @just --list

# --- Proto (proto/ → mod/Nova.Core/Persistence/Protos/Generated/) ---

# Regenerate C# proto bindings via protobuf-net's protogen.
# Output is gitignored — every fresh checkout must run this before build.
proto:
    mkdir -p mod/Nova.Core/Persistence/Protos/Generated
    rm -f mod/Nova.Core/Persistence/Protos/Generated/*.cs
    dotnet tool restore
    dotnet protogen --csharp_out=mod/Nova.Core/Persistence/Protos/Generated --proto_path=proto '+oneof=enum' nova.proto

# --- C# (mod/) ---

mod-build config="Release": proto
    cd mod && dotnet build Nova.sln -c {{config}}

mod-clean:
    cd mod && dotnet clean Nova.sln
    rm -rf mod/Nova.Core/build mod/Nova/build mod/Nova.Tests/bin mod/Nova.Tests/obj

# Run all test suites — C# (mod/) + Rust (crates/).
test config="Release": (mod-build config) save-cli-test
    cd mod && dotnet test Nova.sln -c {{config}} --no-build

# C#-only tests.
mod-test config="Release": (mod-build config)
    cd mod && dotnet test Nova.sln -c {{config}} --no-build

# --- Rust (crates/) ---

# Build the save-cli binary in release mode.
save-cli-build:
    cargo build --release -p nova-save-cli

# Run save-cli (forward args, e.g. `just save-cli -- dump some.hgs`).
save-cli *args: save-cli-build
    target/release/nova-save-cli {{args}}

# Run save-cli's Rust tests (smoke tests for the dump pipeline).
save-cli-test:
    cargo test --release -p nova-save-cli

# Install nova-save-cli to ~/.cargo/bin/ for use anywhere on PATH.
save-cli-install:
    cargo install --path crates/save-cli

# --- Release packaging ---

# Stage GameData/Nova/ into a zip for distribution. Includes:
#   Nova.Core.dll + Nova.dll
#   0Harmony.dll (KSP doesn't bundle Harmony)
#   Google.OrTools managed + osx-x64 native dylib
#   ModuleManager Patches/
dist: (mod-build "Release")
    #!/usr/bin/env bash
    set -euo pipefail
    stage=$(mktemp -d)
    root="$stage/GameData/Nova"
    mkdir -p "$root/Patches"

    # Managed assemblies
    cp mod/Nova.Core/build/Nova.Core.dll  "$root/"
    cp mod/Nova/build/Nova.dll            "$root/"
    cp mod/Nova/build/0Harmony.dll        "$root/"
    cp mod/Nova.Core/build/Google.OrTools.dll "$root/"
    cp mod/Nova.Core/build/Google.Protobuf.dll "$root/" 2>/dev/null || true
    cp mod/Nova.Core/build/protobuf-net.dll "$root/" 2>/dev/null || true

    # OR-Tools native (osx-x64)
    ortools_native="${HOME}/.nuget/packages/google.ortools.runtime.osx-x64/9.8.3296/runtimes/osx-x64/native"
    if [ -d "$ortools_native" ]; then
        cp "$ortools_native"/*.dylib "$root/"
        # P/Invoke expects libgoogle-ortools-native.dylib (lib prefix);
        # the package ships google-ortools-native.dylib, so duplicate it.
        if [ -f "$root/google-ortools-native.dylib" ] && [ ! -f "$root/libgoogle-ortools-native.dylib" ]; then
            cp "$root/google-ortools-native.dylib" "$root/libgoogle-ortools-native.dylib"
        fi
    else
        echo "warning: OR-Tools native runtime not found at $ortools_native" >&2
    fi

    # ModuleManager config overrides
    cp -R configs/overrides/. "$root/Patches/"

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

build: mod-build save-cli-build

check: build test

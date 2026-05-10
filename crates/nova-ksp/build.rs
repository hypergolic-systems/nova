//! Two codegen passes:
//!
//! 1. **prost** — generates Rust types from `proto/nova.proto` so
//!    nova-ksp can deserialize the C#-serialized `VesselStructure` /
//!    `VesselState` / `PartDatabase` blobs that cross the FFI.
//!
//! 2. **csbindgen** — emits matching C# `[StructLayout]` +
//!    `[DllImport]` bindings into `mod/Nova.Ffi.Generated/`. csbindgen
//!    walks `#[repr(C)]` structs and `extern "C"` fns from the Rust
//!    sources we list as inputs; it does not touch the prost-generated
//!    types (which aren't `#[repr(C)]` anyway — they're managed-side
//!    only via protobuf-net on the C# side).

use std::path::PathBuf;

fn main() {
    println!("cargo:rerun-if-changed=src");
    println!("cargo:rerun-if-changed=../../proto/nova.proto");

    // Pass 1: prost — Rust bindings for nova.proto.
    let proto_root = PathBuf::from("../../proto");
    let proto_file = proto_root.join("nova.proto");
    prost_build::Config::new()
        .compile_protos(&[&proto_file], &[&proto_root])
        .expect("compile nova.proto");

    // Pass 2: csbindgen — C# bindings for the FFI surface.
    let cs_out_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("mod")
        .join("Nova.Ffi.Generated");

    if let Err(e) = std::fs::create_dir_all(&cs_out_dir) {
        println!("cargo:warning=skipping csbindgen codegen: {}", e);
        return;
    }

    let cs_file = cs_out_dir.join("NovaNative.g.cs");

    let result = csbindgen::Builder::default()
        .input_extern_file("src/world.rs")
        .input_extern_file("src/vessel.rs")
        .input_extern_file("src/arena.rs")
        .input_extern_file("src/state/battery.rs")
        .input_extern_file("src/state/command.rs")
        .input_extern_file("src/topic.rs")
        .csharp_dll_name("nova_ksp")
        .csharp_namespace("Nova.Ffi.Generated")
        .csharp_class_name("NovaNative")
        .csharp_class_accessibility("public")
        .csharp_use_function_pointer(false)
        .generate_csharp_file(&cs_file);

    if let Err(e) = result {
        println!("cargo:warning=csbindgen failed: {}", e);
    }
}

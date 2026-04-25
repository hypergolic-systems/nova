use std::path::PathBuf;

fn main() {
    let proto_root = PathBuf::from("../../proto");
    let proto_file = proto_root.join("nova.proto");
    println!("cargo:rerun-if-changed={}", proto_file.display());

    prost_build::Config::new()
        .compile_protos(&[&proto_file], &[&proto_root])
        .expect("compile nova.proto");
}

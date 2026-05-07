use anyhow::Result;
use clap::{Parser, Subcommand};
use std::path::PathBuf;

mod dump;
mod proto;

#[derive(Parser)]
#[command(name = "nova-save-cli", version, about = "Inspector for Nova .nvs/.nvc files")]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// Dump a .nvs (save) or .nvc (craft) file as text. File type is
    /// auto-detected from the HGS magic-byte header.
    Dump {
        /// Path to a .nvs or .nvc file.
        file: PathBuf,
    },
}

fn main() -> Result<()> {
    let cli = Cli::parse();
    match cli.command {
        Command::Dump { file } => dump::dump(&file),
    }
}

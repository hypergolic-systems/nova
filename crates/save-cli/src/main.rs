use anyhow::Result;
use clap::{Parser, Subcommand};
use std::path::PathBuf;

mod dump;
mod proto;

#[derive(Parser)]
#[command(name = "nova-save-cli", version, about = "Inspector for Nova .hgs/.hgc files")]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// Dump a .hgs (save) or .hgc (craft) file as text. File type is
    /// auto-detected from the HGS magic-byte header.
    Dump {
        /// Path to a .hgs or .hgc file.
        file: PathBuf,
    },
}

fn main() -> Result<()> {
    let cli = Cli::parse();
    match cli.command {
        Command::Dump { file } => dump::dump(&file),
    }
}

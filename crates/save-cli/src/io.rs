//! HGS file I/O — shared by `dump` and `launch`.
//!
//! Wire format mirrors `mod/Nova.Core/Persistence/NovaFileFormat.cs`:
//!   bytes 0..3: 'H' 'G' 'S'
//!   byte 3:     'C' (craft) | 'S' (save)
//!   bytes 4..8: u32 LE version
//!   bytes 8..:  prost-encoded CraftFile or SaveFile

use anyhow::{bail, Context, Result};
use prost::Message;
use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::Path;

use crate::proto::*;

pub const MAGIC: [u8; 3] = *b"HGS";
pub const CURRENT_VERSION: u32 = 2;

pub enum NovaFile {
    Craft(CraftFile),
    Save(SaveFile),
}

/// Header byte (`'C'` craft / `'S'` save) and the u32 version surfaced
/// alongside the decoded payload — `dump` prints them; the other
/// callers ignore them.
pub struct NovaFileHeader {
    pub kind: char,
    pub version: u32,
}

pub fn read_nova_file(path: &Path) -> Result<(NovaFileHeader, NovaFile)> {
    let mut file = File::open(path).with_context(|| format!("open {}", path.display()))?;
    let (kind, version, payload) = read_with_kind(&mut file)?;
    let header = NovaFileHeader { kind, version };
    let file = match kind {
        'C' => NovaFile::Craft(
            CraftFile::decode(payload.as_slice()).context("decode CraftFile")?,
        ),
        'S' => NovaFile::Save(
            SaveFile::decode(payload.as_slice()).context("decode SaveFile")?,
        ),
        other => bail!("Unknown HGS file type: '{other}'"),
    };
    Ok((header, file))
}

pub fn read_save_file(path: &Path) -> Result<SaveFile> {
    match read_nova_file(path)?.1 {
        NovaFile::Save(s) => Ok(s),
        NovaFile::Craft(_) => bail!("{} is a craft (.nvc), expected a save (.nvs)", path.display()),
    }
}

pub fn read_craft_file(path: &Path) -> Result<CraftFile> {
    match read_nova_file(path)?.1 {
        NovaFile::Craft(c) => Ok(c),
        NovaFile::Save(_) => bail!("{} is a save (.nvs), expected a craft (.nvc)", path.display()),
    }
}

/// Write a SaveFile back to disk via tmp-file-and-rename, so a crash partway
/// through doesn't truncate the player's save. Header is `HGS S <version-LE>`,
/// payload is the prost-encoded SaveFile.
pub fn write_save_file(path: &Path, save: &SaveFile) -> Result<()> {
    let dir = path.parent().unwrap_or_else(|| Path::new("."));
    let tmp_path = dir.join(format!(
        ".{}.tmp",
        path.file_name()
            .and_then(|s| s.to_str())
            .unwrap_or("nova-save-cli")
    ));

    {
        let mut tmp = File::create(&tmp_path)
            .with_context(|| format!("create tmp {}", tmp_path.display()))?;
        tmp.write_all(&MAGIC).context("write magic")?;
        tmp.write_all(&[b'S']).context("write kind")?;
        tmp.write_all(&CURRENT_VERSION.to_le_bytes())
            .context("write version")?;
        tmp.write_all(&save.encode_to_vec())
            .context("write payload")?;
        tmp.sync_all().context("fsync tmp")?;
    }

    fs::rename(&tmp_path, path)
        .with_context(|| format!("rename {} → {}", tmp_path.display(), path.display()))?;
    Ok(())
}

fn read_with_kind(file: &mut File) -> Result<(char, u32, Vec<u8>)> {
    let mut header = [0u8; 8];
    file.read_exact(&mut header).context("read HGS header")?;

    if header[..3] != MAGIC {
        bail!(
            "Invalid HGS magic: {:?}",
            String::from_utf8_lossy(&header[..3])
        );
    }
    let kind = header[3] as char;
    let version = u32::from_le_bytes([header[4], header[5], header[6], header[7]]);

    let mut payload = Vec::new();
    file.read_to_end(&mut payload).context("read payload")?;

    Ok((kind, version, payload))
}

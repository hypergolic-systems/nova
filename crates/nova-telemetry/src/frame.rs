//! Hand-rolled JSON helpers for positional-array wire payloads.
//!
//! Mirrors the legacy `mod/Nova/_legacy/Telemetry/JsonWriter.cs`
//! semantics: invariant-culture number formatting, RFC 8259 string
//! escaping, no whitespace. We don't pull `serde_json` here because
//! the wire format is small and positional, and hand-rolling keeps
//! the cdylib lean and the output byte-for-byte stable.

/// Append `[`.
pub fn write_array_open(out: &mut Vec<u8>) {
    out.push(b'[');
}

/// Append `]`.
pub fn write_array_close(out: &mut Vec<u8>) {
    out.push(b']');
}

/// Append `,` between elements. Pass `&mut first` initialised to
/// `true`; the helper flips it to `false` after the first call.
pub fn write_sep(out: &mut Vec<u8>, first: &mut bool) {
    if *first {
        *first = false;
    } else {
        out.push(b',');
    }
}

/// Write a u32 as a quoted string. Matches Dragonglass's PartTopic
/// id encoding — JS numbers cap at 2^53 and KSP persistent ids are
/// 32-bit, but stringifying lets the TS decoder treat all id fields
/// uniformly across topics.
pub fn write_u32_as_string(out: &mut Vec<u8>, v: u32) {
    out.push(b'"');
    out.extend_from_slice(v.to_string().as_bytes());
    out.push(b'"');
}

/// Write an `f64`. Rust's `to_string` uses ryu, producing the
/// shortest round-trip representation — equivalent to .NET's "R"
/// format. Non-finite (NaN, ±∞) emits literal `0` to match the
/// legacy C# JsonWriter, since the wire format has no JSON
/// representation for non-finite floats.
pub fn write_f64(out: &mut Vec<u8>, v: f64) {
    if !v.is_finite() {
        out.push(b'0');
        return;
    }
    out.extend_from_slice(v.to_string().as_bytes());
}

/// Write a JSON string with RFC 8259 escaping.
pub fn write_str(out: &mut Vec<u8>, s: &str) {
    out.push(b'"');
    for ch in s.chars() {
        match ch {
            '"' => out.extend_from_slice(b"\\\""),
            '\\' => out.extend_from_slice(b"\\\\"),
            '\u{08}' => out.extend_from_slice(b"\\b"),
            '\u{0C}' => out.extend_from_slice(b"\\f"),
            '\n' => out.extend_from_slice(b"\\n"),
            '\r' => out.extend_from_slice(b"\\r"),
            '\t' => out.extend_from_slice(b"\\t"),
            c if (c as u32) < 0x20 => {
                let escaped = format!("\\u{:04x}", c as u32);
                out.extend_from_slice(escaped.as_bytes());
            }
            c => {
                let mut buf = [0u8; 4];
                let s = c.encode_utf8(&mut buf);
                out.extend_from_slice(s.as_bytes());
            }
        }
    }
    out.push(b'"');
}

/// Write a bool as `0` or `1`. Matches the legacy positional-array
/// convention — keeps the wire compact and parses uniformly with
/// neighbouring numerics.
pub fn write_bool_as_bit(out: &mut Vec<u8>, v: bool) {
    out.push(if v { b'1' } else { b'0' });
}

#[cfg(test)]
mod tests {
    use super::*;

    fn s(out: &Vec<u8>) -> &str {
        std::str::from_utf8(out).unwrap()
    }

    #[test]
    fn array_open_close() {
        let mut out = Vec::new();
        write_array_open(&mut out);
        write_array_close(&mut out);
        assert_eq!(s(&out), "[]");
    }

    #[test]
    fn sep_skips_first_then_inserts_commas() {
        let mut out = Vec::new();
        let mut first = true;
        write_array_open(&mut out);
        for v in [1.0, 2.0, 3.0] {
            write_sep(&mut out, &mut first);
            write_f64(&mut out, v);
        }
        write_array_close(&mut out);
        assert_eq!(s(&out), "[1,2,3]");
    }

    #[test]
    fn f64_uses_round_trip_format() {
        let mut out = Vec::new();
        write_f64(&mut out, 0.1);
        assert_eq!(s(&out), "0.1");
    }

    #[test]
    fn f64_nonfinite_emits_zero() {
        let mut out = Vec::new();
        write_f64(&mut out, f64::INFINITY);
        write_f64(&mut out, f64::NAN);
        assert_eq!(s(&out), "00");
    }

    #[test]
    fn u32_as_string_quotes_id() {
        let mut out = Vec::new();
        write_u32_as_string(&mut out, 42);
        assert_eq!(s(&out), "\"42\"");
    }

    #[test]
    fn str_escapes_specials() {
        let mut out = Vec::new();
        write_str(&mut out, "a\"b\nc\\d");
        assert_eq!(s(&out), r#""a\"b\nc\\d""#);
    }

    #[test]
    fn bool_as_bit() {
        let mut out = Vec::new();
        write_bool_as_bit(&mut out, true);
        write_bool_as_bit(&mut out, false);
        assert_eq!(s(&out), "10");
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Nova.Core.Utils;

namespace Nova.Sim.Config;

// Parse stock-KSP-shaped .cfg files into Nova.Core.Utils.ConfigNode
// trees. Mirrors `Assembly-CSharp/ConfigNode.Parse` for the subset
// Nova actually consumes:
//
//   NodeName
//   {
//     key = value
//     key2 = value with spaces
//     ChildNode
//     {
//       inner_key = inner value
//     }
//   }
//
// Conventions:
//   - Line-oriented. A `key = value` token must fit on a single line.
//   - `//` starts a line comment, swallowed to end-of-line. Inline
//     comments (`key = val // note`) are honoured: everything after
//     `//` is dropped.
//   - Tag names and key names use printable ASCII (no spaces).
//   - Whitespace around `=` is trimmed.
//   - A top-level file may contain multiple sibling node trees;
//     `ParseFile` returns a synthetic root whose children are the
//     top-level nodes (matches KSP's wrapping behaviour).
//
// Not supported (Nova doesn't use these):
//   - Multi-line strings or quoted strings — values are raw text up
//     to EOL / `//` boundary.
//   - `EXPRESSION` curve nodes (Nova patches own these in plain form).
//   - ModuleManager-specific operators — those are applied by
//     ModuleManagerLite after parse.
public static class KspConfigParser {
  public static ConfigNode ParseFile(string path) {
    var text = File.ReadAllText(path);
    return Parse(text);
  }

  public static ConfigNode Parse(string text) {
    var tokens = Tokenize(text);
    var root = new ConfigNode("ROOT");
    int pos = 0;
    while (pos < tokens.Count) {
      var child = ParseNode(tokens, ref pos);
      if (child == null) break;
      root.Nodes.Add(child);
    }
    return root;
  }

  // ---- Tokenizer ---------------------------------------------------

  private enum TokKind { Identifier, Equals, OpenBrace, CloseBrace, Value, NewLine }
  private struct Tok { public TokKind Kind; public string Text; public int Line; }

  private static List<Tok> Tokenize(string text) {
    var toks = new List<Tok>();
    int line = 1;
    int i = 0;
    int n = text.Length;
    while (i < n) {
      char c = text[i];
      // Line-comment: swallow to EOL.
      if (c == '/' && i + 1 < n && text[i + 1] == '/') {
        while (i < n && text[i] != '\n') i++;
        continue;
      }
      if (c == '\n') { line++; i++; continue; }
      if (char.IsWhiteSpace(c)) { i++; continue; }
      if (c == '{') { toks.Add(new Tok { Kind = TokKind.OpenBrace,  Text = "{", Line = line }); i++; continue; }
      if (c == '}') { toks.Add(new Tok { Kind = TokKind.CloseBrace, Text = "}", Line = line }); i++; continue; }
      if (c == '=') {
        toks.Add(new Tok { Kind = TokKind.Equals, Text = "=", Line = line });
        i++;
        // Collect rest of line as value text (trimmed; strip trailing
        // `//` comments).
        int start = i;
        while (i < n && text[i] != '\n' && text[i] != '\r') {
          if (text[i] == '/' && i + 1 < n && text[i + 1] == '/') break;
          i++;
        }
        string raw = text.Substring(start, i - start).Trim();
        toks.Add(new Tok { Kind = TokKind.Value, Text = raw, Line = line });
        continue;
      }
      // Identifier (tag name or key). Always consume the start char
      // — IsIdentStart can include sigils (`!`, `@`, `%`) that aren't
      // valid mid-identifier; advancing unconditionally keeps the
      // tokenizer from stalling on those.
      if (IsIdentStart(c)) {
        int start = i;
        i++;
        while (i < n && IsIdentPart(text[i])) i++;
        var ident = text.Substring(start, i - start);
        toks.Add(new Tok { Kind = TokKind.Identifier, Text = ident, Line = line });
        continue;
      }
      // Unknown character — skip to avoid infinite loop. Stock .cfg
      // files don't include these so silently ignoring is safer than
      // hard-failing on edge-case inputs.
      i++;
    }
    return toks;
  }

  private static bool IsIdentStart(char c) {
    // KSP tag/key names include leading `_`, letters, digits, `@`,
    // `!`, `-`, `+` — the last few because ModuleManager patches
    // start with sigils. ModuleManagerLite parses those further.
    // `#` is the leading char of localization keys (`#autoLOC_NNNNN`)
    // — keys in Squad/Localization/dictionary.cfg use the prefix
    // literally, and Resolve looks them up by the full `#autoLOC_…`
    // string.
    return char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '!'
        || c == '-' || c == '+' || c == '%' || c == '*' || c == '?'
        || c == '#';
  }

  private static bool IsIdentPart(char c) {
    return char.IsLetterOrDigit(c)
        || c == '_' || c == '-' || c == '+' || c == '.' || c == '/'
        || c == ':' || c == '@' || c == '[' || c == ']' || c == ','
        || c == '*' || c == '?' || c == '|' || c == '#';
  }

  // ---- Parser ------------------------------------------------------

  private static ConfigNode ParseNode(List<Tok> toks, ref int pos) {
    if (pos >= toks.Count) return null;
    var head = toks[pos];
    if (head.Kind != TokKind.Identifier)
      throw new FormatException("expected node name at line " + head.Line + ", got " + head.Kind);

    var node = new ConfigNode(head.Text);
    pos++;

    if (pos >= toks.Count || toks[pos].Kind != TokKind.OpenBrace)
      throw new FormatException("expected '{' after node name '" + head.Text + "' at line " + head.Line);
    pos++; // consume {

    while (pos < toks.Count) {
      var t = toks[pos];
      if (t.Kind == TokKind.CloseBrace) {
        pos++;
        return node;
      }
      if (t.Kind != TokKind.Identifier)
        throw new FormatException("expected key or child node at line " + t.Line);

      // Look ahead: `name =` → key/value; `name {` → child node.
      var next = pos + 1 < toks.Count ? toks[pos + 1].Kind : TokKind.CloseBrace;
      if (next == TokKind.Equals) {
        var key = t.Text;
        pos += 2; // consume name + =
        if (pos >= toks.Count || toks[pos].Kind != TokKind.Value)
          throw new FormatException("expected value after '=' at line " + t.Line);
        node.AddValue(key, toks[pos].Text);
        pos++;
        continue;
      }
      if (next == TokKind.OpenBrace) {
        // Child node header.
        var child = ParseNode(toks, ref pos);
        if (child != null) node.AddNode(child);
        continue;
      }
      // Bare identifier (no `=`, no `{`) — KSP allows naked keys like
      // `fxOriginalOffset` to flag an empty default. Store with an
      // empty value so downstream code still sees the key present.
      node.AddValue(t.Text, "");
      pos++;
    }

    throw new FormatException("unexpected EOF in node '" + node.Name + "'");
  }
}

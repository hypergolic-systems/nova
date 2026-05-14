using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nova.Sim.Telemetry;

// Recursive-descent JSON parser for inbound op envelopes. Mirrors
// Dragonglass.Telemetry.Util.Json.Parse exactly so the sim's HandleOp
// path consumes the same object tree shape the in-game OpDispatcher
// hands NovaPartTopic.HandleOp:
//
//   object  -> Dictionary<string, object>
//   array   -> List<object>
//   string  -> string
//   number  -> double  (integers too — handlers cast as needed)
//   bool    -> bool
//   null    -> null
//
// Returns null on any syntax error or trailing garbage; callers log
// and discard the frame. Used only for the op envelope's `args`
// field — the sim's outbound formatters write JSON via the existing
// JsonWriter in Nova.Core.Telemetry.
public static class JsonReader {
  public static object Parse(string s) {
    if (s == null) return null;
    int i = 0;
    SkipWs(s, ref i);
    if (!TryParseValue(s, ref i, out object v)) return null;
    SkipWs(s, ref i);
    return i == s.Length ? v : null;
  }

  private static void SkipWs(string s, ref int i) {
    while (i < s.Length) {
      char c = s[i];
      if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
      else return;
    }
  }

  private static bool TryParseValue(string s, ref int i, out object v) {
    v = null;
    SkipWs(s, ref i);
    if (i >= s.Length) return false;
    char c = s[i];
    switch (c) {
      case '{': return TryParseObject(s, ref i, out v);
      case '[': return TryParseArray(s, ref i, out v);
      case '"': return TryParseString(s, ref i, out v);
      case 't': case 'f': return TryParseBool(s, ref i, out v);
      case 'n': return TryParseNull(s, ref i, out v);
      default:
        if (c == '-' || (c >= '0' && c <= '9'))
          return TryParseNumber(s, ref i, out v);
        return false;
    }
  }

  private static bool TryParseObject(string s, ref int i, out object v) {
    v = null;
    if (s[i] != '{') return false;
    i++;
    var dict = new Dictionary<string, object>();
    SkipWs(s, ref i);
    if (i < s.Length && s[i] == '}') { i++; v = dict; return true; }
    while (true) {
      SkipWs(s, ref i);
      if (!TryParseString(s, ref i, out object keyObj)) return false;
      string key = (string)keyObj;
      SkipWs(s, ref i);
      if (i >= s.Length || s[i] != ':') return false;
      i++;
      if (!TryParseValue(s, ref i, out object val)) return false;
      dict[key] = val;
      SkipWs(s, ref i);
      if (i >= s.Length) return false;
      if (s[i] == ',') { i++; continue; }
      if (s[i] == '}') { i++; v = dict; return true; }
      return false;
    }
  }

  private static bool TryParseArray(string s, ref int i, out object v) {
    v = null;
    if (s[i] != '[') return false;
    i++;
    var list = new List<object>();
    SkipWs(s, ref i);
    if (i < s.Length && s[i] == ']') { i++; v = list; return true; }
    while (true) {
      if (!TryParseValue(s, ref i, out object elem)) return false;
      list.Add(elem);
      SkipWs(s, ref i);
      if (i >= s.Length) return false;
      if (s[i] == ',') { i++; continue; }
      if (s[i] == ']') { i++; v = list; return true; }
      return false;
    }
  }

  private static bool TryParseString(string s, ref int i, out object v) {
    v = null;
    if (i >= s.Length || s[i] != '"') return false;
    i++;
    var sb = new StringBuilder();
    while (i < s.Length) {
      char c = s[i++];
      if (c == '"') { v = sb.ToString(); return true; }
      if (c != '\\') { sb.Append(c); continue; }
      if (i >= s.Length) return false;
      char esc = s[i++];
      switch (esc) {
        case '"':  sb.Append('"'); break;
        case '\\': sb.Append('\\'); break;
        case '/':  sb.Append('/'); break;
        case 'b':  sb.Append('\b'); break;
        case 'f':  sb.Append('\f'); break;
        case 'n':  sb.Append('\n'); break;
        case 'r':  sb.Append('\r'); break;
        case 't':  sb.Append('\t'); break;
        case 'u':
          if (i + 4 > s.Length) return false;
          if (!int.TryParse(s.Substring(i, 4),
                  NumberStyles.HexNumber,
                  CultureInfo.InvariantCulture, out int cp))
            return false;
          sb.Append((char)cp);
          i += 4;
          break;
        default: return false;
      }
    }
    return false;
  }

  private static bool TryParseNumber(string s, ref int i, out object v) {
    v = null;
    int start = i;
    if (s[i] == '-') i++;
    while (i < s.Length) {
      char c = s[i];
      if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' ||
          c == '+' || c == '-') i++;
      else break;
    }
    string slice = s.Substring(start, i - start);
    if (!double.TryParse(slice, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double d))
      return false;
    v = d;
    return true;
  }

  private static bool TryParseBool(string s, ref int i, out object v) {
    v = null;
    if (i + 4 <= s.Length && s[i] == 't' && s[i+1] == 'r' && s[i+2] == 'u' && s[i+3] == 'e')
    { i += 4; v = true; return true; }
    if (i + 5 <= s.Length && s[i] == 'f' && s[i+1] == 'a' && s[i+2] == 'l' && s[i+3] == 's' && s[i+4] == 'e')
    { i += 5; v = false; return true; }
    return false;
  }

  private static bool TryParseNull(string s, ref int i, out object v) {
    v = null;
    if (i + 4 <= s.Length && s[i] == 'n' && s[i+1] == 'u' && s[i+2] == 'l' && s[i+3] == 'l')
    { i += 4; return true; }
    return false;
  }
}

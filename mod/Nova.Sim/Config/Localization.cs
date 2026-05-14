using System;
using System.Collections.Generic;
using System.IO;
using Nova.Core.Utils;

namespace Nova.Sim.Config;

// English-only translation table for stock KSP localization keys.
//
// Stock part configs carry their player-facing strings as
// `#autoLOC_NNNNN` keys; real KSP resolves them at part-load time
// via `Localizer.Format` against the matching language's dictionary
// in `Squad/Localization/dictionary.cfg`. The headless sim doesn't
// run KSP's Localizer, so without this lookup the Hud renders raw
// keys (`#autoLOC_500319`) where part titles should appear.
//
// Structure of `dictionary.cfg`:
//
//   Localization {
//     en-us {
//       #autoLOC_18284 = Stability Assist
//       #autoLOC_18285 = Prograde/Retrograde Hold
//       …
//     }
//     ja {  … }
//     …
//   }
//
// We only load `en-us` for now — the Hud is English-only. A future
// `--lang` flag could pick a different node.
//
// Missing keys fall through to the raw input — better to ship the
// untranslated identifier than blank.
public sealed class Localization {
  private readonly Dictionary<string, string> _entries
      = new Dictionary<string, string>(StringComparer.Ordinal);

  public int Count => _entries.Count;

  public static Localization Empty() => new Localization();

  public static Localization Load(string kspPath, string language = "en-us") {
    var loc = new Localization();
    var dictPath = Path.Combine(kspPath, "GameData", "Squad", "Localization", "dictionary.cfg");
    if (!File.Exists(dictPath)) {
      Console.Error.WriteLine("[loc] dictionary not found at " + dictPath);
      return loc;
    }
    ConfigNode root;
    try { root = KspConfigParser.ParseFile(dictPath); }
    catch (Exception ex) {
      Console.Error.WriteLine("[loc] parse failed: " + ex.Message);
      return loc;
    }
    foreach (var l10n in root.GetNodes("Localization")) {
      foreach (var langNode in l10n.GetNodes(language)) {
        for (int i = 0; i < langNode.Values.Count; i++) {
          var kv = langNode.Values[i];
          loc._entries[kv.Key] = kv.Value;
        }
      }
    }
    return loc;
  }

  // Resolve a raw value: if it's an `#autoLOC_…` key we know, return
  // the translation; otherwise return the input unchanged. Unhandled
  // KSP `Localizer` features (placeholder substitution `<<1>>`,
  // pluralization) pass through untouched — none of the strings the
  // Hud reads from part configs today use them.
  public string Resolve(string raw) {
    if (string.IsNullOrEmpty(raw)) return raw;
    if (raw[0] != '#') return raw;
    return _entries.TryGetValue(raw, out var translated) ? translated : raw;
  }
}

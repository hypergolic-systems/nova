using System;
using System.Collections.Generic;
using System.IO;
using Nova.Core.Utils;

namespace Nova.Sim.Config;

// In-memory stock-KSP part catalogue, indexed by internal `name`
// field, with all of Nova's ModuleManager overrides applied.
//
// Build sequence:
//   1. Walk every `.cfg` under <kspPath>/GameData/ (subdirectories
//      included; KSP's PartLoader does the same).
//   2. Collect every `PART { … }` node from any file with name set;
//      first-seen wins on collisions (KSP itself logs a warning and
//      keeps the first).
//   3. Walk every `.cfg` under <repoRoot>/configs/overrides/ and
//      apply `@PART[name] { … }` directives via ModuleManagerLite.
//      A patch may delete the part outright (`!PART {}`); the part
//      disappears from the catalogue in that case.
//
// Lookup is O(1) via the internal dictionary. Missing parts return
// null; the caller decides whether that's fatal (we make loading a
// `.nvc` referencing an unknown part loud — it's nearly always a
// stale GameData path or a missing mod the sim isn't aware of).
public sealed class PartDatabase {
  private readonly Dictionary<string, ConfigNode> _byName = new Dictionary<string, ConfigNode>();
  private int _stockCount;
  private int _patchedCount;
  private int _deletedCount;

  public int StockCount   => _stockCount;
  public int PatchedCount => _patchedCount;
  public int DeletedCount => _deletedCount;
  public int Count        => _byName.Count;
  public IEnumerable<string> Names => _byName.Keys;

  public ConfigNode Get(string partName) {
    if (_byName.TryGetValue(partName, out var n)) return n;
    // KSP quirk: some legacy part names have been remapped between
    // dot and underscore separators (e.g. mk1pod.v2 ↔ mk1pod_v2 after
    // the Making History pod overhaul). Try both forms transparently
    // so craft files written under either convention load.
    if (partName != null) {
      string alt = partName.IndexOf('.') >= 0 ? partName.Replace('.', '_')
                 : partName.IndexOf('_') >= 0 ? partName.Replace('_', '.')
                 : null;
      if (alt != null && _byName.TryGetValue(alt, out n)) return n;
    }
    return null;
  }

  public bool TryGet(string partName, out ConfigNode node) {
    node = Get(partName);
    return node != null;
  }

  // Walk <kspPath>/GameData/ for stock parts, then apply patches from
  // <patchesRoot>, then resolve `#autoLOC_*` localization keys in
  // every part's player-facing fields. Both args are absolute paths.
  public static PartDatabase Build(string kspPath, string patchesRoot) {
    var db = new PartDatabase();
    db.LoadStockParts(kspPath);
    db.ApplyPatches(patchesRoot);
    db.ResolveLocalization(Localization.Load(kspPath));
    return db;
  }

  // Replace `#autoLOC_*` references in player-facing string fields
  // with their en-us translations. Stock KSP does this at part-load
  // time via Localizer; for the sim we do it once at DB-build time
  // so downstream code (telemetry formatters, etc.) never sees raw
  // keys.
  private void ResolveLocalization(Localization loc) {
    foreach (var part in _byName.Values) {
      ResolveValue(part, "title", loc);
      ResolveValue(part, "manufacturer", loc);
      ResolveValue(part, "description", loc);
      ResolveValue(part, "tags", loc);
    }
  }

  private static void ResolveValue(ConfigNode node, string key, Localization loc) {
    for (int i = 0; i < node.Values.Count; i++) {
      var kv = node.Values[i];
      if (kv.Key != key) continue;
      var resolved = loc.Resolve(kv.Value);
      if (!ReferenceEquals(resolved, kv.Value)) {
        node.Values[i] = new ConfigNode.KeyValue { Key = key, Value = resolved };
      }
    }
  }

  private void LoadStockParts(string kspPath) {
    var gameData = Path.Combine(kspPath, "GameData");
    if (!Directory.Exists(gameData))
      throw new DirectoryNotFoundException("GameData not found under ksp-path: " + gameData);

    // Walk every <mod>/Parts subtree under GameData. KSP convention is
    // that part defs live under Parts/; skipping the rest avoids the
    // localization dictionary, control schemes, scenarios, etc. — none
    // of which carry PART nodes and some of which (dictionary.cfg) are
    // big enough to choke a naive parser.
    foreach (var modDir in Directory.EnumerateDirectories(gameData)) {
      var partsRoot = Path.Combine(modDir, "Parts");
      if (!Directory.Exists(partsRoot)) continue;
      foreach (var cfg in Directory.EnumerateFiles(partsRoot, "*.cfg", SearchOption.AllDirectories)) {
        ConfigNode root;
        try { root = KspConfigParser.ParseFile(cfg); }
        catch (Exception ex) {
          Console.Error.WriteLine("[part-db] parse failed: " + cfg + " — " + ex.Message);
          continue;
        }
        CollectParts(root);
      }
    }
  }

  private void CollectParts(ConfigNode root) {
    foreach (var n in root.GetNodes("PART")) {
      var name = n.GetValue("name");
      if (string.IsNullOrEmpty(name)) continue;
      if (_byName.ContainsKey(name)) continue;
      _byName[name] = n;
      _stockCount++;
    }
  }

  private void ApplyPatches(string patchesRoot) {
    if (!Directory.Exists(patchesRoot)) return; // no overrides — fine
    foreach (var cfg in Directory.EnumerateFiles(patchesRoot, "*.cfg", SearchOption.AllDirectories)) {
      ConfigNode root;
      try { root = KspConfigParser.ParseFile(cfg); }
      catch (Exception ex) {
        Console.Error.WriteLine("[part-db] patch parse failed: " + cfg + " — " + ex.Message);
        continue;
      }
      foreach (var patch in root.Nodes) {
        if (!ModuleManagerLite.TryParsePartSelector(patch, out var partNames)) continue;
        foreach (var partName in partNames) {
          // Honor the same dot/underscore fallback used at lookup time.
          var part = Get(partName);
          if (part == null) continue;
          var keep = ModuleManagerLite.ApplyPatch(patch, part);
          if (!keep) {
            _byName.Remove(part.GetValue("name") ?? partName);
            _deletedCount++;
          } else {
            _patchedCount++;
          }
        }
      }
    }
  }
}

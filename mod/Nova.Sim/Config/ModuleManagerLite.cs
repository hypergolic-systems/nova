using System;
using Nova.Core.Utils;

namespace Nova.Sim.Config;

// Applies the minimal subset of ModuleManager operators Nova actually
// uses across its 156 override patches:
//
//   @PART[name]            — selector for a specific stock part
//   !MODULE[ModuleName] {} — delete a child MODULE node by name
//   !RESOURCE[Name] {}     — delete a child RESOURCE node by name
//   !PART                  — delete the matched part entirely
//
// Nothing else: no `:NEEDS`, no `:HAS`, no `:FOR` ordering, no
// wildcards in selectors, no `%key` change-or-add, no `+PART` clones,
// no `*` / `?` patterns. Every operator above is a textual match
// against an exact name in square brackets.
//
// Inside the @PART{…} body, anything that is not a `!MODULE[]` /
// `!RESOURCE[]` / `!PART` directive is treated as an additive merge:
//   - bare `MODULE { … }` / `RESOURCE { … }` blocks → appended
//   - bare `key = value` lines → appended (KSP's stock behaviour
//     allows duplicates; Nova's patches never overwrite, so this
//     suffices)
public static class ModuleManagerLite {
  // True iff the patch node is a `@PART[name(|alt)*] { … }` directive,
  // returning the (one or more) bare part names via `partNames`.
  // ModuleManager allows `|`-separated alternation inside the brackets
  // — Nova's overrides use this for legacy/renamed pairs like
  // `Size2LFB|Size2LFB_v2`. Each alt is applied independently.
  public static bool TryParsePartSelector(ConfigNode node, out string[] partNames) {
    partNames = null;
    var n = node?.Name;
    if (string.IsNullOrEmpty(n) || !n.StartsWith("@PART[")) return false;
    if (!n.EndsWith("]")) return false;
    int prefix = "@PART[".Length;
    int len = n.Length - prefix - 1;
    if (len <= 0) return false;
    var inner = n.Substring(prefix, len);
    partNames = inner.IndexOf('|') >= 0
        ? inner.Split('|')
        : new[] { inner };
    return true;
  }

  // True iff the directive deletes the entire part (`!PART {}` inside
  // a @PART body). Names sit alone in this case.
  public static bool IsDeletePartDirective(ConfigNode node) {
    return node != null && node.Name == "!PART";
  }

  // Try parse `!MODULE[ModuleName]` → `ModuleName`. Returns false on
  // any other shape.
  public static bool TryParseDeleteModule(ConfigNode node, out string moduleName) {
    return TryParseDeleteSelector(node, "!MODULE[", out moduleName);
  }

  // Try parse `!RESOURCE[Name]` → `Name`.
  public static bool TryParseDeleteResource(ConfigNode node, out string resourceName) {
    return TryParseDeleteSelector(node, "!RESOURCE[", out resourceName);
  }

  private static bool TryParseDeleteSelector(ConfigNode node, string prefix, out string name) {
    name = null;
    var n = node?.Name;
    if (string.IsNullOrEmpty(n) || !n.StartsWith(prefix)) return false;
    if (!n.EndsWith("]")) return false;
    int p = prefix.Length;
    int len = n.Length - p - 1;
    if (len <= 0) return false;
    name = n.Substring(p, len);
    return true;
  }

  // Apply one @PART body's directives onto `target`. Mutates `target`
  // in place. Returns false if the patch demands the part be deleted
  // (`!PART {}` directive).
  public static bool ApplyPatch(ConfigNode patchBody, ConfigNode target) {
    if (patchBody == null || target == null) return true;
    if (target.Name != "PART")
      throw new ArgumentException("ApplyPatch target must be a PART node, got '" + target.Name + "'");

    foreach (var d in patchBody.Nodes) {
      if (IsDeletePartDirective(d)) return false; // drop the part

      if (TryParseDeleteModule(d, out var moduleName)) {
        RemoveNamedChild(target, "MODULE", moduleName);
        continue;
      }
      if (TryParseDeleteResource(d, out var resourceName)) {
        RemoveNamedChild(target, "RESOURCE", resourceName);
        continue;
      }
      // Bare additive node — append as-is. (Stock MM resolves
      // top-level `MODULE` / `RESOURCE` differently, but Nova's
      // overrides only ever add modules/resources without renaming.)
      target.Nodes.Add(d);
    }

    // Top-level values inside the @PART body are additive merges
    // against the part itself (e.g. mass overrides). Mirror stock MM:
    // overwrite if the key already exists, else add.
    foreach (var kv in patchBody.Values) {
      bool replaced = false;
      for (int i = 0; i < target.Values.Count; i++) {
        if (target.Values[i].Key == kv.Key) {
          target.Values[i] = new ConfigNode.KeyValue { Key = kv.Key, Value = kv.Value };
          replaced = true;
          break;
        }
      }
      if (!replaced) target.AddValue(kv.Key, kv.Value);
    }

    return true;
  }

  private static void RemoveNamedChild(ConfigNode target, string childTag, string nameValue) {
    for (int i = target.Nodes.Count - 1; i >= 0; i--) {
      var c = target.Nodes[i];
      if (c.Name != childTag) continue;
      var name = c.GetValue("name");
      if (name == nameValue) target.Nodes.RemoveAt(i);
    }
  }
}

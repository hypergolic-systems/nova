using System.Collections.Generic;

namespace Nova.Core.Utils;

// Minimal stock-KSP-shaped config tree, POCO-style.
//
// Nodes hold a tag name, a sequence of key=value pairs (Values), and
// a sequence of child nodes (Nodes). Mirrors the interface subset
// that Nova components read off KSP's ConfigNode:
//   GetValue(key) → first matching value or null
//   GetValues(key) → all matching values
//   GetNodes(name) → all child nodes with the given tag
//   HasValue(key) / HasNode(name)
//
// Construction is intentionally open — Nova.Sim's KspConfigParser
// builds these from .cfg text; tests can build fixtures inline; the
// mod-side adapter (when one is added) converts from KSP's
// ConfigNode by walking values/nodes.
public sealed class ConfigNode {
  public string Name { get; set; } = "";
  public List<KeyValue> Values { get; } = new List<KeyValue>();
  public List<ConfigNode> Nodes { get; } = new List<ConfigNode>();

  public ConfigNode() { }
  public ConfigNode(string name) { Name = name; }

  public string GetValue(string key) {
    for (int i = 0; i < Values.Count; i++) {
      if (Values[i].Key == key) return Values[i].Value;
    }
    return null;
  }

  public IEnumerable<string> GetValues(string key) {
    for (int i = 0; i < Values.Count; i++) {
      if (Values[i].Key == key) yield return Values[i].Value;
    }
  }

  public IEnumerable<ConfigNode> GetNodes(string name) {
    for (int i = 0; i < Nodes.Count; i++) {
      if (Nodes[i].Name == name) yield return Nodes[i];
    }
  }

  public bool HasValue(string key) {
    for (int i = 0; i < Values.Count; i++) {
      if (Values[i].Key == key) return true;
    }
    return false;
  }

  public bool HasNode(string name) {
    for (int i = 0; i < Nodes.Count; i++) {
      if (Nodes[i].Name == name) return true;
    }
    return false;
  }

  public void AddValue(string key, string value) {
    Values.Add(new KeyValue { Key = key, Value = value });
  }

  public void AddNode(ConfigNode node) {
    Nodes.Add(node);
  }

  public struct KeyValue {
    public string Key;
    public string Value;
  }
}

using System;
using System.Collections.Generic;

namespace Nova.Core.Components;

public class Registry {
  public static Dictionary<string, Record> Components = new();

  public static void MaybeRegister(Type type) {
    if (!VirtualComponent.Is(type)) {
      return;
    }

    var name = type.Name;
    if (Components.ContainsKey(name)) {
      throw new Exception($"VirtualComponent type {name} already registered");
    }

    Components[name] = new Record() {
      Type = type,
    };
  }

  public static Record Lookup(Type type) {
    return Lookup(type.Name);
  }

  public static Record Lookup(string name) {
    if (!Components.TryGetValue(name, out var record)) {
      throw new Exception($"Unknown VirtualComponent: {name}");
    }
    return record;
  }

  public class Record {
    public Type Type;
  }
}

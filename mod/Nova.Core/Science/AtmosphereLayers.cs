using System.Collections.Generic;

namespace Nova.Core.Science;

public readonly struct AtmLayer {
  public string Name        { get; }
  public double TopAltitude { get; }   // meters above sea level

  public AtmLayer(string name, double topAltitude) {
    Name = name;
    TopAltitude = topAltitude;
  }
}

// Per-body atmosphere layer table. Loaded from configs/overrides/science/
// atmosphere-layers.cfg at mod-load time (mod side); tests populate it
// directly via AddLayer.
//
// Layers are ordered bottom-to-top. LayerAt walks them in order and returns
// the first whose TopAltitude > altitude. Above the topmost layer (vacuum)
// returns null.
public class AtmosphereLayers {
  private readonly Dictionary<string, List<AtmLayer>> byBody = new();

  public void AddLayer(string bodyName, string layerName, double topAltitude) {
    if (!byBody.TryGetValue(bodyName, out var list))
      byBody[bodyName] = list = new List<AtmLayer>();
    list.Add(new AtmLayer(layerName, topAltitude));
    list.Sort((a, b) => a.TopAltitude.CompareTo(b.TopAltitude));
  }

  public AtmLayer? LayerAt(string bodyName, double altitude) {
    if (!byBody.TryGetValue(bodyName, out var list)) return null;
    foreach (var layer in list)
      if (altitude < layer.TopAltitude) return layer;
    return null;  // above the atmosphere
  }

  public IReadOnlyList<AtmLayer> LayersFor(string bodyName) {
    return byBody.TryGetValue(bodyName, out var list)
        ? list
        : (IReadOnlyList<AtmLayer>)System.Array.Empty<AtmLayer>();
  }
}

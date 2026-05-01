using System;
using System.Linq;
using UnityEngine;
using Nova.Core.Science;

namespace Nova;

// Loads NOVA_ATMOSPHERE_LAYERS configs from GameDatabase and seeds the
// ExperimentRegistry. Runs once at MainMenu — by then GameDatabase has
// finished its first parse, so configs are available.
[KSPAddon(KSPAddon.Startup.MainMenu, true)]
public class NovaScienceAddon : MonoBehaviour {

  void Awake() {
    try {
      var layers = LoadAtmosphereLayers();
      var registry = new ExperimentRegistry();
      registry.Register(new AtmosphericProfileExperiment(layers));
      registry.Register(new LongTermStudyExperiment());
      ExperimentRegistry.Instance = registry;

      int experimentCount = registry.All.Count();
      int bodyCount = layers.LayersFor("Kerbin").Count > 0
          ? FlightGlobals.Bodies?.Count(b => layers.LayersFor(b.bodyName).Count > 0) ?? 0
          : 0;
      NovaLog.Log($"[Science] Registry seeded with {experimentCount} experiments; atmosphere layers loaded for {bodyCount} bodies");
    } catch (Exception e) {
      NovaLog.LogError($"[Science] Addon init failed: {e}");
    }
  }

  private static AtmosphereLayers LoadAtmosphereLayers() {
    var layers = new AtmosphereLayers();
    var nodes = GameDatabase.Instance.GetConfigNodes("NOVA_ATMOSPHERE_LAYERS");
    foreach (var node in nodes) {
      string body = node.GetValue("body");
      if (string.IsNullOrEmpty(body)) {
        NovaLog.LogWarning("[Science] NOVA_ATMOSPHERE_LAYERS missing 'body' — skipped");
        continue;
      }
      foreach (var layer in node.GetNodes("LAYER")) {
        string name = layer.GetValue("name");
        string topStr = layer.GetValue("top");
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(topStr)) {
          NovaLog.LogWarning($"[Science] LAYER on {body} missing name/top — skipped");
          continue;
        }
        if (!double.TryParse(topStr, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var top)) {
          NovaLog.LogWarning($"[Science] LAYER on {body} has non-numeric top='{topStr}' — skipped");
          continue;
        }
        layers.AddLayer(body, name, top);
      }
    }
    return layers;
  }
}

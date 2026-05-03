using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Nova.Telemetry;

// Singleton topic carrying Nova's *virtual scene* — a UI-side scene
// concept that piggy-backs on top of KSP's real `LoadedScene`. KSP
// itself never leaves the underlying scene; the UI's router treats
// the virtual scene as overriding when non-empty.
//
// Today this powers the R&D building click: stock R&D is suppressed
// (see RnDBuildingPatches), the patch flips VirtualScene to "RND",
// and the Nova Hud routes to a full-page R&D view. The same topic
// will host other replacements (Mission Control, Tracking Station, …)
// without further plumbing — they're just other string values.
//
// Wire format: [virtualScene: string]
//   ""             — no override; UI shows whatever real scene KSP is in
//   "RND"          — Nova R&D scene
//   future…
//
// Inbound ops (UI → mod):
//   "setScene" [name: string]
//      Set the virtual scene. The UI calls this with "" when the
//      player exits the Nova view, and may set other names if it
//      ever needs to navigate to a Nova scene without a stock-side
//      trigger. Mod-side code (Harmony patches, GameEvents) sets
//      values directly via `Instance.VirtualScene`.
public sealed class NovaSceneTopic : Topic {
  private const string LogPrefix = "[Nova/Telemetry] ";

  public static NovaSceneTopic Instance { get; private set; }

  public override string Name => "NovaScene";

  private string _virtualScene = "";
  public string VirtualScene {
    get => _virtualScene;
    set {
      var v = value ?? "";
      if (_virtualScene == v) return;
      _virtualScene = v;
      MarkDirty();
    }
  }

  protected override void OnEnable() {
    Instance = this;
    base.OnEnable();
    MarkDirty();
  }

  protected override void OnDisable() {
    base.OnDisable();
    if (Instance == this) Instance = null;
  }

  public override void HandleOp(string op, List<object> args) {
    switch (op) {
      case "setScene": {
        if (args == null || args.Count < 1 || !(args[0] is string name)) {
          Debug.LogWarning(LogPrefix + Name + " setScene: expected [string]");
          return;
        }
        VirtualScene = name;
        return;
      }
      default:
        base.HandleOp(op, args);
        return;
    }
  }

  public override void WriteData(StringBuilder sb) {
    sb.Append('[');
    JsonWriter.WriteString(sb, _virtualScene);
    sb.Append(']');
  }
}

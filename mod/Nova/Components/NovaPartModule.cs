using System;
using System.Collections.Generic;
using System.Linq;
using Nova.Core.Components;

namespace Nova.Components;

public class NovaPartModule : PartModule {

  internal IEnumerable<VirtualComponent> Components;

  [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = true, guiName = "Virtual Components")]
  public string virtualComponentStatus = "";

  public override void OnLoad(ConfigNode node) {
    base.OnLoad(node);
  }

  public override void OnSave(ConfigNode node) {
    base.OnSave(node);
  }

  public override void OnStart(StartState state) {
    base.OnStart(state);

    if (state == StartState.Editor) {
      OnStartEditor();
    } else {
      OnStartFlight();
    }
    virtualComponentStatus = $"{string.Join(", ", Components.Select(c => c.Name))} ({(state == StartState.Editor ? "Editor" : "Flight")})";
  }

  internal ConfigNode GetPrefabModuleConfig() {
    return part.partInfo.partConfig.GetNodes("MODULE")
      .FirstOrDefault(n => n.GetValue("name") == GetType().Name);
  }

  private void OnStartEditor() {
    var moduleConfig = GetPrefabModuleConfig()
      ?? throw new Exception($"No prefab MODULE config for {GetType().Name}");
    var cmp = ComponentFactory.Create(moduleConfig);
    Components = new List<VirtualComponent> { cmp };
  }

  private void OnStartFlight() {
    var mod = vessel.FindVesselModuleImplementing<NovaVesselModule>();
    if (mod?.Virtual == null)
      throw new InvalidOperationException(
        $"VirtualVessel not initialized before PartModule.OnStart for {part.partInfo.name} (id={part.persistentId})");

    var components = mod.Virtual.GetComponents(part.persistentId).ToList();
    if (components.Count == 0)
      throw new InvalidOperationException(
        $"No VirtualComponents for part {part.persistentId} ({part.partInfo.name})");

    Components = components;
  }
}

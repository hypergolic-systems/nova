using System.IO;
using System.Linq;
using Nova.Core.Persistence;
using Nova.Persistence;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova;

/// <summary>
/// Utility belt of static entry points designed for kspcli `eval` calls.
/// kspcli's evaluator walks all loaded assemblies for type names by short
/// name, so `kspcli eval "NovaCli.LoadSave(...)"` resolves to these methods
/// without any registration plumbing.
///
/// All methods are flight-scene-only (they touch <c>FlightGlobals.Vessels</c>
/// and friends). Save names resolve to
/// <c>&lt;KSP&gt;/saves/&lt;name&gt;/persistent.nvs</c>.
/// </summary>
public static class NovaCli {

  /// <summary>
  /// Load <c>&lt;KSP&gt;/saves/&lt;saveName&gt;/persistent.nvs</c> into the
  /// running flight scene. Internally: open file → read HGS header →
  /// deserialize SaveFile → <see cref="NovaSaveLoader.ApplyQuickload"/>.
  /// Same code path F9 quickload uses, just with a user-supplied save name.
  /// </summary>
  public static bool LoadSave(string saveName) {
    var save = ReadSave(saveName);
    NovaLog.Log($"[NovaCli] LoadSave: {saveName} ({save.Vessels.Count} vessel(s))");
    return NovaSaveLoader.ApplyQuickload(save);
  }

  /// <summary>
  /// Load <c>&lt;KSP&gt;/saves/&lt;saveName&gt;/persistent.nvs</c> (typically
  /// one produced offline by `nova-save-cli launch`) and switch the active
  /// vessel to the one that was newly injected. save-cli appends new vessels
  /// to the end of <c>SaveFile.vessels</c>, so the "newly launched" vessel
  /// is the last entry; we read its persistentId from the proto before the
  /// load runs, then look up the matching live <see cref="Vessel"/>
  /// afterwards.
  /// </summary>
  public static bool LaunchVessel(string saveName) {
    var save = ReadSave(saveName);
    if (save.Vessels.Count == 0) {
      NovaLog.Log($"[NovaCli] LaunchVessel: {saveName} contains no vessels");
      return false;
    }
    var targetPid = save.Vessels[save.Vessels.Count - 1].Structure.PersistentId;
    NovaLog.Log($"[NovaCli] LaunchVessel: {saveName} → switching to pid={targetPid}");

    if (!NovaSaveLoader.ApplyQuickload(save)) return false;
    return SwitchToVessel(targetPid);
  }

  /// <summary>
  /// Switch the active vessel to the one matching <paramref name="persistentId"/>.
  /// Returns false (logging a warning) if no such vessel is loaded.
  /// </summary>
  public static bool SwitchToVessel(uint persistentId) {
    var vessel = FlightGlobals.Vessels.FirstOrDefault(v =>
        v != null && v.state != Vessel.State.DEAD && v.persistentId == persistentId);
    if (vessel == null) {
      NovaLog.Log($"[NovaCli] SwitchToVessel: no vessel with persistentId={persistentId}");
      return false;
    }
    if (vessel != FlightGlobals.ActiveVessel) {
      FlightGlobals.ForceSetActiveVessel(vessel);
    }
    if (FlightCamera.fetch != null) {
      FlightCamera.fetch.SetTarget(vessel.transform);
    }
    NovaLog.Log($"[NovaCli] SwitchToVessel: now active '{vessel.vesselName}' (pid={persistentId})");
    return true;
  }

  static Proto.SaveFile ReadSave(string saveName) {
    var path = Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveName, "persistent.nvs");
    using var stream = File.OpenRead(path);
    var (type, _) = NovaFileFormat.ReadPrefix(stream);
    if (type != 'S') throw new InvalidDataException($"{path} is not a save file (type='{type}')");
    return ProtoBuf.Serializer.Deserialize<Proto.SaveFile>(stream);
  }
}

using HarmonyLib;

namespace Nova.Patches;

/// <summary>
/// No-op stock KSP's crossfeed graph rebuild. Nova owns vessel resource
/// flow via its own LP solver — `Part.crossfeedPartSet` is dead weight
/// and the stock `BuildCrossfeedPartSets` walk is a meaningful chunk of
/// per-physics-tick work. Killing the rebuild also makes it impossible
/// for future code to read a stale `crossfeedPartSet` and treat it as
/// authoritative.
///
/// `Part.fuelCrossFeed` itself stays as inert data — stock setters
/// (decouplers, docking nodes, ModuleToggleCrossfeed) keep writing to
/// it, but with the rebuild disabled nothing in Nova reads what's
/// written.
/// </summary>
[HarmonyPatch(typeof(Vessel), nameof(Vessel.BuildCrossfeedPartSets))]
public static class CrossfeedPartSetPatch {

  [HarmonyPrefix]
  public static bool Prefix() => false;
}

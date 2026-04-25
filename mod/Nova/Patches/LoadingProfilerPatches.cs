using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Expansions;
using HarmonyLib;
using Nova;
namespace Nova.Patches;

static class LoadingProfiler {
  static readonly Dictionary<string, Stopwatch> systemTimers = new();
  static readonly HashSet<string> stoppedSystems = new();

  // Track when each system's IsReady first returns true (realtimeSinceStartup)
  static readonly Dictionary<string, float> systemEndTimes = new();

  // Per-phase/part timing via ProgressTitle changes
  static string currentSystem;
  static string lastTitle;
  static Stopwatch titleTimer;
  static readonly List<(string system, string name, double ms)> phaseTimings = new();

  // Aggregate ConfigNode.Load timing
  static readonly Stopwatch configLoadTimer = new();
  static int configLoadCount;

  // DragCube stats
  static int dragCubeSetupCalls;
  static int dragCubeRenders;

  // Baseline: when LoadingScreen.Start() fires (before first LoadSystem)
  static float loadingStartTime = -1f;

  public static void SetLoadingStart() {
    loadingStartTime = UnityEngine.Time.realtimeSinceStartup;
    NovaLog.Log($"[LoadingProfiler] LoadingScreen.Start at t={loadingStartTime:F2}s");
  }

  public static void StartSystem(string name) {
    currentSystem = name;
    var sw = new Stopwatch();
    systemTimers[name] = sw;
    stoppedSystems.Remove(name);
    sw.Start();
  }

  public static void StopSystem(string name) {
    if (stoppedSystems.Contains(name)) return;
    stoppedSystems.Add(name);
    if (systemTimers.TryGetValue(name, out var sw))
      sw.Stop();
    systemEndTimes[name] = UnityEngine.Time.realtimeSinceStartup;
  }

  public static void OnProgressTitle(string title) {
    if (title == lastTitle) return;

    if (titleTimer != null && lastTitle != null && currentSystem != null) {
      titleTimer.Stop();
      phaseTimings.Add((currentSystem, lastTitle, titleTimer.Elapsed.TotalMilliseconds));
    }

    lastTitle = title;
    titleTimer = Stopwatch.StartNew();
  }

  public static void OnConfigLoadStart() {
    configLoadTimer.Start();
  }

  public static void OnConfigLoadEnd() {
    configLoadTimer.Stop();
    configLoadCount++;
  }

  public static void OnDragCubeSetup() {
    dragCubeSetupCalls++;
  }

  public static void OnDragCubeRender() {
    dragCubeRenders++;
  }

  public static void Report(float kspTotalSeconds = 0f) {
    // Flush last phase timing
    if (titleTimer != null && lastTitle != null && currentSystem != null) {
      titleTimer.Stop();
      phaseTimings.Add((currentSystem, lastTitle, titleTimer.Elapsed.TotalMilliseconds));
    }

    var lines = new List<string>();
    lines.Add("====== Loading Profile ======");

    // Compute measured time (systems we have Stopwatch data for)
    double measuredTime = 0;
    var measuredSystems = new Dictionary<string, double>();
    string[] order = { "FontLoader", "GameDatabase", "PartLoader", "ExpansionsLoader" };
    foreach (var name in order) {
      if (systemTimers.TryGetValue(name, out var sw)) {
        measuredSystems[name] = sw.Elapsed.TotalSeconds;
        measuredTime += sw.Elapsed.TotalSeconds;
      }
    }

    // Derive unmeasured time from KSP's total
    // FontLoader + GameDatabase can't be captured (start before our patches)
    // but we know: kspTotal = FontLoader + GameDatabase + MM + measured systems
    // FontLoader ≈ 0 (no .fnt files in GameData)
    double unmeasuredTime = kspTotalSeconds > 0 ? kspTotalSeconds - measuredTime : 0;

    foreach (var name in order) {
      if (measuredSystems.TryGetValue(name, out var secs)) {
        lines.Add($"  {name,-20} {secs,7:F1}s");
      } else if (name == "GameDatabase" && unmeasuredTime > 0) {
        lines.Add($"  {name,-20} {unmeasuredTime,7:F1}s (derived: KSP total minus measured systems)");
      } else if (name == "FontLoader") {
        lines.Add($"  {name,-20}    ~0.0s (no .fnt files found)");
      } else {
        lines.Add($"  {name,-20}     (not captured)");
      }
    }
    lines.Add($"  {"TOTAL",-20} {kspTotalSeconds,7:F1}s (from KSP)");

    lines.Add("--- Aggregates ---");
    lines.Add($"  ConfigNode.Load:   {configLoadTimer.Elapsed.TotalSeconds:F1}s ({configLoadCount} calls)");
    lines.Add($"  DragCube setup:    {dragCubeSetupCalls} parts");
    lines.Add($"  DragCube render:   {dragCubeRenders} parts (cache miss)");
    lines.Add($"  DragCube cached:   {dragCubeSetupCalls - dragCubeRenders} parts");

    // GameDatabase phases
    var dbPhases = phaseTimings.Where(t => t.system == "GameDatabase").ToList();
    if (dbPhases.Count > 0) {
      lines.Add("--- GameDatabase phases (top 20) ---");
      foreach (var (_, name, ms) in dbPhases.OrderByDescending(t => t.ms).Take(20))
        lines.Add($"  {ms,7:F0}ms  {name}");
    }

    // Slowest parts
    var parts = phaseTimings.Where(t => t.system == "PartLoader").ToList();
    if (parts.Count > 0) {
      lines.Add("--- Slowest parts (top 20) ---");
      foreach (var (_, name, ms) in parts.OrderByDescending(t => t.ms).Take(20))
        lines.Add($"  {ms,7:F0}ms  {name}");
    }

    lines.Add("=============================");

    foreach (var line in lines)
      NovaLog.Log($"[LoadingProfiler] {line}");
  }

  // Called from HarmonyPatcher.Initialize after PatchAll, since AnalyticsUtil is internal.
  public static void PatchAnalyticsUtil(Harmony harmony) {
    try {
      var type = AccessTools.TypeByName("AnalyticsUtil");
      if (type == null) {
        NovaLog.LogWarning("[LoadingProfiler] AnalyticsUtil type not found, report will not trigger automatically");
        return;
      }
      var method = AccessTools.Method(type, "LogGameStart", new[] { typeof(float) });
      if (method == null) {
        NovaLog.LogWarning("[LoadingProfiler] AnalyticsUtil.LogGameStart not found");
        return;
      }
      var prefix = new HarmonyMethod(typeof(AnalyticsUtil_LogGameStart_Patch).GetMethod(
        "Prefix", BindingFlags.Static | BindingFlags.NonPublic));
      harmony.Patch(method, prefix: prefix);
    } catch (Exception ex) {
      NovaLog.LogError($"[LoadingProfiler] Failed to patch AnalyticsUtil: {ex.Message}");
    }
  }
}

// --- Loading screen start baseline ---

[HarmonyPatch(typeof(LoadingScreen), "Start")]
static class LoadingScreen_Start_Patch {
  static void Prefix() => LoadingProfiler.SetLoadingStart();
}

// --- Per-system Start/Stop patches ---

[HarmonyPatch(typeof(FontLoader), nameof(FontLoader.StartLoad))]
static class FontLoader_StartLoad_Patch {
  static void Prefix() => LoadingProfiler.StartSystem("FontLoader");
}

[HarmonyPatch(typeof(FontLoader), nameof(FontLoader.IsReady))]
static class FontLoader_IsReady_Patch {
  static void Postfix(bool __result) {
    if (__result) LoadingProfiler.StopSystem("FontLoader");
  }
}

[HarmonyPatch(typeof(GameDatabase), nameof(GameDatabase.StartLoad))]
static class GameDatabase_StartLoad_Patch {
  static void Prefix() => LoadingProfiler.StartSystem("GameDatabase");
}

[HarmonyPatch(typeof(GameDatabase), nameof(GameDatabase.IsReady))]
static class GameDatabase_IsReady_Patch {
  static void Postfix(bool __result) {
    if (__result) LoadingProfiler.StopSystem("GameDatabase");
  }
}

[HarmonyPatch(typeof(PartLoader), nameof(PartLoader.StartLoad))]
static class PartLoader_StartLoad_Patch {
  static void Prefix() => LoadingProfiler.StartSystem("PartLoader");
}

[HarmonyPatch(typeof(PartLoader), nameof(PartLoader.IsReady))]
static class PartLoader_IsReady_Patch {
  static void Postfix(bool __result) {
    if (__result) LoadingProfiler.StopSystem("PartLoader");
  }
}

[HarmonyPatch(typeof(ExpansionsLoader), nameof(ExpansionsLoader.StartLoad))]
static class ExpansionsLoader_StartLoad_Patch {
  static void Prefix() => LoadingProfiler.StartSystem("ExpansionsLoader");
}

[HarmonyPatch(typeof(ExpansionsLoader), nameof(ExpansionsLoader.IsReady))]
static class ExpansionsLoader_IsReady_Patch {
  static void Postfix(bool __result) {
    if (__result) LoadingProfiler.StopSystem("ExpansionsLoader");
  }
}

// --- Per-part/phase timing via ProgressTitle ---

[HarmonyPatch(typeof(PartLoader), nameof(PartLoader.ProgressTitle))]
static class PartLoader_ProgressTitle_Patch {
  static void Postfix(string __result) => LoadingProfiler.OnProgressTitle(__result);
}

[HarmonyPatch(typeof(GameDatabase), nameof(GameDatabase.ProgressTitle))]
static class GameDatabase_ProgressTitle_Patch {
  static void Postfix(string __result) => LoadingProfiler.OnProgressTitle(__result);
}

// --- Aggregate ConfigNode.Load timing ---

[HarmonyPatch(typeof(ConfigNode), nameof(ConfigNode.Load), typeof(string), typeof(bool))]
static class ConfigNode_Load_Patch {
  static void Prefix() => LoadingProfiler.OnConfigLoadStart();
  static void Postfix() => LoadingProfiler.OnConfigLoadEnd();
}

// --- DragCube cache hit tracking ---

[HarmonyPatch(typeof(DragCubeSystem), nameof(DragCubeSystem.SetupDragCubeCoroutine), typeof(Part))]
static class DragCubeSystem_Setup_Patch {
  static void Prefix() => LoadingProfiler.OnDragCubeSetup();
}

[HarmonyPatch(typeof(DragCubeSystem), "RenderDragCubesCoroutine")]
static class DragCubeSystem_Render_Patch {
  static void Prefix() => LoadingProfiler.OnDragCubeRender();
}

// --- Report trigger: fires when loading completes ---

static class AnalyticsUtil_LogGameStart_Patch {
  static void Prefix(float loadingSeconds) => LoadingProfiler.Report(loadingSeconds);
}

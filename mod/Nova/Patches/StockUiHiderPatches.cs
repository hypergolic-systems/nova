using System.Reflection;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.Screens.Flight;
using UnityEngine;

namespace Nova.Patches;

public static class StockUiHiderPatches
{
    private const string LogPrefix = "[Nova/uihide] ";

    // The sphere's visible pixels come from a separate camera
    // (NavBallcamera) that renders the 3D mesh to a RenderTexture,
    // which a RawImage in the UI canvas displays. Disabling the
    // NavballFrame GameObject kills the RawImage; we also disable
    // the external camera so it stops rendering into a texture
    // nobody reads.
    [HarmonyPatch(typeof(NavBall), "Start")]
    public static class NavBallPatch
    {
        [HarmonyPostfix]
        public static void Postfix(NavBall __instance)
        {
            if (__instance == null || __instance.navBall == null) return;

            Transform root = __instance.navBall;
            for (Transform t = __instance.navBall; t != null; t = t.parent)
            {
                if (t.name == "NavballFrame" || t.name == "NavballPanel")
                {
                    root = t;
                    break;
                }
            }
            root.gameObject.SetActive(false);

            foreach (var cam in Object.FindObjectsOfType<Camera>())
            {
                if (cam != null && cam.name.ToLowerInvariant().Contains("navball"))
                {
                    cam.gameObject.SetActive(false);
                }
            }

            NovaLog.Log(LogPrefix + "hid NavBall (root=" + root.name + ")");
        }
    }

    [HarmonyPatch(typeof(METDisplay), "Start")]
    public static class METDisplayPatch
    {
        [HarmonyPostfix]
        public static void Postfix(METDisplay __instance)
        {
            HideSelf(__instance, "METDisplay");
        }
    }

    [HarmonyPatch(typeof(AltitudeTumbler), "Start")]
    public static class AltitudeTumblerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AltitudeTumbler __instance)
        {
            HideSelf(__instance, "AltitudeTumbler");
        }
    }

    [HarmonyPatch(typeof(VerticalSpeedGauge), "Start")]
    public static class VerticalSpeedGaugePatch
    {
        [HarmonyPostfix]
        public static void Postfix(VerticalSpeedGauge __instance)
        {
            HideSelf(__instance, "VerticalSpeedGauge");
        }
    }

    [HarmonyPatch(typeof(SpeedDisplay), "Start")]
    public static class SpeedDisplayPatch
    {
        [HarmonyPostfix]
        public static void Postfix(SpeedDisplay __instance)
        {
            HideSelf(__instance, "SpeedDisplay");
        }
    }

    [HarmonyPatch(typeof(LinearControlGauges), "Start")]
    public static class LinearControlGaugesPatch
    {
        [HarmonyPostfix]
        public static void Postfix(LinearControlGauges __instance)
        {
            HideSelf(__instance, "LinearControlGauges");
        }
    }

    // Stock Kerbal portrait gallery (`KerbalPortraitGallery`).
    // Dragonglass paints the IVA portraits via chroma-key
    // punch-through inside the HUD, so the stock visible row
    // would be a duplicate. We hide it visually via a
    // CanvasGroup with alpha=0 / blocksRaycasts=false rather
    // than `SetActive(false)` so:
    //   * the gallery's MonoBehaviours keep ticking (so
    //     `Portraits` stays populated for our scrape),
    //   * `RectContainment` keeps reporting the per-portrait
    //     rect as visible (so each `Kerbal.kerbalCam` stays
    //     enabled and `avatarTexture` stays live),
    //   * pointer events fall through to our HUD (so the user
    //     can click the EVA / IVA buttons we render in HTML).
    [HarmonyPatch(typeof(KerbalPortraitGallery), "Awake")]
    public static class KerbalPortraitGalleryPatch
    {
        [HarmonyPostfix]
        public static void Postfix(KerbalPortraitGallery __instance)
        {
            if (__instance == null) return;
            var go = __instance.gameObject;
            var cg = go.GetComponent<CanvasGroup>();
            if (cg == null) cg = go.AddComponent<CanvasGroup>();
            cg.alpha = 0f;
            cg.blocksRaycasts = false;
            cg.interactable = false;
            NovaLog.Log(LogPrefix + "hid KerbalPortraitGallery (CanvasGroup α=0)");
        }
    }

    // Stock stager. Dragonglass's StagingStack replaces it;
    // leaving stock's visible alongside causes duplicate icons
    // and conflicting drag targets.
    //
    // Stock's visible root is the `mainListAnchor` VerticalLayoutGroup,
    // a private field on StageManager. We hide it in Awake (the
    // only init method StageManager exposes — no Start override)
    // via reflection — deliberately NOT through
    // `ShowHideStageStack(false)`, which also calls
    // `InputLockManager.SetControlLock(STAGING)` and would kill
    // spacebar-to-stage.
    [HarmonyPatch(typeof(StageManager), "Awake")]
    public static class StageManagerPatch
    {
        private static readonly FieldInfo _anchorField =
            AccessTools.Field(typeof(StageManager), "mainListAnchor");

        [HarmonyPostfix]
        public static void Postfix(StageManager __instance)
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT && HighLogic.LoadedScene != GameScenes.EDITOR) return;
            if (__instance == null || _anchorField == null) return;
            var anchor = _anchorField.GetValue(__instance) as MonoBehaviour;
            if (anchor == null || anchor.gameObject == null)
            {
                NovaLog.LogWarning(LogPrefix + "StageManager.mainListAnchor not found — stock stager left visible");
                return;
            }
            anchor.gameObject.SetActive(false);
            NovaLog.Log(LogPrefix + "hid StageManager.mainListAnchor");
        }
    }

    // `ShowHideStageStack` is the only stock caller that touches
    // `mainListAnchor.gameObject.SetActive`, and it also takes /
    // releases the STAGING input lock as a side effect. Skipping
    // it entirely (return false from a prefix) keeps the anchor
    // permanently inactive AND stops anything from ever locking
    // STAGING. Known stock callers that would otherwise re-show
    // the stager or lock input:
    //   • `ToggleStageStack` — player-facing visibility key
    //   • `FlightUIModeController` — UI-mode change
    //   • `EVAConstructionModeController` — entering/leaving EVA
    //     construction mode
    [HarmonyPatch(typeof(StageManager), nameof(StageManager.ShowHideStageStack))]
    public static class ShowHideStageStackPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (HighLogic.LoadedScene == GameScenes.FLIGHT || HighLogic.LoadedScene == GameScenes.EDITOR)
            {
                return false;
            }
            return true;
        }
    }

    // Several widgets have no dedicated MonoBehaviour we can patch:
    //   • TimeFrame — top-centre cluster: MET clock, TimeWarp buttons,
    //     alarm clock, Telemetry (comms) indicator
    //   • TopFrame/IVACollapseGroup — upper-right lights/gear/brakes/
    //     abort buttons + temperature/atmosphere slide-out
    //   • UIModeFrame/…/UIModeSelector — mode-switch buttons
    //     (flight/docking/map/maneuver)
    //   • TrackingFilters — in-flight vessel-filter drawer toggle
    //
    // Hook into FlightUIModeController.Start — by the time it runs,
    // the whole Flight UI panel tree is instantiated and parented.
    //
    // Full hierarchy paths (not bare names) — GameObject.Find("NAME")
    // returns the first *active* match globally, and at least one
    // duplicate name exists in stock ("IVACollapseGroup" is both a
    // TopFrame child and a TrackingFilters child). Hitting the wrong
    // sibling breaks the tracking-filter UI and cascades into NREs.
    [HarmonyPatch(typeof(FlightUIModeController), "Start")]
    public static class FlightUIRootsPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            HideByPath("Flight/TimeFrame");
            HideByPath("Flight/TopFrame/IVACollapseGroup");
            HideByPath("Flight/UIModeFrame/EVACollapseGroup/UIModeSelector");
            HideByPath("Flight/TrackingFilters");
        }
    }

    // ApplicationLauncher is the persistent app-button strip stock
    // KSP shows along the screen edge — Engineer's Report, Stock dV,
    // Alarm Clock, KSPedia, Action Groups, Crew Manifest, etc. In
    // the editor it lives along the bottom; UIs that draft their
    // own analysis tools (Δv, mass, crew rosters) want it gone so
    // those affordances don't double up.
    //
    // ApplicationLauncher is `DontDestroyOnLoad`, so a one-shot
    // `Hide()` would persist into Flight too — unwanted, since
    // Flight may still need stock app buttons. We scope by patching
    // `Show()` to short-circuit while we're in the editor scene,
    // and additionally calling `Hide()` once on each EditorAny scene entry
    // to wipe whatever visibility state stock left behind from the previous scene.
    // OnDestroy of the addon (scene exit) calls `Show()` so Flight
    // and the Space Center see the launcher again.
    [HarmonyPatch(typeof(ApplicationLauncher), nameof(ApplicationLauncher.Show))]
    public static class ApplicationLauncherShowPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (HighLogic.LoadedScene == GameScenes.EDITOR) return false;
            return true;
        }
    }

    private static void HideSelf(MonoBehaviour mb, string label)
    {
        if (mb == null) return;
        mb.gameObject.SetActive(false);
        NovaLog.Log(LogPrefix + "hid " + label);
    }

    private static void HideByPath(string path)
    {
        var go = GameObject.Find(path);
        if (go == null)
        {
            NovaLog.LogWarning(LogPrefix + "no GameObject at '" + path + "'");
            return;
        }
        go.SetActive(false);
        NovaLog.Log(LogPrefix + "hid " + path);
    }
}

// Editor-scene addon. Calls Hide() once on entry (after stock
// initialization has run) and Show() on exit so Flight isn't
// poisoned. The poll-once-then-stop pattern handles
// both cases without paying a per-frame cost: we re-check
// every Update for ~3 s, then disable Update; if launcher still
// hasn't arrived by then, the stock launcher stays visible
// until the next scene transition.
[KSPAddon(KSPAddon.Startup.EditorAny, once: false)]
public class ApplicationLauncherEditorHider : MonoBehaviour
{
    private const string LogPrefix = "[Nova/uihide] ";
    private const float PollWindow = 3.0f;
    private float _started;
    private bool _applied;

    private void Start()
    {
        _started = Time.realtimeSinceStartup;
        TryApply();
    }

    private void Update()
    {
        if (_applied) { enabled = false; return; }
        if (Time.realtimeSinceStartup - _started > PollWindow)
        {
            enabled = false;
            return;
        }
        TryApply();
    }

    private void TryApply()
    {
        var launcher = ApplicationLauncher.Instance;
        if (launcher == null) return;
        launcher.Hide();
        _applied = true;
        NovaLog.Log(LogPrefix + "hid ApplicationLauncher (editor toolbar)");
    }

    private void OnDestroy()
    {
        var launcher = ApplicationLauncher.Instance;
        if (launcher != null) launcher.Show();
    }
}

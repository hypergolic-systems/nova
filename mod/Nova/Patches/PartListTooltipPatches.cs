using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.Screens.Editor;
using Nova.Telemetry;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Nova.Patches;

// Replace KSP's parts-list tooltip with Nova's `PartInfoPopup`.
//
// Stock flow (decompiled IL at ~/dev/ksp-reference/Assembly-CSharp):
//   `EditorPartIcon` hosts a `PartListTooltipController` (extends
//   `PinnableTooltipController`). On mouse enter, the inherited
//   `TooltipController.OnPointerEnter` calls
//   `UIMasterController.SpawnTooltip(this)`, which instantiates the
//   `PartListTooltip` prefab and binds it via `Setup(availablePart, ...)`.
//   Mouse exit / right-click follow the matching `OnPointerExit` /
//   `OnPointerClick` paths.
//
// Nova suppresses the spawn at the controller's pointer handlers
// (the highest-level Nova-owned cut: doesn't touch `UIMasterController`,
// which is shared with every other tooltip kind). Returning `false`
// from the `OnPointerEnter` prefix skips `base.OnPointerEnter` — the
// stock prefab never instantiates. Before returning we publish a hover
// frame on `NovaPartInfoTopic`; the UI subscribes to that and renders
// `PartInfoPopup.svelte` at the supplied anchor.
//
// Coordinate convention: Unity's `eventData.position` is in screen
// pixels with Y measured **up from bottom**. The browser uses Y
// **down from top**. Flip here so the topic's wire value is already
// browser-space (matches every other Nova → UI coord on the wire).
internal static class PartListTooltipPatches { }

[HarmonyPatch(typeof(PartListTooltipController), nameof(PartListTooltipController.OnPointerEnter))]
internal static class PartListTooltipController_OnPointerEnter_Patch {
  [HarmonyPrefix]
  static bool Prefix(PartListTooltipController __instance, PointerEventData eventData) {
    // Mirror stock's empty-slot guard (decompiled `OnPointerEnter`
    // bails when `editorPartIcon != null && editorPartIcon.isEmptySlot`).
    // Without this, hovering an empty inventory slot would publish a
    // "hover" for a placeholder with no real `AvailablePart`.
    var icon = AccessTools.Field(typeof(PartListTooltipController), "editorPartIcon")
        ?.GetValue(__instance) as EditorPartIcon;
    if (icon == null || icon.isEmptySlot || !icon.isPart) return false;
    var partInfo = icon.partInfo;
    if (partInfo == null) return false;

    var pos = eventData.position;
    double anchorX = pos.x;
    double anchorY = Screen.height - pos.y;
    NovaPartInfoTopic.SetHover(partInfo, anchorX, anchorY);

    return false; // skip stock tooltip spawn
  }
}

// `PartListTooltipController` does NOT override `OnPointerExit` —
// only `OnPointerEnter` and `OnPointerClick`. The exit handler is
// inherited from `PinnableTooltipController`, so targeting it through
// the derived type would silently re-resolve to the base method and
// affect every pinnable tooltip in KSP (navball, action group editor,
// etc.). Patch the base class explicitly and gate on instance type.
//
// Returning `true` so stock's despawn path still runs for non-parts-list
// callers. For parts-list controllers, stock's despawn is a no-op since
// the spawn was suppressed by the OnPointerEnter prefix above — so
// letting it run costs nothing.
[HarmonyPatch(typeof(PinnableTooltipController), nameof(PinnableTooltipController.OnPointerExit))]
internal static class PinnableTooltipController_OnPointerExit_Patch {
  [HarmonyPrefix]
  static void Prefix(PinnableTooltipController __instance) {
    if (__instance is PartListTooltipController) {
      NovaPartInfoTopic.ClearHover();
    }
  }
}

// Right-click on a part icon would normally extend the stock tooltip
// into its "extended info" / pin state. Nova's popup isn't pinnable in
// v1 — suppress the click handler so the underlying icon's own onClick
// (variant select / purchase / pick) still runs but stock's
// tooltip-pin path is short-circuited. Future pin support would flip
// a `pinned` bit on the topic and route the click through to Nova UI
// instead of the stock master controller.
[HarmonyPatch(typeof(PartListTooltipController), nameof(PartListTooltipController.OnPointerClick))]
internal static class PartListTooltipController_OnPointerClick_Patch {
  [HarmonyPrefix]
  static bool Prefix() {
    return false;
  }
}

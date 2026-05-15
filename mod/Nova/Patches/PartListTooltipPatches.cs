using HarmonyLib;
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
    // Don't open a hover while the player is holding a part — the
    // popup would just be in the way of the placement gesture.
    if (EditorLogic.SelectedPart != null) return false;

    // Send the icon's full screen-space rect (browser coords, top-down Y)
    // so the UI can flush the popup against the icon's right edge —
    // adjacent enough that the cursor can travel from icon to popup
    // without crossing empty space — and flip the popup to the icon's
    // left edge if the right side would clip the viewport.
    //
    // `GetWorldCorners` returns world-space coords; we project through
    // the canvas's camera (null for Screen Space - Overlay, populated
    // for Screen Space - Camera) to get screen pixels.
    var iconRt = icon.GetComponent<RectTransform>();
    double iconX, iconY, iconW, iconH;
    if (iconRt != null) {
      var canvas = iconRt.GetComponentInParent<Canvas>();
      var canvasCam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
          ? canvas.worldCamera : null;
      var corners = new Vector3[4]; // BL, TL, TR, BR
      iconRt.GetWorldCorners(corners);
      var bl = RectTransformUtility.WorldToScreenPoint(canvasCam, corners[0]);
      var br = RectTransformUtility.WorldToScreenPoint(canvasCam, corners[3]);
      var tl = RectTransformUtility.WorldToScreenPoint(canvasCam, corners[1]);
      iconX = bl.x;
      iconY = Screen.height - tl.y;       // browser top = screen.height - unity top
      iconW = br.x - bl.x;
      iconH = tl.y - bl.y;                // unity y increases upward
    } else {
      // Should never happen on an EditorPartIcon, but fall back to a
      // zero-sized rect at the cursor so the popup still opens somewhere
      // reasonable.
      var pos = eventData.position;
      iconX = pos.x;
      iconY = Screen.height - pos.y;
      iconW = 0;
      iconH = 0;
    }

    NovaPartInfoTopic.SetHover(partInfo, iconX, iconY, iconW, iconH);

    return false; // skip stock tooltip spawn
  }
}

// Note: there's intentionally no `OnPointerExit` patch. Closing the
// popup is owned by `NovaPartInfoCloser`, which polls the cursor each
// frame and clears only when the cursor leaves both the parts catalog
// rect AND DG's CEF UI. Wiring close-on-exit here would (a) reintroduce
// the rapid-toggle flicker we saw when Unity re-evaluated hover state
// after a CEF composite, and (b) defeat the popup-interactable design
// by closing the moment the cursor crossed from icon onto the popup.

// No `OnPointerClick` patch: stock's `PartListTooltipController.
// OnPointerClick` bails on its first line if the master controller's
// `currentTooltip` is null, and we never spawn the stock tooltip — so
// it's already a no-op for us. Patching it (returning false) would
// only get in the way of any other handlers Unity's EventSystem
// dispatches to the same GameObject, including the icon's own
// pick/grab path.

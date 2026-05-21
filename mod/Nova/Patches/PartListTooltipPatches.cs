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
    // While pinned, hover does not retarget. Stock right-click sticky
    // semantics: only an explicit right-click on a different icon
    // re-pins to that part — see the `OnPointerClick` patch below.
    var topic = NovaPartInfoTopic.Instance;
    if (topic != null && topic.IsPinned) return false;

    PartListTooltipPatches.PublishHover(icon, eventData, partInfo);

    return false; // skip stock tooltip spawn
  }
}

[HarmonyPatch(typeof(PartListTooltipController), nameof(PartListTooltipController.OnPointerClick))]
internal static class PartListTooltipController_OnPointerClick_Patch {
  // Right-click → sticky pin toggle. Stock's
  // `PinnableTooltipController.OnPointerClick` toggles pin on any
  // click; we narrow to right-click only because left-click is the
  // editor's part-placement gesture and stealing it would break the
  // primary editor interaction.
  //
  // Cases:
  //   no hover            → SetHover on the icon, then TogglePin (pins)
  //   hovered, not pinned → if same part: TogglePin (pins)
  //                         if other part: SetHover (re-target), TogglePin
  //   hovered, pinned     → if same part: TogglePin (unpins)
  //                         if other part: SetHover (re-target stays pinned)
  //                                       — handled by ClearHover then SetHover
  //                                         since SetHover ignores pinned-state retarget
  //                                         when we explicitly call it here.
  [HarmonyPrefix]
  static bool Prefix(PartListTooltipController __instance, PointerEventData eventData) {
    if (eventData.button != PointerEventData.InputButton.Right) return true; // left-click → stock
    var icon = AccessTools.Field(typeof(PartListTooltipController), "editorPartIcon")
        ?.GetValue(__instance) as EditorPartIcon;
    if (icon == null || icon.isEmptySlot || !icon.isPart) return false;
    var partInfo = icon.partInfo;
    if (partInfo == null) return false;
    if (EditorLogic.SelectedPart != null) return false;

    var topic = NovaPartInfoTopic.Instance;
    if (topic == null) return false;

    var current = topic.CurrentPart;
    if (current == partInfo) {
      // Same icon: pure toggle.
      NovaPartInfoTopic.TogglePin();
    } else if (current == null) {
      // Nothing currently shown: open + pin.
      PartListTooltipPatches.PublishHover(icon, eventData, partInfo);
      NovaPartInfoTopic.TogglePin();
    } else {
      // Different icon: retarget. If pinned, unpin first so SetHover
      // takes effect (pinned-state retarget is blocked by the hover
      // patch's IsPinned guard), then re-pin to the new part.
      bool wasPinned = topic.IsPinned;
      if (wasPinned) NovaPartInfoTopic.Unpin();
      PartListTooltipPatches.PublishHover(icon, eventData, partInfo);
      // Toggle to pinned regardless of previous state — right-click on
      // a different icon always lands in the pinned state.
      if (!topic.IsPinned) NovaPartInfoTopic.TogglePin();
    }
    return false; // skip base (no extended-info, no stock pin path)
  }
}

internal static class PartListTooltipPatches {
  // Shared icon-rect → SetHover path. Used by both the hover patch
  // and the right-click patch (when the latter has to open a popup
  // before pinning).
  internal static void PublishHover(EditorPartIcon icon,
                                     PointerEventData eventData,
                                     AvailablePart partInfo) {
    // Send the icon's full screen-space rect (browser coords, top-down
    // Y) plus the parts catalog panel's left/right edges in the same
    // coord space. The UI anchors the popup flush against the catalog's
    // right edge by default and flips to the catalog's left edge if it
    // would clip the viewport — neighbouring icons stay visible no
    // matter which icon was hovered.
    //
    // `GetWorldCorners` returns world-space coords; we project through
    // the canvas's camera (null for Screen Space - Overlay, populated
    // for Screen Space - Camera) to get screen pixels.
    var iconRt = icon.GetComponent<RectTransform>();
    double iconX, iconY, iconW, iconH;
    Camera canvasCam = null;
    if (iconRt != null) {
      var canvas = iconRt.GetComponentInParent<Canvas>();
      canvasCam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
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
      var pos = eventData.position;
      iconX = pos.x;
      iconY = Screen.height - pos.y;
      iconW = 0;
      iconH = 0;
    }

    // Catalog rect: from the EditorPartList singleton's RectTransform
    // (stable across scroll — its outer panel doesn't move when the
    // scroll content does). Fall back to icon edges if the singleton
    // isn't around (e.g. R&D building), so the popup still places
    // somewhere reasonable.
    double catalogLeftX = iconX;
    double catalogRightX = iconX + iconW;
    var partList = EditorPartList.Instance;
    if (partList != null) {
      var listRt = partList.GetComponent<RectTransform>();
      if (listRt != null) {
        var corners = new Vector3[4];
        listRt.GetWorldCorners(corners);
        var bl = RectTransformUtility.WorldToScreenPoint(canvasCam, corners[0]);
        var br = RectTransformUtility.WorldToScreenPoint(canvasCam, corners[3]);
        catalogLeftX = bl.x;
        catalogRightX = br.x;
      }
    }

    NovaPartInfoTopic.SetHover(partInfo,
        iconX, iconY, iconW, iconH,
        catalogLeftX, catalogRightX);
  }
}

// Note: there's intentionally no `OnPointerExit` patch. Closing the
// popup is owned by `NovaPartInfoCloser`, which polls the cursor each
// frame and clears only when the cursor leaves both the parts catalog
// rect AND DG's CEF UI. Wiring close-on-exit here would (a) reintroduce
// the rapid-toggle flicker we saw when Unity re-evaluated hover state
// after a CEF composite, and (b) defeat the popup-interactable design
// by closing the moment the cursor crossed from icon onto the popup.

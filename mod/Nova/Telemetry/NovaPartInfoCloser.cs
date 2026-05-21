using UnityEngine;
using UnityEngine.EventSystems;

namespace Nova.Telemetry;

// Owns the "should the part-info popup still be open?" decision.
//
// The HUD doesn't carry hover state — it's purely driven by the wire
// frame on `NovaPartInfoTopic`. Two conditions hold the popup open:
//
//   1. Another part icon is hovered — handled by the `OnPointerEnter`
//      Harmony patch, which retargets the topic via `SetHover(B)`.
//
//   2. The cursor stays over any UI surface (parts catalog, popup,
//      other DG-rendered chrome, stock editor panels, …). Only when
//      the cursor leaves *all* UI does the popup close.
//
// We collapse the "any UI" test to a single call to
// `EventSystem.current.IsPointerOverGameObject()`. Stock KSP UI
// elements are Unity Graphics with raycast targets, so the test
// catches the parts list, scroll content, gaps, tabs, etc.
// Dragonglass's CEF overlay registers a `RawImage` whose
// `ICanvasRaycastFilter` is alpha-keyed by the CEF surface, so
// opaque CEF pixels (including the popup) also count as "over UI"
// via the same single test.
//
// This is what fixed the row-gap closing bug: the catalog rect
// previously used (`EditorPartList.Instance`'s scroll-content rect)
// excluded the per-row gaps the cursor traverses when moving down,
// so the closer would fire between icons. Going through
// `EventSystem` instead defers to Unity's own hit-test, which knows
// the icons + their layout container.
[KSPAddon(KSPAddon.Startup.EditorAny, false)]
public sealed class NovaPartInfoCloser : MonoBehaviour {
  void Update() {
    var topic = NovaPartInfoTopic.Instance;
    if (topic == null || !topic.HasHover) return;

    // The player picking up a part for placement closes the hover —
    // the popup would just be in the way of the placement gesture.
    // Applies even when pinned: holding a part to place is a stronger
    // signal than "I want to keep reading the panel."
    if (EditorLogic.SelectedPart != null) {
      NovaPartInfoTopic.ClearHover();
      return;
    }

    // Pinned popup: suppress the auto-hide test. Esc collapses pin →
    // unpinned-with-hover; the next frame's normal auto-hide test
    // then runs and decides whether to keep the popup open based on
    // cursor-over-UI. Right-click toggles the pin via the
    // `OnPointerClick` Harmony patch.
    if (topic.IsPinned) {
      if (Input.GetKeyDown(KeyCode.Escape)) NovaPartInfoTopic.Unpin();
      return;
    }

    // Cursor over any raycast-receiving UI keeps the hover open. Stock
    // icons (and their parent containers / scroll viewport) register
    // as Unity Graphics with raycast targets; Dragonglass's CEF
    // overlay registers via a RawImage whose `ICanvasRaycastFilter`
    // (`HudRaycastFilter`) is alpha-keyed by the CEF surface, so the
    // popup itself counts as "over UI" through the same single test.
    if (EventSystem.current != null
        && EventSystem.current.IsPointerOverGameObject()) return;
    NovaPartInfoTopic.ClearHover();
  }
}

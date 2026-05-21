using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Components;
using Nova.Core.Components;
using Nova.Core.Components.Propulsion;
using Nova.Core.Telemetry;
using UnityEngine;

namespace Nova.Telemetry;

// Singleton topic driving the editor parts-list hover popup
// (`PartInfoPopup.svelte`). Replaces KSP's stock `PartListTooltip`.
//
// State machine: nullable `(AvailablePart, screen anchor)`. The Harmony
// patch on `PartListTooltipController.OnPointerEnter` calls `SetHover`;
// the matching `OnPointerExit` patch calls `ClearHover`. Each setter
// marks dirty and the broadcaster re-serialises on its next pulse.
//
// Wire format / kind catalogue: see `Nova.Core.Telemetry.PartInfoFormatter`
// — this topic is a thin Unity-side wrapper that resolves the components
// off the prefab's modules and hands them to the formatter.
//
// Lifetime: attached by `NovaTelemetryAddon` to the persistent
// `Dragonglass.Telemetry` host, same scope as `NovaSceneTopic` and
// `NovaEditorShipStructureTopic`. Active outside the editor as well —
// hovering parts in the R&D building goes through the same controller,
// and a stale-clear (`ClearHover`) on scene change keeps the popup
// truthful (cheap; the broadcaster idles on null state).
public sealed class NovaPartInfoTopic : Topic {
  private const string LogPrefix = "[Nova/Telemetry] ";

  public static NovaPartInfoTopic Instance { get; private set; }

  public override string Name => "NovaPartInfo";

  // Pending hover state — captured by `SetHover` on the Harmony patch
  // path. `_partInfo == null` means "no hover" — the topic emits the
  // empty-array sentinel. `_previewLive` flips to true only after the
  // preview-capture addon has confirmed its first push for this part
  // has landed in the native plugin; while it's false the topic still
  // reports empty so the UI never mounts `<PunchThrough>` against a
  // missing texture. The producer (the capture's Update loop) calls
  // `NotifyPreviewReady` once the texture is cached — that's the only
  // path that publishes a full frame to the wire.
  //
  // Icon rect (`_iconX/Y/W/H`) is the part icon's screen-space
  // bounding box in browser coords (top-down Y). The catalog rect
  // (`_catalogLeftX/RightX`) bounds the parts catalog panel in the
  // same coord space; the UI anchors the popup flush against the
  // catalog's right edge (or left, on flip) instead of the icon's
  // edge so neighbouring icons stay visible while the popup is open.
  //
  // `_pinned` is the sticky flag toggled by right-click. While pinned,
  // `NovaPartInfoCloser` suppresses auto-hide, and `SetHover` for a
  // different part re-targets the pin to that part (stock-style
  // right-click-on-different-icon behaviour). The UI mirrors the flag
  // for a pin glyph in the title bar; it has no other effect on the UI
  // since the C# side owns the close decision.
  private AvailablePart _partInfo;
  private double _iconX;
  private double _iconY;
  private double _iconW;
  private double _iconH;
  private double _catalogLeftX;
  private double _catalogRightX;
  private bool _previewLive;
  private bool _pinned;

  /// <summary>True iff a hover is currently active (the popup is open
  /// or about to be). Used by `NovaPartInfoCloser` to decide whether
  /// to run its per-frame "should the popup still be open?" check.</summary>
  public bool HasHover => _partInfo != null;

  /// <summary>True iff the popup is pinned (sticky). Used by
  /// `NovaPartInfoCloser` to suppress its auto-hide test.</summary>
  public bool IsPinned => _pinned;

  /// <summary>The currently-hovered part, or null if none. Used by the
  /// `OnPointerClick` patch to compare against the icon under the cursor
  /// before deciding whether to toggle-pin or re-pin to a new part.</summary>
  public AvailablePart CurrentPart => _partInfo;

  protected override void OnEnable() {
    Instance = this;
    base.OnEnable();
    // Initial state is "no hover" — emit the empty sentinel so any
    // already-subscribed UI immediately knows nothing is open.
    MarkDirty();
  }

  protected override void OnDisable() {
    base.OnDisable();
    if (Instance == this) Instance = null;
  }

  /// <summary>
  /// Open the popup on the given `AvailablePart` anchored at the
  /// browser-space screen point (Y measured from top), with the
  /// catalog panel's left/right edges for catalog-edge anchoring.
  /// Called from the `PartListTooltipController` prefix patch;
  /// idempotent — repeated hovers on the same part still refresh
  /// the anchor rects in case the parts list has scrolled. While
  /// pinned, `SetHover` for a different part re-pins to that part
  /// (matches stock right-click-on-different-icon behaviour).
  /// </summary>
  public static void SetHover(AvailablePart partInfo,
                               double iconX, double iconY,
                               double iconW, double iconH,
                               double catalogLeftX, double catalogRightX) {
    if (Instance == null || partInfo == null) return;
    // Idempotent: same part re-fired is a no-op so the capture isn't
    // restarted and the wire isn't re-dirtied. Unity's EventSystem
    // can re-fire `OnPointerEnter` after CEF composites perturb its
    // hover state, and we never want that to manifest as a visible
    // glitch in the popup or the rotating preview.
    if (Instance._partInfo == partInfo) {
      // Refresh the anchor rects anyway in case the parts list scrolled.
      Instance._iconX = iconX;
      Instance._iconY = iconY;
      Instance._iconW = iconW;
      Instance._iconH = iconH;
      Instance._catalogLeftX = catalogLeftX;
      Instance._catalogRightX = catalogRightX;
      return;
    }
    Instance._partInfo = partInfo;
    Instance._iconX = iconX;
    Instance._iconY = iconY;
    Instance._iconW = iconW;
    Instance._iconH = iconH;
    Instance._catalogLeftX = catalogLeftX;
    Instance._catalogRightX = catalogRightX;
    Instance._previewLive = false;
    // Kick the preview capture to start rendering this part. We do
    // NOT mark dirty here — the wire frame stays empty until the
    // capture's first push lands in the native plugin and it calls
    // `NotifyPreviewReady` below.
    NovaPartPreviewCapture.Instance?.SetActivePart(partInfo);
  }

  /// <summary>
  /// Clear the popup. Called from the `OnPointerExit` prefix and from
  /// scene-change hooks; harmless when nothing is hovered. Also clears
  /// the pinned flag — a clear is unconditional, no sticky overrides.
  /// </summary>
  public static void ClearHover() {
    if (Instance == null) return;
    if (Instance._partInfo == null && !Instance._previewLive && !Instance._pinned) return;
    Instance._partInfo = null;
    Instance._previewLive = false;
    Instance._pinned = false;
    NovaPartPreviewCapture.Instance?.ClearActivePart();
    // Empty frame is always safe to publish — no rect, no texture
    // needed, no magenta possible.
    Instance.MarkDirty();
  }

  /// <summary>
  /// Toggle the pinned (sticky) flag. Only valid when a hover is
  /// active; called from the `OnPointerClick` prefix patch on
  /// right-click. While pinned, `NovaPartInfoCloser` suppresses
  /// auto-hide; SetHover for a different part re-pins to that part.
  /// Marks dirty so the UI can update its pin glyph immediately.
  /// </summary>
  public static void TogglePin() {
    if (Instance == null) return;
    if (Instance._partInfo == null) return;
    Instance._pinned = !Instance._pinned;
    if (Instance._previewLive) Instance.MarkDirty();
  }

  /// <summary>
  /// Force-unpin without clearing the hover. Esc handler in
  /// `NovaPartInfoCloser` calls this so Esc collapses pin → unpinned-
  /// with-hover; the closer's normal auto-hide test runs on the next
  /// frame and decides whether to keep the popup open based on
  /// cursor-over-UI.
  /// </summary>
  public static void Unpin() {
    if (Instance == null) return;
    if (!Instance._pinned) return;
    Instance._pinned = false;
    if (Instance._previewLive) Instance.MarkDirty();
  }

  /// <summary>
  /// Called by `NovaPartPreviewCapture` after its first successful
  /// `DgHudNative_PushStreamFrame` for the current hover. Marks the
  /// topic dirty so the broadcaster releases the full hover frame to
  /// the UI on its next tick — by which point the texture is already
  /// cached in the native plugin, so the `<PunchThrough>` rect the
  /// UI mounts has a texture to composite into immediately.
  /// </summary>
  public static void NotifyPreviewReady() {
    if (Instance == null) return;
    if (Instance._partInfo == null) return; // hover already cleared
    if (Instance._previewLive) return;      // already published
    Instance._previewLive = true;
    Instance.MarkDirty();
  }

  // No inbound ops: the HUD is purely driven by the wire frame
  // (`if info, render; else don't`). Closing the popup is driven
  // from the C# side by `NovaPartInfoCloser`, which polls the
  // cursor against the parts catalog rect and DG's CEF-UI raycast.

  public override void WriteData(StringBuilder sb) {
    // Gate the wire frame on `_previewLive`: we don't release a hover
    // frame to the UI until the preview capture has confirmed its
    // first push, so the `<PunchThrough>` mount always lines up with
    // a cached texture in the native plugin.
    if (_partInfo == null || _partInfo.partPrefab == null || !_previewLive) {
      PartInfoFormatter.WriteEmpty(sb);
      return;
    }

    var prefab = _partInfo.partPrefab;
    // Parse every Nova MODULE off the original cfg. The factory is the
    // single source of truth for cfg → VirtualComponent: every value
    // the popup wants is whatever the factory already extracts.
    var components = new List<VirtualComponent>();
    var modules = _partInfo.partConfig?.GetNodes("MODULE");
    if (modules != null) {
      foreach (var moduleNode in modules) {
        var moduleName = moduleNode.GetValue("name");
        if (!ComponentFactory.IsHgsModule(moduleName)) continue;
        try {
          components.Add(ComponentFactory.Create(moduleNode));
        } catch (System.Exception ex) {
          // Mis-authored cfgs shouldn't take down the popup; log and
          // skip the bad module. The popup will simply lack that group.
          Debug.LogWarning(LogPrefix + "Skipping malformed " + moduleName
              + " on " + _partInfo.name + ": " + ex.Message);
        }
      }
    }

    // Side-channel for fields the factory doesn't populate from cfg
    // alone:
    //   - Docking port `nodeType` lives on the KSP-side
    //     `NovaDockingModule` (stock attach-size string).
    //   - RCS thruster count is set at OnStart from the part's
    //     transforms; for an unplaced prefab we count them directly.
    string dockingNodeType = null;
    int rcsThrusters = 0;
    if (prefab.Modules != null) {
      foreach (PartModule m in prefab.Modules) {
        if (m is NovaDockingModule dock) dockingNodeType = dock.nodeType;
        else if (m is NovaRcsModule rcs) {
          // Same convention NovaRcsModule.OnStart uses — count active
          // model transforms with the configured name. Safe on the
          // prefab GameObject: the model is loaded at PartLoader time.
          var transforms = prefab.FindModelTransforms(rcs.thrusterTransformName);
          if (transforms != null) {
            int active = 0;
            foreach (var t in transforms) {
              if (t.gameObject.activeInHierarchy) active++;
            }
            rcsThrusters = active;
          }
        }
      }
    }

    var dockingInfo = new PartInfoFormatter.DockingPortInfo(dockingNodeType, rcsThrusters);

    // Dry mass: prefab.mass is in tonnes (KSP convention). The wire is
    // in kilograms so the UI can pick its own kg/t threshold without
    // unit math at render time.
    double dryMassKg = prefab.mass * 1000.0;
    double cost = _partInfo.cost;

    PartInfoFormatter.Write(sb,
      _partInfo.name,                     // internal name
      _partInfo.title ?? "",
      _partInfo.manufacturer ?? "",
      _partInfo.description ?? "",
      dryMassKg, cost,
      _iconX, _iconY, _iconW, _iconH,
      _catalogLeftX, _catalogRightX, _pinned,
      components,
      dockingInfo);
  }
}

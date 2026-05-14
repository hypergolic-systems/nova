using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nova.Core.Components.Structural;
using UnityEngine;

namespace Nova.Components;

public class NovaDecouplerModule : NovaPartModule, IStageSeparator, IStageSeparatorChild {

  [KSPField]
  public float ejectionForce = 10f;

  [KSPField]
  public bool staged = true;

  [KSPField]
  public string explosiveNodeID = "top";

  // Session-local fire latch — guards against double-fire within one
  // session, no need to persist. After staging the part is on a
  // different vessel and won't re-enter OnActive on this instance.
  private bool isDecoupled;

  // Editor-time "release every neighbour at once" toggle lives on the
  // Decoupler virtual component (proto-persisted via DecouplerState),
  // not on a KSPField. KSP's ConfigNode save path is not in the loop —
  // Nova owns persistence end-to-end. The module reads through to its
  // Decoupler component so OnActive sees the same source of truth the
  // UI and the save file do.
  //
  // Radial decouplers (explosiveNodeID == "srf") have a single attach
  // face; omni mode collapses to single-node mode. We surface that as
  // CanFullSeparate=false so the UI greys the toggle out, and we
  // re-gate at fire time in case the field is set anyway.
  public bool CanFullSeparate => explosiveNodeID != "srf";

  public bool FullSeparation {
    get => decoupler?.FullSeparation ?? false;
    set { if (decoupler != null) decoupler.FullSeparation = value; }
  }

  private Decoupler decoupler;
  private AttachNode explosiveNode;
  private FXGroup fx;

  public override void OnStart(StartState state) {
    base.OnStart(state);
    decoupler = Components.OfType<Decoupler>().FirstOrDefault();
    if (explosiveNodeID == "srf")
      explosiveNode = part.srfAttachNode;
    else
      explosiveNode = part.FindAttachNode(explosiveNodeID);
    fx = part.findFxGroup("decouple");

    if (part.stagingIcon == string.Empty) {
      part.stagingIcon = explosiveNodeID == "srf" ? "DECOUPLER_HOR" : "DECOUPLER_VERT";
    }

    // Mirror the KSP-side design fields onto the Core component so
    // telemetry formatters in Nova.Core can render the per-decoupler
    // popover without reaching back into the mod-side module.
    if (decoupler != null) {
      decoupler.EjectionForce = ejectionForce;
      decoupler.CanFullSeparate = CanFullSeparate;
    }
  }

  public override void OnActive() {
    if (staged) Decouple();
  }

  private void Decouple() {
    if (isDecoupled) return;

    fx?.Burst();

    if (FullSeparation && CanFullSeparate)
      DecoupleOmni();
    else
      DecoupleSingle();

    GameEvents.onStageSeparation.Fire(
      new EventReport(FlightEvents.STAGESEPARATION, part, null, null, 0));
    isDecoupled = true;
    stagingEnabled = false;
  }

  // Default: cut one attach face, push the two halves apart 50/50.
  private void DecoupleSingle() {
    if (explosiveNode?.attachedPart == null) return;
    var target = explosiveNode.attachedPart;
    if (target == part.parent)
      part.decouple();
    else
      target.decouple();

    var force = ejectionForce * 0.5f;
    var dir = Vector3.Normalize(part.transform.position - target.transform.position);
    StartCoroutine(ApplyForces(2,
      (part, dir * force),
      (target, -dir * force)));
  }

  // Omni: detach every neighbour (children + parent), leaving the
  // decoupler as its own debris fragment between the released halves.
  // Each neighbour gets ejectionForce/2 of impulse; the decoupler eats
  // the equal-and-opposite from each, summed.
  private void DecoupleOmni() {
    var neighbours = part.children.ToList();
    var parent = part.parent;

    var perNeighbour = ejectionForce * 0.5f;
    var forces = new List<(Part, Vector3)>();
    var selfImpulse = Vector3.zero;

    foreach (var child in neighbours) {
      child.decouple();
      var dir = Vector3.Normalize(part.transform.position - child.transform.position);
      forces.Add((child, -dir * perNeighbour));
      selfImpulse += dir * perNeighbour;
    }
    if (parent != null) {
      part.decouple();
      var dir = Vector3.Normalize(part.transform.position - parent.transform.position);
      forces.Add((parent, -dir * perNeighbour));
      selfImpulse += dir * perNeighbour;
    }
    forces.Add((part, selfImpulse));

    StartCoroutine(ApplyForces(2, forces.ToArray()));
  }

  private IEnumerator ApplyForces(int frameDelay, params (Part part, Vector3 force)[] entries) {
    for (int i = 0; i < frameDelay; i++)
      yield return null;
    foreach (var (p, f) in entries)
      if (p != null) p.AddForce(f);
  }

  public override bool IsStageable() => staged;

  public override bool StagingEnabled() => base.StagingEnabled() && staged;

  // IStageSeparator
  public int GetStageIndex(int fallback) {
    if (!stagingEnabled && part.parent != null)
      return part.parent.inverseStage;
    return part.inverseStage;
  }

  // IStageSeparatorChild
  public bool PartDetaches(out List<Part> decoupledParts) {
    decoupledParts = new List<Part>();
    if (FullSeparation && CanFullSeparate) {
      decoupledParts.AddRange(part.children);
      if (part.parent != null) decoupledParts.Add(part.parent);
    } else if (explosiveNode?.attachedPart != null) {
      decoupledParts.Add(explosiveNode.attachedPart);
    }
    return true;
  }

  public bool IsEnginePlate() => false;
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Nova.Components;

public class NovaDecouplerModule : NovaPartModule, IStageSeparator, IStageSeparatorChild {

  [KSPField]
  public float ejectionForce = 10f;

  [KSPField(isPersistant = true)]
  public bool isDecoupled;

  [KSPField]
  public bool staged = true;

  [KSPField]
  public string explosiveNodeID = "top";

  private AttachNode explosiveNode;
  private FXGroup fx;

  public override void OnStart(StartState state) {
    base.OnStart(state);
    if (explosiveNodeID == "srf")
      explosiveNode = part.srfAttachNode;
    else
      explosiveNode = part.FindAttachNode(explosiveNodeID);
    fx = part.findFxGroup("decouple");

    if (part.stagingIcon == string.Empty) {
      part.stagingIcon = explosiveNodeID == "srf" ? "DECOUPLER_HOR" : "DECOUPLER_VERT";
    }
  }

  public override void OnActive() {
    if (staged) Decouple();
  }

  private void Decouple() {
    if (isDecoupled) return;

    fx?.Burst();

    if (explosiveNode?.attachedPart != null) {
      var target = explosiveNode.attachedPart;
      if (target == part.parent)
        part.decouple();
      else
        target.decouple();

      var force = ejectionForce * 0.5f;
      var dir = Vector3.Normalize(part.transform.position - target.transform.position);
      StartCoroutine(ApplyEjectionForce(2, part, dir * force, target, -dir * force));
    }

    GameEvents.onStageSeparation.Fire(
      new EventReport(FlightEvents.STAGESEPARATION, part, null, null, 0));
    isDecoupled = true;
    stagingEnabled = false;
  }

  private IEnumerator ApplyEjectionForce(int frameDelay, Part a, Vector3 forceA, Part b, Vector3 forceB) {
    for (int i = 0; i < frameDelay; i++)
      yield return null;
    if (a != null) a.AddForce(forceA);
    if (b != null) b.AddForce(forceB);
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
    if (explosiveNode?.attachedPart != null)
      decoupledParts.Add(explosiveNode.attachedPart);
    return true;
  }

  public bool IsEnginePlate() => false;
}

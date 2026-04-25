using HarmonyLib;
using Nova;
using UnityEngine;

namespace Nova.Patches;

/// <summary>
/// Temporary instrumentation patches to trace vessel creation, joint
/// creation, unpacking, and collision timing. Remove after debugging.
/// </summary>
[HarmonyPatch]
public static class VesselDebugPatches {

  // --- Part lifecycle ---

  // Note: Part.Start is a coroutine (IEnumerator), can't easily postfix it.
  // Instead, patch ModulesOnStart which Part.Start calls for module init.
  [HarmonyPostfix]
  [HarmonyPatch(typeof(Part), "ModulesOnStart")]
  static void Part_ModulesOnStart_Postfix(Part __instance) {
    var hasRb = __instance.rb != null;
    var hasJoint = __instance.attachJoint != null;
    NovaLog.Log($"[VesselDebug] ModulesOnStart: {__instance.partInfo?.name} vessel={__instance.vessel?.vesselName} hasRb={hasRb} hasJoint={hasJoint} frame={Time.frameCount}");
  }

  [HarmonyPostfix]
  [HarmonyPatch(typeof(Part), "CreateAttachJoint")]
  static void Part_CreateAttachJoint_Postfix(Part __instance, AttachModes mode) {
    var hasJoint = __instance.attachJoint != null;
    NovaLog.Log($"[VesselDebug] CreateAttachJoint: {__instance.partInfo?.name} mode={mode} parent={__instance.parent?.partInfo?.name} hasJoint={hasJoint} frame={Time.frameCount}");
  }

  // --- Pack/Unpack ---

  [HarmonyPostfix]
  [HarmonyPatch(typeof(Part), "Unpack")]
  static void Part_Unpack_Postfix(Part __instance) {
    var hasRb = __instance.rb != null;
    var hasJoint = __instance.attachJoint != null;
    NovaLog.Log($"[VesselDebug] Part.Unpack: {__instance.partInfo?.name} hasRb={hasRb} hasJoint={hasJoint} frame={Time.frameCount}");
  }

  [HarmonyPostfix]
  [HarmonyPatch(typeof(Vessel), "GoOffRails")]
  static void Vessel_GoOffRails_Postfix(Vessel __instance) {
    NovaLog.Log($"[VesselDebug] GoOffRails: {__instance.vesselName} packed={__instance.packed} parts={__instance.parts?.Count} frame={Time.frameCount}");
  }

  // --- Collision ---

  [HarmonyPrefix]
  [HarmonyPatch(typeof(Part), "explode", new System.Type[0])]
  static void Part_Explode_Prefix(Part __instance) {
    NovaLog.Log($"[VesselDebug] Part.explode: {__instance.partInfo?.name} vessel={__instance.vessel?.vesselName} frame={Time.frameCount}");
  }

  // --- Collision ignores ---

  [HarmonyPostfix]
  [HarmonyPatch(typeof(Part), "SetCollisionIgnores")]
  static void Part_SetCollisionIgnores_Postfix(Part __instance) {
    NovaLog.Log($"[VesselDebug] SetCollisionIgnores: {__instance.partInfo?.name} vessel={__instance.vessel?.vesselName} sig={__instance.physicalSignificance} frame={Time.frameCount}");
  }

  // --- Velocity ---

  [HarmonyPostfix]
  [HarmonyPatch(typeof(Part), "ResumeVelocity")]
  static void Part_ResumeVelocity_Postfix(Part __instance) {
    if (__instance.rb == null) return;
    var orbit = __instance.orbit;
    var orbVel = orbit?.GetVel() ?? Vector3d.zero;
    var rfrmVel = (orbit?.referenceBody?.inverseRotation == true)
      ? orbit.referenceBody.getRFrmVel(__instance.partTransform.position)
      : Vector3d.zero;
    var frameVel = Krakensbane.GetFrameVelocity();
    var rbVel = __instance.rb.velocity;
    var landed = __instance.vessel?.LandedOrSplashed ?? false;
    NovaLog.Log($"[VesselDebug] ResumeVelocity: {__instance.partInfo?.name} " +
      $"landed={landed} orbVel=({orbVel.x:F1},{orbVel.y:F1},{orbVel.z:F1}) " +
      $"rfrmVel=({rfrmVel.x:F1},{rfrmVel.y:F1},{rfrmVel.z:F1}) " +
      $"frameVel=({frameVel.x:F1},{frameVel.y:F1},{frameVel.z:F1}) " +
      $"rb.vel=({rbVel.x:F1},{rbVel.y:F1},{rbVel.z:F1}) frame={Time.frameCount}");
  }

  // --- Active vessel ---

  [HarmonyPrefix]
  [HarmonyPatch(typeof(FlightGlobals), "ForceSetActiveVessel")]
  static void ForceSetActiveVessel_Prefix(Vessel v) {
    NovaLog.Log($"[VesselDebug] ForceSetActiveVessel: {v?.vesselName ?? "null"} frame={Time.frameCount}");
  }

  [HarmonyPrefix]
  [HarmonyPatch(typeof(Vessel), "MakeActive")]
  static void Vessel_MakeActive_Prefix(Vessel __instance) {
    NovaLog.Log($"[VesselDebug] MakeActive: {__instance.vesselName} loaded={__instance.loaded} packed={__instance.packed} frame={Time.frameCount}");
  }
}

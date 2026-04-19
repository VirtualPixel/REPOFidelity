using HarmonyLib;
using UnityEngine;

namespace REPOFidelity.Patches;

// ---
// Per-frame allocation cuts in gameplay scripts. Pure NonAlloc swaps and idle-skip
// fast paths — no behavior change, so they run as long as the mod is on regardless
// of the CpuPatchesActive auto-gate.
// ---


// PhysGrabObjectGrabArea.Update calls playerGrabbers.ToList() every frame to allow
// safe iteration over the grabber list while modifying others. When no one's grabbing,
// the .ToList() still allocates an empty List per instance per frame. Skip the whole
// Update when this object has no grabbers and no stale grabber bookkeeping — the
// original cleanup loops have nothing to do in that state.
[HarmonyPatch(typeof(PhysGrabObjectGrabArea), "Update")]
static class GrabAreaIdleSkipPatch
{
    static bool Prefix(PhysGrabObjectGrabArea __instance)
    {
        if (!Settings.ModEnabled || !Settings.AllocationFixesEnabled) return true;

        // vanilla bails immediately for non-master clients — match that early-out
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return false;

        var grabbing = __instance.physGrabObject != null
            ? __instance.physGrabObject.playerGrabbing
            : __instance.staticGrabObject?.playerGrabbing;

        if (grabbing != null && grabbing.Count > 0) return true;
        if (__instance.listOfAllGrabbers.Count > 0) return true;

        // both top-level lists empty → no grab activity, no cleanup needed
        return false;
    }
}


// AudioListenerFollow.Update rebuilds LayerMask.GetMask(new string[] { "LowPassTrigger" })
// and allocates a Collider[] from OverlapSphere on every 15Hz tick. Single instance,
// but constant for the whole session. Cache the mask, swap OverlapSphere to NonAlloc.
[HarmonyPatch(typeof(AudioListenerFollow), "Update")]
static class AudioListenerFollowNonAllocPatch
{
    static readonly Collider[] _buf = new Collider[8];
    static int _triggerMask = -1;

    static bool Prefix(AudioListenerFollow __instance)
    {
        if (!Settings.ModEnabled || !Settings.AllocationFixesEnabled) return true;
        if (!__instance.TargetPositionTransform) return false;

        bool deathCam = SpectateCamera.instance &&
                        SpectateCamera.instance.CheckState(SpectateCamera.State.Death);
        var posT = __instance.TargetPositionTransform;
        if (deathCam)
        {
            __instance.transform.position = posT.position;
        }
        else
        {
            __instance.transform.position = posT.position +
                posT.forward * AssetManager.instance.mainCamera.nearClipPlane;
        }

        var rotT = __instance.TargetRotationTransform;
        if (!rotT) return false;
        __instance.transform.rotation = rotT.rotation;

        if (!SemiFunc.FPSImpulse15()) return false;

        if (_triggerMask < 0) _triggerMask = LayerMask.GetMask("LowPassTrigger");
        __instance.lowPassTrigger = null;
        int count = Physics.OverlapSphereNonAlloc(
            __instance.transform.position, 0.1f, _buf, _triggerMask,
            QueryTriggerInteraction.Collide);
        if (count > 0)
        {
            var hit = _buf[0];
            __instance.lowPassTrigger = hit.GetComponent<LowPassTrigger>()
                ?? hit.GetComponentInParent<LowPassTrigger>();
        }
        return false;
    }
}

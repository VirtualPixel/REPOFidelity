using HarmonyLib;
using UnityEngine;

namespace REPOFidelity.Patches;

// ---
// Per-frame allocations from gameplay scripts that aren't gated by CpuPatchesActive.
// These are pure NonAlloc swaps + idle-skip fast paths — no behavior change, so they
// run as long as the mod is on, regardless of the CPU-patch auto-gate.
//
// Note: the AudioLowPassLogic.CheckLogic full-method-replacement was tried in an
// earlier 1.5.2 iteration and reverted. Even though it removed real allocations,
// Harmony Prefix dispatch overhead on a 4Hz × 170-instance call site exceeded the
// allocation savings on fast CPUs (5090/9950x A/B sample showed -0.14ms with the
// patch). NonAlloc swaps via Harmony are only a net win when the original method
// is expensive enough to amortize the dispatch — CheckLogic is too cheap per call.
// ---


// PhysGrabObjectGrabArea.Update calls playerGrabbers.ToList() every frame to allow
// safe iteration over the grabber list while modifying others. When no one's grabbing,
// the .ToList() still allocates an empty List per instance per frame — 11 instances
// in a typical scene is ~660 throwaway lists/sec straight into gen0.
// Fast-path: skip the whole Update when this object has no grabbers and no stale
// grabber bookkeeping. The original cleanup loops have nothing to do in that state.
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


// AudioListenerFollow runs once per frame — single instance, but allocates a
// `new string[] { "LowPassTrigger" }` for LayerMask.GetMask plus a Collider[]
// from OverlapSphere on every check tick (15Hz). Pennies on the dollar individually
// but live for the whole session, so a constant gen0 drip.
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

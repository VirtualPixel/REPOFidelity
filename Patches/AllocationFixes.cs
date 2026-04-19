using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace REPOFidelity.Patches;

// ---
// Per-frame allocations from gameplay scripts that aren't gated by CpuPatchesActive.
// These are pure NonAlloc swaps + idle-skip fast paths — no behavior change, so they
// run as long as the mod is on, regardless of the CPU-patch auto-gate. Targeted at
// the GC pressure that produces gen1 stop-the-world pauses on busy procedural maps
// (the ~40ms hitch every ~2-3s pattern).
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


// AudioLowPassLogic.CheckLogic runs in a 0.25s coroutine per instance. With 170+
// instances on a busy scene that's ~700 calls/sec, each one allocating a fresh
// Collider[] (OverlapSphere), a fresh RaycastHit[] (RaycastAll), and a fresh
// List<Collider> when the player is within 20m. Cumulatively this is the largest
// per-frame allocator we found (50–100 KB/s on heavy maps).
// Full method replacement using shared NonAlloc buffers and a reused list.
// Logic mirrors AudioLowPassLogic.CheckLogic in R.E.P.O. 0.3.x.
[HarmonyPatch(typeof(AudioLowPassLogic), "CheckLogic")]
static class AudioLowPassNonAllocPatch
{
    // ray buffer at 64 — vanilla RaycastAll is uncapped, and dense PhysGrabObject piles
    // along a 20m ray could exceed a smaller cap and silently drop wall hits, which would
    // leave LowPass=false when vanilla would set it true (audible regression)
    static readonly Collider[] _overlapBuf = new Collider[16];
    static readonly RaycastHit[] _rayBuf = new RaycastHit[64];
    static readonly List<Collider> _wallOverlap = new(8);

    static bool Prefix(AudioLowPassLogic __instance)
    {
        if (!Settings.ModEnabled || !Settings.AllocationFixesEnabled) return true;

        __instance.LowPass = true;
        bool spectateDeath = SpectateCamera.instance &&
                             SpectateCamera.instance.CheckState(SpectateCamera.State.Death);

        var listener = __instance.audioListener;
        var src = __instance.AudioSource;

        if (!listener || !src || src.spatialBlend <= 0f || spectateDeath)
        {
            __instance.LowPass = false;
        }
        else
        {
            Vector3 dir = listener.position - __instance.transform.position;
            // 20m gate matches vanilla — squared distance is faster than .magnitude
            if (dir.sqrMagnitude < 400f)
            {
                __instance.LowPass = false;
                LowPassTrigger? overlapTrigger = null;

                int overlapCount = Physics.OverlapSphereNonAlloc(
                    __instance.transform.position, 0.1f, _overlapBuf,
                    __instance.LayerMaskOverlap, QueryTriggerInteraction.Collide);

                _wallOverlap.Clear();
                for (int i = 0; i < overlapCount; i++)
                {
                    var hit = _overlapBuf[i];
                    if (hit.transform.CompareTag("LowPassTrigger"))
                    {
                        overlapTrigger = hit.GetComponent<LowPassTrigger>();
                        if (!overlapTrigger)
                            overlapTrigger = hit.GetComponentInParent<LowPassTrigger>();
                        if (overlapTrigger) break;
                    }
                    if (hit.transform.CompareTag("Wall"))
                        _wallOverlap.Add(hit);
                }

                var listenerTrigger = AudioListenerFollow.instance.lowPassTrigger;
                if (listenerTrigger)
                {
                    __instance.LowPass = listenerTrigger != overlapTrigger;
                }
                else if (overlapTrigger)
                {
                    __instance.LowPass = true;
                }
                else
                {
                    int rayCount = Physics.RaycastNonAlloc(
                        __instance.transform.position, dir, _rayBuf, dir.magnitude,
                        __instance.LayerMask, QueryTriggerInteraction.Collide);

                    for (int i = 0; i < rayCount; i++)
                    {
                        var hit = _rayBuf[i];
                        if (!hit.collider.transform.CompareTag("Wall")) continue;

                        bool overlapsExisting = false;
                        for (int j = 0; j < _wallOverlap.Count; j++)
                        {
                            if (_wallOverlap[j].transform == hit.collider.transform)
                            { overlapsExisting = true; break; }
                        }
                        if (overlapsExisting) continue;

                        bool ignored = false;
                        var ignoreList = __instance.LowPassIgnoreColliders;
                        for (int k = 0; k < ignoreList.Count; k++)
                        {
                            var ic = ignoreList[k];
                            if (ic && ic.transform == hit.collider.transform)
                            { ignored = true; break; }
                        }
                        if (!ignored)
                        {
                            __instance.LowPass = true;
                            break;
                        }
                    }
                }
            }
        }

        if (__instance.First && src)
        {
            bool active = __instance.LowPass;
            var filter = __instance.AudioLowpassFilter;
            float falloff = __instance.Falloff;
            float falloffMul = __instance.FalloffMultiplier;
            float volume = __instance.Volume;
            float volumeMul = __instance.VolumeMultiplier;

            if (active)
            {
                filter.cutoffFrequency = __instance.LowPassMin;
                src.maxDistance = falloff * falloffMul;
                src.volume = volume * volumeMul;
            }
            else
            {
                filter.cutoffFrequency = __instance.LowPassMax;
                src.maxDistance = falloff;
                src.volume = volume;
            }
            __instance.First = false;
        }

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

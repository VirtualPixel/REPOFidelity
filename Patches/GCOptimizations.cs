using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace REPOFidelity.Patches;

// ---
// shared NonAlloc physics buffers — replaces the array
// allocations that Physics.SphereCastAll / RaycastAll /
// OverlapSphere do every call
// ---

static class PhysicsBuffers
{
    internal static readonly RaycastHit[] Hits = new RaycastHit[64];
    internal static readonly RaycastHit[] Hits2 = new RaycastHit[32];
    internal static readonly Collider[] Overlaps = new Collider[32];

    static int _physGrabMask = -1;
    internal static int PhysGrabMask
    {
        get
        {
            if (_physGrabMask < 0)
                _physGrabMask = LayerMask.GetMask("PhysGrabObject");
            return _physGrabMask;
        }
    }
}

// ---
// PhysGrabber discovery loop — the original SphereCastAll +
// OverlapSphere + SphereCastAll/RaycastAll path runs 50x/sec
// and allocates 3-4 arrays per call. run our NonAlloc version
// before the original method, then let the rest (grab logic,
// climb logic) execute normally.
// ---

[HarmonyPatch(typeof(PhysGrabber), "RayCheck")]
static class RayCheckDiscoveryPatch
{
    // run the discovery scan with NonAlloc before the original method.
    // the original method's discovery loop will still run but finding
    // no undiscovered valuables (we already discovered them) so it
    // effectively becomes a no-op pass through empty results.
    // this isn't perfect but it's safe — grab and climb logic is untouched.
    static void Prefix(PhysGrabber __instance, bool _grab)
    {
        if (!Settings.GcPatchesActive) return;
        if (_grab) return;
        if (__instance.playerAvatar.isDisabled || __instance.playerAvatar.deadSet) return;

        var cam = __instance.playerCamera.transform;
        int count = Physics.SphereCastNonAlloc(cam.position, 1f, cam.forward,
            PhysicsBuffers.Hits, 10f, __instance.mask, QueryTriggerInteraction.Collide);

        for (int i = 0; i < count; i++)
        {
            var valuable = PhysicsBuffers.Hits[i].transform.GetComponent<ValuableObject>();
            if (!valuable) continue;

            if (valuable.discovered && !valuable.discoveredReminder) continue;

            Vector3 point = PhysicsBuffers.Hits[i].point;

            // occlusion check
            int ov = Physics.OverlapSphereNonAlloc(point, 0.01f, PhysicsBuffers.Overlaps, __instance.mask);
            bool blocked = false;
            for (int j = 0; j < ov; j++)
            {
                if (PhysicsBuffers.Overlaps[j].transform.GetComponentInParent<ValuableObject>() != valuable)
                { blocked = true; break; }
            }
            if (blocked && valuable.physGrabObject)
                point = Vector3.MoveTowards(point, valuable.physGrabObject.centerPoint, 0.1f);

            Vector3 dir = cam.position - point;

            if (!valuable.discovered)
            {
                int vis = Physics.SphereCastNonAlloc(point, 0.01f, dir,
                    PhysicsBuffers.Hits2, dir.magnitude, __instance.mask, QueryTriggerInteraction.Collide);
                bool clear = true;
                for (int k = 0; k < vis; k++)
                {
                    if (!PhysicsBuffers.Hits2[k].transform.CompareTag("Player") &&
                        PhysicsBuffers.Hits2[k].transform != PhysicsBuffers.Hits[i].transform)
                    { clear = false; break; }
                }
                if (clear) valuable.Discover(ValuableDiscoverGraphic.State.Discover);
            }
            else if (valuable.discoveredReminder)
            {
                int vis = Physics.RaycastNonAlloc(point, dir,
                    PhysicsBuffers.Hits2, dir.magnitude, __instance.mask, QueryTriggerInteraction.Collide);
                bool clear = true;
                for (int k = 0; k < vis; k++)
                {
                    if (PhysicsBuffers.Hits2[k].collider.transform.CompareTag("Wall"))
                    { clear = false; break; }
                }
                if (clear)
                {
                    valuable.discoveredReminder = false;
                    valuable.Discover(ValuableDiscoverGraphic.State.Reminder);
                }
            }
        }
    }
}

// ForceGrabPhysObject allocates LayerMask.GetMask(new string[]) + RaycastAll
[HarmonyPatch(typeof(PhysGrabber), "ForceGrabPhysObject")]
static class ForceGrabPatch
{
    static bool Prefix(PhysGrabber __instance, PhysGrabObject _physObject)
    {
        if (!Settings.GcPatchesActive) return true;
        if (__instance.playerCamera == null || _physObject == null) return true;
        Vector3 dir = _physObject.midPoint - __instance.playerCamera.transform.position;
        int count = Physics.RaycastNonAlloc(__instance.playerCamera.transform.position,
            dir.normalized, PhysicsBuffers.Hits, dir.magnitude,
            PhysicsBuffers.PhysGrabMask, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            if (!_physObject) continue;
            var parent = PhysicsBuffers.Hits[i].collider?.GetComponentInParent<PhysGrabObject>();
            if (parent == _physObject)
            {
                if (__instance.grabbed)
                    __instance.ReleaseObject(-1, 0.1f);
                __instance.StartGrabbingPhysObject(PhysicsBuffers.Hits[i], _physObject);
                if (__instance.grabbed)
                {
                    __instance.toggleGrab = true;
                    return false;
                }
                break;
            }
        }
        return false;
    }
}


using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace REPOFidelity.Patches;

// Replaces RoomVolumeCheck.Check. Vanilla samples Physics.OverlapBox at a single point
// at 10Hz, which misses rooms entirely when the player moves faster than one tick's worth
// of travel (tumble wings, fast flight). The miss shows up as a false "not in any room"
// state — breaks ambience / truck-safety / enemy-AI room awareness for up to 100ms and
// robs the player of the scouting-points credit for rooms they crossed at speed. Four
// tiers:
//
//   1. Rest-skip. Player stationary and we had rooms last tick — keep cached state and
//      return. Zero physics calls on idle ticks.
//   2. OverlapBoxNonAlloc at current position. Common-case query for moving players.
//   3. BoxCastNonAlloc sweep from last position to current. Catches rooms whose volumes
//      the player crossed entirely between two ticks.
//   4. Sticky carry-over. If overlap and sweep both come up empty but we were in a room
//      last tick, retain that room for up to StickyMaxTicks more ticks. Handles flying
//      above the room's collider ceiling — the sweep can't help there since it starts
//      inside the volume and Unity's cast APIs skip the starting collider.
//
// Not gated behind CpuPatchesActive. Rest-skip makes the common case strictly cheaper
// than vanilla regardless of frame time (measured ~5.7x on a 5090), and the correctness
// fix applies to every player.
[HarmonyPatch(typeof(RoomVolumeCheck), "Check")]
static class RoomVolumeCheckPatch
{
    const int StickyMaxTicks = 3;

    static readonly Collider[] _overlapBuf = new Collider[32];
    static readonly RaycastHit[] _sweepBuf = new RaycastHit[32];

    static bool Prefix(RoomVolumeCheck __instance, ref IEnumerator __result)
    {
        if (!Settings.ModEnabled) return true;
        __result = OptimizedCheck(__instance);
        return false;
    }

    static IEnumerator OptimizedCheck(RoomVolumeCheck instance)
    {
        while (!LevelGenerator.Instance.Generated)
            yield return new WaitForSeconds(0.1f);
        yield return new WaitForSeconds(0.5f);

        var trav = Traverse.Create(instance);
        var player = trav.Field<PlayerAvatar>("player").Value;
        var mask = trav.Field<LayerMask>("Mask").Value;
        var playerWait = new WaitForSeconds(0.1f);
        var nonPlayerWait = new WaitForSeconds(0.5f);

        // Per-instance state via closure — hoisted to fields by the coroutine state machine.
        Vector3 lastPos = Vector3.zero;
        bool hasLastPos = false;
        int stickyTicks = 0;
        var cachedRooms = new List<RoomVolume>(4);

        for (;;)
        {
            if (instance.PauseCheckTimer > 0f)
            {
                instance.PauseCheckTimer -= 0.5f;
                yield return nonPlayerWait;
                continue;
            }

            long t = FrameTimeMeter.Begin();
            RunCheck(instance, player, mask, ref lastPos, ref hasLastPos, ref stickyTicks, cachedRooms);
            FrameTimeMeter.End(FrameTimeMeter.RoomVolumeCheck, t);

            if (!instance.Continuous) yield break;
            yield return player ? playerWait : nonPlayerWait;
        }
    }

    static void RunCheck(
        RoomVolumeCheck instance, PlayerAvatar player, LayerMask mask,
        ref Vector3 lastPos, ref bool hasLastPos, ref int stickyTicks,
        List<RoomVolume> cachedRooms)
    {
        Vector3 size = instance.currentSize;
        if (size == Vector3.zero) size = instance.transform.localScale;
        Vector3 halfSize = size * 0.5f;
        Vector3 position = instance.transform.position + instance.transform.rotation * instance.CheckPosition;
        Quaternion rotation = instance.transform.rotation;

        // Rest-skip: stationary in a known room. CurrentRooms / inTruck / inExtractionPoint
        // still hold last tick's values — just re-run the game-logic tail (idempotent).
        if (hasLastPos
            && instance.wasInRoom
            && stickyTicks == 0
            && (position - lastPos).sqrMagnitude < 0.0001f)
        {
            ApplyGameLogicTail(instance, player);
            return;
        }

        instance.inTruck = false;
        instance.inExtractionPoint = false;
        instance.CurrentRooms.Clear();

        int overlapHits = Physics.OverlapBoxNonAlloc(position, halfSize, _overlapBuf, rotation, mask);
        for (int i = 0; i < overlapHits; i++)
            AddRoom(instance, _overlapBuf[i]);

        // Sweep: catches rooms crossed entirely between last tick and this one.
        if (instance.CurrentRooms.Count == 0 && hasLastPos)
        {
            Vector3 delta = position - lastPos;
            float dist = delta.magnitude;
            if (dist > 0.01f)
            {
                int sweepHits = Physics.BoxCastNonAlloc(
                    lastPos, halfSize, delta / dist, _sweepBuf, rotation, dist, mask);
                for (int i = 0; i < sweepHits; i++)
                    AddRoom(instance, _sweepBuf[i].collider);
            }
        }

        // Sticky: flew above the room's collider ceiling. BoxCast starts inside the volume
        // so it can't catch it, but conceptually the player is still in the room.
        bool usedSticky = false;
        if (instance.CurrentRooms.Count == 0 && instance.wasInRoom && stickyTicks < StickyMaxTicks)
        {
            for (int i = 0; i < cachedRooms.Count; i++)
            {
                var rv = cachedRooms[i];
                if (rv == null) continue;
                instance.CurrentRooms.Add(rv);
                if (rv.Truck) instance.inTruck = true;
                if (rv.Extraction) instance.inExtractionPoint = true;
            }
            if (instance.CurrentRooms.Count > 0)
            {
                stickyTicks++;
                usedSticky = true;
            }
        }
        if (!usedSticky) stickyTicks = 0;

        // Cache current rooms for future sticky fallback. Skip if we just read the cache —
        // nothing new to record.
        if (!usedSticky && instance.CurrentRooms.Count > 0)
        {
            cachedRooms.Clear();
            for (int i = 0; i < instance.CurrentRooms.Count; i++)
                cachedRooms.Add(instance.CurrentRooms[i]);
        }

        ApplyGameLogicTail(instance, player);

        lastPos = position;
        hasLastPos = true;
    }

    static void AddRoom(RoomVolumeCheck instance, Collider col)
    {
        if (col == null) return;
        var rv = col.transform.GetComponent<RoomVolume>();
        if (!rv) rv = col.transform.GetComponentInParent<RoomVolume>();
        if (rv && !instance.CurrentRooms.Contains(rv))
        {
            instance.CurrentRooms.Add(rv);
            if (rv.Truck) instance.inTruck = true;
            if (rv.Extraction) instance.inExtractionPoint = true;
        }
    }

    static void ApplyGameLogicTail(RoomVolumeCheck instance, PlayerAvatar player)
    {
        if (player && instance.CurrentRooms.Count > 0)
        {
            bool sameMap = true;
            MapModule m = instance.CurrentRooms[0].MapModule;
            for (int i = 1; i < instance.CurrentRooms.Count; i++)
            {
                if (m != instance.CurrentRooms[i].MapModule) { sameMap = false; break; }
            }
            if (sameMap) instance.CurrentRooms[0].SetExplored();
        }

        instance.wasInRoom = instance.CurrentRooms.Count > 0;
    }
}

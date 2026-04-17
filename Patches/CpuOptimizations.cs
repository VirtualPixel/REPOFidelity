using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace REPOFidelity.Patches;

[HarmonyPatch(typeof(EnemyDirector), "Update")]
static class EnemyDirectorThrottlePatch
{
    static int _frameSkip;
    static float _cachedActionDelta;

    // EnemyDirector is a singleton — these statics are safe
    static bool Prefix(EnemyDirector __instance)
    {
        if (!Settings.ModEnabled || !Settings.CpuPatchesActive) return true;

        long t = FrameTimeMeter.Begin();

        if (LevelGenerator.Instance.Generated && __instance.spawnIdlePauseTimer > 0f)
        {
            bool allUsed = true;
            foreach (var ep in __instance.enemiesSpawned)
            {
                if (ep && !ep.firstSpawnPointUsed)
                { allUsed = false; break; }
            }
            if (allUsed)
                __instance.spawnIdlePauseTimer -= Time.deltaTime;
            if (__instance.debugNoSpawnIdlePause)
                __instance.spawnIdlePauseTimer = 0f;
        }

        __instance.despawnedDecreaseTimer -= Time.deltaTime;
        if (__instance.despawnedDecreaseTimer <= 0f)
        {
            __instance.despawnedDecreaseMultiplier -= __instance.despawnedDecreasePercent;
            if (__instance.despawnedDecreaseMultiplier < 0f)
                __instance.despawnedDecreaseMultiplier = 0f;
            __instance.despawnedDecreaseTimer = 60f * __instance.despawnedDecreaseMinutes;
        }

        if (RoundDirector.instance.allExtractionPointsCompleted)
        {
            foreach (var ep in __instance.enemiesSpawned)
            {
                if (ep && ep.DespawnedTimer > 30f)
                    ep.DespawnedTimerSet(0f, false);
            }
            RunExtractionLogic(__instance);
        }

        // room comparison — only refresh every 3rd frame, accumulation rate unchanged
        _frameSkip = (_frameSkip + 1) % 3;
        if (_frameSkip == 0)
        {
            float num = 0f;
            foreach (var ep in __instance.enemiesSpawned)
            {
                if (!ep || !ep.Spawned || !ep.playerClose || ep.forceLeave) continue;
                if (CheckRoomOverlap(ep.currentRooms))
                {
                    float w = ep.difficulty switch
                    {
                        EnemyParent.Difficulty.Difficulty3 => 2f,
                        EnemyParent.Difficulty.Difficulty2 => 1f,
                        _ => 0.5f
                    };
                    num += w * ep.actionMultiplier;
                }
            }
            _cachedActionDelta = num;
        }

        if (_cachedActionDelta > 0f)
            __instance.enemyActionAmount += _cachedActionDelta * Time.deltaTime;
        else
        {
            __instance.enemyActionAmount -= 0.1f * Time.deltaTime;
            __instance.enemyActionAmount = Mathf.Max(0f, __instance.enemyActionAmount);
        }

        float threshold = __instance.debugShortActionTimer ? 5f : 120f;
        if (__instance.enemyActionAmount > threshold)
        {
            __instance.enemyActionAmount = 0f;
            LevelPoint lp = SemiFunc.LevelPointGetFurthestFromPlayer(__instance.transform.position, 5f);
            if (lp) __instance.SetInvestigate(lp.transform.position, float.MaxValue, true);
            if (RoundDirector.instance.allExtractionPointsCompleted &&
                __instance.extractionsDoneState == EnemyDirector.ExtractionsDoneState.PlayerRoom)
                __instance.investigatePointTimer = 60f;
            foreach (var ep in __instance.enemiesSpawned)
            {
                if (ep && ep.Spawned) ep.forceLeave = true;
            }
        }

        FrameTimeMeter.End(FrameTimeMeter.EnemyDirector, t);
        return false;
    }

    static void RunExtractionLogic(EnemyDirector inst)
    {
        if (inst.investigatePointTimer <= 0f)
        {
            if (inst.extractionsDoneState == EnemyDirector.ExtractionsDoneState.StartRoom)
            {
                inst.enemyActionAmount = 0f;
                inst.despawnedDecreaseMultiplier = 0f;
                if (inst.extractionDoneStateImpulse)
                {
                    inst.extractionDoneStateTimer = 10f;
                    inst.extractionDoneStateImpulse = false;
                    foreach (var ep in inst.enemiesSpawned)
                    {
                        if (ep && ep.Spawned && !ep.playerClose)
                        { ep.SpawnedTimerPause(0f); ep.SpawnedTimerSet(0f); }
                    }
                }
                inst.investigatePointTimer = inst.investigatePointTime;
                var list = SemiFunc.LevelPointsGetInStartRoom();
                if (list.Count > 0)
                    SemiFunc.EnemyInvestigate(list[Random.Range(0, list.Count)].transform.position, 100f, true);
                inst.extractionDoneStateTimer -= inst.investigatePointTime;
                if (inst.extractionDoneStateTimer <= 0f)
                    inst.extractionsDoneState = EnemyDirector.ExtractionsDoneState.PlayerRoom;
            }
            else
            {
                var list2 = SemiFunc.LevelPointsGetInPlayerRooms();
                if (list2.Count > 0)
                    SemiFunc.EnemyInvestigate(list2[Random.Range(0, list2.Count)].transform.position, 100f, true);
                inst.investigatePointTimer = inst.investigatePointTime;
                inst.investigatePointTime = Mathf.Min(inst.investigatePointTime + 2f, 30f);
            }
        }
        else
        {
            inst.investigatePointTimer -= Time.deltaTime;
        }
    }

    static bool CheckRoomOverlap(List<RoomVolume> enemyRooms)
    {
        foreach (var player in SemiFunc.PlayerGetList())
            foreach (var px in player.RoomVolumeCheck.CurrentRooms)
                foreach (var ey in enemyRooms)
                    if (px == ey) return true;
        return false;
    }

    internal static void ResetCache() => _cachedActionDelta = 0f;
}

// NonAlloc OverlapBox replacement. shared _buf is safe because coroutines
// process synchronously within each step before yielding.
[HarmonyPatch(typeof(RoomVolumeCheck), "Check")]
static class RoomVolumeNonAllocPatch
{
    static readonly Collider[] _buf = new Collider[32];

    static bool Prefix(RoomVolumeCheck __instance, ref IEnumerator __result)
    {
        if (!Settings.ModEnabled || !Settings.CpuPatchesActive) return true;
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

        for (;;)
        {
            if (instance.PauseCheckTimer > 0f)
            {
                instance.PauseCheckTimer -= 0.5f;
                yield return nonPlayerWait;
                continue;
            }

            long t = FrameTimeMeter.Begin();
            CheckSetNonAlloc(instance, player, mask);
            FrameTimeMeter.End(FrameTimeMeter.RoomVolumeCheck, t);

            if (!instance.Continuous) yield break;
            yield return player ? playerWait : nonPlayerWait;
        }
    }

    static void CheckSetNonAlloc(RoomVolumeCheck instance, PlayerAvatar player, LayerMask mask)
    {
        instance.inTruck = false;
        instance.inExtractionPoint = false;
        instance.CurrentRooms.Clear();

        Vector3 size = instance.currentSize;
        if (size == Vector3.zero) size = instance.transform.localScale;

        int count = Physics.OverlapBoxNonAlloc(
            instance.transform.position + instance.transform.rotation * instance.CheckPosition,
            size / 2f, _buf, instance.transform.rotation, mask);

        for (int i = 0; i < count; i++)
        {
            var rv = _buf[i].transform.GetComponent<RoomVolume>();
            if (!rv) rv = _buf[i].transform.GetComponentInParent<RoomVolume>();
            if (rv && !instance.CurrentRooms.Contains(rv))
            {
                instance.CurrentRooms.Add(rv);
                if (rv.Truck) instance.inTruck = true;
                if (rv.Extraction) instance.inExtractionPoint = true;
            }
        }

        if (player && instance.CurrentRooms.Count > 0)
        {
            bool same = true;
            MapModule m = instance.CurrentRooms[0].MapModule;
            for (int i = 1; i < instance.CurrentRooms.Count; i++)
                if (m != instance.CurrentRooms[i].MapModule) { same = false; break; }
            if (same) instance.CurrentRooms[0].SetExplored();
        }

        instance.wasInRoom = instance.CurrentRooms.Count > 0;
    }
}

// NonAlloc + 0.25s per-enemy result cache. shared _buf same safety as above.
[HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.EnemyGetNearestPhysObject))]
static class SemiFuncCachePatch
{
    static readonly Collider[] _buf = new Collider[32];
    static int _mask = -1; // -1 = not yet cached, valid masks are >= 0
    static readonly Dictionary<int, (Vector3 pos, float time)> _cache = new();

    static bool Prefix(Enemy enemy, ref Vector3 __result)
    {
        if (!Settings.ModEnabled || !Settings.CpuPatchesActive) return true;

        long t = FrameTimeMeter.Begin();

        int id = enemy.GetInstanceID();
        float now = Time.time;
        if (_cache.TryGetValue(id, out var cached) && now - cached.time < 0.25f)
        {
            __result = cached.pos;
            FrameTimeMeter.End(FrameTimeMeter.SemiFuncCache, t);
            return false;
        }

        if (_mask < 0) _mask = LayerMask.GetMask("PhysGrabObject");
        int count = Physics.OverlapSphereNonAlloc(enemy.CenterTransform.position, 3f, _buf, _mask);

        PhysGrabObject? nearest = null;
        float best = 9999f;
        for (int i = 0; i < count; i++)
        {
            var pgo = _buf[i].GetComponentInParent<PhysGrabObject>();
            if (!pgo || pgo.GetComponent<EnemyRigidbody>()) continue;
            float d = Vector3.Distance(enemy.CenterTransform.position, pgo.centerPoint);
            if (d < best) { best = d; nearest = pgo; }
        }

        __result = nearest != null ? nearest.centerPoint : Vector3.zero;
        _cache[id] = (__result, now);
        FrameTimeMeter.End(FrameTimeMeter.SemiFuncCache, t);
        return false;
    }

    internal static void ClearCache() => _cache.Clear();
}

// pre-clean playerGrabbing list with backward iteration to fix the
// vanilla forward-loop RemoveAt bug that skips elements after removal
[HarmonyPatch(typeof(PhysGrabObject), "Update")]
static class PhysGrabObjectFixPatch
{
    // cached Rigidbody lookup — avoids GetComponent per Update tick
    static readonly Dictionary<PhysGrabObject, Rigidbody?> _rbCache = new();

    internal static void ClearRbCache() => _rbCache.Clear();

    static bool Prefix(PhysGrabObject __instance)
    {
        if (!Settings.ModEnabled || !Settings.CpuPatchesActive) return true;

        // sleeping and not grabbed → Update is busywork, skip it
        if (!__instance.grabbed)
        {
            if (!_rbCache.TryGetValue(__instance, out var rb))
            {
                rb = __instance.GetComponent<Rigidbody>();
                _rbCache[__instance] = rb;
            }
            if (rb != null && rb.IsSleeping()) return false;
            return true;
        }

        long t = FrameTimeMeter.Begin();
        for (int i = __instance.playerGrabbing.Count - 1; i >= 0; i--)
        {
            var g = __instance.playerGrabbing[i];
            if (!g || !g.grabbed) __instance.playerGrabbing.RemoveAt(i);
        }
        FrameTimeMeter.End(FrameTimeMeter.PhysGrabObjectFix, t);
        return true;
    }
}

// replace allocation-heavy dead-light cleanup with backward-iteration removal
[HarmonyPatch(typeof(LightManager), "UpdateLights")]
static class LightManagerBatchPatch
{
    static bool Prefix(LightManager __instance)
    {
        if (!Settings.ModEnabled || !Settings.CpuPatchesActive) return true;

        long t = FrameTimeMeter.Begin();

        __instance.lastCheckPos = __instance.lightCullTarget.position;

        var lights = __instance.propLights;
        for (int i = lights.Count - 1; i >= 0; i--)
        {
            var pl = lights[i];
            if (pl) __instance.HandleLightActivation(pl);
            else lights.RemoveAt(i);
        }

        var emissions = __instance.propEmissions;
        for (int i = emissions.Count - 1; i >= 0; i--)
        {
            var pe = emissions[i];
            if (pe) __instance.HandleEmissionActivation(pe);
            else emissions.RemoveAt(i);
        }

        FrameTimeMeter.End(FrameTimeMeter.LightManagerBatch, t);
        return false;
    }
}

[HarmonyPatch(typeof(LevelGenerator), "GenerateDone")]
static class ClearCpuCachesOnLevel
{
    static void Postfix()
    {
        SemiFuncCachePatch.ClearCache();
        EnemyDirectorThrottlePatch.ResetCache();
        PlayerCosmeticThrottle.ResetCamera();
    }
}

// PlayerAvatarEyelids, PlayerExpression, and PlayerAvatarOverchargeVisuals all
// tick per-frame per-player. In a 20-player game that's 60 Update calls computing
// eyelid rotations and blendshape springs for visuals nobody can see when the
// player is past fog end. Skip the whole Update in that case.
static class PlayerCosmeticThrottle
{
    static Camera? _cam;

    internal static bool ShouldSkip(Transform t)
    {
        if (!Settings.CpuPatchesActive) return false;
        float fogEnd = Settings.ResolvedEffectiveFogEnd;
        if (fogEnd <= 0f) return false;

        // Camera.main changes across spectate / menu transitions. refetch any time
        // the cached reference goes null OR its backing GameObject was destroyed
        if (_cam == null || _cam.transform == null) _cam = Camera.main;
        if (_cam == null) return false;

        float cutoff = fogEnd * 1.1f;
        return (t.position - _cam.transform.position).sqrMagnitude > cutoff * cutoff;
    }

    internal static void ResetCamera() => _cam = null;
}

[HarmonyPatch(typeof(PlayerAvatarEyelids), "Update")]
static class PlayerAvatarEyelidsThrottlePatch
{
    static bool Prefix(PlayerAvatarEyelids __instance)
        => !PlayerCosmeticThrottle.ShouldSkip(__instance.transform);
}

[HarmonyPatch(typeof(PlayerExpression), "Update")]
static class PlayerExpressionThrottlePatch
{
    static bool Prefix(PlayerExpression __instance)
        => !PlayerCosmeticThrottle.ShouldSkip(__instance.transform);
}

[HarmonyPatch(typeof(PlayerAvatarOverchargeVisuals), "Update")]
static class PlayerAvatarOverchargeVisualsThrottlePatch
{
    static bool Prefix(PlayerAvatarOverchargeVisuals __instance)
        => !PlayerCosmeticThrottle.ShouldSkip(__instance.transform);
}

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace REPOFidelity.Patches;

// ---
// shadow reduction: gated by preset or custom toggles
// ---

[HarmonyPatch(typeof(ParticlePrefabExplosion), "Start")]
static class ExplosionShadowPatch
{
    static void Postfix(ParticlePrefabExplosion __instance)
    {
        if (Settings.ShouldOptimize(Settings.PerfOpt.ExplosionShadows))
            SceneOptimizer.CullExplosionLightShadow(__instance.light);
    }
}

[HarmonyPatch(typeof(ItemLight), "Start")]
static class ItemLightShadowPatch
{
    static void Postfix(ItemLight __instance)
    {
        if (Settings.ShouldOptimize(Settings.PerfOpt.ItemLightShadows))
            SceneOptimizer.CullItemLightShadow(__instance.itemLight);
    }
}

// spectate camera forces shadow distance to 90m; always cap this,
// it's wasteful on every preset for a zoomed death cam
// v0.4.0-tester: SpectateCamera's state-machine tick moved from Update() to
// LateUpdate() (Update() now only calls HeadEnergyLogic). StateDeath still
// writes shadowDistance = 90f on impulse, so LateUpdate postfix re-caps it.
[HarmonyPatch(typeof(SpectateCamera), "LateUpdate")]
static class SpectateShadowPatch
{
    static void Postfix()
    {
        if (QualitySettings.shadowDistance > Settings.ResolvedShadowDistance)
            QualitySettings.shadowDistance = Settings.ResolvedShadowDistance;
    }
}

// ---
// scene-wide optimization scans: run on level gen and
// whenever perf settings change mid-level
// ---

static class SceneOptimizer
{

    // PlayerAvatar.Start fires once per avatar, and a lobby fills in one frame, so
    // every joiner used to trigger its own scene-wide sweep back to back. Apply()
    // is ten FindObjectsOfType passes plus a watchlist rebuild that hit 5250
    // renderers on Museum in the #14 log, and six of those in a frame is six times
    // the native scan churn for one result. Callers that fire per object queue
    // instead; Plugin.LateUpdate drains the queue once.
    static bool _applyQueued;

    internal static void ApplyDeferred() => _applyQueued = true;

    internal static void TickDeferredApply()
    {
        if (!_applyQueued) return;
        Apply();
    }

    internal static void Apply()
    {
        _applyQueued = false;
        long _ftm = FrameTimeMeter.Begin();

        _shadowStrengths.Clear();
        ApplyGpuInstancing(Settings.OptimizationsActive);
        ApplyZeroIntensityShadows(Settings.OptimizationsActive);
        ApplyParticleAutoCull(Settings.OptimizationsActive && Diagnostics.ParticleAutoCull.Value);

        // Diagnostic gate rides the same argument as the auto-cull switch rather than
        // skipping the call: off means the pass restores whatever it had already done
        // and stops there, so flipping the switch mid-session actually lets go.
        ApplyParticleShadowCull(Diagnostics.ParticleShadows.Value
            && Settings.ShouldOptimize(Settings.PerfOpt.ParticleShadows));
        ApplyTinyRendererCull(Settings.ShouldOptimize(Settings.PerfOpt.TinyRendererCulling));
        ApplyAnimatedLightCull(Settings.ShouldOptimize(Settings.PerfOpt.AnimatedLightShadows));

        // scan existing lights in the scene so switching presets mid-level works
        ApplyItemLightShadowCull(Settings.ShouldOptimize(Settings.PerfOpt.ItemLightShadows));
        ApplyExplosionLightShadowCull(Settings.ShouldOptimize(Settings.PerfOpt.ExplosionShadows));
        ApplyPointLightShadowCull(Settings.ShouldOptimize(Settings.PerfOpt.PointLightShadows));

        // must run AFTER other passes: renderers they've set to Off should stay out of the watchlist
        CaptureDistanceCullWatchlist();
        CapturePlayerAvatarRenderers();
        CaptureFlashlightControllers();
        CaptureShadowBudgetWatchlist();

        // synchronous flashlight-budget revert so F10 diagnostic sees OK immediately
        // (tick-based restore would lag by up to 100ms behind the post-disable log)
        if (!Settings.ShouldOptimize(Settings.PerfOpt.FlashlightShadowBudget))
            RestoreFlashlightBudget();

        FrameTimeMeter.End(FrameTimeMeter.SceneApply, _ftm);
    }

    // per-player avatar renderers, plus force updateWhenOffscreen=false on SkinnedMesh
    // so Unity stops paying bone-matrix updates on invisible players
    static void CapturePlayerAvatarRenderers()
    {
        RestorePlayerAvatarRenderers();
        _playerAvatarRenderers.Clear();
        if (!Settings.OptimizationsActive) return;

        int smrAudit = 0;
        foreach (var avatar in Object.FindObjectsOfType<PlayerAvatar>())
        {
            // Local avatar's shadow state is owned by PlayerAvatarVisuals.ApplyLocalVisibilityBody.
            // Its early-exit gate means any write we do won't get re-asserted by the game; skip.
            if (avatar.isLocal) continue;
            foreach (var r in avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (r.shadowCastingMode == ShadowCastingMode.Off) continue;
                _playerAvatarShadowOrig[r] = r.shadowCastingMode;
                _playerAvatarRenderers.Add(r);
                if (r is SkinnedMeshRenderer smr && smr.updateWhenOffscreen)
                {
                    _playerAvatarSmrUpdateOrig[smr] = true;
                    smr.updateWhenOffscreen = false;
                    smrAudit++;
                }
            }
        }
        if (smrAudit > 0)
            Plugin.Log.LogDebug($"player avatar: forced updateWhenOffscreen=false on {smrAudit} skinned meshes");
    }

    internal static void UpdatePlayerAvatarShadowCull(Camera? cam)
    {
        if (cam == null || _playerAvatarRenderers.Count == 0) return;
        if (!Settings.OptimizationsActive) return;

        float fogEnd = Settings.ResolvedEffectiveFogEnd;
        if (fogEnd <= 0f) return;
        float cutoff = fogEnd * 1.1f;
        float cutoffSq = cutoff * cutoff;
        float hystSq = (cutoff * 0.9f) * (cutoff * 0.9f);
        var camPos = cam.transform.position;

        for (int i = 0; i < _playerAvatarRenderers.Count; i++)
        {
            var r = _playerAvatarRenderers[i];
            if (r == null) continue;
            float distSq = (r.transform.position - camPos).sqrMagnitude;
            bool isOff = r.shadowCastingMode == ShadowCastingMode.Off;
            if (isOff && distSq < hystSq
                && _playerAvatarShadowOrig.TryGetValue(r, out var orig))
                r.shadowCastingMode = orig;
            else if (!isOff && distSq > cutoffSq)
                r.shadowCastingMode = ShadowCastingMode.Off;
        }
    }

    static void RestorePlayerAvatarRenderers()
    {
        foreach (var kv in _playerAvatarShadowOrig)
            if (kv.Key != null) kv.Key.shadowCastingMode = kv.Value;
        _playerAvatarShadowOrig.Clear();
        foreach (var kv in _playerAvatarSmrUpdateOrig)
            if (kv.Key != null) kv.Key.updateWhenOffscreen = kv.Value;
        _playerAvatarSmrUpdateOrig.Clear();
    }

    // ---
    // saved-state dictionaries: each mutation records what it changed so F10
    // (mod off) or flag-off returns the scene to vanilla
    // ---

    static readonly Dictionary<MeshRenderer, ShadowCastingMode> _tinyRendererOrig = new();
    static readonly Dictionary<Light, LightShadows> _animatedLightOrig = new();
    static readonly Dictionary<Light, LightShadows> _zeroIntensityOrig = new();
    static readonly Dictionary<Material, bool> _gpuInstancingOrig = new();
    static readonly Dictionary<ParticleSystem, ParticleSystemCullingMode> _particleCullOrig = new();
    static readonly Dictionary<ParticleSystemRenderer, ShadowCastingMode> _particleShadowOrig = new();
    static readonly Dictionary<Light, LightShadows> _itemLightOrig = new();
    static readonly Dictionary<Light, LightShadows> _explosionLightOrig = new();

    // Local-avatar renderers that must stay out of our scene-wide MeshRenderer scans.
    // PlayerAvatar.playerAvatarVisuals and .flashlightController are inspector-linked
    // fields whose transforms sit OUTSIDE PlayerAvatar.transform, so walking PlayerAvatar's
    // children can't find them. Refresh before every scene scan.
    static readonly HashSet<Renderer> _localAvatarRendererSet = new();

    static void RefreshLocalAvatarRendererSet()
    {
        _localAvatarRendererSet.Clear();
        foreach (var pa in Object.FindObjectsOfType<PlayerAvatar>())
        {
            if (!pa.isLocal) continue;
            AddRenderersFromRoot(pa.playerAvatarVisuals?.transform);
            AddRenderersFromRoot(pa.flashlightController?.transform);
            var cosmetics = pa.playerAvatarVisuals?.playerCosmetics;
            if (cosmetics != null)
            {
                AddRenderersFromRoot(cosmetics.transform);
                AddRenderersFromRoot(cosmetics.playerCrown?.transform);
            }
        }
    }

    static void AddRenderersFromRoot(Transform? root)
    {
        if (root == null) return;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            if (r != null) _localAvatarRendererSet.Add(r);
    }

    // ---
    // distance-based shadow cull: small props (<2m bounds) disable shadow casting when
    // beyond fog-clamped shadow distance, re-enable when inside. runs every frame via
    // UpdateDistanceShadowCull, built once per level via CaptureDistanceCullWatchlist.
    // ---

    static readonly List<Renderer> _distanceCullWatchlist = new();
    static readonly Dictionary<Renderer, ShadowCastingMode> _distanceCullOrig = new();

    // flashlight shadow budget: only N closest flashlights keep their original shadow
    // mode, rest go to None. saves original mode per spotlight so F10 / flag-off revert
    static readonly Dictionary<Light, LightShadows> _flashlightBudgetOrig = new();
    static readonly List<FlashlightController> _flashlightControllers = new();
    static readonly List<Light> _pointLightWatchlist = new();
    static readonly Dictionary<Light, LightShadows> _pointLightShadowOrig = new();
    static readonly List<(Light light, float distSq)> _flashlightSorted = new();

    // Potato cuts everything, flashlight cast included. Other presets keep
    // the closest 4; it's the player's primary light source.
    static int ResolveFlashlightBudgetN() =>
        Settings.Preset == QualityPreset.Potato ? 0 : 4;

    // player avatar renderers: skinned meshes for each player. Distant players'
    // avatars still cast into the directional shadow map; gate that by distance.
    // updateWhenOffscreen gets forced false so Unity can skip bone updates when
    // the avatar isn't visible.
    static readonly List<Renderer> _playerAvatarRenderers = new();
    static readonly Dictionary<Renderer, ShadowCastingMode> _playerAvatarShadowOrig = new();
    static readonly Dictionary<SkinnedMeshRenderer, bool> _playerAvatarSmrUpdateOrig = new();

    static void CaptureDistanceCullWatchlist()
    {
        // restore any renderers we'd previously disabled before dropping references,
        // otherwise F10 / flag-off would orphan them in the Off state.
        RestoreDistanceCullWatchlist();
        _distanceCullWatchlist.Clear();
        _distanceCullCursor = 0;
        if (!Settings.ShouldOptimize(Settings.PerfOpt.DistanceShadowCulling)) return;

        // Mid-sized props past shadow distance have no visible contribution;
        // bump the cap above the original 2m to catch more of them. Potato
        // pulls 5m props in too.
        float boundsCap = Settings.Preset == QualityPreset.Potato ? 5f : 3f;

        RefreshLocalAvatarRendererSet();

        int count = 0;
        foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
        {
            if (r.shadowCastingMode == ShadowCastingMode.Off) continue;
            if (r.bounds.size.magnitude >= boundsCap) continue;
            if (_localAvatarRendererSet.Contains(r)) continue;
            _distanceCullWatchlist.Add(r);
            _distanceCullOrig[r] = r.shadowCastingMode;
            count++;
        }
        if (count > 0)
            Plugin.Log.LogDebug($"distance cull watchlist: {count} small renderers");
    }

    // Cursor into the watchlist: picks up where the last tick left off so a
    // 5000-renderer list takes ~5 ticks (~500ms) to fully re-evaluate instead
    // of scanning every entry on every 100ms tick.
    static int _distanceCullCursor;
    const int DistanceCullChunkSize = 1000;

    internal static void UpdateDistanceShadowCull(Camera? cam)
    {
        if (cam == null || _distanceCullWatchlist.Count == 0) return;

        // mod off or flag off: restore everything and bail
        if (!Settings.ShouldOptimize(Settings.PerfOpt.DistanceShadowCulling))
        {
            RestoreDistanceCullWatchlist();
            return;
        }

        // past fully-foggy, same yardstick as the avatar / flashlight-budget paths.
        // the old ResolvedShadowDistance × 0.7f sat inside the visible fog fade band
        // once Settings.ApplyFogClamps had already pulled shadow distance down to
        // fogEnd × 1.1, so small props popped their shadows where you could still see
        // them. Potato yanks right at fogEnd; everything else sits 10% past.
        float fogEnd = Settings.ResolvedEffectiveFogEnd;
        if (fogEnd <= 0f) return;
        float threshold = fogEnd * (Settings.Preset == QualityPreset.Potato ? 1.0f : 1.1f);
        float thresholdSq = threshold * threshold;
        float hystOn = threshold * 0.9f;
        float hystOnSq = hystOn * hystOn;
        var camPos = cam.transform.position;

        int count = _distanceCullWatchlist.Count;
        int chunk = Mathf.Min(DistanceCullChunkSize, count);
        if (_distanceCullCursor >= count) _distanceCullCursor = 0;

        for (int n = 0; n < chunk; n++)
        {
            int i = _distanceCullCursor + n;
            if (i >= count) i -= count;

            var r = _distanceCullWatchlist[i];
            if (r == null) continue;
            float distSq = (r.transform.position - camPos).sqrMagnitude;
            bool isOff = r.shadowCastingMode == ShadowCastingMode.Off;
            if (isOff && distSq < hystOnSq)
                r.shadowCastingMode = _distanceCullOrig.TryGetValue(r, out var orig) ? orig : ShadowCastingMode.On;
            else if (!isOff && distSq > thresholdSq)
                r.shadowCastingMode = ShadowCastingMode.Off;
        }

        _distanceCullCursor = (_distanceCullCursor + chunk) % count;
    }

    static void RestoreDistanceCullWatchlist()
    {
        for (int i = 0; i < _distanceCullWatchlist.Count; i++)
        {
            var r = _distanceCullWatchlist[i];
            if (r == null) continue;
            r.shadowCastingMode = _distanceCullOrig.TryGetValue(r, out var orig) ? orig : ShadowCastingMode.On;
        }
        _distanceCullOrig.Clear();
    }

    // Collect every Point light that currently casts shadows so the per-frame
    // update pass can toggle them on distance. Small short-range lights (<=3m)
    // are skipped; their cubemap cost is already trivial and the draw-call
    // churn from toggling them is worse than just letting them render.
    static void ApplyPointLightShadowCull(bool enable)
    {
        RestorePointLightShadows();
        _pointLightWatchlist.Clear();

        if (!enable) return;

        foreach (var light in Object.FindObjectsOfType<Light>())
        {
            if (light.type != LightType.Point) continue;
            if (light.shadows == LightShadows.None) continue;
            if (light.range <= 3f) continue;
            _pointLightWatchlist.Add(light);
        }
        if (_pointLightWatchlist.Count > 0)
            Plugin.Log.LogDebug($"point-light watchlist: {_pointLightWatchlist.Count} shadowed point lights");
    }

    internal static void UpdatePointLightShadowCull(Camera? cam)
    {
        if (cam == null || _pointLightWatchlist.Count == 0) return;

        if (!Settings.ShouldOptimize(Settings.PerfOpt.PointLightShadows))
        {
            RestorePointLightShadows();
            return;
        }

        // Tie the threshold to the same fog-scaled distance the rest of the
        // shadow-cull logic uses. Adding the light's own range means a 10m-range
        // lamp is kept alive until the player is 10m past the fog wall, which
        // is about when its contribution becomes invisible anyway.
        float fogEnd = Settings.ResolvedEffectiveFogEnd;
        if (fogEnd <= 0f) return;
        var camPos = cam.transform.position;

        for (int i = 0; i < _pointLightWatchlist.Count; i++)
        {
            var light = _pointLightWatchlist[i];
            if (light == null) continue;

            float off = fogEnd + light.range;
            float on  = off * 0.9f; // 10% hysteresis band to stop boundary flicker
            float distSq = (light.transform.position - camPos).sqrMagnitude;
            bool isOff = light.shadows == LightShadows.None;

            if (isOff && distSq < on * on)
            {
                if (_pointLightShadowOrig.TryGetValue(light, out var original))
                {
                    light.shadows = original;
                    _pointLightShadowOrig.Remove(light);
                }
            }
            else if (!isOff && distSq > off * off)
            {
                _pointLightShadowOrig[light] = light.shadows;
                light.shadows = LightShadows.None;
            }
        }
    }

    static void RestorePointLightShadows()
    {
        foreach (var kv in _pointLightShadowOrig)
            if (kv.Key != null) kv.Key.shadows = kv.Value;
        _pointLightShadowOrig.Clear();
    }

    // cap concurrent flashlight shadow maps. N × 2048² shadow maps at 20 players
    // destroys mid-range GPUs; limit to the 4 closest-to-camera flashlights.
    internal static void UpdateFlashlightShadowBudget(Camera? cam)
    {
        if (cam == null) return;

        if (!Settings.ShouldOptimize(Settings.PerfOpt.FlashlightShadowBudget))
        {
            RestoreFlashlightBudget();
            return;
        }

        _flashlightSorted.Clear();
        var camPos = cam.transform.position;
        for (int i = _flashlightControllers.Count - 1; i >= 0; i--)
        {
            var fl = _flashlightControllers[i];
            if (fl == null) { _flashlightControllers.RemoveAt(i); continue; }
            var light = fl.spotlight;
            if (light == null) continue;
            if (!light.isActiveAndEnabled) continue;
            float distSq = (light.transform.position - camPos).sqrMagnitude;
            _flashlightSorted.Add((light, distSq));
        }

        _flashlightSorted.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        // flashlights past fog end have no visible shadow anyway; skip them
        // before the budget check so a far-away player doesn't eat a slot
        float fogEnd = Settings.ResolvedEffectiveFogEnd;
        float fogCutoffSq = fogEnd > 0f ? (fogEnd * 1.1f) * (fogEnd * 1.1f) : float.PositiveInfinity;

        int budgetN = ResolveFlashlightBudgetN();
        int withinBudget = 0;
        for (int i = 0; i < _flashlightSorted.Count; i++)
        {
            var (light, distSq) = _flashlightSorted[i];
            bool pastFog = distSq > fogCutoffSq;
            bool slotAvailable = withinBudget < budgetN;

            if (!pastFog && slotAvailable)
            {
                withinBudget++;
                if (_flashlightBudgetOrig.TryGetValue(light, out var orig))
                {
                    light.shadows = orig;
                    _flashlightBudgetOrig.Remove(light);
                }
            }
            else if (light.shadows != LightShadows.None)
            {
                if (!_flashlightBudgetOrig.ContainsKey(light))
                    _flashlightBudgetOrig[light] = light.shadows;
                light.shadows = LightShadows.None;
            }
        }
    }

    static void RestoreFlashlightBudget()
    {
        foreach (var kv in _flashlightBudgetOrig)
            if (kv.Key != null) kv.Key.shadows = kv.Value;
        _flashlightBudgetOrig.Clear();
    }

    // Refreshed on scene load and player spawn. The per-tick shadow-budget pass
    // iterates this list instead of scanning the whole scene every 0.1s.
    static void CaptureFlashlightControllers()
    {
        _flashlightControllers.Clear();
        foreach (var fl in Object.FindObjectsOfType<FlashlightController>())
            _flashlightControllers.Add(fl);
    }

    // ---
    // shadow budget: limits how many small point lights cast shadows at once.
    // closest N to the camera get shadows with faded strength transitions.
    // ---

    // dim short-range point lights (item glows, valuables, props). Captured once
    // per level so the 100ms tick can iterate a cached list instead of walking
    // every Light in the scene; the scene-wide scan allocates on busy maps.
    static readonly List<Light> _shadowBudgetWatchlist = new();
    static readonly List<(Light light, float dist)> _budgetCandidates = new();
    static readonly Dictionary<int, float> _shadowStrengths = new();
    // shadows mode each glow light shipped with. The budget hands the closest N a
    // soft shadow whether or not the prefab had one, so "restore" has to mean this,
    // not "everything soft": F10 was leaving every glow light in the truck casting
    // when vanilla casts none of them.
    static readonly Dictionary<Light, LightShadows> _shadowBudgetOrig = new();
    const float FadeSpeed = 3f;

    internal static void ResetShadowBudget()
    {
        _shadowStrengths.Clear();
    }

    static void CaptureShadowBudgetWatchlist()
    {
        // put the previous watchlist back before rescanning, or a light the budget
        // had faded gets re-captured with the faded mode as its "original"
        RestoreManagedLights();
        _shadowBudgetWatchlist.Clear();
        if (!Settings.OptimizationsActive) return;

        foreach (var light in Object.FindObjectsOfType<Light>())
        {
            if (light.type != LightType.Point) continue;
            if (light.intensity >= 1f || light.range >= 5f) continue;
            _shadowBudgetWatchlist.Add(light);
            _shadowBudgetOrig[light] = light.shadows;
        }
        if (_shadowBudgetWatchlist.Count > 0)
            Plugin.Log.LogDebug($"shadow budget watchlist: {_shadowBudgetWatchlist.Count} item glow lights");
    }

    internal static void UpdateShadowBudget(Camera? cam)
    {
        if (cam == null) return;

        // mod / optimizations disabled: back to what the prefabs shipped
        if (!Settings.OptimizationsActive)
        {
            RestoreManagedLights();
            return;
        }

        // budget unlimited: every glow light casts
        int budget = Settings.ResolvedShadowBudget;
        if (budget <= 0)
        {
            UnlimitedManagedLights();
            return;
        }

        var camPos = cam.transform.position;
        float cullDist = Settings.ResolvedShadowDistance;
        float dt = 0.1f;

        _budgetCandidates.Clear();

        // iterate cached watchlist, drop destroyed refs in place
        for (int i = _shadowBudgetWatchlist.Count - 1; i >= 0; i--)
        {
            var light = _shadowBudgetWatchlist[i];
            if (light == null) { _shadowBudgetWatchlist.RemoveAt(i); continue; }
            if (!light.enabled || !light.gameObject.activeInHierarchy) continue;
            if (light.intensity <= 0f) continue;

            float dist = Vector3.Distance(camPos, light.transform.position);

            // beyond shadow distance: fade out
            if (dist > cullDist)
            {
                FadeLight(light, 0f, dt);
                continue;
            }

            _budgetCandidates.Add((light, dist));
        }

        // sort by distance, closest first
        _budgetCandidates.Sort((a, b) => a.dist.CompareTo(b.dist));

        for (int i = 0; i < _budgetCandidates.Count; i++)
        {
            var light = _budgetCandidates[i].light;
            float targetStrength = i < budget ? 1f : 0f;
            FadeLight(light, targetStrength, dt);
        }
    }

    static void RestoreManagedLights()
    {
        if (_shadowBudgetOrig.Count == 0) return;
        foreach (var kv in _shadowBudgetOrig)
        {
            if (kv.Key == null) continue;
            kv.Key.shadows = kv.Value;
            kv.Key.shadowStrength = 1f;
        }
        _shadowBudgetOrig.Clear();
        _shadowStrengths.Clear();
    }

    static void UnlimitedManagedLights()
    {
        if (_shadowStrengths.Count == 0) return;
        for (int i = _shadowBudgetWatchlist.Count - 1; i >= 0; i--)
        {
            var light = _shadowBudgetWatchlist[i];
            if (light == null) { _shadowBudgetWatchlist.RemoveAt(i); continue; }
            if (!light.enabled || !light.gameObject.activeInHierarchy) continue;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 1f;
        }
        _shadowStrengths.Clear();
    }

    static void FadeLight(Light light, float target, float dt)
    {
        int id = light.GetInstanceID();
        float current = _shadowStrengths.TryGetValue(id, out float c) ? c : (light.shadows != LightShadows.None ? 1f : 0f);

        // lerp toward target
        float next = Mathf.MoveTowards(current, target, FadeSpeed * dt);
        _shadowStrengths[id] = next;

        if (next <= 0.01f)
        {
            // fully faded out: disable shadow
            if (light.shadows != LightShadows.None)
                light.shadows = LightShadows.None;
            light.shadowStrength = 0f;
        }
        else
        {
            // shadows active, apply strength
            if (light.shadows == LightShadows.None)
                light.shadows = LightShadows.Soft;
            light.shadowStrength = next;
        }
    }

    static void ApplyZeroIntensityShadows(bool enable)
    {
        foreach (var kv in _zeroIntensityOrig)
            if (kv.Key != null) kv.Key.shadows = kv.Value;
        _zeroIntensityOrig.Clear();

        if (!enable) return;

        // Flashlights hit intensity=0 in the pause-menu Hidden state. Killing
        // the shadow here leaves it dead after the user turns it back on;
        // the budget owns them.
        var flashlights = new HashSet<Light>();
        foreach (var fl in Object.FindObjectsOfType<FlashlightController>())
            if (fl.spotlight != null) flashlights.Add(fl.spotlight);

        int count = 0;
        foreach (var light in Object.FindObjectsOfType<Light>())
        {
            if (flashlights.Contains(light)) continue;
            if (light.intensity <= 0f && light.shadows != LightShadows.None)
            {
                _zeroIntensityOrig[light] = light.shadows;
                light.shadows = LightShadows.None;
                count++;
            }
        }
        if (count > 0)
            Plugin.Log.LogDebug($"disabled shadows on {count} zero-intensity lights");
    }

    // Shadow casting on particle renderers: strip it when the preset optimizes, put
    // back exactly what the prefab shipped otherwise. Until 1.7.8 the "on" branch
    // flipped every Off renderer to On, so at Ultra the mod was forcing shadow
    // casting onto hundreds of systems vanilla never shadows (907 on Museum, 1316
    // on Wizard in the #14 log; the 28 it "restored" on the title screen had never
    // been touched by anything), and F10 could not take it back because nothing
    // recorded the starting state. Hundreds of mostly-culled particle systems
    // feeding shadow geometry jobs is also the mutation that lines up with the
    // JobTempAlloc leak warnings in that report, so it stays off the table.
    static void ApplyParticleShadowCull(bool enable)
    {
        foreach (var kv in _particleShadowOrig)
            if (kv.Key != null) kv.Key.shadowCastingMode = kv.Value;
        _particleShadowOrig.Clear();

        if (!enable) return;

        int count = 0;
        foreach (var psr in Object.FindObjectsOfType<ParticleSystemRenderer>())
        {
            if (psr.shadowCastingMode == ShadowCastingMode.Off) continue;
            _particleShadowOrig[psr] = psr.shadowCastingMode;
            psr.shadowCastingMode = ShadowCastingMode.Off;
            count++;
        }
        if (count > 0)
            Plugin.Log.LogDebug($"disabled shadow casting on {count} particle renderers");
    }

    // off-screen and non-emitting systems still tick every frame unless culling is explicit.
    // a typical R.E.P.O. level has 230+ systems registered and 1 emitting; the other ~229
    // are pure overhead until this runs.
    static void ApplyParticleAutoCull(bool enable)
    {
        foreach (var kv in _particleCullOrig)
        {
            if (kv.Key != null)
            {
                var m = kv.Key.main;
                m.cullingMode = kv.Value;
            }
        }
        _particleCullOrig.Clear();

        if (!enable) return;

        int count = 0;
        foreach (var ps in Object.FindObjectsOfType<ParticleSystem>())
        {
            var main = ps.main;
            if (main.cullingMode != ParticleSystemCullingMode.Automatic)
            {
                _particleCullOrig[ps] = main.cullingMode;
                main.cullingMode = ParticleSystemCullingMode.Automatic;
                count++;
            }
        }
        if (count > 0)
            Plugin.Log.LogDebug($"particle auto-cull: enabled on {count} systems");
    }

    static void ApplyGpuInstancing(bool enable)
    {
        foreach (var kv in _gpuInstancingOrig)
            if (kv.Key != null) kv.Key.enableInstancing = kv.Value;
        _gpuInstancingOrig.Clear();

        if (!enable) return;

        int count = 0;
        var seen = new HashSet<Material>();
        foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
        {
            foreach (var mat in r.sharedMaterials)
            {
                if (mat == null || seen.Contains(mat)) continue;
                seen.Add(mat);
                if (mat.shader != null && !mat.enableInstancing)
                {
                    _gpuInstancingOrig[mat] = mat.enableInstancing;
                    mat.enableInstancing = true;
                    count++;
                }
            }
        }
        if (count > 0)
            Plugin.Log.LogDebug($"enabled GPU instancing on {count} materials");
    }

    static void ApplyTinyRendererCull(bool enable)
    {
        foreach (var kv in _tinyRendererOrig)
            if (kv.Key != null) kv.Key.shadowCastingMode = kv.Value;
        _tinyRendererOrig.Clear();

        if (!enable) return;

        // Permanent shadow-caster kill for anything smaller than this bounds
        // diagonal. Potato's 1m catches most small decorative props.
        float sizeCap = Settings.Preset == QualityPreset.Potato ? 1f : 0.5f;

        RefreshLocalAvatarRendererSet();

        int count = 0;
        foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
        {
            if (r.shadowCastingMode == ShadowCastingMode.Off) continue;
            // ShadowsOnly proxies (e.g. flashlight shadow caster) flip to a visible
            // mesh if forced Off. Distance-cull restores per-frame, tiny-cull is
            // permanent; leave them alone.
            if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
            if (r.bounds.size.magnitude >= sizeCap) continue;
            if (_localAvatarRendererSet.Contains(r)) continue;
            _tinyRendererOrig[r] = r.shadowCastingMode;
            r.shadowCastingMode = ShadowCastingMode.Off;
            count++;
        }
        if (count > 0)
            Plugin.Log.LogDebug($"disabled shadow casting on {count} tiny renderers");
    }

    // Same shape as the particle pass: the Start postfixes above only ever cut
    // shadows, so the scene sweep cuts and restores too instead of stamping Soft on
    // every item light whenever the preset does not optimize.
    // Flashlights are handled by UpdateFlashlightShadowBudget; not touched here.
    static void ApplyItemLightShadowCull(bool enable)
    {
        foreach (var kv in _itemLightOrig)
            if (kv.Key != null) kv.Key.shadows = kv.Value;
        _itemLightOrig.Clear();

        if (!enable) return;

        foreach (var il in Object.FindObjectsOfType<ItemLight>())
            CullItemLightShadow(il.itemLight);
    }

    static void ApplyExplosionLightShadowCull(bool enable)
    {
        foreach (var kv in _explosionLightOrig)
            if (kv.Key != null) kv.Key.shadows = kv.Value;
        _explosionLightOrig.Clear();

        if (!enable) return;

        foreach (var ex in Object.FindObjectsOfType<ParticlePrefabExplosion>())
            CullExplosionLightShadow(ex.light);
    }

    // Shared with the Start postfixes so a light spawned mid-level is on the same
    // restore list as the ones the scene sweep found.
    internal static void CullItemLightShadow(Light? light) => CullLightShadow(_itemLightOrig, light);
    internal static void CullExplosionLightShadow(Light? light) => CullLightShadow(_explosionLightOrig, light);

    static void CullLightShadow(Dictionary<Light, LightShadows> orig, Light? light)
    {
        if (light == null || light.shadows == LightShadows.None) return;
        if (!orig.ContainsKey(light)) orig[light] = light.shadows;
        light.shadows = LightShadows.None;
    }

    static void ApplyAnimatedLightCull(bool enable)
    {
        foreach (var kv in _animatedLightOrig)
            if (kv.Key != null) kv.Key.shadows = kv.Value;
        _animatedLightOrig.Clear();

        if (!enable) return;

        int count = 0;
        foreach (var la in Object.FindObjectsOfType<LightAnimator>())
        {
            var light = la.GetComponent<Light>();
            if (light != null && light.shadows != LightShadows.None)
            {
                _animatedLightOrig[light] = light.shadows;
                light.shadows = LightShadows.None;
                count++;
            }
        }
        if (count > 0)
            Plugin.Log.LogDebug($"disabled shadows on {count} animated lights");
    }

    // "LEAK" if any dict is non-empty post-disable, "OK" otherwise. Note
    // watchlist counts are candidate-list sizes, not mutations; they clear on
    // next Apply() so a non-zero value there doesn't mean a broken restore path.
    internal static void LogRestoreState(string tag)
    {
        int shadowRes = QualityPatch.ShadowResOrigCount;
        int avatarRt = PlayerAvatarMenuAAPatch.AvatarRtOrigCount;
        int avatarPpl = PlayerAvatarMenuAAPatch.AvatarPplCount;
        int mutations = _tinyRendererOrig.Count + _animatedLightOrig.Count
                      + _zeroIntensityOrig.Count + _gpuInstancingOrig.Count
                      + _particleCullOrig.Count + _particleShadowOrig.Count
                      + _itemLightOrig.Count + _explosionLightOrig.Count
                      + _shadowBudgetOrig.Count
                      + shadowRes + avatarRt + avatarPpl
                      + _flashlightBudgetOrig.Count + _pointLightShadowOrig.Count
                      + _playerAvatarShadowOrig.Count + _playerAvatarSmrUpdateOrig.Count;
        string prefix = mutations == 0 ? "OK" : "LEAK";
        Plugin.Log.LogDebug(
            $"[restore-state:{tag}] {prefix} mutations={mutations} " +
            $"tinyRend={_tinyRendererOrig.Count} " +
            $"animLight={_animatedLightOrig.Count} " +
            $"zeroInt={_zeroIntensityOrig.Count} " +
            $"gpuInst={_gpuInstancingOrig.Count} " +
            $"particleCull={_particleCullOrig.Count} " +
            $"particleShadow={_particleShadowOrig.Count} " +
            $"itemLight={_itemLightOrig.Count} " +
            $"explosionLight={_explosionLightOrig.Count} " +
            $"glowBudget={_shadowBudgetOrig.Count} " +
            $"shadowRes={shadowRes} " +
            $"avatarRt={avatarRt} " +
            $"avatarPpl={avatarPpl} " +
            $"flashBudget={_flashlightBudgetOrig.Count} " +
            $"pointLight={_pointLightShadowOrig.Count} " +
            $"playerShadow={_playerAvatarShadowOrig.Count} " +
            $"playerSmr={_playerAvatarSmrUpdateOrig.Count} " +
            $"watchlist={_distanceCullWatchlist.Count}");
    }
}

[HarmonyPatch(typeof(LevelGenerator), "GenerateDone")]
static class LevelOptimizationPatch
{
    static void Postfix()
    {
        SceneOptimizer.Apply();
        // destroyed PhysGrabObjects would otherwise pile up as stale keys
        PhysGrabObjectFixPatch.ClearRbCache();
    }
}

// Late-joining / respawning PlayerAvatars need their renderers captured too;
// GenerateDone only fires on level gen, so network joiners slip through otherwise
[HarmonyPatch(typeof(PlayerAvatar), "Start")]
static class PlayerAvatarStartPatch
{
    static void Postfix() => SceneOptimizer.ApplyDeferred();
}

// vanilla renders the avatar preview to a tiny RT (e.g. 209x418) with no AA, so
// edges are jagged when the UI scales it up. Bump RT, add MSAA + SMAA, and gate
// the camera to idle while the menu is hidden.
[HarmonyPatch(typeof(PlayerAvatarMenu), "Start")]
static class PlayerAvatarMenuAAPatch
{
    // Target is the LONG edge. Until 1.7.8 it was the short one, and since the
    // aspect is preserved the vanilla 208x416 preview landed at 1024x2048 aa=4:
    // four times the surface the constant claims, on a UI slot about 400px tall,
    // reallocated on every pause-menu open. 1024 on the long edge is still 2.5x
    // the displayed size, and MSAA + SMAA handles the rest.
    const int TargetLongDim = 1024;
    const int TargetMsaa = 4;

    // saved originals so F10 can revert the RT back to vanilla size/aa
    internal static readonly Dictionary<RenderTexture, (int w, int h, int aa)> _rtOrig = new();

    // for F10 revert: either we attached the PPL (destroy it) or modified an
    // existing one (revert its AA mode)
    internal struct PplState
    {
        internal bool AttachedByUs;
        internal PostProcessLayer.Antialiasing OriginalAa;
    }
    internal static readonly Dictionary<Camera, PplState> _pplState = new();

    static void Postfix(PlayerAvatarMenu __instance)
    {
        if (!Settings.ModEnabled) return;
        ApplyToMenu(__instance);
    }

    // called from Start postfix and from F10 re-enable (where Start won't fire again)
    internal static void ApplyToMenu(PlayerAvatarMenu __instance)
    {
        if (!Diagnostics.AvatarPreviewUpgrade.Value) return;

        // The game tears the preview down itself when its page closes
        // (PlayerAvatarMenu.Update destroys cameraAndStuff, PlayerAvatarMenuHover
        // releases and destroys the RT), so every menu open leaves a dead key
        // behind. Drop them here or the F10 restore-state line reads LEAK forever.
        PruneDead();

        // only the pause-menu preview gets the bump. expressionAvatar variants exist
        // during gameplay (one per player) and must keep vanilla behaviour; an
        // 8-player lobby would otherwise eat real ms on menu-style rendering.
        if (__instance.expressionAvatar) return;

        // bail when multiple menu PAMs are visible at once: bumping each to 1024² +
        // MSAA + SMAA scales linearly (truck lobby with 8+ slots is the classic case).
        // Filter on parentPage being alive AND in active hierarchy so dying PAMs from
        // a pause→customize transition (one extra Update tick before self-destruct)
        // drop out, plus world / icon variants which never get a parentPage in Awake.
        // Cap at 2 leaves room for a real pause+customize overlay.
        int liveMenuCount = 0;
        foreach (var m in Object.FindObjectsOfType<PlayerAvatarMenu>())
        {
            if (m.expressionAvatar || m.worldAvatar || m.iconMakerAvatar) continue;
            if (m.parentPage == null) continue;
            if (!m.parentPage.gameObject.activeInHierarchy) continue;
            if (++liveMenuCount > 2) return;
        }

        if (__instance.cameraAndStuff == null) return;

        var cam = __instance.cameraAndStuff.GetComponentInChildren<Camera>(true);
        if (cam == null)
        {
            Plugin.Log.LogDebug($"avatar preview: no Camera under cameraAndStuff on '{__instance.name}'");
            return;
        }

        cam.allowMSAA = true;

        // SMAA catches the alpha/shader edges MSAA misses
        var ppl = cam.GetComponent<PostProcessLayer>();
        if (ppl != null)
        {
            if (!_pplState.ContainsKey(cam))
                _pplState[cam] = new PplState { AttachedByUs = false, OriginalAa = ppl.antialiasingMode };
            ppl.antialiasingMode = PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing;
            ppl.fastApproximateAntialiasing.keepAlpha = true;
        }
        else
        {
            // no PPL on avatar camera: borrow resources from main camera's PPL to attach one
            // (m_Resources is internal, so needs reflection)
            var mainPpl = Camera.main?.GetComponent<PostProcessLayer>();
            if (mainPpl != null)
            {
                var resField = typeof(PostProcessLayer).GetField("m_Resources",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var resources = resField?.GetValue(mainPpl) as PostProcessResources;
                if (resources != null)
                {
                    var newPpl = cam.gameObject.AddComponent<PostProcessLayer>();
                    newPpl.Init(resources);
                    newPpl.volumeTrigger = cam.transform;
                    newPpl.antialiasingMode = PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing;
                    newPpl.fastApproximateAntialiasing.keepAlpha = true;
                    _pplState[cam] = new PplState { AttachedByUs = true, OriginalAa = PostProcessLayer.Antialiasing.None };
                    Plugin.Log.LogDebug($"avatar preview: attached PostProcessLayer with SMAA to '{cam.name}'");
                }
                else
                {
                    Plugin.Log.LogDebug($"avatar preview: main PPL has no resources; SMAA unavailable on '{cam.name}'");
                }
            }
        }

        var rt = cam.targetTexture;
        if (rt != null)
        {
            int longDim = Mathf.Max(rt.width, rt.height);
            bool needsUpscale = longDim < TargetLongDim;
            bool needsMsaa = rt.antiAliasing < TargetMsaa;
            if ((needsUpscale || needsMsaa) && !_rtOrig.ContainsKey(rt))
            {
                _rtOrig[rt] = (rt.width, rt.height, rt.antiAliasing);

                // preserve the original aspect: take the long edge to TargetLongDim
                // and scale the short one by the same factor.
                float scale = longDim > 0 && longDim < TargetLongDim
                    ? (float)TargetLongDim / longDim : 1f;
                int newW = Mathf.Max(1, Mathf.RoundToInt(rt.width * scale));
                int newH = Mathf.Max(1, Mathf.RoundToInt(rt.height * scale));

                ResizeBoundRt(cam, rt, newW, newH, TargetMsaa);

                Plugin.Log.LogDebug($"avatar preview: RT '{rt.name}' bumped {_rtOrig[rt].w}x{_rtOrig[rt].h} " +
                    $"aa={_rtOrig[rt].aa} -> {newW}x{newH} aa={TargetMsaa}");
            }
        }

        // gate the camera so it only renders while the hosting MenuPage is active
        var gate = cam.gameObject.GetComponent<AvatarCameraGate>();
        if (gate == null) gate = cam.gameObject.AddComponent<AvatarCameraGate>();
        gate.menu = __instance;
        gate.cam = cam;
    }

    // Resize a render texture that is the live target of an enabled camera.
    // PlayerAvatarMenuHover.Awake creates the RT, calls Create() and binds it to the
    // preview camera before PlayerAvatarMenu.Start ever runs, so by the time the
    // postfix gets here the texture is created and bound. Releasing it in that
    // state is the "Releasing render texture that is set as Camera.targetTexture!"
    // case; the camera keeps a pointer to a surface that no longer exists until the
    // next Create, and 1.7.7 did exactly that on every pause-menu open. Unbind and
    // idle the camera first, resize, then hand it back, all inside one call so it
    // never renders to the screen in between (same shape as ResizeBoundRT in
    // UltrawidePatch for the overlay texture).
    static void ResizeBoundRt(Camera cam, RenderTexture rt, int w, int h, int aa)
    {
        bool bound = cam.targetTexture == rt;
        bool wasEnabled = cam.enabled;
        if (bound)
        {
            cam.enabled = false;
            cam.targetTexture = null;
        }
        bool wasCreated = rt.IsCreated();
        if (wasCreated) rt.Release();
        rt.width = w;
        rt.height = h;
        rt.antiAliasing = aa;
        if (wasCreated) rt.Create();
        if (bound)
        {
            cam.targetTexture = rt;
            cam.enabled = wasEnabled;
        }
    }

    static void PruneDead()
    {
        List<RenderTexture>? deadRts = null;
        foreach (var rt in _rtOrig.Keys)
            if (rt == null) (deadRts ??= new List<RenderTexture>()).Add(rt);
        if (deadRts != null)
            foreach (var rt in deadRts) _rtOrig.Remove(rt);

        List<Camera>? deadCams = null;
        foreach (var cam in _pplState.Keys)
            if (cam == null) (deadCams ??= new List<Camera>()).Add(cam);
        if (deadCams != null)
            foreach (var cam in deadCams) _pplState.Remove(cam);
    }

    // F10 hook: put every live preview RT back to the size and sample count the
    // game created it with, then strip the AA layer and the gate.
    internal static void RestoreAvatarRt()
    {
        PruneDead();
        if (_rtOrig.Count == 0 && _pplState.Count == 0) return;

        // cameras targeting our tracked RTs; the gate comes off them at the end
        var affectedCams = new List<Camera>();
        foreach (var cam in Object.FindObjectsOfType<Camera>(true))
            if (cam.targetTexture != null && _rtOrig.ContainsKey(cam.targetTexture))
                affectedCams.Add(cam);

        foreach (var kv in _rtOrig)
        {
            var rt = kv.Key;
            if (rt == null) continue;
            var owner = affectedCams.Find(c => c.targetTexture == rt);
            if (owner != null)
                ResizeBoundRt(owner, rt, kv.Value.w, kv.Value.h, kv.Value.aa);
            else
            {
                bool wasCreated = rt.IsCreated();
                if (wasCreated) rt.Release();
                rt.width = kv.Value.w;
                rt.height = kv.Value.h;
                rt.antiAliasing = kv.Value.aa;
                if (wasCreated) rt.Create();
            }
        }
        _rtOrig.Clear();

        // attached-by-us → destroy; modified → revert AA mode
        foreach (var kv in _pplState)
        {
            var cam = kv.Key;
            if (cam == null) continue;
            var ppl = cam.GetComponent<PostProcessLayer>();
            if (ppl == null) continue;
            if (kv.Value.AttachedByUs)
                Object.Destroy(ppl);
            else
                ppl.antialiasingMode = kv.Value.OriginalAa;
        }
        _pplState.Clear();

        // re-enable cameras, strip the gate; vanilla rendering takes back over
        foreach (var cam in affectedCams)
        {
            cam.enabled = true;
            var gate = cam.GetComponent<AvatarCameraGate>();
            if (gate != null) Object.Destroy(gate);
        }
    }

    internal static int AvatarRtOrigCount => _rtOrig.Count;
    internal static int AvatarPplCount => _pplState.Count;

    // Hand one preview back to the game before it tears the preview down. The RT
    // is not resized back: the game is about to Release and Destroy it anyway, and
    // reallocating a surface during teardown is the thing we are trying to avoid.
    // Everything the mod bolted on comes off while the camera is still valid.
    internal static void ReleasePreview(RenderTexture? rt, Camera? cam)
    {
        if (rt != null) _rtOrig.Remove(rt);
        if (cam == null) return;

        if (_pplState.TryGetValue(cam, out var state))
        {
            _pplState.Remove(cam);
            var ppl = cam.GetComponent<PostProcessLayer>();
            if (ppl != null)
            {
                if (state.AttachedByUs) Object.Destroy(ppl);
                else ppl.antialiasingMode = state.OriginalAa;
            }
        }

        var gate = cam.GetComponent<AvatarCameraGate>();
        if (gate != null) Object.Destroy(gate);
    }

    // F10 re-enable: Start already fired, so scan + reapply manually
    internal static void ReapplyAll()
    {
        foreach (var menu in Object.FindObjectsOfType<PlayerAvatarMenu>())
            ApplyToMenu(menu);
    }
}

// PlayerAvatarMenuHover owns the preview render texture: Awake creates it, binds
// it to the preview camera and the RawImage, and OnDestroy unbinds, releases and
// destroys it. That runs when the menu page dies, one or more frames before
// PlayerAvatarMenu.Update destroys the camera object, so this is the last moment
// the camera is still alive and still pointing at a live surface. Take our
// PostProcessLayer and gate off here rather than leaving them on a camera whose
// target is about to be pulled out from under them, and drop the tracking entry
// in the same breath so the dictionaries never carry a dead key across a scene
// change. Before 1.7.8 both only got cleaned on the next menu open or on F10.
[HarmonyPatch(typeof(PlayerAvatarMenuHover), "OnDestroy")]
static class PlayerAvatarMenuHoverTeardownPatch
{
    static void Prefix(PlayerAvatarMenuHover __instance)
        => PlayerAvatarMenuAAPatch.ReleasePreview(
            __instance.renderTextureInstance, __instance.previewCamera);
}

// Toggles camera.enabled based on whether the hosting MenuPage is in the
// hierarchy. activeInHierarchy (not pageActive) catches the Opening animation
// too, so the preview isn't blank for the fade-in frames.
internal class AvatarCameraGate : MonoBehaviour
{
    internal PlayerAvatarMenu? menu;
    internal Camera? cam;

    void LateUpdate()
    {
        if (cam == null) { Destroy(this); return; }

        // mod off: defer to vanilla behaviour (always enabled). Never while the
        // camera has no target texture though; the game clears it in
        // PlayerAvatarMenuHover.OnDestroy and an enabled preview camera with no
        // target draws the avatar over the whole screen.
        if (!Settings.ModEnabled)
        {
            if (!cam.enabled && cam.targetTexture != null) cam.enabled = true;
            return;
        }

        // mod on: gate on whether the menu page is in the hierarchy and active.
        // Catches Opening / Active / Activating; only skips when fully hidden.
        bool shouldRender = menu != null
            && menu.parentPage != null
            && menu.parentPage.gameObject.activeInHierarchy;
        if (cam.enabled != shouldRender) cam.enabled = shouldRender;
    }
}

// re-apply when perf-relevant settings change mid-level
static class PerfSettingsWatcher
{
    static bool _registered;
    static int _lastPreset = -1;
    static int _lastShadowQ = -1;
    static int _lastShadowBudget;
    static int _lastPerfExp, _lastPerfItem, _lastPerfAnim, _lastPerfPart, _lastPerfTiny, _lastPerfDist, _lastPerfFlash;

    internal static void Register()
    {
        if (_registered) return;
        _registered = true;
        SnapshotState();
        Settings.OnSettingsChanged += OnChanged;
    }

    static bool _lastModEnabled = true;

    static void OnChanged()
    {
        bool changed = Settings.ModEnabled != _lastModEnabled
            || (int)Settings.Preset != _lastPreset
            || (int)Settings.ResolvedShadowQuality != _lastShadowQ
            || Settings.ResolvedShadowBudget != _lastShadowBudget
            || Settings.PerfExplosionShadows != _lastPerfExp
            || Settings.PerfItemLightShadows != _lastPerfItem
            || Settings.PerfAnimatedLightShadows != _lastPerfAnim
            || Settings.PerfParticleShadows != _lastPerfPart
            || Settings.PerfTinyRendererCulling != _lastPerfTiny
            || Settings.PerfDistanceShadowCulling != _lastPerfDist
            || Settings.PerfFlashlightShadowBudget != _lastPerfFlash;

        if (!changed) return;

        // if shadow budget changed, reset fade state so lights apply instantly
        if (Settings.ResolvedShadowBudget != _lastShadowBudget)
            SceneOptimizer.ResetShadowBudget();

        SnapshotState();
        SceneOptimizer.Apply();
    }

    static void SnapshotState()
    {
        _lastModEnabled = Settings.ModEnabled;
        _lastPreset = (int)Settings.Preset;
        _lastShadowQ = (int)Settings.ResolvedShadowQuality;
        _lastShadowBudget = Settings.ResolvedShadowBudget;
        _lastPerfExp = Settings.PerfExplosionShadows;
        _lastPerfItem = Settings.PerfItemLightShadows;
        _lastPerfAnim = Settings.PerfAnimatedLightShadows;
        _lastPerfPart = Settings.PerfParticleShadows;
        _lastPerfTiny = Settings.PerfTinyRendererCulling;
        _lastPerfDist = Settings.PerfDistanceShadowCulling;
        _lastPerfFlash = Settings.PerfFlashlightShadowBudget;
    }
}

// ---
// GC reduction: always on, no visual impact
// ---

// PhysGrabber.ColorStateSetColor: 6x GetComponent cached on Start
static class GrabberComponentCache
{
    class CachedRenderers
    {
        public Material? beam;
        public Material? point1;
        public Material? point2;
        public Material? rotate;
        public Light? grabLight;
        public Material? orb0;
        public Material? orb1;
        public bool valid;
    }

    static readonly ConditionalWeakTable<PhysGrabber, CachedRenderers> _cache = new();

    internal static void CacheFor(PhysGrabber g)
    {
        if (_cache.TryGetValue(g, out _)) return;

        var c = new CachedRenderers();
        try
        {
            if (g.physGrabBeam != null)
                c.beam = g.physGrabBeam.GetComponent<LineRenderer>()?.material;
            if (g.physGrabPointVisual1 != null)
                c.point1 = g.physGrabPointVisual1.GetComponent<MeshRenderer>()?.material;
            if (g.physGrabPointVisual2 != null)
                c.point2 = g.physGrabPointVisual2.GetComponent<MeshRenderer>()?.material;
            if (g.physGrabPointVisualRotate != null)
                c.rotate = g.physGrabPointVisualRotate.GetComponent<MeshRenderer>()?.material;

            var arm = g.playerAvatar?.playerAvatarVisuals?.playerAvatarRightArm;
            if (arm != null)
            {
                c.grabLight = arm.grabberLight;
                if (arm.grabberOrbSpheres != null && arm.grabberOrbSpheres.Length >= 2)
                {
                    c.orb0 = arm.grabberOrbSpheres[0]?.GetComponent<MeshRenderer>()?.material;
                    c.orb1 = arm.grabberOrbSpheres[1]?.GetComponent<MeshRenderer>()?.material;
                }
            }
            c.valid = true;
        }
        catch { c.valid = false; }

        _cache.AddOrUpdate(g, c);
    }

    internal static bool TryApplyColor(PhysGrabber g, Color main, Color emission)
    {
        if (!_cache.TryGetValue(g, out var c)) return false;
        if (!c.valid) return false;

        g.currentBeamColor = main;
        SetColor(c.beam, main, emission);
        SetColor(c.point1, main, emission);
        SetColor(c.point2, main, emission);
        SetColor(c.rotate, main, emission);
        if (c.grabLight != null) c.grabLight.color = main;
        SetColor(c.orb0, main, emission);
        SetColor(c.orb1, main, emission);
        return true;
    }

    static void SetColor(Material? mat, Color main, Color emission)
    {
        if (mat == null) return;
        mat.color = main;
        mat.SetColor("_EmissionColor", emission);
    }
}

[HarmonyPatch(typeof(PhysGrabber), "Start")]
static class CacheGrabberOnStart
{
    static void Postfix(PhysGrabber __instance)
    {
        GrabberComponentCache.CacheFor(__instance);
    }
}

[HarmonyPatch(typeof(PhysGrabber), "ColorStateSetColor")]
static class SkipGrabberGetComponent
{
    static bool Prefix(PhysGrabber __instance, Color mainColor, Color emissionColor)
    {
        if (!Settings.ModEnabled) return true;
        return !GrabberComponentCache.TryApplyColor(__instance, mainColor, emissionColor);
    }
}

// hook settings changes: registered from LevelGenerator.GenerateDone
// since Plugin.Awake can't be patched from within itself
[HarmonyPatch(typeof(LevelGenerator), "GenerateDone")]
static class RegisterPerfWatcher
{
    static void Postfix() => PerfSettingsWatcher.Register();
}

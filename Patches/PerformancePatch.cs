using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace REPOFidelity.Patches;

// ---
// shadow reduction — gated by preset or custom toggles
// ---

[HarmonyPatch(typeof(ParticlePrefabExplosion), "Start")]
static class ExplosionShadowPatch
{
    static void Postfix(ParticlePrefabExplosion __instance)
    {
        if (Settings.ShouldOptimize(Settings.PerfOpt.ExplosionShadows) && __instance.light != null)
            __instance.light.shadows = LightShadows.None;
    }
}

[HarmonyPatch(typeof(ItemLight), "Start")]
static class ItemLightShadowPatch
{
    static void Postfix(ItemLight __instance)
    {
        if (Settings.ShouldOptimize(Settings.PerfOpt.ItemLightShadows) && __instance.itemLight != null)
            __instance.itemLight.shadows = LightShadows.None;
    }
}

[HarmonyPatch(typeof(FlashlightController), "Start")]
static class FlashlightShadowPatch
{
    static void Postfix(FlashlightController __instance)
    {
        if (Settings.ShouldOptimize(Settings.PerfOpt.ItemLightShadows) && __instance.spotlight != null)
            __instance.spotlight.shadows = LightShadows.None;
    }
}

// spectate camera forces shadow distance to 90m — always cap this,
// it's wasteful on every preset for a zoomed death cam
[HarmonyPatch(typeof(SpectateCamera), "Update")]
static class SpectateShadowPatch
{
    static void Postfix()
    {
        if (QualitySettings.shadowDistance > Settings.ResolvedShadowDistance)
            QualitySettings.shadowDistance = Settings.ResolvedShadowDistance;
    }
}

// ---
// scene-wide optimization scans — run on level gen and
// whenever perf settings change mid-level
// ---

static class SceneOptimizer
{
    internal static void Apply()
    {
        _shadowStrengths.Clear();
        ApplyGpuInstancing(Settings.ModEnabled);
        ApplyZeroIntensityShadows(Settings.ModEnabled);
        ApplyParticleAutoCull(Settings.ModEnabled);

        SetParticleShadows(!Settings.ShouldOptimize(Settings.PerfOpt.ParticleShadows));
        ApplyTinyRendererCull(Settings.ShouldOptimize(Settings.PerfOpt.TinyRendererCulling));
        ApplyAnimatedLightCull(Settings.ShouldOptimize(Settings.PerfOpt.AnimatedLightShadows));

        // scan existing lights in the scene so switching presets mid-level works
        SetItemLightShadows(!Settings.ShouldOptimize(Settings.PerfOpt.ItemLightShadows));
        SetExplosionLightShadows(!Settings.ShouldOptimize(Settings.PerfOpt.ExplosionShadows));

        // must run AFTER other passes — renderers they've set to Off should stay out of the watchlist
        CaptureDistanceCullWatchlist();
    }

    // ---
    // saved-state dictionaries — each mutation records what it changed so F10
    // (mod off) or flag-off returns the scene to vanilla
    // ---

    static readonly Dictionary<MeshRenderer, ShadowCastingMode> _tinyRendererOrig = new();
    static readonly Dictionary<Light, LightShadows> _animatedLightOrig = new();
    static readonly Dictionary<Light, LightShadows> _zeroIntensityOrig = new();
    static readonly Dictionary<Material, bool> _gpuInstancingOrig = new();
    static readonly Dictionary<ParticleSystem, ParticleSystemCullingMode> _particleCullOrig = new();

    // ---
    // distance-based shadow cull — small props (<2m bounds) disable shadow casting when
    // beyond fog-clamped shadow distance, re-enable when inside. runs every frame via
    // UpdateDistanceShadowCull, built once per level via CaptureDistanceCullWatchlist.
    // ---

    static readonly List<Renderer> _distanceCullWatchlist = new();

    // flashlight shadow budget — only N closest flashlights keep their original shadow
    // mode, rest go to None. saves original mode per spotlight so F10 / flag-off revert
    static readonly Dictionary<Light, LightShadows> _flashlightBudgetOrig = new();
    static readonly List<(Light light, float distSq)> _flashlightSorted = new();
    const int FlashlightBudgetN = 4;

    static void CaptureDistanceCullWatchlist()
    {
        // restore any renderers we'd previously disabled before dropping references,
        // otherwise F10 / flag-off would orphan them in the Off state.
        RestoreDistanceCullWatchlist();
        _distanceCullWatchlist.Clear();
        if (!Settings.ShouldOptimize(Settings.PerfOpt.DistanceShadowCulling)) return;

        int count = 0;
        foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
        {
            if (r.shadowCastingMode == ShadowCastingMode.Off) continue;
            if (r.bounds.size.magnitude >= 2f) continue;
            _distanceCullWatchlist.Add(r);
            count++;
        }
        if (count > 0)
            Plugin.Log.LogInfo($"distance cull watchlist: {count} small renderers");
    }

    internal static void UpdateDistanceShadowCull(Camera? cam)
    {
        if (cam == null || _distanceCullWatchlist.Count == 0) return;

        // mod off or flag off — restore everything and bail
        if (!Settings.ShouldOptimize(Settings.PerfOpt.DistanceShadowCulling))
        {
            RestoreDistanceCullWatchlist();
            return;
        }

        float threshold = Settings.ResolvedShadowDistance * 0.7f;
        float thresholdSq = threshold * threshold;
        float hystOn = threshold * 0.9f;
        float hystOnSq = hystOn * hystOn;
        var camPos = cam.transform.position;

        for (int i = 0; i < _distanceCullWatchlist.Count; i++)
        {
            var r = _distanceCullWatchlist[i];
            if (r == null) continue;
            float distSq = (r.transform.position - camPos).sqrMagnitude;
            bool isOff = r.shadowCastingMode == ShadowCastingMode.Off;
            if (isOff && distSq < hystOnSq)
                r.shadowCastingMode = ShadowCastingMode.On;
            else if (!isOff && distSq > thresholdSq)
                r.shadowCastingMode = ShadowCastingMode.Off;
        }
    }

    static void RestoreDistanceCullWatchlist()
    {
        for (int i = 0; i < _distanceCullWatchlist.Count; i++)
        {
            var r = _distanceCullWatchlist[i];
            if (r != null) r.shadowCastingMode = ShadowCastingMode.On;
        }
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
        foreach (var fl in Object.FindObjectsOfType<FlashlightController>())
        {
            var light = fl.spotlight;
            if (light == null) continue;
            if (!light.isActiveAndEnabled) continue;
            float distSq = (light.transform.position - camPos).sqrMagnitude;
            _flashlightSorted.Add((light, distSq));
        }

        _flashlightSorted.Sort((a, b) => a.distSq.CompareTo(b.distSq));

        for (int i = 0; i < _flashlightSorted.Count; i++)
        {
            var light = _flashlightSorted[i].light;
            if (i < FlashlightBudgetN)
            {
                // within budget — restore original mode if we'd previously muted it
                if (_flashlightBudgetOrig.TryGetValue(light, out var orig))
                {
                    light.shadows = orig;
                    _flashlightBudgetOrig.Remove(light);
                }
            }
            else if (light.shadows != LightShadows.None)
            {
                // over budget — mute shadows, save original for restore
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

    // ---
    // shadow budget — limits how many small point lights cast shadows at once.
    // closest N to the camera get shadows with faded strength transitions.
    // ---

    static readonly List<(Light light, float dist)> _budgetCandidates = new();
    static readonly Dictionary<int, float> _shadowStrengths = new();
    const float FadeSpeed = 3f;

    internal static void ResetShadowBudget()
    {
        _shadowStrengths.Clear();
    }

    internal static void UpdateShadowBudget(Camera? cam)
    {
        if (cam == null) return;

        // mod disabled or budget unlimited — restore all managed lights
        if (!Settings.ModEnabled)
        {
            RestoreManagedLights();
            return;
        }

        int budget = Settings.ResolvedShadowBudget;
        if (budget <= 0)
        {
            RestoreManagedLights();
            return;
        }

        var camPos = cam.transform.position;
        float cullDist = Settings.ResolvedShadowDistance;
        float dt = 0.1f;

        _budgetCandidates.Clear();

        foreach (var light in Object.FindObjectsOfType<Light>())
        {
            if (!light.enabled || !light.gameObject.activeInHierarchy) continue;
            if (light.intensity <= 0f) continue;

            // only manage item glow lights — skip infrastructure
            if (light.type != LightType.Point || light.intensity >= 1f || light.range >= 5f)
                continue;

            float dist = Vector3.Distance(camPos, light.transform.position);

            // beyond shadow distance — fade out
            if (dist > cullDist)
            {
                FadeLight(light, 0f, dt);
                continue;
            }

            _budgetCandidates.Add((light, dist));
        }

        // sort by distance — closest first
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
        if (_shadowStrengths.Count == 0) return;
        foreach (var light in Object.FindObjectsOfType<Light>())
        {
            if (!light.enabled || !light.gameObject.activeInHierarchy) continue;
            if (light.type != LightType.Point || light.intensity >= 1f || light.range >= 5f)
                continue;
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
            // fully faded out — disable shadow
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

        int count = 0;
        foreach (var light in Object.FindObjectsOfType<Light>())
        {
            if (light.intensity <= 0f && light.shadows != LightShadows.None)
            {
                _zeroIntensityOrig[light] = light.shadows;
                light.shadows = LightShadows.None;
                count++;
            }
        }
        if (count > 0)
            Plugin.Log.LogInfo($"disabled shadows on {count} zero-intensity lights");
    }

    static void SetParticleShadows(bool on)
    {
        var mode = on ? ShadowCastingMode.On : ShadowCastingMode.Off;
        int count = 0;
        foreach (var ps in Object.FindObjectsOfType<ParticleSystemRenderer>())
        {
            if (on && ps.shadowCastingMode == ShadowCastingMode.Off)
            {
                ps.shadowCastingMode = ShadowCastingMode.On;
                ps.receiveShadows = true;
                count++;
            }
            else if (!on && ps.shadowCastingMode != ShadowCastingMode.Off)
            {
                ps.shadowCastingMode = ShadowCastingMode.Off;
                ps.receiveShadows = false;
                count++;
            }
        }
        if (count > 0)
            Plugin.Log.LogInfo($"{(on ? "restored" : "disabled")} shadows on {count} particle renderers");
    }

    // off-screen and non-emitting systems still tick every frame unless culling is explicit.
    // a typical R.E.P.O. level has 230+ systems registered and 1 emitting — the other ~229
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
            Plugin.Log.LogInfo($"particle auto-cull: enabled on {count} systems");
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
            Plugin.Log.LogInfo($"enabled GPU instancing on {count} materials");
    }

    static void ApplyTinyRendererCull(bool enable)
    {
        foreach (var kv in _tinyRendererOrig)
            if (kv.Key != null) kv.Key.shadowCastingMode = kv.Value;
        _tinyRendererOrig.Clear();

        if (!enable) return;

        int count = 0;
        foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
        {
            if (r.shadowCastingMode == ShadowCastingMode.Off) continue;
            if (r.bounds.size.magnitude < 0.3f)
            {
                _tinyRendererOrig[r] = r.shadowCastingMode;
                r.shadowCastingMode = ShadowCastingMode.Off;
                count++;
            }
        }
        if (count > 0)
            Plugin.Log.LogInfo($"disabled shadow casting on {count} tiny renderers");
    }

    static void SetItemLightShadows(bool on)
    {
        foreach (var il in Object.FindObjectsOfType<ItemLight>())
        {
            if (il.itemLight == null) continue;
            il.itemLight.shadows = on ? LightShadows.Soft : LightShadows.None;
        }

        // flashlight is a separate component with its own Light
        foreach (var fl in Object.FindObjectsOfType<FlashlightController>())
        {
            if (fl.spotlight == null) continue;
            fl.spotlight.shadows = on ? LightShadows.Soft : LightShadows.None;
        }
    }

    static void SetExplosionLightShadows(bool on)
    {
        foreach (var ex in Object.FindObjectsOfType<ParticlePrefabExplosion>())
        {
            if (ex.light == null) continue;
            ex.light.shadows = on ? LightShadows.Soft : LightShadows.None;
        }
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
            Plugin.Log.LogInfo($"disabled shadows on {count} animated lights");
    }

    // "LEAK" if any dict is non-empty post-disable, "OK" otherwise. Note
    // watchlist counts are candidate-list sizes, not mutations — they clear on
    // next Apply() so a non-zero value there doesn't mean a broken restore path.
    internal static void LogRestoreState(string tag)
    {
        int shadowRes = QualityPatch.ShadowResOrigCount;
        int avatarRt = PlayerAvatarMenuAAPatch.AvatarRtOrigCount;
        int avatarPpl = PlayerAvatarMenuAAPatch.AvatarPplCount;
        int mutations = _tinyRendererOrig.Count + _animatedLightOrig.Count
                      + _zeroIntensityOrig.Count + _gpuInstancingOrig.Count
                      + _particleCullOrig.Count + shadowRes + avatarRt + avatarPpl
                      + _flashlightBudgetOrig.Count;
        string prefix = mutations == 0 ? "OK" : "LEAK";
        Plugin.Log.LogInfo(
            $"[restore-state:{tag}] {prefix} mutations={mutations} " +
            $"tinyRend={_tinyRendererOrig.Count} " +
            $"animLight={_animatedLightOrig.Count} " +
            $"zeroInt={_zeroIntensityOrig.Count} " +
            $"gpuInst={_gpuInstancingOrig.Count} " +
            $"particleCull={_particleCullOrig.Count} " +
            $"shadowRes={shadowRes} " +
            $"avatarRt={avatarRt} " +
            $"avatarPpl={avatarPpl} " +
            $"flashBudget={_flashlightBudgetOrig.Count} " +
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

// vanilla renders the avatar preview to a tiny RT (e.g. 209x418) with no AA, so
// edges are jagged when the UI scales it up. Bump RT, add MSAA + SMAA, and gate
// the camera to idle while the menu is hidden.
[HarmonyPatch(typeof(PlayerAvatarMenu), "Start")]
static class PlayerAvatarMenuAAPatch
{
    // 2048 was overkill for a ~400px UI display and cost ~0.7ms/frame; 1024 looks
    // identical and costs a quarter as much. MSAA + SMAA handles the rest.
    const int TargetRtSize = 1024;
    const int MaxLongDim = 2048;
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
        // only the pause-menu preview gets the bump. expressionAvatar variants exist
        // during gameplay (one per player) and must keep vanilla behaviour — an
        // 8-player lobby would otherwise eat real ms on menu-style rendering.
        if (__instance.expressionAvatar) return;

        if (__instance.cameraAndStuff == null) return;

        var cam = __instance.cameraAndStuff.GetComponentInChildren<Camera>(true);
        if (cam == null)
        {
            Plugin.Log.LogInfo($"avatar preview: no Camera under cameraAndStuff on '{__instance.name}'");
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
            // no PPL on avatar camera — borrow resources from main camera's PPL to attach one
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
                    Plugin.Log.LogInfo($"avatar preview: attached PostProcessLayer with SMAA to '{cam.name}'");
                }
                else
                {
                    Plugin.Log.LogInfo($"avatar preview: main PPL has no resources — SMAA unavailable on '{cam.name}'");
                }
            }
        }

        var rt = cam.targetTexture;
        if (rt != null)
        {
            bool needsUpscale = rt.width < TargetRtSize && rt.height < TargetRtSize;
            bool needsMsaa = rt.antiAliasing < TargetMsaa;
            if ((needsUpscale || needsMsaa) && !_rtOrig.ContainsKey(rt))
            {
                _rtOrig[rt] = (rt.width, rt.height, rt.antiAliasing);

                // preserve original aspect ratio — scale shortest dimension up to
                // TargetRtSize, apply same factor to the longer one. Cap longer dim
                // at MaxLongDim so extreme aspect ratios don't blow up VRAM.
                int shortDim = Mathf.Min(rt.width, rt.height);
                float scale = shortDim < TargetRtSize ? (float)TargetRtSize / shortDim : 1f;
                int newW = Mathf.RoundToInt(rt.width * scale);
                int newH = Mathf.RoundToInt(rt.height * scale);
                int longDim = Mathf.Max(newW, newH);
                if (longDim > MaxLongDim)
                {
                    float shrink = (float)MaxLongDim / longDim;
                    newW = Mathf.RoundToInt(newW * shrink);
                    newH = Mathf.RoundToInt(newH * shrink);
                }

                bool wasCreated = rt.IsCreated();
                if (wasCreated) rt.Release();
                rt.width = newW;
                rt.height = newH;
                rt.antiAliasing = TargetMsaa;
                if (wasCreated) rt.Create();

                Plugin.Log.LogInfo($"avatar preview: RT '{rt.name}' bumped {_rtOrig[rt].w}x{_rtOrig[rt].h} " +
                    $"aa={_rtOrig[rt].aa} → {newW}x{newH} aa={TargetMsaa}");
            }
        }

        // gate the camera so it only renders while the hosting MenuPage is active
        var gate = cam.gameObject.GetComponent<AvatarCameraGate>();
        if (gate == null) gate = cam.gameObject.AddComponent<AvatarCameraGate>();
        gate.menu = __instance;
        gate.cam = cam;
    }

    // F10 hook. Unity leaves RTs in a bad state if mutated while a camera's
    // actively rendering them, so cycle cam.enabled around the resize.
    internal static void RestoreAvatarRt()
    {
        if (_rtOrig.Count == 0) return;

        // collect cameras targeting our tracked RTs, disable during mutation
        var affectedCams = new List<Camera>();
        foreach (var cam in Object.FindObjectsOfType<Camera>())
        {
            if (cam.targetTexture != null && _rtOrig.ContainsKey(cam.targetTexture))
            {
                cam.enabled = false;
                affectedCams.Add(cam);
            }
        }

        foreach (var kv in _rtOrig)
        {
            var rt = kv.Key;
            if (rt == null) continue;
            bool wasCreated = rt.IsCreated();
            if (wasCreated) rt.Release();
            rt.width = kv.Value.w;
            rt.height = kv.Value.h;
            rt.antiAliasing = kv.Value.aa;
            if (wasCreated) rt.Create();
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

        // re-enable cameras, strip the gate — vanilla rendering takes back over
        foreach (var cam in affectedCams)
        {
            cam.enabled = true;
            var gate = cam.GetComponent<AvatarCameraGate>();
            if (gate != null) Object.Destroy(gate);
        }
    }

    internal static int AvatarRtOrigCount => _rtOrig.Count;
    internal static int AvatarPplCount => _pplState.Count;

    // F10 re-enable — Start already fired, so scan + reapply manually
    internal static void ReapplyAll()
    {
        foreach (var menu in Object.FindObjectsOfType<PlayerAvatarMenu>())
            ApplyToMenu(menu);
    }
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

        // mod off — defer to vanilla behaviour (always enabled)
        if (!Settings.ModEnabled)
        {
            if (!cam.enabled) cam.enabled = true;
            return;
        }

        // mod on — gate on whether the menu page is in the hierarchy and active.
        // Catches Opening / Active / Activating — only skips when fully hidden.
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
// GC reduction — always on, no visual impact
// ---

// PhysGrabber.ColorStateSetColor — 6x GetComponent cached on Start
static class GrabberComponentCache
{
    class CachedRenderers
    {
        public Material beam;
        public Material point1;
        public Material point2;
        public Material rotate;
        public Light grabLight;
        public Material orb0;
        public Material orb1;
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

    static void SetColor(Material mat, Color main, Color emission)
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

// hook settings changes — registered from LevelGenerator.GenerateDone
// since Plugin.Awake can't be patched from within itself
[HarmonyPatch(typeof(LevelGenerator), "GenerateDone")]
static class RegisterPerfWatcher
{
    static void Postfix() => PerfSettingsWatcher.Register();
}

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

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
        EnableGPUInstancing();
        DisableZeroIntensityShadows();

        SetParticleShadows(!Settings.ShouldOptimize(Settings.PerfOpt.ParticleShadows));

        if (Settings.ShouldOptimize(Settings.PerfOpt.TinyRendererCulling))
            SetTinyRendererShadows(false);

        if (Settings.ShouldOptimize(Settings.PerfOpt.AnimatedLightShadows))
            SetAnimatedLightShadows(false);

        // scan existing lights in the scene so switching presets mid-level works
        SetItemLightShadows(!Settings.ShouldOptimize(Settings.PerfOpt.ItemLightShadows));
        SetExplosionLightShadows(!Settings.ShouldOptimize(Settings.PerfOpt.ExplosionShadows));
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

    static void DisableZeroIntensityShadows()
    {
        int count = 0;
        foreach (var light in Object.FindObjectsOfType<Light>())
        {
            if (light.intensity <= 0f && light.shadows != LightShadows.None)
            {
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

    static void EnableGPUInstancing()
    {
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
                    mat.enableInstancing = true;
                    count++;
                }
            }
        }
        if (count > 0)
            Plugin.Log.LogInfo($"enabled GPU instancing on {count} materials");
    }

    static void SetTinyRendererShadows(bool on)
    {
        if (on) return; // can't restore — don't know original state
        int count = 0;
        foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
        {
            if (r.shadowCastingMode == ShadowCastingMode.Off) continue;
            if (r.bounds.size.magnitude < 0.3f)
            {
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

    static void SetAnimatedLightShadows(bool on)
    {
        if (on) return; // one-way — can't restore without knowing original shadow mode
        int count = 0;
        foreach (var la in Object.FindObjectsOfType<LightAnimator>())
        {
            var light = la.GetComponent<Light>();
            if (light != null && light.shadows != LightShadows.None)
            {
                light.shadows = LightShadows.None;
                count++;
            }
        }
        if (count > 0)
            Plugin.Log.LogInfo($"disabled shadows on {count} animated lights");
    }
}

[HarmonyPatch(typeof(LevelGenerator), "GenerateDone")]
static class LevelOptimizationPatch
{
    static void Postfix() => SceneOptimizer.Apply();
}

// re-apply when perf-relevant settings change mid-level
static class PerfSettingsWatcher
{
    static bool _registered;
    static int _lastPreset = -1;
    static int _lastShadowQ = -1;
    static int _lastShadowBudget;
    static int _lastPerfExp, _lastPerfItem, _lastPerfAnim, _lastPerfPart, _lastPerfTiny;

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
            || Settings.PerfTinyRendererCulling != _lastPerfTiny;

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

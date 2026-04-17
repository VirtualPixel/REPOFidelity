using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace REPOFidelity.Patches;

[HarmonyPatch]
internal static class QualityPatch
{
    // Flashlight lights get an Ultra-only 4096 exemption; everything else follows
    // the range-driven bracket table in ApplyRangeTieredLightShadows.
    private static readonly HashSet<Light> _flashlightLights = new();

    private static ShadowResolution _vanillaShadowRes;
    private static float _vanillaShadowDist;
    private static float _vanillaLodBias;
    private static int _vanillaPixelLights;
    private static AnisotropicFiltering _vanillaAF;
    private static int _vanillaTexMip;
    private static bool _vanillaQualitySaved;

    internal static void SaveVanillaQuality()
    {
        if (_vanillaQualitySaved) return;
        _vanillaShadowRes = QualitySettings.shadowResolution;
        _vanillaShadowDist = QualitySettings.shadowDistance;
        _vanillaLodBias = QualitySettings.lodBias;
        _vanillaPixelLights = QualitySettings.pixelLightCount;
        _vanillaAF = QualitySettings.anisotropicFiltering;
        _vanillaTexMip = QualitySettings.globalTextureMipmapLimit;
        _vanillaQualitySaved = true;

        Plugin.Log.LogInfo($"Vanilla defaults: shadows={_vanillaShadowRes}/{_vanillaShadowDist}m " +
            $"LOD={_vanillaLodBias} lights={_vanillaPixelLights} AF={_vanillaAF} texMip={_vanillaTexMip}");
    }

    internal static void RestoreVanillaQuality()
    {
        if (!_vanillaQualitySaved) return;
        QualitySettings.shadowResolution = _vanillaShadowRes;
        QualitySettings.shadowDistance = _vanillaShadowDist;
        QualitySettings.lodBias = _vanillaLodBias;
        QualitySettings.pixelLightCount = _vanillaPixelLights;
        QualitySettings.anisotropicFiltering = _vanillaAF;
        QualitySettings.globalTextureMipmapLimit = _vanillaTexMip;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GraphicsManager), nameof(GraphicsManager.UpdateAll))]
    public static void PostfixUpdateAll()
    {
        ApplyQualitySettings();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GraphicsManager), nameof(GraphicsManager.UpdateLightDistance))]
    public static void PostfixUpdateLightDistance(GraphicsManager __instance)
    {
        if (!Settings.ModEnabled) return;
        float oldDist = __instance.lightDistance;
        __instance.lightDistance = Settings.ResolvedLightDistance;

        // If we increased the distance, tell LightManager to re-evaluate
        // (lights that were culled at the old distance need reactivating)
        if (__instance.lightDistance > oldDist && LightManager.instance != null)
            LightManager.instance.UpdateInstant();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GraphicsManager), nameof(GraphicsManager.UpdateMotionBlur))]
    public static void PostfixUpdateMotionBlur()
    {
        if (!Settings.ModEnabled) return;
        if (!Settings.MotionBlurOverride && PostProcessing.Instance != null)
            PostProcessing.Instance.motionBlur.active = false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GraphicsManager), nameof(GraphicsManager.UpdateChromaticAberration))]
    public static void PostfixChromaticAberration()
    {
        if (!Settings.ModEnabled) return;
        if (!Settings.ChromaticAberration && PostProcessing.Instance != null)
            PostProcessing.Instance.chromaticAberration.active = false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GraphicsManager), nameof(GraphicsManager.UpdateLensDistortion))]
    public static void PostfixLensDistortion()
    {
        if (!Settings.ModEnabled) return;
        if (!Settings.LensDistortion && PostProcessing.Instance != null)
            PostProcessing.Instance.lensDistortion.active = false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GraphicsManager), nameof(GraphicsManager.UpdateGrain))]
    public static void PostfixGrain()
    {
        if (!Settings.ModEnabled) return;
        if (!Settings.FilmGrain && PostProcessing.Instance != null)
            PostProcessing.Instance.grain.active = false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EnvironmentDirector), nameof(EnvironmentDirector.Setup))]
    public static void PostfixEnvironmentSetup(EnvironmentDirector __instance)
    {
        UpscalerManager.SaveVanillaFog();

        if (!Settings.ModEnabled) return;
        float fogMult = Settings.ResolvedFogMultiplier;
        if (fogMult != 1f)
        {
            RenderSettings.fogEndDistance *= fogMult;
            RenderSettings.fogStartDistance *= fogMult;
        }
        Settings.ResolvedEffectiveFogEnd = RenderSettings.fogEndDistance;
        Settings.ApplyFogClamps();

        float viewDist = Settings.ResolvedViewDistance;
        if (viewDist > 0f)
        {
            __instance.MainCamera.farClipPlane = viewDist;
        }
        else
        {
            // never clip tighter than vanilla — distant lights bleed through fog
            float clip = RenderSettings.fogEndDistance + 10f;
            if (UpscalerManager._vanillaSaved)
                clip = Mathf.Max(clip, UpscalerManager._vanillaFarClip);
            __instance.MainCamera.farClipPlane = clip;
        }

        Plugin.Log.LogInfo($"Fog: {RenderSettings.fogStartDistance:F0}-{RenderSettings.fogEndDistance:F0}m, " +
            $"clip: {__instance.MainCamera.farClipPlane:F0}m");
    }

    internal static void ApplyFogAndDrawDistance()
    {
        if (!Settings.ModEnabled) return;
        if (!UpscalerManager._vanillaSaved) return;

        float fogMult = Settings.ResolvedFogMultiplier;
        RenderSettings.fogStartDistance = UpscalerManager._vanillaFogStart * fogMult;
        RenderSettings.fogEndDistance = UpscalerManager._vanillaFogEnd * fogMult;
        Settings.ResolvedEffectiveFogEnd = RenderSettings.fogEndDistance;
        Settings.ApplyFogClamps();

        float viewDist = Settings.ResolvedViewDistance;
        var cam = Camera.main;
        if (cam != null)
        {
            if (viewDist > 0f)
            {
                cam.farClipPlane = viewDist;
            }
            else
            {
                float clip = RenderSettings.fogEndDistance + 10f;
                if (UpscalerManager._vanillaSaved)
                    clip = Mathf.Max(clip, UpscalerManager._vanillaFarClip);
                cam.farClipPlane = clip;
            }
        }

        // Update light distance
        if (GraphicsManager.instance != null)
            GraphicsManager.instance.lightDistance = Settings.ResolvedLightDistance;
        if (LightManager.instance != null)
            LightManager.instance.UpdateInstant();

        ApplyLayerCulling(cam);
    }

    // cull non-gameplay layers at fog edge to save draw calls
    private static void ApplyLayerCulling(Camera? cam)
    {
        if (cam == null) return;

        float fogEnd = RenderSettings.fogEndDistance;
        float[] distances = new float[32];
        for (int i = 8; i < 32; i++)
        {
            string name = LayerMask.LayerToName(i);
            if (string.IsNullOrEmpty(name)) continue;
            if (name.Contains("Player") || name.Contains("Enemy") ||
                name.Contains("Interact") || name.Contains("Trigger") ||
                name.Contains("PhysGrab") || name.Contains("Valuable"))
                continue;
            distances[i] = fogEnd + 5f;
        }
        cam.layerCullDistances = distances;
    }

    internal static void ApplyQualitySettings()
    {
        if (!Settings.ModEnabled) return;
        SaveVanillaQuality();

        try
        {
            var preset = Settings.Preset;

            ApplyShadowResolution();
            QualitySettings.shadowDistance = Settings.ResolvedShadowDistance;
            QualitySettings.lodBias = Settings.ResolvedLODBias;
            QualitySettings.pixelLightCount = Settings.ResolvedPixelLightCount;

            int af = Settings.ResolvedAnisotropicFiltering;
            int texMip = (int)Settings.ResolvedTextureQuality;

            ApplyAnisotropicFiltering(af);
            QualitySettings.globalTextureMipmapLimit = texMip;

            // Light render distance
            if (GraphicsManager.instance != null)
                GraphicsManager.instance.lightDistance = Settings.ResolvedLightDistance;

            // Apply fog and draw distance live
            ApplyFogAndDrawDistance();

            Plugin.Log.LogInfo($"[{preset}] shadows={Settings.ResolvedShadowQuality}/{Settings.ResolvedShadowDistance}m " +
                $"LOD={Settings.ResolvedLODBias} AF={af} lights={Settings.ResolvedPixelLightCount} " +
                $"lightDist={Settings.ResolvedLightDistance}m tex={texMip}");
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"QualityPatch failed: {ex}");
        }
    }

    private static void ApplyShadowResolution()
    {
        switch (Settings.ResolvedShadowQuality)
        {
            case ShadowQuality.Low:
                QualitySettings.shadowResolution = UnityEngine.ShadowResolution.Low;
                QualitySettings.shadowCascades = 1;
                break;
            case ShadowQuality.Medium:
                QualitySettings.shadowResolution = UnityEngine.ShadowResolution.Medium;
                QualitySettings.shadowCascades = 2;
                break;
            case ShadowQuality.High:
                QualitySettings.shadowResolution = UnityEngine.ShadowResolution.High;
                QualitySettings.shadowCascades = 4;
                break;
            case ShadowQuality.Ultra:
                QualitySettings.shadowResolution = UnityEngine.ShadowResolution.VeryHigh;
                QualitySettings.shadowCascades = 4;
                break;
        }
        ApplyRangeTieredLightShadows();
    }

    // re-scan scene for Flashlight spotlights — cheap, runs on every shadow-resolution apply
    // (level change, preset change). handles respawn / new level correctly without separate tracking.
    private static void RefreshFlashlightLights()
    {
        _flashlightLights.Clear();
        foreach (var fl in Object.FindObjectsOfType<FlashlightController>())
        {
            if (fl.spotlight != null) _flashlightLights.Add(fl.spotlight);
        }
    }

    // range-tiered shadow map resolution applied to every non-directional light.
    // resolution buckets are: <5m→256, 5-10m→512, 10-20m→1024, >20m→2048.
    // Flashlight gets 4096 on Ultra only (player-focused spotlight, keep pristine).
    // Potato caps the top bucket to 1024 for extra savings.
    private static void ApplyRangeTieredLightShadows()
    {
        RefreshFlashlightLights();

        bool ultraFlashlight = Settings.ResolvedShadowQuality == ShadowQuality.Ultra;
        int cap = Settings.Preset == QualityPreset.Potato ? 1024 : 4096;

        int touched = 0;
        foreach (var light in Object.FindObjectsOfType<Light>())
        {
            if (light.intensity <= 0f && light.shadows != LightShadows.None)
                light.shadows = LightShadows.None;

            // directional uses the global shadow resolution + cascades
            if (light.type == LightType.Directional)
            {
                light.shadowCustomResolution = 0;
                continue;
            }

            // Flashlight exemption — only on Ultra
            if (ultraFlashlight && _flashlightLights.Contains(light))
            {
                light.shadowCustomResolution = 4096;
                touched++;
                continue;
            }

            float range = light.range;
            int res = range switch
            {
                < 5f => 256,
                < 10f => 512,
                < 20f => 1024,
                _ => 2048,
            };
            light.shadowCustomResolution = Mathf.Min(res, cap);
            touched++;
        }

        if (touched > 0)
            Plugin.Log.LogInfo($"shadow-res: tiered {touched} lights (flashlights={_flashlightLights.Count}, cap={cap})");
    }

    private static void ApplyAnisotropicFiltering(int level)
    {
        if (level <= 0)
        {
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
        }
        else
        {
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            Texture.SetGlobalAnisotropicFilteringLimits(level, level);
        }
    }
}

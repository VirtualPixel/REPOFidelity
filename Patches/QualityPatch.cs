using HarmonyLib;
using UnityEngine;

namespace REPOFidelity.Patches;

[HarmonyPatch]
internal static class QualityPatch
{
    private static bool _ultraShadowsApplied;

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
                SetLightShadowResolution(0);
                break;
            case ShadowQuality.Medium:
                QualitySettings.shadowResolution = UnityEngine.ShadowResolution.Medium;
                QualitySettings.shadowCascades = 2;
                SetLightShadowResolution(0);
                break;
            case ShadowQuality.High:
                QualitySettings.shadowResolution = UnityEngine.ShadowResolution.High;
                QualitySettings.shadowCascades = 4;
                SetLightShadowResolution(0);
                break;
            case ShadowQuality.Ultra:
                QualitySettings.shadowResolution = UnityEngine.ShadowResolution.VeryHigh;
                QualitySettings.shadowCascades = 4;
                SetLightShadowResolution(4096);
                break;
        }
    }

    private static void SetLightShadowResolution(int resolution)
    {
        // Skip the scan if we're setting 0 (default) and haven't applied custom resolutions
        if (resolution == 0 && !_ultraShadowsApplied) return;
        _ultraShadowsApplied = resolution > 0;
        foreach (var light in Object.FindObjectsOfType<Light>())
        {
            if (light.intensity <= 0f && light.shadows != LightShadows.None)
                light.shadows = LightShadows.None;

            if (resolution <= 0)
            {
                light.shadowCustomResolution = 0;
                continue;
            }

            // directional lights use the global shadow resolution + cascades
            if (light.type == LightType.Directional)
            {
                light.shadowCustomResolution = 0;
                continue;
            }

            // small decorative point lights get lower resolution — they don't
            // need 4K shadow maps at 2.9m range
            if (light.type == LightType.Point && light.intensity < 1f && light.range < 5f)
                light.shadowCustomResolution = 512;
            else if (light.range < 10f)
                light.shadowCustomResolution = Mathf.Min(resolution, 2048);
            else
                light.shadowCustomResolution = resolution;
        }
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

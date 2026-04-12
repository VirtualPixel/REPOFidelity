using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace REPOFidelity.Patches;

[HarmonyPatch(typeof(RenderTextureMain))]
internal static class RenderTexturePatch
{
    // Saved vanilla values for F10 restore
    internal static DepthTextureMode OriginalDepthMode;
    internal static PostProcessLayer.Antialiasing[] OriginalAAModes = null!;

    [HarmonyPostfix]
    [HarmonyPatch("Start")]
    public static void PostfixStart(RenderTextureMain __instance)
    {
        Camera? mainCam = null;

        // Save original values before modifying
        if (__instance.cameras.Count > 0)
        {
            OriginalDepthMode = __instance.cameras[0].depthTextureMode;
            OriginalAAModes = new PostProcessLayer.Antialiasing[__instance.cameras.Count];
            for (int j = 0; j < __instance.cameras.Count; j++)
            {
                var ppl = __instance.cameras[j].GetComponent<PostProcessLayer>();
                OriginalAAModes[j] = ppl != null ? ppl.antialiasingMode : PostProcessLayer.Antialiasing.None;
            }
        }

        for (int i = 0; i < __instance.cameras.Count; i++)
        {
            var cam = __instance.cameras[i];

            if (i == 0)
            {
                mainCam = cam;
                // depth + MV only for temporal upscalers
                bool needsDepth = Settings.ResolvedUpscaleMode is UpscaleMode.DLSS
                    or UpscaleMode.DLAA or UpscaleMode.FSR_Temporal;
                if (needsDepth)
                {
                    cam.depthTextureMode |= DepthTextureMode.Depth;
                    cam.depthTextureMode |= DepthTextureMode.MotionVectors;
                }
            }

            var ppl = cam.GetComponent<PostProcessLayer>();
            if (ppl != null)
            {
                ppl.antialiasingMode = Settings.ResolvedAAMode switch
                {
                    AAMode.TAA => PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing,
                    AAMode.SMAA => PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing,
                    AAMode.FXAA => PostProcessLayer.Antialiasing.FastApproximateAntialiasing,
                    AAMode.Off => PostProcessLayer.Antialiasing.None,
                    _ => ppl.antialiasingMode
                };
                Plugin.Log.LogDebug($"AA on {cam.name}: {ppl.antialiasingMode}");
            }
        }

        // force native res RT so overlayRawImage stays crisp
        __instance.textureWidthOriginal = Screen.width;
        __instance.textureHeightOriginal = Screen.height;
        // sync textureWidth too or OnScreen() breaks (price labels etc)
        __instance.textureWidth = Screen.width;
        __instance.textureHeight = Screen.height;

        if (mainCam != null)
        {
            var manager = mainCam.gameObject.AddComponent<UpscalerManager>();
            manager.Setup(__instance, mainCam);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch("Update")]
    public static void PrefixUpdate(RenderTextureMain __instance)
    {
        if (!Settings.ModEnabled) return;
        if (Settings.Pixelation) return;

        var manager = UpscalerManager.Instance;
        if (manager != null && manager.CurrentTier == UpscalerManager.RenderTier.Upscaler)
        {
            // textureWidthOriginal = display size (UI layout depends on this)
            // textureWidth = render size (OnScreen() uses this)
            __instance.textureWidthOriginal = Screen.width;
            __instance.textureHeightOriginal = Screen.height;
            var gameRT = __instance.renderTexture;
            if (gameRT != null)
            {
                __instance.textureWidth = gameRT.width;
                __instance.textureHeight = gameRT.height;
            }
            return;
        }

        // Passthrough and NativeScaling: game RT at native res
        __instance.textureWidthOriginal = Screen.width;
        __instance.textureHeightOriginal = Screen.height;
        __instance.textureWidth = Screen.width;
        __instance.textureHeight = Screen.height;
    }

    internal static void RestoreVanillaCameraSettings()
    {
        if (RenderTextureMain.instance == null) return;
        var cameras = RenderTextureMain.instance.cameras;

        if (cameras.Count > 0)
            cameras[0].depthTextureMode = OriginalDepthMode;

        if (OriginalAAModes != null)
        {
            for (int i = 0; i < cameras.Count && i < OriginalAAModes.Length; i++)
            {
                var ppl = cameras[i].GetComponent<PostProcessLayer>();
                if (ppl != null)
                    ppl.antialiasingMode = OriginalAAModes[i];
            }
        }
    }

    internal static void ReapplyModCameraSettings()
    {
        if (RenderTextureMain.instance == null) return;
        var cameras = RenderTextureMain.instance.cameras;

        if (cameras.Count > 0)
        {
            bool needsDepth = Settings.ResolvedUpscaleMode is UpscaleMode.DLSS
                or UpscaleMode.DLAA or UpscaleMode.FSR_Temporal;
            if (needsDepth)
            {
                cameras[0].depthTextureMode |= DepthTextureMode.Depth;
                cameras[0].depthTextureMode |= DepthTextureMode.MotionVectors;
            }
        }

        for (int i = 0; i < cameras.Count; i++)
        {
            var ppl = cameras[i].GetComponent<PostProcessLayer>();
            if (ppl != null)
            {
                ppl.antialiasingMode = Settings.ResolvedAAMode switch
                {
                    AAMode.TAA => PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing,
                    AAMode.SMAA => PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing,
                    AAMode.FXAA => PostProcessLayer.Antialiasing.FastApproximateAntialiasing,
                    AAMode.Off => PostProcessLayer.Antialiasing.None,
                    _ => ppl.antialiasingMode
                };
            }
        }
    }

    internal static float GetRenderScale()
    {
        return Settings.ResolvedRenderScale / 100f;
    }
}

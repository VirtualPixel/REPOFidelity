using UnityEngine;

namespace REPOFidelity.Shaders;

internal static class CASShader
{
    private static Material? _material;
    private static bool _initialized;
    private static readonly int SharpnessId = Shader.PropertyToID("_Sharpness");

    private static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        var shader = ShaderBundle.LoadShader("Hidden/REPOFidelity/CAS");
        if (shader == null || !shader.isSupported)
        {
            Plugin.Log.LogWarning("CAS shader unavailable");
            return;
        }

        _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        Plugin.Log.LogInfo("CAS shader loaded");
    }

    public static void Apply(RenderTexture source, RenderTexture destination, float sharpness)
    {
        Init();
        if (_material == null)
        {
            Graphics.Blit(source, destination);
            return;
        }
        _material.SetFloat(SharpnessId, sharpness);
        Graphics.Blit(source, destination, _material);
    }
}

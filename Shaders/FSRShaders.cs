using UnityEngine;

namespace REPOFidelity.Shaders;

internal static class FSRShaders
{
    private static Material? _easuMaterial;
    private static Material? _rcasMaterial;
    private static bool _initialized;

    private static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        var easuShader = ShaderBundle.LoadShader("Hidden/REPOFidelity/FSR_EASU");
        if (easuShader != null && easuShader.isSupported)
            _easuMaterial = new Material(easuShader) { hideFlags = HideFlags.HideAndDontSave };

        var rcasShader = ShaderBundle.LoadShader("Hidden/REPOFidelity/FSR_RCAS");
        if (rcasShader != null && rcasShader.isSupported)
            _rcasMaterial = new Material(rcasShader) { hideFlags = HideFlags.HideAndDontSave };

        if (_easuMaterial != null)
            Plugin.Log.LogInfo("FSR shaders loaded from bundle");
        else
            Plugin.Log.LogWarning("FSR EASU shader unavailable — FSR upscaling disabled");
    }

    public static Material? GetEASUMaterial()
    {
        Init();
        return _easuMaterial;
    }

    public static Material? GetRCASMaterial()
    {
        Init();
        return _rcasMaterial;
    }
}

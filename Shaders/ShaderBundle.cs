using System.IO;
using System.Reflection;
using UnityEngine;

namespace REPOFidelity.Shaders;

internal static class ShaderBundle
{
    private static AssetBundle? _bundle;
    private static bool _loadAttempted;
    private static Shader?[]? _allShaders;

    public static Shader? LoadShader(string name)
    {
        if (!_loadAttempted)
        {
            _loadAttempted = true;
            _bundle = TryLoadBundle();
            if (_bundle != null)
            {
                _allShaders = _bundle.LoadAllAssets<Shader>();
                Plugin.Log.LogDebug($"Loaded {_allShaders.Length} shader(s) from bundle");
                foreach (var s in _allShaders)
                    if (s != null) Plugin.Log.LogDebug($"  Shader: {s.name}");
            }
        }

        if (_allShaders == null) return null;

        foreach (var shader in _allShaders)
        {
            if (shader != null && shader.name == name)
                return shader;
        }

        Plugin.Log.LogWarning($"Shader '{name}' not found in bundle");
        return null;
    }

    private static AssetBundle? TryLoadBundle()
    {
        // Look for the bundle file next to the plugin DLL
        var dllPath = Assembly.GetExecutingAssembly().Location;
        var dllDir = Path.GetDirectoryName(dllPath);
        if (dllDir == null) return null;

        var bundlePath = Path.Combine(dllDir, "repofidelity_shaders");
        if (!File.Exists(bundlePath))
        {
            Plugin.Log.LogWarning($"Shader bundle not found at: {bundlePath}");
            Plugin.Log.LogWarning("FSR and CAS shaders unavailable. Build the shader bundle in Unity Editor.");
            return null;
        }

        var bundle = AssetBundle.LoadFromFile(bundlePath);
        if (bundle == null)
        {
            Plugin.Log.LogError("Failed to load shader AssetBundle");
            return null;
        }

        Plugin.Log.LogDebug("Shader bundle loaded successfully");
        return bundle;
    }
}

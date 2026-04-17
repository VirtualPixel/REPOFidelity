using System;
using System.IO;
using UnityEngine;

namespace REPOFidelity;

// simple JSON-backed settings file — keeps us out of BepInEx config (and REPOConfig)
internal class SettingsFile
{
    private readonly string _path;
    private SettingsData _data;
    private bool _suppressSave;

    internal SettingsData Data => _data;
    internal event Action? Changed;

    internal SettingsFile(string path)
    {
        _path = path;
        _data = new SettingsData();
        Load();
    }

    internal void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            string json = File.ReadAllText(_path);
            var loaded = JsonUtility.FromJson<SettingsData>(json);
            if (loaded != null) _data = loaded;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Failed to load settings: {ex.Message}");
        }
    }


    internal void Save()
    {
        if (_suppressSave) return;
        try
        {
            string dir = Path.GetDirectoryName(_path)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonUtility.ToJson(_data, true));
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Failed to save settings: {ex.Message}");
        }
    }

    internal void NotifyChanged()
    {
        if (!_suppressSave) Changed?.Invoke();
    }

    internal void SuppressEvents(Action action)
    {
        _suppressSave = true;
        try { action(); }
        finally { _suppressSave = false; }
    }
}

[Serializable]
internal class AutoTuneData
{
    public string version = "";
    public string gpuName = "";
    public int resWidth;
    public int resHeight;
    public bool cpuBound;

    public int upscaler = (int)UpscaleMode.Auto;
    public int renderScale = 100;
    public float sharpening = 0.3f;
    public int aaMode = (int)AAMode.Off;
    public int shadowQuality = (int)ShadowQuality.Ultra;
    public float shadowDistance = 150f;
    public float lodBias = 4f;
    public int pixelLightCount = 16;
    public float lightDistance = 75f;
    public float fogMultiplier = 1.1f;
    public float viewDistance = 0f;
    public int anisotropicFiltering = 16;
    public int perfLevel = 0;

    // bump this when autotune logic changes but version doesn't
    internal const int AutoTuneRevision = 3;
    public int revision;

    internal bool IsStale()
    {
        return version != BuildInfo.Version
            || revision < AutoTuneRevision
            || gpuName != SystemInfo.graphicsDeviceName
            || (resWidth > 0 && (resWidth != Screen.width || resHeight != Screen.height));
    }
}

[Serializable]
internal class SettingsData
{
    // preset — default to Auto so first-time users get auto-tuned
    public int preset = (int)QualityPreset.Auto;

    // display
    public int resWidth;
    public int resHeight;

    // upscaling
    public int upscaler = (int)UpscaleMode.Auto;
    public int renderScale = 67;
    public float sharpening = 0.5f;
    public int aaMode = (int)AAMode.Auto;
    public bool pixelation = false;

    // visuals
    public int shadowQuality = (int)ShadowQuality.Ultra;
    public float shadowDistance = 75f;
    public float lodBias = 3f;
    public int anisotropicFiltering = 16;
    public int pixelLightCount = 6;
    public int textureQuality = (int)TextureRes.Full;
    public float lightDistance = 30f;
    public float fogMultiplier = 1f;
    public float viewDistance = 0f;

    // fixes
    public bool motionBlur = false;
    public bool chromaticAberration = false;
    public bool lensDistortion = false;
    public bool filmGrain = true;
    public bool extractionFlickerFix = true;

    public int shadowBudget = -1;

    // performance — these default to -1 (auto, driven by preset).
    // 0 = off, 1 = on. only applies when preset is Custom.
    public int perfExplosionShadows = -1;
    public int perfItemLightShadows = -1;
    public int perfAnimatedLightShadows = -1;
    public int perfParticleShadows = -1;
    public int perfTinyRendererCulling = -1;

    // bottleneck detection — true = CPU-bound (default assumption),
    // overwritten by auto-benchmark when it runs
    public bool cpuBound = true;
    public int benchResWidth;
    public int benchResHeight;

    // cpu optimizations — -1 = auto (enable when frame time > 8ms), 0 = off, 1 = on
    public int cpuPatchMode = -1;

    // debug
    public int toggleKey = (int)KeyCode.F10;
    public bool debugOverlay = false;
    public bool benchmark = false;
    public bool autoConfigured = false;
    public string autoConfigVersion = "";
}

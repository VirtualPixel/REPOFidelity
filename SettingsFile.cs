using System;
using System.IO;
using UnityEngine;

namespace REPOFidelity;

// simple JSON-backed settings file; keeps us out of BepInEx config (and REPOConfig)
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

    // bump this when autotune logic changes (algorithm tweak, new perf gating, etc).
    // a bump forces every installed user through autotune once on first launch after the
    // mod update. mod-version changes alone do NOT force a re-tune; only revision bumps,
    // GPU changes, or resolution changes do.
    internal const int AutoTuneRevision = 7;
    public int revision;

    internal bool IsStale()
    {
        return revision < AutoTuneRevision
            || gpuName != SystemInfo.graphicsDeviceName
            || (resWidth > 0 && (resWidth != Screen.width || resHeight != Screen.height));
    }
}

[Serializable]
internal class SettingsData
{
    // preset: default to Auto so first-time users get auto-tuned
    public int preset = (int)QualityPreset.Auto;

    // machine identity: stamped on first run, used to detect a settings file
    // that traveled to different hardware via a shared profile
    public string gpuName = "";

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
    // Render the HUD/menus into the overlay camera's target texture at panel pixel
    // density instead of the game's fixed low-res overlay RT, so text is crisp at
    // every aspect. On by default (this is an HD mod); the opt-out restores the
    // vanilla soft HUD. Existing settings files lack the field, so the initializer
    // here ships it on for them too.
    public bool sharpHud = true;

    // ultra-wide / FOV
    // 0 = use the game's per-player default (70). Otherwise interpreted as
    // the vertical FOV the camera should run at; Unity's HOR+ behaviour
    // expands horizontal FOV from there based on Screen.aspect.
    public int verticalFovOverride = 0;
    // auto-correct CanvasScaler.matchWidthOrHeight to height-match when
    // Screen.aspect runs above ~16:9, so menus don't stretch on ultra-wide.
    public bool ultrawideUiFix = true;
    public bool ultrawideHudUnstretch = true;

    public int shadowBudget = -1;

    // performance: these default to -1 (auto, driven by preset).
    // 0 = off, 1 = on. only applies when preset is Custom.
    public int perfExplosionShadows = -1;
    public int perfItemLightShadows = -1;
    public int perfAnimatedLightShadows = -1;
    public int perfParticleShadows = -1;
    public int perfTinyRendererCulling = -1;
    public int perfDistanceShadowCulling = -1;
    public int perfFlashlightShadowBudget = -1;
    public int perfPointLightShadows = -1;

    // bottleneck detection: true = CPU-bound (default assumption),
    // overwritten by auto-benchmark when it runs
    public bool cpuBound = true;

    // cpu optimizations: -1 = auto (enable when frame time > 8ms), 0 = off, 1 = on
    public int cpuPatchMode = -1;

    // debug
    public int toggleKey = (int)KeyCode.F10;
    public int f11Target = (int)F11Target.FullOptLayer;
    public bool debugOverlay = false;
    public bool benchmark = false;
    public bool autoConfigured = false;
    public string autoConfigVersion = "";
}

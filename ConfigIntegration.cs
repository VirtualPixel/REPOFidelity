using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace REPOFidelity;

// Performance settings are stored as int in Settings; this maps the three
// meaningful states onto a dropdown REPOConfig can render.
internal enum PerfMode { Auto = -1, Keep = 0, Disable = 1 }

// Mirrors every mod setting into a BepInEx ConfigEntry so REPOConfig (or a
// hand-edited .cfg) can drive the mod when MenuLib is absent. settings.json
// stays canonical: on bind we pull the current value, and OnSettingsChanged
// pushes any settings.json change back into the entries.
internal static class ConfigIntegration
{
    private static bool _syncing;
    private static readonly List<Action> _refreshers = new();

    internal static void Initialize(ConfigFile cfg)
    {
        BindInt(cfg, "Display", "Vertical FOV", 0, 110,
            () => Settings.VerticalFovOverride, v => Settings.VerticalFovOverride = v,
            "0 = game default. Vertical FOV; horizontal expands with aspect.");
        BindBool(cfg, "Display", "Ultra-Wide UI Fix",
            () => Settings.UltrawideUiFix, v => Settings.UltrawideUiFix = v);
        BindBool(cfg, "Display", "Ultra-Wide HUD Unstretch",
            () => Settings.UltrawideHudUnstretch, v => Settings.UltrawideHudUnstretch = v);

        BindEnum(cfg, "Quality", "Quality Preset",
            () => Settings.Preset, v => Settings.Preset = v);

        BindEnum(cfg, "Upscaling", "Upscaler",
            () => Settings.UpscaleModeSetting, v => Settings.UpscaleModeSetting = v);
        BindInt(cfg, "Upscaling", "Render Scale", 33, 100,
            () => Settings.RenderScale, v => Settings.RenderScale = v);
        BindFloat(cfg, "Upscaling", "Sharpening", 0f, 1f,
            () => Settings.Sharpening, v => Settings.Sharpening = v);
        BindEnum(cfg, "Upscaling", "Anti-Aliasing",
            () => Settings.AntiAliasingMode, v => Settings.AntiAliasingMode = v);

        BindEnum(cfg, "Shadows & Lighting", "Shadow Quality",
            () => Settings.ShadowQualitySetting, v => Settings.ShadowQualitySetting = v);
        BindFloat(cfg, "Shadows & Lighting", "Shadow Distance", 5f, 200f,
            () => Settings.ShadowDistance, v => Settings.ShadowDistance = v);
        // Display the resolved budget (ShadowBudget's raw -1 = "auto, follow shadow
        // quality" sentinel would clamp to 0 in this range and read wrong), matching
        // the MenuLib slider. Writing any value pins it, same as the menu.
        BindInt(cfg, "Shadows & Lighting", "Shadow Limit", 0, 50,
            () => Settings.ResolvedShadowBudget, v => Settings.ShadowBudget = v, "0 = unlimited");
        BindFloat(cfg, "Shadows & Lighting", "Light Distance", 10f, 100f,
            () => Settings.LightDistance, v => Settings.LightDistance = v);
        BindInt(cfg, "Shadows & Lighting", "Max Lights", 1, 16,
            () => Settings.PixelLightCount, v => Settings.PixelLightCount = v);

        BindEnum(cfg, "Textures & Detail", "Texture Quality",
            () => Settings.TextureQuality, v => Settings.TextureQuality = v);
        BindIntList(cfg, "Textures & Detail", "Texture Filtering", new[] { 0, 2, 4, 8, 16 },
            () => Settings.AnisotropicFiltering, v => Settings.AnisotropicFiltering = v,
            "Anisotropic filtering");
        BindFloat(cfg, "Textures & Detail", "Detail Distance", 0.5f, 4f,
            () => Settings.LODBias, v => Settings.LODBias = v, "LOD bias");

        BindFloat(cfg, "Environment", "Fog Distance", 0.3f, 1.1f,
            () => Settings.FogDistanceMultiplier, v => Settings.FogDistanceMultiplier = v,
            "1.0 = vanilla; lower pulls fog closer");
        BindFloat(cfg, "Environment", "Draw Distance", 0f, 500f,
            () => Settings.ViewDistance, v => Settings.ViewDistance = v,
            "0 = auto. Camera far clip in meters.");

        BindBool(cfg, "Post Processing", "Motion Blur",
            () => Settings.MotionBlurOverride, v => Settings.MotionBlurOverride = v);
        BindBool(cfg, "Post Processing", "Chromatic Aberration",
            () => Settings.ChromaticAberration, v => Settings.ChromaticAberration = v);
        BindBool(cfg, "Post Processing", "Lens Distortion",
            () => Settings.LensDistortion, v => Settings.LensDistortion = v);
        BindBool(cfg, "Post Processing", "Film Grain",
            () => Settings.FilmGrain, v => Settings.FilmGrain = v);
        BindBool(cfg, "Post Processing", "Pixelation",
            () => Settings.Pixelation, v => Settings.Pixelation = v);
        BindBool(cfg, "Post Processing", "Sharp HUD",
            () => Settings.SharpHud, v => Settings.SharpHud = v);

        BindPerf(cfg, "Performance", "Explosion Shadows",
            () => Settings.PerfExplosionShadows, v => Settings.PerfExplosionShadows = v);
        BindPerf(cfg, "Performance", "Item Light Shadows",
            () => Settings.PerfItemLightShadows, v => Settings.PerfItemLightShadows = v);
        BindPerf(cfg, "Performance", "Animated Light Shadows",
            () => Settings.PerfAnimatedLightShadows, v => Settings.PerfAnimatedLightShadows = v);
        BindPerf(cfg, "Performance", "Particle Shadows",
            () => Settings.PerfParticleShadows, v => Settings.PerfParticleShadows = v);
        BindPerf(cfg, "Performance", "Small Object Shadows",
            () => Settings.PerfTinyRendererCulling, v => Settings.PerfTinyRendererCulling = v);
        BindPerf(cfg, "Performance", "Point Light Shadows",
            () => Settings.PerfPointLightShadows, v => Settings.PerfPointLightShadows = v);

        BindBool(cfg, "Misc", "Fix Extraction Flicker",
            () => Settings.ExtractionPointFlicker, v => Settings.ExtractionPointFlicker = v);
        BindBool(cfg, "Misc", "Debug Overlay",
            () => Settings.DebugOverlay, v => Settings.DebugOverlay = v);
        BindKey(cfg, "Misc", "Mod Toggle Key",
            () => Settings.ToggleKey, v => Settings.ToggleKey = v,
            "Disables mod for vanilla comparison");
        BindEnum(cfg, "Misc", "F11 Target",
            () => Settings.F11TargetSetting, v => Settings.F11TargetSetting = v);

        // Force every entry to the canonical settings.json value, overriding any
        // stale value a hand-edited or carried-over .cfg may have loaded.
        Refresh();
        Settings.OnSettingsChanged += Refresh;
    }

    private static void Refresh()
    {
        _syncing = true;
        try
        {
            foreach (var r in _refreshers) r();
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void BindBool(ConfigFile cfg, string section, string name,
        Func<bool> getter, Action<bool> setter, string desc = "")
    {
        var e = cfg.Bind(section, name, getter(), new ConfigDescription(desc));
        e.SettingChanged += (_, __) => { if (!_syncing) setter(e.Value); };
        _refreshers.Add(() => e.Value = getter());
    }

    private static void BindInt(ConfigFile cfg, string section, string name, int min, int max,
        Func<int> getter, Action<int> setter, string desc = "")
    {
        var e = cfg.Bind(section, name, getter(),
            new ConfigDescription(desc, new AcceptableValueRange<int>(min, max)));
        e.SettingChanged += (_, __) => { if (!_syncing) setter(e.Value); };
        _refreshers.Add(() => e.Value = getter());
    }

    private static void BindFloat(ConfigFile cfg, string section, string name, float min, float max,
        Func<float> getter, Action<float> setter, string desc = "")
    {
        var e = cfg.Bind(section, name, getter(),
            new ConfigDescription(desc, new AcceptableValueRange<float>(min, max)));
        e.SettingChanged += (_, __) => { if (!_syncing) setter(e.Value); };
        _refreshers.Add(() => e.Value = getter());
    }

    private static void BindEnum<T>(ConfigFile cfg, string section, string name,
        Func<T> getter, Action<T> setter, string desc = "") where T : Enum
    {
        var e = cfg.Bind(section, name, getter(), new ConfigDescription(desc));
        e.SettingChanged += (_, __) => { if (!_syncing) setter(e.Value); };
        _refreshers.Add(() => e.Value = getter());
    }

    private static void BindKey(ConfigFile cfg, string section, string name,
        Func<KeyCode> getter, Action<KeyCode> setter, string desc = "")
    {
        var e = cfg.Bind(section, name, getter(), new ConfigDescription(desc));
        e.SettingChanged += (_, __) => { if (!_syncing) setter(e.Value); };
        _refreshers.Add(() => e.Value = getter());
    }

    private static void BindIntList(ConfigFile cfg, string section, string name, int[] values,
        Func<int> getter, Action<int> setter, string desc = "")
    {
        var e = cfg.Bind(section, name, getter(),
            new ConfigDescription(desc, new AcceptableValueList<int>(values)));
        e.SettingChanged += (_, __) => { if (!_syncing) setter(e.Value); };
        _refreshers.Add(() => e.Value = getter());
    }

    private static void BindPerf(ConfigFile cfg, string section, string name,
        Func<int> getter, Action<int> setter, string desc = "")
    {
        var e = cfg.Bind(section, name, (PerfMode)getter(), new ConfigDescription(desc));
        e.SettingChanged += (_, __) => { if (!_syncing) setter((int)e.Value); };
        _refreshers.Add(() => e.Value = (PerfMode)getter());
    }
}

using System;
using HarmonyLib;
using MenuLib;
using MenuLib.MonoBehaviors;
using MenuLib.Structs;
using UnityEngine;

namespace REPOFidelity;

[HarmonyPatch]
internal static class MenuIntegration
{
    private static REPOPopupPage? _page;
    private static bool _initialized;
    private static bool _syncing;

    private static REPOSlider? _presetSlider, _upscalerSlider, _renderScaleSlider;
    private static REPOSlider? _sharpeningSlider, _aaSlider;
    private static REPOToggle? _pixelationToggle;
    private static REPOSlider? _shadowQualitySlider, _shadowDistanceSlider, _shadowBudgetSlider;
    private static REPOSlider? _lodSlider, _afSlider, _lightsSlider;
    private static REPOSlider? _textureSlider, _lightDistSlider;
    private static REPOSlider? _fogSlider, _viewDistSlider;
    private static REPOToggle? _motionBlurToggle, _caToggle, _lensToggle, _grainToggle;
    private static REPOToggle? _flickerToggle, _overlayToggle;
    private static REPOSlider? _windowModeSlider, _resolutionSlider, _fpsSlider, _gammaSlider;
    private static REPOToggle? _vsyncToggle, _bloomToggle, _glitchToggle;
    private static REPOSlider? _perfExplosionSlider, _perfItemLightSlider;
    private static REPOSlider? _perfAnimLightSlider, _perfParticleSlider, _perfTinySlider;
    private static UnityEngine.UI.Text? _benchHintText;
    private static bool _benchmarkQueued;

    private static readonly string[] FpsOptions = { "30", "60", "90", "120", "144", "240", "Unlimited" };
    private static readonly int[] FpsValues = { 30, 60, 90, 120, 144, 240, -1 };
    private static readonly string[] PerfOptions = { "Auto", "Off", "On" };
    private static readonly int[] PerfValues = { -1, 0, 1 };
    private static readonly string[] AfOptions = { "Off", "2x", "4x", "8x", "16x" };
    private static readonly int[] AfValues = { 0, 2, 4, 8, 16 };
    private static readonly string[] TexOptions = { "Quarter", "Half", "Full" };
    private static readonly TextureRes[] TexValues = { TextureRes.Quarter, TextureRes.Half, TextureRes.Full };

    internal static void Initialize()
    {
        _initialized = true;
        Settings.OnSettingsChanged += SyncModSettings;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MenuPageSettings), nameof(MenuPageSettings.ButtonEventGraphics))]
    public static bool PrefixGraphics()
    {
        if (!_initialized) return true;
        MenuManager.instance.PageCloseAllAddedOnTop();
        OpenPage();
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MenuPageSettings), nameof(MenuPageSettings.ButtonEventAudio))]
    public static void PrefixAudio() => ClosePage();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MenuPageSettings), nameof(MenuPageSettings.ButtonEventGameplay))]
    public static void PrefixGameplay() => ClosePage();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MenuPageSettings), nameof(MenuPageSettings.ButtonEventControls))]
    public static void PrefixControls() => ClosePage();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MenuPageSettings), nameof(MenuPageSettings.ButtonEventBack))]
    public static void PrefixBack() => ClosePage();

    private static void ClosePage()
    {
        try { _page?.ClosePage(true); } catch { }
    }

    private static void OpenPage()
    {
        if (_page == null) CreatePage();
        _page!.OpenPage(openOnTop: true);
        SyncAll();
        UpdateBenchHint();
    }

    private static void UpdateBenchHint()
    {
        if (_benchHintText == null) return;
        if (_benchmarkQueued)
            _benchHintText.text = "  Benchmark queued for next level";
        else if (!SemiFunc.RunIsLevel())
            _benchHintText.text = "  Start a game to auto-tune";
        else
            _benchHintText.text = "";
    }

    private static void CreatePage()
    {
        _page = MenuAPI.CreateREPOPopupPage("Graphics",
            shouldCachePage: true, pageDimmerVisibility: false, spacing: 2f,
            localPosition: new Vector2(0f, 0f));
        _page.maskPadding = new Padding(0, 0, 0, 0);

        AddButton("RESET TO DEFAULT SETTINGS", () => { Settings.Preset = QualityPreset.High; SyncAll(); });

        // Display
        AddLabel("Display");
        AddStringSlider("Window Mode", "", new[] { "Fullscreen", "Windowed" }, "Fullscreen",
            s => GameSet(DataDirector.Setting.WindowMode, s == "Windowed" ? 1 : 0,
                () => GraphicsManager.instance.UpdateWindowMode(true)), out _windowModeSlider);
        var resOptions = Settings.GetAvailableResolutions(out int resIdx);
        AddStringSlider("Resolution", "", resOptions, resOptions[resIdx],
            s => { if (!_syncing) Settings.SetResolution(s); }, out _resolutionSlider);
        AddModToggle("VSync", false,
            b => GameSet(DataDirector.Setting.Vsync, b ? 1 : 0,
                () => GraphicsManager.instance.UpdateVsync()), out _vsyncToggle);
        AddStringSlider("Max FPS", "", FpsOptions, "144", s => {
            if (_syncing) return;
            int idx = Array.IndexOf(FpsOptions, s);
            DataDirector.instance.SettingValueSet(DataDirector.Setting.MaxFPS, idx >= 0 ? FpsValues[idx] : 144);
            GraphicsManager.instance.UpdateMaxFPS();
            DataDirector.instance.SaveSettings();
        }, out _fpsSlider);
        AddIntSlider("Gamma", "Brightness", 0, 100, 40, "",
            v => GameSet(DataDirector.Setting.Gamma, v, () => GraphicsManager.instance.UpdateGamma()), out _gammaSlider);

        // Quality
        AddLabel("Quality");
        var presetNames = new[] { "Auto", "Potato", "Low", "Medium", "High", "Ultra", "Custom" };
        AddStringSlider("Quality Preset", "Sets all options below",
            presetNames, Settings.Preset.ToString(),
            s => { if (!_syncing) Settings.Preset = Enum.Parse<QualityPreset>(s); }, out _presetSlider);

        // Upscaling
        AddLabel("Upscaling");
        var availableUpscalers = GPUDetector.GetAvailableUpscalerNames();
        string currentUpscaler = Settings.UpscaleModeSetting.ToString();
        // DLAA is shown as DLSS in the dropdown (DLSS at 100% = DLAA)
        if (currentUpscaler == "DLAA") currentUpscaler = "DLSS";
        if (currentUpscaler == "FSR_Temporal") currentUpscaler = "FSR";
        if (Array.IndexOf(availableUpscalers, currentUpscaler) < 0) currentUpscaler = "Auto";
        AddStringSlider("Upscaler", "DLSS at 100% = DLAA (native AA)",
            availableUpscalers, currentUpscaler,
            s => {
                // map display names back to enum values
                string enumName = s == "FSR" ? "FSR_Temporal" : s;
                ModSet(() => Settings.UpscaleModeSetting = Enum.Parse<UpscaleMode>(enumName));
            }, out _upscalerSlider);
        AddIntSlider("Render Scale", "Resolution % before upscaling",
            33, 100, Settings.RenderScale, "%",
            v => ModSet(() => Settings.RenderScale = v), out _renderScaleSlider);
        AddFloatSlider("Sharpening", "Post-upscale CAS", 0f, 1f, 2, Settings.Sharpening, "",
            v => ModSet(() => Settings.Sharpening = v), out _sharpeningSlider);
        var aaOptions = new[] { "Auto", "SMAA", "FXAA", "Off" };
        string currentAA = Settings.AntiAliasingMode.ToString();
        if (Array.IndexOf(aaOptions, currentAA) < 0) currentAA = "Auto";
        AddStringSlider("Anti-Aliasing", "SMAA / FXAA",
            aaOptions, currentAA,
            s => ModSet(() => Settings.AntiAliasingMode = Enum.Parse<AAMode>(s)), out _aaSlider);
        AddModToggle("Pixelation (Retro)", Settings.Pixelation,
            b => ModSet(() => Settings.Pixelation = b), out _pixelationToggle);

        // Shadows & Lighting
        AddLabel("Shadows & Lighting");
        AddStringSlider("Shadow Quality", "Shadow map resolution",
            Enum.GetNames(typeof(ShadowQuality)), Settings.ShadowQualitySetting.ToString(),
            s => ModSet(() => Settings.ShadowQualitySetting = Enum.Parse<ShadowQuality>(s)), out _shadowQualitySlider);
        AddFloatSlider("Shadow Distance", "", 5f, 200f, 0, Settings.ShadowDistance, "m",
            v => ModSet(() => Settings.ShadowDistance = v), out _shadowDistanceSlider);
        AddIntSlider("Shadow Limit", "Max nearby shadows (0=unlimited)", 0, 50,
            Settings.ResolvedShadowBudget, "",
            v => ModSet(() => Settings.ShadowBudget = v), out _shadowBudgetSlider);
        AddFloatSlider("Light Distance", "", 10f, 100f, 0, Settings.LightDistance, "m",
            v => ModSet(() => Settings.LightDistance = v), out _lightDistSlider);
        AddIntSlider("Max Lights", "Per object", 1, 16, Settings.PixelLightCount, "",
            v => ModSet(() => Settings.PixelLightCount = v), out _lightsSlider);

        // Textures & Detail
        AddLabel("Textures & Detail");
        int texIdx = Array.IndexOf(TexValues, Settings.TextureQuality);
        if (texIdx < 0) texIdx = 2;
        AddStringSlider("Texture Quality", "", TexOptions, TexOptions[texIdx],
            s => ModSet(() => Settings.TextureQuality = TexValues[Array.IndexOf(TexOptions, s)]), out _textureSlider);
        int afIdx = Array.IndexOf(AfValues, Settings.AnisotropicFiltering);
        if (afIdx < 0) afIdx = 4;
        AddStringSlider("Texture Filtering", "Anisotropic filtering", AfOptions, AfOptions[afIdx],
            s => ModSet(() => Settings.AnisotropicFiltering = AfValues[Array.IndexOf(AfOptions, s)]), out _afSlider);
        AddFloatSlider("Detail Distance", "LOD bias", 0.5f, 4f, 1, Settings.LODBias, "x",
            v => ModSet(() => Settings.LODBias = v), out _lodSlider);

        // Environment
        AddLabel("Environment");
        AddFloatSlider("Fog Distance", "Fog end multiplier", 1f, 1.1f, 2, Settings.FogDistanceMultiplier, "x",
            v => ModSet(() => Settings.FogDistanceMultiplier = v), out _fogSlider);
        AddFloatSlider("Draw Distance", "Camera far clip (0=auto)", 0f, 500f, 0, Settings.ViewDistance, "m",
            v => ModSet(() => Settings.ViewDistance = v), out _viewDistSlider);

        // Post Processing
        AddLabel("Post Processing");
        AddModToggle("Motion Blur", Settings.MotionBlurOverride,
            b => ModSet(() => Settings.MotionBlurOverride = b), out _motionBlurToggle);
        AddModToggle("Chromatic Aberration", Settings.ChromaticAberration,
            b => ModSet(() => Settings.ChromaticAberration = b), out _caToggle);
        AddModToggle("Lens Distortion", Settings.LensDistortion,
            b => ModSet(() => Settings.LensDistortion = b), out _lensToggle);
        AddModToggle("Film Grain", Settings.FilmGrain,
            b => ModSet(() => Settings.FilmGrain = b), out _grainToggle);
        AddModToggle("Bloom", true,
            b => GameSet(DataDirector.Setting.Bloom, b ? 1 : 0,
                () => GraphicsManager.instance.UpdateBloom()), out _bloomToggle);
        AddModToggle("Glitch Loop", true,
            b => GameSet(DataDirector.Setting.GlitchLoop, b ? 1 : 0,
                () => GraphicsManager.instance.UpdateGlitchLoop()), out _glitchToggle);

        // Performance
        AddLabel("Performance");
        AddButton("Auto-Tune (Benchmark)", () =>
        {
            Settings.Preset = QualityPreset.Auto;
            if (SemiFunc.RunIsLevel())
            {
                _page!.ClosePage(false);
                if (MenuManager.instance != null)
                    MenuManager.instance.PageCloseAllAddedOnTop();
                Settings.InvalidateAutoTune();
                Settings.BenchmarkMode = true;
            }
            else
            {
                Settings.InvalidateAutoTune();
                _benchmarkQueued = true;
                UpdateBenchHint();
            }
        });

        AddPerfSlider("Explosion Shadows", "Disable shadows on explosion lights",
            Settings.PerfExplosionShadows, v => ModSet(() => Settings.PerfExplosionShadows = v),
            out _perfExplosionSlider);
        AddPerfSlider("Item Light Shadows", "Disable shadows on flashlights, effects",
            Settings.PerfItemLightShadows, v => ModSet(() => Settings.PerfItemLightShadows = v),
            out _perfItemLightSlider);
        AddPerfSlider("Animated Light Shadows", "Disable shadows on animated lights",
            Settings.PerfAnimatedLightShadows, v => ModSet(() => Settings.PerfAnimatedLightShadows = v),
            out _perfAnimLightSlider);
        AddPerfSlider("Particle Shadows", "Disable shadow casting on particles",
            Settings.PerfParticleShadows, v => ModSet(() => Settings.PerfParticleShadows = v),
            out _perfParticleSlider);
        AddPerfSlider("Small Object Shadows", "Disable shadows on tiny objects",
            Settings.PerfTinyRendererCulling, v => ModSet(() => Settings.PerfTinyRendererCulling = v),
            out _perfTinySlider);

        AddModToggle("Fix Extraction Flicker", Settings.ExtractionPointFlicker,
            b => ModSet(() => Settings.ExtractionPointFlicker = b), out _flickerToggle);
        AddModToggle("Debug Overlay", Settings.DebugOverlay,
            b => ModSet(() => Settings.DebugOverlay = b), out _overlayToggle);
        var keyOpts = new[] { "F10", "F9", "F8", "F7", "F6", "F5" };
        var keyVals = new[] { KeyCode.F10, KeyCode.F9, KeyCode.F8, KeyCode.F7, KeyCode.F6, KeyCode.F5 };
        int keyIdx = Array.IndexOf(keyVals, Settings.ToggleKey);
        if (keyIdx < 0) keyIdx = 0;
        AddStringSlider("Mod Toggle Key", "Disables mod for vanilla comparison",
            keyOpts, keyOpts[keyIdx], s => {
                int i = Array.IndexOf(keyOpts, s);
                if (i >= 0) Settings.ToggleKey = keyVals[i];
            }, out _);

        // hint text — updated each time the page opens
        _page!.AddElementToScrollView(sv =>
        {
            var label = MenuAPI.CreateREPOLabel("", sv, new Vector2(0f, -10f));
            _benchHintText = label.GetComponentInChildren<UnityEngine.UI.Text>();
            return label.rectTransform;
        });
    }

    private static void ModSet(Action a) { if (!_syncing) a(); }

    private static void GameSet(DataDirector.Setting setting, int value, Action update)
    {
        if (_syncing) return;
        DataDirector.instance.SettingValueSet(setting, value);
        update();
        DataDirector.instance.SaveSettings();
    }

    private static void AddLabel(string text) =>
        _page!.AddElementToScrollView(sv => MenuAPI.CreateREPOLabel(text, sv, Vector2.zero).rectTransform);

    private static void AddButton(string text, Action onClick, float xOffset = 38f) =>
        _page!.AddElementToScrollView(sv => MenuAPI.CreateREPOButton(text, onClick, sv, new Vector2(xOffset, 0f)).rectTransform);

    private static void AddStringSlider(string text, string desc, string[] options, string def,
        Action<string> cb, out REPOSlider? r)
    {
        REPOSlider? s = null;
        _page!.AddElementToScrollView(sv => {
            s = MenuAPI.CreateREPOSlider(text, desc, (string v) => cb(v), sv,
                stringOptions: options, defaultOption: def, localPosition: Vector2.zero,
                barBehavior: REPOSlider.BarBehavior.UpdateWithValue);
            return s.rectTransform;
        });
        r = s;
    }

    private static void AddIntSlider(string text, string desc, int min, int max, int def, string post,
        Action<int> cb, out REPOSlider? r)
    {
        REPOSlider? s = null;
        _page!.AddElementToScrollView(sv => {
            s = MenuAPI.CreateREPOSlider(text, desc, (int v) => cb(v), sv,
                localPosition: Vector2.zero, min: min, max: max, defaultValue: def,
                postfix: post, barBehavior: REPOSlider.BarBehavior.UpdateWithValue);
            return s.rectTransform;
        });
        r = s;
    }

    private static void AddFloatSlider(string text, string desc, float min, float max, int prec,
        float def, string post, Action<float> cb, out REPOSlider? r)
    {
        REPOSlider? s = null;
        _page!.AddElementToScrollView(sv => {
            s = MenuAPI.CreateREPOSlider(text, desc, (float v) => cb(v), sv,
                localPosition: Vector2.zero, min: min, max: max, precision: prec,
                defaultValue: def, postfix: post, barBehavior: REPOSlider.BarBehavior.UpdateWithValue);
            return s.rectTransform;
        });
        r = s;
    }

    private static void AddPerfSlider(string text, string desc, int current,
        Action<int> cb, out REPOSlider? r)
    {
        int idx = Array.IndexOf(PerfValues, current);
        if (idx < 0) idx = 0;
        AddStringSlider(text, desc, PerfOptions, PerfOptions[idx],
            s => { int i = Array.IndexOf(PerfOptions, s); if (i >= 0) cb(PerfValues[i]); }, out r);
    }

    private static void AddModToggle(string text, bool def, Action<bool> cb, out REPOToggle? r)
    {
        REPOToggle? t = null;
        _page!.AddElementToScrollView(sv => {
            t = MenuAPI.CreateREPOToggle(text, b => cb(b), sv, Vector2.zero, "ON", "OFF", def);
            return t.rectTransform;
        });
        r = t;
    }


    private static void SyncAll()
    {
        _syncing = true;
        try { SyncGame(); SyncMod(); }
        finally { _syncing = false; }
    }

    private static void SyncModSettings()
    {
        if (_page == null) return;
        _syncing = true;
        try { SyncMod(); }
        finally { _syncing = false; }
    }

    private static void SyncGame()
    {
        int wm = DataDirector.instance.SettingValueFetch(DataDirector.Setting.WindowMode);
        SetStr(_windowModeSlider, wm == 1 ? "Windowed" : "Fullscreen");
        SetStr(_resolutionSlider, $"{Settings.OutputWidth}x{Settings.OutputHeight}");
        _vsyncToggle?.SetState(DataDirector.instance.SettingValueFetch(DataDirector.Setting.Vsync) == 1, false);

        int fps = DataDirector.instance.SettingValueFetch(DataDirector.Setting.MaxFPS);
        int fpsIdx = Array.IndexOf(FpsValues, fps);
        if (fpsIdx < 0) { fpsIdx = 4; for (int i = 0; i < FpsValues.Length; i++) if (FpsValues[i] >= fps && FpsValues[i] != -1) { fpsIdx = i; break; } }
        SetStr(_fpsSlider, FpsOptions[fpsIdx]);

        SetNum(_gammaSlider, DataDirector.instance.SettingValueFetch(DataDirector.Setting.Gamma));
        _bloomToggle?.SetState(DataDirector.instance.SettingValueFetch(DataDirector.Setting.Bloom) == 1, false);
        _glitchToggle?.SetState(DataDirector.instance.SettingValueFetch(DataDirector.Setting.GlitchLoop) == 1, false);
    }

    private static void SyncMod()
    {
        SetStr(_presetSlider, Settings.Preset.ToString());
        string upName = Settings.UpscaleModeSetting.ToString();
        if (upName == "DLAA") upName = "DLSS";
        if (upName == "FSR_Temporal") upName = "FSR";
        SetStr(_upscalerSlider, upName);
        // Show the resolved (clamped) render scale so slider reflects actual applied value
        SetNum(_renderScaleSlider, Settings.ResolvedRenderScale);
        SetNum(_sharpeningSlider, Settings.Sharpening);
        // TAA is removed from dropdown — show what it resolves to
        string aaDisplay = Settings.AntiAliasingMode == AAMode.TAA
            ? Settings.ResolvedAAMode.ToString()
            : Settings.AntiAliasingMode.ToString();
        SetStr(_aaSlider, aaDisplay);
        _pixelationToggle?.SetState(Settings.Pixelation, false);
        SetStr(_shadowQualitySlider, Settings.ShadowQualitySetting.ToString());
        SetNum(_shadowDistanceSlider, Settings.ShadowDistance);
        SetNum(_shadowBudgetSlider, Settings.ResolvedShadowBudget);
        SetNum(_lodSlider, Settings.LODBias);

        int afIdx = Array.IndexOf(AfValues, Settings.AnisotropicFiltering);
        if (afIdx >= 0) SetStr(_afSlider, AfOptions[afIdx]);

        SetNum(_lightsSlider, Settings.PixelLightCount);
        int texIdx = Array.IndexOf(TexValues, Settings.TextureQuality);
        if (texIdx >= 0) SetStr(_textureSlider, TexOptions[texIdx]);
        SetNum(_lightDistSlider, Settings.LightDistance);
        SetNum(_fogSlider, Settings.FogDistanceMultiplier);
        SetNum(_viewDistSlider, Settings.ViewDistance);
        _motionBlurToggle?.SetState(Settings.MotionBlurOverride, false);
        _caToggle?.SetState(Settings.ChromaticAberration, false);
        _lensToggle?.SetState(Settings.LensDistortion, false);
        _grainToggle?.SetState(Settings.FilmGrain, false);
        _flickerToggle?.SetState(Settings.ExtractionPointFlicker, false);
        _overlayToggle?.SetState(Settings.DebugOverlay, false);
        SyncPerf(_perfExplosionSlider, Settings.PerfExplosionShadows);
        SyncPerf(_perfItemLightSlider, Settings.PerfItemLightShadows);
        SyncPerf(_perfAnimLightSlider, Settings.PerfAnimatedLightShadows);
        SyncPerf(_perfParticleSlider, Settings.PerfParticleShadows);
        SyncPerf(_perfTinySlider, Settings.PerfTinyRendererCulling);
    }

    private static void SyncPerf(REPOSlider? s, int value)
    {
        int idx = Array.IndexOf(PerfValues, value);
        if (idx >= 0) SetStr(s, PerfOptions[idx]);
    }

    private static void SetNum(REPOSlider? s, float v) => s?.SetValue(v, false);
    private static void SetStr(REPOSlider? s, string opt)
    {
        if (s?.stringOptions == null) return;
        int i = Array.IndexOf(s.stringOptions, opt);
        if (i >= 0) s.SetValue(i, false);
    }
}

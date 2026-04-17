using System;
using System.IO;
using UnityEngine;

namespace REPOFidelity;

internal enum QualityPreset { Potato, Low, Medium, High, Ultra, Custom, Auto }
internal enum UpscaleMode { Auto, DLAA, DLSS, FSR4, FSR_Temporal, FSR, Off }
internal enum AAMode { Auto, TAA, SMAA, FXAA, Off }
internal enum ShadowQuality { Low, Medium, High, Ultra }
internal enum TextureRes { Full, Half, Quarter }

internal static class Settings
{
    private static SettingsFile _file = null!;
    private static SettingsData D => _file.Data;
    private static bool _initComplete;

    internal static QualityPreset Preset
    {
        get => (QualityPreset)D.preset;
        set { D.preset = (int)value; _file.Save(); OnChanged(); }
    }
    internal static int OutputWidth
    {
        get => D.resWidth > 0 ? D.resWidth : Screen.width;
        set { D.resWidth = value; _file.Save(); OnChanged(); }
    }
    internal static int OutputHeight
    {
        get => D.resHeight > 0 ? D.resHeight : Screen.height;
        set { D.resHeight = value; _file.Save(); OnChanged(); }
    }

    internal static UpscaleMode UpscaleModeSetting
    {
        get => (UpscaleMode)D.upscaler;
        set { D.upscaler = (int)value; _file.Save(); OnSettingTweaked(); }
    }
    internal static int RenderScale
    {
        get => D.renderScale;
        set { D.renderScale = Mathf.Clamp(value, 33, 100); _file.Save(); OnSettingTweaked(); }
    }
    internal static float Sharpening
    {
        get => D.sharpening;
        // per-frame uniform — skip the pipeline rebuild, still mark Custom.
        set
        {
            D.sharpening = Mathf.Clamp(value, 0f, 1f);
            _file.Save();
            if (_initComplete && Preset != QualityPreset.Custom)
            {
                _file.SuppressEvents(() => D.preset = (int)QualityPreset.Custom);
                _file.Save();
            }
        }
    }
    internal static AAMode AntiAliasingMode
    {
        get => (AAMode)D.aaMode;
        set { D.aaMode = (int)value; _file.Save(); OnSettingTweaked(); }
    }
    internal static bool Pixelation
    {
        get => D.pixelation;
        set { D.pixelation = value; _file.Save(); OnSettingTweaked(); }
    }
    internal static ShadowQuality ShadowQualitySetting
    {
        get => (ShadowQuality)D.shadowQuality;
        set { D.shadowQuality = (int)value; _file.Save(); OnSettingTweaked(); }
    }
    internal static float ShadowDistance
    {
        get => D.shadowDistance;
        set { D.shadowDistance = Mathf.Clamp(value, 5f, 200f); _file.Save(); OnSettingTweaked(); }
    }
    internal static float LODBias
    {
        get => D.lodBias;
        set { D.lodBias = Mathf.Clamp(value, 0.5f, 4f); _file.Save(); OnSettingTweaked(); }
    }
    internal static int AnisotropicFiltering
    {
        get => D.anisotropicFiltering;
        set { D.anisotropicFiltering = value; _file.Save(); OnSettingTweaked(); }
    }
    internal static int PixelLightCount
    {
        get => D.pixelLightCount;
        set { D.pixelLightCount = Mathf.Clamp(value, 1, 16); _file.Save(); OnSettingTweaked(); }
    }
    internal static TextureRes TextureQuality
    {
        get => (TextureRes)D.textureQuality;
        set { D.textureQuality = (int)value; _file.Save(); OnSettingTweaked(); }
    }
    internal static float LightDistance
    {
        get => D.lightDistance;
        set { D.lightDistance = Mathf.Clamp(value, 10f, 100f); _file.Save(); OnSettingTweaked(); }
    }
    internal static float FogDistanceMultiplier
    {
        get => D.fogMultiplier;
        set { D.fogMultiplier = Mathf.Clamp(value, 0.3f, 1.1f); _file.Save(); OnSettingTweaked(); }
    }
    internal static float ViewDistance
    {
        get => D.viewDistance;
        set { D.viewDistance = Mathf.Clamp(value, 0f, 500f); _file.Save(); OnSettingTweaked(); }
    }
    internal static bool MotionBlurOverride
    {
        get => D.motionBlur;
        set { D.motionBlur = value; _file.Save(); OnSettingTweaked(); }
    }
    internal static bool ChromaticAberration
    {
        get => D.chromaticAberration;
        set { D.chromaticAberration = value; _file.Save(); OnSettingTweaked(); }
    }
    internal static bool LensDistortion
    {
        get => D.lensDistortion;
        set { D.lensDistortion = value; _file.Save(); OnSettingTweaked(); }
    }
    internal static bool FilmGrain
    {
        get => D.filmGrain;
        set { D.filmGrain = value; _file.Save(); OnSettingTweaked(); }
    }
    internal static bool ExtractionPointFlicker
    {
        get => D.extractionFlickerFix;
        set { D.extractionFlickerFix = value; _file.Save(); OnSettingTweaked(); }
    }
    internal static KeyCode ToggleKey
    {
        get => (KeyCode)D.toggleKey;
        set { D.toggleKey = (int)value; _file.Save(); }
    }
    internal static bool DebugOverlay
    {
        get => D.debugOverlay;
        set { D.debugOverlay = value; _file.Save(); }
    }
    internal static bool BenchmarkMode
    {
        get => D.benchmark;
        set { D.benchmark = value; _file.Save(); }
    }
    internal static bool AutoConfigured => !_autoTune.IsStale();

    internal static void InvalidateAutoTune()
    {
        _autoTune.version = "";
    }

    internal static bool ModEnabled = true;

    internal static bool CpuBound => _autoTune.IsStale() || _autoTune.cpuBound;

    // --- CPU optimizations ---
    // User-facing toggles (saved to file). -1 = auto, 0 = off, 1 = on.
    // Auto mode: patches activate only when frame time is high enough
    // that the savings outweigh Harmony overhead (~8ms threshold).
    internal static int CpuPatchMode
    {
        get => D.cpuPatchMode;
        set { D.cpuPatchMode = value; _file.Save(); }
    }

    // Runtime gate — updated every 0.5s, true when CPU patches should be active.
    // When CpuPatchMode is Auto (-1), this tracks rolling frame time.
    // When forced (0 or 1), returns the forced value.
    internal static bool CpuPatchesActive { get; private set; } = true;

    private static float _cpuGateTimer;
    private static float _cpuGateAccum;
    private static int _cpuGateFrames;
    private const float CpuGateThresholdMs = 8f; // ~125 FPS — below this, patches cost more than they save

    internal static void UpdateCpuGate()
    {
        if (CpuPatchMode == 1) { CpuPatchesActive = true; return; }
        if (CpuPatchMode == 0) { CpuPatchesActive = false; return; }

        // auto mode: sample frame time over 0.5s windows
        _cpuGateAccum += UnityEngine.Time.unscaledDeltaTime;
        _cpuGateFrames++;
        _cpuGateTimer += UnityEngine.Time.unscaledDeltaTime;

        if (_cpuGateTimer >= 0.5f && _cpuGateFrames > 0)
        {
            float avgMs = (_cpuGateAccum / _cpuGateFrames) * 1000f;
            CpuPatchesActive = avgMs >= CpuGateThresholdMs;
            _cpuGateTimer = 0f;
            _cpuGateAccum = 0f;
            _cpuGateFrames = 0;
        }
    }

    internal static int ShadowBudget
    {
        get => D.shadowBudget;
        set { D.shadowBudget = value; _file.Save(); OnSettingTweaked(); }
    }
    internal static int ResolvedShadowBudget;

    // lowest fog multiplier presets/auto-tune may assign — manual slider can go lower.
    // Placeholder value, revisit after tester feedback on what's actually playable.
    internal const float PlayableFogFloor = 0.5f;

    // per-optimization toggles for Custom preset. -1 = auto (follow preset logic)
    internal static int PerfExplosionShadows
    {
        get => D.perfExplosionShadows;
        set { D.perfExplosionShadows = value; _file.Save(); }
    }
    internal static int PerfItemLightShadows
    {
        get => D.perfItemLightShadows;
        set { D.perfItemLightShadows = value; _file.Save(); }
    }
    internal static int PerfAnimatedLightShadows
    {
        get => D.perfAnimatedLightShadows;
        set { D.perfAnimatedLightShadows = value; _file.Save(); }
    }
    internal static int PerfParticleShadows
    {
        get => D.perfParticleShadows;
        set { D.perfParticleShadows = value; _file.Save(); }
    }
    internal static int PerfTinyRendererCulling
    {
        get => D.perfTinyRendererCulling;
        set { D.perfTinyRendererCulling = value; _file.Save(); }
    }
    internal static int PerfDistanceShadowCulling
    {
        get => D.perfDistanceShadowCulling;
        set { D.perfDistanceShadowCulling = value; _file.Save(); }
    }

    // check whether a specific optimization should be active.
    // for non-Custom presets, uses the tier system.
    // for Custom, checks the per-toggle override first, falls back to tier.
    internal static bool ShouldOptimize(PerfOpt opt)
    {
        // F10 disables the mod — respect that for visual changes
        if (!ModEnabled) return false;

        // Custom preset with explicit override
        if (Preset == QualityPreset.Custom)
        {
            int toggle = opt switch
            {
                PerfOpt.ExplosionShadows => D.perfExplosionShadows,
                PerfOpt.ParticleShadows => D.perfParticleShadows,
                PerfOpt.ItemLightShadows => D.perfItemLightShadows,
                PerfOpt.TinyRendererCulling => D.perfTinyRendererCulling,
                PerfOpt.AnimatedLightShadows => D.perfAnimatedLightShadows,
                PerfOpt.DistanceShadowCulling => D.perfDistanceShadowCulling,
                _ => -1,
            };
            if (toggle == 0) return false;
            if (toggle == 1) return true;
            // -1 = auto, fall through to tier logic
        }

        int level = Preset switch
        {
            QualityPreset.Ultra => 0,
            QualityPreset.High => 1,
            QualityPreset.Medium => 2,
            QualityPreset.Auto => _autoTune.perfLevel,
            QualityPreset.Custom => ResolvedShadowQuality switch
            {
                ShadowQuality.Ultra => 0,
                ShadowQuality.High => 1,
                ShadowQuality.Medium => 2,
                _ => 3,
            },
            _ => 3,
        };

        return opt switch
        {
            PerfOpt.ExplosionShadows => level >= 1,
            PerfOpt.ParticleShadows => level >= 1,
            PerfOpt.ItemLightShadows => level >= 2,
            PerfOpt.TinyRendererCulling => level >= 2,
            PerfOpt.AnimatedLightShadows => level >= 3,
            PerfOpt.DistanceShadowCulling => level >= 0, // always on when mod enabled
            _ => false,
        };
    }

    internal enum PerfOpt
    {
        ExplosionShadows,
        ParticleShadows,
        ItemLightShadows,
        TinyRendererCulling,
        AnimatedLightShadows,
        DistanceShadowCulling,
    }

    internal static UpscaleMode ResolvedUpscaleMode;
    internal static int ResolvedRenderScale;
    internal static AAMode ResolvedAAMode;
    internal static ShadowQuality ResolvedShadowQuality;
    internal static float ResolvedShadowDistance;
    internal static float ResolvedLODBias;
    internal static int ResolvedPixelLightCount;
    internal static float ResolvedLightDistance;
    internal static float ResolvedFogMultiplier;
    // fog end distance (meters) after ResolvedFogMultiplier is applied; populated when fog is written
    internal static float ResolvedEffectiveFogEnd;
    internal static float ResolvedViewDistance;
    internal static int ResolvedAnisotropicFiltering;
    internal static TextureRes ResolvedTextureQuality;

    internal static event Action? OnSettingsChanged;

    private static AutoTuneData _autoTune = new();
    private static string _autoTunePath = "";

    internal static bool AutoTuneNeedsBenchmark => _autoTune.IsStale();

    internal static void Init()
    {
        string dir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
        _file = new SettingsFile(Path.Combine(dir, "settings.json"));
        _file.Changed += () => { ResolveAutoDefaults(); OnSettingsChanged?.Invoke(); };

        // load auto-tune profile
        _autoTunePath = Path.Combine(dir, "autotune.json");
        LoadAutoTune();

        _initComplete = true;
        MigrateOldConfig(dir);
    }

    private static void LoadAutoTune()
    {
        if (!File.Exists(_autoTunePath)) return;
        try
        {
            var loaded = JsonUtility.FromJson<AutoTuneData>(File.ReadAllText(_autoTunePath));
            if (loaded != null) _autoTune = loaded;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Failed to load autotune: {ex.Message}");
        }
    }

    internal static void SaveAutoTune(AutoTuneData data)
    {
        _autoTune = data;
        try
        {
            File.WriteAllText(_autoTunePath, JsonUtility.ToJson(data, true));
            Plugin.Log.LogInfo("Auto-tune profile saved");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Failed to save autotune: {ex.Message}");
        }

        if (Preset == QualityPreset.Auto)
        {
            ResolveAutoDefaults();
            OnSettingsChanged?.Invoke();
        }
    }

    private static void MigrateOldConfig(string pluginDir)
    {
        // nuke old BepInEx cfg so REPOConfig stops showing our entries
        try
        {
            string oldCfg = Path.Combine(pluginDir, "..", "..", "config", "Vippy.REPOFidelity.cfg");
            if (File.Exists(oldCfg))
            {
                File.Delete(oldCfg);
                Plugin.Log.LogInfo("Deleted old BepInEx config");
            }
        }
        catch { }
    }

    private static void OnChanged()
    {
        if (!_initComplete) return;
        _file.NotifyChanged();
    }

    private static void OnSettingTweaked()
    {
        if (!_initComplete) return;
        if (Preset != QualityPreset.Custom)
        {
            _file.SuppressEvents(() => D.preset = (int)QualityPreset.Custom);
            _file.Save();
        }
        _file.NotifyChanged();
    }

    internal static void BatchUpdate(Action action)
    {
        _file.SuppressEvents(action);
        _file.Save();
        ResolveAutoDefaults();
        OnSettingsChanged?.Invoke();
    }

    public static void ResolveAutoDefaults()
    {
        var preset = Preset;

        if (preset != QualityPreset.Custom)
        {
            ApplyPreset(preset);
            SyncCustomToResolved();
        }
        else
        {
            ResolvedUpscaleMode = UpscaleModeSetting switch
            {
                UpscaleMode.Auto => BestUpscaler(UpscaleTier.Quality),
                UpscaleMode.FSR => UpscaleMode.FSR_Temporal,
                UpscaleMode.FSR4 => UpscaleMode.FSR_Temporal,
                _ => UpscaleModeSetting
            };

            if (!GPUDetector.IsUpscalerSupported(ResolvedUpscaleMode))
            {
                Plugin.Log.LogWarning($"{ResolvedUpscaleMode} not supported — falling back to Auto");
                ResolvedUpscaleMode = BestUpscaler(UpscaleTier.Quality);
            }

            int minScale = MinRenderScale(ResolvedUpscaleMode);

            // DLSS at 100% is DLAA (native-res AA, no upscaling)
            if (ResolvedUpscaleMode == UpscaleMode.DLSS && RenderScale >= 100)
                ResolvedUpscaleMode = UpscaleMode.DLAA;
            // legacy DLAA selection — keep at 100%
            if (ResolvedUpscaleMode == UpscaleMode.DLAA)
                ResolvedRenderScale = 100;
            else
                ResolvedRenderScale = Mathf.Clamp(RenderScale, minScale, 100);

            bool hasTemporalUpscaler = ResolvedUpscaleMode is UpscaleMode.DLAA
                or UpscaleMode.DLSS or UpscaleMode.FSR_Temporal;

            if (AntiAliasingMode == AAMode.Auto)
            {
                // Auto: temporal upscalers already provide AA, no need for post-process AA
                ResolvedAAMode = hasTemporalUpscaler ? AAMode.Off : AAMode.SMAA;
            }
            else if (AntiAliasingMode == AAMode.TAA)
            {
                // TAA removed — treat as SMAA
                ResolvedAAMode = hasTemporalUpscaler ? AAMode.Off : AAMode.SMAA;
            }
            else
            {
                // SMAA/FXAA are edge-based, safe to stack with temporal upscalers
                ResolvedAAMode = AntiAliasingMode;
            }
            ResolvedShadowQuality = ShadowQualitySetting;
            ResolvedShadowDistance = ShadowDistance;
            ResolvedLODBias = LODBias;
            ResolvedPixelLightCount = PixelLightCount;
            ResolvedLightDistance = LightDistance;
            ResolvedFogMultiplier = FogDistanceMultiplier;
            ResolvedViewDistance = ViewDistance;
            ResolvedAnisotropicFiltering = AnisotropicFiltering;
            ResolvedTextureQuality = TextureQuality;
            ResolvedShadowBudget = ShadowBudget == -1
                ? ResolvedShadowQuality switch
                {
                    ShadowQuality.Ultra => 25,
                    ShadowQuality.High => 20,
                    ShadowQuality.Medium => 15,
                    _ => 10,
                }
                : ShadowBudget;
        }

        ApplyFogClamps();

        Plugin.Log.LogInfo($"Resolved [{preset}]: {ResolvedUpscaleMode} {ResolvedRenderScale}% " +
            $"AA={ResolvedAAMode} shadows={ResolvedShadowQuality}/{ResolvedShadowDistance}m " +
            $"LOD={ResolvedLODBias} lights={ResolvedPixelLightCount} " +
            $"fogEnd={ResolvedEffectiveFogEnd:F0}m lightDist={ResolvedLightDistance:F0}m");
    }

    // ceiling shadow and light distances at small multiples of the effective fog end.
    // renderings past fog are invisible; the overshoot keeps transitions smooth
    // for casters partially inside the fog transition band.
    internal static void ApplyFogClamps()
    {
        float end = ResolvedEffectiveFogEnd;
        if (end <= 0f) return; // fog not yet captured; clamp will re-run when it is
        ResolvedShadowDistance = Mathf.Min(ResolvedShadowDistance, end * 1.1f);
        ResolvedLightDistance = Mathf.Min(ResolvedLightDistance, end * 1.2f);
    }

    private static void SyncCustomToResolved()
    {
        _file.SuppressEvents(() =>
        {
            // Write the user-facing upscaler (not resolved) so DLSS at 100%
            // doesn't permanently lock to DLAA when the user lowers the slider.
            var userUpscaler = ResolvedUpscaleMode == UpscaleMode.DLAA
                ? UpscaleMode.DLSS : ResolvedUpscaleMode;
            D.upscaler = (int)userUpscaler;
            D.renderScale = ResolvedRenderScale;
            D.aaMode = (int)ResolvedAAMode;
            D.shadowQuality = (int)ResolvedShadowQuality;
            D.shadowDistance = ResolvedShadowDistance;
            D.lodBias = ResolvedLODBias;
            D.pixelLightCount = ResolvedPixelLightCount;
            D.lightDistance = ResolvedLightDistance;
            D.fogMultiplier = ResolvedFogMultiplier;
            D.viewDistance = ResolvedViewDistance;
            D.sharpening = Preset switch
            {
                QualityPreset.Potato => 0f,
                QualityPreset.Low => 0f,
                QualityPreset.Medium => 0f,
                QualityPreset.High => 0.5f,
                QualityPreset.Ultra => 0.3f,
                QualityPreset.Auto => _autoTune.sharpening,
                _ => D.sharpening
            };
            D.anisotropicFiltering = ResolvedAnisotropicFiltering;
            D.textureQuality = (int)ResolvedTextureQuality;
            D.shadowBudget = ResolvedShadowBudget;
        });
        _file.Save();
    }

    internal static string[] GetAvailableResolutions(out int currentIndex)
    {
        var native = Screen.currentResolution;
        float nativeAspect = (float)native.width / native.height;
        int minHeight = 720;

        var seen = new System.Collections.Generic.HashSet<string>();
        var results = new System.Collections.Generic.List<string>();

        foreach (var r in Screen.resolutions)
        {
            if (r.height < minHeight) continue;
            float aspect = (float)r.width / r.height;
            if (Mathf.Abs(aspect - nativeAspect) > 0.02f) continue;

            string key = $"{r.width}x{r.height}";
            if (!seen.Add(key)) continue;
            results.Add(key);
        }

        string nativeKey = $"{native.width}x{native.height}";
        if (!seen.Contains(nativeKey))
            results.Add(nativeKey);

        results.Sort((a, b) =>
        {
            int wa = int.Parse(a.Split('x')[0]);
            int wb = int.Parse(b.Split('x')[0]);
            return wa.CompareTo(wb);
        });

        string current = $"{OutputWidth}x{OutputHeight}";
        currentIndex = results.IndexOf(current);
        if (currentIndex < 0) currentIndex = 0;

        return results.ToArray();
    }

    internal static void SetResolution(string wxh)
    {
        var parts = wxh.Split('x');
        if (parts.Length != 2) return;
        int w = int.Parse(parts[0]);
        int h = int.Parse(parts[1]);
        D.resWidth = w;
        D.resHeight = h;
        _file.Save();

        Screen.SetResolution(w, h, Screen.fullScreen);
        OnChanged();

        Plugin.Log.LogInfo($"Resolution: {w}x{h}");
    }

    internal static int MinRenderScale(UpscaleMode mode) => mode switch
    {
        UpscaleMode.DLSS => 33,           // hardware upscaler handles ultra-low
        UpscaleMode.DLAA => 100,          // native-res AA
        UpscaleMode.FSR4 => 50,
        UpscaleMode.FSR_Temporal => 50,
        UpscaleMode.FSR => 50,            // EASU floor
        UpscaleMode.Off => 50,            // game's native scaling handles sub-100 via pixelation
        _ => 50
    };

    private enum UpscaleTier { Budget, Quality, NativeAA }

    private static UpscaleMode BestUpscaler(UpscaleTier tier) => tier switch
    {
        // budget: cheapest usable upscaler — FSR Temporal beats FSR spatial on
        // both cost and quality per benchmark data. iGPU falls back to Off
        // because even FSR's shader blit costs more than the savings on HD/UHD.
        UpscaleTier.Budget => GPUDetector.IsIntegratedGpu ? UpscaleMode.Off : UpscaleMode.FSR_Temporal,
        // native AA: best AA at 100% scale — DLAA on NVIDIA, FSR Temporal
        // everywhere else (AMD / Intel dGPU / Apple Silicon).
        UpscaleTier.NativeAA => GPUDetector.DlssAvailable ? UpscaleMode.DLAA
            : UpscaleMode.FSR_Temporal,
        // quality: best upscaler available — DLSS on NVIDIA RTX,
        // FSR Temporal on AMD / Intel dGPU / Apple Silicon.
        _ => GPUDetector.DlssAvailable
            ? UpscaleMode.DLSS : UpscaleMode.FSR_Temporal,
    };

    internal static void ApplyPreset(QualityPreset preset)
    {
        bool cpu = CpuBound;

        switch (preset)
        {
            case QualityPreset.Potato:
                // max perf, matches vanilla's 50% render scale. cut everything else.
                ResolvedUpscaleMode = UpscaleMode.Off;
                ResolvedRenderScale = 50;
                ResolvedAAMode = AAMode.Off;
                ResolvedShadowQuality = ShadowQuality.Low; ResolvedShadowDistance = 10f;
                ResolvedLODBias = 0.5f; ResolvedPixelLightCount = 2;
                ResolvedLightDistance = 10f; ResolvedFogMultiplier = 1f; ResolvedViewDistance = 0f;
                ResolvedAnisotropicFiltering = 2; ResolvedTextureQuality = TextureRes.Full;
                ResolvedShadowBudget = 5;
                break;
            case QualityPreset.Low:
                // same 50% res as Potato but SMAA + better shadows/LOD/lights.
                // should still beat or match vanilla FPS.
                ResolvedUpscaleMode = UpscaleMode.Off;
                ResolvedRenderScale = cpu ? 100 : 50;
                ResolvedAAMode = AAMode.SMAA;
                ResolvedShadowQuality = ShadowQuality.Low; ResolvedShadowDistance = 20f;
                ResolvedLODBias = 1f; ResolvedPixelLightCount = 4;
                ResolvedLightDistance = 20f; ResolvedFogMultiplier = 1f; ResolvedViewDistance = 0f;
                ResolvedAnisotropicFiltering = 4; ResolvedTextureQuality = TextureRes.Full;
                ResolvedShadowBudget = 10;
                break;
            case QualityPreset.Medium:
                // 75% + SMAA. Big visual jump. No upscaler pipeline.
                ResolvedUpscaleMode = UpscaleMode.Off;
                ResolvedRenderScale = cpu ? 100 : 75;
                ResolvedAAMode = AAMode.SMAA;
                ResolvedShadowQuality = ShadowQuality.Medium; ResolvedShadowDistance = 30f;
                ResolvedLODBias = 1.5f; ResolvedPixelLightCount = 6;
                ResolvedLightDistance = 25f; ResolvedFogMultiplier = 1f; ResolvedViewDistance = 0f;
                ResolvedAnisotropicFiltering = 8; ResolvedTextureQuality = TextureRes.Full;
                ResolvedShadowBudget = 15;
                break;
            case QualityPreset.High:
                // 100% native + upscaler AA. Premium look.
                if (cpu)
                {
                    ResolvedUpscaleMode = GPUDetector.DlssAvailable ? UpscaleMode.DLAA : UpscaleMode.Off;
                    ResolvedRenderScale = 100;
                    ResolvedAAMode = GPUDetector.DlssAvailable ? AAMode.Off : AAMode.SMAA;
                }
                else
                {
                    ResolvedUpscaleMode = BestUpscaler(UpscaleTier.Quality);
                    ResolvedRenderScale = 75;
                    ResolvedAAMode = AAMode.Off;
                }
                ResolvedShadowQuality = ShadowQuality.High; ResolvedShadowDistance = 85f;
                ResolvedLODBias = 3f; ResolvedPixelLightCount = 8;
                ResolvedLightDistance = 45f; ResolvedFogMultiplier = 1.1f; ResolvedViewDistance = 0f;
                ResolvedAnisotropicFiltering = 16; ResolvedTextureQuality = TextureRes.Full;
                ResolvedShadowBudget = 20;
                break;
            case QualityPreset.Ultra:
                // 100% + best upscaler + maxed everything
                if (cpu)
                {
                    ResolvedUpscaleMode = GPUDetector.DlssAvailable ? UpscaleMode.DLAA : UpscaleMode.Off;
                    ResolvedRenderScale = 100;
                    ResolvedAAMode = GPUDetector.DlssAvailable ? AAMode.Off : AAMode.SMAA;
                }
                else
                {
                    ResolvedUpscaleMode = BestUpscaler(UpscaleTier.NativeAA);
                    ResolvedRenderScale = 100;
                    ResolvedAAMode = AAMode.Off;
                }
                ResolvedShadowQuality = ShadowQuality.Ultra; ResolvedShadowDistance = 150f;
                ResolvedLODBias = 4f; ResolvedPixelLightCount = 16;
                ResolvedLightDistance = 75f; ResolvedFogMultiplier = 1.1f; ResolvedViewDistance = 0f;
                ResolvedAnisotropicFiltering = 16; ResolvedTextureQuality = TextureRes.Full;
                ResolvedShadowBudget = 25;
                break;
            case QualityPreset.Auto:
                ApplyAutoTune();
                break;
        }
    }

    private static void ApplyAutoTune()
    {
        if (_autoTune.IsStale())
        {
            // no valid autotune yet — fall back to High until benchmark runs
            Plugin.Log.LogInfo("Auto: no valid autotune profile, using High as fallback");
            ApplyPreset(QualityPreset.High);
            return;
        }

        var at = _autoTune;
        ResolvedUpscaleMode = (UpscaleMode)at.upscaler;
        ResolvedRenderScale = at.renderScale;
        ResolvedAAMode = (AAMode)at.aaMode;
        ResolvedShadowQuality = (ShadowQuality)at.shadowQuality;
        ResolvedShadowDistance = at.shadowDistance;
        ResolvedLODBias = at.lodBias;
        ResolvedPixelLightCount = at.pixelLightCount;
        ResolvedLightDistance = at.lightDistance;
        ResolvedFogMultiplier = at.fogMultiplier;
        ResolvedViewDistance = at.viewDistance;
        ResolvedAnisotropicFiltering = at.anisotropicFiltering;
        ResolvedTextureQuality = TextureRes.Full;
        ResolvedShadowBudget = ResolvedShadowQuality switch
        {
            ShadowQuality.Ultra => 25,
            ShadowQuality.High => 20,
            ShadowQuality.Medium => 15,
            _ => 10,
        };
    }

    internal static void AutoSelectPreset(float avgFpsRaw, float low1Fps, float low01Fps, bool cpuBound = false)
    {
        int refresh = Mathf.Max(Screen.currentResolution.refreshRate, 60);
        float target = refresh;

        // Weighted composite — average dominates for steady scenes, lows
        // punish systems with hitches. 50/30/20 matches how smoothness
        // actually feels; spikes show up in the 1 % and 0.1 % buckets.
        float p01 = low01Fps > 0 ? low01Fps : low1Fps;
        float weighted = avgFpsRaw * 0.5f + low1Fps * 0.3f + p01 * 0.2f;

        // Scene complexity factor: sparse benchmark scene means real gameplay
        // will hit denser areas, so scale down primary fps to keep headroom.
        // ~3000 MeshRenderers is typical; clamp bounds prevent wild swings.
        int renderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>().Length;
        float sceneFactor = Mathf.Clamp(3000f / Mathf.Max(renderers, 1500f), 0.85f, 1.0f);

        // Thermal / GC / driver-variance cushion on top of the scene factor.
        const float ThermalSafety = 0.92f;

        float avgFps = weighted * sceneFactor * ThermalSafety;

        Plugin.Log.LogInfo($"Auto-tune inputs: avg={avgFpsRaw:F0}  1%={low1Fps:F0}  0.1%={p01:F0}  " +
            $"renderers={renderers}  weighted={weighted:F0}  sceneFactor={sceneFactor:F2}  " +
            $"primary={avgFps:F0}  target={target}Hz  {(cpuBound ? "CPU-BOUND" : "gpu-bound")}");

        // start from Ultra
        int scale = 100;
        var shQ = ShadowQuality.Ultra;
        float shD = 150f, lod = 4f, lDist = 75f, sharp = 0.3f, fog = 1.1f;
        int lights = 16, af = 16;
        var tex = TextureRes.Full;

        // pick upscaler based on bottleneck
        // CPU-bound: temporal upscaling adds CPU overhead for zero gain, use SMAA.
        // GPU-bound: temporal upscaler reduces fill rate.
        UpscaleMode upscaler;
        AAMode aa;

        if (cpuBound)
        {
            // DLAA is free on NVIDIA tensor cores — use it even when CPU-bound
            if (GPUDetector.DlssAvailable)
            {
                upscaler = UpscaleMode.DLAA;
                aa = AAMode.Off;
                Plugin.Log.LogInfo("CPU-bound + NVIDIA: using DLAA");
            }
            else
            {
                upscaler = UpscaleMode.Off;
                aa = AAMode.SMAA;
                Plugin.Log.LogInfo("CPU-bound: using SMAA");
            }
        }
        else
        {
            upscaler = BestUpscaler(UpscaleTier.Quality);
            aa = AAMode.Off; // temporal upscalers handle AA
        }

        float ultraCost = GpuCost(scale, shQ, shD, lod, lights, lDist, af, tex, fog);
        float curCost = GpuCost(ResolvedRenderScale, ResolvedShadowQuality,
            ResolvedShadowDistance, ResolvedLODBias, ResolvedPixelLightCount,
            ResolvedLightDistance, ResolvedAnisotropicFiltering, ResolvedTextureQuality,
            ResolvedFogMultiplier);

        float estUltra = avgFps * (curCost / ultraCost);
        float budget = estUltra / target;
        int minScale = MinRenderScale(upscaler);

        if (cpuBound)
        {
            // CPU-bound: GpuCost does NOT predict CPU savings. Pure-GPU knobs
            // (AF, LOD, fog, shadow resolution, texture quality) don't reduce
            // main-thread work, so they stay maxed — the GPU already has
            // headroom by definition on a CPU-bound system. Step only knobs
            // that reduce draw-call count (shadow distance shrinks the caster
            // set, light count reduces forward passes per lit mesh, light
            // distance kills far lights). Budget here is the direct measured
            // FPS ratio, not a model.
            float headroom = avgFps / target;
            Plugin.Log.LogInfo($"CPU-bound: headroom={headroom:F2}  (avgFps={avgFps:F0}  target={target:F0})");

            if (headroom < 0.95f)
            {
                // Staircase of draw-call reductions. Farthest/least-visible
                // shadow casters fade first; pixel lights drop only when we
                // can't meet target from shadow cuts alone.
                shD = 100f;
                if (headroom < 0.90f) { lDist = 50f; }
                if (headroom < 0.80f) { shD = 75f;  lights = 12; }
                if (headroom < 0.70f) { shD = 50f;  lights = 10; }
                if (headroom < 0.60f) { shD = 35f;  lights = 8;  lDist = 35f; }
                if (headroom < 0.50f) { shD = 25f;  lights = 6;  lDist = 25f; }
                if (headroom < 0.40f) { shD = 15f;  lights = 4;  lDist = 15f;
                                         shQ = ShadowQuality.High; lod = 2f; af = 8; }
                if (headroom < 0.30f) { shQ = ShadowQuality.Medium; lod = 1f; af = 4; }
                if (headroom < 0.20f)
                {
                    // Final fallback: GPU-side relief for borderline systems —
                    // scale drop lets the GPU return frames faster, which
                    // shortens the CPU stall in Unity's render submission.
                    shQ = ShadowQuality.Low;
                    if (upscaler == UpscaleMode.Off || upscaler == UpscaleMode.DLAA)
                    {
                        upscaler = BestUpscaler(UpscaleTier.Quality);
                        aa = AAMode.Off;
                        minScale = MinRenderScale(upscaler);
                    }
                    scale = Mathf.Clamp(Mathf.RoundToInt(75f * Mathf.Sqrt(Mathf.Max(headroom, 0.1f) / 0.2f)), minScale, 80);
                }
            }
            else if (headroom > 1.15f)
            {
                // Bonus tier — CPU is cruising. GPU has headroom too (CPU-bound
                // by definition), so push pure-GPU fidelity up without spending
                // CPU. Shadow distance / light count stay at Ultra — raising
                // either would eat CPU budget we just verified we have.
                sharp = 0.4f;
                if (headroom > 1.30f) { lod = 5f; }
                if (headroom > 1.50f) { lod = 6f; shD = 175f; }  // shD tiny CPU bump acceptable
                if (headroom > 1.80f) { shD = 200f; }            // only when truly bottomless
            }
            // else: within ±5 % of target — keep Ultra defaults.
        }
        else
        {
            // GPU-bound: step down settings to fit the budget
            if (budget < 1f) { af = 8; budget = Rebudget(); }
            if (budget < 1f) { fog = 1f; budget = Rebudget(); }
            if (budget < 1f) { lod = 2f; budget = Rebudget(); }
            if (budget < 1f) { lDist = 35f; budget = Rebudget(); }
            if (budget < 1f) { lights = 8; budget = Rebudget(); }
            if (budget < 1f) { shD = 50f; budget = Rebudget(); }
            if (budget < 1f) { shQ = ShadowQuality.High; budget = Rebudget(); }
            if (budget < 1f) { shQ = ShadowQuality.Medium; budget = Rebudget(); }

            if (budget < 1f)
            {
                if (upscaler == UpscaleMode.Off)
                {
                    upscaler = BestUpscaler(UpscaleTier.Quality);
                    aa = AAMode.Off;
                    minScale = MinRenderScale(upscaler);
                }
                scale = Mathf.Clamp(Mathf.RoundToInt(100f * Mathf.Sqrt(budget)), minScale, 100);
                budget = Rebudget();
            }

            if (budget < 0.8f)
            {
                shQ = ShadowQuality.Low; shD = 25f; lights = 4; lDist = 15f;
                af = 4; lod = 1f;
                scale = Mathf.Clamp(Mathf.RoundToInt(scale * 0.8f), minScale, 100);
            }

            if (budget < 0.5f)
            {
                upscaler = BestUpscaler(UpscaleTier.Budget);
                minScale = MinRenderScale(upscaler);
                scale = Mathf.Clamp(Mathf.RoundToInt(scale * 0.7f), minScale, 100);
                shQ = ShadowQuality.Low; shD = 10f; lights = 2; lDist = 10f;
                af = 2; lod = 0.5f;
                aa = AAMode.Off;
                sharp = 0f;
            }

            if (scale >= 100)
            {
                upscaler = BestUpscaler(UpscaleTier.NativeAA);
                aa = AAMode.Off;
            }

            // Headroom bonus — when the system easily clears target at Ultra,
            // push a few invisible-to-subtle values beyond vanilla Ultra so
            // strong rigs actually see richer visuals instead of leftover fps.
            // Re-measured between each bump so we don't blow past the budget.
            if (budget > 1.3f) { shD   = 200f; budget = Rebudget(); }
            if (budget > 1.3f) { lDist = 100f; budget = Rebudget(); }
            if (budget > 1.5f) { lod   = 5f;   budget = Rebudget(); }
            if (budget > 1.5f) { sharp = 0.4f;                      }
        }

        int perfLevel = shQ switch
        {
            ShadowQuality.Ultra => 0,
            ShadowQuality.High => 1,
            ShadowQuality.Medium => 2,
            _ => 3,
        };

        Plugin.Log.LogInfo($"Auto-tune result: {upscaler} {scale}% AA={aa} shQ={shQ} shD={shD} " +
            $"lod={lod} lights={lights} lDist={lDist} af={af} tex={tex} fog={fog} perfLevel={perfLevel}");

        SaveAutoTune(new AutoTuneData
        {
            version = BuildInfo.Version,
            revision = AutoTuneData.AutoTuneRevision,
            gpuName = SystemInfo.graphicsDeviceName ?? "",
            resWidth = Screen.width,
            resHeight = Screen.height,
            cpuBound = cpuBound,
            upscaler = (int)upscaler,
            renderScale = scale,
            sharpening = sharp,
            aaMode = (int)aa,
            shadowQuality = (int)shQ,
            shadowDistance = shD,
            lodBias = lod,
            pixelLightCount = lights,
            lightDistance = lDist,
            fogMultiplier = fog,
            anisotropicFiltering = af,
            perfLevel = perfLevel,
        });

        float Rebudget()
        {
            float c = GpuCost(scale, shQ, shD, lod, lights, lDist, af, tex, fog);
            return estUltra / (c / ultraCost) / target;
        }
    }

    // tuned from benchmark runs across RX 6400, P4000, RTX 4070S, RTX 5090
    private static float GpuCost(int scale, ShadowQuality shQ, float shD,
        float lod, int lights, float lDist, int af, TextureRes tex, float fog)
    {
        float s = scale / 100f;
        float c = s * s * 0.45f;                              // render scale: quadratic, dominates at low scales
        c += shQ switch {                                      // shadow map resolution
            ShadowQuality.Ultra => 0.25f, ShadowQuality.High => 0.15f,
            ShadowQuality.Medium => 0.08f, _ => 0.03f };
        c += (Mathf.Min(shD, 150f) / 200f) * 0.08f;          // shadow draw range
        c += (lights / 16f) * 0.03f;                          // pixel lights
        c += (lDist / 100f) * 0.02f;                          // light render distance
        c += (lod / 4f) * 0.02f;                              // LOD bias
        c += tex switch {                                      // texture bandwidth
            TextureRes.Full => 0.04f, TextureRes.Half => 0.02f, _ => 0.01f };
        c += (af / 16f) * 0.01f;                              // anisotropic filtering
        c += (Mathf.Max(fog - 1f, 0f) / 4f) * 0.03f;         // fog distance
        return Mathf.Max(c, 0.05f);
    }
}

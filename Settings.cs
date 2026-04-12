using System;
using System.IO;
using UnityEngine;

namespace REPOFidelity;

internal enum QualityPreset { Potato, Low, Medium, High, Ultra, Custom }
internal enum UpscaleMode { Auto, DLAA, DLSS, FSR4, FSR_Temporal, FSR, Off }
internal enum AAMode { Auto, TAA, SMAA, FXAA, Off }
internal enum ShadowQuality { Low, Medium, High, Ultra }
internal enum TextureRes { Full, Half, Quarter }

internal static class Settings
{
    private static SettingsFile _file = null!;
    private static SettingsData D => _file.Data;
    private static bool _initComplete;

    // accessors — read/write to JSON-backed data, save + notify on write
    internal static QualityPreset Preset
    {
        get => (QualityPreset)D.preset;
        set { D.preset = (int)value; _file.Save(); OnChanged(); }
    }
    internal static UpscaleMode UpscaleModeSetting
    {
        get => (UpscaleMode)D.upscaler;
        set { D.upscaler = (int)value; _file.Save(); OnSettingTweaked(); }
    }
    internal static int RenderScale
    {
        get => D.renderScale;
        set { D.renderScale = Mathf.Clamp(value, 15, 100); _file.Save(); OnSettingTweaked(); }
    }
    internal static float Sharpening
    {
        get => D.sharpening;
        set { D.sharpening = Mathf.Clamp(value, 0f, 1f); _file.Save(); OnSettingTweaked(); }
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
        set { D.fogMultiplier = Mathf.Clamp(value, 1f, 1.1f); _file.Save(); OnSettingTweaked(); }
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
    internal static bool AutoConfigured
    {
        get => D.autoConfigured
            && D.autoConfigVersion == BuildInfo.Version
            && (D.benchResWidth == 0 || (D.benchResWidth == Screen.width && D.benchResHeight == Screen.height));
        set
        {
            D.autoConfigured = value;
            if (value)
            {
                D.autoConfigVersion = BuildInfo.Version;
                D.benchResWidth = Screen.width;
                D.benchResHeight = Screen.height;
            }
            _file.Save();
        }
    }

    internal static bool ModEnabled = true;

    internal static bool CpuBound
    {
        get
        {
            // if resolution changed since benchmark, the result is stale —
            // default to true (CPU-bound is the safe assumption)
            if (D.benchResWidth > 0 && (D.benchResWidth != Screen.width || D.benchResHeight != Screen.height))
                return true;
            return D.cpuBound;
        }
        set { D.cpuBound = value; _file.Save(); }
    }

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
    }

    // resolved values (computed from preset or custom)
    internal static UpscaleMode ResolvedUpscaleMode;
    internal static int ResolvedRenderScale;
    internal static AAMode ResolvedAAMode;
    internal static ShadowQuality ResolvedShadowQuality;
    internal static float ResolvedShadowDistance;
    internal static float ResolvedLODBias;
    internal static int ResolvedPixelLightCount;
    internal static float ResolvedLightDistance;
    internal static float ResolvedFogMultiplier;
    internal static float ResolvedViewDistance;
    internal static int ResolvedAnisotropicFiltering;
    internal static TextureRes ResolvedTextureQuality;

    internal static event Action? OnSettingsChanged;

    internal static void Init()
    {
        string dir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
        _file = new SettingsFile(Path.Combine(dir, "settings.json"));
        _file.Changed += () => { ResolveAutoDefaults(); OnSettingsChanged?.Invoke(); };
        _initComplete = true;

        // migrate old BepInEx config if it exists and we have no settings yet
        MigrateOldConfig(dir);
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

    // call this when a non-preset setting changes to auto-switch to Custom
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

    // suppress save/notify during batch updates (preset apply, auto-tune, etc.)
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
        }

        Plugin.Log.LogInfo($"Resolved [{preset}]: {ResolvedUpscaleMode} {ResolvedRenderScale}% " +
            $"AA={ResolvedAAMode} shadows={ResolvedShadowQuality}/{ResolvedShadowDistance}m " +
            $"LOD={ResolvedLODBias} lights={ResolvedPixelLightCount}");
    }

    private static void SyncCustomToResolved()
    {
        _file.SuppressEvents(() =>
        {
            D.upscaler = (int)ResolvedUpscaleMode;
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
                QualityPreset.Medium => 0.6f,
                QualityPreset.High => 0.5f,
                QualityPreset.Ultra => 0.3f,
                _ => D.sharpening
            };
            D.anisotropicFiltering = ResolvedAnisotropicFiltering;
            D.textureQuality = (int)ResolvedTextureQuality;
        });
        _file.Save();
    }

    internal static int MinRenderScale(UpscaleMode mode) => mode switch
    {
        UpscaleMode.DLSS => 33,           // DLSS hardware handles ultra-low scales
        UpscaleMode.DLAA => 100,          // native-res AA by definition
        UpscaleMode.FSR4 => 50,           // never below 50% for software upscalers
        UpscaleMode.FSR_Temporal => 50,   // temporal accumulation needs enough input detail
        UpscaleMode.FSR => 50,            // spatial-only EASU floor
        UpscaleMode.Off => 100,           // no upscaler = native only
        _ => 50                           // Auto / unknown — safe floor
    };

    private enum UpscaleTier { Budget, Quality, NativeAA }

    private static UpscaleMode BestUpscaler(UpscaleTier tier) => tier switch
    {
        // budget: cheapest usable upscaler — FSR Temporal beats FSR spatial on both
        // cost and quality per benchmark data. iGPU falls back to Off.
        UpscaleTier.Budget => GPUDetector.IsIntegratedGpu ? UpscaleMode.Off : UpscaleMode.FSR_Temporal,
        // native AA: best AA at 100% scale — DLAA on NVIDIA, FSR Temporal elsewhere
        UpscaleTier.NativeAA => GPUDetector.DlssAvailable ? UpscaleMode.DLAA
            : UpscaleMode.FSR_Temporal,
        // quality: best upscaler available — DLSS on NVIDIA, FSR Temporal elsewhere
        _ => GPUDetector.DlssAvailable
            ? UpscaleMode.DLSS : UpscaleMode.FSR_Temporal,
    };

    private static void ApplyPreset(QualityPreset preset)
    {
        bool cpu = CpuBound;

        switch (preset)
        {
            case QualityPreset.Potato:
                // CPU and GPU paths converge for Potato — no upscaler, no AA.
                // iGPU can't benefit from temporal upscaling (shader overhead > fill savings)
                // and even SMAA costs measurable frames at this tier.
                ResolvedUpscaleMode = UpscaleMode.Off;
                ResolvedRenderScale = 100;
                ResolvedAAMode = AAMode.Off;
                ResolvedTextureQuality = cpu ? TextureRes.Full : TextureRes.Quarter;
                ResolvedShadowQuality = ShadowQuality.Low; ResolvedShadowDistance = 10f;
                ResolvedLODBias = 0.5f; ResolvedPixelLightCount = 2;
                ResolvedLightDistance = 10f; ResolvedFogMultiplier = 1f; ResolvedViewDistance = 0f;
                ResolvedAnisotropicFiltering = 2;
                break;
            case QualityPreset.Low:
                // No upscaler at Low tier — SMAA provides cheap edge AA at native res.
                // Better visuals than upscaling at sub-native on limited hardware.
                ResolvedUpscaleMode = UpscaleMode.Off;
                ResolvedRenderScale = 100;
                ResolvedAAMode = AAMode.SMAA;
                ResolvedTextureQuality = cpu ? TextureRes.Full : TextureRes.Half;
                ResolvedShadowQuality = ShadowQuality.Low; ResolvedShadowDistance = 20f;
                ResolvedLODBias = 1f; ResolvedPixelLightCount = 3;
                ResolvedLightDistance = 15f; ResolvedFogMultiplier = 1f; ResolvedViewDistance = 0f;
                ResolvedAnisotropicFiltering = 4;
                break;
            case QualityPreset.Medium:
                if (cpu)
                {
                    ResolvedUpscaleMode = UpscaleMode.Off;
                    ResolvedRenderScale = 100; ResolvedAAMode = AAMode.SMAA;
                }
                else
                {
                    ResolvedUpscaleMode = BestUpscaler(UpscaleTier.Quality);
                    ResolvedRenderScale = 50; ResolvedAAMode = AAMode.Off;
                }
                ResolvedShadowQuality = ShadowQuality.Medium; ResolvedShadowDistance = 40f;
                ResolvedLODBias = 2f; ResolvedPixelLightCount = 4;
                ResolvedLightDistance = 25f; ResolvedFogMultiplier = 1f; ResolvedViewDistance = 0f;
                ResolvedAnisotropicFiltering = 8; ResolvedTextureQuality = TextureRes.Full;
                break;
            case QualityPreset.High:
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
                    ResolvedAAMode = AAMode.Off; // temporal upscaler handles AA
                }
                ResolvedShadowQuality = ShadowQuality.High; ResolvedShadowDistance = 85f;
                ResolvedLODBias = 3f; ResolvedPixelLightCount = 8;
                ResolvedLightDistance = 45f; ResolvedFogMultiplier = 1.1f; ResolvedViewDistance = 0f;
                ResolvedAnisotropicFiltering = 16; ResolvedTextureQuality = TextureRes.Full;
                break;
            case QualityPreset.Ultra:
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
                break;
        }
    }

    // auto-tune: target refresh rate, adjusts for CPU vs GPU bottleneck
    internal static void AutoSelectPreset(float avgFps, bool cpuBound = false)
    {
        float target = Mathf.Max(Screen.currentResolution.refreshRate, 60f) * 1.05f;

        Plugin.Log.LogInfo($"Auto-tune: measured {avgFps:F0}, target {target:F0} " +
            $"({Screen.currentResolution.refreshRate}Hz) {(cpuBound ? "CPU-BOUND" : "gpu-bound")}");

        // start from Ultra
        int scale = 100;
        var shQ = ShadowQuality.Ultra;
        float shD = 150f, lod = 4f, lDist = 75f, sharp = 0.3f, fog = 1.1f;
        int lights = 16, af = 16;
        var tex = TextureRes.Full;

        // pick upscaler based on bottleneck
        // CPU-bound: no temporal upscaling — it adds CPU overhead for zero FPS gain.
        // use SMAA instead (edge-based, cheap on both sides).
        // GPU-bound: temporal upscaler helps by reducing fill rate.
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
            // start at Ultra, only step down settings with real CPU cost.
            // render scale, AF, textures stay maxed — they're GPU-only.
            // shadow distance and lights have CPU cost (draw call submission).
            if (budget < 1f) { shD = 100f; budget = Rebudget(); }
            if (budget < 1f) { lights = 12; budget = Rebudget(); }
            if (budget < 1f) { shD = 75f; budget = Rebudget(); }
            if (budget < 1f) { shQ = ShadowQuality.High; budget = Rebudget(); }
            if (budget < 1f) { lights = 8; budget = Rebudget(); }
            if (budget < 1f) { shD = 50f; budget = Rebudget(); }
            if (budget < 1f) { shQ = ShadowQuality.Medium; budget = Rebudget(); }
            if (budget < 1f) { lights = 4; shD = 25f; budget = Rebudget(); }
            if (budget < 1f) { shQ = ShadowQuality.Low; shD = 10f; lights = 2; }

            Plugin.Log.LogInfo($"CPU-bound: budget={budget:F2} shQ={shQ} shD={shD} lights={lights}");
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
                af = 4; lod = 1f; tex = TextureRes.Half;
                scale = Mathf.Clamp(Mathf.RoundToInt(scale * 0.8f), minScale, 100);
            }

            if (budget < 0.5f)
            {
                upscaler = BestUpscaler(UpscaleTier.Budget);
                minScale = MinRenderScale(upscaler);
                scale = Mathf.Clamp(Mathf.RoundToInt(scale * 0.7f), minScale, 100);
                shQ = ShadowQuality.Low; shD = 10f; lights = 2; lDist = 10f;
                af = 2; lod = 0.5f; tex = TextureRes.Quarter;
                aa = AAMode.Off;
                sharp = 0f;
            }

            if (scale >= 100)
            {
                upscaler = BestUpscaler(UpscaleTier.NativeAA);
                aa = AAMode.Off;
            }
        }

        // No upscaler = no reconstruction. Sub-100% scale would be a raw
        // bilinear blit — blurry AND slower on iGPU due to RT pipeline overhead.
        if (upscaler == UpscaleMode.Off && scale < 100)
        {
            Plugin.Log.LogInfo($"Auto-tune: upscaler Off — forcing native render scale (was {scale}%)");
            scale = 100;
        }

        Plugin.Log.LogInfo($"Auto-tune result: {upscaler} {scale}% AA={aa} shQ={shQ} shD={shD} " +
            $"lod={lod} lights={lights} lDist={lDist} af={af} tex={tex} fog={fog}");

        BatchUpdate(() =>
        {
            D.upscaler = (int)upscaler;
            D.renderScale = scale;
            D.sharpening = sharp;
            D.shadowQuality = (int)shQ;
            D.shadowDistance = shD;
            D.lodBias = lod;
            D.pixelLightCount = lights;
            D.lightDistance = lDist;
            D.anisotropicFiltering = af;
            D.textureQuality = (int)tex;
            D.fogMultiplier = fog;
            D.aaMode = (int)aa;
            D.cpuBound = cpuBound;
            D.preset = (int)QualityPreset.Custom;
            D.autoConfigured = true;
            D.autoConfigVersion = BuildInfo.Version;
            D.benchResWidth = Screen.width;
            D.benchResHeight = Screen.height;
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

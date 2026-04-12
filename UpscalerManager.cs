using REPOFidelity.Patches;
using REPOFidelity.Shaders;
using REPOFidelity.Upscalers;
using UnityEngine;
using UnityEngine.Rendering;

namespace REPOFidelity;

internal class UpscalerManager : MonoBehaviour
{
    internal enum RenderTier
    {
        Passthrough,    // No processing — camera renders directly to game RT at native res
        NativeScaling,  // Game's built-in textureWidthOriginal scaling, optional CAS
        Upscaler        // DLSS/FSR — game RT is low-res input, output RT for display
    }

    internal RenderTier CurrentTier { get; private set; }

    internal static UpscalerManager? Instance { get; private set; }

    private IUpscaler? _upscaler;
    private RenderTextureMain? _renderTextureMain;
    private Camera? _camera;
    private RenderTexture? _outputRT;
    private UnityEngine.UI.RawImage? _overlayRawImage;
    private bool _fallbackCameraRedirect;
    private int _inputWidth;
    private int _inputHeight;
    private int _outputWidth;
    private int _outputHeight;
    private bool _useCameraCallback;
    private float _fpsTimer;
    private int _fpsFrameCount;
    private float _currentFps;
    private float _currentFrameTime;
    private string _debugText = "";
    private string _debugText2 = "";
    private bool _repoHdDetected;

    // Reflection accessor for overlayRawImage (may not be publicized)
    private static readonly System.Reflection.FieldInfo? OverlayField =
        typeof(RenderTextureMain).GetField("overlayRawImage",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);

    // Benchmark
    private bool _benchmarkActive;
    private bool _autoBenchmark; // true when running first-boot auto-detection
    private float _benchmarkTimer;
    private float _benchmarkWarmup;
    private readonly System.Collections.Generic.List<float> _benchmarkFrameTimes = new();
    private readonly System.Collections.Generic.List<float> _benchmarkCpuTimes = new();
    private readonly System.Collections.Generic.List<float> _benchmarkGpuTimes = new();
    private readonly FrameTiming[] _ftBuf = new FrameTiming[1];
    private int _benchmarkVsyncPrev;
    private const float BenchmarkDuration = 15f;
    private const float AutoBenchmarkDuration = 12f;
    private const float WarmupDuration = 5f;
    private const float ThermalSafetyFactor = 0.90f; // account for GPU thermal throttling

    private void Awake()
    {
        Instance = this;
        Settings.OnSettingsChanged += Reinitialize;
        _repoHdDetected = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("BlueAmulet.REPO_HD");
    }

    internal void Reinitialize()
    {
        if (_renderTextureMain == null || _camera == null) return;

        Plugin.Log.LogInfo("Reinitializing upscaler pipeline...");

        // tear down current state
        if (_useCameraCallback)
            Camera.onPostRender -= OnPostRenderCallback;

        _upscaler?.Dispose();
        _upscaler = null;

        // Restore overlayRawImage to the game's RT before releasing our output RT
        RestoreOverlayRawImage();

        // clear camera target before releasing the RT it points at,
        // otherwise Unity errors on "releasing render texture set as targetTexture"
        // and the screen goes black
        if (_camera != null)
            _camera.targetTexture = null;

        ReleaseRT(ref _outputRT);
        _fallbackCameraRedirect = false;
        _useCameraCallback = false;

        // rebuild with new settings
        Setup(_renderTextureMain, _camera);

        // reapply quality + AA settings
        QualityPatch.ApplyQualitySettings();
        Patches.RenderTexturePatch.ReapplyModCameraSettings();
    }

    internal void Setup(RenderTextureMain rtMain, Camera camera)
    {
        _renderTextureMain = rtMain;
        _camera = camera;
        _outputWidth = Screen.width;
        _outputHeight = Screen.height;

        // Grab overlayRawImage reference via reflection
        _overlayRawImage = OverlayField?.GetValue(rtMain) as UnityEngine.UI.RawImage;
        if (_overlayRawImage == null)
            Plugin.Log.LogWarning("overlayRawImage not found — upscaler tier will use camera-redirect fallback");

        _upscaler = Settings.ResolvedUpscaleMode switch
        {
            UpscaleMode.FSR_Temporal => new TemporalUpscaler(),
            UpscaleMode.DLSS => new DLSSUpscaler(dlaaMode: false),
            UpscaleMode.DLAA => new DLSSUpscaler(dlaaMode: true),
            _ => null
        };

        if (_upscaler != null && !_upscaler.IsAvailable)
        {
            Plugin.Log.LogWarning($"{_upscaler.Name} unavailable — falling back to FSR Temporal");
            _upscaler = new TemporalUpscaler();
            Settings.ResolvedUpscaleMode = UpscaleMode.FSR_Temporal;

            if (!_upscaler.IsAvailable)
            {
                Plugin.Log.LogWarning("FSR also unavailable — no upscaling");
                _upscaler = null;
                Settings.ResolvedUpscaleMode = UpscaleMode.Off;

                // Without an upscaler, reduced render scale just adds a blurry bilinear
                // round-trip for zero benefit. Force native res and rely on quality
                // settings alone for performance (shadows, LOD, lights, etc.)
                if (Settings.ResolvedRenderScale < 100)
                {
                    Plugin.Log.LogWarning("No upscaler available — forcing native render scale");
                    Settings.ResolvedRenderScale = 100;
                }
            }
        }

        // no upscaler + sub-native = blurry blit for zero benefit
        if (_upscaler == null && Settings.ResolvedRenderScale < 100)
        {
            Plugin.Log.LogWarning("No upscaler + sub-native scale — forcing native resolution");
            Settings.ResolvedRenderScale = 100;
        }

        bool hasCAS = Settings.Sharpening > 0.01f;
        if (_upscaler != null)
            CurrentTier = RenderTier.Upscaler;
        else if (hasCAS)
            CurrentTier = RenderTier.NativeScaling;
        else
            CurrentTier = RenderTier.Passthrough;

        // DLSS needs camera-event timing for depth/motion vectors
        _useCameraCallback = _upscaler is DLSSUpscaler;

        switch (CurrentTier)
        {
            case RenderTier.Passthrough:
                SetupPassthrough(rtMain);
                break;
            case RenderTier.NativeScaling:
                SetupNativeScaling(rtMain);
                break;
            case RenderTier.Upscaler:
                SetupUpscaler(rtMain, camera);
                break;
        }
    }

    private void SetupPassthrough(RenderTextureMain rtMain)
    {
        if (_camera != null && rtMain.renderTexture != null)
            _camera.targetTexture = rtMain.renderTexture;

        RestoreOverlayRawImage();

        Plugin.Log.LogInfo("Passthrough: rendering at native resolution, no processing");
    }

    private void SetupNativeScaling(RenderTextureMain rtMain)
    {
        if (_camera != null && rtMain.renderTexture != null)
            _camera.targetTexture = rtMain.renderTexture;

        RestoreOverlayRawImage();

        Plugin.Log.LogInfo("NativeScaling: game handles RT sizing, CAS via temp RT");
    }

    private void SetupUpscaler(RenderTextureMain rtMain, Camera camera)
    {
        float scale = RenderTexturePatch.GetRenderScale();
        _inputWidth = Mathf.Max(Mathf.RoundToInt(_outputWidth * scale), 1);
        _inputHeight = Mathf.Max(Mathf.RoundToInt(_outputHeight * scale), 1);

        var gameRT = rtMain.renderTexture;

        if (_overlayRawImage != null)
        {
            // Primary path: swap overlayRawImage to our output RT
            // Resize game's RT to input dimensions (camera renders to it at low-res)
            if (gameRT != null)
            {
                gameRT.Release();
                gameRT.width = _inputWidth;
                gameRT.height = _inputHeight;
                gameRT.Create();
            }

            // Camera targets game's RT naturally (no redirect needed)
            if (_camera != null && gameRT != null)
                _camera.targetTexture = gameRT;

            // Create output RT at full resolution for display
            var format = gameRT != null ? gameRT.format : RenderTextureFormat.DefaultHDR;
            _outputRT = new RenderTexture(_outputWidth, _outputHeight, 0, format)
            {
                filterMode = FilterMode.Bilinear
            };
            _outputRT.Create();

            // Swap overlay to display our output RT
            _overlayRawImage.texture = _outputRT;

            _fallbackCameraRedirect = false;

            Plugin.Log.LogInfo($"Upscaler (primary): {_inputWidth}x{_inputHeight} -> {_outputWidth}x{_outputHeight}");
        }
        else
        {
            // Fallback path: redirect camera to our output RT (low-res), game RT used for display
            _fallbackCameraRedirect = true;

            var format = gameRT != null ? gameRT.format : RenderTextureFormat.DefaultHDR;

            // Create output RT at INPUT dimensions with depth — this is the camera target
            _outputRT = new RenderTexture(_inputWidth, _inputHeight, 24, format)
            {
                filterMode = FilterMode.Bilinear
            };
            _outputRT.Create();

            // Camera renders into our low-res output RT
            camera.targetTexture = _outputRT;

            // Resize game's RT to full output dimensions (for display)
            if (gameRT != null)
            {
                gameRT.Release();
                gameRT.width = _outputWidth;
                gameRT.height = _outputHeight;
                gameRT.Create();
            }

            Plugin.Log.LogInfo($"Upscaler (fallback): {_inputWidth}x{_inputHeight} -> {_outputWidth}x{_outputHeight}");
        }

        if (_upscaler != null)
        {
            _upscaler.Initialize(_camera, _inputWidth, _inputHeight, _outputWidth, _outputHeight);
            Plugin.Log.LogInfo($"Upscaler active: {_upscaler.Name}");
        }

        if (_useCameraCallback && _camera != null)
            Camera.onPostRender += OnPostRenderCallback;
    }

    // Restore overlayRawImage back to the game's render texture
    private void RestoreOverlayRawImage()
    {
        if (_overlayRawImage == null || _renderTextureMain == null) return;
        if (_renderTextureMain.renderTexture != null)
            _overlayRawImage.texture = _renderTextureMain.renderTexture;
    }

    // DLSS needs depth/MV textures which are only valid after the camera renders
    private void OnPostRenderCallback(Camera cam)
    {
        if (cam != _camera) return;
        ProcessFrame();
    }

    private void LateUpdate()
    {
        if (!Settings.ModEnabled || _renderTextureMain == null) return;

        // Passthrough: nothing to do
        if (CurrentTier == RenderTier.Passthrough) return;

        // Resolution change detection
        if (Screen.width != _outputWidth || Screen.height != _outputHeight)
        {
            _outputWidth = Screen.width;
            _outputHeight = Screen.height;
            HandleResolutionChange();
        }

        if (CurrentTier == RenderTier.NativeScaling)
        {
            // Game handles RT sizing via textureWidthOriginal (PrefixUpdate)
            if (Settings.Sharpening > 0.01f)
            {
                var gameRT = _renderTextureMain.renderTexture;
                if (gameRT != null)
                    ApplyCAS(gameRT);
            }
            return;
        }

        // Upscaler tier below this point

        // Ensure camera targets the right RT (game's SetRenderTexture may redirect it)
        if (_fallbackCameraRedirect && _camera != null && _outputRT != null && _camera.targetTexture != _outputRT)
            _camera.targetTexture = _outputRT;

        // DLSS is processed in OnPostRender callback for correct depth/MV timing
        if (_useCameraCallback) return;

        ProcessFrame();
    }

    private void ProcessFrame()
    {
        if (_renderTextureMain == null || _upscaler == null) return;

        var gameRT = _renderTextureMain.renderTexture;

        if (_fallbackCameraRedirect)
        {
            // Fallback: camera renders to _outputRT (low-res), upscale into gameRT (full-res display)
            if (_outputRT == null || !_outputRT.IsCreated()) return;
            if (gameRT == null || !gameRT.IsCreated()) return;

            _upscaler?.OnRenderImage(_outputRT, gameRT);

            // CAS applied to gameRT (skip for DLSS which has built-in sharpening)
            if (!(_upscaler is DLSSUpscaler) && Settings.Sharpening > 0.01f)
                ApplyCAS(gameRT);
        }
        else
        {
            // Primary: gameRT holds low-res input, _outputRT is full-res display target
            if (gameRT == null || !gameRT.IsCreated()) return;
            if (_outputRT == null || !_outputRT.IsCreated()) return;

            _upscaler?.OnRenderImage(gameRT, _outputRT);

            // CAS applied to _outputRT (skip for DLSS which has built-in sharpening)
            if (!(_upscaler is DLSSUpscaler) && Settings.Sharpening > 0.01f)
                ApplyCAS(_outputRT);
        }
    }

    private static void ApplyCAS(RenderTexture target)
    {
        if (target == null || !target.IsCreated()) return;
        var temp = RenderTexture.GetTemporary(target.width, target.height, 0, target.format);
        CASShader.Apply(target, temp, Settings.Sharpening);
        Graphics.Blit(temp, target);
        RenderTexture.ReleaseTemporary(temp);
    }

    private void Update()
    {
        if (!_benchmarkActive && Input.GetKeyDown(Settings.ToggleKey))
        {
            Settings.ModEnabled = !Settings.ModEnabled;
            Plugin.Log.LogInfo($"Mod {(Settings.ModEnabled ? "ENABLED" : "DISABLED")}");

            // re-scan scene to apply/undo shadow optimizations
            Patches.SceneOptimizer.Apply();

            if (!Settings.ModEnabled)
            {
                // Restore overlayRawImage to game's RT before disabling
                RestoreOverlayRawImage();

                // put everything back to vanilla
                if (_camera != null && _renderTextureMain != null)
                    _camera.targetTexture = _renderTextureMain.renderTexture;

                Patches.RenderTexturePatch.RestoreVanillaCameraSettings();
                RestoreVanillaSettings();
                Patches.QualityPatch.RestoreVanillaQuality();

                if (GraphicsManager.instance != null)
                    GraphicsManager.instance.UpdateAll();

                if (_camera != null)
                    _camera.layerCullDistances = new float[32];
            }
            else
            {
                // Re-point overlayRawImage back to _outputRT if we're in Upscaler tier
                if (CurrentTier == RenderTier.Upscaler && _outputRT != null && _overlayRawImage != null)
                    _overlayRawImage.texture = _outputRT;

                // Re-redirect camera if using fallback path
                if (_fallbackCameraRedirect && _camera != null && _outputRT != null)
                    _camera.targetTexture = _outputRT;

                // Re-apply camera depth mode + AA
                Patches.RenderTexturePatch.ReapplyModCameraSettings();

                // Restore our render texture to full resolution
                if (_renderTextureMain != null)
                {
                    _renderTextureMain.textureWidthOriginal = Screen.width;
                    _renderTextureMain.textureHeightOriginal = Screen.height;
                    var rt = _renderTextureMain.renderTexture;
                    if (rt != null && CurrentTier != RenderTier.Upscaler)
                    {
                        // For non-upscaler tiers, ensure game RT is at native res
                        if (rt.width != _outputWidth || rt.height != _outputHeight)
                        {
                            rt.Release();
                            rt.width = _outputWidth;
                            rt.height = _outputHeight;
                            rt.Create();
                        }
                    }
                }

                // Re-apply fog extension
                float fogMult = Settings.ResolvedFogMultiplier;
                if (_vanillaSaved && fogMult > 1f)
                {
                    RenderSettings.fogStartDistance = _vanillaFogStart * fogMult;
                    RenderSettings.fogEndDistance = _vanillaFogEnd * fogMult;
                    if (_camera != null)
                        _camera.farClipPlane = RenderSettings.fogEndDistance + 10f;
                }

                // Re-apply all quality settings
                Patches.QualityPatch.ApplyQualitySettings();
            }
        }

        // Only track FPS when debug overlay is visible, mod is disabled (shows OFF text), or benchmark is active
        if (Settings.DebugOverlay || !Settings.ModEnabled || _benchmarkActive)
        {
            _fpsTimer += Time.unscaledDeltaTime;
            _fpsFrameCount++;

            if (_fpsTimer >= 0.5f)
            {
                _currentFps = _fpsFrameCount / _fpsTimer;
                _currentFrameTime = _fpsTimer / _fpsFrameCount * 1000f;
                _fpsFrameCount = 0;
                _fpsTimer = 0f;

                if (Settings.DebugOverlay || !Settings.ModEnabled)
                {
                    string mode = CurrentTier switch
                    {
                        RenderTier.Upscaler => _upscaler?.Name ?? "Unknown",
                        RenderTier.NativeScaling => "CAS Only",
                        _ => "Off"
                    };
                    string input = CurrentTier == RenderTier.Upscaler ? $"{_inputWidth}x{_inputHeight}" : "native";
                    string output = $"{_outputWidth}x{_outputHeight}";
                    float scale = RenderTexturePatch.GetRenderScale();
                    string preset = Settings.Preset.ToString();
                    string aa = Settings.ResolvedAAMode == AAMode.Off ? "" : $" AA={Settings.ResolvedAAMode}";
                    string vsync = QualitySettings.vSyncCount > 0 ? " VSync" : "";
                    _debugText = $"REPO Fidelity [{preset}] | {mode}{aa} | {input} -> {output} ({scale * 100:F0}%) | {_currentFps:F1} FPS ({_currentFrameTime:F1}ms){vsync}";
                    _debugText2 = $"{GPUDetector.GpuName} | Shadows={Settings.ResolvedShadowQuality}/{Settings.ResolvedShadowDistance:F0}m | Lights={Settings.ResolvedPixelLightCount} LOD={Settings.ResolvedLODBias:F1}x | Sharp={Settings.Sharpening:F1} Fog={Settings.ResolvedFogMultiplier:F2}x";
                }
            }
        }

        // Auto-benchmark on first boot — only in actual gameplay levels.
        // Uses RunManager API to distinguish lobby from gameplay (same pattern as SharedUpgradesPlus).
        // Wait 10s after level load for loading screen and asset streaming to finish.
        // cancel any stale benchmark if we're not in a real level
        if (_benchmarkActive && !IsInGameplayLevel())
        {
            _benchmarkActive = false;
            _autoBenchmark = false;
            QualitySettings.vSyncCount = _benchmarkVsyncPrev;
        }

        if (!Settings.AutoConfigured && !_benchmarkActive && !_autoBenchmark
            && _vanillaSaved && IsInGameplayLevel()
            && LevelGenerator.Instance != null && LevelGenerator.Instance.Generated
            && Time.unscaledTime - _lastEnvironmentSetupTime >= 10f)
        {
            _autoBenchmark = true;
            StartBenchmark();
            Plugin.Log.LogInfo("=== AUTO-BENCHMARK: detecting best preset ===");
        }

        // Manual benchmark
        if (Settings.BenchmarkMode && !_benchmarkActive)
        {
            _autoBenchmark = false;
            StartBenchmark();
            Plugin.Log.LogInfo("=== BENCHMARK STARTED ===");
        }

        if (_benchmarkActive)
        {
            // Warmup phase — let GPU/shaders/textures stabilize before measuring
            if (_benchmarkWarmup > 0f)
            {
                _benchmarkWarmup -= Time.unscaledDeltaTime;
                return;
            }

            _benchmarkTimer += Time.unscaledDeltaTime;
            float frameTimeMs = Time.unscaledDeltaTime * 1000f;

            // Discard clearly broken frames (loading, GC > 3x typical)
            if (frameTimeMs > 0.01f && frameTimeMs < 500f)
                _benchmarkFrameTimes.Add(frameTimeMs);

            // capture CPU vs GPU split
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _ftBuf) > 0)
            {
                if (_ftBuf[0].cpuFrameTime > 0) _benchmarkCpuTimes.Add((float)_ftBuf[0].cpuFrameTime);
                if (_ftBuf[0].gpuFrameTime > 0) _benchmarkGpuTimes.Add((float)_ftBuf[0].gpuFrameTime);
            }

            float duration = _autoBenchmark ? AutoBenchmarkDuration : BenchmarkDuration;

            if (_benchmarkTimer >= duration && _benchmarkFrameTimes.Count > 30)
            {
                FinishBenchmark();
            }
        }
    }

    // Cached GUIStyles — avoid heap allocation every OnGUI frame
    private GUIStyle? _guiStyleLarge, _guiShadowLarge;
    private GUIStyle? _guiStyleMed, _guiShadowMed;
    private GUIStyle? _guiStyleSmall, _guiShadowSmall;
    private GUIStyle? _guiStyleWarn, _guiShadowWarn;

    private void EnsureGUIStyles()
    {
        if (_guiStyleLarge != null) return;
        _guiStyleLarge = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
        _guiStyleLarge.normal.textColor = Color.red;
        _guiShadowLarge = new GUIStyle(_guiStyleLarge);
        _guiShadowLarge.normal.textColor = Color.black;

        _guiStyleMed = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
        _guiStyleMed.normal.textColor = Color.red;
        _guiShadowMed = new GUIStyle(_guiStyleMed);
        _guiShadowMed.normal.textColor = Color.black;

        _guiStyleSmall = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
        _guiStyleSmall.normal.textColor = Color.yellow;
        _guiShadowSmall = new GUIStyle(_guiStyleSmall);
        _guiShadowSmall.normal.textColor = Color.black;

        _guiStyleWarn = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
        _guiStyleWarn.normal.textColor = new Color(1f, 0.6f, 0f); // orange
        _guiShadowWarn = new GUIStyle(_guiStyleWarn);
        _guiShadowWarn.normal.textColor = Color.black;
    }

    private void OnGUI()
    {
        if (!_repoHdDetected && string.IsNullOrEmpty(_debugText)) return;
        EnsureGUIStyles();

        if (_repoHdDetected)
        {
            float y = Screen.height - 60f;
            GUI.Label(new Rect(11, y + 1, 800, 30), "REPO HD detected — remove it, REPO Fidelity replaces it", _guiShadowWarn);
            GUI.Label(new Rect(10, y, 800, 30), "REPO HD detected — remove it, REPO Fidelity replaces it", _guiStyleWarn);
        }

        if (string.IsNullOrEmpty(_debugText)) return;

        // Benchmark overlay (always visible during benchmark)
        if (_benchmarkActive)
        {
            float duration = _autoBenchmark ? AutoBenchmarkDuration : BenchmarkDuration;
            float remaining = _benchmarkWarmup > 0 ? _benchmarkWarmup + duration : duration - _benchmarkTimer;

            string label = _autoBenchmark ? "AUTO-DETECTING" : "BENCHMARKING";
            string benchText = _benchmarkWarmup > 0
                ? $"{label}... warming up ({remaining:F0}s)"
                : $"{label}... {remaining:F0}s | {_benchmarkFrameTimes.Count} frames | {_currentFps:F0} FPS";

            GUI.Label(new Rect(11, 41, 900, 35), benchText, _guiShadowLarge);
            GUI.Label(new Rect(10, 40, 900, 35), benchText, _guiStyleLarge);
        }

        // Show "MOD OFF" + FPS when disabled
        if (!Settings.ModEnabled)
        {
            string offText = $"REPO FIDELITY OFF ({Settings.ToggleKey}) | {_currentFps:F1} FPS ({_currentFrameTime:F1}ms)";
            GUI.Label(new Rect(11, 11, 800, 30), offText, _guiShadowMed);
            GUI.Label(new Rect(10, 10, 800, 30), offText, _guiStyleMed);
            return;
        }

        if (!Settings.DebugOverlay) return;

        GUI.Label(new Rect(11, 11, 1200, 30), _debugText, _guiShadowSmall);
        GUI.Label(new Rect(10, 10, 1200, 30), _debugText, _guiStyleSmall);
        GUI.Label(new Rect(11, 31, 1200, 30), _debugText2, _guiShadowSmall);
        GUI.Label(new Rect(10, 30, 1200, 30), _debugText2, _guiStyleSmall);
    }

    private void HandleResolutionChange()
    {
        // Passthrough: nothing to do
        if (CurrentTier == RenderTier.Passthrough) return;

        // NativeScaling: nothing needed — PrefixUpdate handles game RT sizing
        if (CurrentTier == RenderTier.NativeScaling) return;

        // Upscaler tier: recalculate input dimensions and recreate RTs
        float scale = RenderTexturePatch.GetRenderScale();
        _inputWidth = Mathf.Max(Mathf.RoundToInt(_outputWidth * scale), 1);
        _inputHeight = Mathf.Max(Mathf.RoundToInt(_outputHeight * scale), 1);

        var gameRT = _renderTextureMain?.renderTexture;

        if (_fallbackCameraRedirect)
        {
            // Fallback: resize _outputRT (low-res camera target) and game RT (full-res display)
            ReleaseRT(ref _outputRT);
            var format = gameRT != null ? gameRT.format : RenderTextureFormat.DefaultHDR;
            _outputRT = new RenderTexture(_inputWidth, _inputHeight, 24, format)
            {
                filterMode = FilterMode.Bilinear
            };
            _outputRT.Create();

            if (_camera != null)
                _camera.targetTexture = _outputRT;

            if (gameRT != null)
            {
                gameRT.Release();
                gameRT.width = _outputWidth;
                gameRT.height = _outputHeight;
                gameRT.Create();
            }
        }
        else
        {
            // Primary: resize game RT (low-res input) and recreate _outputRT (full-res display)
            if (gameRT != null)
            {
                gameRT.Release();
                gameRT.width = _inputWidth;
                gameRT.height = _inputHeight;
                gameRT.Create();
            }

            ReleaseRT(ref _outputRT);
            var format = gameRT != null ? gameRT.format : RenderTextureFormat.DefaultHDR;
            _outputRT = new RenderTexture(_outputWidth, _outputHeight, 0, format)
            {
                filterMode = FilterMode.Bilinear
            };
            _outputRT.Create();

            // Re-point overlayRawImage to the new output RT
            if (_overlayRawImage != null)
                _overlayRawImage.texture = _outputRT;
        }

        if (_upscaler != null)
            _upscaler.OnResolutionChanged(_inputWidth, _inputHeight, _outputWidth, _outputHeight);
    }

    private void StartBenchmark()
    {
        _benchmarkActive = true;
        _benchmarkTimer = 0f;
        _benchmarkWarmup = WarmupDuration;
        _benchmarkFrameTimes.Clear();
        _benchmarkCpuTimes.Clear();
        _benchmarkGpuTimes.Clear();

        // Disable VSync during benchmark so we measure actual GPU capability
        _benchmarkVsyncPrev = QualitySettings.vSyncCount;
        QualitySettings.vSyncCount = 0;
    }

    private void FinishBenchmark()
    {
        try
        {
            // Sort frame times for percentile calculations
            _benchmarkFrameTimes.Sort();
            int count = _benchmarkFrameTimes.Count;

            // Filter outliers: discard frames > 3x the median (GC pauses, loading spikes)
            float median = _benchmarkFrameTimes[count / 2];
            float outlierThreshold = median * 3f;
            var filtered = _benchmarkFrameTimes.FindAll(t => t <= outlierThreshold);
            int filteredCount = filtered.Count;

            if (filteredCount < 10)
            {
                Plugin.Log.LogWarning("Benchmark: too few valid frames — skipping auto-tune");
                return;
            }

            // Average frame time (correct method — NOT arithmetic mean of FPS)
            float totalMs = 0f;
            for (int i = 0; i < filteredCount; i++) totalMs += filtered[i];
            float avgMs = totalMs / filteredCount;
            float avgFps = 1000f / avgMs;

            // 1% low: average of worst 1% of frame times
            int lowCount = Mathf.Max(filteredCount / 100, 1);
            float low1TotalMs = 0f;
            for (int i = filteredCount - lowCount; i < filteredCount; i++) low1TotalMs += filtered[i];
            float low1Ms = low1TotalMs / lowCount;
            float low1Fps = 1000f / low1Ms;

            // 0.1% low (if enough samples)
            float low01Fps = 0f;
            if (filteredCount >= 1000)
            {
                int low01Count = Mathf.Max(filteredCount / 1000, 1);
                float low01TotalMs = 0f;
                for (int i = filteredCount - low01Count; i < filteredCount; i++) low01TotalMs += filtered[i];
                low01Fps = 1000f / (low01TotalMs / low01Count);
            }

            string preset = Settings.Preset.ToString();
            string mode = _upscaler != null ? _upscaler.Name : "Off";
            string res = CurrentTier == RenderTier.Upscaler ? $"{_inputWidth}x{_inputHeight}" : "native";
            int discarded = count - filteredCount;

            // CPU vs GPU bottleneck detection
            float avgCpuMs = 0f, avgGpuMs = 0f;
            bool cpuBound = false;
            if (_benchmarkCpuTimes.Count > 30 && _benchmarkGpuTimes.Count > 30)
            {
                float cpuTotal = 0f, gpuTotal = 0f;
                for (int i = 0; i < _benchmarkCpuTimes.Count; i++) cpuTotal += _benchmarkCpuTimes[i];
                for (int i = 0; i < _benchmarkGpuTimes.Count; i++) gpuTotal += _benchmarkGpuTimes[i];
                avgCpuMs = cpuTotal / _benchmarkCpuTimes.Count;
                avgGpuMs = gpuTotal / _benchmarkGpuTimes.Count;
                // CPU-bound if CPU takes 20%+ longer than GPU
                // GPU-bound only when GPU clearly exceeds CPU.
                // at parity, dropping render scale won't help — treat as CPU-bound.
                cpuBound = avgGpuMs < avgCpuMs * 1.3f;
            }

            Plugin.Log.LogInfo("=== BENCHMARK RESULTS ===");
            Plugin.Log.LogInfo($"  Preset: {preset} | Upscaler: {mode} | Render: {res} -> {_outputWidth}x{_outputHeight}");
            Plugin.Log.LogInfo($"  Frames: {filteredCount} measured, {discarded} outliers discarded");
            Plugin.Log.LogInfo($"  Avg: {avgFps:F1} FPS ({avgMs:F1}ms)");
            Plugin.Log.LogInfo($"  1% Low: {low1Fps:F1} FPS ({low1Ms:F1}ms)");
            if (low01Fps > 0) Plugin.Log.LogInfo($"  0.1% Low: {low01Fps:F1} FPS");
            if (avgCpuMs > 0) Plugin.Log.LogInfo($"  CPU: {avgCpuMs:F1}ms | GPU: {avgGpuMs:F1}ms | {(cpuBound ? "CPU-BOUND" : "GPU-bound")}");
            Plugin.Log.LogInfo("=========================");

            float tuningFps = low1Fps * ThermalSafetyFactor;
            Plugin.Log.LogInfo($"  Tuning target: {tuningFps:F1} FPS (1% low x {ThermalSafetyFactor} thermal safety)");

            Settings.CpuBound = cpuBound;
            Settings.AutoSelectPreset(tuningFps, cpuBound);
        }
        finally
        {
            // Always restore VSync and clean up benchmark state
            QualitySettings.vSyncCount = _benchmarkVsyncPrev;
            if (_autoBenchmark) _autoBenchmark = false;
            _benchmarkActive = false;
            Settings.BenchmarkMode = false;
        }
    }

    private static bool IsInGameplayLevel()
    {
        if (RunManager.instance == null) return false;
        // SemiFunc.RunIsLevel checks against all non-gameplay levels
        // (main menu, lobby menu, lobby, shop, tutorial, arena, splash)
        return SemiFunc.RunIsLevel();
    }

    private static void ReleaseRT(ref RenderTexture? rt)
    {
        if (rt != null)
        {
            rt.Release();
            Object.Destroy(rt);
            rt = null;
        }
    }

    // Saved vanilla values for F10 restore — internal so QualityPatch can read them
    internal static float _vanillaFogStart;
    internal static float _vanillaFogEnd;
    internal static float _vanillaFarClip;
    internal static bool _vanillaSaved;
    internal static int _environmentSetupCount;
    internal static float _lastEnvironmentSetupTime;

    internal static void SaveVanillaFog()
    {
        _vanillaFogStart = RenderSettings.fogStartDistance;
        _vanillaFogEnd = RenderSettings.fogEndDistance;
        if (Camera.main != null)
            _vanillaFarClip = Camera.main.farClipPlane;
        _vanillaSaved = true;
        _environmentSetupCount++;
        _lastEnvironmentSetupTime = Time.unscaledTime;

        Plugin.Log.LogInfo($"Vanilla fog: start={_vanillaFogStart:F0}m end={_vanillaFogEnd:F0}m clip={_vanillaFarClip:F0}m (env #{_environmentSetupCount})");
    }

    private void RestoreVanillaSettings()
    {
        // Restore fog to what the game set before we modified it
        if (_vanillaSaved)
        {
            RenderSettings.fogStartDistance = _vanillaFogStart;
            RenderSettings.fogEndDistance = _vanillaFogEnd;
            if (_camera != null)
                _camera.farClipPlane = _vanillaFarClip;
        }

        // Keep the render texture at native resolution — don't restore pixelation.
        // The camera renders directly to the game's RT at full res.
        // This gives a clean native vs upscaled comparison.
        if (_renderTextureMain != null)
        {
            _renderTextureMain.textureWidthOriginal = Screen.width;
            _renderTextureMain.textureHeightOriginal = Screen.height;
        }

        Plugin.Log.LogInfo("Vanilla settings restored (native res, no effects)");
    }

    private void OnDestroy()
    {
        Settings.OnSettingsChanged -= Reinitialize;

        if (_useCameraCallback)
            Camera.onPostRender -= OnPostRenderCallback;

        _upscaler?.Dispose();

        // Restore overlayRawImage and camera target to game's render texture
        RestoreOverlayRawImage();

        if (_camera != null && _renderTextureMain != null)
            _camera.targetTexture = _renderTextureMain.renderTexture;

        ReleaseRT(ref _outputRT);

        Instance = null;
    }
}

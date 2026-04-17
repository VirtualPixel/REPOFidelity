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
    private int _inputWidth;
    private int _inputHeight;
    private int _outputWidth;
    private int _outputHeight;
    private bool _useCameraCallback;
    private bool _repoHdDetected;

    // public accessors for overlay
    internal static bool RepoHdDetected => Instance != null && Instance._repoHdDetected;
    internal static bool BenchmarkActive => Instance != null && Instance._benchmarkActive;
    internal static bool AutoBenchmarkRunning => Instance != null && Instance._autoBenchmark;
    internal static float BenchmarkProgress
    {
        get
        {
            if (Instance == null || !Instance._benchmarkActive) return 0f;
            float p0 = Phase0Duration;
            float p1 = Instance._autoBenchmark ? AutoBenchmarkDuration : BenchmarkDuration;
            float total = WarmupDuration + p0 + WarmupDuration + p1;
            float elapsed;
            if (Instance._benchmarkPhase == 0)
                elapsed = (WarmupDuration - Instance._benchmarkWarmup) +
                    (Instance._benchmarkWarmup > 0 ? 0 : Instance._benchmarkTimer);
            else
                elapsed = WarmupDuration + p0 + (WarmupDuration - Instance._benchmarkWarmup) +
                    (Instance._benchmarkWarmup > 0 ? 0 : Instance._benchmarkTimer);
            return Mathf.Clamp01(elapsed / total);
        }
    }

    // Temporal jitter — shared between DLSS and FSR
    private int _jitterIndex;
    private Matrix4x4 _savedProjectionMatrix;
    private bool _jitterApplied;
    private static readonly float[] HaltonX = GenerateHalton(2, 32);
    private static readonly float[] HaltonY = GenerateHalton(3, 32);
    internal float JitterX { get; private set; }
    internal float JitterY { get; private set; }

    // F10 toggle
    private bool _togglePending;

    // Benchmark
    private bool _benchmarkActive;
    private bool _autoBenchmark; // true when running first-boot auto-detection
    private float _benchmarkTimer;
    private float _benchmarkWarmup;
    private readonly System.Collections.Generic.List<float> _benchmarkFrameTimes = new();
    private int _benchmarkVsyncPrev;
    private int _benchmarkPhase; // 0 = low-GPU probe, 1 = real measurement
    private float _lowGpuFps; // CPU ceiling from phase 0
    private int _savedRenderScale; // stash original scale during phase 0
    private const float BenchmarkDuration = 15f;
    private const float AutoBenchmarkDuration = 12f;
    private const float Phase0Duration = 4f;
    private const float WarmupDuration = 3f;
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
        Camera.onPreRender -= OnPreRenderJitter;
        Camera.onPostRender -= OnPostRenderRestore;
        if (_useCameraCallback)
            Camera.onPostRender -= OnPostRenderCallback;

        _upscaler?.Dispose();
        _upscaler = null;

        // Restore overlayRawImage to the game's RT before releasing our output RT
        // clear camera target before releasing the RT it points at
        if (_camera != null)
            _camera.targetTexture = null;

        ReleaseRT(ref _outputRT);
        _useCameraCallback = false;

        // rebuild with new settings
        if (_renderTextureMain != null && _camera != null)
            Setup(_renderTextureMain, _camera);

        // reapply quality + AA settings
        QualityPatch.ApplyQualitySettings();
        Patches.RenderTexturePatch.ReapplyModCameraSettings();
    }

    internal void Setup(RenderTextureMain rtMain, Camera camera)
    {
        _renderTextureMain = rtMain;
        _camera = camera;
        _outputWidth = Settings.OutputWidth;
        _outputHeight = Settings.OutputHeight;

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

        bool hasCAS = Settings.Sharpening > 0.01f;
        bool hasScale = Settings.ResolvedRenderScale < 100;
        if (_upscaler != null)
            CurrentTier = RenderTier.Upscaler;
        else if (hasCAS || hasScale)
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



        Plugin.Log.LogInfo("Passthrough mode");
    }

    private void SetupNativeScaling(RenderTextureMain rtMain)
    {
        if (_camera != null && rtMain.renderTexture != null)
            _camera.targetTexture = rtMain.renderTexture;



        Plugin.Log.LogInfo("NativeScaling mode");
    }

    private void SetupUpscaler(RenderTextureMain rtMain, Camera camera)
    {
        float scale = RenderTexturePatch.GetRenderScale();
        _inputWidth = Mathf.Max(Mathf.RoundToInt(_outputWidth * scale), 1);
        _inputHeight = Mathf.Max(Mathf.RoundToInt(_outputHeight * scale), 1);

        var gameRT = rtMain.renderTexture;
        var format = gameRT != null ? gameRT.format : RenderTextureFormat.DefaultHDR;

        // Camera-redirect: camera → _outputRT (low-res) → upscaler → game RT (full-res display).
        // Can't use game RT as low-res input because the game's Update() resizes it
        // from textureWidthOriginal every frame, and overlayRawImage points to a separate
        // "Render Texture Overlay" with an internal blit from renderTexture.
        _outputRT = new RenderTexture(_inputWidth, _inputHeight, 24, format)
        {
            filterMode = FilterMode.Bilinear,
            enableRandomWrite = true // DLSS needs UAV access
        };
        _outputRT.Create();

        camera.targetTexture = _outputRT;

        // Game RT at full output res — the game's overlay pipeline blits this to screen
        if (gameRT != null)
        {
            gameRT.Release();
            gameRT.width = _outputWidth;
            gameRT.height = _outputHeight;
            gameRT.Create();
        }

        Plugin.Log.LogInfo($"Upscaler: {_inputWidth}x{_inputHeight} -> {_outputWidth}x{_outputHeight}");

        if (_upscaler != null && _camera != null)
        {
            _upscaler.Initialize(_camera, _inputWidth, _inputHeight, _outputWidth, _outputHeight);
            Plugin.Log.LogInfo($"Upscaler active: {_upscaler.Name}");
        }

        // register jitter callbacks for any temporal upscaler
        if (_upscaler != null && _camera != null)
        {
            Camera.onPreRender += OnPreRenderJitter;
            Camera.onPostRender += OnPostRenderRestore;
        }

        if (_useCameraCallback && _camera != null)
            Camera.onPostRender += OnPostRenderCallback;
    }

    private void OnPreRenderJitter(Camera cam)
    {
        if (cam != _camera || _upscaler == null || !Settings.ModEnabled) return;
        if (_inputWidth <= 0 || _inputHeight <= 0) return;

        _jitterIndex = (_jitterIndex + 1) % HaltonX.Length;
        JitterX = (HaltonX[_jitterIndex] - 0.5f) / _inputWidth;
        JitterY = (HaltonY[_jitterIndex] - 0.5f) / _inputHeight;

        cam.ResetProjectionMatrix();
        _savedProjectionMatrix = cam.projectionMatrix;
        cam.nonJitteredProjectionMatrix = _savedProjectionMatrix;

        var jittered = _savedProjectionMatrix;
        jittered.m02 += JitterX * 2f;
        jittered.m12 += JitterY * 2f;
        cam.projectionMatrix = jittered;
        _jitterApplied = true;
    }

    private void OnPostRenderRestore(Camera cam)
    {
        if (cam != _camera || !_jitterApplied) return;
        cam.projectionMatrix = _savedProjectionMatrix;
        _jitterApplied = false;
    }

    private void OnPostRenderCallback(Camera cam)
    {
        if (cam != _camera) return;
        ProcessFrame();
    }

    private static float[] GenerateHalton(int baseVal, int count)
    {
        var result = new float[count];
        for (int i = 0; i < count; i++)
        {
            float f = 1f, r = 0f;
            int idx = i + 1;
            while (idx > 0) { f /= baseVal; r += f * (idx % baseVal); idx /= baseVal; }
            result[i] = r;
        }
        return result;
    }

    private void LateUpdate()
    {
        if (!Settings.ModEnabled || _renderTextureMain == null) return;

        // Passthrough: nothing to do
        if (CurrentTier == RenderTier.Passthrough) return;

        // Resolution change detection
        int targetW = Settings.OutputWidth;
        int targetH = Settings.OutputHeight;
        if (targetW != _outputWidth || targetH != _outputHeight)
        {
            _outputWidth = targetW;
            _outputHeight = targetH;
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

        // game may redirect camera back to renderTexture
        if (_camera != null && _outputRT != null && _camera.targetTexture != _outputRT)
            _camera.targetTexture = _outputRT;

        // DLSS is processed in OnPostRender callback for correct depth/MV timing
        if (_useCameraCallback) return;

        ProcessFrame();
    }

    private void ProcessFrame()
    {
        if (_renderTextureMain == null || _upscaler == null) return;
        if (_outputRT == null || !_outputRT.IsCreated()) return;

        var gameRT = _renderTextureMain.renderTexture;
        if (gameRT == null || !gameRT.IsCreated()) return;

        // _outputRT (low-res camera target) → upscaler → gameRT (full-res display)
        _upscaler.OnRenderImage(_outputRT, gameRT);

        if (_upscaler is not DLSSUpscaler && Settings.Sharpening > 0.01f)
            ApplyCAS(gameRT);
    }

    private static void ApplyCAS(RenderTexture target)
    {
        if (target == null || !target.IsCreated()) return;
        var temp = RenderTexture.GetTemporary(target.width, target.height, 0, target.format);
        CASShader.Apply(target, temp, Settings.Sharpening);
        Graphics.Blit(temp, target);
        RenderTexture.ReleaseTemporary(temp);
    }

    private float _shadowBudgetTimer;
    private const float ShadowBudgetInterval = 0.1f;

    private void Update()
    {
        _shadowBudgetTimer += Time.unscaledDeltaTime;
        if (_shadowBudgetTimer >= ShadowBudgetInterval)
        {
            _shadowBudgetTimer = 0f;
            Patches.SceneOptimizer.UpdateShadowBudget(_camera);
            Patches.SceneOptimizer.UpdateDistanceShadowCull(_camera);
            Patches.SceneOptimizer.UpdateFlashlightShadowBudget(_camera);
            Patches.SceneOptimizer.UpdatePlayerAvatarShadowCull(_camera);
        }

        if (Input.GetKeyDown(KeyCode.F7) && Settings.ToggleKey != KeyCode.F7)
            LightDiagnostics.Run();

        // F9 launches the full diagnostic sweep when the user has opted in via
        // the config file — off by default so normal play doesn't trigger a 90s
        // probe. Gated to gameplay levels only (menus have no scene to measure);
        // pressing F9 again during a run aborts it.
        if (Input.GetKeyDown(KeyCode.F9)
            && Settings.ToggleKey != KeyCode.F9
            && Settings.DiagnosticsEnabled
            && (CostProbe.Running || IsInGameplayLevel()))
            CostProbe.Toggle();

        // F11 toggles the optimization layer only — upscaler / AA stay on.
        // When off, every shadow / physics / render hack reverts to vanilla;
        // F10 cuts the whole mod instead
        if (Input.GetKeyDown(KeyCode.F11) && Settings.ToggleKey != KeyCode.F11)
        {
            Settings.OptimizationsEnabled = !Settings.OptimizationsEnabled;
            Plugin.Log.LogInfo($"Optimizations {(Settings.OptimizationsEnabled ? "ENABLED" : "DISABLED")}");
            if (!Settings.OptimizationsEnabled)
                Patches.SceneOptimizer.LogRestoreState("pre-opt-disable");
            Patches.SceneOptimizer.Apply();
            Patches.QualityPatch.ApplyQualitySettings();
            if (!Settings.OptimizationsEnabled)
                Patches.SceneOptimizer.LogRestoreState("post-opt-disable");
        }

        if (!_benchmarkActive && !_togglePending && Input.GetKeyDown(Settings.ToggleKey))
        {
            bool enabling = !Settings.ModEnabled;

            // fire glitch BEFORE switching so it's already rendering when the swap happens
            PlayGlitch();

            if (!enabling)
            {
                // disabling is instant — no render texture rebuild needed
                Settings.ModEnabled = false;
                Plugin.Log.LogInfo("Mod DISABLED");
                // snapshot modifications BEFORE restore so the log shows what we reverted
                Patches.SceneOptimizer.LogRestoreState("pre-disable");
                Patches.SceneOptimizer.Apply();

                _upscaler?.Dispose();
                _upscaler = null;

                if (_camera != null && _renderTextureMain != null)
                    _camera.targetTexture = _renderTextureMain.renderTexture;

                Patches.RenderTexturePatch.RestoreVanillaResolution();
                Patches.RenderTexturePatch.RestoreVanillaCameraSettings();
                RestoreVanillaSettings();
                Patches.QualityPatch.RestoreVanillaQuality();
                Patches.PlayerAvatarMenuAAPatch.RestoreAvatarRt();

                if (GraphicsManager.instance != null)
                    GraphicsManager.instance.UpdateAll();

                if (_camera != null)
                    _camera.layerCullDistances = new float[32];

                Patches.SceneOptimizer.LogRestoreState("post-disable");
            }
            else
            {
                // enabling: defer by 2 frames so the glitch covers the RT rebuild
                _togglePending = true;
                StartCoroutine(DeferredEnable());
            }
        }

        // cancel benchmark if we left the gameplay level
        if (_benchmarkActive && !IsInGameplayLevel())
        {
            if (_benchmarkPhase == 0)
                ApplyBenchmarkScale(_savedRenderScale);
            _benchmarkActive = false;
            _autoBenchmark = false;
            QualitySettings.vSyncCount = _benchmarkVsyncPrev;
            Application.targetFrameRate = _benchmarkFpsPrev;
            Settings.BenchmarkMode = false;
        }

        if (Settings.AutoTuneNeedsBenchmark && !_benchmarkActive && !_autoBenchmark
            && _vanillaSaved && IsInGameplayLevel()
            && LevelGenerator.Instance != null && LevelGenerator.Instance.Generated
            && Time.unscaledTime - _lastEnvironmentSetupTime >= 10f)
        {
            _autoBenchmark = true;
            StartBenchmark();
            Plugin.Log.LogInfo("=== AUTO-BENCHMARK: autotune profile stale, re-detecting ===");
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

            if (frameTimeMs > 0.01f && frameTimeMs < 500f)
                _benchmarkFrameTimes.Add(frameTimeMs);

            if (_benchmarkPhase == 0)
            {
                bool ready = _benchmarkTimer >= Phase0Duration && _benchmarkFrameTimes.Count > 30;
                bool timeout = _benchmarkTimer >= Phase0Duration * 3f;
                if (ready || timeout)
                    FinishPhase0();
            }
            else
            {
                float duration = _autoBenchmark ? AutoBenchmarkDuration : BenchmarkDuration;
                bool ready = _benchmarkTimer >= duration && _benchmarkFrameTimes.Count > 30;
                bool timeout = _benchmarkTimer >= duration * 2f;
                if (ready || timeout)
                    FinishBenchmark();
            }
        }
    }

    private static void PlayGlitch()
    {
        var glitch = CameraGlitch.Instance;
        if (glitch == null) return;
        if (glitch.ActiveParent != null)
            glitch.ActiveParent.SetActive(true);
        glitch.PlayShort();
    }

    private System.Collections.IEnumerator DeferredEnable()
    {
        // wait 2 frames so the glitch effect covers the RT rebuild
        yield return null;
        yield return null;

        Settings.ModEnabled = true;
        Plugin.Log.LogInfo("Mod ENABLED");

        Reinitialize();
        Patches.RenderTexturePatch.ReapplyModCameraSettings();

        if (_renderTextureMain != null)
        {
            _renderTextureMain.textureWidthOriginal = Settings.OutputWidth;
            _renderTextureMain.textureHeightOriginal = Settings.OutputHeight;
            var rt = _renderTextureMain.renderTexture;
            if (rt != null && CurrentTier != RenderTier.Upscaler)
            {
                if (rt.width != _outputWidth || rt.height != _outputHeight)
                {
                    rt.Release();
                    rt.width = _outputWidth;
                    rt.height = _outputHeight;
                    rt.Create();
                }
            }
        }

        float fogMult = Settings.ResolvedFogMultiplier;
        if (_vanillaSaved && fogMult != 1f)
        {
            RenderSettings.fogStartDistance = _vanillaFogStart * fogMult;
            RenderSettings.fogEndDistance = _vanillaFogEnd * fogMult;
            if (_camera != null)
                _camera.farClipPlane = RenderSettings.fogEndDistance + 10f;
        }
        if (_vanillaSaved)
        {
            Settings.ResolvedEffectiveFogEnd = _vanillaFogEnd * fogMult;
            Settings.ApplyFogClamps();
        }

        Patches.SceneOptimizer.Apply();
        Patches.QualityPatch.ApplyQualitySettings();
        // Start already ran on existing PlayerAvatarMenus; postfix won't re-fire
        Patches.PlayerAvatarMenuAAPatch.ReapplyAll();
        _togglePending = false;
    }

    private void HandleResolutionChange()
    {
        if (CurrentTier != RenderTier.Upscaler) return;

        float scale = RenderTexturePatch.GetRenderScale();
        _inputWidth = Mathf.Max(Mathf.RoundToInt(_outputWidth * scale), 1);
        _inputHeight = Mathf.Max(Mathf.RoundToInt(_outputHeight * scale), 1);

        var gameRT = _renderTextureMain?.renderTexture;

        // Recreate _outputRT at new scaled input size
        ReleaseRT(ref _outputRT);
        var format = gameRT != null ? gameRT.format : RenderTextureFormat.DefaultHDR;
        _outputRT = new RenderTexture(_inputWidth, _inputHeight, 24, format)
        {
            filterMode = FilterMode.Bilinear,
            enableRandomWrite = true // DLSS needs UAV access
        };
        _outputRT.Create();

        if (_camera != null)
            _camera.targetTexture = _outputRT;

        // Game RT at full output res
        if (gameRT != null)
        {
            gameRT.Release();
            gameRT.width = _outputWidth;
            gameRT.height = _outputHeight;
            gameRT.Create();
        }

        if (_upscaler != null)
            _upscaler.OnResolutionChanged(_inputWidth, _inputHeight, _outputWidth, _outputHeight);
    }

    private int _benchmarkFpsPrev;

    private void StartBenchmark()
    {
        _benchmarkActive = true;
        _benchmarkTimer = 0f;
        _benchmarkWarmup = WarmupDuration;
        _benchmarkFrameTimes.Clear();
        _benchmarkPhase = 0;
        _lowGpuFps = 0f;

        // unlock framerate for accurate measurement
        _benchmarkVsyncPrev = QualitySettings.vSyncCount;
        _benchmarkFpsPrev = Application.targetFrameRate;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        // phase 0: drop render scale to measure CPU ceiling
        _savedRenderScale = Settings.ResolvedRenderScale;
        ApplyBenchmarkScale(25);
        PlayGlitch();
    }

    private void FinishPhase0()
    {
        _benchmarkFrameTimes.Sort();
        int count = _benchmarkFrameTimes.Count;
        float median = _benchmarkFrameTimes[count / 2];
        var filtered = _benchmarkFrameTimes.FindAll(t => t <= median * 3f);
        if (filtered.Count < 10) { _lowGpuFps = 0f; }
        else
        {
            float total = 0f;
            for (int i = 0; i < filtered.Count; i++) total += filtered[i];
            _lowGpuFps = 1000f / (total / filtered.Count);
        }

        Plugin.Log.LogInfo($"Phase 0 (CPU ceiling): {_lowGpuFps:F0} FPS at 25% scale");

        // transition to phase 1: restore scale, re-warmup, measure real perf
        ApplyBenchmarkScale(_savedRenderScale);
        PlayGlitch();
        _benchmarkPhase = 1;
        _benchmarkTimer = 0f;
        _benchmarkWarmup = WarmupDuration;
        _benchmarkFrameTimes.Clear();
    }

    private void ApplyBenchmarkScale(int scale)
    {
        Settings.ResolvedRenderScale = scale;
        int w = Mathf.Max(Mathf.RoundToInt(_outputWidth * scale / 100f), 1);
        int h = Mathf.Max(Mathf.RoundToInt(_outputHeight * scale / 100f), 1);
        if (w == _inputWidth && h == _inputHeight) return;

        _inputWidth = w;
        _inputHeight = h;

        if (CurrentTier == RenderTier.Upscaler)
        {
            ReleaseRT(ref _outputRT);
            var gameRT = _renderTextureMain?.renderTexture;
            var format = gameRT != null ? gameRT.format : RenderTextureFormat.DefaultHDR;
            _outputRT = new RenderTexture(w, h, 24, format) { filterMode = FilterMode.Bilinear };
            _outputRT.Create();
            if (_camera != null) _camera.targetTexture = _outputRT;
            if (_upscaler != null)
                _upscaler.OnResolutionChanged(w, h, _outputWidth, _outputHeight);
        }
    }

    private void FinishBenchmark()
    {
        try
        {
            _benchmarkFrameTimes.Sort();
            int count = _benchmarkFrameTimes.Count;

            float median = _benchmarkFrameTimes[count / 2];
            var filtered = _benchmarkFrameTimes.FindAll(t => t <= median * 3f);
            int filteredCount = filtered.Count;

            if (filteredCount < 10)
            {
                Plugin.Log.LogWarning("Benchmark: too few valid frames — skipping auto-tune");
                return;
            }

            float totalMs = 0f;
            for (int i = 0; i < filteredCount; i++) totalMs += filtered[i];
            float avgMs = totalMs / filteredCount;
            float avgFps = 1000f / avgMs;

            int lowCount = Mathf.Max(filteredCount / 100, 1);
            float low1TotalMs = 0f;
            for (int i = filteredCount - lowCount; i < filteredCount; i++) low1TotalMs += filtered[i];
            float low1Ms = low1TotalMs / lowCount;
            float low1Fps = 1000f / low1Ms;

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

            // if real FPS is close to the low-GPU-load ceiling, GPU isn't the bottleneck.
            // at high FPS the ratio naturally converges to 1.0 even on GPU-bound systems
            // (frame time differences shrink), so use a stricter threshold above 120 FPS.
            bool cpuBound = true;
            if (_lowGpuFps > 0f && avgFps > 0f)
            {
                float ratio = avgFps / _lowGpuFps;
                float threshold = avgFps > 120f ? 0.95f : 0.85f;
                cpuBound = ratio >= threshold;
                Plugin.Log.LogInfo($"  Ratio: {ratio:P1}, threshold: {threshold:P0} (fps={avgFps:F0})");

                // Proton/DXVK fix: draw call translation overhead scales with resolution,
                // so 25% scale also reduces CPU load — making the ratio misleadingly low.
                // Secondary check: if the CPU ceiling itself is below target, the CPU
                // can't sustain the target framerate regardless of GPU load.
                float targetFps = Mathf.Max((float)Screen.currentResolution.refreshRateRatio.value, 60f);
                bool translationLayer = Application.platform == RuntimePlatform.LinuxPlayer
                    || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan;

                if (!cpuBound && _lowGpuFps < targetFps)
                {
                    cpuBound = true;
                    Plugin.Log.LogInfo($"  Bottleneck override: CPU ceiling ({_lowGpuFps:F0}) < target ({targetFps:F0}) " +
                        $"-> CPU-BOUND (CPU can't sustain target even at minimum GPU load)");
                }
                else if (!cpuBound && translationLayer && ratio < 0.95f)
                {
                    // on translation layers, the 25% test is unreliable —
                    // use a tighter threshold since DXVK overhead artificially
                    // inflates the ceiling
                    cpuBound = true;
                    Plugin.Log.LogInfo($"  Bottleneck override: translation layer detected " +
                        $"({Application.platform}/{SystemInfo.graphicsDeviceType}), " +
                        $"ratio {ratio:P0} < 95% -> CPU-BOUND");
                }

                Plugin.Log.LogInfo($"  Bottleneck: {avgFps:F0} / {_lowGpuFps:F0} ceiling = {ratio:P0} " +
                    $"-> {(cpuBound ? "CPU-BOUND" : "GPU-bound")}");
            }

            Plugin.Log.LogInfo("=== BENCHMARK RESULTS ===");
            Plugin.Log.LogInfo($"  Preset: {preset} | Upscaler: {mode} | Render: {res} -> {_outputWidth}x{_outputHeight}");
            Plugin.Log.LogInfo($"  CPU: {SystemInfo.processorType} ({SystemInfo.processorCount} threads)");
            Plugin.Log.LogInfo($"  RAM: {SystemInfo.systemMemorySize}MB | Platform: {Application.platform} | API: {SystemInfo.graphicsDeviceType}");
            Plugin.Log.LogInfo($"  Frames: {filteredCount} measured, {discarded} outliers discarded");
            Plugin.Log.LogInfo($"  Avg: {avgFps:F1} FPS ({avgMs:F1}ms)");
            Plugin.Log.LogInfo($"  1% Low: {low1Fps:F1} FPS ({low1Ms:F1}ms)");
            if (low01Fps > 0) Plugin.Log.LogInfo($"  0.1% Low: {low01Fps:F1} FPS");
            Plugin.Log.LogInfo($"  CPU ceiling (25% scale): {_lowGpuFps:F1} FPS | Full scale: {avgFps:F1} FPS | Ratio: {(avgFps / Mathf.Max(_lowGpuFps, 1f)):P0}");
            Plugin.Log.LogInfo("=========================");

            Settings.AutoSelectPreset(avgFps, low1Fps, low01Fps, cpuBound);

            // glitch to cover the settings switch
            PlayGlitch();
        }
        finally
        {
            QualitySettings.vSyncCount = _benchmarkVsyncPrev;
            Application.targetFrameRate = _benchmarkFpsPrev;
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

        Settings.ResolvedEffectiveFogEnd = _vanillaFogEnd * Settings.ResolvedFogMultiplier;
        Settings.ApplyFogClamps();

        Plugin.Log.LogInfo($"Vanilla fog: start={_vanillaFogStart:F0}m end={_vanillaFogEnd:F0}m clip={_vanillaFarClip:F0}m (env #{_environmentSetupCount})");
    }

    private void RestoreVanillaSettings()
    {
        if (_vanillaSaved)
        {
            RenderSettings.fogStartDistance = _vanillaFogStart;
            RenderSettings.fogEndDistance = _vanillaFogEnd;
            if (_camera != null)
                _camera.farClipPlane = _vanillaFarClip;
        }

        Plugin.Log.LogInfo("Vanilla settings restored");
    }

    private void OnDestroy()
    {
        Settings.OnSettingsChanged -= Reinitialize;
        Camera.onPreRender -= OnPreRenderJitter;
        Camera.onPostRender -= OnPostRenderRestore;

        if (_useCameraCallback)
            Camera.onPostRender -= OnPostRenderCallback;

        _upscaler?.Dispose();

        // Restore camera target to game's render texture
        if (_camera != null && _renderTextureMain != null)
            _camera.targetTexture = _renderTextureMain.renderTexture;

        ReleaseRT(ref _outputRT);

        Instance = null;
    }
}

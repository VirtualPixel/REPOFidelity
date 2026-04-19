using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace REPOFidelity;

// F9-triggered dev tool. Samples every profiler recorder Unity exposes,
// times each camera individually, Harmony-instruments the top MonoBehaviour
// types for per-script cost, sweeps upscalers + Potato + vanilla, and writes
// a ranked frame_cost.txt (also copied to the clipboard).
internal class CostProbe : MonoBehaviour
{
    internal static CostProbe? Instance { get; private set; }
    internal static bool Running { get; private set; }
    internal static string Status = "";
    internal static float Progress;

    private const float WarmupSeconds = 1.5f;
    private const float SampleSeconds = 8f;
    private const float SettleSeconds = 1.5f;
    private const float UpscalerSampleSeconds = 3f;
    private const float ScriptSampleSeconds = 4f;
    private const int   ScriptTopN = 20;

    private static readonly string OutputPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
        "frame_cost.txt");

    private readonly Dictionary<Camera, CamTiming> _camTimings = new();

    private int _savedVSyncCount;
    private int _savedTargetFrameRate;
    private bool _frameLimitUncapped;
    private bool _presetRevertSuppressed;

    private float _sweepStartTime;
    private float _sweepExpectedDuration;
    private float _sweepStartProgress;
    private float _sweepEndProgress;
    private bool _sweepSmoothActive;

    private class CamTiming
    {
        public readonly Stopwatch Sw = new();
        public double TotalMs;
        public int Frames;
    }

    internal static void Toggle()
    {
        if (Running) { Abort(); return; }
        if (Instance == null)
        {
            var go = new GameObject("REPOFidelity_CostProbe");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<CostProbe>();
        }
        Instance.StartCoroutine(Instance.RunSafe());
    }

    private IEnumerator WaitForAutotuneIfActive()
    {
        if (!UpscalerManager.BenchmarkActive) yield break;
        Status = "Waiting for autotune to finish";
        while (UpscalerManager.BenchmarkActive) yield return null;
        yield return new WaitForSeconds(SettleSeconds);
    }

    internal static void Abort()
    {
        if (!Running) return;
        if (Instance != null)
        {
            Instance.StopAllCoroutines();
            Camera.onPreRender  -= Instance.OnCamPre;
            Camera.onPostRender -= Instance.OnCamPost;
            Instance._camTimings.Clear();
            Instance.RestoreFrameLimit();
            Instance.RestorePresetRevertSuppression();
            Settings.AllocationFixesEnabled = true;
        }
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        Running = false;
        Status = "";
        Plugin.Log.LogInfo("Cost probe cancelled");
    }

    private void RestoreFrameLimit()
    {
        if (!_frameLimitUncapped) return;
        QualitySettings.vSyncCount = _savedVSyncCount;
        Application.targetFrameRate = _savedTargetFrameRate;
        _frameLimitUncapped = false;
    }

    private void RestorePresetRevertSuppression()
    {
        if (!_presetRevertSuppressed) return;
        Settings.PopPresetRevertSuppression();
        _presetRevertSuppressed = false;
    }

    private static void AppendConfigFiles(StringBuilder report)
    {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        AppendFileSection(report, "autotune.json", Path.Combine(dir, "autotune.json"));
        AppendFileSection(report, "settings.json", Path.Combine(dir, "settings.json"));
    }

    private static void AppendFileSection(StringBuilder report, string label, string path)
    {
        report.AppendLine($"== {label} ==");
        try
        {
            report.AppendLine(File.Exists(path) ? File.ReadAllText(path).TrimEnd() : "(missing)");
        }
        catch (Exception ex)
        {
            report.AppendLine($"(read failed: {ex.Message})");
        }
        report.AppendLine();
    }

    // Keeps the game's input-disable timer topped up while the probe runs so the
    // player can't walk around or move the camera during measurement. F9 still
    // goes through Unity's raw Input manager, so the abort binding still fires.
    // GameDirector.SetDisableInput naturally decays to false within a second of
    // the last call, so no explicit cleanup is needed on Abort / natural finish.
    void Update()
    {
        if (!Running) return;
        if (GameDirector.instance != null)
            GameDirector.instance.SetDisableInput(1f);

        // Smooth the sweep progress bar — the discrete SweepProgress() cells
        // jump in ~7% chunks otherwise. Interpolate wall-clock between the
        // current cell's start value and the next cell's start value so the
        // bar animates instead of snapping.
        if (_sweepSmoothActive && _sweepExpectedDuration > 0f)
        {
            float t = Mathf.Clamp01((Time.unscaledTime - _sweepStartTime) / _sweepExpectedDuration);
            float target = Mathf.Lerp(_sweepStartProgress, _sweepEndProgress, t);
            if (target > Progress) Progress = target;
        }
    }

    private IEnumerator RunSafe()
    {
        var inner = Run();
        while (true)
        {
            bool next;
            try { next = inner.MoveNext(); }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Cost probe failed: {ex}");
                Camera.onPreRender  -= OnCamPre;
                Camera.onPostRender -= OnCamPost;
                RestoreFrameLimit();
                RestorePresetRevertSuppression();
                Settings.AllocationFixesEnabled = true;
                Running = false;
                Status = "ERROR";
                yield break;
            }
            if (!next) yield break;
            yield return inner.Current;
        }
    }

    private void OnCamPre(Camera cam)
    {
        if (!Running || cam == null) return;
        if (!_camTimings.TryGetValue(cam, out var t))
        {
            t = new CamTiming();
            _camTimings[cam] = t;
        }
        t.Sw.Restart();
    }

    private void OnCamPost(Camera cam)
    {
        if (!Running || cam == null) return;
        if (!_camTimings.TryGetValue(cam, out var t)) return;
        t.Sw.Stop();
        t.TotalMs += t.Sw.Elapsed.TotalMilliseconds;
        t.Frames++;
    }

    private IEnumerator Run()
    {
        Running = true;
        Progress = 0f;
        _camTimings.Clear();

        yield return WaitForAutotuneIfActive();

        var savedLockState = Cursor.lockState;
        var savedVisible   = Cursor.visible;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;

        // Save user state — baseline measures the user's real game settings so
        // the profiler breakdown reflects what they're actually experiencing. The
        // sweep later normalizes to a fixed config (Ultra + DLAA + fog 1.0×) so
        // individual preset / fog cells are comparable across users and builds.
        var origPreset = Settings.Preset;
        var origUpscaler = Settings.UpscaleModeSetting;
        var origFog = Settings.FogDistanceMultiplier;
        var origModEnabled = Settings.ModEnabled;

        // Force mod on for baseline so the breakdown reflects gameplay-with-mod
        // cost. If the user left F10 off when they triggered the probe, baseline
        // would just duplicate the Vanilla (F10) sweep cell.
        if (!origModEnabled)
        {
            Settings.ModEnabled = true;
            Patches.SceneOptimizer.Apply();
            Patches.QualityPatch.ApplyQualitySettings();
        }

        var report = new StringBuilder();
        report.AppendLine($"== REPOFidelity frame cost — {DateTime.Now:yyyy-MM-dd HH:mm:ss} (v{BuildInfo.Version}) ==");
        report.AppendLine($"GPU:    {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize}MB, {SystemInfo.graphicsDeviceType})");
        report.AppendLine($"CPU:    {SystemInfo.processorType} ({SystemInfo.processorCount} cores, {SystemInfo.systemMemorySize}MB RAM)");
        report.AppendLine($"OS:     {SystemInfo.operatingSystem}");
        report.AppendLine($"Screen: {Screen.width}x{Screen.height} @ {Screen.currentResolution.refreshRateRatio.value:F0}Hz ({(Screen.fullScreen ? "fullscreen" : "windowed")})");
        report.AppendLine($"Mod:    [{Settings.Preset}] upscaler={Settings.ResolvedUpscaleMode} " +
            $"scale={Settings.ResolvedRenderScale}% shadowQ={Settings.ResolvedShadowQuality} " +
            $"shadowD={Settings.ResolvedShadowDistance:F0}m lights={Settings.ResolvedPixelLightCount} " +
            $"lod={Settings.ResolvedLODBias:F1} AF={Settings.ResolvedAnisotropicFiltering}x");
        report.AppendLine($"Range:  fog={Settings.ResolvedFogMultiplier:F2}x " +
            $"effectiveFogEnd={Settings.ResolvedEffectiveFogEnd:F0}m " +
            $"lightD={Settings.ResolvedLightDistance:F0}m shadowBudget={Settings.ResolvedShadowBudget}");
        report.AppendLine($"Flags:  modEnabled={Settings.ModEnabled} optEnabled={Settings.OptimizationsEnabled} cpuPatches={Settings.CpuPatchesActive}");
        report.AppendLine();

        Status = "Measuring (all markers + per-camera + scene)";
        yield return new WaitForSeconds(SettleSeconds);

        var handles = new List<ProfilerRecorderHandle>();
        ProfilerRecorderHandle.GetAvailable(handles);

        var timeRecs  = new List<(ProfilerRecorder rec, string name, string cat)>();
        var countRecs = new List<(ProfilerRecorder rec, string name, string cat)>();
        var memRecs   = new List<(ProfilerRecorder rec, string name, string cat)>();

        foreach (var h in handles)
        {
            var desc = ProfilerRecorderHandle.GetDescription(h);
            var rec = ProfilerRecorder.StartNew(desc.Category, desc.Name, 256);
            var entry = (rec, desc.Name, desc.Category.ToString());
            switch (desc.UnitType)
            {
                case ProfilerMarkerDataUnit.TimeNanoseconds: timeRecs.Add(entry);  break;
                case ProfilerMarkerDataUnit.Count:           countRecs.Add(entry); break;
                case ProfilerMarkerDataUnit.Bytes:           memRecs.Add(entry);   break;
            }
        }

        Camera.onPreRender  += OnCamPre;
        Camera.onPostRender += OnCamPost;

        yield return new WaitForSeconds(WarmupSeconds);
        Progress = 0.03f;

        var timeTotals  = new double[timeRecs.Count];
        var countTotals = new long[countRecs.Count];
        var memTotals   = new long[memRecs.Count];

        // GC tracking — delta over the sample reveals whether 0.1% lows come
        // from GC pauses. Unity's Mono stops the world on gen 0 collection; a
        // few collections over an 8s sample is normal, many is a spike source.
        int gen0Start = System.GC.CollectionCount(0);
        int gen1Start = System.GC.CollectionCount(1);
        long monoStart = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
        float worstFrameMs = 0f;

        // Reset right before baseline so mod-internal timing only covers the
        // baseline sample window, not the script-instrumentation or sweep phases.
        ModTiming.Reset();

        var frames = new List<float>(2048);
        float elapsed = 0f;
        while (elapsed < SampleSeconds)
        {
            float dt = Time.unscaledDeltaTime;
            frames.Add(dt);
            elapsed += dt;
            float frameMs = dt * 1000f;
            if (frameMs > worstFrameMs) worstFrameMs = frameMs;
            Progress = 0.03f + 0.22f * (elapsed / SampleSeconds);
            for (int i = 0; i < timeRecs.Count;  i++) timeTotals[i]  += timeRecs[i].rec.LastValue;
            for (int i = 0; i < countRecs.Count; i++) countTotals[i] += countRecs[i].rec.LastValue;
            for (int i = 0; i < memRecs.Count;   i++) memTotals[i]   += memRecs[i].rec.LastValue;
            yield return null;
        }

        var modTimingSnapshot = ModTiming.Read().ToList();

        int gen0Delta = System.GC.CollectionCount(0) - gen0Start;
        int gen1Delta = System.GC.CollectionCount(1) - gen1Start;
        long monoEnd = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();

        Camera.onPreRender  -= OnCamPre;
        Camera.onPostRender -= OnCamPost;
        Progress = 0.25f;

        int fc = Mathf.Max(1, frames.Count);
        var timeStats  = new List<(string name, string cat, double ms)>(timeRecs.Count);
        var countStats = new List<(string name, string cat, double n)>(countRecs.Count);
        var memStats   = new List<(string name, string cat, double bytes)>(memRecs.Count);
        for (int i = 0; i < timeRecs.Count; i++)
            timeStats.Add((timeRecs[i].name, timeRecs[i].cat, timeTotals[i] / fc / 1_000_000.0));
        for (int i = 0; i < countRecs.Count; i++)
            countStats.Add((countRecs[i].name, countRecs[i].cat, (double)countTotals[i] / fc));
        for (int i = 0; i < memRecs.Count; i++)
            memStats.Add((memRecs[i].name, memRecs[i].cat, (double)memTotals[i] / fc));

        for (int i = 0; i < timeRecs.Count;  i++) timeRecs[i].rec.Dispose();
        for (int i = 0; i < countRecs.Count; i++) countRecs[i].rec.Dispose();
        for (int i = 0; i < memRecs.Count;   i++) memRecs[i].rec.Dispose();

        var baseline = ComputeSample(frames);

        timeStats.Sort((a, b) => b.ms.CompareTo(a.ms));
        countStats.Sort((a, b) => b.n.CompareTo(a.n));
        memStats.Sort((a, b) => b.bytes.CompareTo(a.bytes));

        double cpuMain  = LookupMs(timeStats, "CPU Main Thread Frame Time");
        double cpuRend  = LookupMs(timeStats, "CPU Render Thread Frame Time");
        double cpuTotal = LookupMs(timeStats, "CPU Total Frame Time");
        double gpuTotal = LookupMs(timeStats, "GPU Frame Time");
        // Unity's GPU Frame Time recorder occasionally returns garbage values
        // (overflow / driver hiccup during rapid state swaps). Clamp to sane range.
        if (gpuTotal > 100.0 || gpuTotal < 0) gpuTotal = 0;
        string bottleneck = gpuTotal <= 0 ? "CPU (main thread) [GPU marker unavailable]"
                          : gpuTotal > cpuMain + 0.2 ? "GPU"
                          : cpuMain > gpuTotal + 0.2 ? "CPU (main thread)"
                          : "balanced CPU/GPU";

        report.AppendLine($"Baseline:     {baseline.AvgMs:F2} ms ({baseline.AvgFps:F0} fps)  1%={baseline.P1Low:F0} fps  0.1%={baseline.P01Low:F0} fps  worstFrame={worstFrameMs:F1} ms  ({baseline.FrameCount} frames)");
        if (cpuTotal > 0) report.AppendLine($"CPU total:    {cpuTotal:F2} ms    main={cpuMain:F2}   render={cpuRend:F2}");
        if (gpuTotal > 0) report.AppendLine($"GPU total:    {gpuTotal:F2} ms");
        report.AppendLine($"Bottleneck:   {bottleneck}");
        long monoDeltaKb = (monoEnd - monoStart) / 1024;
        float gcPerSec = gen0Delta / (float)SampleSeconds;
        report.AppendLine($"GC:           gen0={gen0Delta} gen1={gen1Delta} over {SampleSeconds:F0}s  ({gcPerSec:F2}/s)  mono Δ={monoDeltaKb:+0;-0} KB");
        report.AppendLine();

        // ---- A/B comparison: same scene, allocation patches off vs on ----
        // Temporary diagnostic: flip Settings.AllocationFixesEnabled, sample again,
        // restore. The previous baseline (above) already captured "patches on" data,
        // so we only need the inverse half here. Same procedural map → variance
        // factored out, the delta is purely the patches' contribution.
        Status = "Sampling baseline with allocation patches OFF (A/B compare)";
        Settings.AllocationFixesEnabled = false;
        yield return new WaitForSeconds(SettleSeconds);

        int abGen0Start = System.GC.CollectionCount(0);
        int abGen1Start = System.GC.CollectionCount(1);
        long abMonoStart = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
        float abWorstMs = 0f;
        var abFrames = new List<float>(2048);
        float abElapsed = 0f;
        while (abElapsed < SampleSeconds)
        {
            float dt = Time.unscaledDeltaTime;
            abFrames.Add(dt);
            abElapsed += dt;
            float frameMs = dt * 1000f;
            if (frameMs > abWorstMs) abWorstMs = frameMs;
            Progress = 0.25f + 0.05f * (abElapsed / SampleSeconds);
            yield return null;
        }
        Settings.AllocationFixesEnabled = true;

        var abSample = ComputeSample(abFrames);
        int abGen0 = System.GC.CollectionCount(0) - abGen0Start;
        int abGen1 = System.GC.CollectionCount(1) - abGen1Start;
        long abMonoKb = (UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - abMonoStart) / 1024;

        report.AppendLine("== A/B compare (allocation patches on vs off, same scene) ==");
        report.AppendLine($"  patches ON :  {baseline.AvgMs:F2} ms ({baseline.AvgFps:F0} fps)  worstFrame={worstFrameMs:F1}  gen1={gen1Delta}  mono Δ={monoDeltaKb:+0;-0} KB");
        report.AppendLine($"  patches OFF:  {abSample.AvgMs:F2} ms ({abSample.AvgFps:F0} fps)  worstFrame={abWorstMs:F1}  gen1={abGen1}  mono Δ={abMonoKb:+0;-0} KB");
        float dMs = abSample.AvgMs - baseline.AvgMs;
        float dWorst = abWorstMs - worstFrameMs;
        long dMono = abMonoKb - monoDeltaKb;
        int dGen1 = abGen1 - gen1Delta;
        report.AppendLine($"  delta (off−on): {dMs:+0.00;-0.00} ms   worst {dWorst:+0.0;-0.0} ms   gen1 {dGen1:+0;-0}   mono {dMono:+0;-0} KB  (positive = patches helped)");
        report.AppendLine();

        // ---- Every profiler marker, ranked ----
        report.AppendLine("== Frame cost — profiler markers ranked highest→lowest (ms/frame) ==");
        int shown = 0;
        foreach (var (name, cat, ms) in timeStats)
        {
            if (ms < 0.01) continue;
            report.AppendLine($"  {ms,7:F2} ms  [{cat,-12}] {name}");
            if (++shown >= 80) break;
        }
        if (shown == 0) report.AppendLine("  (no profiler time markers captured — Unity build may strip them)");
        report.AppendLine();

        // ---- Mod-internal cost ----
        double tickToMsMod = 1000.0 / Stopwatch.Frequency;
        int baselineFrames = Mathf.Max(1, frames.Count);
        var modRows = modTimingSnapshot
            .Select(r => (name: r.name, ms: r.ticks * tickToMsMod / baselineFrames,
                          callsPerFrame: (float)r.calls / baselineFrames, r.calls))
            .Where(r => r.calls > 0)
            .OrderByDescending(r => r.ms)
            .ToList();

        report.AppendLine($"== Mod-internal cost (Stopwatch spans over {SampleSeconds:F0}s baseline, ms/frame) ==");
        if (modRows.Count == 0)
        {
            report.AppendLine("  (no mod spans hit — mod may be disabled or pipeline idle)");
        }
        else
        {
            report.AppendLine("  ms/frame   calls/f   name");
            double modTotal = 0;
            foreach (var r in modRows)
            {
                report.AppendLine($"  {r.ms,7:F3}    {r.callsPerFrame,5:F1}   {r.name.Substring("REPOFidelity.".Length)}");
                modTotal += r.ms;
            }
            report.AppendLine($"  ──────");
            report.AppendLine($"  {modTotal,7:F3}   (sum of instrumented mod spans)");
        }
        report.AppendLine();

        // ---- Draw stats ----
        report.AppendLine("== Draw stats (per frame avg) ==");
        bool anyCount = false;
        void EmitCount(string label, string marker)
        {
            double n = LookupCount(countStats, marker);
            if (n <= 0) return;
            report.AppendLine($"  {label,-22} {FmtBig(n)}");
            anyCount = true;
        }
        EmitCount("Draw calls:",      "Draw Calls Count");
        EmitCount("Batches:",         "Batches Count");
        EmitCount("SetPass calls:",   "SetPass Calls Count");
        EmitCount("Triangles:",       "Triangles Count");
        EmitCount("Vertices:",        "Vertices Count");
        EmitCount("Shadow casters:",  "Shadow Casters Count");
        EmitCount("Used textures:",   "Used Textures Count");
        EmitCount("Render textures:", "Render Textures Count");
        if (!anyCount) report.AppendLine("  (no count markers captured)");
        report.AppendLine();

        if (countStats.Count > 0)
        {
            report.AppendLine("== All count markers, ranked (top 20) ==");
            int c = 0;
            foreach (var (name, cat, n) in countStats)
            {
                if (n < 1) continue;
                report.AppendLine($"  {FmtBig(n),10}  [{cat,-12}] {name}");
                if (++c >= 20) break;
            }
            report.AppendLine();
        }

        // Only per-frame-delta memory markers (GC alloc, frame uploads) —
        // excluding gauge markers like "System Used Memory" which report totals.
        var perFrameMem = memStats.Where(x =>
            x.name.IndexOf("In Frame", StringComparison.OrdinalIgnoreCase) >= 0 ||
            x.name.IndexOf("Alloc",    StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (perFrameMem.Count > 0)
        {
            report.AppendLine("== Allocations per frame (GC / buffer uploads) ==");
            foreach (var (name, cat, b) in perFrameMem)
            {
                if (b < 1) continue;
                report.AppendLine($"  {FmtBytes(b),10}  [{cat,-12}] {name}");
            }
            report.AppendLine();
        }

        // ---- Per-camera ----
        report.AppendLine("== Per-camera render cost (CPU Stopwatch around onPreRender/onPostRender) ==");
        if (_camTimings.Count == 0)
        {
            report.AppendLine("  (no camera events fired during sample — unusual)");
        }
        else
        {
            var cams = _camTimings
                .Select(kv => (cam: kv.Key, ms: kv.Value.TotalMs / Mathf.Max(1, kv.Value.Frames), kv.Value.Frames))
                .OrderByDescending(x => x.ms)
                .ToList();
            foreach (var (cam, ms, f) in cams)
            {
                string name = cam != null ? cam.name : "(destroyed)";
                report.AppendLine($"  {ms,7:F2} ms  {name}  ({f} renders)");
            }
        }
        _camTimings.Clear();
        report.AppendLine();

        // ---- Scene composition ----
        var scene = GatherSceneMetrics();
        report.AppendLine("== Scene composition ==");
        report.AppendLine($"  Renderers:                     {scene.Renderers.Total}");
        report.AppendLine($"    Visible:                       {scene.Renderers.Visible} ({Pct(scene.Renderers.Visible, scene.Renderers.Total)})");
        report.AppendLine($"    Shadow casting:                {scene.Renderers.ShadowCasting} ({Pct(scene.Renderers.ShadowCasting, scene.Renderers.Total)})");
        report.AppendLine($"    Shadow casting + off-screen:   {scene.Renderers.ShadowCastingHidden} ({Pct(scene.Renderers.ShadowCastingHidden, scene.Renderers.Total)})  ← off-screen shadow work");
        report.AppendLine($"    No LODGroup:                   {scene.Renderers.NoLODGroup} ({Pct(scene.Renderers.NoLODGroup, scene.Renderers.Total)})");
        report.AppendLine($"    Tris (sum of sharedMesh):      {FmtBig(scene.TotalSceneTris)}");
        report.AppendLine($"  Skinned mesh renderers:         {scene.SkinnedRenderers}");
        report.AppendLine($"  Particle systems:               {scene.TotalParticles} total, {scene.ActiveParticles} emitting, {FmtBig(scene.ActiveParticleCount)} particles alive");
        report.AppendLine($"  Reflection probes:              {scene.ReflectionProbes}");
        report.AppendLine($"  Trail / Line renderers:         {scene.TrailRenderers} / {scene.LineRenderers}");
        report.AppendLine($"  Audio sources (playing):        {scene.AudioSourcesPlaying} / {scene.AudioSources}");
        report.AppendLine($"  Lights:                         {scene.Lights.Active} active, {scene.Lights.Shadowing} casting shadow");
        report.AppendLine($"    Directional + shadow:          {scene.Lights.Directional}");
        report.AppendLine($"    Point + shadow:                {scene.Lights.PointShadow}  (×6 cubemap faces)");
        report.AppendLine($"    Spot + shadow:                 {scene.Lights.SpotShadow}");
        report.AppendLine($"  MonoBehaviours w/ Update:       {scene.UpdatingBehaviours}");
        report.AppendLine($"  MonoBehaviours w/ LateUpdate:   {scene.LateUpdatingBehaviours}");
        report.AppendLine($"  MonoBehaviours w/ FixedUpdate:  {scene.FixedUpdatingBehaviours}");
        report.AppendLine();

        // ---- Multiplayer / per-player breakdown ----
        AppendMultiplayerSection(report);

        // ---- Top MonoBehaviour types by instance count ----
        if (scene.TopBehaviourTypes.Count > 0)
        {
            report.AppendLine("== MonoBehaviour types with Update, ranked by live instance count (top 20) ==");
            foreach (var (name, count) in scene.TopBehaviourTypes.Take(20))
                report.AppendLine($"  {count,5}×  {name}");
            report.AppendLine();
        }

        // ---- Per-light shadow cost proxy ----
        if (scene.LightDetail.Count > 0)
        {
            report.AppendLine("== Per-light shadow cost proxy, ranked (top 20) ==");
            report.AppendLine("  score   type         range     res   shadow     name");
            foreach (var l in scene.LightDetail.OrderByDescending(x => x.CostScore).Take(20))
                report.AppendLine($"  {l.CostScore,5:F0}   {l.Type,-12} {l.Range,5:F0}m   {l.Resolution,5}   {l.ShadowMode,-8}  {l.Name}");
            report.AppendLine("  (proxy = faces × res² × range² for shadowers, 0 otherwise — higher = more shadow map work)");
            report.AppendLine();
        }

        // ---- Harmony-instrumented per-script timing ----
        // Unity retail strips Behaviour.Update, so the only way to see
        // which MonoBehaviours eat frame time is to patch them ourselves.
        var scriptTimings = new List<(string label, double totalMs, double perCallUs, int calls)>();
        if (scene.TopBehaviourTypes.Count > 0)
        {
            Status = "Script profiling (Harmony)";
            var topTypes = new List<Type>();
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                if (!mb.isActiveAndEnabled) continue;
                var t = mb.GetType();
                if (!topTypes.Contains(t)) topTypes.Add(t);
            }
            var topSet = scene.TopBehaviourTypes.Take(ScriptTopN).Select(x => x.typeName).ToHashSet();
            topTypes = topTypes.Where(t => topSet.Contains(t.Name)).ToList();

            var harmony = new Harmony("REPOFidelity.CostProbe.ScriptTiming");
            var prefixInfo  = typeof(ScriptTiming).GetMethod(nameof(ScriptTiming.Prefix),  BindingFlags.Static | BindingFlags.Public);
            var postfixInfo = typeof(ScriptTiming).GetMethod(nameof(ScriptTiming.Postfix), BindingFlags.Static | BindingFlags.Public);
            var prefix = new HarmonyMethod(prefixInfo);
            var postfix = new HarmonyMethod(postfixInfo);

            ScriptTiming.Clear();
            var patched = new List<MethodInfo>();
            foreach (var type in topTypes)
                PatchIfPresent(type, "Update",      harmony, prefix, postfix, patched);
            foreach (var type in topTypes)
                PatchIfPresent(type, "LateUpdate",  harmony, prefix, postfix, patched);
            foreach (var type in topTypes)
                PatchIfPresent(type, "FixedUpdate", harmony, prefix, postfix, patched);

            yield return new WaitForSeconds(WarmupSeconds);

            ScriptTiming.Reset();
            int scriptFrames = 0;
            float scriptElapsed = 0f;
            while (scriptElapsed < ScriptSampleSeconds)
            {
                scriptElapsed += Time.unscaledDeltaTime;
                scriptFrames++;
                Progress = 0.28f + 0.09f * (scriptElapsed / ScriptSampleSeconds);
                yield return null;
            }

            foreach (var method in patched)
            {
                try { harmony.Unpatch(method, HarmonyPatchType.Prefix,  harmony.Id); } catch { }
                try { harmony.Unpatch(method, HarmonyPatchType.Postfix, harmony.Id); } catch { }
            }

            double tickToMs = 1000.0 / Stopwatch.Frequency;
            int fn = Mathf.Max(1, scriptFrames);
            foreach (var method in patched)
            {
                var (ticks, calls) = ScriptTiming.Read(method);
                if (calls == 0) continue;
                double totalMs  = ticks * tickToMs / fn;
                double perCallUs = calls == 0 ? 0 : (ticks * 1_000_000.0 / Stopwatch.Frequency) / calls;
                string label = method.DeclaringType!.Name + "." + method.Name;
                scriptTimings.Add((label, totalMs, perCallUs, calls / fn));
            }
            scriptTimings.Sort((a, b) => b.totalMs.CompareTo(a.totalMs));
            ScriptTiming.Clear();
        }

        report.AppendLine($"== Script cost per type (Harmony-instrumented, {ScriptSampleSeconds:F0}s, top {ScriptTopN} types) ==");
        if (scriptTimings.Count == 0)
            report.AppendLine("  (no script methods captured)");
        else
        {
            report.AppendLine("  ms/frame   calls/f   µs/call   method");
            foreach (var (label, totalMs, perCallUs, callsPerFrame) in scriptTimings)
                report.AppendLine($"  {totalMs,7:F3}    {callsPerFrame,5}    {perCallUs,7:F2}   {label}");
            double totalScriptMs = scriptTimings.Sum(x => x.totalMs);
            report.AppendLine($"  ──────");
            report.AppendLine($"  {totalScriptMs,7:F3}   (sum of instrumented methods)");
        }
        report.AppendLine();

        // ---- Upscaler + Potato + Vanilla sweep ----
        // Normalize to a reference config (Ultra + DLAA + fog 1.0×) so individual
        // sweep cells are comparable across users / lobbies / builds. The baseline
        // above was measured at the user's actual settings; the sweep below is the
        // apples-to-apples comparison. Uncap the framerate so sweep cells reflect
        // raw hardware cost rather than collapsing onto the user's FPS cap.
        _savedVSyncCount = QualitySettings.vSyncCount;
        _savedTargetFrameRate = Application.targetFrameRate;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        _frameLimitUncapped = true;

        // Sweep cells mutate individual settings (UpscaleModeSetting, fog, etc.)
        // which normally drops Preset to Custom. Suppress that so the user's
        // Auto / Ultra / etc. preset survives the probe.
        Settings.PushPresetRevertSuppression();
        _presetRevertSuppressed = true;

        Status = "Sweep: Auto / DLSS / FSR / Off / Potato / Vanilla";
        report.AppendLine($"== Sweep (Auto = autotune's picks; rest normalized Ultra + DLAA + fog 1.0×, VSync off + uncapped, {UpscalerSampleSeconds}s each) ==");
        var upscalers = new List<UpscaleMode>();
        if (GPUDetector.IsUpscalerSupported(UpscaleMode.DLSS))         upscalers.Add(UpscaleMode.DLSS);
        if (GPUDetector.IsUpscalerSupported(UpscaleMode.FSR_Temporal)) upscalers.Add(UpscaleMode.FSR_Temporal);
        upscalers.Add(UpscaleMode.Off);

        var presetLadder = new[]
        {
            QualityPreset.Potato,
            QualityPreset.Low,
            QualityPreset.Medium,
            QualityPreset.High,
            QualityPreset.Ultra,
        };
        var fogPasses = new[] { 1.1f, 0.3f };

        var sweepResults = new List<(string label, float ms, float shadowD, float lightD, bool isActive)>();
        int totalCells = 1 + upscalers.Count + presetLadder.Length * fogPasses.Length + 1;
        int cellIdx = 0;
        void SweepProgress()
        {
            cellIdx++;
            Progress = 0.38f + 0.60f * ((float)cellIdx / totalCells);
        }

        // Wall-clock interpolation so the bar animates between cell completions
        // instead of snapping every ~4.5s. Duration includes settle + sample per
        // cell. Update() picks this up and lerps Progress.
        _sweepStartProgress = 0.38f;
        _sweepEndProgress = 0.98f;
        _sweepExpectedDuration = totalCells * (SettleSeconds + UpscalerSampleSeconds);
        _sweepStartTime = Time.unscaledTime;
        _sweepSmoothActive = true;

        // Auto cell uses autotune's actual resolved config, not the Ultra+DLAA
        // normalization the rest of the sweep runs against — so users see the
        // Auto frametime directly instead of interpolating between discrete presets.
        Status = "Sweep: Auto (autotune's picks)";
        Settings.ApplyPreset(QualityPreset.Auto);
        Patches.QualityPatch.ApplyFogAndDrawDistance();
        Patches.SceneOptimizer.Apply();
        Patches.QualityPatch.ApplyQualitySettings();
        yield return new WaitForSeconds(SettleSeconds);
        Sample autoSample = default;
        yield return SampleFrames(UpscalerSampleSeconds, v => autoSample = v);
        sweepResults.Add(("Auto", autoSample.AvgMs,
            Settings.ResolvedShadowDistance, Settings.ResolvedLightDistance,
            origPreset == QualityPreset.Auto));
        SweepProgress();

        // Normalize for the remaining cells — Ultra + DLAA + fog 1.0×.
        Settings.ApplyPreset(QualityPreset.Ultra);
        Settings.ResolvedUpscaleMode = UpscaleMode.DLAA;
        Settings.ResolvedRenderScale = 100;
        Settings.ResolvedFogMultiplier = 1.0f;
        Patches.QualityPatch.ApplyFogAndDrawDistance();
        Patches.SceneOptimizer.Apply();
        Patches.QualityPatch.ApplyQualitySettings();
        yield return new WaitForSeconds(SettleSeconds);

        foreach (var mode in upscalers)
        {
            Status = $"Sweep: {mode}";
            Settings.UpscaleModeSetting = mode;
            yield return new WaitForSeconds(SettleSeconds);
            Sample s = default;
            yield return SampleFrames(UpscalerSampleSeconds, v => s = v);
            sweepResults.Add(($"{mode}", s.AvgMs, Settings.ResolvedShadowDistance, Settings.ResolvedLightDistance,
                mode == origUpscaler && origPreset == Settings.Preset));
            SweepProgress();
        }

        // Preset × fog matrix — force fog to 1.1x (loosest allowed) and 0.3x (tightest),
        // walk all 5 presets at each. This shows frame-time-per-quality-tier AND the
        // fog-driven shadow/light clamp cascading through each preset.
        Sample potatoSample = default;
        foreach (var fog in fogPasses)
        {
            foreach (var p in presetLadder)
            {
                Status = $"Sweep: {p} @ fog {fog:F1}x";
                // ApplyPreset directly — bypasses file-save event storm, sets Resolved values from preset.
                Settings.ApplyPreset(p);
                // Override fog after ApplyPreset (which hardcodes its own fog per preset).
                Settings.ResolvedFogMultiplier = fog;
                // Push fog to RenderSettings + update ResolvedEffectiveFogEnd + re-clamp shadow/light.
                Patches.QualityPatch.ApplyFogAndDrawDistance();
                Patches.SceneOptimizer.Apply();
                Patches.QualityPatch.ApplyQualitySettings();
                yield return new WaitForSeconds(SettleSeconds);
                Sample ps = default;
                yield return SampleFrames(UpscalerSampleSeconds, v => ps = v);
                sweepResults.Add(($"{p}@{fog:F1}x", ps.AvgMs,
                    Settings.ResolvedShadowDistance, Settings.ResolvedLightDistance, false));
                if (p == QualityPreset.Potato && Mathf.Approximately(fog, 1.1f)) potatoSample = ps;
                SweepProgress();
            }
        }

        // Vanilla step — flip mod completely off. Mirror the F10 disable flow so
        // QualitySettings (shadow distance, LOD, etc.) genuinely reset to vanilla
        // instead of inheriting the previous matrix cell's aggressive values.
        Status = "Sweep: Vanilla (mod OFF)";
        Settings.ModEnabled = false;
        Patches.QualityPatch.RestoreVanillaQuality();
        Patches.SceneOptimizer.Apply();
        yield return new WaitForSeconds(SettleSeconds);
        Sample vanillaSample = default;
        yield return SampleFrames(UpscalerSampleSeconds, v => vanillaSample = v);
        sweepResults.Add(("Vanilla (F10)", vanillaSample.AvgMs, QualitySettings.shadowDistance, 0f, false));
        SweepProgress();

        // Restore — mirrors OptimizerBenchmark exit path. Re-apply mod's values on top
        // of the vanilla QualitySettings so we end exactly where we started.
        Settings.ModEnabled = origModEnabled;
        Settings.Preset = origPreset;
        Settings.UpscaleModeSetting = origUpscaler;
        Settings.FogDistanceMultiplier = origFog;
        Patches.SceneOptimizer.Apply();
        Patches.QualityPatch.ApplyQualitySettings();
        Patches.QualityPatch.ApplyFogAndDrawDistance();
        RestoreFrameLimit();
        RestorePresetRevertSuppression();
        _sweepSmoothActive = false;
        yield return new WaitForSeconds(SettleSeconds);

        float bestMs = sweepResults.Min(r => r.ms);
        foreach (var (label, ms, shadowD, lightD, isActive) in sweepResults)
        {
            string tag = isActive ? " (active)" : "";
            string delta = Mathf.Approximately(ms, bestMs) ? " ← fastest" : $"  {ms - bestMs:+0.00;-0.00}";
            string range = lightD > 0f
                ? $"  shadowD={shadowD:F0}m lightD={lightD:F0}m"
                : $"  shadowD={shadowD:F0}m";
            report.AppendLine($"  {label,-16} {ms,6:F2} ms{range}{tag}{delta}");
        }
        float modOverhead = sweepResults.Where(r => r.label != "Vanilla (F10)").Min(r => r.ms) - vanillaSample.AvgMs;
        float potatoVsVanilla = potatoSample.AvgMs - vanillaSample.AvgMs;
        report.AppendLine($"  Mod overhead vs vanilla (best mod mode): {modOverhead:+0.00;-0.00} ms");
        report.AppendLine($"  Potato vs vanilla:                       {potatoVsVanilla:+0.00;-0.00} ms   (negative = Potato wins)");
        report.AppendLine();

        // ---- Opportunities ----
        report.AppendLine("== Optimization opportunities (ranked by likely impact) ==");
        BuildOpportunities(report, timeStats, countStats, perFrameMem, scene, scriptTimings);
        report.AppendLine();

        // ---- Config dump ----
        AppendConfigFiles(report);

        var text = report.ToString();
        bool clipboardOk = false;
        try
        {
            File.AppendAllText(OutputPath, text);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Cost probe save failed: {ex.Message}"); }

        try
        {
            clipboardOk = TrySystemClipboard(text);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Clipboard copy failed: {ex.Message}"); }

        Plugin.Log.LogInfo(clipboardOk
            ? $"Cost probe: appended to {OutputPath} (copied to clipboard)"
            : $"Cost probe: appended to {OutputPath} (clipboard unavailable — read the file)");

        Cursor.lockState = savedLockState;
        Cursor.visible   = savedVisible;

        Status = clipboardOk ? "Done (clipboard)" : "Done (file only)";
        Progress = 1f;
        Running = false;
        yield return new WaitForSeconds(5f);
        Status = "";
    }

    private static bool TrySystemClipboard(string text)
    {
        GUIUtility.systemCopyBuffer = text;
        if (GUIUtility.systemCopyBuffer == text) return true;
        if (Application.platform == RuntimePlatform.LinuxPlayer)
            return TryLinuxClipboardFallback(text);
        return false;
    }

    private static bool TryLinuxClipboardFallback(string text)
    {
        var tools = new[]
        {
            ("wl-copy", ""),
            ("xclip", "-selection clipboard"),
            ("xsel", "--clipboard --input"),
        };
        foreach (var (cmd, args) in tools)
        {
            try
            {
                var psi = new ProcessStartInfo(cmd, args)
                {
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                p.StandardInput.Write(text);
                p.StandardInput.Close();
                if (p.WaitForExit(2000) && p.ExitCode == 0) return true;
            }
            catch { }
        }
        return false;
    }

    // Harmony-driven timing for the per-script instrumentation pass.
    // Main-thread only, no locking. Register() gates Postfix so unrelated
    // Harmony traffic doesn't hit the dictionary.
    internal static class ScriptTiming
    {
        internal class Acc { public long Ticks; public int Calls; }
        private static readonly Dictionary<MethodBase, Acc> _accs = new();

        internal static void Clear() => _accs.Clear();

        internal static void Reset()
        {
            foreach (var a in _accs.Values) { a.Ticks = 0; a.Calls = 0; }
        }

        internal static void Register(MethodInfo m)
        {
            if (!_accs.ContainsKey(m)) _accs[m] = new Acc();
        }

        internal static (long ticks, int calls) Read(MethodInfo m)
        {
            if (_accs.TryGetValue(m, out var a)) return (a.Ticks, a.Calls);
            return (0, 0);
        }

        public static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        public static void Postfix(MethodBase __originalMethod, long __state)
        {
            if (!_accs.TryGetValue(__originalMethod, out var a)) return;
            a.Ticks += Stopwatch.GetTimestamp() - __state;
            a.Calls++;
        }
    }

    private static void PatchIfPresent(Type type, string methodName, Harmony harmony,
        HarmonyMethod prefix, HarmonyMethod postfix, List<MethodInfo> patched)
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public |
                               BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var method = type.GetMethod(methodName, F, null, Type.EmptyTypes, null);
        if (method == null) return;
        try
        {
            ScriptTiming.Register(method);
            harmony.Patch(method, prefix, postfix);
            patched.Add(method);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Probe: can't patch {type.Name}.{methodName}: {ex.Message}");
        }
    }

    // Breaks down what players are costing: per-avatar distance + renderer
    // count, flashlight budget state (within / culled / past-fog), and the
    // throttle-eligible cosmetic component counts.
    private static void AppendMultiplayerSection(StringBuilder report)
    {
        var avatars = UnityEngine.Object.FindObjectsOfType<PlayerAvatar>();
        if (avatars.Length == 0) return;

        var cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        float fogEnd = Settings.ResolvedEffectiveFogEnd;
        float fogCutoff = fogEnd > 0f ? fogEnd * 1.1f : float.PositiveInfinity;

        report.AppendLine($"== Multiplayer / per-player (fog cutoff {fogCutoff:F0}m) ==");
        report.AppendLine($"  PlayerAvatars: {avatars.Length}");
        int pastFog = 0, shadowRenderers = 0;
        foreach (var a in avatars)
        {
            if (a == null) continue;
            float dist = cam != null ? Vector3.Distance(a.transform.position, camPos) : 0f;
            bool beyond = dist > fogCutoff;
            if (beyond) pastFog++;
            int casters = 0;
            foreach (var r in a.GetComponentsInChildren<Renderer>(true))
                if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) casters++;
            shadowRenderers += casters;
            string tag = a.isLocal ? "local" : "remote";
            string gate = beyond ? " past-fog" : "";
            report.AppendLine($"    [{tag,-6}] {a.playerName ?? "(unknown)",-20} {dist,5:F0}m  {casters,3} casters{gate}");
        }
        report.AppendLine($"  Total shadow-casting player renderers: {shadowRenderers}  ({pastFog} players past fog)");

        var flashlights = UnityEngine.Object.FindObjectsOfType<FlashlightController>();
        if (flashlights.Length > 0)
        {
            var sorted = new List<(FlashlightController fl, float dist)>();
            foreach (var fl in flashlights)
            {
                if (fl.spotlight == null) continue;
                float d = cam != null ? Vector3.Distance(fl.spotlight.transform.position, camPos) : 0f;
                sorted.Add((fl, d));
            }
            sorted.Sort((a, b) => a.dist.CompareTo(b.dist));
            const int budgetCap = 4;
            int active = 0, overBudget = 0, flashPastFog = 0;
            foreach (var (fl, d) in sorted)
            {
                if (d > fogCutoff) flashPastFog++;
                else if (active < budgetCap) active++;
                else overBudget++;
            }
            report.AppendLine($"  Flashlights: {sorted.Count} total — {active} within budget, {overBudget} culled over budget, {flashPastFog} past fog");
        }

        int eyelidsTotal = UnityEngine.Object.FindObjectsOfType<PlayerAvatarEyelids>().Length;
        int expressionsTotal = UnityEngine.Object.FindObjectsOfType<PlayerExpression>().Length;
        int overchargeTotal = UnityEngine.Object.FindObjectsOfType<PlayerAvatarOverchargeVisuals>().Length;
        report.AppendLine($"  Cosmetic components: Eyelids×{eyelidsTotal}  Expression×{expressionsTotal}  Overcharge×{overchargeTotal}");
        report.AppendLine($"  (components past fog cutoff skip their Update — see per-player distances above)");
        report.AppendLine();
    }

    private static double LookupMs(List<(string name, string cat, double ms)> list, string marker)
    {
        foreach (var (n, _, v) in list) if (n == marker) return v;
        return 0;
    }

    private static double LookupCount(List<(string name, string cat, double n)> list, string marker)
    {
        foreach (var (n, _, v) in list) if (n == marker) return v;
        return 0;
    }

    private static double LookupBytes(List<(string name, string cat, double bytes)> list, string marker)
    {
        foreach (var (n, _, v) in list) if (n == marker) return v;
        return 0;
    }

    private static string FmtBig(double n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000:F2}M";
        if (n >= 1_000)     return $"{n / 1_000:F1}K";
        return $"{n:F0}";
    }

    private static string FmtBytes(double b)
    {
        if (b >= 1_048_576) return $"{b / 1_048_576:F2} MB";
        if (b >= 1024)      return $"{b / 1024:F1} KB";
        return $"{b:F0} B";
    }

    private static string Pct(int n, int total) => total > 0 ? $"{100.0 * n / total:F0}%" : "-";

    private struct Sample
    {
        public float AvgFps, AvgMs, P1Low, P01Low;
        public int FrameCount;
    }

    private IEnumerator SampleFrames(float seconds, Action<Sample> onDone)
    {
        yield return new WaitForSeconds(WarmupSeconds);

        var frames = new List<float>(512);
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            float dt = Time.unscaledDeltaTime;
            frames.Add(dt);
            elapsed += dt;
            yield return null;
        }
        onDone(ComputeSample(frames));
    }

    private static Sample ComputeSample(List<float> frames)
    {
        if (frames.Count == 0) return default;
        float sum = 0f;
        for (int i = 0; i < frames.Count; i++) sum += frames[i];
        float avgMs = sum / frames.Count * 1000f;
        frames.Sort();
        return new Sample
        {
            AvgFps = 1000f / avgMs,
            AvgMs = avgMs,
            P1Low = PercentileLow(frames, 0.01f),
            P01Low = PercentileLow(frames, 0.001f),
            FrameCount = frames.Count
        };
    }

    private static float PercentileLow(List<float> sorted, float percentile)
    {
        int count = Mathf.Max(1, Mathf.CeilToInt(sorted.Count * percentile));
        float worst = 0f;
        for (int i = sorted.Count - 1; i >= sorted.Count - count; i--)
            worst += sorted[i];
        return 1f / (worst / count);
    }

    private struct RendererBreakdown
    {
        public int Total, Visible, ShadowCasting, ShadowCastingHidden, NoLODGroup;
    }

    private struct LightBreakdown
    {
        public int Active, Shadowing, Directional, PointShadow, SpotShadow;
    }

    private struct LightInfo
    {
        public string Name, Type, ShadowMode;
        public float Range;
        public int Resolution;
        public float CostScore;
    }

    private struct SceneMetrics
    {
        public RendererBreakdown Renderers;
        public LightBreakdown Lights;
        public long TotalSceneTris;
        public int SkinnedRenderers;
        public int TotalParticles, ActiveParticles;
        public int ActiveParticleCount;
        public int ReflectionProbes, TrailRenderers, LineRenderers;
        public int AudioSources, AudioSourcesPlaying;
        public int UpdatingBehaviours, LateUpdatingBehaviours, FixedUpdatingBehaviours;
        public List<LightInfo> LightDetail;
        public List<(string typeName, int count)> TopBehaviourTypes;
    }

    // Cache reflection lookups per Type — the scene has many instances of each MB.
    private static readonly Dictionary<Type, (bool upd, bool late, bool fixedU)> _methodCache = new();

    private static (bool upd, bool late, bool fixedU) GetMethodFlags(Type t)
    {
        if (_methodCache.TryGetValue(t, out var cached)) return cached;
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        bool upd = false, late = false, fixedU = false;
        var cur = t;
        while (cur != null && cur != typeof(MonoBehaviour) && cur != typeof(Behaviour) && cur != typeof(object))
        {
            if (!upd    && cur.GetMethod("Update",      F, null, Type.EmptyTypes, null) != null) upd = true;
            if (!late   && cur.GetMethod("LateUpdate",  F, null, Type.EmptyTypes, null) != null) late = true;
            if (!fixedU && cur.GetMethod("FixedUpdate", F, null, Type.EmptyTypes, null) != null) fixedU = true;
            if (upd && late && fixedU) break;
            cur = cur.BaseType;
        }
        var result = (upd, late, fixedU);
        _methodCache[t] = result;
        return result;
    }

    private static SceneMetrics GatherSceneMetrics()
    {
        var m = new SceneMetrics
        {
            LightDetail = new List<LightInfo>(),
            TopBehaviourTypes = new List<(string, int)>()
        };

        foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
        {
            m.Renderers.Total++;
            bool visible = r.isVisible;
            bool shadow  = r.shadowCastingMode != ShadowCastingMode.Off;
            if (visible)            m.Renderers.Visible++;
            if (shadow)             m.Renderers.ShadowCasting++;
            if (shadow && !visible) m.Renderers.ShadowCastingHidden++;
            if (r.GetComponentInParent<LODGroup>() == null) m.Renderers.NoLODGroup++;

            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                // GetIndexCount reads GPU metadata; .triangles needs isReadable=true
                // on the mesh, which game assets don't ship with — would throw a
                // Unity error per renderer otherwise.
                var mesh = mf.sharedMesh;
                long indices = 0;
                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                    indices += (long)mesh.GetIndexCount(sm);
                m.TotalSceneTris += indices / 3;
            }
        }

        m.SkinnedRenderers = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>().Length;

        foreach (var ps in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
        {
            m.TotalParticles++;
            if (ps.isPlaying && ps.particleCount > 0)
            {
                m.ActiveParticles++;
                m.ActiveParticleCount += ps.particleCount;
            }
        }

        m.ReflectionProbes = UnityEngine.Object.FindObjectsOfType<ReflectionProbe>().Length;
        m.TrailRenderers   = UnityEngine.Object.FindObjectsOfType<TrailRenderer>().Length;
        m.LineRenderers    = UnityEngine.Object.FindObjectsOfType<LineRenderer>().Length;

        foreach (var a in UnityEngine.Object.FindObjectsOfType<AudioSource>())
        {
            m.AudioSources++;
            if (a.isPlaying) m.AudioSourcesPlaying++;
        }

        foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>())
        {
            if (!light.enabled || !light.gameObject.activeInHierarchy) continue;
            m.Lights.Active++;
            bool shadows = light.shadows != LightShadows.None;
            if (shadows) m.Lights.Shadowing++;
            switch (light.type)
            {
                case LightType.Directional: if (shadows) m.Lights.Directional++; break;
                case LightType.Point:       if (shadows) m.Lights.PointShadow++; break;
                case LightType.Spot:        if (shadows) m.Lights.SpotShadow++; break;
            }

            int res = light.shadowCustomResolution;
            if (res <= 0)
            {
                switch (light.shadowResolution)
                {
                    case LightShadowResolution.VeryHigh: res = 4096; break;
                    case LightShadowResolution.High:     res = 2048; break;
                    case LightShadowResolution.Medium:   res = 1024; break;
                    case LightShadowResolution.Low:      res = 512;  break;
                    default:                             res = 1024; break;
                }
            }

            float faces = light.type == LightType.Point ? 6f
                        : light.type == LightType.Directional ? QualitySettings.shadowCascades : 1f;
            float rangeScore = light.type == LightType.Directional ? 2500f : (light.range * light.range);
            float cost = shadows ? faces * (res / 1024f) * (res / 1024f) * (rangeScore / 100f) : 0f;

            m.LightDetail.Add(new LightInfo
            {
                Name       = light.name,
                Type       = light.type.ToString(),
                Range      = light.range,
                Resolution = res,
                ShadowMode = shadows ? light.shadows.ToString() : "Off",
                CostScore  = cost
            });
        }

        var behaviourCounts = new Dictionary<Type, int>();
        foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
        {
            if (!mb.isActiveAndEnabled) continue;
            var t = mb.GetType();
            var (upd, late, fixedU) = GetMethodFlags(t);
            if (upd)    m.UpdatingBehaviours++;
            if (late)   m.LateUpdatingBehaviours++;
            if (fixedU) m.FixedUpdatingBehaviours++;
            if (upd)
            {
                if (!behaviourCounts.TryGetValue(t, out var c)) c = 0;
                behaviourCounts[t] = c + 1;
            }
        }

        foreach (var kv in behaviourCounts.OrderByDescending(k => k.Value))
            m.TopBehaviourTypes.Add((kv.Key.Name, kv.Value));

        return m;
    }

    private static void BuildOpportunities(
        StringBuilder r,
        List<(string name, string cat, double ms)> timeStats,
        List<(string name, string cat, double n)> countStats,
        List<(string name, string cat, double bytes)> memStats,
        SceneMetrics scene,
        List<(string label, double totalMs, double perCallUs, int calls)> scriptTimings)
    {
        var ideas = new List<(double priority, string text)>();

        // Coarse markers — may or may not be exposed on retail Unity.
        double physics   = LookupMs(timeStats, "Physics.Simulate") + LookupMs(timeStats, "FixedUpdate.PhysicsFixedUpdate");
        double animation = LookupMs(timeStats, "Animators.Update");
        double particles = LookupMs(timeStats, "Particles.Update");
        double renderImg = LookupMs(timeStats, "Camera.ImageEffects");
        double gcAlloc   = LookupBytes(memStats, "GC Allocated In Frame");

        // Top-3 script offenders from Harmony timing — concrete names, not guesses.
        int topShown = 0;
        foreach (var (label, totalMs, perCallUs, calls) in scriptTimings)
        {
            if (totalMs < 0.05 || topShown >= 3) break;
            ideas.Add((totalMs * 200,
                $"Top script cost: {label} — {totalMs:F3} ms/frame ({calls} calls at {perCallUs:F1} µs each). " +
                "Rate-limit, cache, or Harmony-prefix-skip when state hasn't changed."));
            topShown++;
        }

        if (scene.Renderers.ShadowCastingHidden > 0 && scene.Renderers.Total > 0)
        {
            double ratio = (double)scene.Renderers.ShadowCastingHidden / scene.Renderers.Total;
            ideas.Add((ratio * 300,
                $"{scene.Renderers.ShadowCastingHidden} off-screen shadow casters ({ratio * 100:F0}% of scene renderers). " +
                "Prefabs that don't need to cast shadows should set shadowCastingMode=Off — cost isn't directly visible in retail markers but cubemap passes are real."));
        }
        if (scene.Renderers.NoLODGroup > 300)
            ideas.Add((scene.Renderers.NoLODGroup / 4.0,
                $"{scene.Renderers.NoLODGroup} renderers have no LODGroup ({Pct(scene.Renderers.NoLODGroup, scene.Renderers.Total)}). Distant small objects render at full detail — most impactful when main-cam render cost is high."));
        if (scene.Lights.PointShadow > 8)
            ideas.Add((scene.Lights.PointShadow * 10,
                $"{scene.Lights.PointShadow} point lights with shadows × 6 faces = {scene.Lights.PointShadow * 6} cubemap passes/frame. Shadow budget + resolution tiering are the highest-leverage knobs."));
        if (physics > 0.3)
            ideas.Add((physics * 70,
                $"Physics costs {physics:F2} ms/frame. Trim layer collision matrix, raise fixed timestep interval, switch non-dynamic rigidbodies to kinematic."));
        if (animation > 0.2)
            ideas.Add((animation * 60,
                $"Animators.Update costs {animation:F2} ms/frame across {scene.SkinnedRenderers} skinned renderers. Set Animator.cullingMode=BasedOnRenderers / CullCompletely on distant characters."));
        if (particles > 0.2)
            ideas.Add((particles * 60,
                $"Particles.Update costs {particles:F2} ms/frame across {scene.ActiveParticles} active systems ({FmtBig(scene.ActiveParticleCount)} particles alive). Distance-cull emit rate."));
        if (renderImg > 0.3)
            ideas.Add((renderImg * 50,
                $"Camera.ImageEffects (post-processing stack) costs {renderImg:F2} ms/frame. Audit which OnRenderImage passes are doing work."));
        if (gcAlloc > 1024)
            ideas.Add((gcAlloc / 64.0,
                $"GC allocates {FmtBytes(gcAlloc)}/frame — hunt per-frame boxing, closures, and ToArray/ToList calls."));

        double drawCalls = LookupCount(countStats, "Draw Calls Count");
        if (drawCalls > 1500)
            ideas.Add((drawCalls / 40.0,
                $"{drawCalls:F0} draw calls / frame. Static batching or GPU instancing on recurring prefabs would cut CPU cost."));

        if (scene.ReflectionProbes > 1)
            ideas.Add((scene.ReflectionProbes * 3,
                $"{scene.ReflectionProbes} reflection probes — each realtime probe updating adds CPU+GPU cost. Mark as baked or type=Custom when possible."));

        if (ideas.Count == 0)
        {
            r.AppendLine("  (no high-cost categories detected — baseline looks clean)");
            return;
        }

        ideas.Sort((a, b) => b.priority.CompareTo(a.priority));
        int i = 1;
        foreach (var (_, text) in ideas)
            r.AppendLine($"  {i++}. {text}");
    }
}

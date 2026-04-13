using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace REPOFidelity;

// F12 benchmark — 2-pass A/B: vanilla vs GPU/GC only vs all optimizations
internal class OptimizerBenchmark : MonoBehaviour
{
    internal static OptimizerBenchmark? Instance { get; private set; }
    internal static bool Running { get; private set; }
    internal static string Status = "";
    internal static float Progress;

    private const float WarmupSeconds = 3f;
    private const float MeasureSeconds = 15f;
    private const int Passes = 2;

    private static bool _savedModEnabled;
    private static int _savedCpuMode;

    internal static void Launch()
    {
        if (Running) return;
        if (Instance == null)
        {
            var go = new GameObject("REPOFidelity_OptimizerBenchmark");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<OptimizerBenchmark>();
        }
        Instance.StartCoroutine(Instance.RunSafe());
    }

    internal static void Abort()
    {
        if (!Running) return;
        if (Instance != null) Instance.StopAllCoroutines();
        Restore();
        Running = false;
        Status = "Aborted";
    }

    private static void Save()
    {
        _savedModEnabled = Settings.ModEnabled;
        _savedCpuMode = Settings.CpuPatchMode;
    }

    private static void Restore()
    {
        Settings.ModEnabled = _savedModEnabled;
        Settings.CpuPatchMode = _savedCpuMode;
        Settings.UpdateCpuGate();
    }

    private static void ForceCpu(bool on)
    {
        Settings.CpuPatchMode = on ? 1 : 0;
        Settings.UpdateCpuGate();
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
                Plugin.Log.LogError($"Benchmark failed: {ex}");
                Restore();
                Running = false;
                Status = "ERROR";
                yield break;
            }
            if (!next) yield break;
            yield return inner.Current;
        }
    }

    private IEnumerator Run()
    {
        Running = true;
        Save();

        Plugin.Log.LogInfo($"=== FIDELITY BENCHMARK ({Passes}x {MeasureSeconds}s) ===");

        var vanillaAccum = new Accum();
        var gpuGcAccum = new Accum();
        var allOnAccum = new Accum();

        // 3 phases per pass: vanilla, gpu/gc, all-on
        int totalPhases = Passes * 3;
        int phase = 0;

        for (int pass = 0; pass < Passes; pass++)
        {
            string pl = $"Pass {pass + 1}/{Passes}";

            // 1. Vanilla — mod completely off
            Settings.ModEnabled = false;
            ForceCpu(false);
            Patches.SceneOptimizer.Apply();
            Glitch();
            Status = $"{pl}: Vanilla (mod OFF)";
            Progress = (float)phase / totalPhases;
            Plugin.Log.LogInfo(Status);
            yield return Settle();
            var r = new Result(); yield return Measure(r);
            vanillaAccum.Add(r); phase++;

            // 2. GPU/GC only — mod on, CPU patches forced off
            Settings.ModEnabled = true;
            ForceCpu(false);
            Patches.SceneOptimizer.Apply();
            Patches.QualityPatch.ApplyQualitySettings();
            Glitch();
            Status = $"{pl}: GPU/GC only";
            Progress = (float)phase / totalPhases;
            Plugin.Log.LogInfo(Status);
            yield return Settle();
            r = new Result(); yield return Measure(r);
            gpuGcAccum.Add(r); phase++;

            // 3. Everything on
            Settings.ModEnabled = true;
            ForceCpu(true);
            Patches.SceneOptimizer.Apply();
            Glitch();
            Status = $"{pl}: All ON";
            Progress = (float)phase / totalPhases;
            Plugin.Log.LogInfo(Status);
            yield return Settle();
            r = new Result(); yield return Measure(r);
            allOnAccum.Add(r); phase++;
        }

        Restore();
        Patches.SceneOptimizer.Apply();
        if (Settings.ModEnabled) Patches.QualityPatch.ApplyQualitySettings();
        Glitch();

        var vanilla = vanillaAccum.Compute();
        var gpuGc = gpuGcAccum.Compute();
        var allOn = allOnAccum.Compute();

        var report = BuildReport(vanilla, gpuGc, allOn);

        string path = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
            "optimizer_benchmark.txt");
        try { File.WriteAllText(path, report); }
        catch (Exception ex) { Plugin.Log.LogWarning($"Report save failed: {ex.Message}"); }

        Plugin.Log.LogInfo("\n" + report);
        Status = "Done! optimizer_benchmark.txt";
        Progress = 1f;
        Running = false;
        yield return new WaitForSeconds(5f);
        Status = "";
    }

    private static string BuildReport(Result vanilla, Result gpuGc, Result allOn)
    {
        var sb = new StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
        sb.AppendLine("║            REPO FIDELITY — FULL BENCHMARK REPORT            ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine($"  Date:       {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  GPU:        {SystemInfo.graphicsDeviceName}");
        sb.AppendLine($"  VRAM:       {SystemInfo.graphicsMemorySize}MB");
        sb.AppendLine($"  API:        {SystemInfo.graphicsDeviceType}");
        sb.AppendLine($"  CPU:        {SystemInfo.processorType} ({SystemInfo.processorCount} threads)");
        sb.AppendLine($"  RAM:        {SystemInfo.systemMemorySize}MB");
        sb.AppendLine($"  Platform:   {Application.platform}");
        sb.AppendLine($"  Resolution: {Screen.width}x{Screen.height}");
        sb.AppendLine($"  Passes:     {Passes} x {MeasureSeconds}s (warmup {WarmupSeconds}s)");
        sb.AppendLine($"  CPU gate:   {(Settings.CpuPatchMode == -1 ? "Auto (>8ms)" : Settings.CpuPatchMode == 1 ? "Forced ON" : "Forced OFF")}");
        sb.AppendLine();

        float gpuGcDelta = vanilla.AvgMs - gpuGc.AvgMs;
        float cpuDelta = gpuGc.AvgMs - allOn.AvgMs;
        float totalDelta = vanilla.AvgMs - allOn.AvgMs;
        float totalFps = allOn.AvgFps - vanilla.AvgFps;
        float totalPct = vanilla.AvgMs > 0 ? totalDelta / vanilla.AvgMs * 100f : 0f;

        sb.AppendLine("──────────────────────────────────────────────────────────────");
        sb.AppendLine("  RESULTS (averaged across passes)");
        sb.AppendLine("──────────────────────────────────────────────────────────────");
        sb.AppendLine($"  Vanilla (mod OFF):   {vanilla.AvgFps,6:F1} FPS  {vanilla.AvgMs,7:F2}ms  1%low: {vanilla.P1Low,5:F1}  N={vanilla.Frames.Count}");
        sb.AppendLine($"  GPU/GC only:         {gpuGc.AvgFps,6:F1} FPS  {gpuGc.AvgMs,7:F2}ms  1%low: {gpuGc.P1Low,5:F1}  N={gpuGc.Frames.Count}");
        sb.AppendLine($"  All ON:              {allOn.AvgFps,6:F1} FPS  {allOn.AvgMs,7:F2}ms  1%low: {allOn.P1Low,5:F1}  N={allOn.Frames.Count}");
        sb.AppendLine();
        sb.AppendLine($"  GPU/GC savings:      {gpuGcDelta:+0.000;-0.000}ms  ({vanilla.AvgFps:F1} -> {gpuGc.AvgFps:F1} FPS)");
        sb.AppendLine($"  CPU patch savings:   {cpuDelta:+0.000;-0.000}ms  ({gpuGc.AvgFps:F1} -> {allOn.AvgFps:F1} FPS)");
        sb.AppendLine($"  Total improvement:   {totalDelta:+0.000;-0.000}ms  ({totalFps:+0.0;-0.0} FPS, {totalPct:+0.0;-0.0}%)");
        sb.AppendLine();

        sb.AppendLine("──────────────────────────────────────────────────────────────");
        sb.AppendLine("  OPTIMIZATIONS INCLUDED");
        sb.AppendLine("──────────────────────────────────────────────────────────────");
        sb.AppendLine("  GPU/GC:");
        sb.AppendLine("    GPU instancing, shadow culling, layer distance culling");
        sb.AppendLine("    GrabberComponentCache, RayCheck/ForceGrab NonAlloc");
        sb.AppendLine("    Quality overrides (shadows, LOD, AF, lights, fog)");
        sb.AppendLine("  CPU (auto-enabled when frame time > 8ms):");
        sb.AppendLine("    EnemyDirector throttle, RoomVolume NonAlloc");
        sb.AppendLine("    SemiFunc cache, PhysGrab fix, LightManager batch");
        sb.AppendLine();

        return sb.ToString();
    }

    static void Glitch()
    {
        var g = CameraGlitch.Instance;
        if (g == null) return;
        if (g.ActiveParent != null) g.ActiveParent.SetActive(true);
        g.PlayShort();
    }

    private IEnumerator Settle()
    {
        for (int i = 0; i < 5; i++) yield return null;
        yield return new WaitForSeconds(WarmupSeconds);
    }

    private IEnumerator Measure(Result result)
    {
        float elapsed = 0f;
        while (elapsed < MeasureSeconds)
        {
            result.Frames.Add(Time.unscaledDeltaTime);
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }
        result.Compute();
    }

    private class Result
    {
        public float AvgMs, AvgFps, P1Low;
        public readonly List<float> Frames = new();
        public void Compute()
        {
            if (Frames.Count == 0) return;
            float sum = 0f;
            for (int i = 0; i < Frames.Count; i++) sum += Frames[i];
            AvgMs = sum / Frames.Count * 1000f;
            AvgFps = 1000f / AvgMs;
            Frames.Sort();
            int p1 = Mathf.Max(1, Mathf.CeilToInt(Frames.Count * 0.01f));
            float ws = 0f;
            for (int i = Frames.Count - 1; i >= Frames.Count - p1; i--) ws += Frames[i];
            P1Low = 1f / (ws / p1);
        }
    }

    private class Accum
    {
        private readonly List<float> _all = new();
        public void Add(Result r) => _all.AddRange(r.Frames);
        public Result Compute()
        {
            var r = new Result();
            r.Frames.AddRange(_all);
            r.Compute();
            return r;
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace REPOFidelity;

// F7-triggered diagnostic — measures the per-optimization frame-time impact of every
// runtime-toggleable optimization in the mod, on the same scene, back-to-back. Output
// is a clipboard-ready table with delta ms / 1% low / worst frame for each toggle.
//
// Existence is for one purpose: gathering numbers to present to the studio with the
// "Should the base game adopt this?" pitch. The doc at /mnt/d/desktop/REPOFidelity_studio_pitch.md
// has empty rows for Beast PC + Crap PC; F7 fills them.
//
// Methodology:
//   1. 5s baseline sample with current settings (whatever the user has)
//   2. For each toggleable optimization:
//      - Snapshot current state
//      - Force OFF
//      - Settle 1.5s (PerfXxx flips fire SceneOptimizer.Apply which scans the scene)
//      - Sample 5s
//      - Restore
//      - Settle 1.5s
//   3. Per-toggle delta = (sample with opt OFF) - baseline
//
// Total runtime: 5 + 10 × (1.5 + 5 + 1.5) = ~85s on the default toggle list.
internal class IndividualOptProbe : MonoBehaviour
{
    internal static IndividualOptProbe? Instance;
    internal static bool Running;
    internal static string Status = "";
    internal static float Progress;

    private const float BaselineSeconds = 5f;
    private const float SampleSeconds = 5f;
    private const float SettleSeconds = 1.5f;

    private static readonly string OutputPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
        "f7_individual_savings.txt");

    internal static void Toggle()
    {
        if (Instance == null)
        {
            var go = new GameObject("REPOFidelity.IndividualOptProbe");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<IndividualOptProbe>();
        }

        if (Running) { Abort(); return; }
        Running = true;
        Instance.StartCoroutine(Instance.RunSafe());
    }

    private static void Abort()
    {
        if (Instance != null) Instance.StopAllCoroutines();
        RestoreAll();
        Running = false;
        Status = "";
        Plugin.Log.LogInfo("F7 individual-opt probe cancelled");
    }

    // Defensive restore — called from abort / exception / natural finish.
    // Each toggle has its own restore in the Toggle struct, but if we're partway
    // through and crash, this one-shot resets everything to "as if probe never ran".
    private static void RestoreAll()
    {
        Settings.CpuPatchesProbeOverride = null;
        Settings.AllocationFixesEnabled = true;
        // Perf flags are restored by their per-toggle snapshot/restore in the loop.
        // If the loop didn't reach the restore for a flag, that flag stays where
        // the loop left it — accept that risk in exchange for not corrupting the
        // user's settings on a graceful path.
    }

    private readonly struct OptEntry
    {
        public readonly string Id;
        public readonly string Description;
        public readonly Func<int> Snapshot;
        public readonly Action ForceOff;
        public readonly Action<int> Restore;

        public OptEntry(string id, string desc, Func<int> snap, Action off, Action<int> restore)
        {
            Id = id; Description = desc; Snapshot = snap; ForceOff = off; Restore = restore;
        }
    }

    private static List<OptEntry> BuildToggles() => new()
    {
        new OptEntry("S2/S4/S5/S9/S10/S21 (CPU patches bundle)",
            "EnemyDirector throttle, RoomVolumeCheck NonAlloc, SemiFunc cache, " +
            "PhysGrabObject idle-skip, LightManager backward iter, cosmetic fog throttle",
            // Snapshot: read whatever the auto-gate decided
            () => Settings.CpuPatchesProbeOverride.HasValue ? (Settings.CpuPatchesProbeOverride.Value ? 1 : 0) : -1,
            () => Settings.CpuPatchesProbeOverride = false,
            v => Settings.CpuPatchesProbeOverride = v == -1 ? null : (bool?)(v == 1)),

        new OptEntry("S3 + S11 (allocation fixes)",
            "PhysGrabObjectGrabArea idle-skip, AudioListenerFollow NonAlloc + cached mask",
            () => Settings.AllocationFixesEnabled ? 1 : 0,
            () => Settings.AllocationFixesEnabled = false,
            v => Settings.AllocationFixesEnabled = v == 1),

        new OptEntry("A1 (explosion shadows)",
            "Disable shadows on explosion lights",
            () => Settings.PerfExplosionShadows,
            () => Settings.PerfExplosionShadows = 0,
            v => Settings.PerfExplosionShadows = v),

        new OptEntry("A2 (item light shadows)",
            "Disable shadows on handheld glow item lights",
            () => Settings.PerfItemLightShadows,
            () => Settings.PerfItemLightShadows = 0,
            v => Settings.PerfItemLightShadows = v),

        new OptEntry("A3 (animated light shadows)",
            "Disable shadows on LightAnimator-controlled lights",
            () => Settings.PerfAnimatedLightShadows,
            () => Settings.PerfAnimatedLightShadows = 0,
            v => Settings.PerfAnimatedLightShadows = v),

        new OptEntry("A4 (particle shadows)",
            "Disable shadows on particle renderers",
            () => Settings.PerfParticleShadows,
            () => Settings.PerfParticleShadows = 0,
            v => Settings.PerfParticleShadows = v),

        new OptEntry("S20 (tiny renderer culling)",
            "Disable shadows on small props (bounds < 0.5m)",
            () => Settings.PerfTinyRendererCulling,
            () => Settings.PerfTinyRendererCulling = 0,
            v => Settings.PerfTinyRendererCulling = v),

        new OptEntry("A5 (distance shadow culling)",
            "Per-frame distance-based shadow toggle for mid-bounded props",
            () => Settings.PerfDistanceShadowCulling,
            () => Settings.PerfDistanceShadowCulling = 0,
            v => Settings.PerfDistanceShadowCulling = v),

        new OptEntry("A7 (flashlight shadow budget)",
            "Cap concurrent flashlight shadow maps at N closest",
            () => Settings.PerfFlashlightShadowBudget,
            () => Settings.PerfFlashlightShadowBudget = 0,
            v => Settings.PerfFlashlightShadowBudget = v),

        new OptEntry("A6 (point light shadow culling)",
            "Disable shadows on distant point lights past fog + range",
            () => Settings.PerfPointLightShadows,
            () => Settings.PerfPointLightShadows = 0,
            v => Settings.PerfPointLightShadows = v),
    };

    private IEnumerator RunSafe()
    {
        var inner = Run();
        while (true)
        {
            bool next;
            try { next = inner.MoveNext(); }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"F7 probe failed: {ex}");
                RestoreAll();
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
        Progress = 0f;
        Status = "F7: warmup";
        Plugin.Log.LogInfo("F7 individual-opt probe starting");

        // settle a bit before we start measuring — let the user finish positioning,
        // and let any pending scene work flush
        yield return new WaitForSeconds(SettleSeconds);

        var report = new StringBuilder();
        report.AppendLine($"== REPOFidelity individual-opt savings — {DateTime.Now:yyyy-MM-dd HH:mm:ss} (v{BuildInfo.Version}) ==");
        report.AppendLine($"GPU:    {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize}MB, {SystemInfo.graphicsDeviceType})");
        report.AppendLine($"CPU:    {SystemInfo.processorType} ({SystemInfo.processorCount} cores, {SystemInfo.systemMemorySize}MB RAM)");
        report.AppendLine($"OS:     {SystemInfo.operatingSystem}");
        report.AppendLine($"Screen: {Screen.width}x{Screen.height} @ {Screen.currentResolution.refreshRateRatio.value:F0}Hz ({(Screen.fullScreen ? "fullscreen" : "windowed")})");
        report.AppendLine($"Mod:    [{Settings.Preset}] modEnabled={Settings.ModEnabled} optEnabled={Settings.OptimizationsEnabled} cpuPatches={Settings.CpuPatchesActive}");
        report.AppendLine();

        // ---- baseline (current state, all opts as configured) ----
        Status = "F7: baseline (all opts as-configured)";
        var baseline = new SampleResult();
        yield return SampleWindow(BaselineSeconds, baseline);
        Progress = 0.05f;

        report.AppendLine("== Baseline (current settings, all opts enabled) ==");
        report.AppendLine($"  {baseline.AvgMs:F2} ms ({baseline.AvgFps:F0} fps)  1%={baseline.P1Fps:F0} fps  worst={baseline.WorstMs:F1} ms  ({baseline.FrameCount} frames)");
        report.AppendLine();

        // ---- per-toggle sweep ----
        report.AppendLine("== Per-optimization savings (each opt forced OFF for 5s, then restored) ==");
        report.AppendLine("  Δ_ms columns: positive = opt is helping (frame time goes UP without it)");
        report.AppendLine();
        report.AppendLine($"  {"id",-50}  {"Δ avg ms",10}  {"Δ 1% low ms",13}  {"Δ worst ms",12}");
        report.AppendLine($"  {new string('-', 50)}  {new string('-', 10)}  {new string('-', 13)}  {new string('-', 12)}");

        var toggles = BuildToggles();
        for (int i = 0; i < toggles.Count; i++)
        {
            var t = toggles[i];
            Status = $"F7: {i + 1}/{toggles.Count} — {t.Id}";

            int snap = t.Snapshot();
            t.ForceOff();
            yield return new WaitForSeconds(SettleSeconds);

            var withOff = new SampleResult();
            yield return SampleWindow(SampleSeconds, withOff);

            t.Restore(snap);
            yield return new WaitForSeconds(SettleSeconds);

            float dAvg = withOff.AvgMs - baseline.AvgMs;
            float dP1 = withOff.P1Ms - baseline.P1Ms;
            float dWorst = withOff.WorstMs - baseline.WorstMs;
            report.AppendLine($"  {t.Id,-50}  {dAvg,+10:+0.00;-0.00}  {dP1,+13:+0.00;-0.00}  {dWorst,+12:+0.0;-0.0}");

            Progress = 0.05f + 0.85f * ((i + 1) / (float)toggles.Count);
        }

        report.AppendLine();
        report.AppendLine("Notes:");
        report.AppendLine("  - 'Δ avg ms' is positive when removing the optimization makes the average frame slower — i.e. the opt was saving that much per frame on average.");
        report.AppendLine("  - 'Δ 1% low ms' is positive when removing the opt makes the worst-1%-frames slower. Often more meaningful than avg for stutter-felt opts.");
        report.AppendLine("  - 'Δ worst ms' is the single worst frame in the sample window. Spiky and noisy — treat as directional, not gospel.");
        report.AppendLine("  - Some optimizations (S12 updateWhenOffscreen, S13 particle culling, S14 GPU instancing, S19 zero-intensity light shadows, S22 player avatar shadow distance, S15-S18 + S23-S24 quality config) cannot be cleanly toggled at runtime — they apply at level load via SceneOptimizer or QualityPatch. Measure those by F10-toggling the whole mod off in the same scene.");
        report.AppendLine("  - Run on a heavy procedural map (level 8+) for the most useful numbers. Run on both Beast PC and Crap PC.");
        report.AppendLine();

        // ensure everything is back to baseline before writing
        RestoreAll();
        Settings.AllocationFixesEnabled = true;

        var text = report.ToString();
        try { File.AppendAllText(OutputPath, text); }
        catch (Exception ex) { Plugin.Log.LogWarning($"F7 save failed: {ex.Message}"); }

        bool clipboardOk = false;
        try
        {
            GUIUtility.systemCopyBuffer = text;
            clipboardOk = GUIUtility.systemCopyBuffer == text;
        }
        catch { }

        Plugin.Log.LogInfo(clipboardOk
            ? $"F7 individual-opt probe done — written to {OutputPath} (copied to clipboard)"
            : $"F7 individual-opt probe done — written to {OutputPath} (clipboard unavailable)");

        Status = clipboardOk ? "F7 done (clipboard)" : "F7 done (file only)";
        Progress = 1f;
        Running = false;
    }

    private class SampleResult
    {
        public float AvgMs;
        public float AvgFps;
        public float P1Ms;
        public float P1Fps;
        public float WorstMs;
        public int FrameCount;
    }

    private static IEnumerator SampleWindow(float seconds, SampleResult result)
    {
        var frames = new List<float>(512);
        float elapsed = 0f;
        float worstMs = 0f;
        while (elapsed < seconds)
        {
            float dt = Time.unscaledDeltaTime;
            frames.Add(dt);
            elapsed += dt;
            float ms = dt * 1000f;
            if (ms > worstMs) worstMs = ms;
            yield return null;
        }

        if (frames.Count == 0)
        {
            result.AvgMs = result.AvgFps = result.P1Ms = result.P1Fps = result.WorstMs = 0f;
            result.FrameCount = 0;
            yield break;
        }

        float sum = 0f;
        for (int i = 0; i < frames.Count; i++) sum += frames[i];
        float avgMs = sum / frames.Count * 1000f;

        frames.Sort();
        int p1Count = Mathf.Max(1, Mathf.CeilToInt(frames.Count * 0.01f));
        float p1Sum = 0f;
        for (int i = frames.Count - 1; i >= frames.Count - p1Count; i--) p1Sum += frames[i];
        float p1Ms = p1Sum / p1Count * 1000f;

        result.AvgMs = avgMs;
        result.AvgFps = 1000f / avgMs;
        result.P1Ms = p1Ms;
        result.P1Fps = 1000f / p1Ms;
        result.WorstMs = worstMs;
        result.FrameCount = frames.Count;
    }
}

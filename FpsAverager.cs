using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace REPOFidelity;

// F9 manual FPS averager — collects frame times, computes avg/1%/0.1% lows,
// appends results + full settings snapshot to fps_averager.txt
internal class FpsAverager : MonoBehaviour
{
    internal static FpsAverager? Instance { get; private set; }
    internal static bool Running { get; private set; }
    internal static string Status = "";
    internal static float Progress;

    private const float Duration = 15f;
    private const float WarmupSeconds = 2f;

    private static readonly string OutputPath = Path.Combine(
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
        "fps_averager.txt");

    internal static void Toggle()
    {
        if (Running) { Abort(); return; }
        if (Instance == null)
        {
            var go = new GameObject("REPOFidelity_FpsAverager");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<FpsAverager>();
        }
        Instance.StartCoroutine(Instance.RunSafe());
    }

    internal static void Abort()
    {
        if (!Running) return;
        if (Instance != null) Instance.StopAllCoroutines();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        Running = false;
        Status = "";
        Plugin.Log.LogInfo("FPS averager cancelled");
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
                Plugin.Log.LogError($"FPS averager failed: {ex}");
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
        const float totalDuration = Duration * 2 + WarmupSeconds * 2 + 1f; // for progress

        // unlock cursor so user can click out/type during test
        var savedLockState = Cursor.lockState;
        var savedVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // --- Pass 1: current facing ---
        Status = "PASS 1 settling...";
        Progress = 0f;
        Plugin.Log.LogInfo("FPS averager: pass 1 (current facing)");
        for (int i = 0; i < 5; i++) yield return null;
        yield return new WaitForSeconds(WarmupSeconds);

        var frames1 = new List<float>();
        float elapsed = 0f;
        while (elapsed < Duration)
        {
            frames1.Add(Time.unscaledDeltaTime);
            elapsed += Time.unscaledDeltaTime;
            float remaining = Duration - elapsed;
            Progress = Mathf.Clamp01(elapsed / totalDuration);
            Status = $"PASS 1  {remaining:F0}s";
            yield return null;
        }
        var scene1 = GatherSceneMetrics();
        var result1 = ComputeResult(frames1);

        // --- Snap camera 180 ---
        var cam = Camera.main;
        Transform? rotTarget = null;
        if (cam != null)
        {
            // rotate the player body, not just the camera, to avoid mouse-look snapping back
            var avatar = cam.GetComponentInParent<PlayerAvatar>();
            rotTarget = avatar != null ? avatar.transform : cam.transform;
            rotTarget.Rotate(0f, 180f, 0f);
        }

        // --- Pass 2: opposite facing ---
        Status = "PASS 2 settling...";
        Plugin.Log.LogInfo("FPS averager: pass 2 (opposite facing)");
        for (int i = 0; i < 5; i++) yield return null;
        yield return new WaitForSeconds(WarmupSeconds);

        var frames2 = new List<float>();
        elapsed = 0f;
        while (elapsed < Duration)
        {
            frames2.Add(Time.unscaledDeltaTime);
            elapsed += Time.unscaledDeltaTime;
            float remaining = Duration - elapsed;
            Progress = Mathf.Clamp01((Duration + WarmupSeconds + elapsed) / totalDuration);
            Status = $"PASS 2  {remaining:F0}s";
            yield return null;
        }
        var scene2 = GatherSceneMetrics();
        var result2 = ComputeResult(frames2);

        // --- Snap back ---
        if (rotTarget != null)
            rotTarget.Rotate(0f, 180f, 0f);

        // --- Write combined report ---
        var report = BuildCombinedReport(result1, scene1, result2, scene2);
        try
        {
            File.AppendAllText(OutputPath, report);
            GUIUtility.systemCopyBuffer = report;
            Plugin.Log.LogInfo($"FPS averager: results appended to {OutputPath} (copied to clipboard)");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"FPS averager save failed: {ex.Message}"); }

        Plugin.Log.LogInfo($"FPS averager: P1={result1.AvgFps:F1} P2={result2.AvgFps:F1}");

        // restore cursor
        Cursor.lockState = savedLockState;
        Cursor.visible = savedVisible;

        Status = $"P1:{result1.AvgFps:F0}  P2:{result2.AvgFps:F0}  gap:{result1.AvgFps - result2.AvgFps:+0;-0}";
        Progress = 1f;
        Running = false;
        yield return new WaitForSeconds(5f);
        Status = "";
    }

    private struct FpsResult
    {
        public float AvgFps, AvgMs, P1Low, P01Low;
        public int FrameCount;
    }

    private static FpsResult ComputeResult(List<float> frames)
    {
        if (frames.Count == 0) return default;
        float sum = 0f;
        for (int i = 0; i < frames.Count; i++) sum += frames[i];
        float avgMs = sum / frames.Count * 1000f;
        frames.Sort();
        return new FpsResult
        {
            AvgFps = 1000f / avgMs,
            AvgMs = avgMs,
            P1Low = ComputePercentileLow(frames, 0.01f),
            P01Low = ComputePercentileLow(frames, 0.001f),
            FrameCount = frames.Count
        };
    }

    private static float ComputePercentileLow(List<float> sorted, float percentile)
    {
        // 1% low = average of the worst 1% of frame times, reported as FPS
        int count = Mathf.Max(1, Mathf.CeilToInt(sorted.Count * percentile));
        float worst = 0f;
        for (int i = sorted.Count - 1; i >= sorted.Count - count; i--)
            worst += sorted[i];
        return 1f / (worst / count);
    }

    private struct SceneMetrics
    {
        public int TotalLights, ActiveLights, ShadowingLights;
        public float TotalShadowCost;
        public int EstShadowDrawCalls;
        public int TotalRenderers, VisibleRenderers, ShadowCastingRenderers;
    }

    private static SceneMetrics GatherSceneMetrics()
    {
        var m = new SceneMetrics();

        // lights
        var lights = UnityEngine.Object.FindObjectsOfType<Light>();
        m.TotalLights = lights.Length;
        foreach (var light in lights)
        {
            bool active = light.enabled && light.gameObject.activeInHierarchy;
            if (active) m.ActiveLights++;
            if (active && light.shadows != LightShadows.None)
            {
                m.ShadowingLights++;
                int faces = light.type switch
                {
                    LightType.Point => 6,
                    LightType.Spot => 1,
                    LightType.Directional => QualitySettings.shadowCascades,
                    _ => 0
                };
                int res = light.shadowCustomResolution > 0 ? light.shadowCustomResolution
                    : QualitySettings.shadowResolution switch
                    {
                        ShadowResolution.Low => 256,
                        ShadowResolution.Medium => 512,
                        ShadowResolution.High => 1024,
                        ShadowResolution.VeryHigh => 2048,
                        _ => 1024
                    };
                m.TotalShadowCost += faces * ((float)res * res / (1024f * 1024f));

                // count shadow-casting renderers in this light's range
                var colliders = Physics.OverlapSphere(light.transform.position, light.range);
                var seen = new HashSet<Renderer>();
                int renderersInRange = 0;
                foreach (var col in colliders)
                {
                    var r = col.GetComponent<Renderer>();
                    if (r != null && r.shadowCastingMode != ShadowCastingMode.Off && seen.Add(r))
                        renderersInRange++;
                }
                m.EstShadowDrawCalls += renderersInRange * faces;
            }
        }

        // renderers
        var renderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
        m.TotalRenderers = renderers.Length;
        foreach (var r in renderers)
        {
            if (r.isVisible) m.VisibleRenderers++;
            if (r.shadowCastingMode != ShadowCastingMode.Off) m.ShadowCastingRenderers++;
        }

        return m;
    }

    private static string BuildCombinedReport(FpsResult r1, SceneMetrics s1, FpsResult r2, SceneMetrics s2)
    {
        var sb = new StringBuilder();
        float gap = r1.AvgFps - r2.AvgFps;

        sb.AppendLine($"── {DateTime.Now:yyyy-MM-dd HH:mm:ss} ── {SystemInfo.graphicsDeviceName} ── {Screen.width}x{Screen.height} ──");

        if (Settings.ModEnabled)
        {
            string cpu = Settings.CpuPatchesActive ? "ON" : "OFF";
            sb.AppendLine($"  [{Settings.Preset}] {Settings.ResolvedUpscaleMode} {Settings.ResolvedAAMode} " +
                $"SH:{Settings.ResolvedShadowQuality}/{Settings.ResolvedShadowDistance:F0}m " +
                $"L:{Settings.ResolvedPixelLightCount}/{Settings.ResolvedLightDistance:F0}m " +
                $"LOD:{Settings.ResolvedLODBias:F1} Scale:{Settings.ResolvedRenderScale}% " +
                $"Sharp:{Settings.Sharpening:F2} AF:{Settings.ResolvedAnisotropicFiltering}x CPU:{cpu}");
        }
        else
        {
            sb.AppendLine($"  [VANILLA] mod disabled");
        }

        sb.AppendLine($"  PASS 1 (facing):   {r1.AvgFps,6:F1} avg  {r1.AvgMs:F2}ms  1%={r1.P1Low:F1}  0.1%={r1.P01Low:F1}  ({r1.FrameCount} frames)");
        sb.AppendLine($"    visible:{s1.VisibleRenderers}  shadowing:{s1.ShadowingLights} cost:{s1.TotalShadowCost:F0} draws:{s1.EstShadowDrawCalls}");
        sb.AppendLine($"  PASS 2 (behind):   {r2.AvgFps,6:F1} avg  {r2.AvgMs:F2}ms  1%={r2.P1Low:F1}  0.1%={r2.P01Low:F1}  ({r2.FrameCount} frames)");
        sb.AppendLine($"    visible:{s2.VisibleRenderers}  shadowing:{s2.ShadowingLights} cost:{s2.TotalShadowCost:F0} draws:{s2.EstShadowDrawCalls}");
        sb.AppendLine($"  GAP: {gap:+0.0;-0.0} FPS  renderers:{s1.TotalRenderers} shadow-casters:{s1.ShadowCastingRenderers}");
        sb.AppendLine();

        return sb.ToString();
    }
}

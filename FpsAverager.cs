using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace REPOFidelity;

// F9 benchmark sweep — cycles through DLSS, FSR Temporal, and Off, running
// two facing directions per mode. Appends the combined report to fps_averager.txt
// and copies it to the clipboard.
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

        var savedLockState = Cursor.lockState;
        var savedVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        var modes = new List<(UpscaleMode mode, string label)>();
        if (GPUDetector.IsUpscalerSupported(UpscaleMode.DLSS))
            modes.Add((UpscaleMode.DLSS, "DLSS"));
        if (GPUDetector.IsUpscalerSupported(UpscaleMode.FSR_Temporal))
            modes.Add((UpscaleMode.FSR_Temporal, "FSR_Temporal"));
        modes.Add((UpscaleMode.Off, "Off"));

        var originalMode = Settings.UpscaleModeSetting;
        float totalDuration = modes.Count * (Duration * 2 + WarmupSeconds * 2 + ModeSwitchSettle);
        float elapsedAllModes = 0f;

        var combinedReport = new StringBuilder();
        combinedReport.AppendLine($"══ Benchmark Sweep {DateTime.Now:yyyy-MM-dd HH:mm:ss} ══ {SystemInfo.graphicsDeviceName} ══ {Screen.width}x{Screen.height} ══");
        combinedReport.AppendLine();

        foreach (var (mode, label) in modes)
        {
            Status = $"→ {label} (settling)";
            Plugin.Log.LogInfo($"FPS averager: switching to {label}");

            Settings.UpscaleModeSetting = mode;
            yield return new WaitForSeconds(ModeSwitchSettle);
            elapsedAllModes += ModeSwitchSettle;

            Status = $"{label} P1 settling...";
            for (int i = 0; i < 5; i++) yield return null;
            yield return new WaitForSeconds(WarmupSeconds);
            elapsedAllModes += WarmupSeconds;

            var frames1 = new List<float>();
            float elapsed = 0f;
            while (elapsed < Duration)
            {
                frames1.Add(Time.unscaledDeltaTime);
                elapsed += Time.unscaledDeltaTime;
                Progress = Mathf.Clamp01((elapsedAllModes + elapsed) / totalDuration);
                Status = $"{label} P1  {Duration - elapsed:F0}s";
                yield return null;
            }
            elapsedAllModes += Duration;
            var s1 = GatherSceneMetrics();
            var r1 = ComputeResult(frames1);

            var cam = Camera.main;
            Transform? rotTarget = null;
            if (cam != null)
            {
                var avatar = cam.GetComponentInParent<PlayerAvatar>();
                rotTarget = avatar != null ? avatar.transform : cam.transform;
                rotTarget.Rotate(0f, 180f, 0f);
            }

            Status = $"{label} P2 settling...";
            for (int i = 0; i < 5; i++) yield return null;
            yield return new WaitForSeconds(WarmupSeconds);
            elapsedAllModes += WarmupSeconds;

            var frames2 = new List<float>();
            elapsed = 0f;
            while (elapsed < Duration)
            {
                frames2.Add(Time.unscaledDeltaTime);
                elapsed += Time.unscaledDeltaTime;
                Progress = Mathf.Clamp01((elapsedAllModes + elapsed) / totalDuration);
                Status = $"{label} P2  {Duration - elapsed:F0}s";
                yield return null;
            }
            elapsedAllModes += Duration;
            var s2 = GatherSceneMetrics();
            var r2 = ComputeResult(frames2);

            if (rotTarget != null) rotTarget.Rotate(0f, 180f, 0f);

            combinedReport.Append(BuildCombinedReport(r1, s1, r2, s2));
        }

        Settings.UpscaleModeSetting = originalMode;

        var report = combinedReport.ToString();
        try
        {
            File.AppendAllText(OutputPath, report);
            GUIUtility.systemCopyBuffer = report;
            Plugin.Log.LogInfo($"FPS averager: swept {modes.Count} modes, appended to {OutputPath} (copied to clipboard)");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"FPS averager save failed: {ex.Message}"); }

        Cursor.lockState = savedLockState;
        Cursor.visible = savedVisible;

        Status = $"Done — {modes.Count} modes swept (clipboard)";
        Progress = 1f;
        Running = false;
        yield return new WaitForSeconds(5f);
        Status = "";
    }

    private const float ModeSwitchSettle = 2.5f;

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

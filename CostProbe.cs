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

    internal static void Abort()
    {
        if (!Running) return;
        if (Instance != null)
        {
            Instance.StopAllCoroutines();
            Camera.onPreRender  -= Instance.OnCamPre;
            Camera.onPostRender -= Instance.OnCamPost;
            Instance._camTimings.Clear();
        }
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        Running = false;
        Status = "";
        Plugin.Log.LogInfo("Cost probe cancelled");
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

        var savedLockState = Cursor.lockState;
        var savedVisible   = Cursor.visible;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;

        // Save user state so we can both normalize test config AND cleanly restore.
        // Baseline measurement was user-dependent before — now forced to a reference
        // config (Ultra preset, DLAA, fog 1.0×) so runs across builds are comparable.
        var origPreset = Settings.Preset;
        var origUpscaler = Settings.UpscaleModeSetting;
        var origFog = Settings.FogDistanceMultiplier;

        Status = "Normalizing test config (Ultra + DLAA + fog 1.0x)";
        Settings.ApplyPreset(QualityPreset.Ultra);
        Settings.ResolvedUpscaleMode = UpscaleMode.DLAA;
        Settings.ResolvedRenderScale = 100;
        Settings.ResolvedFogMultiplier = 1.0f;
        Patches.QualityPatch.ApplyFogAndDrawDistance();
        Patches.SceneOptimizer.Apply();
        Patches.QualityPatch.ApplyQualitySettings();
        yield return new WaitForSeconds(SettleSeconds);

        var report = new StringBuilder();
        report.AppendLine($"== REPOFidelity frame cost — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==");
        report.AppendLine($"GPU:    {SystemInfo.graphicsDeviceName}");
        report.AppendLine($"CPU:    {SystemInfo.processorType} ({SystemInfo.processorCount} cores)");
        report.AppendLine($"Screen: {Screen.width}x{Screen.height}");
        report.AppendLine($"Test:   forced Ultra + DLAA + fog 1.0× (user had [{origPreset}] upscaler={origUpscaler} fog={origFog:F2}×)");
        report.AppendLine($"Mod:    [Ultra-forced] upscaler={Settings.ResolvedUpscaleMode} " +
            $"scale={Settings.ResolvedRenderScale}% shadowQ={Settings.ResolvedShadowQuality} " +
            $"shadowD={Settings.ResolvedShadowDistance:F0}m lights={Settings.ResolvedPixelLightCount} " +
            $"lod={Settings.ResolvedLODBias:F1} AF={Settings.ResolvedAnisotropicFiltering}x");
        report.AppendLine($"Range:  fog={Settings.ResolvedFogMultiplier:F2}x " +
            $"effectiveFogEnd={Settings.ResolvedEffectiveFogEnd:F0}m " +
            $"lightD={Settings.ResolvedLightDistance:F0}m shadowBudget={Settings.ResolvedShadowBudget}");
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

        var timeTotals  = new double[timeRecs.Count];
        var countTotals = new long[countRecs.Count];
        var memTotals   = new long[memRecs.Count];

        var frames = new List<float>(2048);
        float elapsed = 0f;
        while (elapsed < SampleSeconds)
        {
            float dt = Time.unscaledDeltaTime;
            frames.Add(dt);
            elapsed += dt;
            Progress = elapsed / SampleSeconds;
            for (int i = 0; i < timeRecs.Count;  i++) timeTotals[i]  += timeRecs[i].rec.LastValue;
            for (int i = 0; i < countRecs.Count; i++) countTotals[i] += countRecs[i].rec.LastValue;
            for (int i = 0; i < memRecs.Count;   i++) memTotals[i]   += memRecs[i].rec.LastValue;
            yield return null;
        }

        Camera.onPreRender  -= OnCamPre;
        Camera.onPostRender -= OnCamPost;

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

        report.AppendLine($"Baseline:     {baseline.AvgMs:F2} ms ({baseline.AvgFps:F0} fps)  1%={baseline.P1Low:F0} fps  0.1%={baseline.P01Low:F0} fps  ({baseline.FrameCount} frames)");
        if (cpuTotal > 0) report.AppendLine($"CPU total:    {cpuTotal:F2} ms    main={cpuMain:F2}   render={cpuRend:F2}");
        if (gpuTotal > 0) report.AppendLine($"GPU total:    {gpuTotal:F2} ms");
        report.AppendLine($"Bottleneck:   {bottleneck}");
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
        Status = "Sweep: DLSS / FSR / Off / Potato / Vanilla";
        report.AppendLine($"== Upscalers + Potato + Vanilla ({UpscalerSampleSeconds}s each) ==");
        var upscalers = new List<UpscaleMode>();
        if (GPUDetector.IsUpscalerSupported(UpscaleMode.DLSS))         upscalers.Add(UpscaleMode.DLSS);
        if (GPUDetector.IsUpscalerSupported(UpscaleMode.FSR_Temporal)) upscalers.Add(UpscaleMode.FSR_Temporal);
        upscalers.Add(UpscaleMode.Off);

        var sweepResults = new List<(string label, float ms, float shadowD, float lightD, bool isActive)>();

        foreach (var mode in upscalers)
        {
            Status = $"Sweep: {mode}";
            Settings.UpscaleModeSetting = mode;
            yield return new WaitForSeconds(SettleSeconds);
            Sample s = default;
            yield return SampleFrames(UpscalerSampleSeconds, v => s = v);
            sweepResults.Add(($"{mode}", s.AvgMs, Settings.ResolvedShadowDistance, Settings.ResolvedLightDistance,
                mode == origUpscaler && origPreset == Settings.Preset));
        }

        // Preset × fog matrix — force fog to 1.1x (loosest allowed) and 0.3x (tightest),
        // walk all 5 presets at each. This shows frame-time-per-quality-tier AND the
        // fog-driven shadow/light clamp cascading through each preset.
        var presetLadder = new[]
        {
            QualityPreset.Potato,
            QualityPreset.Low,
            QualityPreset.Medium,
            QualityPreset.High,
            QualityPreset.Ultra,
        };
        var fogPasses = new[] { 1.1f, 0.3f };
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

        // Restore — mirrors OptimizerBenchmark exit path. Re-apply mod's values on top
        // of the vanilla QualitySettings so we end exactly where we started.
        Settings.ModEnabled = true;
        Settings.Preset = origPreset;
        Settings.UpscaleModeSetting = origUpscaler;
        Settings.FogDistanceMultiplier = origFog;
        Patches.SceneOptimizer.Apply();
        Patches.QualityPatch.ApplyQualitySettings();
        Patches.QualityPatch.ApplyFogAndDrawDistance();
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

        var text = report.ToString();
        try
        {
            File.AppendAllText(OutputPath, text);
            GUIUtility.systemCopyBuffer = text;
            Plugin.Log.LogInfo($"Cost probe: appended to {OutputPath} (copied to clipboard)");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Cost probe save failed: {ex.Message}"); }

        Cursor.lockState = savedLockState;
        Cursor.visible   = savedVisible;

        Status = "Done (clipboard)";
        Progress = 1f;
        Running = false;
        yield return new WaitForSeconds(5f);
        Status = "";
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
                m.TotalSceneTris += mf.sharedMesh.triangles.LongLength / 3;
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

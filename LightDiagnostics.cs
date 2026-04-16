using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace REPOFidelity;

// F7 light diagnostics — dumps every light in the scene with shadow info,
// component types, estimated cost, and summary totals
internal static class LightDiagnostics
{
    private static readonly string OutputPath = Path.Combine(
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
        "light_diagnostics.txt");

    internal static void Run()
    {
        var lights = UnityEngine.Object.FindObjectsOfType<Light>();
        if (lights.Length == 0)
        {
            Plugin.Log.LogInfo("Light diagnostics: no lights found");
            return;
        }

        // gather info per light
        var entries = new List<LightEntry>();
        foreach (var light in lights)
        {
            var entry = new LightEntry
            {
                Name = GetPath(light.transform),
                Type = light.type,
                ShadowMode = light.shadows,
                ShadowRes = light.shadowCustomResolution,
                GlobalShadowRes = QualitySettings.shadowResolution,
                Range = light.range,
                Intensity = light.intensity,
                Color = light.color,
                Enabled = light.enabled && light.gameObject.activeInHierarchy,
                CastsShadows = light.shadows != LightShadows.None,
                RenderMode = light.renderMode,
            };

            // detect game components on this object or parents
            var go = light.gameObject;
            entry.HasLightAnimator = go.GetComponent<LightAnimator>() != null;
            entry.HasItemLight = go.GetComponent<ItemLight>() != null;
            entry.HasFlashlight = go.GetComponent<FlashlightController>() != null;
            entry.HasExplosion = go.GetComponentInParent<ParticlePrefabExplosion>() != null;

            // shadow map faces: point = 6 (cubemap), spot = 1, directional = cascades
            entry.ShadowFaces = light.type switch
            {
                LightType.Point => 6,
                LightType.Spot => 1,
                LightType.Directional => QualitySettings.shadowCascades,
                _ => 0
            };

            // effective shadow resolution
            int effectiveRes = entry.ShadowRes > 0 ? entry.ShadowRes : GlobalResValue();
            entry.EffectiveRes = effectiveRes;

            // estimated relative cost (shadow faces x resolution^2, normalized)
            if (entry.CastsShadows && entry.Enabled)
                entry.ShadowCost = entry.ShadowFaces * ((float)effectiveRes * effectiveRes / (1024f * 1024f));
            else
                entry.ShadowCost = 0f;

            // count renderers in range (approximation of shadow draw calls)
            if (entry.CastsShadows && entry.Enabled)
            {
                int renderersInRange = 0;
                var colliders = Physics.OverlapSphere(light.transform.position, light.range);
                var seen = new HashSet<Renderer>();
                foreach (var col in colliders)
                {
                    var r = col.GetComponent<Renderer>();
                    if (r != null && r.shadowCastingMode != ShadowCastingMode.Off && seen.Add(r))
                        renderersInRange++;
                }
                entry.RenderersInRange = renderersInRange;
                entry.EstDrawCalls = renderersInRange * entry.ShadowFaces;
            }

            entries.Add(entry);
        }

        // sort by cost descending
        entries.Sort((a, b) => b.ShadowCost.CompareTo(a.ShadowCost));

        // build report
        var sb = new StringBuilder();
        sb.AppendLine("══════════════════════════════════════════════════════════════");
        sb.AppendLine($"  LIGHT DIAGNOSTICS — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("══════════════════════════════════════════════════════════════");
        sb.AppendLine();
        sb.AppendLine($"  Total lights:         {lights.Length}");

        int active = 0, shadowing = 0, point = 0, spot = 0, dir = 0, area = 0;
        int hasAnimator = 0, hasItemLight = 0, hasFlashlight = 0, hasExplosion = 0, plainLight = 0;
        float totalCost = 0f;
        int totalEstDrawCalls = 0;

        foreach (var e in entries)
        {
            if (e.Enabled) active++;
            if (e.CastsShadows && e.Enabled) shadowing++;
            switch (e.Type)
            {
                case LightType.Point: point++; break;
                case LightType.Spot: spot++; break;
                case LightType.Directional: dir++; break;
                default: area++; break;
            }
            if (e.HasLightAnimator) hasAnimator++;
            else if (e.HasItemLight) hasItemLight++;
            else if (e.HasFlashlight) hasFlashlight++;
            else if (e.HasExplosion) hasExplosion++;
            else plainLight++;
            totalCost += e.ShadowCost;
            totalEstDrawCalls += e.EstDrawCalls;
        }

        sb.AppendLine($"  Active:               {active}");
        sb.AppendLine($"  Casting shadows:      {shadowing}");
        sb.AppendLine();
        sb.AppendLine($"  By type:              Point={point}  Spot={spot}  Directional={dir}  Area={area}");
        sb.AppendLine($"  By component:         Plain={plainLight}  LightAnimator={hasAnimator}  ItemLight={hasItemLight}  Flashlight={hasFlashlight}  Explosion={hasExplosion}");
        sb.AppendLine();
        sb.AppendLine($"  Global shadow res:    {QualitySettings.shadowResolution}");
        sb.AppendLine($"  Shadow distance:      {QualitySettings.shadowDistance:F0}m");
        sb.AppendLine($"  Shadow cascades:      {QualitySettings.shadowCascades}");
        sb.AppendLine();
        sb.AppendLine($"  Total shadow cost:    {totalCost:F1} (relative units: faces x res²/1M)");
        sb.AppendLine($"  Est shadow draw calls:{totalEstDrawCalls}");
        sb.AppendLine();

        // summary by category
        sb.AppendLine("──────────────────────────────────────────────────────────────");
        sb.AppendLine("  COST BY CATEGORY");
        sb.AppendLine("──────────────────────────────────────────────────────────────");

        float costPlain = 0, costAnim = 0, costItem = 0, costFlash = 0, costExplo = 0;
        int dcPlain = 0, dcAnim = 0, dcItem = 0, dcFlash = 0, dcExplo = 0;
        foreach (var e in entries)
        {
            if (e.HasLightAnimator) { costAnim += e.ShadowCost; dcAnim += e.EstDrawCalls; }
            else if (e.HasItemLight) { costItem += e.ShadowCost; dcItem += e.EstDrawCalls; }
            else if (e.HasFlashlight) { costFlash += e.ShadowCost; dcFlash += e.EstDrawCalls; }
            else if (e.HasExplosion) { costExplo += e.ShadowCost; dcExplo += e.EstDrawCalls; }
            else { costPlain += e.ShadowCost; dcPlain += e.EstDrawCalls; }
        }

        sb.AppendLine($"  Plain Light:      cost={costPlain,8:F1}  est draw calls={dcPlain,6}");
        sb.AppendLine($"  LightAnimator:    cost={costAnim,8:F1}  est draw calls={dcAnim,6}");
        sb.AppendLine($"  ItemLight:        cost={costItem,8:F1}  est draw calls={dcItem,6}");
        sb.AppendLine($"  Flashlight:       cost={costFlash,8:F1}  est draw calls={dcFlash,6}");
        sb.AppendLine($"  Explosion:        cost={costExplo,8:F1}  est draw calls={dcExplo,6}");
        sb.AppendLine();

        // per-light detail
        sb.AppendLine("──────────────────────────────────────────────────────────────");
        sb.AppendLine("  PER-LIGHT DETAIL (sorted by shadow cost, descending)");
        sb.AppendLine("──────────────────────────────────────────────────────────────");

        foreach (var e in entries)
        {
            string status = e.Enabled ? (e.CastsShadows ? "SHADOW" : "noshadow") : "INACTIVE";
            string component = e.HasLightAnimator ? "[Animator]" :
                               e.HasItemLight ? "[ItemLight]" :
                               e.HasFlashlight ? "[Flashlight]" :
                               e.HasExplosion ? "[Explosion]" : "[Plain]";

            sb.AppendLine();
            sb.AppendLine($"  {e.Name}");
            sb.AppendLine($"    {e.Type} {component}  {status}  render={e.RenderMode}");
            sb.AppendLine($"    range={e.Range:F1}m  intensity={e.Intensity:F2}  color=({e.Color.r:F2},{e.Color.g:F2},{e.Color.b:F2})");

            if (e.CastsShadows && e.Enabled)
            {
                sb.AppendLine($"    shadow={e.ShadowMode}  res={e.EffectiveRes} (custom={e.ShadowRes})  faces={e.ShadowFaces}");
                sb.AppendLine($"    renderers in range={e.RenderersInRange}  est draw calls={e.EstDrawCalls}  cost={e.ShadowCost:F1}");
            }
        }

        sb.AppendLine();

        // write
        var report = sb.ToString();
        try
        {
            File.AppendAllText(OutputPath, report);
            GUIUtility.systemCopyBuffer = report;
            Plugin.Log.LogInfo($"Light diagnostics appended to {OutputPath} (copied to clipboard)");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"Light diagnostics save failed: {ex.Message}"); }

        Plugin.Log.LogInfo($"Light diagnostics: {lights.Length} lights, {shadowing} shadowing, cost={totalCost:F1}");
    }

    private static int GlobalResValue()
    {
        return QualitySettings.shadowResolution switch
        {
            UnityEngine.ShadowResolution.Low => 256,
            UnityEngine.ShadowResolution.Medium => 512,
            UnityEngine.ShadowResolution.High => 1024,
            UnityEngine.ShadowResolution.VeryHigh => 2048,
            _ => 1024
        };
    }

    private static string GetPath(Transform t)
    {
        // short path: parent/name (full hierarchy is too noisy)
        if (t.parent != null)
            return $"{t.parent.name}/{t.name}";
        return t.name;
    }

    private struct LightEntry
    {
        public string Name;
        public LightType Type;
        public LightShadows ShadowMode;
        public int ShadowRes;
        public ShadowResolution GlobalShadowRes;
        public int EffectiveRes;
        public float Range;
        public float Intensity;
        public Color Color;
        public bool Enabled;
        public bool CastsShadows;
        public LightRenderMode RenderMode;
        public bool HasLightAnimator;
        public bool HasItemLight;
        public bool HasFlashlight;
        public bool HasExplosion;
        public int ShadowFaces;
        public float ShadowCost;
        public int RenderersInRange;
        public int EstDrawCalls;
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace REPOFidelity;

// bottom-left status overlay — debug info, benchmark progress, mod-off indicator
internal static class Overlay
{
    private static GUIStyle? _styleTitle, _shadowTitle;
    private static GUIStyle? _styleInfo, _shadowInfo;
    private static GUIStyle? _styleWarn, _shadowWarn;
    private static GUIStyle? _styleDim, _shadowDim;
    private static int _lastScreenH;

    private static readonly List<Line> _lines = new();
    private static bool _showProgress;
    private static float _progress;
    private static Color _progressColor;

    // smoothed FPS
    private static float _fpsAccum, _fpsTimer;
    private static int _fpsFrames;
    private static float _smoothFps, _smoothMs;

    // layout
    private const float LineSpacing = 28f;
    private const float ShadowOff = 1.5f;

    internal static void UpdateLines()
    {
        // smooth FPS over 0.5s
        _fpsAccum += Time.unscaledDeltaTime;
        _fpsFrames++;
        _fpsTimer += Time.unscaledDeltaTime;
        if (_fpsTimer >= 0.5f)
        {
            _smoothMs = (_fpsAccum / _fpsFrames) * 1000f;
            _smoothFps = 1000f / Mathf.Max(_smoothMs, 0.001f);
            _fpsAccum = 0f; _fpsFrames = 0; _fpsTimer = 0f;
        }

        _lines.Clear();
        _showProgress = false;

        // --- collect lines in display order (top to bottom) ---

        if (UpscalerManager.RepoHdDetected)
            _lines.Add(new Line("REPO HD DETECTED - PLEASE REMOVE", Col.Warn));

        // auto-tune / benchmark status
        if (UpscalerManager.BenchmarkActive)
        {
            _showProgress = true;
            _progress = UpscalerManager.BenchmarkProgress;
            _progressColor = new Color(0.9f, 0.35f, 0.35f);
            string label = UpscalerManager.AutoBenchmarkRunning ? "AUTO-TUNING" : "BENCHMARKING";
            _lines.Add(new Line($"{label}   {Mathf.RoundToInt(_progress * 100)}%   {_smoothFps:F0} FPS", Col.Warn));
        }

        // optimizer benchmark (F12)
        if (OptimizerBenchmark.Running || !string.IsNullOrEmpty(OptimizerBenchmark.Status))
        {
            _lines.Add(new Line($"OPTIMIZER   {OptimizerBenchmark.Status}", Col.Info));
            if (OptimizerBenchmark.Running)
            {
                _showProgress = true;
                _progress = OptimizerBenchmark.Progress;
                _progressColor = new Color(0.35f, 0.85f, 0.4f);
            }
        }

        // mod disabled
        if (!Settings.ModEnabled)
        {
            _lines.Add(new Line(
                $"REPO FIDELITY OFF  ({Settings.ToggleKey})   {_smoothFps:F0} FPS   {_smoothMs:F1}ms",
                Col.Warn));
        }

        // debug overlay
        if (Settings.ModEnabled && Settings.DebugOverlay)
        {
            string preset = Settings.Preset.ToString().ToUpper();
            string upscaler = Settings.ResolvedUpscaleMode.ToString();
            string aa = Settings.ResolvedAAMode != AAMode.Off ? $" {Settings.ResolvedAAMode}" : "";

            _lines.Add(new Line(
                $"FIDELITY [{preset}]   {upscaler}{aa}   {_smoothFps:F0} FPS   {_smoothMs:F1}ms",
                Col.Title));

            string cpu = Settings.CpuPatchesActive ? "ON" : "OFF";
            string cpuMode = Settings.CpuPatchMode switch { 1 => "FORCED", 0 => "FORCED", _ => "AUTO" };
            _lines.Add(new Line(
                $"SH {Settings.ResolvedShadowQuality}/{Settings.ResolvedShadowDistance:F0}m" +
                $"   LIGHTS {Settings.ResolvedPixelLightCount}" +
                $"   LOD {Settings.ResolvedLODBias:F1}" +
                $"   CPU {cpu} ({cpuMode})",
                Col.Dim));
        }
    }

    internal static void Draw()
    {
        if (_lines.Count == 0) return;
        EnsureStyles();

        float s = Mathf.Max(Screen.height / 1080f, 0.5f);
        float x = 20f * s;
        float spacing = LineSpacing * s;
        float sh = ShadowOff * s;

        // total height of all content
        float totalH = _lines.Count * spacing;
        if (_showProgress) totalH += 10f * s;

        // position: bottom-left, above the bottom edge
        float startY = Screen.height - totalH - 50f * s;
        float y = startY;
        float labelW = Screen.width * 0.55f;

        // draw lines top-to-bottom
        for (int i = 0; i < _lines.Count; i++)
        {
            GetStyles(_lines[i].Color, out var style, out var shadow);
            var rect = new Rect(x, y, labelW, spacing);

            GUI.Label(new Rect(rect.x + sh, rect.y + sh, rect.width, rect.height),
                _lines[i].Text, shadow);
            GUI.Label(rect, _lines[i].Text, style);

            y += spacing;
        }

        // progress bar below all text
        if (_showProgress)
        {
            float barW = 280f * s;
            float barH = 3f * s;
            float barY = y + 4f * s;
            GUI.DrawTexture(new Rect(x, barY, barW, barH), Texture2D.whiteTexture,
                ScaleMode.StretchToFill, false, 0, new Color(0.15f, 0.15f, 0.15f, 0.5f), 0, 0);
            GUI.DrawTexture(new Rect(x, barY, barW * Mathf.Clamp01(_progress), barH),
                Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, _progressColor, 0, 0);
        }
    }

    private static void EnsureStyles()
    {
        if (_styleTitle != null && _lastScreenH == Screen.height) return;
        _lastScreenH = Screen.height;

        float s = Mathf.Max(Screen.height / 1080f, 0.5f);
        int titleSize = Mathf.Max(Mathf.RoundToInt(17 * s), 11);
        int infoSize = Mathf.Max(Mathf.RoundToInt(16 * s), 11);
        int dimSize = Mathf.Max(Mathf.RoundToInt(14 * s), 10);

        MakePair(titleSize, new Color(0.3f, 0.92f, 0.4f), out _styleTitle, out _shadowTitle);
        MakePair(infoSize, new Color(0.35f, 0.88f, 0.45f), out _styleInfo, out _shadowInfo);
        MakePair(infoSize, new Color(0.95f, 0.35f, 0.3f), out _styleWarn, out _shadowWarn);
        MakePair(dimSize, new Color(0.45f, 0.52f, 0.45f), out _styleDim, out _shadowDim);
    }

    private static void MakePair(int size, Color color, out GUIStyle text, out GUIStyle shadow)
    {
        text = new GUIStyle
        {
            fontSize = size,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        text.normal.textColor = color;

        shadow = new GUIStyle(text);
        shadow.normal.textColor = new Color(0, 0, 0, 0.55f);
    }

    private static void GetStyles(Col c, out GUIStyle text, out GUIStyle shadow)
    {
        (text, shadow) = c switch
        {
            Col.Title => (_styleTitle!, _shadowTitle!),
            Col.Info => (_styleInfo!, _shadowInfo!),
            Col.Warn => (_styleWarn!, _shadowWarn!),
            Col.Dim => (_styleDim!, _shadowDim!),
            _ => (_styleTitle!, _shadowTitle!)
        };
    }

    private struct Line
    {
        public string Text;
        public Col Color;
        public Line(string text, Col color) { Text = text; Color = color; }
    }

    private enum Col { Title, Info, Warn, Dim }
}

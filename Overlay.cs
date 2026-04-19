using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace REPOFidelity;

// bottom-left status overlay. native HUD elements (TMP + scanlines) when in-game,
// OnGUI fallback during menus/loading.
internal static class Overlay
{
    // native HUD
    static GameObject? _root;
    static readonly List<NativeLine> _nativeLines = new();
    static RectTransform? _rootRt;
    static RectTransform? _progressBgNative;
    static RectTransform? _progressFillNative;
    static Image? _progressFillImg;
    static TMP_FontAsset? _gameFont;
    static Sprite? _scanlineSprite;
    static bool _nativeActive;

    // shared state built each frame
    static readonly List<LineData> _lines = new();
    static bool _showProgress;
    static float _progress;
    static Color _progressColor;

    // smoothed FPS
    static float _fpsAccum, _fpsTimer;
    static int _fpsFrames;
    static float _smoothFps, _smoothMs;

    // exposed for the settings menu status line
    internal static float SmoothFps => _smoothFps;
    internal static float SmoothMs => _smoothMs;

    // animation
    const float SlideSpeed = 6f;     // lerp speed for slide in/out
    const float HideOffsetY = -40f;  // how far below target to start/end

    // layout
    const float FontSize = 14f;
    const float LineH = 20f;
    const float TextWidth = 500f; // max width, text overflows so actual rendering is content-width
    const float BaseX = 12f;
    const float BaseY = 110f; // above flashlight HUD element

    // OnGUI fallback
    static GUIStyle? _styleTitle, _shadowTitle;
    static GUIStyle? _styleInfo, _shadowInfo;
    static GUIStyle? _styleWarn, _shadowWarn;
    static GUIStyle? _styleDim, _shadowDim;
    static int _lastScreenH;

    internal static void UpdateLines()
    {
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

        if (UpscalerManager.RepoHdDetected)
            _lines.Add(new LineData("REPO HD DETECTED - REMOVE", Col.Warn));

        if (UpscalerManager.BenchmarkActive)
        {
            _showProgress = true;
            _progress = UpscalerManager.BenchmarkProgress;
            _progressColor = new Color(0.9f, 0.35f, 0.35f);
            string label = UpscalerManager.AutoBenchmarkRunning ? "AUTO-TUNING" : "BENCHMARKING";
            _lines.Add(new LineData($"{label}  {Mathf.RoundToInt(_progress * 100)}%  {_smoothFps:F0} FPS  {_smoothMs:F1}ms", Col.Warn));
        }

        if (OptimizerBenchmark.Running || !string.IsNullOrEmpty(OptimizerBenchmark.Status))
        {
            _lines.Add(new LineData($"OPTIMIZER  {OptimizerBenchmark.Status}", Col.Info));
            if (OptimizerBenchmark.Running)
            {
                _showProgress = true;
                _progress = OptimizerBenchmark.Progress;
                _progressColor = new Color(0.35f, 0.85f, 0.4f);
            }
        }

        // Cost probe — suppress all other FPS display while running
        if (CostProbe.Running || !string.IsNullOrEmpty(CostProbe.Status))
        {
            _lines.Add(new LineData(CostProbe.Status, Col.Info));
            if (CostProbe.Running)
            {
                _showProgress = true;
                _progress = CostProbe.Progress;
                _progressColor = new Color(0.4f, 0.7f, 0.95f);
            }
            _nativeActive = TryUpdateNative();
            return;
        }

        if (!Settings.ModEnabled)
            _lines.Add(new LineData($"FIDELITY OFF ({Settings.ToggleKey})  {_smoothFps:F0} FPS  {_smoothMs:F1}ms", Col.Warn));
        else if (!Settings.OptimizationsEnabled)
            _lines.Add(new LineData($"OPTIMIZATIONS OFF (F11)  {_smoothFps:F0} FPS  {_smoothMs:F1}ms", Col.Warn));

        if (Settings.ModEnabled && Settings.DebugOverlay)
        {
            string preset = Settings.Preset.ToString().ToUpper();
            string up = Settings.ResolvedUpscaleMode.ToString();
            string aa = Settings.ResolvedAAMode != AAMode.Off ? $" {Settings.ResolvedAAMode}" : "";
            // skip FPS when benchmark is already showing it
            bool benchShowing = UpscalerManager.BenchmarkActive || OptimizerBenchmark.Running;
            string fps = benchShowing ? "" : $"  {_smoothFps:F0} FPS  {_smoothMs:F1}ms";
            _lines.Add(new LineData($"[{preset}] {up}{aa}{fps}", Col.Title));

            string cpu = Settings.CpuPatchesActive ? "ON" : "OFF";
            string cm = Settings.CpuPatchMode switch { 1 => "FORCED", 0 => "FORCED", _ => "AUTO" };
            _lines.Add(new LineData(
                $"SH:{Settings.ResolvedShadowQuality}/{Settings.ResolvedShadowDistance:F0}m " +
                $"L:{Settings.ResolvedPixelLightCount} LOD:{Settings.ResolvedLODBias:F1} CPU:{cpu}({cm})",
                Col.Dim));
        }

        _nativeActive = TryUpdateNative();
    }

    internal static void Draw()
    {
        if (_nativeActive || _lines.Count == 0) return;
        DrawOnGUI();
    }

    // ── native HUD ──

    static bool TryUpdateNative()
    {
        if (HUDCanvas.instance == null) { ClearNative(); return false; }
        if (_root == null) { _nativeLines.Clear(); BuildNative(); }
        if (_root == null) return false;

        // grow line pool
        while (_nativeLines.Count < _lines.Count)
            _nativeLines.Add(CreateNativeLine(_nativeLines.Count));

        float dt = Time.unscaledDeltaTime;
        float progressH = _showProgress ? 10f : 0f;

        for (int i = 0; i < _nativeLines.Count; i++)
        {
            var nl = _nativeLines[i];
            bool visible = i < _lines.Count;

            if (visible)
            {
                nl.Go.SetActive(true);
                nl.Text.text = _lines[i].Text;
                nl.Text.color = GetColor(_lines[i].Color);

                // target position: stack bottom-up, line 0 at top
                float targetY = progressH + BaseY + (_lines.Count - 1 - i) * LineH;
                nl.TargetY = targetY;

                // if just appeared, start from below
                if (!nl.WasVisible)
                    nl.CurrentY = targetY + HideOffsetY;
                nl.WasVisible = true;
            }
            else
            {
                // slide out downward
                nl.TargetY = nl.CurrentY + HideOffsetY;
                if (nl.WasVisible)
                    nl.WasVisible = false;
            }

            // animate
            nl.CurrentY = Mathf.Lerp(nl.CurrentY, nl.TargetY, dt * SlideSpeed);

            // hide when fully off screen
            if (!visible && Mathf.Abs(nl.CurrentY - nl.TargetY) < 0.5f)
            {
                nl.Go.SetActive(false);
                continue;
            }

            nl.Rt.anchoredPosition = new Vector2(0, nl.CurrentY);

            // size scanline overlay to match rendered text width
            if (nl.ScanRt != null && nl.Text.preferredWidth > 0)
                nl.ScanRt.sizeDelta = new Vector2(nl.Text.preferredWidth + 4f, 0);

            _nativeLines[i] = nl;
        }

        _root.SetActive(_lines.Count > 0 || AnyAnimating());

        // progress bar
        if (_progressBgNative != null)
        {
            _progressBgNative.gameObject.SetActive(_showProgress);
            if (_showProgress && _progressFillNative != null && _progressFillImg != null)
            {
                _progressFillNative.anchorMax = new Vector2(Mathf.Clamp01(_progress), 1);
                _progressFillImg.color = _progressColor;
            }
        }

        return true;
    }

    static bool AnyAnimating()
    {
        for (int i = 0; i < _nativeLines.Count; i++)
            if (_nativeLines[i].Go.activeSelf) return true;
        return false;
    }

    static void BuildNative()
    {
        if (HUDCanvas.instance == null) return;
        FindGameAssets();
        if (_gameFont == null) return;

        _root = new GameObject("FidelityOverlay");
        _root.transform.SetParent(HUDCanvas.instance.rect, false);

        _rootRt = _root.AddComponent<RectTransform>();
        _rootRt.anchorMin = Vector2.zero;
        _rootRt.anchorMax = Vector2.zero;
        _rootRt.pivot = Vector2.zero;
        _rootRt.anchoredPosition = new Vector2(BaseX, 0);
        _rootRt.sizeDelta = new Vector2(TextWidth + 20, 200);

        // progress bar
        var progBg = new GameObject("ProgressBg");
        progBg.transform.SetParent(_rootRt, false);
        _progressBgNative = progBg.AddComponent<RectTransform>();
        _progressBgNative.anchorMin = Vector2.zero;
        _progressBgNative.anchorMax = new Vector2(0, 0);
        _progressBgNative.pivot = Vector2.zero;
        _progressBgNative.anchoredPosition = new Vector2(0, BaseY - 8);
        _progressBgNative.sizeDelta = new Vector2(220, 3);
        var bgImg = progBg.AddComponent<Image>();
        bgImg.color = new Color(0.15f, 0.15f, 0.15f, 0.5f);
        bgImg.raycastTarget = false;
        progBg.SetActive(false);

        var fill = new GameObject("Fill");
        fill.transform.SetParent(_progressBgNative, false);
        _progressFillNative = fill.AddComponent<RectTransform>();
        _progressFillNative.anchorMin = Vector2.zero;
        _progressFillNative.anchorMax = new Vector2(0, 1);
        _progressFillNative.pivot = new Vector2(0, 0.5f);
        _progressFillNative.offsetMin = Vector2.zero;
        _progressFillNative.offsetMax = Vector2.zero;
        _progressFillImg = fill.AddComponent<Image>();
        _progressFillImg.raycastTarget = false;

        Plugin.Log.LogInfo("Overlay: native HUD created");
    }

    static NativeLine CreateNativeLine(int index)
    {
        var go = new GameObject($"FidelityLine{index}");
        go.transform.SetParent(_root!.transform, false);

        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0, 0);
        rt.anchoredPosition = new Vector2(0, HideOffsetY); // start below
        rt.sizeDelta = new Vector2(TextWidth, LineH);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.font = _gameFont;
        tmp.fontSize = FontSize;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.BottomLeft;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.raycastTarget = false;
        tmp.enableWordWrapping = false;

        RectTransform? scanRt = null;
        if (_scanlineSprite != null)
        {
            var scanGo = new GameObject("Scanlines");
            scanGo.transform.SetParent(go.transform, false);
            scanRt = scanGo.AddComponent<RectTransform>();
            // anchored to left, sized dynamically to match text width
            scanRt.anchorMin = Vector2.zero;
            scanRt.anchorMax = new Vector2(0, 1);
            scanRt.pivot = new Vector2(0, 0.5f);
            scanRt.offsetMin = Vector2.zero;
            scanRt.offsetMax = Vector2.zero;
            scanRt.sizeDelta = new Vector2(10, 0); // updated per frame
            var scanImg = scanGo.AddComponent<Image>();
            scanImg.sprite = _scanlineSprite;
            scanImg.type = Image.Type.Tiled;
            scanImg.raycastTarget = false;
            scanGo.AddComponent<UIScanlines>();
        }

        return new NativeLine
        {
            Go = go, Text = tmp, Rt = rt, ScanRt = scanRt,
            CurrentY = HideOffsetY, TargetY = HideOffsetY, WasVisible = false
        };
    }

    static void FindGameAssets()
    {
        if (_gameFont != null) return;
        foreach (var tmp in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
        {
            if (tmp.font != null && tmp.gameObject.scene.isLoaded)
            { _gameFont = tmp.font; break; }
        }
        foreach (var scan in Resources.FindObjectsOfTypeAll<UIScanlines>())
        {
            var img = scan.GetComponent<Image>();
            if (img != null && img.sprite != null)
            {
                _scanlineSprite = img.sprite;
                Plugin.Log.LogInfo($"Overlay: scanline sprite '{_scanlineSprite.name}'");
                break;
            }
        }
        if (_gameFont != null)
            Plugin.Log.LogInfo($"Overlay: game font '{_gameFont.name}'");
    }

    static void ClearNative()
    {
        if (_root != null) Object.Destroy(_root);
        _root = null;
        _nativeLines.Clear();
    }

    struct NativeLine
    {
        public GameObject Go;
        public TextMeshProUGUI Text;
        public RectTransform Rt;
        public RectTransform? ScanRt;
        public float CurrentY;
        public float TargetY;
        public bool WasVisible;
    }

    // ── OnGUI fallback ──

    static void DrawOnGUI()
    {
        EnsureOnGUIStyles();
        float s = Mathf.Max(Screen.height / 1080f, 0.5f);
        float x = 20f * s;
        float spacing = 28f * s;
        float sh = 1.5f * s;
        float totalH = _lines.Count * spacing + (_showProgress ? 10f * s : 0);
        float y = Screen.height - totalH - 50f * s;

        for (int i = 0; i < _lines.Count; i++)
        {
            GetOnGUIStyles(_lines[i].Color, out var style, out var shadow);
            var rect = new Rect(x, y, Screen.width * 0.5f, spacing);
            GUI.Label(new Rect(rect.x + sh, rect.y + sh, rect.width, rect.height), _lines[i].Text, shadow);
            GUI.Label(rect, _lines[i].Text, style);
            y += spacing;
        }

        if (_showProgress)
        {
            float barW = 220f * s;
            GUI.DrawTexture(new Rect(x, y + 4f * s, barW, 3f * s), Texture2D.whiteTexture,
                ScaleMode.StretchToFill, false, 0, new Color(0.1f, 0.1f, 0.1f, 0.5f), 0, 0);
            GUI.DrawTexture(new Rect(x, y + 4f * s, barW * Mathf.Clamp01(_progress), 3f * s),
                Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, _progressColor, 0, 0);
        }
    }

    static Color GetColor(Col c) => c switch
    {
        Col.Title => new Color(0.3f, 0.92f, 0.4f),
        Col.Info => new Color(0.35f, 0.88f, 0.45f),
        Col.Warn => new Color(0.95f, 0.35f, 0.3f),
        Col.Dim => new Color(0.45f, 0.52f, 0.45f),
        _ => new Color(0.9f, 0.95f, 0.9f)
    };

    static void EnsureOnGUIStyles()
    {
        if (_styleTitle != null && _lastScreenH == Screen.height) return;
        _lastScreenH = Screen.height;
        float s = Mathf.Max(Screen.height / 1080f, 0.5f);
        int big = Mathf.Max(Mathf.RoundToInt(16 * s), 11);
        int sm = Mathf.Max(Mathf.RoundToInt(13 * s), 10);
        MakePair(big, GetColor(Col.Title), out _styleTitle, out _shadowTitle);
        MakePair(big, GetColor(Col.Info), out _styleInfo, out _shadowInfo);
        MakePair(big, GetColor(Col.Warn), out _styleWarn, out _shadowWarn);
        MakePair(sm, GetColor(Col.Dim), out _styleDim, out _shadowDim);
    }

    static void MakePair(int size, Color color, out GUIStyle text, out GUIStyle shadow)
    {
        text = new GUIStyle { fontSize = size, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        text.normal.textColor = color;
        shadow = new GUIStyle(text);
        shadow.normal.textColor = new Color(0, 0, 0, 0.55f);
    }

    static void GetOnGUIStyles(Col c, out GUIStyle text, out GUIStyle shadow)
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

    struct LineData
    {
        public string Text;
        public Col Color;
        public LineData(string text, Col color) { Text = text; Color = color; }
    }

    enum Col { White, Title, Info, Warn, Dim }
}

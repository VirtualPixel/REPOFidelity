using System.Collections;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.UI;

namespace REPOFidelity.Patches;

// Ultrawide support: aspect-aware FOV defaults, full-screen world view via an underlay
// canvas, menu-camera narrowing, F10 vanilla-16:9 compare. HOR+ horizontal expansion is
// automatic from Camera.aspect tracking Screen.width/height; vertical FOV is what we
// tune.

internal static class CameraZoomFovOverride
{
    static readonly Dictionary<CameraZoom, float> _originals = new();
    static readonly Dictionary<CameraZoom, Coroutine> _activeAnims = new();
    const float AnimDuration = 0.25f;

    internal static void Apply(CameraZoom cz)
    {
        if (cz == null) return;
        if (VRCompat.Active) return; // VR owns the FOV; the headset sets it per eye
        if (!Settings.ModEnabled) { Restore(cz); return; }

        if (!_originals.ContainsKey(cz))
            _originals[cz] = cz.playerZoomDefault;

        // Slider override (>0) wins. Otherwise pure HOR+: vanilla vertical FOV at
        // any aspect, capped only at extreme widths.
        float target = Settings.VerticalFovOverride > 0
            ? Settings.VerticalFovOverride
            : ComputeAspectAwareDefault(_originals[cz]);

        float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 0f;
        float hFov = aspect > 0f
            ? 2f * Mathf.Atan(Mathf.Tan(target * Mathf.Deg2Rad / 2f) * aspect) * Mathf.Rad2Deg : 0f;
        Plugin.Log.LogDebug($"[fov] gameplay vFOV {_originals[cz]:F0} -> {target:F0} " +
            $"(slider={Settings.VerticalFovOverride}, aspect={aspect:F3}, hFOV={hFov:F0})");
        StartFovAnim(cz, target);
    }

    // Pure HOR+: keep the vanilla vertical FOV so wider panels reveal more world at
    // the sides with the same center framing. The old default ramped vertical FOV
    // to 80 at 21:9 on top of the horizontal gain, putting horizontal FOV near 127
    // degrees; rectilinear projection smears the outer screen so badly there that
    // the genuinely-new side content reads as pure stretch. Only intervene when
    // plain HOR+ would push horizontal FOV past 120 degrees (32:9 territory):
    // lower vertical FOV just enough to hold that cap.
    internal static float ComputeAspectAwareDefault(float vanilla16x9Fov)
    {
        if (Screen.height == 0) return vanilla16x9Fov;
        float aspect = (float)Screen.width / Screen.height;
        const float maxHFovRad = 120f * Mathf.Deg2Rad;
        float vFovRad = vanilla16x9Fov * Mathf.Deg2Rad;
        float hFovRad = 2f * Mathf.Atan(Mathf.Tan(vFovRad / 2f) * aspect);
        if (hFovRad <= maxHFovRad) return vanilla16x9Fov;
        return 2f * Mathf.Atan(Mathf.Tan(maxHFovRad / 2f) / aspect) * Mathf.Rad2Deg;
    }

    internal static void Restore(CameraZoom cz)
    {
        if (cz == null) return;
        if (_originals.TryGetValue(cz, out var orig))
            StartFovAnim(cz, orig);
    }

    internal static void RestoreAll()
    {
        foreach (var kv in _originals)
            if (kv.Key != null) StartFovAnim(kv.Key, kv.Value);
    }

    internal static void RefreshAll()
    {
        foreach (var cz in Object.FindObjectsOfType<CameraZoom>())
            Apply(cz);
    }

    // CameraZoom.Update only advances zoomLerp during sprint/tumble override events, so
    // after a settings change the FOV would stay frozen until the next such event. Drive
    // the lerp state from a coroutine instead.
    static void StartFovAnim(CameraZoom cz, float targetFov)
    {
        if (Plugin.Instance == null) { WriteFovImmediate(cz, targetFov); return; }
        if (_activeAnims.TryGetValue(cz, out var existing) && existing != null)
            Plugin.Instance.StopCoroutine(existing);

        float startFov = cz.playerZoomDefault;
        if (Mathf.Approximately(startFov, targetFov)) return;

        _activeAnims[cz] = Plugin.Instance.StartCoroutine(AnimateFov(cz, startFov, targetFov, AnimDuration));
    }

    static void WriteFovImmediate(CameraZoom cz, float fov)
    {
        cz.playerZoomDefault = fov;
        cz.zoomPrev = fov;
        cz.zoomNew = fov;
        cz.zoomLerp = 1f;
    }

    static IEnumerator AnimateFov(CameraZoom cz, float startFov, float targetFov, float duration)
    {
        float t = 0f;
        while (t < duration && cz != null)
        {
            t += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / duration));
            WriteFovImmediate(cz, Mathf.Lerp(startFov, targetFov, k));
            yield return null;
        }
        if (cz != null)
        {
            WriteFovImmediate(cz, targetFov);
            _activeAnims.Remove(cz);
        }
    }
}

[HarmonyPatch(typeof(CameraZoom), "Awake")]
internal static class CameraZoomAwakePatch
{
    [HarmonyPostfix]
    static void Postfix(CameraZoom __instance) => CameraZoomFovOverride.Apply(__instance);
}

internal static class MenuCameraFovOverride
{
    static Camera? _trackedCam;
    static float _vanillaFov;
    static bool _captured;

    internal static void Apply()
    {
        if (VRCompat.Active) return; // VR owns the FOV; the headset sets it per eye
        if (!Settings.ModEnabled) { Restore(); return; }
        var noTarget = CameraNoPlayerTarget.instance;
        if (noTarget == null || noTarget.cam == null) return;
        var cam = noTarget.cam;

        // Capturing 0 here (Awake postfix racing the prefab init) yields a pinhole FOV
        // and a black render.
        if (cam.fieldOfView < 5f) return;

        if (!_captured || _trackedCam != cam)
        {
            _trackedCam = cam;
            _vanillaFov = cam.fieldOfView;
            _captured = true;
        }

        float corrected = ApplyAspectCorrection(_vanillaFov);
        if (!Mathf.Approximately(cam.fieldOfView, corrected))
            cam.fieldOfView = corrected;
    }

    internal static void Restore()
    {
        if (_captured && _trackedCam != null)
            _trackedCam.fieldOfView = _vanillaFov;
        _captured = false;
        _trackedCam = null;
    }

    // Full VERT- on the menu camera: vFOV reduced so the horizontal span matches
    // the 16:9 framing the title set was built for. The menu set has hard edges
    // (black void past the backdrop); any extra horizontal reveal shows them as a
    // hard stop line. The old partial 0.51 strength left exactly that visible at
    // 21:9. Menu framing is static art, so cropping a little vertical costs
    // nothing.
    static float ApplyAspectCorrection(float baseFov)
    {
        if (Screen.height == 0) return baseFov;
        float aspect = (float)Screen.width / Screen.height;
        const float refAspect = 16f / 9f;
        if (aspect <= refAspect + 0.01f) return baseFov;

        // target_vFov = 2 * atan(tan(baseFov/2) * refAspect / aspect)
        float baseRad = baseFov * Mathf.Deg2Rad;
        return 2f * Mathf.Atan(Mathf.Tan(baseRad / 2f) * refAspect / aspect) * Mathf.Rad2Deg;
    }
}

[HarmonyPatch(typeof(CameraNoPlayerTarget), "Awake")]
internal static class CameraNoPlayerTargetAwakePatch
{
    [HarmonyPostfix]
    static void Postfix() => MenuCameraFovOverride.Apply();
}

// Tightens fog on the main-menu truck scene at wide aspects. The truck assets are
// despawned at fixed boundaries (rolling treadmill), not culled by camera distance, so
// adjusting cull / LOD / farClipPlane does nothing for the popping. Stronger fog moves
// the visible fade ahead of the despawn point so the disappearance happens inside fog.
internal static class UltrawideMenuTweaks
{
    const float FogMult = 0.60f;
    const int StabilizationFrames = 30;

    static bool _applied;
    static Camera? _trackedCam;
    static float _vanillaFogStart;
    static float _vanillaFogEnd;
    static int _menuFrames;

    internal static void Tick()
    {
        var menu = CameraNoPlayerTarget.instance;
        var cam = menu != null ? menu.cam : null;

        bool shouldApply = Settings.ModEnabled
                           && Settings.UltrawideUiFix
                           && Screen.height > 0
                           && (float)Screen.width / Screen.height > 16f / 9f + 0.01f
                           && SemiFunc.MenuLevel()
                           && cam != null;

        if (shouldApply)
        {
            _menuFrames++;
            // Wait for the menu scene's lighting to settle before sampling vanilla fog.
            // Reading too early can hit fogStart/End = 0; fog * 0.60 = 0 makes fog dense
            // at zero distance and the screen fills with the fog colour (white on menu).
            if (_menuFrames < StabilizationFrames) return;
            if (!_applied || _trackedCam != cam) ApplyOnce(cam!);
            EnforceEachFrame();
            return;
        }

        _menuFrames = 0;

        if (_applied)
        {
            // The main menu shares its Unity scene with gameplay, so RenderSettings.fog
            // is global. Writing menu vanilla back on a context change (entering a level,
            // menu camera destroyed) bleeds menu fog onto gameplay. Restore only when
            // the same menu camera is still active; otherwise drop our state silently.
            bool stillInMenuContext = SemiFunc.MenuLevel() && cam != null && cam == _trackedCam;
            if (stillInMenuContext)
                Restore();
            else
                ClearWithoutRestoringRenderSettings();
        }
    }

    static void ApplyOnce(Camera cam)
    {
        float vanillaStart = RenderSettings.fogStartDistance;
        float vanillaEnd = RenderSettings.fogEndDistance;
        if (vanillaEnd <= 1f) return;

        _trackedCam = cam;
        _vanillaFogStart = vanillaStart;
        _vanillaFogEnd = vanillaEnd;
        _applied = true;
    }

    static void EnforceEachFrame()
    {
        RenderSettings.fogStartDistance = _vanillaFogStart * FogMult;
        RenderSettings.fogEndDistance = _vanillaFogEnd * FogMult;
    }

    static void Restore()
    {
        RenderSettings.fogStartDistance = _vanillaFogStart;
        RenderSettings.fogEndDistance = _vanillaFogEnd;
        _applied = false;
        _trackedCam = null;
    }

    static void ClearWithoutRestoringRenderSettings()
    {
        _applied = false;
        _trackedCam = null;
    }
}

// F10 "vanilla 16:9" comparison view. Toggles UltrawideUiFix off (so RestoreAll
// re-enables the game's full-screen black Background and the inner 16:9 mainImage) and
// forces every camera's aspect to 16:9 so world content renders un-squashed into the
// 16:9 RT. Camera.aspect is per-camera and per-frame-enforced because game scripts can
// write it from Screen.aspect.
internal static class UltrawideCompareResolution
{
    static bool _active;
    static bool _savedUiFix;

    internal static bool IsCompareActive => _active;

    internal static void HandleToggle(bool enabling)
    {
        if (Screen.height <= 0) return;
        float aspect = (float)Screen.width / Screen.height;
        const float refAspect = 16f / 9f;

        if (!enabling)
        {
            if (_active) return;
            if (aspect <= refAspect + 0.01f) return;

            _savedUiFix = Settings.UltrawideUiFix;
            Settings.UltrawideUiFix = false;
            ApplyAspectOverrideToAllCameras(refAspect);
            _active = true;
        }
        else
        {
            if (!_active) return;
            ResetAspectOnAllCameras();
            Settings.UltrawideUiFix = _savedUiFix;
            _active = false;
        }
    }

    internal static void TickEnforcement()
    {
        if (!_active) return;
        ApplyAspectOverrideToAllCameras(16f / 9f);
    }

    static void ApplyAspectOverrideToAllCameras(float aspect)
    {
        if (Camera.main != null) Camera.main.aspect = aspect;
        var rtm = RenderTextureMain.instance;
        if (rtm != null && rtm.cameras != null)
            foreach (var c in rtm.cameras)
                if (c != null) c.aspect = aspect;
        var noTarget = CameraNoPlayerTarget.instance;
        if (noTarget != null && noTarget.cam != null)
            noTarget.cam.aspect = aspect;
    }

    static void ResetAspectOnAllCameras()
    {
        if (Camera.main != null) Camera.main.ResetAspect();
        var rtm = RenderTextureMain.instance;
        if (rtm != null && rtm.cameras != null)
            foreach (var c in rtm.cameras)
                if (c != null) c.ResetAspect();
        var noTarget = CameraNoPlayerTarget.instance;
        if (noTarget != null && noTarget.cam != null)
            noTarget.cam.ResetAspect();
    }
}

// HUD-unstretch cursor compensation (active only while OverlayCameraWiden is
// applied): with the overlay camera framing the panel aspect, the HUD canvas
// renders into the central 16:9 region of the overlay texture, so it DISPLAYS as
// a centered 16:9 region of the screen, while the game converts mouse to
// HUD-canvas coords assuming the HUD spans the full screen. Both converters
// (SemiFunc.UIMousePosToUIPos, UIPositionToUIPosition) are linear in the input
// screen coords, so remapping the input from the displayed region to the full
// virtual screen is exact regardless of the magic constants downstream, and the
// output is clamped so the cursor parks visibly at the HUD edge instead of
// leaving the canvas and vanishing. The reverse helper
// UIGetRectTransformPositionOnScreen works purely in canvas space, needs nothing.
internal static class HudCursorRemap
{
    internal static bool Active;

    internal static Vector3 Correct(Vector3 screenPos)
    {
        if (!Active || Screen.height == 0) return screenPos;
        float aspect = (float)Screen.width / Screen.height;
        const float refAspect = 16f / 9f;
        // No clamp: the widened capture renders UI beyond the canvas edges too,
        // so coords past the virtual range put the cursor in the side regions,
        // 1:1 under the mouse across the whole panel. (A clamp was needed when
        // the display was a centered box and the side capture wasn't shown.)
        if (aspect > refAspect + 0.01f)
        {
            float boxW = Screen.height * refAspect;
            float left = (Screen.width - boxW) * 0.5f;
            screenPos.x = (screenPos.x - left) * (Screen.width / boxW);
        }
        else if (aspect < refAspect - 0.01f)
        {
            float boxH = Screen.width / refAspect;
            float bottom = (Screen.height - boxH) * 0.5f;
            screenPos.y = (screenPos.y - bottom) * (Screen.height / boxH);
        }
        return screenPos;
    }

    internal static Vector3 CorrectedMousePosition() => Correct(Input.mousePosition);
}

// UIMousePosToUIPos reads Input.mousePosition directly, so the input correction is
// injected by swapping that property read for CorrectedMousePosition. A no-op when
// HudCursorRemap.Active is false, so 16:9 players and the classic stretch mode run
// vanilla math untouched.
[HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.UIMousePosToUIPos))]
internal static class UIMousePosRemapPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMouse = AccessTools.PropertyGetter(typeof(Input), nameof(Input.mousePosition));
        var corrected = AccessTools.Method(typeof(HudCursorRemap), nameof(HudCursorRemap.CorrectedMousePosition));
        int swapped = 0;
        foreach (var code in instructions)
        {
            if (code.opcode == OpCodes.Call && Equals(code.operand, getMouse))
            {
                yield return new CodeInstruction(OpCodes.Call, corrected)
                {
                    labels = code.labels,
                    blocks = code.blocks,
                };
                swapped++;
                continue;
            }
            yield return code;
        }
        if (swapped == 0)
            Plugin.Log.LogWarning("[ultrawide] UIMousePosToUIPos transpiler matched nothing; " +
                "HUD-unstretch cursor remap is inactive this session. Report this if it appears after a game update.");
    }
}

[HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.UIPositionToUIPosition))]
internal static class UIPositionRemapPatch
{
    static void Prefix(ref Vector3 position) => position = HudCursorRemap.Correct(position);
}

// Grounded in the runtime pipeline dump (2026-06-12): every visible HUD element,
// the menus and the cursor live on the world-space HUD Canvas (712x400), captured
// by the ORTHO overlay camera (size 200, target = the 16:9 overlay texture) whose
// framing exactly fits that canvas; the vignette comes from a PostProcessVolume on
// that camera, so it's baked into the texture in FRAME space. Vanilla letterboxes
// the overlay texture; our underlay displays it stretched full-screen, and THAT
// stretch is the HUD distortion users report. Fix at the camera: override its
// aspect (and size, on narrower panels, so the canvas isn't cropped) to match the
// panel. The canvas then renders pre-squeezed into the texture and the display
// stretch cancels exactly, while the frame-space vignette spans the full panel
// with no seam. No transform, RT, or canvas is touched. Enforced per frame from
// UpscalerManager: the canvas-squeeze attempt died to a boot-order race where its
// one-shot apply no-opped while the cursor remap stayed on; a per-frame tick that
// derives BOTH from the same condition can't desynchronize like that.
internal static class OverlayCameraWiden
{
    static Camera? _cam;
    static float _vanillaSize;
    static bool _applied;
    static bool _panelLogged;

    internal static void Tick()
    {
        // One-shot panel report at Info: proves which build is running and what
        // aspect the game window actually reports, even when the widen never
        // engages. Field-debug anchor for shared-profile machines.
        if (!_panelLogged && Screen.height > 0)
        {
            _panelLogged = true;
            Plugin.Log.LogInfo($"[ultrawide] panel {Screen.width}x{Screen.height} " +
                $"aspect={(float)Screen.width / Screen.height:F3} " +
                $"requiresFix={UltrawideCanvasFix.RequiresAspectFix()}");
        }

        var cam = CameraOverlay.instance != null ? CameraOverlay.instance.overlayCamera : null;
        if (cam == null)
        {
            _cam = null;
            _applied = false;
            HudCursorRemap.Active = false;
            return;
        }
        if (_cam != cam)
        {
            _cam = cam;
            _vanillaSize = cam.orthographicSize;
            _applied = false;
        }

        // UnderlayActive matters during boot: the capture must only widen once the
        // full-screen mirror actually displays it. Widening against the game's
        // vanilla boxed display shows a squeezed HUD through the whole boot
        // sequence (splash, loading) until the first menu refresh builds the
        // underlay.
        bool want = Settings.ModEnabled
                    && Settings.UltrawideUiFix
                    && Settings.UltrawideHudUnstretch
                    && !VRCompat.Active
                    && !UltrawideCompareResolution.IsCompareActive
                    && UltrawideCanvasFix.RequiresAspectFix()
                    && UltrawideCanvasFix.UnderlayActive
                    && Screen.height > 0;

        if (want)
        {
            const float refAspect = 16f / 9f;
            float aspect = (float)Screen.width / Screen.height;
            // Wider panels: hold the vertical size, the extra aspect widens the
            // capture. Narrower panels: hold the horizontal half-span instead
            // (size grows), or the canvas edges would crop out of frame.
            float size = Mathf.Max(_vanillaSize, _vanillaSize * refAspect / aspect);
            if (!Mathf.Approximately(cam.aspect, aspect)) cam.aspect = aspect;
            if (!Mathf.Approximately(cam.orthographicSize, size)) cam.orthographicSize = size;
            _applied = true;
        }
        else if (_applied)
        {
            cam.orthographicSize = _vanillaSize;
            cam.ResetAspect();
            _applied = false;
        }
        HudCursorRemap.Active = _applied;
        HudCoverStretch.Tick(_applied);
        MenuEdgeArtExtend.Tick(_applied);
        HudParkedShift.Tick(_applied);
        RevealGuard.Tick(_applied);
    }
}

// The general "cover" problem: the game draws full-canvas cover art all over
// the HUD canvas (the boot/transition Fade, the main menu's vignette gradient,
// the pause menu's black dim, the splash background, the hurt vignette, level
// loading covers...). All of it is sized to the canvas, so under the widened
// capture it only covers the central 16:9 and its edge reads as a hard line
// with the world visible past it. There is no per-object fix that survives
// game updates, so this is a CLASSIFIER: anything under the HUD canvas whose
// footprint spans the whole canvas AND is identifiably cover art (a solid
// color with no sprite, or art named like a vignette/fade/background) gets
// stretched to the capture, remembered, and restored when the widen drops.
// Rescanned on a slow cadence and on every menu page open, so covers that
// spawn later are caught without naming them individually. Positioned HUD
// elements and big DRAWN art (the result-screen truck) are excluded by the
// classification, so nothing visibly authored ever distorts.
internal static class HudCoverStretch
{
    static readonly List<(RectTransform Rect, Vector3 Vanilla)> _stretched = new();
    static float _rescanTimer;
    static bool _active;

    internal static void Tick(bool widenActive)
    {
        if (widenActive && !_active)
        {
            _active = true;
            _rescanTimer = 0f;
            Rescan();
        }
        else if (!widenActive && _active)
        {
            _active = false;
            RestoreAll();
        }
        else if (_active)
        {
            // Tight cadence: loading covers spawn in bursts and a slow rescan
            // shows them unstretched for a beat (reported as quick artifacts
            // outside 16:9 on level load).
            _rescanTimer += Time.unscaledDeltaTime;
            if (_rescanTimer >= 0.4f)
            {
                _rescanTimer = 0f;
                Rescan();
            }
        }
    }

    internal static void Rescan()
    {
        if (!_active || Screen.height == 0) return;
        var hud = HUDCanvas.instance;
        if (hud == null || hud.rect == null) return;

        const float refAspect = 16f / 9f;
        float aspect = (float)Screen.width / Screen.height;
        float xFactor = aspect > refAspect ? aspect / refAspect : 1f;
        float yFactor = aspect < refAspect ? refAspect / aspect : 1f;
        if (xFactor <= 1f && yFactor <= 1f) return;

        foreach (var g in hud.rect.GetComponentsInChildren<Graphic>(true))
        {
            if (g is not Image && g is not RawImage) continue;
            var rt = g.rectTransform;
            if (!IsCover(g, rt)) continue;
            bool seen = false;
            foreach (var (r, _) in _stretched)
                if (r == rt) { seen = true; break; }
            if (seen) continue;
            var s = rt.localScale;
            _stretched.Add((rt, s));
            rt.localScale = new Vector3(s.x * xFactor, s.y * yFactor, s.z);
            Plugin.Log.LogDebug($"[ultrawide] cover stretched: {g.gameObject.name} ({ArtName(g)})");
        }
    }

    // A cover spans the whole canvas (footprint in canvas units; the HUD
    // canvas is world-space at scale 1, so rect * lossyScale lands in canvas
    // units) and is either a bare solid color or art named like a cover.
    // Footprint excludes positioned elements; the name/sprite test excludes
    // big authored art like the result-screen truck sprites.
    static bool IsCover(Graphic g, RectTransform rt)
    {
        float w = rt.rect.width * Mathf.Abs(rt.lossyScale.x);
        float h = rt.rect.height * Mathf.Abs(rt.lossyScale.y);
        if (w < 600f || h < 290f) return false;
        string art = ArtName(g);
        if (art.Length == 0) return true; // bare solid color = a dim/fade by construction
        string n = (g.gameObject.name + "|" + art).ToLowerInvariant();
        return n.Contains("vignette") || n.Contains("fade") || n.Contains("background")
            || n.Contains("gradient") || n.Contains("dark") || n.Contains("dim");
    }

    static string ArtName(Graphic g)
    {
        if (g is Image img) return img.sprite != null ? img.sprite.name : "";
        if (g is RawImage raw) return raw.texture != null ? raw.texture.name : "";
        return "";
    }

    internal static bool Manages(Transform t)
    {
        foreach (var (rt, _) in _stretched)
            if (rt != null && (t == rt || t.IsChildOf(rt))) return true;
        return false;
    }

    static void RestoreAll()
    {
        foreach (var (rt, vanilla) in _stretched)
            if (rt != null) rt.localScale = vanilla;
        _stretched.Clear();
    }
}

// The main menu's dark side gradient ("Background", sprite "gradient") is NOT a
// cover: it is positioned art, full canvas height but anchored to the left half
// of the canvas (spans the canvas left edge to just past center). Under the
// widened capture it stops short of the capture edge and leaves a bare strip
// where the vignette visibly cuts off. A center stretch would shift its falloff
// across the screen, so instead it gets extended toward the capture edge with
// the OPPOSITE edge held fixed: scale around the far edge, compensate the
// anchored position. Classified, not named: any full-height gradient art that
// touches exactly one horizontal canvas edge gets the same treatment.
internal static class MenuEdgeArtExtend
{
    static readonly List<(RectTransform Rect, Vector3 Scale, Vector2 Pos)> _extended = new();
    static float _rescanTimer;
    static bool _active;

    internal static void Tick(bool widenActive)
    {
        if (widenActive && !_active)
        {
            _active = true;
            _rescanTimer = 0f;
            Rescan();
        }
        else if (!widenActive && _active)
        {
            _active = false;
            RestoreAll();
        }
        else if (_active)
        {
            _rescanTimer += Time.unscaledDeltaTime;
            if (_rescanTimer >= 0.4f)
            {
                _rescanTimer = 0f;
                Rescan();
            }
        }
    }

    internal static void Rescan()
    {
        if (!_active || Screen.height == 0) return;
        var hud = HUDCanvas.instance;
        if (hud == null || hud.rect == null) return;

        const float refAspect = 16f / 9f;
        float aspect = (float)Screen.width / Screen.height;
        float xFactor = aspect > refAspect ? aspect / refAspect : 1f;
        float yFactor = aspect < refAspect ? refAspect / aspect : 1f;
        if (xFactor <= 1f && yFactor <= 1f) return;

        float canvasHalfW = hud.rect.sizeDelta.x * 0.5f;
        float canvasH = hud.rect.sizeDelta.y;
        float captureHalfW = canvasHalfW * xFactor;

        foreach (var g in hud.rect.GetComponentsInChildren<Graphic>(true))
        {
            if (g is not Image && g is not RawImage) continue;
            var rt = g.rectTransform;
            // point anchors and centered pivot only; the shift math below assumes
            // localScale grows the rect symmetrically around its center
            if (rt.anchorMin.x != rt.anchorMax.x) continue;
            if (!Mathf.Approximately(rt.pivot.x, 0.5f) || !Mathf.Approximately(rt.pivot.y, 0.5f)) continue;

            string art = g is Image img ? (img.sprite != null ? img.sprite.name : "")
                       : g is RawImage raw ? (raw.texture != null ? raw.texture.name : "") : "";
            if (!(g.gameObject.name + "|" + art).ToLowerInvariant().Contains("gradient")) continue;

            float w = rt.rect.width * Mathf.Abs(rt.lossyScale.x);
            float h = rt.rect.height * Mathf.Abs(rt.lossyScale.y);
            if (w < 100f || w >= 600f) continue;        // covers are HudCoverStretch's job
            if (h < canvasH * 0.9f) continue;            // full-height art only

            // Classify in parent-relative coords, NOT canvas space: menu pages
            // slide in when opened, and a canvas-space scan caught mid-slide once
            // classified the left gradient as RIGHT-touching (measured span
            // [0,478]). anchoredPosition doesn't move during the page slide; at
            // rest the page is centered, so these ARE canvas units then. Skip
            // scaled parents where that equivalence breaks.
            if (rt.parent is RectTransform pr && Mathf.Abs(pr.lossyScale.x - 1f) > 0.05f) continue;
            Vector2 center = rt.anchoredPosition;
            float leftEdge = center.x - w * 0.5f;
            float rightEdge = center.x + w * 0.5f;
            bool touchesLeft = leftEdge <= -canvasHalfW + 8f;
            bool touchesRight = rightEdge >= canvasHalfW - 8f;
            if (touchesLeft == touchesRight) continue;   // both = cover, neither = floating

            bool seen = false;
            foreach (var (r, _, _) in _extended)
                if (r == rt) { seen = true; break; }
            if (seen) continue;

            var s = rt.localScale;
            var p = rt.anchoredPosition;
            _extended.Add((rt, s, p));

            if (xFactor > 1f)
            {
                // grow toward the touched edge, hold the far edge in place.
                // Overshoot past the capture edge: the gradient sprite's leading
                // texel column is soft (one texel is ~7 canvas units at this
                // stretch), and it left a thin bright strip when the rect landed
                // exactly on the edge. The overshoot region is off-display.
                const float overshoot = 16f;
                float f = touchesLeft ? (rightEdge + captureHalfW + overshoot) / w
                                      : (captureHalfW + overshoot - leftEdge) / w;
                float shift = (f - 1f) * w * 0.5f;
                rt.localScale = new Vector3(s.x * f, s.y * yFactor, s.z);
                rt.anchoredPosition = new Vector2(p.x + (touchesLeft ? -shift : shift), p.y);
            }
            else
            {
                rt.localScale = new Vector3(s.x, s.y * yFactor, s.z);
            }

            // measured post-apply reach (parent-relative = canvas units at page
            // rest), so a persisting bar shows WHERE the extension landed
            float wAfter = rt.rect.width * Mathf.Abs(rt.lossyScale.x);
            float cAfter = rt.anchoredPosition.x;
            Plugin.Log.LogDebug($"[ultrawide] edge art extended: {g.gameObject.name} ({art})" +
                $" span=[{cAfter - wAfter * 0.5f:F1},{cAfter + wAfter * 0.5f:F1}] capture=[{-captureHalfW:F1},{captureHalfW:F1}]");
        }
    }

    internal static bool Manages(Transform t)
    {
        foreach (var (rt, _, _) in _extended)
            if (rt != null && (t == rt || t.IsChildOf(rt))) return true;
        return false;
    }

    static void RestoreAll()
    {
        foreach (var (rt, scale, pos) in _extended)
        {
            if (rt == null) continue;
            rt.localScale = scale;
            rt.anchoredPosition = pos;
        }
        _extended.Clear();
    }
}

// SemiUI "hides" HUD elements by parking them at hidePosition just OUTSIDE the
// canvas, children still active (deactivation only happens after a full
// show/hide cycle; elements that never get Show()n, like the arena race timer
// on the title screen, sit parked and alive forever). Vanilla never renders
// past the canvas, but the widened capture does: on narrow panels the extra
// VERTICAL headroom exposed the whole parking lot (friend's 5:4 report). Push
// every off-canvas parked spot outward by the capture's extra margin so
// elements clear the capture edge with the same clearance they had against the
// canvas edge. The hide/show woosh animations are untouched, just longer.
internal static class HudParkedShift
{
    static readonly List<(SemiUI Ui, Vector2 Hide)> _shifted = new();
    // Field-debug bookkeeping: classify every SemiUI once at Info so a user's
    // default LogOutput.log shows whether the shift engaged on their machine
    // (LogDebug never reaches the disk log on a default BepInEx config).
    static readonly HashSet<SemiUI> _classified = new();
    static readonly HashSet<SemiUI> _pendingLogged = new();
    static bool _summaryLogged;
    static float _rescanTimer;
    static bool _active;

    internal static void Tick(bool widenActive)
    {
        if (widenActive && !_active)
        {
            _active = true;
            _rescanTimer = 0f;
            Rescan();
        }
        else if (!widenActive && _active)
        {
            _active = false;
            RestoreAll();
        }
        else if (_active)
        {
            _rescanTimer += Time.unscaledDeltaTime;
            if (_rescanTimer >= 0.4f)
            {
                _rescanTimer = 0f;
                Rescan();
            }
        }
    }

    internal static void Rescan()
    {
        if (!_active || Screen.height == 0) return;
        var hud = HUDCanvas.instance;
        if (hud == null || hud.rect == null) return;

        const float refAspect = 16f / 9f;
        float aspect = (float)Screen.width / Screen.height;
        float xFactor = aspect > refAspect ? aspect / refAspect : 1f;
        float yFactor = aspect < refAspect ? refAspect / aspect : 1f;
        if (xFactor <= 1f && yFactor <= 1f) return;

        float halfW = hud.rect.sizeDelta.x * 0.5f;
        float halfH = hud.rect.sizeDelta.y * 0.5f;
        float extX = halfW * (xFactor - 1f) + 8f;
        float extY = halfH * (yFactor - 1f) + 8f;

        if (!_summaryLogged)
        {
            _summaryLogged = true;
            Plugin.Log.LogInfo($"[ultrawide] parked-shift scan: aspect={aspect:F3} " +
                $"xF={xFactor:F3} yF={yFactor:F3} canvas={hud.rect.sizeDelta.x:F0}x{hud.rect.sizeDelta.y:F0} " +
                $"ext=({extX:F1},{extY:F1})");
        }

        foreach (var ui in hud.rect.GetComponentsInChildren<SemiUI>(true))
        {
            if (ui.allChildren == null)
            {
                // Start hasn't run; hidePosition is still a raw offset. The 0.4s
                // rescan picks it up once Start parks it.
                if (_pendingLogged.Add(ui))
                    Plugin.Log.LogInfo($"[ultrawide] parked-shift: {ui.gameObject.name} start pending");
                continue;
            }
            bool seen = false;
            foreach (var (u, _) in _shifted)
                if (u == ui) { seen = true; break; }
            if (seen) continue;

            var mover = ui.animateTheEntireObject ? ui.transform : ui.textRectTransform;
            if (mover == null || mover.parent == null)
            {
                if (_classified.Add(ui))
                    Plugin.Log.LogInfo($"[ultrawide] parked-shift: {ui.gameObject.name} no mover " +
                        $"(animateEntire={ui.animateTheEntireObject})");
                continue;
            }

            Vector2 parked = hud.rect.InverseTransformPoint(
                mover.parent.TransformPoint(new Vector3(ui.hidePosition.x, ui.hidePosition.y, 0f)));

            // push only along axes where the parked point already cleared the canvas
            // edge; parked-inside elements (hide-in-place, shrink hides) stay put
            var push = Vector2.zero;
            if (xFactor > 1f)
            {
                if (parked.x >= halfW) push.x = extX;
                else if (parked.x <= -halfW) push.x = -extX;
            }
            if (yFactor > 1f)
            {
                if (parked.y >= halfH) push.y = extY;
                else if (parked.y <= -halfH) push.y = -extY;
            }
            if (push == Vector2.zero)
            {
                if (_classified.Add(ui))
                    Plugin.Log.LogInfo($"[ultrawide] parked-shift: {ui.gameObject.name} inside " +
                        $"parked=({parked.x:F1},{parked.y:F1}) half=({halfW:F0},{halfH:F0})");
                continue;
            }

            Vector2 localPush = mover.parent.InverseTransformVector(
                hud.rect.TransformVector(new Vector3(push.x, push.y, 0f)));

            _shifted.Add((ui, ui.hidePosition));
            bool atPark = ui.hidePositionCurrent == ui.hidePosition;
            ui.hidePosition += localPush;
            if (atPark) ui.hidePositionCurrent = ui.hidePosition;
            _classified.Add(ui);
            Plugin.Log.LogInfo($"[ultrawide] parked-shift: {ui.gameObject.name} shifted " +
                $"push=({push.x:F1},{push.y:F1}) parked=({parked.x:F1},{parked.y:F1}) atPark={atPark}");
        }
    }

    static void RestoreAll()
    {
        foreach (var (ui, hide) in _shifted)
        {
            if (ui == null) continue;
            bool atPark = ui.hidePositionCurrent == ui.hidePosition;
            ui.hidePosition = hide;
            if (atPark) ui.hidePositionCurrent = hide;
        }
        _shifted.Clear();
        _classified.Clear();
        _pendingLogged.Clear();
        _summaryLogged = false;
    }
}

// The widened capture renders HUD-canvas space that vanilla never shows, and
// anything a mod (or the game) leaves out there becomes visible: third-party
// chat windows placed by raw coordinates, art staged off-canvas, parking spots
// our SemiUI shift doesn't know about. Principle: a Graphic whose rect lies
// ENTIRELY outside the vanilla canvas is invisible on every 16:9 machine by
// construction, so hiding it always restores vanilla appearance. Cull those at
// the CanvasRenderer (no component state touched). Anything culled that starts
// moving or rescaling is unculled the same frame, so woosh-in animations and
// sliding menu pages are never eaten; the next scan re-evaluates wherever it
// settles. Partial spills stay untouched (their on-canvas part is legit) but
// get logged once at Info, same as culls, so a default LogOutput.log from a
// machine we can't touch names every revealed element.
internal static class RevealGuard
{
    static readonly List<(Graphic G, Vector3 Pos, Vector3 Scale, SemiUI? Semi)> _culled = new();
    static readonly HashSet<int> _logged = new();
    static readonly Vector3[] _corners = new Vector3[4];
    static float _timer;
    static bool _active;

    internal static void Tick(bool widenActive)
    {
        if (widenActive && !_active)
        {
            _active = true;
            _timer = 0f;
            Scan();
        }
        else if (!widenActive && _active)
        {
            _active = false;
            UncullAll();
        }
        else if (_active)
        {
            WatchCulled();
            _timer += Time.unscaledDeltaTime;
            if (_timer >= 1f)
            {
                _timer = 0f;
                Scan();
            }
        }
    }

    // Uncull the moment a culled element moves, rescales, or deactivates; a
    // 1s scan cadence is far too slow to hand a shown element back.
    static void WatchCulled()
    {
        for (int i = _culled.Count - 1; i >= 0; i--)
        {
            var (g, pos, scale, semi) = _culled[i];
            if (g == null)
            {
                _culled.RemoveAt(i);
                continue;
            }
            // A parked-SemiUI cull is released the instant the owner leaves its hide
            // anchor (it has started animating open); the child Graphic's own
            // localPosition never moves on a show, so the parent has to be watched.
            bool unparked = semi != null
                && ((Vector2)semi.transform.localPosition - semi.hidePosition).sqrMagnitude >= 1f;
            var t = g.rectTransform;
            if (!g.isActiveAndEnabled || t.localPosition != pos || t.localScale != scale || unparked)
            {
                g.canvasRenderer.cull = false;
                _culled.RemoveAt(i);
            }
        }
    }

    static void Scan()
    {
        var hud = HUDCanvas.instance;
        if (hud == null || hud.rect == null || Screen.height == 0) return;

        const float refAspect = 16f / 9f;
        float aspect = (float)Screen.width / Screen.height;
        float xFactor = aspect > refAspect ? aspect / refAspect : 1f;
        float yFactor = aspect < refAspect ? refAspect / aspect : 1f;
        if (xFactor <= 1f && yFactor <= 1f) return;

        float halfW = hud.rect.sizeDelta.x * 0.5f;
        float halfH = hud.rect.sizeDelta.y * 0.5f;
        float capW = halfW * xFactor;
        float capH = halfH * yFactor;

        foreach (var g in hud.rect.GetComponentsInChildren<Graphic>(false))
        {
            if (!g.enabled || g.color.a < 0.01f) continue;

            var rt = g.rectTransform;
            rt.GetWorldCorners(_corners);
            Vector2 min = new(float.MaxValue, float.MaxValue);
            Vector2 max = new(float.MinValue, float.MinValue);
            for (int i = 0; i < 4; i++)
            {
                Vector2 c = hud.rect.InverseTransformPoint(_corners[i]);
                min = Vector2.Min(min, c);
                max = Vector2.Max(max, c);
            }

            // inside the vanilla canvas: visible on every aspect, not ours
            bool outsideCanvas = min.x < -halfW - 1f || max.x > halfW + 1f
                              || min.y < -halfH - 1f || max.y > halfH + 1f;
            if (!outsideCanvas) continue;
            // entirely past the capture too: still invisible, ignore
            bool inCapture = min.x < capW && max.x > -capW && min.y < capH && max.y > -capH;
            if (!inCapture) continue;

            if (HudCoverStretch.Manages(rt) || MenuEdgeArtExtend.Manages(rt)) continue;
            // The mouse cursor follows the pointer across the whole panel: HudCursorRemap
            // runs it 1:1 under the mouse, including past the vanilla canvas edges, so it
            // is genuinely visible wherever it parks on a widened display. The
            // "outside the canvas = invisible on 16:9" premise never holds for it, and the
            // cull can't be self-correcting either: MenuCursor moves its PARENT transform,
            // while the Graphic scanned here is the child mesh whose own localPosition and
            // scale never change, so WatchCulled (which watches the Graphic's transform)
            // can't hand it back even while the cursor moves. Exempt it outright.
            if (g.GetComponentInParent<MenuCursor>() != null) continue;
            // Menu pages are off-limits too. A MenuPage slides and animates as a whole
            // unit, parks its buttons by SemiUI hover state, and hosts the rotating
            // Semibot model on a render texture that never moves. The static
            // outside-the-canvas test produces false positives across all of it (the
            // whole main menu logged as "hidden"), and WatchCulled can't recover a button
            // resting at its parked anchor or a still model. Whatever a page legitimately
            // shows is its own business; the reveal cull only owns the in-game HUD parking
            // lot and stray off-canvas art, neither of which lives under a MenuPage.
            if (g.GetComponentInParent<MenuPage>() != null) continue;
            var cg = g.GetComponentInParent<CanvasGroup>();
            if (cg != null && cg.alpha < 0.01f) continue;

            // A Graphic under a currently-parked SemiUI element is not authored-visible
            // HUD: vanilla shows none of it in this state. SemiUI parks by moving its
            // transform to hidePosition and disabling only the child uiText; it does not
            // deactivate its other children (AllChildrenSetActive(false)) until a full
            // show/hide cycle has run. An element that never opens in a given scene
            // (chat on the title screen) therefore leaves its frame/box children active
            // at the parked anchor, and the widened capture reveals them past the canvas
            // edge. Those children usually only SPILL the edge (the parent pivot sits
            // just inside), so the fullyOutside test below leaves them showing. Promote
            // the spill to cullable when the owning SemiUI is parked: the whole element
            // is meant to be hidden, and WatchCulled unculls the frame it animates open
            // (localPosition leaves the park).
            bool parkedSemiUI = false;
            var semi = g.GetComponentInParent<SemiUI>();
            if (semi != null && semi.hidePosition != semi.showPosition)
            {
                parkedSemiUI = ((Vector2)semi.transform.localPosition - semi.hidePosition)
                    .sqrMagnitude < 1f;
            }

            // fully off-canvas = invisible vanilla = safe to hide
            bool fullyOutside = max.x <= -halfW + 1f || min.x >= halfW - 1f
                             || max.y <= -halfH + 1f || min.y >= halfH - 1f;
            if ((fullyOutside || parkedSemiUI) && !g.canvasRenderer.cull)
            {
                g.canvasRenderer.cull = true;
                _culled.Add((g, rt.localPosition, rt.localScale, parkedSemiUI ? semi : null));
            }

            if (!_logged.Add(g.GetInstanceID())) continue;
            string path = g.transform.parent != null
                ? g.transform.parent.name + "/" + g.gameObject.name
                : g.gameObject.name;
            Plugin.Log.LogInfo($"[ultrawide] reveal: {path} <{g.GetType().Name}> " +
                $"{(fullyOutside ? "hidden" : parkedSemiUI ? "parked-hidden" : "spills")} " +
                $"rect=({min.x:F0},{min.y:F0})..({max.x:F0},{max.y:F0}) " +
                $"canvas=({halfW:F0},{halfH:F0}) cap=({capW:F0},{capH:F0}) a={g.color.a:F2}");
        }
    }

    static void UncullAll()
    {
        foreach (var (g, _, _, _) in _culled)
            if (g != null) g.canvasRenderer.cull = false;
        _culled.Clear();
    }
}

// Stale camera aspects: Camera Top ships pinned to the vanilla 750/418 box
// ratio (1.794), so once the render target is panel-sized its whole layer
// draws stretched across the wide display while the world layer renders true.
// REPO renders GRABBED objects on the top layer (to avoid wall clipping), so a
// held cart at the screen edge looked stretched while the world behind it was
// correct: the source of the "fake ultrawide" impression. Re-derive any game
// camera whose aspect deviates from its actual render target, enforced per
// frame in case game code re-pins it. Hands off while F10-compare owns the
// aspects, and inert when the panel needs no fix.
internal static class GameCameraAspectGuard
{
    internal static void Tick()
    {
        if (VRCompat.Active || UltrawideCompareResolution.IsCompareActive) return;
        if (!Settings.ModEnabled || !Settings.UltrawideUiFix) return;
        if (!UltrawideCanvasFix.RequiresAspectFix() || Screen.height == 0) return;

        var rtm = RenderTextureMain.instance;
        if (rtm == null || rtm.cameras == null) return;
        foreach (var c in rtm.cameras)
        {
            if (c == null) continue;
            var tex = c.targetTexture;
            float want = tex != null
                ? (float)tex.width / Mathf.Max(1, tex.height)
                : (float)Screen.width / Screen.height;
            if (Mathf.Abs(c.aspect - want) > 0.01f) c.ResetAspect();
        }

    }
}

// Aspect ratios change at runtime (resolution dropdown, window drag, monitor hop).
// All the aspect-derived state above is recomputed by RefreshAll, but those refreshes
// ride scene/menu/settings events, so a bare resolution switch would leave the
// squeeze factor, cursor remap box, and FOV defaults stale. Ticked every frame from
// UpscalerManager; only does work when Screen dims actually change.
internal static class UltrawideResolutionWatcher
{
    static int _w, _h;
    static float _retryTimer;

    internal static void Tick()
    {
        // Fallback engage: the menu/scene event triggers can die (a Harmony patch
        // conflict or load-order quirk killed MenuPage.Awake's postfix in one
        // session and the presentation never came up). If the underlay should be
        // active but isn't, retry on a slow cadence; RefreshAll self-gates
        // (splash, VR, aspect), so this is a no-op everywhere else.
        if (Settings.ModEnabled && Settings.UltrawideUiFix
            && UltrawideCanvasFix.RequiresAspectFix() && !UltrawideCanvasFix.UnderlayActive)
        {
            _retryTimer += Time.unscaledDeltaTime;
            if (_retryTimer >= 0.5f)
            {
                _retryTimer = 0f;
                UltrawideCanvasFix.RefreshAll();
            }
        }
        else
        {
            _retryTimer = 0f;
        }

        if (Screen.width == _w && Screen.height == _h) return;
        bool first = _w == 0;
        _w = Screen.width;
        _h = Screen.height;
        if (first) return; // startup sample; the normal apply path covers the first pass

        Plugin.Log.LogDebug($"[ultrawide] resolution change -> {_w}x{_h}, refreshing aspect state");
        UltrawideCanvasFix.RefreshAll();
        CameraZoomFovOverride.RefreshAll();
        MenuCameraFovOverride.Apply();
    }
}

// Renders the world to a full-screen RawImage on a sortOrder=0 canvas behind the game's
// UI canvas (sortOrder=1), so the world fills the wide screen while the game's UI
// hierarchy stays untouched. The game's full-screen Background (which letterboxes the
// 16:9 inner box on vanilla) is disabled so the underlay shows through; the inner
// Render Texture Main RawImage is also disabled so its squashed view doesn't overdraw
// the underlay. Render Texture Overlay (post-FX: vignette, bloom, screen flashes) is
// mirrored onto a sibling RawImage at full-screen size so post-FX cover the full aspect.
internal static class UltrawideCanvasFix
{
    // Wider than this triggers the underlay for the 21:9 / 32:9 case (kills horizontal letterbox).
    const float WideThreshold = 1.85f;
    // Narrower than this triggers the underlay for the 16:10 / 4:3 / 5:4 case (kills vertical letterbox).
    // 16:9 = 1.7777..., so 1.768 catches anything definitively narrower while leaving exact 16:9 untouched.
    const float NarrowThreshold = 1.768f;

    static GameObject? _ultrawideUnderlay;
    static GameObject? _underlayCanvasGo;
    static RawImage? _underlayRawImage;

    static RawImage? _hiddenMainImage;
    static bool _hiddenMainImageWasEnabled;

    static Image? _hiddenBackground;
    static bool _hiddenBackgroundWasEnabled;

    static RawImage? _hiddenOverlayRawImage;
    static bool _hiddenOverlayWasEnabled;
    static GameObject? _ultrawidePostFx;
    static RawImage? _postFxRawImage;

    internal static void RefreshAll()
    {
        // VR bypasses the flat Render Texture Main presentation this underlay rebuilds.
        if (VRCompat.Active) return;
        // Splash sequence stays fully vanilla: the splash art is HUD-canvas sized,
        // so under our wide presentation it only covers the central 16:9 and the
        // already-loaded menu world peeks out around it. The first main-menu
        // MenuPage.Awake lands right after the splash and engages everything.
        bool inSplash = RunManager.instance != null
                        && RunManager.instance.levelCurrent != null
                        && SemiFunc.IsSplashScreen();
        bool active = Settings.ModEnabled && Settings.UltrawideUiFix && RequiresAspectFix() && !inSplash;
        if (!active) { RestoreAll(); return; }
        EnsureUltrawideUnderlay();

        GameCameraAspectGuard.Tick();
        // HUD-unstretch itself runs from OverlayCameraWiden.Tick (per frame, owns
        // HudCursorRemap.Active too) so it can't race scene construction.
    }

    // True while the full-screen mirror presentation is actually in place.
    // OverlayCameraWiden gates on this so the capture never widens against the
    // game's vanilla boxed display (boot sequence, mid-restore).
    internal static bool UnderlayActive => _underlayCanvasGo != null && _postFxRawImage != null;

    // Wider than 16:9 (21:9, 32:9). Used by callers that specifically want the wider case
    // (e.g. menu camera narrowing, FOV bump, F10 vanilla-compare).
    internal static bool IsUltrawide()
        => Screen.height > 0 && (float)Screen.width / Screen.height > WideThreshold;

    // True when the panel's aspect doesn't match the game's fixed 16:9 inner box.
    // Vanilla letterboxes the world view to a centred 16:9 RawImage:
    //   Wider panels (21:9 / 32:9): letterbox lives on the LEFT and RIGHT.
    //   Narrower panels (16:10 / 4:3 / 5:4): letterbox lives on the TOP and BOTTOM.
    // Same underlay path solves both: bind the world camera RT to a full-screen RawImage,
    // hide the game's inner mainImage and the surrounding Background, mirror post-FX.
    // The world camera's HOR+ default already tracks Screen.aspect, so the worldRT contents
    // render at panel aspect; displaying that RT full-screen un-stretches it correctly.
    internal static bool RequiresAspectFix()
    {
        if (Screen.height == 0) return false;
        float aspect = (float)Screen.width / Screen.height;
        return aspect > WideThreshold || aspect < NarrowThreshold;
    }

    static void EnsureUltrawideUnderlay()
    {
        var rtm = RenderTextureMain.instance;
        if (rtm == null || rtm.overlayRawImage == null) return;
        var mainRT = rtm.overlayRawImage.rectTransform.parent as RectTransform;
        if (mainRT == null) return;
        var mainImage = mainRT.GetComponent<RawImage>();
        if (mainImage == null) return;

        // The RawImage texture assigned in the prefab can be a different object from
        // RenderTextureMain.renderTexture (the RT the camera actually writes to). Bind
        // the latter so the underlay shows what's being rendered, not a stale prefab tex.
        var worldRT = rtm.renderTexture;
        if (worldRT == null) return;

        if (_underlayCanvasGo != null && _underlayRawImage != null)
        {
            // RawImage re-meshes whenever texture is reassigned, even to the same value,
            // and the re-mesh shows up as a black flash on every settings refresh (FOV
            // slider drag triggers a refresh). Only reassign on actual change.
            if (_underlayRawImage.texture != worldRT)
            {
                _underlayRawImage.texture = worldRT;
                _underlayRawImage.SetMaterialDirty();
            }
            if (mainImage.enabled) HideMainImage(mainImage);
            HideCanvasBackgroundIfPresent(mainImage);
            RouteOverlayThroughUnderlay(rtm);
            return;
        }

        _underlayCanvasGo = new GameObject("REPOFidelity Ultrawide Canvas",
            typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
            typeof(GraphicRaycaster));
        Object.DontDestroyOnLoad(_underlayCanvasGo);
        var canvas = _underlayCanvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 0;
        _underlayCanvasGo.GetComponent<CanvasScaler>().uiScaleMode =
            CanvasScaler.ScaleMode.ConstantPixelSize;

        _ultrawideUnderlay = new GameObject("REPOFidelity Ultrawide Underlay",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        _ultrawideUnderlay.transform.SetParent(_underlayCanvasGo.transform, false);

        var rt = _ultrawideUnderlay.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = Vector2.zero;

        _underlayRawImage = _ultrawideUnderlay.GetComponent<RawImage>();
        _underlayRawImage.texture = worldRT;
        _underlayRawImage.color = Color.white;
        _underlayRawImage.raycastTarget = false;

        HideMainImage(mainImage);
        HideCanvasBackgroundIfPresent(mainImage);
        RouteOverlayThroughUnderlay(rtm);
    }

    static void HideMainImage(RawImage mainImage)
    {
        if (_hiddenMainImage != mainImage)
        {
            _hiddenMainImage = mainImage;
            _hiddenMainImageWasEnabled = mainImage.enabled;
        }
        // Disabling the RawImage component leaves children rendering. The HUD lives here
        // as descendants and must keep drawing.
        mainImage.enabled = false;
    }

    // Sibling of Render Texture Main on the outer Canvas. A full-screen opaque black
    // Image whose default purpose is to letterbox the side regions outside the centred
    // 750x418 box. With our underlay providing the wide world view, this Background has
    // to be off so the underlay can show through.
    static void HideCanvasBackgroundIfPresent(RawImage mainImage)
    {
        var canvasT = mainImage.GetComponentInParent<Canvas>()?.transform;
        if (canvasT == null) return;
        var bgT = canvasT.Find("Background");
        if (bgT == null) return;
        var bg = bgT.GetComponent<Image>();
        if (bg == null) return;
        if (_hiddenBackground != bg)
        {
            _hiddenBackground = bg;
            _hiddenBackgroundWasEnabled = bg.enabled;
        }
        if (bg.enabled) bg.enabled = false;
    }

    // Render Texture Overlay holds the post-processing pass at 750x418. HUD elements are
    // parented under it (not under Main as the names might suggest), so resizing the
    // overlay's RectTransform would drag HUD with it. Disable its draw and mirror its
    // texture/material onto a sibling at full-screen size on the underlay canvas.
    static void RouteOverlayThroughUnderlay(RenderTextureMain rtm)
    {
        var overlay = rtm.overlayRawImage;
        if (overlay == null) return;

        if (_hiddenOverlayRawImage != overlay)
        {
            _hiddenOverlayRawImage = overlay;
            _hiddenOverlayWasEnabled = overlay.enabled;
        }

        var overlayTex = overlay.texture;
        var overlayMat = overlay.material;

        if (overlay.enabled) overlay.enabled = false;
        if (_underlayCanvasGo == null) return;

        if (_ultrawidePostFx == null)
        {
            _ultrawidePostFx = new GameObject("REPOFidelity Ultrawide PostFx",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            _ultrawidePostFx.transform.SetParent(_underlayCanvasGo.transform, false);
            var prt = _ultrawidePostFx.GetComponent<RectTransform>();
            prt.anchorMin = Vector2.zero;
            prt.anchorMax = Vector2.one;
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            prt.sizeDelta = Vector2.zero;
            _postFxRawImage = _ultrawidePostFx.GetComponent<RawImage>();
            _postFxRawImage.raycastTarget = false;
            _postFxRawImage.color = Color.white;
        }

        if (_postFxRawImage != null)
        {
            if (_postFxRawImage.texture != overlayTex) _postFxRawImage.texture = overlayTex;
            if (_postFxRawImage.material != overlayMat) _postFxRawImage.material = overlayMat;
        }
    }

    internal static void RestoreAll()
    {
        if (_hiddenMainImage != null)
        {
            _hiddenMainImage.enabled = _hiddenMainImageWasEnabled;
            _hiddenMainImage = null;
        }
        if (_hiddenBackground != null)
        {
            _hiddenBackground.enabled = _hiddenBackgroundWasEnabled;
            _hiddenBackground = null;
        }
        if (_hiddenOverlayRawImage != null)
        {
            _hiddenOverlayRawImage.enabled = _hiddenOverlayWasEnabled;
            _hiddenOverlayRawImage = null;
        }
        if (_ultrawidePostFx != null) Object.Destroy(_ultrawidePostFx);
        if (_underlayCanvasGo != null) Object.Destroy(_underlayCanvasGo);
        if (_ultrawideUnderlay != null) Object.Destroy(_ultrawideUnderlay);
        _ultrawidePostFx = null;
        _postFxRawImage = null;
        _underlayCanvasGo = null;
        _ultrawideUnderlay = null;
        _underlayRawImage = null;
    }
}

// MenuPage has no Awake; targeting it left this patch dead in every build (the
// engagement only ever worked through fallbacks). Start is the real Unity hook.
[HarmonyPatch(typeof(MenuPage), "Start")]
internal static class MenuPageStartUltrawidePatch
{
    [HarmonyPostfix]
    static void Postfix(MenuPage __instance)
    {
        UltrawideCanvasFix.RefreshAll();
        MenuCameraFovOverride.Apply();
        HudCoverStretch.Rescan();
        // same-frame extension for freshly spawned pages; the 0.4s cadence alone
        // shows the bare strip for a visible beat on every menu open
        MenuEdgeArtExtend.Rescan();
        HudParkedShift.Rescan();
    }
}

[HarmonyPatch(typeof(LevelGenerator), "GenerateDone")]
internal static class LevelGeneratorUltrawidePatch
{
    [HarmonyPostfix]
    static void Postfix()
    {
        UltrawideCanvasFix.RefreshAll();
        CameraZoomFovOverride.RefreshAll();
        UltrawideSettingsWatcher.Register();
    }
}

internal static class UltrawideSettingsWatcher
{
    static bool _registered;

    internal static void Register()
    {
        if (_registered) return;
        _registered = true;
        Settings.OnSettingsChanged += OnChanged;
    }

    // Wrap each refresh independently. An exception in the canvas pipeline must not
    // prevent the FOV refresh, or vice versa - that combination once silently killed the
    // FOV slider when the canvas walker hit a destroyed Graphic.
    static void OnChanged()
    {
        try { UltrawideCanvasFix.RefreshAll(); }
        catch (System.Exception e) { Plugin.Log.LogError($"[ultrawide] canvas RefreshAll threw: {e}"); }
        try { CameraZoomFovOverride.RefreshAll(); }
        catch (System.Exception e) { Plugin.Log.LogError($"[ultrawide] FOV RefreshAll threw: {e}"); }
        try { MenuCameraFovOverride.Apply(); }
        catch (System.Exception e) { Plugin.Log.LogError($"[ultrawide] menu FOV Apply threw: {e}"); }
    }
}

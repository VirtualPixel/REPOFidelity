using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
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
        if (!Settings.ModEnabled) { Restore(cz); return; }

        if (!_originals.ContainsKey(cz))
            _originals[cz] = cz.playerZoomDefault;

        // Slider override (>0) wins. Otherwise the default scales with aspect:
        // 16:9 = vanilla, 21:9 ~80, 32:9 = 90.
        float target = Settings.VerticalFovOverride > 0
            ? Settings.VerticalFovOverride
            : ComputeAspectAwareDefault(_originals[cz]);
        StartFovAnim(cz, target);
    }

    internal static float ComputeAspectAwareDefault(float vanilla16x9Fov)
    {
        if (Screen.height == 0) return vanilla16x9Fov;
        float aspect = (float)Screen.width / Screen.height;
        const float a169 = 16f / 9f;
        const float a219 = 2.389f;
        const float a329 = 32f / 9f;
        if (aspect <= a169 + 0.01f) return vanilla16x9Fov;
        if (aspect <= a219)
            return Mathf.Lerp(vanilla16x9Fov, 80f, Mathf.InverseLerp(a169, a219, aspect));
        if (aspect <= a329)
            return Mathf.Lerp(80f, 90f, Mathf.InverseLerp(a219, a329, aspect));
        return 90f;
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

    // Lerp between vanilla HOR+ (vFOV unchanged) and full VERT- (vFOV reduced so hFOV
    // matches the 16:9-equivalent). Strength: 0 at 16:9, ~0.51 at 21:9, 1.0 at 32:9.
    // Used on the menu camera to hide the world edge that wider FOV reveals past the
    // truck on the title scene.
    static float ApplyAspectCorrection(float baseFov)
    {
        if (Screen.height == 0) return baseFov;
        float aspect = (float)Screen.width / Screen.height;
        const float refAspect = 16f / 9f;
        if (aspect <= refAspect + 0.01f) return baseFov;

        float strength = Mathf.Clamp01((aspect / refAspect - 1f) * 1.5f);
        if (strength <= 0f) return baseFov;

        // target_vFov = 2 * atan(tan(baseFov/2) * refAspect / aspect)
        float baseRad = baseFov * Mathf.Deg2Rad;
        float fullCorrectedRad = 2f * Mathf.Atan(Mathf.Tan(baseRad / 2f) * refAspect / aspect);
        return Mathf.Lerp(baseFov, fullCorrectedRad * Mathf.Rad2Deg, strength);
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

// Renders the world to a full-screen RawImage on a sortOrder=0 canvas behind the game's
// UI canvas (sortOrder=1), so the world fills the wide screen while the game's UI
// hierarchy stays untouched. The game's full-screen Background (which letterboxes the
// 16:9 inner box on vanilla) is disabled so the underlay shows through; the inner
// Render Texture Main RawImage is also disabled so its squashed view doesn't overdraw
// the underlay. Render Texture Overlay (post-FX: vignette, bloom, screen flashes) is
// mirrored onto a sibling RawImage at full-screen size so post-FX cover the full aspect.
internal static class UltrawideCanvasFix
{
    const float Threshold = 1.85f;

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
        bool active = Settings.ModEnabled && Settings.UltrawideUiFix && IsUltrawide();
        if (!active) { RestoreAll(); return; }
        EnsureUltrawideUnderlay();
    }

    internal static bool IsUltrawide()
        => Screen.height > 0 && (float)Screen.width / Screen.height > Threshold;

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

[HarmonyPatch(typeof(MenuPage), "Awake")]
internal static class MenuPageAwakeUltrawidePatch
{
    [HarmonyPostfix]
    static void Postfix()
    {
        UltrawideCanvasFix.RefreshAll();
        MenuCameraFovOverride.Apply();
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

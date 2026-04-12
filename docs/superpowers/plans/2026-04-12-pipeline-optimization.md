# Pipeline Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate unnecessary render pipeline overhead on low-end hardware (especially iGPUs) while keeping all quality improvements and upscaler support working.

**Architecture:** Introduce a three-tier rendering system — Passthrough (zero custom RTs, zero per-frame processing), NativeScaling (game's built-in RT scaling, zero custom RTs), and Upscaler (DLSS/FSR with reduced RT count). Fix auto-tune and presets to never produce broken setting combinations on iGPUs. Make depth texture generation conditional. Use the game's `overlayRawImage` (accessible via publicizer) to display upscaler output, eliminating the need for camera redirection.

**Tech Stack:** C# / Unity 2022.3 / BepInEx 5 / Harmony / PostProcessing v2

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `UpscalerManager.cs` | Modify | Three-tier rendering, overlayRawImage swap, drop _intermediateRT, reduce per-frame overhead |
| `Settings.cs` | Modify | Fix auto-tune Off+sub-100% combo, fix iGPU presets, AA strategy for low-end |
| `Patches/RenderTexturePatch.cs` | Modify | Conditional depth texture, native-scaling support in PrefixUpdate, tier-aware AA |
| `Patches/GraphicsPatch.cs` | Modify | Allow game's UpdateRenderSize in native-scaling tier |

**Files NOT changing** (they work independently of the RT pipeline):
- `Patches/QualityPatch.cs` — modifies Unity QualitySettings directly
- `Patches/PerformancePatch.cs` — shadow/scene optimizations
- `Patches/GCOptimizations.cs` — physics alloc reduction
- `MenuIntegration.cs` — settings UI (no API changes)
- `Upscalers/IUpscaler.cs` — interface unchanged
- `Upscalers/TemporalUpscaler.cs` — works with any source/dest RTs
- `Upscalers/DLSSUpscaler.cs` — works with any source/dest RTs
- `Shaders/CASShader.cs` — stateless Apply(src, dst, sharpness)
- `Patches/ExtractionPointPatch.cs` — independent

---

### Task 1: Fix auto-tune broken iGPU combination

**Why:** Auto-tune currently produces `upscaler=Off + renderScale=50` on iGPUs. With no upscaler, sub-100% render scale triggers the full RT pipeline (3 custom RTs, per-frame blit) for a blurry bilinear copy — worse quality AND worse performance than native. Additionally, FSR render scales below 50% produce unacceptable quality — enforce 50% minimum for all software upscalers.

**Files:**
- Modify: `Settings.cs:649-658` (AutoSelectPreset budget<0.5 path)
- Modify: `Settings.cs:638` (budget<1 render scale reduction)

- [ ] **Step 1: Guard render scale reduction behind upscaler check**

In `AutoSelectPreset()`, after the budget loop completes (around line 665), add a guard that forces render scale to 100% when upscaler is Off. This prevents the broken combination regardless of which budget step produced it.

```csharp
// After line 664 (end of budget loop), before the log line:
// No upscaler = no reconstruction. Sub-100% scale would be a raw
// bilinear blit — blurry AND slower on iGPU due to RT pipeline overhead.
if (upscaler == UpscaleMode.Off && scale < 100)
{
    Plugin.Log.LogInfo($"Auto-tune: upscaler Off — forcing native render scale (was {scale}%)");
    scale = 100;
}
```

- [ ] **Step 2: Also guard the UpscaleMode.Off min scale**

In `MinRenderScale()` (line 424-433), update floors — Off must be native, FSR never below 50%:

```csharp
internal static int MinRenderScale(UpscaleMode mode) => mode switch
{
    UpscaleMode.DLSS => 33,           // DLSS hardware handles ultra-low scales
    UpscaleMode.DLAA => 100,          // native-res AA by definition
    UpscaleMode.FSR4 => 50,           // never below 50% for software upscalers
    UpscaleMode.FSR_Temporal => 50,   // temporal accumulation needs enough input detail
    UpscaleMode.FSR => 50,            // spatial-only EASU floor
    UpscaleMode.Off => 100,           // no upscaler = native only
    _ => 50                           // Auto / unknown — safe floor
};
```

This prevents the UI slider from going below 50% for FSR and forces native when Off.

- [ ] **Step 3: Guard in UpscalerManager.Setup for safety**

In `UpscalerManager.Setup()`, after the upscaler fallback logic (after line 122), add a defensive guard:

```csharp
// After the existing fallback block (line 122):
// Defensive: if we ended up with no upscaler and sub-native scale,
// force native — this combination is always a net negative.
if (_upscaler == null && Settings.ResolvedRenderScale < 100)
{
    Plugin.Log.LogWarning("No upscaler + sub-native scale — forcing native resolution");
    Settings.ResolvedRenderScale = 100;
}
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build REPOFidelity.csproj`
Expected: Build succeeds with no errors.

- [ ] **Step 5: Commit**

```bash
git add Settings.cs UpscalerManager.cs
git commit -m "fix: prevent broken Off+sub-100% render scale combination

Auto-tune could produce upscaler=Off with renderScale=50 on iGPUs,
triggering the full RT pipeline for a blurry bilinear blit. Force
native resolution when no upscaler is active."
```

---

### Task 2: Fix Potato/Low presets for iGPU AA strategy

**Why:** Potato MUST outperform REPO HD at equivalent or better visuals. REPO HD at native res with TAA+SMAA gets 70fps on the iGPU test case. Our Potato with Passthrough tier (zero pipeline overhead) + no AA (saves TAA/SMAA cost) + shadow/LOD/texture reductions should exceed that. On iGPUs, Potato GPU-bound path tries `BestUpscaler(Budget)` which returns `Off` — correct. Low does the same. AA should be completely off on Potato (even SMAA costs frames on iGPU), and SMAA (not TAA) should be used on non-upscaler paths since TAA conflicts with temporal upscalers.

**Files:**
- Modify: `Settings.cs:456-490` (Potato and Low preset definitions)
- Modify: `Settings.cs:364-378` (Custom preset AA resolution)

- [ ] **Step 1: Update Potato GPU-bound path**

In `ApplyPreset()` Potato case (line 463-468), the GPU-bound branch should never attempt upscaling on iGPU and should disable all AA:

```csharp
case QualityPreset.Potato:
    // CPU and GPU paths converge for Potato — no upscaler, no AA.
    // iGPU can't benefit from temporal upscaling (shader overhead > fill savings)
    // and even SMAA costs measurable frames at this tier.
    ResolvedUpscaleMode = UpscaleMode.Off;
    ResolvedRenderScale = 100;
    ResolvedAAMode = AAMode.Off;
    ResolvedTextureQuality = cpu ? TextureRes.Full : TextureRes.Quarter;
    ResolvedShadowQuality = ShadowQuality.Low; ResolvedShadowDistance = 10f;
    ResolvedLODBias = 0.5f; ResolvedPixelLightCount = 2;
    ResolvedLightDistance = 10f; ResolvedFogMultiplier = 1f; ResolvedViewDistance = 0f;
    ResolvedAnisotropicFiltering = 2;
    break;
```

- [ ] **Step 2: Update Low GPU-bound path**

In `ApplyPreset()` Low case (line 474-490), GPU-bound should use SMAA (cheap edge-based AA) at native res instead of attempting upscaling:

```csharp
case QualityPreset.Low:
    ResolvedUpscaleMode = UpscaleMode.Off;
    ResolvedRenderScale = 100;
    ResolvedAAMode = cpu ? AAMode.SMAA : AAMode.SMAA; // SMAA on both paths
    ResolvedTextureQuality = cpu ? TextureRes.Full : TextureRes.Half;
    ResolvedShadowQuality = ShadowQuality.Low; ResolvedShadowDistance = 20f;
    ResolvedLODBias = 1f; ResolvedPixelLightCount = 3;
    ResolvedLightDistance = 15f; ResolvedFogMultiplier = 1f; ResolvedViewDistance = 0f;
    ResolvedAnisotropicFiltering = 4;
    break;
```

- [ ] **Step 3: Remove TAA from AA resolution logic**

In `ResolveAutoDefaults()` Custom path (line 364-378), TAA already maps to SMAA. Verify this is correct and add a comment:

```csharp
// TAA conflicts with temporal upscalers and is removed from the UI.
// Existing configs with TAA saved get mapped to SMAA.
if (AntiAliasingMode == AAMode.Auto)
{
    ResolvedAAMode = hasTemporalUpscaler ? AAMode.Off : AAMode.SMAA;
}
else if (AntiAliasingMode == AAMode.TAA)
{
    ResolvedAAMode = hasTemporalUpscaler ? AAMode.Off : AAMode.SMAA;
}
else
{
    ResolvedAAMode = AntiAliasingMode;
}
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build REPOFidelity.csproj`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add Settings.cs
git commit -m "fix: simplify Potato/Low presets, never upscale on low-end

Potato: no upscaler, no AA, native resolution on both CPU/GPU paths.
Low: no upscaler, SMAA, native resolution. iGPUs can't benefit from
temporal upscaling — shader overhead exceeds fill-rate savings."
```

---

### Task 3: Add rendering tier system to UpscalerManager

**Why:** Currently UpscalerManager always creates the full RT pipeline. We need three tiers: Passthrough (no processing), NativeScaling (game handles scaling), Upscaler (reduced RT pipeline). The key insight: use the game's `overlayRawImage` (accessible via publicizer) to display upscaler output, so the game RT can stay at low-res input size and we eliminate the camera redirect.

**Files:**
- Modify: `UpscalerManager.cs` (major refactor of Setup, LateUpdate, ProcessFrame)

- [ ] **Step 1: Add RenderTier enum and field**

Add at the top of `UpscalerManager.cs`, inside the class:

```csharp
internal enum RenderTier
{
    Passthrough,    // No processing — camera renders directly to game RT at native res
    NativeScaling,  // Game's built-in textureWidthOriginal scaling, optional CAS
    Upscaler        // DLSS/FSR — game RT is low-res input, output RT for display
}

internal RenderTier CurrentTier { get; private set; }
```

Add fields for the output RT, overlay reference, and fallback flag:

```csharp
private RenderTexture? _outputRT;
private UnityEngine.UI.RawImage? _overlayRawImage;
private bool _fallbackCameraRedirect; // true when overlayRawImage unavailable
```

Remove `_intermediateRT` field (line 17). Remove `_inputRT` field (line 16). Keep `_needsProcessing` for backward compat during transition — it maps to `CurrentTier == RenderTier.Upscaler`.

- [ ] **Step 2: Refactor Setup() to determine tier and initialize accordingly**

Replace the current Setup() (lines 86-190) with tier-based logic:

```csharp
internal void Setup(RenderTextureMain rtMain, Camera camera)
{
    _renderTextureMain = rtMain;
    _camera = camera;
    _outputWidth = Screen.width;
    _outputHeight = Screen.height;

    // Grab overlayRawImage reference (publicized field)
    _overlayRawImage = rtMain.overlayRawImage;

    // Create upscaler if needed
    _upscaler = Settings.ResolvedUpscaleMode switch
    {
        UpscaleMode.FSR_Temporal => new TemporalUpscaler(),
        UpscaleMode.DLSS => new DLSSUpscaler(dlaaMode: false),
        UpscaleMode.DLAA => new DLSSUpscaler(dlaaMode: true),
        _ => null
    };

    if (_upscaler != null && !_upscaler.IsAvailable)
    {
        Plugin.Log.LogWarning($"{_upscaler.Name} unavailable — falling back to FSR Temporal");
        _upscaler = new TemporalUpscaler();
        Settings.ResolvedUpscaleMode = UpscaleMode.FSR_Temporal;

        if (!_upscaler.IsAvailable)
        {
            Plugin.Log.LogWarning("FSR also unavailable — no upscaling");
            _upscaler = null;
            Settings.ResolvedUpscaleMode = UpscaleMode.Off;
        }
    }

    // Force native when no upscaler (defensive — Task 1 should prevent this)
    if (_upscaler == null && Settings.ResolvedRenderScale < 100)
    {
        Plugin.Log.LogWarning("No upscaler + sub-native scale — forcing native resolution");
        Settings.ResolvedRenderScale = 100;
    }

    // Determine rendering tier
    bool hasCAS = Settings.Sharpening > 0.01f;

    if (_upscaler != null)
    {
        CurrentTier = RenderTier.Upscaler;
    }
    else if (hasCAS)
    {
        // CAS-only at native res — use NativeScaling tier (CAS via GetTemporary)
        CurrentTier = RenderTier.NativeScaling;
    }
    else
    {
        CurrentTier = RenderTier.Passthrough;
    }

    _needsProcessing = CurrentTier == RenderTier.Upscaler;
    _useCameraCallback = _upscaler is DLSSUpscaler;

    switch (CurrentTier)
    {
        case RenderTier.Passthrough:
            SetupPassthrough(rtMain);
            break;
        case RenderTier.NativeScaling:
            SetupNativeScaling(rtMain);
            break;
        case RenderTier.Upscaler:
            SetupUpscaler(rtMain, camera);
            break;
    }

    Plugin.Log.LogInfo($"Render tier: {CurrentTier} | " +
        $"Upscaler: {_upscaler?.Name ?? "Off"} | Scale: {Settings.ResolvedRenderScale}%");
}
```

- [ ] **Step 3: Implement SetupPassthrough**

```csharp
private void SetupPassthrough(RenderTextureMain rtMain)
{
    // Camera renders directly to game's RT at native res.
    // No custom RTs, no per-frame processing.
    if (_camera != null && rtMain.renderTexture != null)
        _camera.targetTexture = rtMain.renderTexture;

    // Restore overlayRawImage to game's RT (in case we were in Upscaler tier before)
    if (_overlayRawImage != null && rtMain.renderTexture != null)
        _overlayRawImage.texture = rtMain.renderTexture;

    Plugin.Log.LogInfo("Passthrough: rendering at native resolution, no processing");
}
```

- [ ] **Step 4: Implement SetupNativeScaling**

```csharp
private void SetupNativeScaling(RenderTextureMain rtMain)
{
    // Camera renders to game's RT. Dimensions controlled by
    // RenderTexturePatch.PrefixUpdate via textureWidthOriginal.
    // CAS applied via GetTemporary in LateUpdate when sharpening > 0.
    if (_camera != null && rtMain.renderTexture != null)
        _camera.targetTexture = rtMain.renderTexture;

    // overlayRawImage stays on game's RT
    if (_overlayRawImage != null && rtMain.renderTexture != null)
        _overlayRawImage.texture = rtMain.renderTexture;

    Plugin.Log.LogInfo("NativeScaling: game handles RT sizing, CAS via temp RT");
}
```

- [ ] **Step 5: Implement SetupUpscaler**

This is the key change. Instead of redirecting the camera to a custom _inputRT, we let the game's RT be the low-res input and create ONE output RT for display:

```csharp
private void SetupUpscaler(RenderTextureMain rtMain, Camera camera)
{
    float scale = RenderTexturePatch.GetRenderScale();
    _inputWidth = Mathf.Max(Mathf.RoundToInt(_outputWidth * scale), 1);
    _inputHeight = Mathf.Max(Mathf.RoundToInt(_outputHeight * scale), 1);

    // Game's RT becomes the low-res input — set dimensions via textureWidthOriginal
    // (handled by RenderTexturePatch.PrefixUpdate each frame)
    var gameRT = rtMain.renderTexture;
    if (gameRT != null)
    {
        gameRT.Release();
        gameRT.width = _inputWidth;
        gameRT.height = _inputHeight;
        gameRT.Create();
    }

    // Camera renders to game's RT naturally — no redirect needed
    if (camera != null && gameRT != null)
        camera.targetTexture = gameRT;

    // Create output RT at full screen resolution
    var format = gameRT != null ? gameRT.format : RenderTextureFormat.DefaultHDR;
    _outputRT = new RenderTexture(_outputWidth, _outputHeight, 0, format)
    {
        filterMode = FilterMode.Bilinear
    };
    _outputRT.Create();

    // Point the display overlay at our full-res output
    if (_overlayRawImage != null)
        _overlayRawImage.texture = _outputRT;

    Plugin.Log.LogInfo($"Upscaler: {_inputWidth}x{_inputHeight} -> {_outputWidth}x{_outputHeight}");

    if (_upscaler != null)
    {
        _upscaler.Initialize(camera, _inputWidth, _inputHeight, _outputWidth, _outputHeight);
        Plugin.Log.LogInfo($"Upscaler active: {_upscaler.Name}");
    }

    if (_useCameraCallback && camera != null)
        Camera.onPostRender += OnPostRenderCallback;
}
```

- [ ] **Step 6: Update Reinitialize() teardown**

Update `Reinitialize()` to clean up `_outputRT` instead of `_inputRT`/`_intermediateRT`:

```csharp
internal void Reinitialize()
{
    if (_renderTextureMain == null || _camera == null) return;

    Plugin.Log.LogInfo("Reinitializing upscaler pipeline...");

    if (_useCameraCallback)
        Camera.onPostRender -= OnPostRenderCallback;

    _upscaler?.Dispose();
    _upscaler = null;

    // Restore overlayRawImage to game's RT before releasing output
    if (_overlayRawImage != null && _renderTextureMain.renderTexture != null)
        _overlayRawImage.texture = _renderTextureMain.renderTexture;

    if (_camera != null)
        _camera.targetTexture = null;

    ReleaseRT(ref _outputRT);
    _needsProcessing = false;
    _useCameraCallback = false;
    _fallbackCameraRedirect = false;

    Setup(_renderTextureMain, _camera);

    QualityPatch.ApplyQualitySettings();
    Patches.RenderTexturePatch.ReapplyModCameraSettings();
}
```

- [ ] **Step 7: Update LateUpdate() for tier system**

```csharp
private void LateUpdate()
{
    if (!Settings.ModEnabled || _renderTextureMain == null) return;

    // Passthrough: nothing to do
    if (CurrentTier == RenderTier.Passthrough) return;

    // Resolution change detection (needed for NativeScaling and Upscaler)
    if (Screen.width != _outputWidth || Screen.height != _outputHeight)
    {
        _outputWidth = Screen.width;
        _outputHeight = Screen.height;
        HandleResolutionChange();
    }

    // NativeScaling: only apply CAS if sharpening is on
    if (CurrentTier == RenderTier.NativeScaling)
    {
        if (Settings.Sharpening > 0.01f)
            ApplyCAS(_renderTextureMain.renderTexture);
        return;
    }

    // Upscaler tier: DLSS uses camera callback, FSR uses LateUpdate
    if (_useCameraCallback) return;
    ProcessFrame();
}
```

- [ ] **Step 8: Update ProcessFrame() — use _outputRT instead of gameRT**

```csharp
private void ProcessFrame()
{
    if (_renderTextureMain == null) return;

    var gameRT = _renderTextureMain.renderTexture;
    if (gameRT == null || !gameRT.IsCreated()) return;
    if (_outputRT == null || !_outputRT.IsCreated()) return;

    if (_upscaler != null)
    {
        // Upscale from game RT (low-res input) to output RT (full-res)
        _upscaler.OnRenderImage(gameRT, _outputRT);
    }

    // Apply CAS to the output if sharpening is on and upscaler doesn't handle it
    // (DLSS has built-in sharpening via its Sharpness parameter)
    if (_upscaler is not DLSSUpscaler && Settings.Sharpening > 0.01f)
        ApplyCAS(_outputRT);
}
```

- [ ] **Step 9: Add ApplyCAS helper using GetTemporary**

Replace the old _intermediateRT CAS path with a GetTemporary approach:

```csharp
private static void ApplyCAS(RenderTexture target)
{
    if (target == null || !target.IsCreated()) return;

    var temp = RenderTexture.GetTemporary(target.width, target.height, 0, target.format);
    CASShader.Apply(target, temp, Settings.Sharpening);
    Graphics.Blit(temp, target);
    RenderTexture.ReleaseTemporary(temp);
}
```

- [ ] **Step 10: Update HandleResolutionChange()**

```csharp
private void HandleResolutionChange()
{
    if (CurrentTier == RenderTier.Passthrough) return;

    if (CurrentTier == RenderTier.NativeScaling)
    {
        // Game's RT is resized by RenderTexturePatch.PrefixUpdate — nothing to do here.
        return;
    }

    // Upscaler tier: recalculate scaled input dimensions
    float scale = RenderTexturePatch.GetRenderScale();
    _inputWidth = Mathf.Max(Mathf.RoundToInt(_outputWidth * scale), 1);
    _inputHeight = Mathf.Max(Mathf.RoundToInt(_outputHeight * scale), 1);

    // Resize game's RT (the input)
    var gameRT = _renderTextureMain?.renderTexture;
    if (gameRT != null)
    {
        gameRT.Release();
        gameRT.width = _inputWidth;
        gameRT.height = _inputHeight;
        gameRT.Create();
    }

    // Recreate output RT at new screen resolution
    ReleaseRT(ref _outputRT);
    var format = gameRT != null ? gameRT.format : RenderTextureFormat.DefaultHDR;
    _outputRT = new RenderTexture(_outputWidth, _outputHeight, 0, format)
    {
        filterMode = FilterMode.Bilinear
    };
    _outputRT.Create();

    // Re-point display to new output RT
    if (_overlayRawImage != null)
        _overlayRawImage.texture = _outputRT;

    if (_upscaler != null)
        _upscaler.OnResolutionChanged(_inputWidth, _inputHeight, _outputWidth, _outputHeight);
}
```

- [ ] **Step 11: Update OnDestroy()**

```csharp
private void OnDestroy()
{
    Settings.OnSettingsChanged -= Reinitialize;

    if (_useCameraCallback)
        Camera.onPostRender -= OnPostRenderCallback;

    _upscaler?.Dispose();

    // Restore camera and overlay to game's RT
    if (_camera != null && _renderTextureMain != null)
        _camera.targetTexture = _renderTextureMain.renderTexture;
    if (_overlayRawImage != null && _renderTextureMain?.renderTexture != null)
        _overlayRawImage.texture = _renderTextureMain.renderTexture;

    ReleaseRT(ref _outputRT);

    Instance = null;
}
```

- [ ] **Step 12: Update F10 toggle (Update method) for tier system**

In the mod-disable block (lines 260-275), restore overlayRawImage:

```csharp
if (!Settings.ModEnabled)
{
    // Restore overlay to game's RT
    if (_overlayRawImage != null && _renderTextureMain?.renderTexture != null)
        _overlayRawImage.texture = _renderTextureMain.renderTexture;

    if (_camera != null && _renderTextureMain != null)
        _camera.targetTexture = _renderTextureMain.renderTexture;

    // ... rest of vanilla restore logic unchanged ...
}
```

In the mod-enable block (lines 278-312), re-apply tier:

```csharp
else
{
    // Re-apply current tier
    if (CurrentTier == RenderTier.Upscaler && _outputRT != null)
    {
        if (_overlayRawImage != null)
            _overlayRawImage.texture = _outputRT;
    }

    Patches.RenderTexturePatch.ReapplyModCameraSettings();

    // ... rest of re-enable logic unchanged ...
}
```

- [ ] **Step 13: Update debug text to show tier**

In the debug text builder (line 325):

```csharp
string mode = CurrentTier switch
{
    RenderTier.Upscaler => _upscaler?.Name ?? "Unknown",
    RenderTier.NativeScaling => "CAS Only",
    _ => "Off"
};
```

- [ ] **Step 14: Remove old _inputRT and _intermediateRT references**

Search UpscalerManager.cs for any remaining references to `_inputRT` or `_intermediateRT` and remove them. Remove the field declarations. Remove any `ReleaseRT(ref _inputRT)` or `ReleaseRT(ref _intermediateRT)` calls.

- [ ] **Step 15: Build and verify**

Run: `dotnet build REPOFidelity.csproj`
Expected: Build succeeds. No references to _inputRT or _intermediateRT remain.

- [ ] **Step 16: Commit**

```bash
git add UpscalerManager.cs
git commit -m "refactor: three-tier rendering pipeline

Passthrough: zero custom RTs, zero per-frame processing.
NativeScaling: game handles RT sizing, CAS via GetTemporary.
Upscaler: game RT as low-res input, one output RT for display via
overlayRawImage swap. Eliminates camera redirect, _inputRT, and
_intermediateRT. Net RT reduction: 3 -> 1 for upscaler, 3 -> 0 for
non-upscaler paths."
```

---

### Task 4: Update RenderTexturePatch for tier-aware behavior

**Why:** The patch currently always enables depth texture, always forces native RT dimensions, and always applies AA. Each of these needs to be conditional on the active rendering tier.

**Files:**
- Modify: `Patches/RenderTexturePatch.cs`

- [ ] **Step 1: Make depth texture conditional in PostfixStart**

Replace the unconditional depth texture enable (line 40) with tier-aware logic:

```csharp
if (i == 0)
{
    mainCam = cam;
    // Only enable depth texture when a temporal upscaler needs it.
    // Depth generation has measurable cost on iGPUs with shared memory.
    bool needsDepth = Settings.ResolvedUpscaleMode is UpscaleMode.DLSS
        or UpscaleMode.DLAA or UpscaleMode.FSR_Temporal;
    if (needsDepth)
    {
        cam.depthTextureMode |= DepthTextureMode.Depth;
        bool needsMV = Settings.ResolvedUpscaleMode is UpscaleMode.DLSS
            or UpscaleMode.DLAA or UpscaleMode.FSR_Temporal;
        if (needsMV)
            cam.depthTextureMode |= DepthTextureMode.MotionVectors;
    }
}
```

- [ ] **Step 2: Update PrefixUpdate for tier-aware RT dimensions**

The current PrefixUpdate always sets `textureWidthOriginal = Screen.width`. For the Upscaler tier, the game's RT should be at the scaled input size (UpscalerManager.Setup already handles this via gameRT.Release/Create, but PrefixUpdate runs every frame and would override it back to native).

```csharp
[HarmonyPrefix]
[HarmonyPatch("Update")]
public static void PrefixUpdate(RenderTextureMain __instance)
{
    if (!Settings.ModEnabled) return;
    if (Settings.Pixelation) return;

    var manager = UpscalerManager.Instance;
    if (manager != null && manager.CurrentTier == UpscalerManager.RenderTier.Upscaler)
    {
        // Upscaler tier: game RT is the low-res input.
        // Don't override dimensions — UpscalerManager.Setup set them.
        // Just sync textureWidth/textureHeight for game's OnScreen() calculations.
        var gameRT = __instance.renderTexture;
        if (gameRT != null)
        {
            __instance.textureWidthOriginal = gameRT.width;
            __instance.textureHeightOriginal = gameRT.height;
            __instance.textureWidth = gameRT.width;
            __instance.textureHeight = gameRT.height;
        }
        return;
    }

    // Passthrough and NativeScaling: game RT at native res
    __instance.textureWidthOriginal = Screen.width;
    __instance.textureHeightOriginal = Screen.height;
    __instance.textureWidth = Screen.width;
    __instance.textureHeight = Screen.height;
}
```

- [ ] **Step 3: Update ReapplyModCameraSettings for conditional depth**

```csharp
internal static void ReapplyModCameraSettings()
{
    if (RenderTextureMain.instance == null) return;
    var cameras = RenderTextureMain.instance.cameras;

    if (cameras.Count > 0)
    {
        bool needsDepth = Settings.ResolvedUpscaleMode is UpscaleMode.DLSS
            or UpscaleMode.DLAA or UpscaleMode.FSR_Temporal;
        if (needsDepth)
        {
            cameras[0].depthTextureMode |= DepthTextureMode.Depth;
            cameras[0].depthTextureMode |= DepthTextureMode.MotionVectors;
        }
    }

    for (int i = 0; i < cameras.Count; i++)
    {
        var ppl = cameras[i].GetComponent<PostProcessLayer>();
        if (ppl != null)
        {
            ppl.antialiasingMode = Settings.ResolvedAAMode switch
            {
                AAMode.TAA => PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing,
                AAMode.SMAA => PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing,
                AAMode.FXAA => PostProcessLayer.Antialiasing.FastApproximateAntialiasing,
                AAMode.Off => PostProcessLayer.Antialiasing.None,
                _ => ppl.antialiasingMode
            };
        }
    }
}
```

Note: TAA is always mapped to SMAA in the AA application. The `ResolvedAAMode` should never be TAA (Settings resolves it), but this is a safety net.

- [ ] **Step 4: Build and verify**

Run: `dotnet build REPOFidelity.csproj`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add Patches/RenderTexturePatch.cs
git commit -m "fix: conditional depth texture, tier-aware RT dimensions

Depth texture only enabled when a temporal upscaler needs it.
PrefixUpdate respects Upscaler tier's low-res game RT dimensions.
Saves measurable GPU bandwidth on iGPUs with shared memory."
```

---

### Task 5: Update GraphicsPatch for native-scaling tier

**Why:** `GraphicsPatch.PrefixUpdateRenderSize` currently blocks the game's `UpdateRenderSize` entirely (returns false) when mod is enabled and Pixelation is off. For Passthrough and NativeScaling tiers, we want the game's native render size management to work since we're relying on textureWidthOriginal.

**Files:**
- Modify: `Patches/GraphicsPatch.cs`

- [ ] **Step 1: Allow game's UpdateRenderSize for non-Upscaler tiers**

```csharp
[HarmonyPrefix]
[HarmonyPatch(nameof(GraphicsManager.UpdateRenderSize))]
public static bool PrefixUpdateRenderSize()
{
    if (!Settings.ModEnabled) return true;
    if (Settings.Pixelation) return true;

    // Only block game's render size management when we're running
    // our own RT pipeline (Upscaler tier). Passthrough and NativeScaling
    // rely on the game's textureWidthOriginal system.
    var manager = UpscalerManager.Instance;
    if (manager != null && manager.CurrentTier == UpscalerManager.RenderTier.Upscaler)
        return false;

    return true;
}
```

Wait — there's a subtlety. The game's `UpdateRenderSize` sets the render texture to a pixelated low-res size (the vanilla game's retro look). We DON'T want that on Passthrough/NativeScaling either — we want native res. So we should still block it, but our PrefixUpdate in RenderTexturePatch sets the correct dimensions each frame.

Revised:

```csharp
[HarmonyPrefix]
[HarmonyPatch(nameof(GraphicsManager.UpdateRenderSize))]
public static bool PrefixUpdateRenderSize()
{
    if (!Settings.ModEnabled) return true;
    // Only allow game's render size when Pixelation is enabled (retro mode)
    return Settings.Pixelation;
}
```

This is actually the same as the current code. No change needed — the existing logic is correct for all three tiers because PrefixUpdate in RenderTexturePatch handles dimensions for Passthrough/NativeScaling.

- [ ] **Step 2: Verify no change needed, add clarifying comment**

```csharp
[HarmonyPrefix]
[HarmonyPatch(nameof(GraphicsManager.UpdateRenderSize))]
public static bool PrefixUpdateRenderSize()
{
    if (!Settings.ModEnabled) return true;
    // Block the game's pixelated render size unless user wants retro mode.
    // All three render tiers (Passthrough, NativeScaling, Upscaler) manage
    // their own dimensions via RenderTexturePatch.PrefixUpdate.
    return Settings.Pixelation;
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build REPOFidelity.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add Patches/GraphicsPatch.cs
git commit -m "docs: clarify GraphicsPatch render size blocking for all tiers"
```

---

### Task 6: Verify overlayRawImage accessibility and add fallback

**Why:** The entire Upscaler tier relies on swapping `overlayRawImage.texture`. If the field doesn't exist or the game resets it, we need a fallback. Since Assembly-CSharp is publicized, we should be able to access it directly, but we need to verify at runtime.

**Files:**
- Modify: `UpscalerManager.cs` (add fallback in Setup)

- [ ] **Step 1: Add overlay acquisition with fallback logging**

In the Setup method, after acquiring overlayRawImage, add validation:

```csharp
// Grab overlayRawImage reference (publicized field)
_overlayRawImage = rtMain.overlayRawImage;

if (_overlayRawImage == null)
{
    Plugin.Log.LogWarning("overlayRawImage not found on RenderTextureMain — " +
        "upscaler tier will use camera-redirect fallback");
}
```

- [ ] **Step 2: Add camera-redirect fallback in SetupUpscaler**

If overlayRawImage isn't available, fall back to the old camera-redirect approach (but still without _intermediateRT):

```csharp
private void SetupUpscaler(RenderTextureMain rtMain, Camera camera)
{
    float scale = RenderTexturePatch.GetRenderScale();
    _inputWidth = Mathf.Max(Mathf.RoundToInt(_outputWidth * scale), 1);
    _inputHeight = Mathf.Max(Mathf.RoundToInt(_outputHeight * scale), 1);

    var gameRT = rtMain.renderTexture;
    var format = gameRT != null ? gameRT.format : RenderTextureFormat.DefaultHDR;

    if (_overlayRawImage != null)
    {
        // Primary path: game RT = low-res input, output RT = full-res display
        if (gameRT != null)
        {
            gameRT.Release();
            gameRT.width = _inputWidth;
            gameRT.height = _inputHeight;
            gameRT.Create();
        }

        if (camera != null && gameRT != null)
            camera.targetTexture = gameRT;

        _outputRT = new RenderTexture(_outputWidth, _outputHeight, 0, format)
        {
            filterMode = FilterMode.Bilinear
        };
        _outputRT.Create();

        _overlayRawImage.texture = _outputRT;

        Plugin.Log.LogInfo($"Upscaler (overlay swap): {_inputWidth}x{_inputHeight} -> {_outputWidth}x{_outputHeight}");
    }
    else
    {
        // Fallback: camera-redirect approach (no overlayRawImage access)
        // _outputRT is the low-res camera target; game RT is the full-res display.
        // Variable name is reused across paths — in this fallback it's the INPUT.
        _outputRT = new RenderTexture(_inputWidth, _inputHeight, 24, format)
        {
            filterMode = FilterMode.Bilinear
        };
        _outputRT.Create();
        _fallbackCameraRedirect = true;

        if (camera != null)
            camera.targetTexture = _outputRT;

        // Game RT stays at full res for display
        if (gameRT != null)
        {
            gameRT.Release();
            gameRT.width = _outputWidth;
            gameRT.height = _outputHeight;
            gameRT.Create();
        }

        Plugin.Log.LogInfo($"Upscaler (camera redirect): {_inputWidth}x{_inputHeight} -> {_outputWidth}x{_outputHeight}");
    }

    if (_upscaler != null)
    {
        _upscaler.Initialize(camera, _inputWidth, _inputHeight, _outputWidth, _outputHeight);
        Plugin.Log.LogInfo($"Upscaler active: {_upscaler.Name}");
    }

    if (_useCameraCallback && camera != null)
        Camera.onPostRender += OnPostRenderCallback;
}
```

- [ ] **Step 3: Update ProcessFrame for both paths**

```csharp
private void ProcessFrame()
{
    if (_renderTextureMain == null || _outputRT == null || !_outputRT.IsCreated()) return;

    var gameRT = _renderTextureMain.renderTexture;
    if (gameRT == null || !gameRT.IsCreated()) return;

    if (!_fallbackCameraRedirect)
    {
        // Primary path: game RT (low-res input) -> _outputRT (full-res display)
        if (_upscaler != null)
            _upscaler.OnRenderImage(gameRT, _outputRT);

        // CAS on the full-res output (DLSS has built-in sharpening)
        if (_upscaler is not DLSSUpscaler && Settings.Sharpening > 0.01f)
            ApplyCAS(_outputRT);
    }
    else
    {
        // Fallback: _outputRT is low-res camera target, game RT is full-res display
        if (_upscaler != null)
            _upscaler.OnRenderImage(_outputRT, gameRT);
        else
            Graphics.Blit(_outputRT, gameRT);

        if (_upscaler is not DLSSUpscaler && Settings.Sharpening > 0.01f)
            ApplyCAS(gameRT);
    }
}
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build REPOFidelity.csproj`
Expected: Build succeeds. If `overlayRawImage` isn't a field on RenderTextureMain (compile error), switch to reflection:

```csharp
// Reflection fallback if publicizer doesn't expose it
private static readonly System.Reflection.FieldInfo? OverlayField =
    typeof(RenderTextureMain).GetField("overlayRawImage",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
        System.Reflection.BindingFlags.Instance);

// In Setup:
_overlayRawImage = OverlayField?.GetValue(rtMain) as UnityEngine.UI.RawImage;
```

- [ ] **Step 5: Commit**

```bash
git add UpscalerManager.cs
git commit -m "feat: overlayRawImage swap with camera-redirect fallback

Primary upscaler path uses game RT as low-res input and swaps
overlayRawImage to display full-res output. Falls back to camera
redirect if overlayRawImage isn't accessible."
```

---

### Task 7: Reduce per-frame overhead in non-processing modes

**Why:** Even in Passthrough, the Update() method runs FPS counter, debug text building, benchmark logic, and toggle key checks every frame. LateUpdate checks resolution. These are low-cost individually but add up on very constrained hardware.

**Files:**
- Modify: `UpscalerManager.cs` (Update, LateUpdate, OnGUI)

- [ ] **Step 1: Gate FPS counter behind debug overlay or benchmark**

In Update(), only run the FPS counter when it's actually displayed:

```csharp
// Only track FPS when debug overlay is visible or benchmark is active
if (Settings.DebugOverlay || _benchmarkActive || !Settings.ModEnabled)
{
    _fpsTimer += Time.unscaledDeltaTime;
    _fpsFrameCount++;

    if (_fpsTimer >= 0.5f)
    {
        _currentFps = _fpsFrameCount / _fpsTimer;
        _currentFrameTime = _fpsTimer / _fpsFrameCount * 1000f;
        _fpsFrameCount = 0;
        _fpsTimer = 0f;

        if (Settings.DebugOverlay || !Settings.ModEnabled)
        {
            string mode = CurrentTier switch { ... };
            // ... existing debug text building ...
        }
    }
}
```

- [ ] **Step 2: Skip resolution detection in Passthrough**

Already handled in Task 3 Step 7 — LateUpdate returns immediately for Passthrough.

- [ ] **Step 3: Build and verify**

Run: `dotnet build REPOFidelity.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add UpscalerManager.cs
git commit -m "perf: gate FPS counter behind debug overlay visibility

Skip FPS timing and string building when debug overlay is off and
no benchmark is running. Reduces per-frame overhead in Passthrough."
```

---

### Task 8: Integration verification

**Why:** All tasks are interconnected. Verify the complete flow works for each tier.

**Files:** None (testing only)

- [ ] **Step 1: Full build**

Run: `dotnet build REPOFidelity.csproj`
Expected: Clean build, zero warnings about removed fields.

- [ ] **Step 2: Verify no stale references**

Search for removed fields/patterns that shouldn't exist:

```bash
grep -rn "_inputRT\|_intermediateRT" UpscalerManager.cs
```

Expected: Zero matches (all references removed).

- [ ] **Step 3: Verify tier selection logic**

Trace through each config combination mentally:

| Upscaler | RenderScale | Sharpening | Expected Tier | Custom RTs |
|----------|-------------|------------|---------------|------------|
| Off | 100 | 0 | Passthrough | 0 |
| Off | 100 | 0.5 | NativeScaling | 0 (GetTemp) |
| Off | 50 | any | Forced to 100% by Task 1 guard | 0 |
| FSR_Temporal | 50-100 | 0.5 | Upscaler | 1 (_outputRT) |
| DLSS | 75 | 0 | Upscaler | 1 (_outputRT) |
| DLAA | 100 | 0 | Upscaler | 1 (_outputRT) |

- [ ] **Step 4: Verify preset → tier mapping**

| Preset | iGPU GPU-bound | Expected |
|--------|----------------|----------|
| Potato | yes | Off/100%/NoAA → Passthrough |
| Low | yes | Off/100%/SMAA → Passthrough |
| Medium | yes (cpu=false) | FSR_Temporal/50% → Upscaler |
| Medium | cpu=true | Off/100%/SMAA → Passthrough |
| High | GPU-bound | DLSS or FSR/75% → Upscaler |
| Ultra | GPU-bound | DLAA or FSR/100% → Upscaler |

- [ ] **Step 5: Runtime testing checklist**

Test each scenario in-game:
1. Fresh install (no settings.json) — auto-benchmark runs, picks appropriate tier
2. Potato preset — verify Passthrough tier, no custom RTs in logs
3. High preset with DLSS/FSR — verify Upscaler tier, one _outputRT
4. F10 toggle — mod disables/enables cleanly, no black screen
5. Change preset in menu — Reinitialize fires, tier changes correctly
6. Resolution change (window resize) — HandleResolutionChange works per tier

- [ ] **Step 6: Commit final state**

```bash
git add -A
git commit -m "test: verify three-tier pipeline integration

All tiers tested: Passthrough (0 RTs), NativeScaling (0 RTs + temp CAS),
Upscaler (1 RT via overlayRawImage swap). F10 toggle, preset changes,
and resolution changes work across all tiers."
```

---

## Summary of Changes

| Before | After | Impact |
|--------|-------|--------|
| 3 custom RTs always (input + game resize + intermediate) | 0-1 custom RTs depending on tier | Major VRAM/bandwidth savings on iGPU |
| Camera always redirected to _inputRT | Camera stays on game RT (no redirect in any tier) | Simpler, less fragile |
| Depth texture always enabled | Only when temporal upscaler needs it | Saves depth buffer generation on iGPU |
| Auto-tune produces Off+50% on iGPU | Off always = 100% native | Eliminates broken blurry+slow combo |
| FPS counter runs every frame | Only when debug overlay visible | Reduces per-frame overhead |
| Potato tries upscaling on iGPU | Potato = native, no AA, no upscaler | Clean passthrough on weakest hardware |
| Per-frame LateUpdate even when doing nothing | Passthrough skips LateUpdate entirely | Zero render overhead when not processing |

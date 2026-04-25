# Ultrawide UI Implementation Plan

> **Context:** Builds on local `ultrawide` branch (commit `c546850`). Spec: `docs/superpowers/specs/2026-04-25-ultrawide-ui-design.md`. FOV slider, native-aspect resolution picker, and dedicated underlay Canvas have already landed; the remaining work is the Mode A underlay-black bug, Mode B (TrueAnchor) implementation, the toggle UI, and verification.

**Goal:** Ship 21:9-and-wider support with two render modes (S2 default, TrueAnchor opt-in) selectable at runtime.

**Architecture:**
- **Mode A (S2, default):** existing underlay Canvas at `sortOrder=0` displays the world RT full-screen behind the game's `sortOrder=1` Canvas. `Render Texture Main` (750×418) stays untouched, HUD inside it stays in centered 16:9. Mod-safe.
- **Mode B (TrueAnchor, opt-in):** underlay disabled, `Render Texture Main` and child `Render Texture Overlay` `sizeDelta` stretched to fill outer Canvas. HUD elements inside Main reposition per their existing anchor presets — corners go to true screen corners.

**Tech Stack:** BepInEx 5, HarmonyX, AssemblyPublicizer (for `zoomPrev`/`zoomNew` access), Unity UI (`Canvas`, `RawImage`, `RectTransform`).

**Files:**
- Modify: `Patches/UltrawidePatch.cs` — bulk of the change. Mode A texture decision, Mode B (`UltrawideStretchMain` static class), mode dispatch in `RefreshAll`.
- Modify: `Settings.cs` — `TrueAnchorMode` accessor (lines ~150 area, alongside `UltrawideUiFix`).
- Modify: `SettingsFile.cs` — `trueAnchorMode` field (alongside `ultrawideUiFix`).
- Modify: `MenuIntegration.cs` — `_trueAnchorToggle` field declaration + Display-section toggle line + `SyncAll` line.
- Modify: `CHANGELOG.md` — bullet.

---

## Task 1: Resolve Mode A underlay-black bug

The underlay Canvas is built and visible (yellow diagnostic confirmed it filled the 21:9 screen) but `_underlayRawImage.texture = rtm.renderTexture` shows black. Walk the four hypotheses in order; stop at the first one that turns the underlay into the world view.

**Files:**
- Modify: `Patches/UltrawidePatch.cs:143` (the `worldRT` assignment) and adjacent `HideMainImage` logic

**Hypothesis 1: texture reference is wrong.**

- [ ] **Step 1.1.** Build the current branch (`dotnet build` from repo root, or whatever the project's build command is — check `prebuild.ps1`/`postbuild.ps1`). Deploy to BepInEx plugins. Launch R.E.P.O. on 21:9 monitor.

- [ ] **Step 1.2.** Read `~/AppData/Roaming/com.kesomannen.gale/repo/profiles/Development/BepInEx/LogOutput.log`, grep for `[ultrawide] underlay live`. The log line ends with either `'<name>' == worldRT` or `'<name>' != worldRT`.

  - If `!=`: H1 wins. Continue to Step 1.3.
  - If `==`: H1 invalidated (texture refs are the same; bug is elsewhere). Skip to Hypothesis 2.

- [ ] **Step 1.3** (only if H1 wins). In `Patches/UltrawidePatch.cs`, change the texture source at line 143 from:

  ```csharp
  var worldRT = rtm.renderTexture;
  ```

  to:

  ```csharp
  // mainImage.texture is the texture the prefab assigned to the game's display RawImage.
  // It can differ from rtm.renderTexture (the C# field) in the prefab; the game blits between them.
  var worldRT = mainImage.texture as RenderTexture ?? rtm.renderTexture;
  ```

  Update line 150's compare to match (the assignment already uses the same `worldRT` variable, so just the source changes).

- [ ] **Step 1.4** (only if H1 wins). Build, deploy, launch on 21:9. Confirm world view is now visible behind the HUD across the full screen. Commit:

  ```bash
  git add Patches/UltrawidePatch.cs
  git commit -m "ultrawide: bind underlay texture to mainImage.texture (H1 fix)"
  ```

  **If world view is now visible: skip to Task 2.** Otherwise continue to Hypothesis 2.

**Hypothesis 2: disabling `mainImage.enabled` breaks the camera→RT pipeline.**

- [ ] **Step 1.5.** Replace `HideMainImage` in `Patches/UltrawidePatch.cs:196-204` with an off-screen-move version (keep `enabled = true`, push the parent Main off-screen):

  ```csharp
  static void HideMainImage(UnityEngine.UI.RawImage mainImage)
  {
      var mainRT = mainImage.rectTransform;
      if (_hiddenMainImage != mainImage)
      {
          _hiddenMainImage = mainImage;
          _hiddenMainImageWasEnabled = mainImage.enabled;
          _hiddenMainOriginalAnchoredPos = mainRT.anchoredPosition;
      }
      // Leave enabled = true so the camera→RT pipeline keeps running, but move the
      // 16:9 squashed display far off-screen so it doesn't overlap the underlay.
      mainRT.anchoredPosition = new Vector2(0f, -10000f);
  }
  ```

  Add the new field at line 118-area:

  ```csharp
  static Vector2 _hiddenMainOriginalAnchoredPos;
  ```

  Update `RestoreAll` at line 244-249 to also restore the position:

  ```csharp
  internal static void RestoreAll()
  {
      if (_hiddenMainImage != null)
      {
          _hiddenMainImage.enabled = _hiddenMainImageWasEnabled;
          _hiddenMainImage.rectTransform.anchoredPosition = _hiddenMainOriginalAnchoredPos;
          _hiddenMainImage = null;
      }
      if (_underlayCanvasGo != null) Object.Destroy(_underlayCanvasGo);
      if (_ultrawideUnderlay != null) Object.Destroy(_ultrawideUnderlay);
      _underlayCanvasGo = null;
      _ultrawideUnderlay = null;
      _underlayRawImage = null;
  }
  ```

- [ ] **Step 1.6.** Build, deploy, launch on 21:9. Inspect.

  - If world view appears in underlay: H2 wins. Commit:
    ```bash
    git add Patches/UltrawidePatch.cs
    git commit -m "ultrawide: move Main off-screen instead of disabling (H2 fix)"
    ```
    Skip to Task 2.
  - If still black: continue to Hypothesis 3.

**Hypothesis 3: RT reference captured before game writes to it.**

- [ ] **Step 1.7.** Defer the texture capture until the first frame after underlay creation. Replace the eager `worldRT` capture in `EnsureUltrawideUnderlay` with a lazy refresh in a per-frame check. In `EnsureUltrawideUnderlay`, drop the early `if (worldRT == null) return;` guard and unconditionally re-assign every refresh:

  ```csharp
  // Re-grab worldRT every refresh — defends against the underlying RT being recreated
  // by SetRenderTexture (Release → Create) when settings change.
  if (_underlayRawImage != null)
  {
      var rt = (mainImage.texture as RenderTexture) ?? rtm.renderTexture;
      if (rt != null && _underlayRawImage.texture != rt)
          _underlayRawImage.texture = rt;
  }
  ```

  Also add a one-shot `Plugin.StartCoroutine`-style retry from `LevelGeneratorUltrawidePatch.Postfix` that calls `RefreshAll()` after a few frames to ensure the RT has been written.

- [ ] **Step 1.8.** Build, deploy, launch. Same go/no-go: if world view appears, commit and proceed to Task 2; else continue.

**Hypothesis 4: try `overlayRawImage.texture`.**

- [ ] **Step 1.9.** As a last resort, try the other texture reference. In `EnsureUltrawideUnderlay`, change `worldRT` to `rtm.overlayRawImage.texture as RenderTexture`. Build, deploy, test.

- [ ] **Step 1.10.** **If all four hypotheses fail:** stop. The Mode A architecture as designed is unbuildable on this game. Update the spec's "Open implementation risk" section, escalate to user, and pivot to TrueAnchor-only (skip to Task 4 and ship without Mode A as the default — the design needs revisit before we know whether to fall back to Mode B as default or leave ultrawide unfixed for mod-compat-priority users).

**Verification (whichever hypothesis won):**

- [ ] **Step 1.11.** On the 21:9 monitor, the world view is visible across the full screen, HUD elements are present and positioned in the centered 16:9 region, no obvious distortion or flicker.
- [ ] **Step 1.12.** Press F10. Confirm vanilla 16:9 letterboxed view returns (the underlay disappears, Main's RawImage is back to its original state).
- [ ] **Step 1.13.** Press F10 again. Confirm Mode A re-engages cleanly.

---

## Task 2: Implement Mode B (TrueAnchor) — stretch Main

New code path that stretches `Render Texture Main` and `Render Texture Overlay` `sizeDelta` to fill the outer Canvas. Underlay is disabled in this mode (Main covers the screen on its own).

**Files:**
- Modify: `Patches/UltrawidePatch.cs` — add new `UltrawideStretchMain` static class

- [ ] **Step 2.1.** Add a new static class to `Patches/UltrawidePatch.cs`, below `UltrawideCanvasFix`:

  ```csharp
  // ---
  // TrueAnchor mode — stretch Render Texture Main + Overlay to fill the outer Canvas.
  // HUD elements that are children of Main reposition per their existing anchor presets.
  // Corner-anchored HUD goes to true screen corners (the AAA-style ultrawide look).
  // ---
  internal static class UltrawideStretchMain
  {
      static RectTransform? _mainRT;
      static RectTransform? _overlayRT;
      static Vector2 _vanillaMainSize;
      static Vector2 _vanillaOverlaySize;
      static bool _captured;

      internal static void Apply()
      {
          var rtm = RenderTextureMain.instance;
          if (rtm == null || rtm.overlayRawImage == null) return;
          _overlayRT = rtm.overlayRawImage.rectTransform;
          _mainRT = _overlayRT.parent as RectTransform;
          if (_mainRT == null) return;

          if (!_captured)
          {
              _vanillaMainSize = _mainRT.sizeDelta;
              _vanillaOverlaySize = _overlayRT.sizeDelta;
              _captured = true;
          }

          // The outer Canvas's RectTransform.sizeDelta is already screen-aspect via
          // the height-match scaler. Read it at apply time — don't hardcode.
          var outerCanvas = _mainRT.GetComponentInParent<Canvas>();
          if (outerCanvas == null) return;
          var canvasRT = outerCanvas.GetComponent<RectTransform>();
          if (canvasRT == null) return;

          var target = canvasRT.sizeDelta;
          _mainRT.sizeDelta = target;
          _overlayRT.sizeDelta = target;
      }

      internal static void Restore()
      {
          if (_captured && _mainRT != null && _overlayRT != null)
          {
              _mainRT.sizeDelta = _vanillaMainSize;
              _overlayRT.sizeDelta = _vanillaOverlaySize;
          }
      }

      // Restore + forget. Used on F10 / mod disable.
      internal static void RestoreAndClear()
      {
          Restore();
          _mainRT = null;
          _overlayRT = null;
          _captured = false;
      }
  }
  ```

- [ ] **Step 2.2.** Build the project. Confirm no compile errors. Don't deploy yet — Mode B isn't wired up to a flag, so it'd be dead code in this commit.

- [ ] **Step 2.3.** Commit:

  ```bash
  git add Patches/UltrawidePatch.cs
  git commit -m "ultrawide: add UltrawideStretchMain for Mode B (TrueAnchor)"
  ```

---

## Task 3: Settings plumbing for TrueAnchorMode

Add the config flag.

**Files:**
- Modify: `SettingsFile.cs` — add `trueAnchorMode` field
- Modify: `Settings.cs` — add `TrueAnchorMode` accessor

- [ ] **Step 3.1.** In `SettingsFile.cs`, near line 148 (where `ultrawideUiFix` is declared), add:

  ```csharp
  public bool trueAnchorMode = false;
  ```

  Default `false` per spec — Mode A is the default.

- [ ] **Step 3.2.** In `Settings.cs`, near line 151-155 (where `UltrawideUiFix` is declared), add:

  ```csharp
  internal static bool TrueAnchorMode
  {
      get => D.trueAnchorMode;
      set { D.trueAnchorMode = value; _file.Save(); OnSettingTweaked(); }
  }
  ```

- [ ] **Step 3.3.** Build. Confirm no compile errors. Commit:

  ```bash
  git add Settings.cs SettingsFile.cs
  git commit -m "ultrawide: add TrueAnchorMode setting (default off)"
  ```

---

## Task 4: Wire mode dispatch in UltrawidePatch

`UltrawideCanvasFix.RefreshAll` currently does Mode A only. Extend to dispatch between Mode A (when `TrueAnchorMode` is false) and Mode B (when true). Each mode tears down the other's effects on switch.

**Files:**
- Modify: `Patches/UltrawidePatch.cs` — `UltrawideCanvasFix.RefreshAll`

- [ ] **Step 4.1.** Replace `RefreshAll` (currently lines 120-129) with mode dispatch:

  ```csharp
  internal static void RefreshAll()
  {
      bool active = Settings.ModEnabled && Settings.UltrawideUiFix
                    && IsUltrawide();

      if (!active)
      {
          UltrawideStretchMain.RestoreAndClear();
          RestoreAll();
          return;
      }

      DiagnoseOverlayHierarchy();

      if (Settings.TrueAnchorMode)
      {
          // Mode B: stretch Main, no underlay. Restore Mode A state if we were in it.
          RestoreAll();
          UltrawideStretchMain.Apply();
      }
      else
      {
          // Mode A: underlay layer, Main untouched. Restore Mode B state if we were in it.
          UltrawideStretchMain.Restore();
          EnsureUltrawideUnderlay();
      }
  }
  ```

  Note: `UltrawideStretchMain.Restore()` (called on B→A) intentionally keeps `_captured = true` so a subsequent A→B doesn't lose the vanilla sizes. Only `RestoreAndClear` (called on full deactivation) clears the capture.

- [ ] **Step 4.2.** Build. Confirm no compile errors.

- [ ] **Step 4.3.** Deploy. Manually flip `D.trueAnchorMode = true` in `~/AppData/Roaming/com.kesomannen.gale/repo/profiles/Development/BepInEx/config/<plugin-config-file>` (or use a debug toggle if quicker), launch on 21:9. Verify:
  - With `trueAnchorMode = false`: same as Task 1 result (Mode A underlay).
  - With `trueAnchorMode = true`: Main stretches to fill screen. World view is undistorted. HUD elements at corners go to true screen corners.

- [ ] **Step 4.4.** Commit:

  ```bash
  git add Patches/UltrawidePatch.cs
  git commit -m "ultrawide: dispatch between Mode A (underlay) and Mode B (stretch) via TrueAnchorMode"
  ```

---

## Task 5: F10 restore extension

F10 already calls `UltrawideCanvasFix.RestoreAll()` indirectly (via `Settings.ModEnabled = false` → `OnSettingsChanged` → `RefreshAll` → `!active` branch). With Task 4's `RestoreAndClear`, the Mode B vanilla sizes are also restored. Just verify the path runs end-to-end.

**Files:** none — verification only.

- [ ] **Step 5.1.** With `TrueAnchorMode = true` and active on 21:9, press F10. Observe:
  - Main returns to 750×418 (HUD elements snap back to centered 16:9 box).
  - Underlay Canvas is destroyed (no longer in scene).
  - Vanilla 16:9 letterboxed view is showing.

- [ ] **Step 5.2.** Press F10 again. Observe Mode B re-engages cleanly: Main stretches, HUD goes to corners.

- [ ] **Step 5.3.** Toggle `TrueAnchorMode` to `false` (still F10-active, mod on). Observe instant switch to Mode A — Main shrinks back to 750×418, underlay Canvas spawns, world view fills screen via underlay.

- [ ] **Step 5.4.** Toggle back to `true`. Observe Mode B re-engages — underlay is destroyed, Main stretches.

If any of the above fails, the dispatch logic in Task 4 needs revisiting (most likely culprit: `_captured` state lifecycle).

---

## Task 6: Menu UI — `TrueAnchorMode` toggle

Add a checkbox next to the existing `Ultra-Wide UI Fix` toggle in the F1 menu Display section.

**Files:**
- Modify: `MenuIntegration.cs`

- [ ] **Step 6.1.** At line ~28 (where `_ultrawideUiToggle` is declared), add:

  ```csharp
  private static REPOToggle? _trueAnchorToggle;
  ```

- [ ] **Step 6.2.** At line 219 (right after the `Ultra-Wide UI Fix` toggle), add:

  ```csharp
  AddModToggle("True-corner HUD anchoring",
      "AAA-style: HUD anchors to actual screen corners. May affect HUD-modifying mods.",
      Settings.TrueAnchorMode,
      b => ModSet(() => Settings.TrueAnchorMode = b),
      out _trueAnchorToggle);
  ```

  **Note:** check the actual signature of `AddModToggle` — the existing call at line 218 has 3 args (`name, value, callback, out toggle`). If there's no overload that takes a tooltip string, omit it (the existing toggle has no tooltip either) and add the warning to the label: `"True-corner HUD anchoring (may affect mods)"`.

- [ ] **Step 6.3.** At line 545 (in `SyncAll`, where `_ultrawideUiToggle?.SetState` lives), add:

  ```csharp
  _trueAnchorToggle?.SetState(Settings.TrueAnchorMode, false);
  ```

- [ ] **Step 6.4.** Build, deploy, launch. Open F1 menu → Display section. Verify:
  - `True-corner HUD anchoring` checkbox is visible directly below `Ultra-Wide UI Fix`.
  - Toggling it on a 21:9 monitor switches Mode A ↔ Mode B at runtime (no menu close needed).
  - Checkbox state persists across game restarts (the `_file.Save()` in the setter handles this).

- [ ] **Step 6.5.** Commit:

  ```bash
  git add MenuIntegration.cs
  git commit -m "ultrawide: add True-corner HUD anchoring toggle in Display menu"
  ```

---

## Task 7: 16:9 monitor regression check

Both modes are no-ops below the 1.85 ultrawide threshold. Verify on a 16:9 display (or by forcing the resolution picker to a 16:9 mode on a 21:9 monitor).

**Files:** none.

- [ ] **Step 7.1.** Set resolution to 1920×1080 (16:9). Launch. Open F1 → Display. Verify:
  - Both modes inactive — no underlay Canvas in scene, Main is at vanilla 750×418, HUD looks identical to vanilla.
  - Toggling `TrueAnchorMode` on does nothing visible (gate kicks in before either mode applies).
  - Toggling `Ultra-Wide UI Fix` off does nothing visible (already inactive).

- [ ] **Step 7.2.** Set resolution back to 3440×1440 (21:9). Verify both modes work as expected per Task 4 / Task 6 results.

---

## Task 8: Mod compat smoke test

Confirm that Mode A is invisible to a HUD-modifying mod. The user's Thunderstore-tracked `Development` profile already has several mods installed; pick the most HUD-touching one (e.g. `REPOConfig`, `BetterChat`, or whatever HUD-customizing mod is on hand).

**Files:** none.

- [ ] **Step 8.1.** With `Ultra-Wide UI Fix` on, `TrueAnchorMode` off (Mode A), 21:9 monitor: install/enable the chosen HUD-mod. Launch. Verify the mod's UI elements appear at their expected positions inside the 16:9 region. If positions are visibly shifted from where they appear on a 16:9 monitor (testable by toggling resolution back to 1920×1080), that's a Mode A bug — investigate.

- [ ] **Step 8.2.** Toggle `TrueAnchorMode` on (Mode B). Verify the mod's UI elements may shift (this is expected — the toggle's tooltip warns about this) but the game doesn't crash and HUD remains functional.

- [ ] **Step 8.3.** Document any specific mod incompatibilities discovered in the spec's "Risks" section as future-self notes.

---

## Task 9: CHANGELOG

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 9.1.** Add a bullet under the next pending version section (or create a new `[Unreleased]` section if there isn't one). Match existing style:

  ```markdown
  - **Ultrawide support (21:9 / 32:9):** new render mode for ultrawide monitors. World view extends to full screen aspect; HUD stays in centered 16:9 region by default (mod-safe). Optional **True-corner HUD anchoring** toggle (Display menu) re-anchors HUD to actual screen corners — AAA-style look, may affect HUD-modifying mods.
  ```

- [ ] **Step 9.2.** Commit:

  ```bash
  git add CHANGELOG.md
  git commit -m "changelog: ultrawide support (S2 default + TrueAnchor toggle)"
  ```

---

## Out of scope

- **Sorting unrelated WIP commits** off the `ultrawide` branch (F11Target, AutoTuneRevision, CpuPatchesF11Disabled, FrameTimeMeter). Per spec, this is housekeeping handled separately before merging to main.
- **Death-cam farClip backport** to public. Independent fix listed in `project_known_issues.md`; not gated on this work.
- **Tooltip parameter on `AddModToggle`** — if the existing API doesn't take one, the plan accepts a degraded label. Don't expand the toggle API just for this feature.
- **Per-mod compat shims** — if a specific HUD-modifying mod has issues even in Mode A, that's a separate investigation, not part of this implementation.
- **32:9-specific tuning** — same code path as 21:9 with a larger aspect; no first-party validation hardware available.
- **Menus / pause / death-cam behavior at ultrawide** — out of spec scope. They inherit whatever Mode A/B is doing for the world canvas.

---

## Self-review notes

- Spec coverage: Part A (foundation) is "already shipping" → no task. Part B (Mode A) → Task 1. Part C (Mode B) → Tasks 2 + 4. Part D (Config + UI) → Tasks 3, 4, 6. Part E (Activation gate) → already in `IsUltrawide()`, regression-checked in Task 7. Part F (F10 restore) → Task 5.
- Type consistency: `UltrawideStretchMain.Apply` / `Restore` / `RestoreAndClear` used consistently in Task 2 + Task 4. `Settings.TrueAnchorMode` used consistently in Tasks 3, 4, 6.
- No-placeholder check: every code change shows the actual code; every test step has a concrete pass/fail criterion; every commit step has the actual command. The H1-H4 conditional flow is the one place where a step can be skipped — that's by design (decision points), not a placeholder.

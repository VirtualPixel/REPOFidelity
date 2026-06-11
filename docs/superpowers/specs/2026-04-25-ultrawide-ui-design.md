# Ultrawide Support - Design

**Date:** 2026-04-25
**Status:** Design
**Relates to:** local `ultrawide` branch (commit `c546850`), memory `project_ultrawide_progress.md`, `project_repo_render_architecture.md`

## Problem

R.E.P.O.'s built-in display chain pins the world view to a fixed 750×418 (16:9) `Render Texture Main` RawImage inside the outer `Canvas`. The outer Canvas already auto-fits the screen aspect via height-match scaler (e.g. 998.56×418 at 21:9), and the camera already renders the wider field of view automatically (`cam.aspect = 2.389` confirmed at 21:9). The bottleneck is purely the inner display container: on an ultrawide monitor the world image gets squashed/cropped into the 16:9 box and the rest of the screen is dead space.

Four community mods already address this (`REPOTrueUltrawide`, `REPOUltrawide`, `FovUpdate`, `UltrawideOrLongFix`). All four take the same approach: stretch `Render Texture Main` to fill the outer Canvas. That works visually but moves the HUD with it - HUD elements are children of `Render Texture Main`, so any element using non-corner anchor presets gets distorted, and any mod that hardcoded positions to Main's 750×418 breaks. None of the existing mods preserve mod compatibility as a first-class concern.

REPOFidelity's ultrawide goal differs: ship 21:9 and wider support **without breaking other mods that hook the existing UI hierarchy**, while still optionally offering the AAA-style true-screen-corner anchoring for users who want it.

## Goal

Two render modes, runtime-toggleable via one config flag:

- **S2 (default).** UI hierarchy is left untouched - Main stays at 750×418, HUD elements stay where they are. The world view extends past Main into the wider screen via a separate underlay layer behind the game's Canvas. Mod-safe by construction.
- **TrueAnchorMode (opt-in).** Stretch `Render Texture Main` (and child `Render Texture Overlay`) to fill the outer Canvas. HUD elements that are children of Main reposition naturally per their existing anchor presets - corner-anchored go to true screen corners. The AAA look, accepting the mod-compat risk by user opt-in.

Aspect ratio handling is dynamic (any aspect wider than 16:9, no per-aspect special cases).

## Non-goals

- No reparenting of existing HUD elements out of Main's hierarchy. The toggle changes Main's size, not the parent of any UI element.
- No new world camera or duplicate world rendering. Both modes use the existing single camera and existing RT.
- No changes to menus' built-in scaling - pause menu, settings, start/death screens remain whatever they currently do (S2 keeps them inside the 16:9 Main; TrueAnchor lets them inhabit the stretched Main).
- No fix for community mods that already break themselves on ultrawide. Compatibility is best-effort, not adversarial.
- No 4:3 or vertical-monitor support. Aspect < 16:9 is a no-op (vanilla render).

## Design

### Part A - Common foundation (already shipping on `ultrawide` branch)

These pieces have already landed in commit `c546850` and are unchanged by this spec:

- **FOV slider** in the F1 Display section. Postfix patches `CameraZoom.Awake` and pushes `zoomPrev`/`zoomNew`/`zoomLerp` directly so post-Awake writes propagate. Default 0 = use the game's per-player default.
- **Native-aspect resolution picker.** Filters monitor's reported modes to its native aspect (±0.05 tolerance), synthesizes 50%/67%/75%/83% downscales snapped to 8-pixel boundaries, ensuring 21:9 and 32:9 monitors aren't stuck with 2-3 entries.
- **`RenderTexturePatch.PostfixUpdate`** already overrides `RenderTextureMain.textureWidthOriginal/Original` to `Settings.OutputWidth/Height` (= `Screen.width/height`), so the underlying RT IS sized to screen aspect (3440×1440 confirmed at 21:9). No spec work needed here.

### Part B - Mode A (S2): underlay layer

**B1. Dedicated underlay Canvas.**
Spawn a new Canvas named `REPOFidelity Ultrawide Canvas` with `RenderMode.ScreenSpaceOverlay`, `sortOrder = 0`, `DontDestroyOnLoad`, `CanvasScaler.ConstantPixelSize`. Game's main Canvas is `sortOrder = 1`, so the underlay draws below it and HUD draws on top. Already implemented.

**B2. Underlay RawImage.**
Child of the underlay Canvas, named `REPOFidelity Ultrawide Underlay`. Anchored full-stretch (`anchorMin=(0,0)`, `anchorMax=(1,1)`, all offsets 0) so it fills the screen at any aspect. Already implemented.

**B3. Bind the underlay's texture to the world's RT.**
This is the open bug. The branch currently sets `_underlayRawImage.texture = RenderTextureMain.instance.renderTexture` and the result is black. Four hypotheses are documented in memory; they must be tested in this order:

1. Repoint at `mainImage.texture` (the prefab-assigned texture on the game's RawImage). The diagnostic log line added in `c546850` reports whether `worldRT == mainImage.tex`; first action on resuming work is to read that log and pick.
2. Leave game's `mainImage.enabled = true` and instead move it off-screen (`anchoredPosition` very high) - guards against game code that conditionally writes the RT only when its display is enabled.
3. Capture the RT reference lazily (first frame after game scene loads) instead of at patch-init time.
4. Try `RenderTextureMain.instance.overlayRawImage.texture` - the names suggest the swap may be inverted.

If all four fail, Mode A is unbuildable on the current architecture; design collapses to TrueAnchor-only and we revisit. Estimated probability all four fail: low (~5%) - community mods successfully render the world via the same RT.

**B4. Game's `mainImage` handling.**
On Mode A activation, `mainImage.enabled` is set to whichever value Phase 1 testing settles on:
- If H1 wins (texture ref was wrong, swapping to `mainImage.texture` shows the world): `enabled = false` is fine, the underlay carries the world view.
- If H2 wins (game stops writing the RT when `mainImage.enabled = false`): leave `enabled = true` and instead push `Render Texture Main`'s `anchoredPosition` far off-screen (e.g. `(0, -10000)`) so the 16:9 squashed view doesn't overlap the underlay.
- Original `mainImage.enabled` and `Render Texture Main`'s `anchoredPosition` are both captured at first activation for F10 restore.

The spec assumes one of H1 or H2 wins; if neither does and we reach H3/H4, the same pattern applies (capture-and-restore the relevant fields).

### Part C - Mode B (TrueAnchor): stretch-Main

**C1. Capture vanilla sizes.**
On first patch run (before any mode mutates state), record `Render Texture Main` and `Render Texture Overlay` `RectTransform.sizeDelta` to `_vanillaMainSize` and `_vanillaOverlaySize`. These persist across mode switches and are written back on Mode B deactivation and on F10 restore.

**C2. Compute target stretched size.**
Read the outer Canvas's `RectTransform.sizeDelta` directly at the moment of mode activation. Its height-match Canvas Scaler has already done the math: at 3440×1440 the runtime-observed value is `(998.56, 418)`; at 5120×1440 (32:9) it scales to `(1488.89, 418)`. The 418 height is whatever the game's reference-resolution-derived UI-coord height happens to be - read it from the Canvas, don't hardcode it.

**C3. Apply stretched size.**
Set `Main.RectTransform.sizeDelta = canvasUISize`, same for `Overlay`. HUD elements inside Main reposition per their own anchor presets. Failed approach #5 in the memory already proved this works visually for the world image - the "failure" was the user wanting S2 instead.

**C4. Disable underlay in Mode B.**
The underlay Canvas's GameObject is set inactive when Mode B activates (it would just be drawing the same world below an opaque full-screen Main, wasting fill rate). Re-enabled when switching back to Mode A.

### Part D - Config and UI

**D1. Config flag.**
`bool TrueAnchorMode` in `Settings.cs`. Default `false`. Persisted to BepInEx config, no restart required.

**D2. Toggle UI.**
Checkbox in the F1 Display section, alongside the existing FOV slider. Label: *"True-corner HUD anchoring (ultrawide)"*. Tooltip: *"AAA-style: HUD anchors to actual screen corners. May affect HUD-modifying mods."* Visible only when current screen aspect > 16:9 (no-op below that, hide to avoid confusion).

**D3. Runtime mode switch.**
On `TrueAnchorMode` value change:
- A → B: disable underlay Canvas. Write stretched sizes into Main+Overlay. Force `mainImage.enabled = true` and `Render Texture Main.anchoredPosition` back to its captured vanilla position (Mode B requires Main to be visible and on-screen - it IS the world view).
- B → A: write `_vanillaMainSize`/`_vanillaOverlaySize` back to Main+Overlay. Re-enable underlay Canvas. Set `mainImage.enabled` and `anchoredPosition` to Mode A's chosen state per B4.

No frame delay needed - RectTransform writes apply on the next layout pass.

### Part E - Activation gate

Both modes are no-ops when current `Screen.width / Screen.height ≤ 16/9 + 0.01`. Aspect check runs once on patch init and on resolution change (the resolution picker already raises a change event we can subscribe to). On 16:9 monitors, this entire system stays dormant; on 4:3/vertical monitors, also dormant (vanilla render).

### Part F - F10 restore

Existing F10 already restores vanilla state (mainImage.enabled, etc.). Extend to also:
- Set `Main`/`Overlay` sizeDelta back to `_vanillaMainSize`/`_vanillaOverlaySize` if they were modified.
- Disable the ultrawide underlay Canvas.

## Implementation order

Phase 1 - finish Mode A (S2):
1. Read the `[ultrawide] underlay live` diagnostic log from the last build to determine `worldRT == mainImage.tex` outcome.
2. Apply hypothesis 1 (or whichever the log indicates), test on 21:9 monitor.
3. If still black, walk hypotheses 2 → 3 → 4 in order.
4. Once underlay shows the world, commit fix and verify HUD still renders correctly on top.

Phase 2 - implement Mode B (TrueAnchor):
5. Add `_vanillaMainSize`/`_vanillaOverlaySize` capture on patch init.
6. Implement stretched-size apply on Mode B activation.
7. Implement underlay enable/disable on mode switch.
8. Add F10 restore extension.

Phase 3 - config UI and gating:
9. Add `TrueAnchorMode` config flag and toggle UI in F1 menu Display section.
10. Add aspect-ratio activation gate.
11. Wire toggle change → mode switch.

Phase 4 - verify and ship:
12. Test matrix: 16:9 (no change), 21:9 Mode A, 21:9 Mode B, runtime A↔B toggle, F10 restore.
13. Mod compat smoke test: install one popular HUD-modifying mod (e.g. REPOConfig), verify Mode A is invisible to it.

Outside this implementation plan (housekeeping, separate task before merge to main):
- Sort the unrelated WIP commits on the `ultrawide` branch (F11Target enum, AutoTuneRevision bump, CpuPatchesF11Disabled, FrameTimeMeter instrumentation) into separate commits or a separate branch.

## Testing

- **16:9 monitor:** no visible change either mode. Both modes inactive (gate per Part E).
- **21:9 monitor Mode A:** world view extends to full screen via underlay; HUD pinned in centered 16:9 region (game's Main untouched).
- **21:9 monitor Mode B:** world fills screen via stretched Main; HUD elements at true screen corners or per their original anchor presets.
- **Runtime A↔B toggle:** HUD visibly snaps; underlay appears/disappears. No restart required.
- **F10 restore:** vanilla restored regardless of which mode was active when pressed.
- **32:9 (no first-party hardware):** validate via reports from existing community mods plus the dynamic AR2 math; same code path as 21:9 with a larger aspect number.
- **Mod compat smoke test:** one HUD-modifying mod installed, Mode A active, mod's UI elements appear in their expected positions inside the 16:9 region.

## Risks

- **Underlay-black bug unresolvable.** All four hypotheses fail. Mitigation: design collapses to TrueAnchor-only and we revisit Part B. ~5% probability based on community mods successfully using the same RT.
- **TrueAnchor distorts a HUD element we don't anticipate.** A vanilla HUD element with stretch-anchors (e.g. a full-width banner) would horizontally distort. Mitigation: visual smoke test on 21:9 with TrueAnchor on; if a problem element exists, document it in the toggle's tooltip rather than try to special-case it.
- **Mod compat in TrueAnchor is user's problem.** The toggle is opt-in and the tooltip warns. Not a design risk.
- **Future game update changes the UI hierarchy.** Both modes hook by GameObject path / known component reference. A REPO update that renames `Render Texture Main` or restructures the Canvas would break both modes simultaneously. Mitigation: existing diagnostic logs make the failure mode obvious; fix is a path update.

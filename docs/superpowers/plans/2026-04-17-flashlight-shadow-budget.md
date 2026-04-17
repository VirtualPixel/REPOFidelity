# Flashlight Shadow Budget Implementation Plan

> **Context:** Bug report from v0.2/v0.3 (yoe): 8+ players + nametag mod = 50fps. Even at shipping version, 20 players' flashlight shadows (20 × 2048² on Potato-Low, 20 × 4096² on Ultra) scale linearly and overwhelm mid-range GPUs. Need to cap concurrent flashlight shadows to a fixed budget.

**Goal:** Limit simultaneous flashlight shadow maps to 4 closest-to-camera instances. Rest stay visually lit (cone unchanged) but drop `LightShadows.None`.

**Design summary:**
- Per-10Hz tick, sort active flashlights by distance to main camera
- Closest 4 keep original shadow mode
- Rest: `LightShadows.None`
- Save original `LightShadows` per flashlight on first touch; F10 revert restores

**Files:**
- Modify: `Patches/PerformancePatch.cs` — new method + call site
- Modify: `Settings.cs` — new `PerfOpt.FlashlightShadowBudget` + tiering + perf toggle
- Modify: `SettingsFile.cs` — new `perfFlashlightShadowBudget` field
- Modify: `UpscalerManager.cs` — hook into existing 10Hz tick
- Modify: `CHANGELOG.md` — bullet

---

## Task 1: Settings plumbing

- [ ] Add `DistanceShadowCulling`-style enum member `FlashlightShadowBudget` to `Settings.PerfOpt`
- [ ] Add `D.perfFlashlightShadowBudget` to `SettingsFile` (default `-1`, follows preset)
- [ ] Add public accessor `Settings.PerfFlashlightShadowBudget`
- [ ] Add to `ShouldOptimize` switch (auto toggle read) and tier logic (`level >= 0` = always on)
- [ ] Track in `PerfSettingsWatcher` so flag flips re-apply

## Task 2: Flashlight budget logic

- [ ] New `static readonly Dictionary<Light, LightShadows> _flashlightBudgetOrig` in `SceneOptimizer`
- [ ] New method `UpdateFlashlightShadowBudget(Camera? cam)`:
  - `if (!ShouldOptimize) → RestoreFlashlightBudget; return;`
  - Gather candidate list: `FindObjectsOfType<FlashlightController>()`, skip null spotlights, skip disabled/inactive
  - Compute distance to cam.position per spotlight, sort ascending
  - `const int BudgetN = 4;`
  - For index < BudgetN: if currently None and we have original tracked, restore original; else leave as-is
  - For index ≥ BudgetN: if currently not None, save original (if not already), set to None
- [ ] New method `RestoreFlashlightBudget()`: iterate dict, restore each, clear
- [ ] Expose `AvatarFlashlightCount => _flashlightBudgetOrig.Count` for the diagnostic log (or just include in `LogRestoreState` directly)

## Task 3: Tick integration

- [ ] In `UpscalerManager.Update`, the existing 10Hz block calls `UpdateShadowBudget` and `UpdateDistanceShadowCull`. Add `UpdateFlashlightShadowBudget` right after.

## Task 4: F10 revert + diagnostic

- [ ] In `LogRestoreState`, add `flashBudget={_flashlightBudgetOrig.Count}` line. Include in `mutations` total.
- [ ] No separate F10 hook needed — restore path fires on next tick when `ShouldOptimize` returns false (`Settings.ModEnabled = false` propagates).

## Task 5: Verification

- [ ] Build
- [ ] Manual test: spin up a multiplayer lobby (or single-player for baseline) → confirm log shows `flashBudget=0` on first tick with ≤4 players, `flashBudget=N` when N players have flashlights active
- [ ] Visual: in a lobby with 5+ players, distant flashlights should have lit cones but no shadow on walls
- [ ] F9 sanity: no regression on frame time with 1 player

## Task 6: CHANGELOG

- [ ] Add bullet under the 1.4.0 section.

---

## Out of scope

- Dynamic budget based on player count (fixed 4 is simpler and predictable)
- Fading shadow strength in/out during distance transitions (existing small-light budget does this; flashlights toggle cleanly since they're usually either close or not)
- Budget sharing with the small-point-light system
- Extending to static level spot lamps (design choice: they're not user-driven, shouldn't compete)

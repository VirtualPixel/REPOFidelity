# Per-Range Shadow Resolution Implementation Plan

> **Context:** Optimization #2 from F9 run 2026-04-17 analysis. Current `SetLightShadowResolution` applies only on Ultra and uses coarse buckets. Rework to range-driven brackets that apply on every preset, with per-preset overrides.

**Goal:** Replace the current Ultra-only, purpose-categorized shadow-resolution logic with range-driven brackets applied across all presets, Flashlight exempt on Ultra, Potato capped.

**Reference spec:** F9 run analysis 2026-04-17, optimization #2.

**Current code:** `Patches/QualityPatch.cs:230-289` (`ApplyShadowResolution` + `SetLightShadowResolution`).

---

## Design summary

| Range | Resolution (default) | Potato cap applied |
|---|---|---|
| `< 5m` | 256 | 256 |
| `5–10m` | 512 | 512 |
| `10–20m` | 1024 | 1024 |
| `≥ 20m` | 2048 | **1024** |

**Flashlight:** always 4096 when `ResolvedShadowQuality == Ultra`. Otherwise follows the table above.
**Directional:** `shadowCustomResolution = 0` (use global + cascades). Unchanged.
**Zero-intensity:** `shadows = LightShadows.None`. Unchanged.

---

## Task 1: Track Flashlight lights

**Files:** `Patches/QualityPatch.cs`

- [ ] Add a static `HashSet<Light> _flashlightLights` field at class scope.
- [ ] Add `RefreshFlashlightLights()` that clears the set and repopulates from `Object.FindObjectsOfType<FlashlightController>()`, adding each `fl.spotlight` if non-null.
- [ ] Log the count on refresh (helps confirm detection in logs).

## Task 2: Rewrite `SetLightShadowResolution` → `ApplyRangeTieredLightShadows`

**Files:** `Patches/QualityPatch.cs`

- [ ] Rename and replace the method. New signature: no parameters — reads preset/quality from Settings directly.
- [ ] Call `RefreshFlashlightLights()` at start.
- [ ] For each `Light` in scene:
  1. Zero-intensity shadows → `LightShadows.None` (preserve existing behavior).
  2. Directional → `shadowCustomResolution = 0`, continue.
  3. `ResolvedShadowQuality == Ultra` AND light in `_flashlightLights` → `shadowCustomResolution = 4096`, continue.
  4. Compute bracket from `light.range` (use the table).
  5. If `Settings.Preset == QualityPreset.Potato` apply `Mathf.Min(res, 1024)`.
  6. Assign `shadowCustomResolution = res`.
- [ ] Drop the `_ultraShadowsApplied` early-return — the new method is cheap enough and consistent behavior matters more than skipping.

## Task 3: Call on every preset in `ApplyShadowResolution`

**Files:** `Patches/QualityPatch.cs:230-255`

- [ ] Remove per-preset `SetLightShadowResolution(N)` calls.
- [ ] After the `switch` that sets `QualitySettings.shadowResolution` / `shadowCascades`, call `ApplyRangeTieredLightShadows()` unconditionally.

## Task 4: Build + F9 measurement checkpoint

- [ ] Build (`dotnet build`) — confirm no errors.
- [ ] F9 probe from same truck spot, same starting preset.
- [ ] Compare per-light shadow cost proxy section to previous run. Expected:
  - Flashlight score: Ultra unchanged (4096 → 100), High/Medium/Low drops (2048 → 25).
  - Big Lamp / Ceiling Lamp (7m range) drops from 2048 → 512 on all presets.
  - Button/Screen lights (3m range) drops from 2048 → 256.
  - Matrix cells: meaningful frame-time drop on Ultra@1.1x given Flashlight stays 4096 only there.
- [ ] If Potato cap behavior looks right in logs (range-20m lights show 1024 instead of 2048 on Potato), feature is done.

## Task 5: CHANGELOG

- [ ] Add bullet to the 1.4.0 section.
- [ ] No version bump in commit.

---

## Out of scope

- F10-is-truly-vanilla work (deferred).
- Avatar preview AA (deferred).
- Any dynamic per-frame re-tiering (range doesn't change at runtime, one-shot apply per level is fine).

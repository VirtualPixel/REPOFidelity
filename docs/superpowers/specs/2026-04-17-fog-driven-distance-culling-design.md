# Fog-Driven Distance Culling — Design

**Date:** 2026-04-17
**Status:** Design
**Relates to:** F9 run analysis 2026-04-17, optimization idea #1

## Problem

From the F9 run (4K DLAA, RTX 5090 + 9950X, 3.69 ms / 271 fps, CPU-bound):
1835 of 2300 renderers (80%) cast shadows while off-screen. The main camera takes 1.32 ms (36% of the frame).

Two distinct wastes hide inside that number:

1. **Shadow-distance-past-fog.** On the High preset, `ResolvedShadowDistance = 85m`; on Ultra, `150m`. R.E.P.O.'s indoor fog end is roughly 20–40m. Shadows are being rendered into shadow maps for geometry that is completely invisible behind fog. This affects walls, floors, and every large caster — the biggest single class of waste.

2. **Off-screen props within shadow distance.** Small dynamic props sitting behind the camera, inside fog, still render into shadow maps because their shadows fall inside the camera frustum. No distance knob addresses this; the renderer itself needs toggling off.

Fog is clamped 1.0–1.1x of vanilla today (`Settings.cs:108`), and the CHANGELOG for 1.3.0 claims the slider was "opened up below 1.0×" — but the setter clamp contradicts that. Fixing the clamp also opens up a CPU-bound escape valve: a player on an RX 6400 who sets fog to 0.5x would get proportionally tighter shadow and light ranges as a cascade effect, rather than only the fog shader savings.

## Goal

Make fog the master distance knob. Shadow and light ranges derive from fog end rather than being independently preset-tiered values that can exceed it. Off-screen small props get explicit distance-based shadow culling for the residual waste that no global clamp can reach.

## Non-goals

- No change to how fog itself renders (color, density curve, shader)
- No volumetric fog (future hook mentioned, not built)
- No change to directional-light shadow rendering (sun/sky lights are separate from point/spot and mostly already sensible)
- No LOD changes — we don't own the meshes

## Design

### Part A — Fog as the master distance knob

**A1. Open the fog setter range.**
`Settings.cs:108` currently reads `Mathf.Clamp(value, 1f, 1.1f)`. Change to `Mathf.Clamp(value, 0.3f, 1.1f)`. Lower bound of 0.3x gives a real performance knob without making the game unplayable. Upper bound stays at 1.1x (gameplay advantage). Slider bounds in `MenuIntegration.cs` will be matched to the new range.

**A2. Derive an "effective fog end" value.**
In `UpscalerManager` near the existing fog application (around line 531, where `RenderSettings.fogEndDistance = _vanillaFogEnd * fogMult` lives), compute:

```
effectiveFogEnd = _vanillaFogEnd × ResolvedFogMultiplier
```

This is already the value being assigned to `RenderSettings.fogEndDistance`. Expose it as a new `Settings.ResolvedEffectiveFogEnd` field so downstream systems can read it without re-computing.

**A3. Clamp shadow distance.**
After `effectiveFogEnd` is known, apply a ceiling:

```
ResolvedShadowDistance = Min(presetValue, effectiveFogEnd × 1.1)
```

The `× 1.1` overshoot allows shadows from casters just inside the fog line to still contribute to the visible edge. Preset value stays the lower bound — Potato's 10m is not stretched upward. On Ultra with ~40m vanilla fog, shadow distance drops from 150m to ~44m. On Potato with 10m shadow distance and higher fog, nothing changes.

**A4. Clamp light distance.**
Same approach:

```
ResolvedLightDistance = Min(presetValue, effectiveFogEnd × 1.2)
```

The `× 1.2` factor is slightly more generous than shadows because lights can bloom into fog atmospherically even when the source itself isn't visible. Feeds through the existing `GraphicsManager.UpdateLightDistance` postfix that already writes `ResolvedLightDistance`.

**A5. Recompute on relevant triggers.**
The clamp must re-run when:
- Fog multiplier changes (user tweaks slider)
- Preset changes (resolved values reset)
- Level changes (vanilla fog end may differ per level)
- Custom settings mutation

The existing `Settings.OnSettingTweaked()` path and `UpscalerManager`'s fog setup already fire on these. The clamp lives in one function called from both places.

### Part B — Per-prop distance cull

**B1. Gating flag.**
Add `PerfOpt.DistanceShadowCulling` to the `PerfOpt` enum in `Settings.cs`. Tiered as `level >= 0` — always on when the mod is enabled. Toggleable per-setting in Custom preset like the other `PerfOpt` flags.

**B2. Watchlist capture.**
In `SceneOptimizer.Apply()`, iterate `Object.FindObjectsOfType<MeshRenderer>()`. For each renderer where `bounds.size.magnitude < 2f` AND `shadowCastingMode != Off`, add to a static `List<Renderer> _distanceCullWatchlist`. Clear the list at the start of `Apply()` so level changes rebuild cleanly. The capture must run *after* other passes in `Apply()` that toggle `shadowCastingMode` off (`SetParticleShadows`, `SetTinyRendererShadows`) so renderers those passes have disabled stay excluded from our watchlist.

**B3. Per-frame toggle.**
Add `UpdateDistanceShadowCull(Camera cam)` to `SceneOptimizer`. Called from the same site in `UpscalerManager.cs:374` that calls `UpdateShadowBudget(_camera)` today. Loops the watchlist:

```
threshold = ResolvedShadowDistance × 0.7
thresholdSq = threshold × threshold
hystOnSq = (threshold × 0.9)² // dead band for flicker prevention
camPos = cam.transform.position

for renderer in _distanceCullWatchlist:
    distSq = (renderer.transform.position - camPos).sqrMagnitude
    isOff = renderer.shadowCastingMode == ShadowCastingMode.Off
    if isOff and distSq < hystOnSq:
        renderer.shadowCastingMode = ShadowCastingMode.On
    elif !isOff and distSq > thresholdSq:
        renderer.shadowCastingMode = ShadowCastingMode.Off
```

Uses squared distance to avoid per-frame `Sqrt`. Dead band of 10% prevents flicker when the player lingers at the boundary.

**B4. Disable path.**
When `ModEnabled = false` or `PerfOpt.DistanceShadowCulling = false`, loop the watchlist once and set every entry back to `ShadowCastingMode.On` (we captured only renderers that started as On, so restoring to On is correct). Clear the list. This path fires from `Settings.OnSettingTweaked()` when the flag flips off.

### Integration order

Build, measure, build, measure — not build-all-then-measure. This tells us whether each part is earning its keep.

1. **Part A** alone — implement A1–A5, run F9 at 4K DLAA on the RTX 5090, record frame time, main camera time, and shadow map draw count.
2. **Part B** on top of A — implement B1–B4, re-run F9, compare incremental delta.
3. If Part B delivers <0.05 ms, drop it — the scan cost isn't worth it. Part A stands alone.

### Settings schema changes

- `FogDistanceMultiplier` clamp range widens: `(1f, 1.1f)` → `(0.3f, 1.1f)`
- New `Settings.ResolvedEffectiveFogEnd` field (float, meters)
- New `PerfOpt.DistanceShadowCulling` enum member with `level >= 0` gating
- New `perfDistanceShadowCulling` bool in `SettingsFile` with default `true`

### Files touched (expected)

- `Settings.cs` — clamp widen, new Resolved field, new PerfOpt member, clamp logic in `Recompute()`
- `SettingsFile.cs` — new bool field
- `Patches/PerformancePatch.cs` — new watchlist, `UpdateDistanceShadowCull`, extend `Apply()`
- `UpscalerManager.cs` — compute `ResolvedEffectiveFogEnd` at fog application site, call new per-frame tick
- `MenuIntegration.cs` — fog slider range update
- `README.md` / `CHANGELOG.md` — document the range change and new distance behavior

## Risks & mitigations

- **Fog < 1.0x is a gameplay change.** Players see enemies closer. Mitigation: default stays 1.0x, only power users who open the slider get the tighter fog. CHANGELOG already anticipated this direction.
- **Shadow clamp could look wrong in large outdoor areas.** If a future R.E.P.O. level has 100m+ fog, our clamp won't constrain anything — which is correct behavior. Only risk is if vanilla fog end is ever detected incorrectly. Mitigation: `_vanillaFogEnd` is captured once during environment setup and logged; sanity-check in logs.
- **Per-frame watchlist scan cost.** At ~500 watchlist entries, sqrMagnitude loop is cheap but not free. Mitigation: the scan IS the whole point, but if F9 shows this stepping on savings, switch to every-N-frames instead of every frame.
- **Prop spawns mid-level won't be in the watchlist.** Acceptable — they're usually near the player. If it turns out to matter, add a watchlist rebuild on specific spawn events (e.g., item drops) in a later iteration.
- **Fog-clamped light distance could dim mid-range lights.** `ResolvedLightDistance` controls how far the R.E.P.O. `LightManager` keeps lights active. Cutting it could make a light at 40m pop off. The `×1.2` factor gives buffer, but if visible, dial up to `×1.5`.

## Testing

- **F9 before/after** at 4K DLAA on the RTX 5090 — primary measurement
- **Fog 1.0x / 0.7x / 0.3x sweep** — verify cascade works: each step down should reduce shadow and light distance proportionally
- **Preset switches mid-level** Potato ↔ Ultra — watchlist rebuilds, clamps re-apply
- **F10 disable toggle** — shadow casting restored on all watchlist entries
- **Visual check** — fog edge shouldn't show hard shadow popping; test in Wizard and Manor levels
- **Cross-machine** — deploy to X (RX 6400), Y (P4000), Z (4070 Super) via existing drive mappings, capture benchmarks

## Future hooks

- **Volumetric fog toggle.** If a later feature swaps Unity fog for a volumetric system where light and shadow pierce through, the clamp becomes conditional on a `Settings.VolumetricFog` bool. Trivial addition.
- **Per-prop cull cadence tuning.** If the watchlist loop cost matters, easy switch from per-frame to every-N-frames with a frame counter.
- **Watchlist rebuild trigger on item spawn events.** Opt-in later if missing spawned props turns into visible shadow gaps.

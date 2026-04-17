# Fog-Driven Distance Culling — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **TEACH MODE:** REPOFidelity is a release mod. The user writes the code. Tasks describe *what* to change, *where*, and *why* with small (≤3-line) illustrative snippets. Do not paste full implementations.
>
> **No unit test suite:** verification is F9 (CostProbe) runtime measurement and the existing F11 optimizer benchmark.

**Goal:** Make fog the single source of truth for shadow and light range, and add per-prop distance-based shadow culling for the residual off-screen waste that no global clamp reaches.

**Architecture:** Part A clamps `ResolvedShadowDistance` and `ResolvedLightDistance` to small multiples of `effectiveFogEnd = _vanillaFogEnd × ResolvedFogMultiplier`. Part B captures a size-filtered watchlist on level generation and toggles `shadowCastingMode` per frame based on distance-to-camera. Part B only proceeds if Part A's F9 delta leaves room for it to matter.

**Tech Stack:** C# 10 / .NET, BepInEx 5.x, Harmony 2.x, Unity (built-in RP). Existing patterns in `Patches/PerformancePatch.cs` and `Settings.cs`.

**Reference spec:** `docs/superpowers/specs/2026-04-17-fog-driven-distance-culling-design.md`

---

## Part A — Fog as master distance knob

### Task A1: Widen fog clamp + fix the <1.0x gate

**Files:**
- Modify: `Settings.cs:108`
- Modify: `UpscalerManager.cs:527-534`

**Why:** CHANGELOG 1.3.0 claimed fog was "opened up below 1.0×" but the setter clamp still rejects anything under 1.0, and the fog-application gate in `UpscalerManager` only applies fog when `fogMult > 1f`. Both must change or the slider will look open but do nothing below 1.0.

- [ ] **Step A1.1: Widen `FogDistanceMultiplier` setter clamp**

`Settings.cs:108` currently clamps to `(1f, 1.1f)`. Change the lower bound to `0.3f`.

Quick sanity check before editing: confirm no other spot in `Settings.cs` reapplies the 1.0 floor (grep for `fogMultiplier` and `1f`). If you find one, fix it too.

- [ ] **Step A1.2: Remove the `fogMult > 1f` gate in fog application**

`UpscalerManager.cs:527-534` guards the fog write with `if (_vanillaSaved && fogMult > 1f)`. Change to `fogMult != 1f` (so `0.3 → 1.1` range all apply; exact `1.0` is a no-op skip that saves a write).

The block below the guard multiplies `_vanillaFogStart` and `_vanillaFogEnd` — no changes to those lines; the multiplication already works for values < 1.

- [ ] **Step A1.3: Commit**

```
git add Settings.cs UpscalerManager.cs
git commit -m "fog: open multiplier range to 0.3x–1.1x, apply on <1.0 as well"
```

---

### Task A2: Add `PlayableFogFloor` + `ResolvedEffectiveFogEnd`

**Files:**
- Modify: `Settings.cs` (near the other `Resolved*` fields around line 301-312)

**Why:** A2 introduces the two new state bits we need: a constant that caps automated paths (presets/auto-tune) above the "unplayable" zone, and a shared runtime value (`effectiveFogEnd`) that downstream clamps read instead of recomputing `_vanillaFogEnd × fogMult` in multiple places.

- [ ] **Step A2.1: Add the constant**

Add near the top of the internal Settings fields (look for where other internal constants like `ShadowBudget` bounds live; pick a sensible neighbor). Name: `PlayableFogFloor`, type `float`, value `0.5f`. Add a one-line comment saying it's a TBD placeholder — real value comes after tester feedback.

- [ ] **Step A2.2: Add the resolved field**

Next to `ResolvedFogMultiplier` at line 309: add `internal static float ResolvedEffectiveFogEnd;`. Initialize implicitly to 0. It will be filled in by `UpscalerManager` when vanilla fog is captured (Task A3).

- [ ] **Step A2.3: Commit**

```
git add Settings.cs
git commit -m "fog: add PlayableFogFloor constant and ResolvedEffectiveFogEnd field"
```

---

### Task A3: Wire `ResolvedEffectiveFogEnd` through fog application

**Files:**
- Modify: `UpscalerManager.cs` (fog application block around line 527-534)
- Modify: `UpscalerManager.cs` (`_vanillaSaved` capture site — search for `_vanillaFogEnd =`)

**Why:** `ResolvedEffectiveFogEnd` must equal `_vanillaFogEnd × ResolvedFogMultiplier` and must be recomputed when either input changes. The fog write block at 527-534 is the right place because it already runs on fog changes, preset changes, and level reloads.

- [ ] **Step A3.1: Assign `ResolvedEffectiveFogEnd` whenever fog is written**

Inside the `if (fogMult != 1f)` block (post Task A1.2), after the `fogEndDistance` assignment, also set:

```csharp
Settings.ResolvedEffectiveFogEnd = RenderSettings.fogEndDistance;
```

And do the same in the `else` branch / pass-through — if fogMult == 1f, set `ResolvedEffectiveFogEnd = _vanillaFogEnd`. (If there's no explicit `else`, add one.)

- [ ] **Step A3.2: Verify at startup**

Once the game loads and fog is captured (search for `_vanillaFogEnd = RenderSettings.fogEndDistance;`), `ResolvedEffectiveFogEnd` also gets initialized. If the assignment sequence would ever read `ResolvedEffectiveFogEnd` before the first fog-apply call, also write it at the capture site. (Likely not needed — the clamp consumers run after `Apply()`, but verify.)

- [ ] **Step A3.3: Commit**

```
git add UpscalerManager.cs
git commit -m "fog: populate ResolvedEffectiveFogEnd when fog is applied"
```

---

### Task A4: Clamp `ResolvedShadowDistance` and `ResolvedLightDistance` to fog end

**Files:**
- Modify: `Settings.cs` (inside `Recompute` after `ResolvedShadowDistance`/`ResolvedLightDistance` are assigned — around line 465-468)
- Possibly: `Patches/QualityPatch.cs` (where `QualitySettings.shadowDistance` is pushed to Unity) — verify the resolved value flows through after clamping

**Why:** Once preset/custom values have populated `Resolved*` fields, apply the fog ceiling. This is a *ceiling only* — preset value stays the lower bound so Potato's 10m isn't stretched.

- [ ] **Step A4.1: Add a clamp helper**

Small static method on `Settings` — name it something like `ApplyFogClamps()`. Body (illustrative, ≤5 lines):

```csharp
float end = ResolvedEffectiveFogEnd;
if (end <= 0f) return; // fog not yet captured
ResolvedShadowDistance = Mathf.Min(ResolvedShadowDistance, end * 1.1f);
ResolvedLightDistance  = Mathf.Min(ResolvedLightDistance,  end * 1.2f);
```

- [ ] **Step A4.2: Call the helper at the end of `Recompute()`**

After the block that fills in `ResolvedShadowDistance` / `ResolvedLightDistance` (look at lines 464-468 and the preset branches around 618-685), call `ApplyFogClamps()`. This fires on preset change, custom change, and auto-tune.

- [ ] **Step A4.3: Call the helper from the fog-apply site**

In `UpscalerManager` where we just wrote `ResolvedEffectiveFogEnd` (Task A3.1), also call `Settings.ApplyFogClamps()` right after. Reason: when fog itself changes, `Recompute()` doesn't run; we still need the clamp to update.

- [ ] **Step A4.4: Verify the clamped values reach Unity**

`ResolvedShadowDistance` → `QualitySettings.shadowDistance` in `QualityPatch.cs`. `ResolvedLightDistance` → R.E.P.O.'s `GraphicsManager.lightDistance` via the existing postfix at `QualityPatch.cs:53-64`. Both patches re-read the Resolved fields each call, so the clamp propagates automatically on next apply.

Read both paths and confirm. No code changes needed if already correct.

- [ ] **Step A4.5: Add a startup log line**

In `Recompute()`'s existing log (`Plugin.Log.LogInfo($"Resolved [{preset}]...")`), append the clamped shadow/light distances. Helps verification during testing.

Illustrative extension of the existing string:

```csharp
$" fogEnd={ResolvedEffectiveFogEnd:F0}m shadowsClamped={ResolvedShadowDistance}m lightsClamped={ResolvedLightDistance}m"
```

- [ ] **Step A4.6: Commit**

```
git add Settings.cs Patches/QualityPatch.cs
git commit -m "fog: clamp ResolvedShadowDistance and ResolvedLightDistance to fogEnd × 1.1/1.2"
```

---

### Task A5: Match the fog slider UI range

**Files:**
- Modify: `MenuIntegration.cs` (search for `FogDistanceMultiplier` or `fogMultiplier`)

**Why:** Settings clamp now accepts 0.3–1.1, but the slider UI may still present 1.0–1.1. Match the UI to the new range.

- [ ] **Step A5.1: Find the slider definition**

Grep for `FogDistance`, `fogMultiplier`, or `Fog Distance` in `MenuIntegration.cs` and whatever UI helper it uses. Confirm the min/max bounds.

- [ ] **Step A5.2: Update the slider range**

Min → `0.3f`, max stays `1.1f`. If the slider has step granularity (e.g. 0.05), keep the same step — the wider range just means more stops. Double-check the label/tooltip still reads correctly.

- [ ] **Step A5.3: Commit**

```
git add MenuIntegration.cs
git commit -m "fog: widen menu slider range to 0.3x–1.1x"
```

---

### Task A6: Part A measurement checkpoint

**Files:** none (manual verification)

**Why:** Measure the real impact of Part A before deciding whether Part B is worth building.

- [ ] **Step A6.1: Build**

`dotnet build` in the repo root. Confirm the postbuild deploy script (`postbuild.ps1`) copies the DLL into the local game.

- [ ] **Step A6.2: Launch R.E.P.O., load a gameplay level (Wizard or Manor)**

- [ ] **Step A6.3: F9 run at 4K DLAA Ultra**

Record:
- Average frame time
- Main camera time (CostProbe reports this)
- Main thread vs GPU time
- The "shadow casters off-screen" figure if CostProbe surfaces it

Expected: shadow-related costs drop. Exact delta unknown — hypothesis is 0.3–0.7 ms on CPU-bound systems.

- [ ] **Step A6.4: Sanity check presets**

Cycle Potato → Ultra. Each preset's log line should show `fogEnd=` and the clamped shadow/light distances. Potato's 10m shadow stays 10m (fog end > 10m). Ultra's 150m should clamp to ~44m (assuming ~40m vanilla fog).

- [ ] **Step A6.5: Sanity check fog slider**

Drag fog to 0.5x, then 0.3x. Confirm:
- Fog visibly tightens
- Shadow distance in the log drops proportionally
- Light distance in the log drops proportionally
- No "pop" at fog boundary (walk toward a wall with a shadow caster near fog edge — the shadow fades with fog rather than abruptly disappearing)

- [ ] **Step A6.6: Decide about Part B**

If Part A alone closes >80% of the "80% off-screen shadow casters" gap (inspect CostProbe's shadow cost numbers before/after), Part B's marginal value may be low. Log findings, then decide: build Part B, or stop here and move to optimization item #2 (per-range shadow resolution).

Record the measurement in `docs/superpowers/plans/2026-04-17-fog-driven-distance-culling.md` as a new "Measurements" appendix, or in a separate benchmark notes file if you prefer. Do not commit in-progress benchmark scratch — commit a summary.

---

## Part B — Per-prop distance-based shadow culling

> **Only proceed to Part B if Part A's F9 measurement shows meaningful residual shadow cost worth attacking.**

### Task B1: Add `PerfOpt.DistanceShadowCulling` enum + plumbing

**Files:**
- Modify: `Settings.cs` (PerfOpt enum around line 292, `ShouldOptimize` switch around line 253, tiering switch around line 283)
- Modify: `SettingsFile.cs` (add new bool field next to other `perf*` bools)

**Why:** Matches existing `PerfOpt` pattern. Tiered `level >= 0` so it's on for every preset including Potato.

- [ ] **Step B1.1: Add the enum member**

`Settings.cs:292-299`: add `DistanceShadowCulling` to the `PerfOpt` enum. Put it at the bottom for minimal diff.

- [ ] **Step B1.2: Add the `D.perfDistanceShadowCulling` read**

`Settings.cs:253-257`: extend the `ShouldOptimize` switch with a new case mapping `PerfOpt.DistanceShadowCulling => D.perfDistanceShadowCulling`.

- [ ] **Step B1.3: Add the tiering**

`Settings.cs:283-287`: add `PerfOpt.DistanceShadowCulling => level >= 0` (always on). Follow the same style as existing arms.

- [ ] **Step B1.4: Add the `perfDistanceShadowCulling` field to `SettingsFile`**

`SettingsFile.cs`: find the other `perf*` bools (grep for `perfParticleShadows`). Add `public bool perfDistanceShadowCulling = true;` next to them.

- [ ] **Step B1.5: Expose public accessor if needed**

`Settings.cs` has `PerfExplosionShadows`, `PerfItemLightShadows`, etc. properties. Check the pattern and add a matching `PerfDistanceShadowCulling` property that reads/writes `D.perfDistanceShadowCulling`.

- [ ] **Step B1.6: Track in `PerfSettingsWatcher`**

`Patches/PerformancePatch.cs:316-354`: the watcher re-applies SceneOptimizer when perf flags change. Add a new tracked field `_lastPerfDist` and include it in the `changed` comparison. Snapshot it in `SnapshotState()`.

- [ ] **Step B1.7: Commit**

```
git add Settings.cs SettingsFile.cs Patches/PerformancePatch.cs
git commit -m "perf: add DistanceShadowCulling flag with PerfOpt plumbing"
```

---

### Task B2: Capture the distance-cull watchlist in `SceneOptimizer.Apply()`

**Files:**
- Modify: `Patches/PerformancePatch.cs` — `SceneOptimizer` class (currently ends around line 307)

**Why:** One-shot capture on level gen. Reusing the same `Apply()` entry point that already fires on level-gen postfix and on perf-setting changes.

- [ ] **Step B2.1: Add a static list field**

In `SceneOptimizer`, near the other static fields:

```csharp
static readonly List<Renderer> _distanceCullWatchlist = new();
```

- [ ] **Step B2.2: Add a capture method**

Write a private static method `CaptureDistanceCullWatchlist()`. Behavior:

1. Clear the list.
2. If `!Settings.ShouldOptimize(Settings.PerfOpt.DistanceShadowCulling)`, return.
3. Walk `Object.FindObjectsOfType<MeshRenderer>()`. For each where `bounds.size.magnitude < 2f` AND `shadowCastingMode != ShadowCastingMode.Off`, add to the list.
4. Log the captured count (`"distance cull watchlist: {count} renderers"`).

- [ ] **Step B2.3: Call it from `Apply()`, AFTER the tiny-renderer and particle passes**

`SceneOptimizer.Apply()` already calls `SetTinyRendererShadows(false)` if flagged. Place the call to `CaptureDistanceCullWatchlist()` *after* that block so any renderer we've just disabled via tiny-renderer culling is excluded from our watchlist (since the filter checks `!= ShadowCastingMode.Off`).

- [ ] **Step B2.4: Commit**

```
git add Patches/PerformancePatch.cs
git commit -m "perf: capture distance-cull watchlist in SceneOptimizer.Apply"
```

---

### Task B3: Per-frame toggle — `UpdateDistanceShadowCull`

**Files:**
- Modify: `Patches/PerformancePatch.cs` — `SceneOptimizer` class
- Modify: `UpscalerManager.cs` (Update loop around line 365-375)

**Why:** Per-frame distance check with hysteresis dead band. Piggybacks on the existing 0.1s-cadence `UpdateShadowBudget` call site — the watchlist loop is cheap enough that 10 Hz is plenty.

- [ ] **Step B3.1: Add `UpdateDistanceShadowCull(Camera cam)` method to `SceneOptimizer`**

Behavior (illustrative pseudo — you write the C#):

```
if cam is null or watchlist empty → return
if !Settings.ModEnabled or !ShouldOptimize(DistanceShadowCulling) → RestoreDistanceCullWatchlist(); return
threshold = ResolvedShadowDistance × 0.7
thresholdSq = threshold²
hystSq = (threshold × 0.9)²
foreach renderer in watchlist:
    skip null
    distSq = (renderer.transform.position - cam.position).sqrMagnitude
    isOff = (renderer.shadowCastingMode == Off)
    if isOff && distSq < hystSq: set On
    else if !isOff && distSq > thresholdSq: set Off
```

Use squared distance — avoid per-frame `Sqrt`. Prune dead refs as you go (destroyed renderers become null).

- [ ] **Step B3.2: Add `RestoreDistanceCullWatchlist()` helper**

Small method that iterates the watchlist and sets `shadowCastingMode = ShadowCastingMode.On` on every non-null entry. Does *not* clear the list — `Apply()` does that.

- [ ] **Step B3.3: Hook into the Update loop**

`UpscalerManager.cs:368-376`: the Update method already has a 0.1s cadence timer that calls `UpdateShadowBudget(_camera)`. Add a second call right after:

```csharp
Patches.SceneOptimizer.UpdateDistanceShadowCull(_camera);
```

Same cadence is fine — 10 Hz is enough for the boundary walk; the dead band absorbs single-tick lag.

- [ ] **Step B3.4: Commit**

```
git add Patches/PerformancePatch.cs UpscalerManager.cs
git commit -m "perf: per-frame distance-based shadow cull toggle with hysteresis"
```

---

### Task B4: Part B measurement checkpoint

**Files:** none (manual verification)

- [ ] **Step B4.1: Build and launch**

- [ ] **Step B4.2: F9 at 4K DLAA Ultra, same spot as Task A6.3**

Compare to the Task A6.3 numbers. Record the incremental delta (Part B - Part A).

- [ ] **Step B4.3: Visual regression check**

Walk around Wizard and Manor. Specifically look for:
- Shadow of a small prop disappearing as you walk away — should be invisible at the threshold distance
- Shadow reappearing as you walk back — should fade in smoothly inside the dead band, no snap
- Off-screen casters: rotate the camera 180° from a group of props and confirm the shadow map cost drops (CostProbe should reflect this)

- [ ] **Step B4.4: Edge cases**

- Toggle `ModEnabled` off with F10 → all watchlist renderers restore to `On`
- Toggle Perf Distance Cull off via Custom preset (after B1.5's accessor) → same result
- Reload level → watchlist rebuilds cleanly (check log count)

- [ ] **Step B4.5: Decide keep / drop**

Criteria:
- If incremental delta ≥ 0.05 ms and no visual regressions → keep, proceed to Task B5
- If delta < 0.05 ms → drop Part B; `git revert` B1–B3 commits and stop. Part A stands alone.
- If visual regressions are present → investigate threshold or dead band tuning first before deciding.

---

### Task B5: Cross-machine validation (conditional on B4.5 keep)

**Files:** none (test machine deployment)

- [ ] **Step B5.1: Deploy to X, Y, Z test machines**

Copy the built DLL per existing network drive mappings (see memory: X=RX 6400, Y=P4000, Z=4070 Super).

- [ ] **Step B5.2: F9 and F11 on each**

Record baseline vs. new build for each machine. Particular interest: does Part B help the RX 6400 and P4000 materially?

---

## Finishing

### Task F1: CHANGELOG entry

**Files:**
- Modify: `CHANGELOG.md`

**Why:** Document the user-visible change. Two behaviors shift: fog slider range opens down to 0.3x, and shadow/light distance now derive from fog rather than being fixed per preset.

- [ ] **Step F1.1: Add a new unreleased section at the top**

Follow the existing changelog voice ("imperative, user-facing, concise"). One bullet per user-visible change. Example bullets (revise to taste):

- Fog slider now opens down to 0.3× (was 1.0× floor). Pulling fog closer also tightens shadow and light range proportionally — a real CPU-bound performance knob.
- Shadow and light distances now cap at the fog end plus a small overshoot for smooth transitions. Ultra's 150m shadow range no longer wastes draw calls on casters hidden behind fog.
- (Only if Part B shipped:) Small props (<2m bounds) disable shadow casting when beyond ~70% of the effective shadow distance, with hysteresis to prevent flicker.

No version bump in the commit (memory: version bumps only per release).

- [ ] **Step F1.2: Commit**

```
git add CHANGELOG.md
git commit -m "changelog: fog-driven shadow/light distance + optional per-prop cull"
```

---

## Rollback guardrails

- Each Part A task is a separate commit, so if A2/A3/A4 reveal a regression, `git revert` is surgical.
- Part A and Part B are independent commits series — Part B can be reverted without disturbing Part A.
- The `PlayableFogFloor` value is a constant you can tune later without re-plumbing.

## Out-of-scope (confirmed with user, for future iterations)

- Volumetric fog (user suggested as atmosphere idea)
- Auto-tune integration for the fog knob — presets/auto-tune staying above `PlayableFogFloor` is in scope; *using* fog as an auto-tune stepdown lever is not.
- Watchlist rebuild on mid-level item spawn events
- Version bump (release-time concern, not per-feature)

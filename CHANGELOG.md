## 1.4.0

- Shadow and light distance now clamp to fog end instead of being independent per-preset values. Ultra's 150m shadows behind a 40m fog wall was pure waste — the geometry's invisible anyway. Shadow caps at fog × 1.1, light at fog × 1.2, the overshoot keeps casters right at the fog line from popping as you walk past
- Fog slider lower bound opened to 0.3× — 1.3.0's changelog claimed this already happened but the setter clamp was still blocking it, and the fog apply path had a `> 1f` gate that silently ignored anything under vanilla. Presets and auto-tune stay above 0.5× ("playable floor") so dragging fog into your face stays a deliberate choice
- Potato preset defaults to fog 0.85× — small atmospheric reduction, small extra savings on top of the distance cascade
- Small renderers (bounds < 2m) stop casting shadows past 70% of effective shadow distance, re-enable when closer, 10% hysteresis band kills flicker at the boundary. Cuts off-screen shadow-map work that the game pays for on distant props
- Per-light shadow map resolution is bucketed by range across every preset: <5m → 256, 5-10m → 512, 10-20m → 1024, >20m → 2048. Flashlight keeps 4096 on Ultra only. Potato caps at 1024. No more 4K shadow maps on 3m Button Lights
- ParticleSystem.cullingMode set to Automatic on every system so off-screen and non-emitting systems skip their per-frame update. A typical level registers 230+ systems with 1 actively emitting — the other 229 were ticking for nothing
- F9 cost probe sweeps a preset × fog matrix — all five presets at fog 1.1× and 0.3×, plus upscaler and vanilla steps. Fully automated, settings restored on exit. Report header gained a "Range:" line with fog multiplier, effective fog end, and light distance so runs are self-documenting when comparing builds
- Fixed F9 GPU frame time occasionally returning garbage 700-million-ms values after rapid state swaps — bottleneck label falls back to CPU when the marker's unreliable instead of incorrectly reading "GPU"
- Fixed F9 vanilla sample inheriting the prior cell's shadow distance instead of restoring Unity's vanilla QualitySettings before measuring

## 1.3.0

- DLSS evaluation now runs on a private D3D12 device with D3D11↔D3D12 shared-texture interop, working around the D3D11 DLSS path being blocked by the driver on RTX 50-series (Blackwell). Existing RTX 20/30/40 cards use the same code path
- DLSS gets projection-matrix jitter the same way FSR Temporal does — sub-pixel accumulation now functions, which is the whole point of a temporal upscaler
- DLSS now requests Preset E (CNN) instead of the transformer default — more forgiving of Unity's built-in-RP motion vectors which aren't perfectly jitter-clean
- Fixed DLSS output flashing white the first frame after enable (output RT was undefined until DLSS wrote the first frame over it)
- Fixed DLSS staying black when a scale change caused Unity to recycle an RT's native pointer into a new texture — shared-handle cache now cleared on re-init
- Sharpening slider no longer rebuilds the whole upscaler pipeline every tick, since it's a per-frame shader uniform
- Shadow budget system — caps how many small point lights cast shadows at once, closest to camera get priority. Fades shadow strength in/out for smooth transitions instead of pop-in. Configurable per preset (Potato=5, Ultra=25) or manually via Shadow Limit slider (0=unlimited)
- Tiered shadow map resolution — directional lights use global resolution with cascades instead of a forced custom value, small decorative lights get 512 instead of 4096, infrastructure lights cap at 2048. Massive shadow cost reduction with no visible quality loss
- Shadow cascades — Low/1, Medium/2, High+Ultra/4. Fixes directional light shadow quality (window lighting, outdoor shadows) which was previously stuck at 1 cascade
- Disabled shadows on zero-intensity lights — mines and other inactive light sources were generating shadow maps for nothing
- FSR Temporal now jitters the projection matrix for proper sub-pixel accumulation — sharper edges and better temporal stability
- Fixed FXAA darkening the image — keepAlpha wasn't set, so luminance was bleeding into the alpha channel during compositing
- Max FPS is now a smooth slider (30–360 + Unlimited) instead of preset options — set exact values for adaptive sync
- Performance toggle labels changed from Off/On to Keep/Disable for clarity
- Auto-tune now weights 1% and 0.1% lows into its decision (50% avg + 30% 1%-low + 20% 0.1%-low). Stuttery systems actually step down now instead of sliding by on a good average
- Auto-tune targets monitor refresh rate directly — the old `refresh × 1.05` padding was fighting the 1%-low safety the benchmark was already applying, which is how a 5090 on 240Hz ended up with LOD 2 and 8x AF
- Fixed CPU-bound stepdown using GPU-cost predictions to size CPU savings. New ladder only touches the knobs that actually reduce draw-call count (shadow distance, light count, light distance) and leaves shadow quality / LOD / AF at Ultra where they belong. Strong-CPU systems now also get a bonus tier that raises sharpness, LOD, and shadow distance above vanilla Ultra when benchmark headroom allows
- Scene-complexity factor — benchmark accounts for sparse scenes (menu, small rooms) vs. packed ones so real gameplay doesn't blow the budget
- Intel Arc iGPU (Meteor Lake / Lunar Lake "Intel Arc Graphics") is now detected as integrated. Discrete Arc A380/A750/A770/B580 still come through as regular dGPUs and get FSR
- Live status line at the top of the graphics menu — CPU-bound/GPU-bound tag, frame time, fps, render resolution, upscaler. Updates 4×/sec while the menu's open
- Auto-Tune button label now tells you what clicking will actually do (`AUTO-TUNE BENCHMARK (15s)` in-game, `AUTO-TUNE — WILL QUEUE (START A GAME)` in the menu, `AUTO-TUNE QUEUED (WILL RUN ON NEXT LEVEL)` after queuing). Tap while queued to cancel
- Shadow Limit and Draw Distance sliders moved the "0 = unlimited" / "0 = auto" hint into the label instead of burying it in the description
- Fog Distance slider opened up below 1.0× so it's actually a performance knob — upper bound stays at 1.1× because anything farther would give a gameplay advantage
- Pixelation moved from the Upscaling group to Post Processing, which is what it actually is
- Potato preset now always runs at 50% render scale (matches vanilla Potato) instead of flipping to 100% on CPU-bound systems

## 1.2.0

- Added CPU optimization patches — EnemyDirector loop throttling, RoomVolumeCheck NonAlloc, SemiFunc result caching, PhysGrabObject list iteration bugfix, LightManager allocation-free cleanup
- CPU patches auto-enable based on frame time — active when needed (>8ms), dormant on fast systems where Harmony overhead would cost more than the savings
- Added F11 optimizer benchmark — measures vanilla vs GPU/GC vs all optimizations with 2-pass averaging, writes `optimizer_benchmark.txt`
- Fixed auto-tune misclassifying high-FPS systems as CPU-bound — threshold now 95% above 120fps, 85% below
- Fixed auto-tune on Proton/DXVK — detects when CPU ceiling is below target refresh regardless of ratio
- Fixed CPU-bound auto-tune maxing GPU settings on weak GPUs — stepdown cascade now applies to both CPU and GPU settings when budget is tight
- Fixed divide-by-zero in CPU patch gate during scene transitions
- CPU-impacting settings step down before pure-GPU settings (fog, AF) in the auto-tune ladder
- Auto-tune unlocks FPS cap during measurement and restores it after
- Fixed DLSS motion vector warning on F10 toggle — upscaler disposed before camera depth mode restore
- F10 now restores vanilla pixelated resolution correctly
- F10/F11/auto-tune transitions use the game's glitch effect to mask settings switching
- Overlay rewritten — native HUD text with scanlines when in-game, OnGUI fallback in menus. Bottom-left, slide-up animation, smoothed FPS counter
- Added CPU and system info to startup log and benchmark results

## 1.1.2

- Rewrote CPU/GPU bottleneck detection — old method didn't work on D3D11, so every system was incorrectly tagged as GPU-bound. Now runs a two-phase benchmark to measure the actual bottleneck
- Fixed CPU-bound auto-tune trashing shadow quality for no gain — shadow resolution is GPU-only, so the CPU-bound ladder no longer touches it. Only reduces shadow distance and light count, and never below vanilla defaults
- Benchmark now shows a progress bar with percentage instead of raw frame counts
- Fixed DLSS not loading on some NVIDIA systems (driver store folder naming mismatch)
- Fixed DLSS showing as available on non-RTX GPUs (Quadro P4000, GTX series)
- Fixed items dropping when pulled from inventory with auto-hold enabled

## 1.1.1

- Fixed Auto preset stripping flashlight/explosion/particle shadows on high-end hardware — shadow optimizations now only kick in when the benchmark shows the PC actually needs them
- Fixed DLSS not loading on some NVIDIA systems (driver store folder naming mismatch)
- Fixed DLSS showing as available on non-RTX GPUs (Quadro P4000, GTX series)
- Fixed items dropping when pulled from inventory with auto-hold enabled

## 1.1.0

- Added resolution selector — shows resolutions matching your monitor's aspect ratio, from 720p up to native. Render scale works relative to this.
- Added "Auto" preset — runs a benchmark and stores results in `autotune.json`, separate from your settings. Other presets are never touched. Re-benchmarks on mod updates or hardware changes.
- Reworked presets — Potato and Low run at 50% through the game's native scaling (no render pipeline overhead, should beat vanilla FPS). Medium at 75% with SMAA. Upscalers only at High+.
- Fixed iGPU getting broken auto-tune results (upscaler Off + 50% was running through the full custom pipeline for no reason)
- Cut the upscaler path from 3 render textures down to 1. Non-upscaler presets use zero.
- Depth texture only generated when DLSS/FSR needs it — frees up bandwidth on iGPUs
- Fixed DLSS render scale slider locking to 100% after a preset switch
- Textures locked to Full — lowering them doesn't help in R.E.P.O., the textures are tiny
- FSR minimum raised to 50% (below that it falls apart)
- Debug overlay scales with resolution instead of using fixed pixel sizes
- Auto-tune button now sets preset to Auto automatically
- FPS counter skipped when debug overlay is off

## 1.0.0

- DLSS 4 Super Resolution + DLAA support (NVIDIA RTX, DLL bundled)
- FSR Temporal upscaling for any GPU (AMD, Intel, NVIDIA)
- CAS sharpening pass
- Anti-aliasing options — SMAA and FXAA (removed TAA to avoid temporal conflicts)
- Quality presets — Potato, Low, Medium, High, Ultra, Custom
- Auto-benchmark on first launch, targets monitor refresh rate
- CPU vs GPU bottleneck detection — adjusts the right settings for each
- Full graphics settings menu (replaces vanilla Graphics page)
- Shadow quality, shadow distance, LOD bias, texture filtering, texture quality
- Light distance, fog distance, draw distance controls
- Post-processing toggles (motion blur, chromatic aberration, lens distortion, grain)
- Per-layer fog culling for GPU savings
- CPU performance optimizations — NonAlloc physics, cached components, GC reduction
- GPU auto-detection (NVIDIA/AMD/Intel, VRAM, performance tier)
- F10 toggle for vanilla comparison (fully reverts all mod changes)
- Auto-tune can be triggered from the pause menu
- Vanilla display settings (window mode, vsync, fps, gamma) preserved in menu
- Fixed extraction point flicker
- Fixed black screen when switching presets mid-level
- Fixed flashlight shadows not disabling on Potato preset
- Fixed AA not applying correctly on preset switch
- Fixed auto-benchmark running during loading screens and main menu

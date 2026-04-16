## 1.3.0

- Shadow budget system — caps how many small point lights cast shadows at once, closest to camera get priority. Fades shadow strength in/out for smooth transitions instead of pop-in. Configurable per preset (Potato=5, Ultra=25) or manually via Shadow Limit slider (0=unlimited)
- Tiered shadow map resolution — directional lights use global resolution with cascades instead of a forced custom value, small decorative lights get 512 instead of 4096, infrastructure lights cap at 2048. Massive shadow cost reduction with no visible quality loss
- Shadow cascades — Low/1, Medium/2, High+Ultra/4. Fixes directional light shadow quality (window lighting, outdoor shadows) which was previously stuck at 1 cascade
- Disabled shadows on zero-intensity lights — mines and other inactive light sources were generating shadow maps for nothing
- FSR Temporal now jitters the projection matrix for proper sub-pixel accumulation — sharper edges and better temporal stability
- Fixed FXAA darkening the image — keepAlpha wasn't set, so luminance was bleeding into the alpha channel during compositing
- Max FPS is now a smooth slider (0–360, 0 = unlimited) instead of preset options — lets you dial in exact values for adaptive sync
- Performance toggle labels changed from Off/On to Keep/Disable for clarity

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

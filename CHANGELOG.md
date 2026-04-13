## 1.2.0

- Added CPU optimization patches — EnemyDirector throttling, RoomVolumeCheck NonAlloc, SemiFunc nearest-object caching, PhysGrabObject list iteration fix, LightManager allocation-free cleanup
- CPU patches auto-enable based on frame time — kicks in when needed (>8ms), stays off on fast systems where the overhead would hurt
- Added F12 optimizer benchmark — measures all optimizations (GPU/GC + CPU) with 2-pass averaging, writes a detailed report to `optimizer_benchmark.txt`
- Fixed auto-tune misclassifying high-FPS systems as CPU-bound — ratio threshold now scales with framerate (95% above 120 FPS, 85% below)
- Fixed auto-tune misclassifying Proton/DXVK systems — secondary check detects when the CPU ceiling is below target refresh rate
- Fixed CPU-bound auto-tune maxing GPU settings on systems with weak GPUs — the stepdown cascade now reduces GPU settings too when the budget is tight
- CPU-impacting settings (lights, shadow distance, LOD) now step down before pure-GPU settings (fog, AF) in the auto-tune ladder
- Auto-tune benchmark now unlocks FPS cap during measurement (`Application.targetFrameRate = -1`) alongside VSync disable, then restores both after
- Fixed DLSS motion vector warning on F10 toggle — upscaler command buffers are now disposed before camera depth mode is restored
- Rewrote overlay as a proper layout manager — all status text (debug, benchmark, mod-off) stacks cleanly in the bottom-left without overlapping
- FPS counter in overlay smoothed over 0.5s for readability
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

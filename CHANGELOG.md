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

- **New:** DLSS 4 Super Resolution + DLAA support (NVIDIA RTX, DLL bundled)
- **New:** FSR Temporal upscaling for any GPU (AMD, Intel, NVIDIA)
- **New:** CAS sharpening pass
- **New:** Anti-aliasing options — SMAA and FXAA (removed TAA to avoid temporal conflicts)
- **New:** Quality presets — Potato, Low, Medium, High, Ultra, Custom
- **New:** Auto-benchmark on first launch, targets monitor refresh rate
- **New:** CPU vs GPU bottleneck detection — adjusts the right settings for each
- **New:** Full graphics settings menu (replaces vanilla Graphics page)
- **New:** Shadow quality, shadow distance, LOD bias, texture filtering, texture quality
- **New:** Light distance, fog distance, draw distance controls
- **New:** Post-processing toggles (motion blur, chromatic aberration, lens distortion, grain)
- **New:** Per-layer fog culling for GPU savings
- **New:** CPU performance optimizations — NonAlloc physics, cached components, GC reduction
- **New:** GPU auto-detection (NVIDIA/AMD/Intel, VRAM, performance tier)
- **New:** F10 toggle for vanilla comparison (fully reverts all mod changes)
- **New:** Auto-tune can be triggered from the pause menu
- **New:** Vanilla display settings (window mode, vsync, fps, gamma) preserved in menu
- **Fixed:** Extraction point flicker
- **Fixed:** Black screen when switching presets mid-level
- **Fixed:** Flashlight shadows not disabling on Potato preset
- **Fixed:** AA not applying correctly on preset switch
- **Fixed:** Auto-benchmark running during loading screens and main menu

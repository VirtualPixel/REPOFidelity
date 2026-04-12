## 1.1.0

- **Reworked presets** — Potato and Low now run at 50% render scale through the game's native RT system (zero pipeline overhead). Potato should be faster than vanilla. Medium bumped to 75% with SMAA. Upscalers (DLSS/FSR) only kick in at High and above.
- **Fixed iGPU performance** — auto-tune no longer gives broken settings on integrated GPUs. Previously produced upscaler=Off at 50% which ran through the full custom RT pipeline for a blurry bilinear blit. Now routes through the game's own scaling.
- **Reduced render pipeline overhead** — eliminated two of three custom render textures for the upscaler path. Non-upscaler presets use zero custom RTs.
- **Depth texture is conditional** — only generated when a temporal upscaler (DLSS/FSR) actually needs it. Saves GPU bandwidth on iGPUs.
- **FPS counter only runs when visible** — debug overlay off = no per-frame string building.
- **Fixed DLSS render scale slider** getting stuck at 100% after preset switch. DLSS at 100% auto-promotes to DLAA, but the underlying setting now stays as DLSS so the slider still works.
- **Textures always Full** — R.E.P.O.'s textures are small enough that Half/Quarter gave no measurable FPS gain. Removed the quality hit.
- **FSR minimum render scale raised to 50%** — below that the temporal accumulation produces unacceptable quality.

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

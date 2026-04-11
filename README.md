# REPO Fidelity — HD Graphics & Performance Mod for R.E.P.O.

Better than REPO HD. Better than vanilla. Graphics overhaul that goes both ways — **squeeze more FPS on potato PCs** or **push visuals on high-end rigs**. Auto-detects your hardware and picks the best settings. Full in-game settings menu. DLSS, FSR, SMAA, shadow control, the works.

**Replaces REPO HD.** Everything it does, plus DLSS, auto-benchmark, CPU/GPU-aware tuning, performance optimizations, and way more control. If you have REPO HD installed, remove it.

## Vanilla vs REPO Fidelity

### Environment

<table>
<tr><td align="center"><b>Vanilla</b></td><td align="center"><b>REPO Fidelity</b></td></tr>
<tr>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/truck_bay_vanilla.png" width="400"></td>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/truck_bay_fidelity.png" width="400"></td>
</tr>
</table>

Cleaner edges, better shadow quality, no more pixelated mess. Notice the grating detail and wall panels.

### Up Close — Edges & Text

<table>
<tr><td align="center"><b>Vanilla</b></td><td align="center"><b>REPO Fidelity</b></td></tr>
<tr>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/whiteboard_vanilla.png" width="400"></td>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/whiteboard_fidelity.png" width="400"></td>
</tr>
</table>

<table>
<tr><td align="center"><b>Vanilla</b></td><td align="center"><b>REPO Fidelity</b></td></tr>
<tr>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/dumpster_vanilla.png" width="400"></td>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/dumpster_fidelity.png" width="400"></td>
</tr>
</table>

Hazard stripes, text, and fine geometry all render without the jagged staircase edges.

### Lighting & Textures

<table>
<tr><td align="center"><b>Vanilla</b></td><td align="center"><b>REPO Fidelity</b></td></tr>
<tr>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/candle_wall_vanilla.png" width="400"></td>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/candle_wall_fidelity.png" width="400"></td>
</tr>
</table>

<table>
<tr><td align="center"><b>Vanilla</b></td><td align="center"><b>REPO Fidelity</b></td></tr>
<tr>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/candle_table_vanilla.png" width="400"></td>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/candle_table_fidelity.png" width="400"></td>
</tr>
</table>

Brickwork, candlelight, and shadow edges all sharpen up. Look at the wall texture and the base of the candle.

### Object Detail

<table>
<tr><td align="center"><b>Vanilla</b></td><td align="center"><b>REPO Fidelity</b></td></tr>
<tr>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/vase_vanilla.png" width="400"></td>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/vase_fidelity.png" width="400"></td>
</tr>
</table>

<table>
<tr><td align="center"><b>Vanilla</b></td><td align="center"><b>REPO Fidelity</b></td></tr>
<tr>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/clown_vanilla.png" width="400"></td>
<td><img src="https://raw.githubusercontent.com/VirtualPixel/REPOFidelity/main/media/clown_fidelity.png" width="400"></td>
</tr>
</table>

Surface detail on the vase and sharper edges on the clown figure. Shadows render properly instead of blocky artifacts.

## Features

**Upscaling & Anti-Aliasing**
- **DLSS** — NVIDIA AI upscaling. At 100% render scale, runs as DLAA (native-res AA)
- **FSR** — AMD temporal upscaling, works on any GPU including Linux/Proton
- **SMAA** — sharp edge-based AA, no ghosting, works everywhere
- **CAS sharpening** — adjustable post-upscale sharpening

**Quality Settings**
- Shadow quality (Low through Ultra) and shadow distance (5–200m)
- Light render distance (vanilla caps around 30m)
- LOD bias, anisotropic filtering, texture quality
- Pixel light count (1–16 per object)
- Fog distance, draw distance
- Post-processing toggles (motion blur, chromatic aberration, lens distortion, film grain, bloom)

**Performance Optimizations**
- Reduces shadow caster count (up to 85% fewer shadow passes)
- GPU instancing on all materials (fewer draw calls)
- Cached physics queries (less garbage collection pressure)
- Disables unnecessary shadows on explosions, particle effects, animated lights
- All optimizations scale with preset — Ultra keeps full visual quality

**Smart Auto-Benchmark**
- Runs on first launch, picks settings for your refresh rate
- Detects CPU vs GPU bottleneck — won't waste visual quality on settings that can't help
- CPU-bound machines keep native resolution instead of pointlessly upscaling
- NVIDIA gets DLSS/DLAA automatically, everyone else gets SMAA
- Re-run anytime from the settings menu

## Presets

All presets adapt to your hardware. CPU-bound machines keep 100% render scale and sharp AA since dropping resolution wouldn't help FPS anyway.

| Preset | What it does |
|--------|-------------|
| **Potato** | Strips shadows, lighting, effects. Maximum FPS for struggling hardware |
| **Low** | Minimal quality with SMAA. Playable on old hardware |
| **Medium** | Vanilla-equivalent quality with SMAA and medium shadows |
| **High** | Above vanilla. DLAA on NVIDIA, extended shadows and lighting |
| **Ultra** | Maximum quality. DLAA, Ultra shadows at 150m, full lighting range |
| **Custom** | Tweak everything individually. Per-setting performance toggles |

## Settings

Replaces the game's Graphics page. All vanilla display settings (window mode, VSync, max FPS, gamma) plus every mod setting. Preset selector or go Custom.

| Setting | Range | Default | Description |
|---|---|---|---|
| Preset | Potato–Custom | Auto | Quality level, auto-detected on first launch |
| Upscaler | DLSS / FSR / Off | Auto | DLSS on NVIDIA, FSR on AMD/Intel, Off if CPU-bound |
| Render Scale | 50–100% | 100% | Internal resolution before upscaling |
| Anti-Aliasing | SMAA / FXAA / Off | SMAA | Post-process AA (disabled when upscaler provides AA) |
| Shadow Quality | Off / Low / Med / High / Ultra | Varies | Shadow map resolution |
| Shadow Distance | 5–200m | Varies | Max shadow render distance |
| Pixel Lights | 1–16 | Varies | Per-object dynamic lights |
| LOD Bias | 0.5–4.0 | Varies | Level of detail distance |
| Texture Quality | Full / Half / Quarter | Full | Mipmap bias |
| Anisotropic Filtering | Off / 4x / 8x / 16x | 8x | Texture sharpness at angles |
| Light Distance | 10–100m | 50m | Max light render range |
| Fog Distance | 1.0–1.1x | 1.0x | Fog end distance multiplier |
| Mod Toggle Key | F5–F10 | F10 | Disables mod entirely for vanilla comparison |

**F10** (configurable) toggles the entire mod off for vanilla comparison — everything reverts including performance optimizations.

## Installation

1. Install [BepInEx 5](https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/)
2. Install [MenuLib](https://thunderstore.io/c/repo/p/nickklmao/MenuLib/) for the settings UI
3. Drop this mod in `BepInEx/plugins/`
4. Launch — benchmark runs automatically on first level

DLSS DLL is bundled. No extra downloads.

## Coming from REPO HD?

Remove it. REPO Fidelity does everything REPO HD does:
- Removes pixelation / upscales to native
- Anti-aliasing (SMAA, plus DLSS/DLAA for NVIDIA)
- Extraction point flicker fix
- Plus: DLSS upscaling, auto-benchmark, CPU-aware tuning, shadow optimization, full settings menu, performance presets, 15+ configurable quality settings

## Compatibility

- Works alongside most mods — only conflicts with other render pipeline mods
- MenuLib required for settings UI (soft dependency — mod works without it)
- Singleplayer and multiplayer
- Windows and Linux (Proton)

## Known Issues

- Switching presets rapidly can briefly flash a black frame
- Tiny object shadow removal can't be restored mid-level (requires level reload)

Report bugs on [GitHub](https://github.com/VirtualPixel/REPOFidelity/issues).

---

## Contact

| Purpose | Where |
|---|---|
| Bug reports & suggestions | [GitHub Issues](https://github.com/VirtualPixel/REPOFidelity/issues) |
| R.E.P.O. Modding community | [Discord](https://discord.gg/9fDzZ9sk95) |

<a href="https://ko-fi.com/vippydev" target="_blank">
<img src="https://storage.ko-fi.com/cdn/brandasset/v2/support_me_on_kofi_dark.png" alt="Ko-Fi" width="200">
</a>

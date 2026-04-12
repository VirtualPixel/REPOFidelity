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
- Runs on first launch, stores results in `autotune.json` — separate from your settings
- Re-runs automatically when the mod updates or your hardware/resolution changes
- Detects CPU vs GPU bottleneck — won't waste visual quality on settings that can't help
- "Auto" preset uses the benchmarked profile. Other presets are never touched by auto-tune.
- Re-run manually anytime from the settings menu

## Presets

All presets adapt to your hardware. CPU-bound machines keep 100% render scale since dropping resolution wouldn't help FPS anyway. Potato through Medium use the game's native render system with zero pipeline overhead — upscalers only activate at High and above.

| Preset | Render Scale | AA | Shadows | Target |
|--------|-------------|-----|---------|--------|
| **Auto** | Benchmarked | Benchmarked | Benchmarked | Auto-tuned for your hardware. Re-runs on mod update or hardware change |
| **Potato** | 50% | Off | Low / 10m | Faster than vanilla. Cuts everything for max FPS |
| **Low** | 50% | SMAA | Low / 20m | Near-vanilla FPS with cleaner image |
| **Medium** | 75% | SMAA | Med / 30m | Big visual upgrade, no upscaler overhead |
| **High** | 75–100% | DLSS/FSR | High / 85m | Premium. Upscaler handles AA |
| **Ultra** | 100% | DLAA/FSR | Ultra / 150m | Maxed everything |
| **Custom** | Any | Any | Any | Tweak individually. Per-setting perf toggles |

## Settings

Replaces the game's Graphics page. All vanilla display settings (window mode, VSync, max FPS, gamma) plus every mod setting. Preset selector or go Custom.

| Setting | Range | Default | Description |
|---|---|---|---|
| Preset | Auto–Custom | Auto | Quality level. Auto uses benchmarked profile |
| Upscaler | DLSS / FSR / Off | Auto | DLSS on NVIDIA, FSR on AMD/Intel, Off if CPU-bound |
| Resolution | Monitor-specific | Native | Output resolution. Filtered to your aspect ratio |
| Render Scale | 33–100% | 100% | Internal resolution before upscaling to selected resolution |
| Anti-Aliasing | SMAA / FXAA / Off | SMAA | Post-process AA (disabled when upscaler provides AA) |
| Shadow Quality | Off / Low / Med / High / Ultra | Varies | Shadow map resolution |
| Shadow Distance | 5–200m | Varies | Max shadow render distance |
| Pixel Lights | 1–16 | Varies | Per-object dynamic lights |
| LOD Bias | 0.5–4.0 | Varies | Level of detail distance |
| Texture Quality | Full | Full | Always full (R.E.P.O. textures are too small for lower mips to help) |
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

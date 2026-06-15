## 1.7.6

- Ultrawide: the upgrades list goes away properly again. Holding the map shows your upgrades on the left; on a wide panel they used to get stuck there when you dropped the map instead of sliding off. 1.7.4 dropped a cull that was clipping hiding HUD at the vanilla edge (it was eating real shown UI), and that left the upgrades list, which parks deep inside the canvas and whose text overflows its box, lingering in the widened band. The cull is back but now keyed on the owning element actually heading for its hide anchor, so it never touches shown UI (inventory slots, settings arrows), and the list's hide is extended so it wooshes fully off the side on its own. Reported by AncientPixel-Aron.
- Narrow panels: 4:3 and 5:4 now letterbox like vanilla instead of fighting it. The HUD is a fixed 16:9 layout, and filling a much-taller panel either floated it in a centered band (inventory rode high, off-canvas strip leaked) or cropped the edge buttons. There's no clean way to do both, and vanilla's letterbox just looks right, so panels at 4:3 and narrower fall back to it. 16:10 and 3:2 still fill the screen (they barely inset). Endershade's 5:4 is the case this fixes.
- Ultrawide: the valuable discover box and value text now track. The bracket box and the floating value snap onto a valuable by projecting it through the camera onto the 16:9 canvas, but the widened capture shows more than that canvas, so they sat ~3/4 of the way out and lagged the item toward center, and the box read skinny. Both now scale by the capture so they lock onto the valuable across the full panel at the right size.
- Ultrawide: the HUD re-frames on a resolution or monitor change. The shift/cull passes are aspect-specific but only reset when the wide presentation turned fully off, so switching straight from one ultrawide resolution to another left stale offsets. They now re-evaluate whenever the panel aspect changes.
- BetterView compatibility note. BetterView is also a render-texture mod: its `RenderTexture` options (`BlockRenderTextureSizeChange`, `RenderResolutionScale`, `BlockTemporaryResolutionDrop`) drive the same texture REPOFidelity's upscaler resizes to your output resolution every frame, so the two fight and the view garbles, the TAB map worst of all (reported by Mortycio). Turn those three off in BetterView (`false`, `1.0`, `false`) and keep it for its lighting and color effects; let REPOFidelity own the resolution. Documented in the readme's compatibility section.

Loom's arms are fixed now (that was ScalerCore). I'm still afraid of them, and on 32:9 there's just more screen for them to reach across.

---

## 1.7.5

- Ultrawide: stowing the map clears the inventory off the wide panel again. 1.7.4 dropped the geometric reveal-cull and left only the parked-element culling, but that only ever checked the nearest SemiUI above a revealed graphic. The inventory parks as a whole (the parent slides off-canvas) while every slot keeps its own SemiUI, so a slot still holding an item read as "shown" at the slot level even though the parked parent had carried it past the canvas edge, and its icon and battery bars sat in the revealed band on a 21:9/32:9 panel instead of hiding with everything else. The cull now walks the whole SemiUI chain above a revealed graphic and judges parked by the element's own animated hide position instead of its transform, so a slot carried off by a parked parent is caught the same as one parked on its own, and un-culls the instant anything in that chain animates open. Reported by AncientPixel-Aron on 21:9.

---

## 1.7.4

- Ultrawide: the main-menu scene renders right on a wide panel. The title truck used to render fat and stretched while the in-game world looked correct. The upscaler resizes the shared render texture to the panel resolution and the game re-enables its listed cameras to pick that up, but the menu camera is not in that list, so its aspect stayed at the boot-time 16:9 and a 16:9 view got stretched across the panel. The menu camera now tracks the panel aspect like the world cameras, and widens HOR+ (vanilla vertical framing, held at 120 degrees horizontal so 32:9 doesn't fisheye) so the truck sits back at its real size instead of stretched or zoomed. Reported by Mortycio on 32:9 and AncientPixel-Aron on 21:9. (Wide panels for now; the tall sub-16:9 case is still a follow-up.)
- HDR displays stop blowing out to white. The DLSS output buffer was a fixed 8-bit format that clamps everything to the 0-1 range, so on an HDR panel, where the frame carries highlights brighter than 1, the upscaled image clipped to white and read as overexposed. The buffer now matches the game's render-texture format, preserving the full range. SDR is unaffected; if the format is ever rejected the frame falls back to a straight copy rather than failing.
- Ultrawide: the reveal-cull stops eating real UI. The pass that hides HUD graphics revealed past the vanilla 16:9 canvas had a geometric branch that culled anything sitting fully outside that canvas. On a widened panel that band is exactly where legitimately-shown UI lives, so it kept hiding real things: an inventory slot on the right, and the settings arrows, which culled as they scrolled past the edge and popped back in the moment they moved. That blanket geometric cull is gone. What it was built for, the HUD elements the game parks just off-canvas (the Arena Race timer on the title screen and the like), is still handled by the parked-element culling and the cover/edge-art passes, so nothing the player was never meant to see leaks back in.
- Settings are now reachable through REPOConfig when MenuLib is not installed. The in-game graphics menu is built on MenuLib, which is incompatible with RepoXR (VR), so anyone who dropped MenuLib to use VR had no in-game way to configure the mod. Every mod setting is now also bound as a config entry: hidden from REPOConfig while MenuLib is present (the graphics menu stays the surface), shown when it is absent. settings.json stays the source of truth and the two surfaces stay in sync. Game settings like resolution and vsync are unchanged, they live on the game's own video menu. Reported by MatelessSnail (#8).

---

## 1.7.3

- Ultrawide: the menus stop disappearing. 1.7.2 added a pass that hides HUD graphics the widened capture reveals past the vanilla 16:9 canvas, on the premise that anything fully outside that canvas is invisible to a 16:9 player and safe to cull. That premise is wrong for anything interactive or animated, and the pass was eating the menus: the mouse cursor vanished (it follows the pointer 1:1 across the whole panel, so it spends most of its time past the canvas edge where it is genuinely visible), the main-menu buttons went invisible until you hovered them, and the Semibot model in the pause menu blinked out a second after the page opened. None of it could self-correct: the watchdog that hands a culled element back watches the element's own transform, but the cursor drives its parent, a parked button rests without moving, and the model sits on a render texture that never moves, so once culled they stayed gone. The cursor and everything under a menu page are now exempt from the cull, which only ever needed to own the in-game HUD parking lot and stray off-canvas art. Reported by AncientPixel-Aron, komkomissarov, b3nz1k and Mortycio. Thanks to everyone who reported it.

---

## 1.7.2

- DLSS is back. 1.7.1 shipped without its two NVIDIA runtime files (`nvngx_dlss.dll` and `ngx_bridge.dll`), so every 1.7.1 install logged "nvngx_dlss.dll missing or invalid" on a loop and ran with DLSS/DLAA disabled. The package step pulled those DLLs from the build output and only warned when they were absent, so a clean output dir silently produced a DLL-less zip. They are restored, and the package step now hard-fails if either ngx file is missing instead of warning, so a release can't go out without them again. Reinstalling 1.7.1 didn't help, since the package itself had nothing to reinstall; update to 1.7.2 and DLSS works again (on Linux/Proton you still need NVAPI enabled for it to actually engage).
- Ultrawide: parked HUD elements stop leaking past the widened capture. SemiUI hides an element by sliding its transform to a park anchor without deactivating the frame and box children, so on an element that never opens in a scene (the chat box on the title screen) the widened capture caught those children spilling past the canvas edge. A Graphic under a parked SemiUI is now culled even when it only spills the edge, and it un-culls the instant the element animates open, so what you see still matches a 16:9 player.

---

## 1.7.1

- The ultrawide HUD stretch is fixed (the 1.7.0 known issue, reported by Mortycio and AncientPixel-Aron). Since 1.6.0 the mod displayed the game's 16:9 HUD texture stretched across the full panel, so menus and HUD rendered smeared and off-center on 21:9/32:9. The HUD lives on a world-space canvas captured by an orthographic overlay camera, and the fix is at that camera: its aspect now matches the panel (the capture grows vertically on narrower-than-16:9 panels so nothing crops), the canvas renders pre-squeezed into the texture, and the full-screen display stretch cancels it exactly. The frame-space vignette spans the whole panel with no seam, and the mouse mapping derives from the same per-frame condition as the framing so the cursor and the geometry can't desync. Works at 21:9, 32:9, 16:10, 4:3 and 5:4.
- Wide panels actually show more world now instead of stretching the sides. Two separate causes, both real. "Camera Top", the layer the game renders held objects on so they don't clip through walls, ships pinned to the vanilla box ratio; a held cart at the screen edge drew stretched while the world behind it rendered true, and that mismatch is most of where the "fake ultrawide" feel came from. Camera aspects are re-derived from their actual render targets every frame now. Separately, the old aspect-aware FOV default ramped vertical FOV to 80 at 21:9 on top of the aspect gain, and the rectilinear edge smear at ~127 degrees horizontal reads as pure stretch. The default is plain HOR+ now: vanilla vertical FOV at every aspect, capped at 120 degrees horizontal so 32:9 doesn't fisheye. The slider still overrides.
- The widened capture sees parts of the HUD canvas that vanilla never shows, and three cleanups deal with what lives out there. Full-canvas covers (boot fade, pause dim, hurt vignette, splash and loading backgrounds) are classified generically and stretched to the capture so their edges stop reading as hard lines mid-screen. The title screen's side gradient extends to the screen edge, which kills the black bar on the main menu. And the HUD parking lot is handled: the game "hides" HUD elements by parking them just past the canvas edge while still rendered, and on narrow panels the taller capture exposed the whole lot (the arena race timer sat top-center on the title screen). Parked elements now get pushed past the capture edge, and anything else that is only visible because of the widen, including UI other mods stage off-canvas, is hidden until it moves. What you see matches what a 16:9 player sees.
- Settings now survive riding along in a shared mod profile. settings.json lives next to the DLL, so importing someone's profile imports their saved resolution and preset. Three guards: the saved resolution is validated after the graphics system is up instead of during plugin load (applying it that early shoved the game into a tiny corner window on the test machine), the preset resets to Auto when the GPU name changes (GPU name, not resolution, so swapping monitors on your own rig doesn't trip it), and Auto with no benchmark yet falls back by GPU tier instead of assuming High (sorry to the iGPU that got High shadows).
- Better field diagnostics at default log verbosity: the load banner logs panel resolution and aspect, the ultrawide system logs a one-line classification for every HUD element it touches, and a `[ultrawide] reveal:` line names anything that shows up past the vanilla canvas. A stock LogOutput.log is now enough to debug an aspect-ratio report remotely.
- Typography pass over every text surface (readme, changelog, config descriptions, log lines). No behavior change.

Thanks to Endershade for testing the narrow-panel path on a 5:4 monitor and finding most of what's fixed above.

---

## 1.7.0

- Experimental RepoXR (VR) compatibility: new and only lightly tested, so treat it as a first pass and report anything that looks wrong in a headset. The HD pipeline redirects the main camera onto its own render texture and jitters the projection matrix each frame for temporal upscaling. Both assume one flat display, so under RepoXR they collapsed the stereo view (the bug report described each eye pointing the wrong way). When a VR headset is active REPOFidelity now stands its camera pipeline down: no upscaler, no render-texture redirect, no projection jitter, no FOV or ultrawide/aspect override. The optimization layer keeps running, since shadow culling, the CPU and GC patches and the quality settings never touch the stereo view and VR can use the extra frames. Raising VR render resolution stays RepoXR's job through its own CameraResolution setting. VR is detected from Unity's XR state, so there's no hard dependency on RepoXR.
- Pulled the F9 cost probe and the F11 light-diagnostics dump out of the build. Both were dev-only instruments I used while tuning, and the cost probe patches and unpatches methods at runtime to time them; no business shipping that in a release. They live in a separate dev-tools folder now. The `F9 Cost Probe` toggle is gone from the Graphics menu and `diagnosticsEnabled` drops out of settings.json; nothing else changes in-game.
- Quieted the logs for normal play. The fog/clip debug line no longer prints every frame (it only logs when the value changes), and a pile of routine status lines (resolution changes, preset resolves, mod on/off, shader loads) dropped from info to debug. A default install now shows the load banner, real warnings, and the benchmark output when you run one, and stays quiet mid-game. Also pulled the per-frame timing spans that only fed the old cost probe, so the shadow tick does a touch less work.
- Fixed players falling into the void on their second death when running alongside an extraction-point revive mod (InstantRevive, Hura Instant Revive). The CPU layer throttles distant players' cosmetic Updates to save work past fog distance, but it was also skipping `PlayerReviveEffects.Update`, which is the only thing that ends a revive (that class has no Reset), so a corpse sitting past fog from the spectate camera never finished reviving and the next death teleported the player to a stale death-head position, i.e. into the void. Death and revive effects are no longer throttled; they cost next to nothing when idle, so it was never buying anything. Thanks to AngelcoMilk for the report (#4).

---

## 1.6.3

- 16:10 / 4:3 / 5:4 panels now get the same un-squashed world view 21:9 / 32:9 panels have had since 1.6.0. Vanilla on a 16:10 panel renders the camera's HOR+ output into the game's fixed 750×418 (16:9) `Render Texture Main` RawImage and letterboxes top and bottom. The underlay path was already there for the wider case but gated only on `aspect > 1.85`; added a symmetric `aspect < 1.768` lower threshold (16:9 = 1.7777..., so 1.768 catches anything definitively narrower while leaving exact 16:9 untouched) so the underlay engages for narrower-than-16:9 panels too. Same path solves both directions: bind world camera RT to a full-screen RawImage on the `sortOrder=0` underlay canvas, hide the game's `mainImage` + `Background`, mirror the post-FX RT onto a sibling at full-screen size. FOV bump, menu-camera narrowing, and main-menu fog tightening stay 16:9+ only; those compensate for the world edge that wider FOV reveals on the truck scene, which a 16:10 panel doesn't expose
- Fixed dead arrow hover/click on every `REPOSlider`'s `<` `>` buttons in the graphics popup. MenuLib's `OpenMenuPage` writes `addedPageOnTop = false` unconditionally, and the game's `MenuManager` filter then skips `RegisterHover` on stock `MenuButton`s under that flag; slider arrows registered no hover state and clicks did nothing. Re-stamping `addedPageOnTop = true` after `OpenPage` restores the standard hover + click path. Drag still worked through the slider's own input handler, so this only affected users clicking the arrows instead of dragging

---

## 1.6.2

- Updated for R.E.P.O. v0.4. 1.6.1 had the recompile but missed the v0.4-specific shadow-proxy fix: the game now ships shadow-only proxy renderers on the local avatar (body, head, flashlight) and the previous distance-cull / tiny-renderer cull passes stamped `ShadowCastingMode.On` over them, leaving the body and flashlight visible in first-person. Death cam also flashed a white frame because the mod's fog postfix was stamping `fogEnd + 10` over the death camera's 70m near plane and inverting the frustum. Both fixed plus two related cleanups; full per-fix detail in the 1.5.3 entries below
- Patches retargeted for v0.4: shadow distance cap moved from `SpectateCamera.Update` to `LateUpdate` (game moved its state-machine tick), fog multiplier moved to a `FogLogic` postfix so room-to-room `RoomFog` transitions stay scaled, and `RoomVolumeCheck.CheckSet` replacement mirrors v0.4's new scouting-point credit + tutorial extraction reminder. Existing shadow cap, fog multiplier, and optimized room-volume check behave the same on v0.4 as they did on v0.3

---

## 1.6.1

- v0.4 recompile (superseded by 1.6.2; 1.6.1 was missing the shadow-proxy and death-cam fixes)

---

## 1.6.0

- Ultra-wide support (21:9, 32:9, anything above 16:9). Vanilla on a 21:9 panel renders the world at HOR+ camera aspect into the game's fixed 750×418 (16:9) `Render Texture Main` RawImage, so the world image is squashed horizontally and the screen sides are filled with a full-screen black `Background` image. The mod adds a separate `sortOrder=0` Canvas with a full-screen RawImage bound to the same world RT, hides the game's `Background` and `Render Texture Main` so they don't overdraw, and mirrors the post-processing pass (`Render Texture Overlay`) onto a sibling RawImage at full-screen size so vignette / bloom / screen flashes cover the full aspect. Game's UI canvas (HUD, buttons, menus) is left untouched at `sortOrder=1` and renders on top; mods that hook those by GameObject path or component continue to find their targets unchanged
- Aspect-aware default vertical FOV. 16:9 stays at vanilla, 21:9 lerps up to ~80°, 32:9 = 90°. Lerps linearly inside each band. Slider override (any value > 0) wins over the aspect default. FOV slider also animates smoothly now via a 0.25s SmoothStep coroutine; previously sat frozen until a sprint or tumble event ticked the game's zoom curve
- Menu / title-screen camera narrows on wide aspects to hide the world edge that wider FOV reveals past the truck. Strength scales: 0 at 16:9, ~0.51 at 21:9, 1.0 at 32:9 (full VERT- math). Hooked on `CameraNoPlayerTarget.Awake`
- Main-menu fog tightens to 0.60× of vanilla on ultra-wide aspects only. The truck scene is a rolling treadmill: assets despawn at fixed boundaries, not at camera distance, so far-clip / lod-bias / cull-distance overrides do nothing for the popping. Stronger fog moves the visible fade ahead of the despawn point so the disappearance happens behind opaque fog. Self-gated on `MenuLevel()` + `UltrawideUiFix` + aspect > 16:9, with a 30-frame stabilization delay so we don't sample fog while the scene's lighting is still mid-load (would write fog × 0.60 = 0 = fog-colour screen flood). Restored when transitioning out of the menu, but never written into gameplay's `RenderSettings.fog`, since the main menu shares its Unity scene with gameplay and a vanilla restore would bleed menu values onto the level
- F10 ("vanilla 16:9 compare") for ultra-wide users. Toggles `UltrawideUiFix` off (so the game's `Background` re-letterboxes the sides) and forces every camera's aspect to 16:9 (so world content renders un-squashed into the 16:9 mainImage box). Restored on next F10. Per-frame enforcement of camera aspect because game scripts can write it from `Screen.aspect`
- Persisted resolution validated against the current monitor at startup. Catches "saved 16:9 mode on a 21:9 panel and now we're back on a 16:9 panel that can't deliver", and "stale 720p fallback from a previous bad mode-set". On mismatch (>5% aspect difference, or persisted pixel count < 50% of monitor native) the saved values reset to `Display.main.systemWidth/Height`. Resolution dropdown also reads native from `Display.main` instead of `Screen.currentResolution` so the dropdown can list the actual native even when the game is currently running below it
- Auto-benchmark no longer auto-fires on resolution change. New `AutoTuneNeedsInitialBenchmark` checks revision + GPU but not screen size, so swapping resolutions doesn't trigger a 90-second benchmark every time. Resolution-driven staleness is silent until the user manually re-runs from the menu. Auto-benchmark is also gated on `ModEnabled` so an F10-disable + resolution swap can't re-poke the upscaler while it's torn down (the old "black screen after F10" failure mode)
- F10 keypress blocked while the mod's graphics popup is open. Prevented half-applied UI state when F10 fired with a settings sub-page in the way
- `OnSettingsChanged` handler wraps each refresh call in try/catch. A single bad frame in the canvas pipeline used to silently kill the FOV slider when the canvas walker hit a destroyed Graphic; now each refresh is independent

## 1.5.3

- Distance shadow cull threshold for small props (`bounds.size.magnitude < 3m`, or 5m on Potato) was `ResolvedShadowDistance × 0.7f`, but `Settings.ApplyFogClamps` already pulls `ResolvedShadowDistance` down to `fogEnd × 1.1`, so the cull point landed at ~0.77 × `fogEnd`. On levels with a typical fog config (start ~50% of end), that's dead in the middle of the visible fog fade-in band; small props lost their shadows where you could still see them. Threshold now uses `fogEnd × 1.1f` to match the player-avatar / point-light / flashlight-budget paths in the same file (Potato pulls at `fogEnd × 1.0f` for the extra savings)
- `RoomVolumeCheck.CheckSet` replacement: overlap + swept-path + sticky + rest-skip. Vanilla samples `Physics.OverlapBox` at a single point at 10Hz, so fast movement (tumble wings, fly) can skip a whole room between ticks. The miss reads as a false "not in any room" state: breaks per-room ambience / reverb, flickers truck-safety and enemy-AI room awareness, and costs the player the scouting-points credit when the game eventually adds that. Replaced with a four-tier check: rest-skip when the player is stationary, `OverlapBoxNonAlloc` for the common case, `BoxCastNonAlloc` sweep from last position to catch seam crossings, and sticky carry-over for brief coverage gaps where the player flies above the room's collider ceiling (the sweep can't catch those because Unity's cast APIs skip the starting collider). Ungated: rest-skip makes the common case strictly cheaper than vanilla, so the patch now runs regardless of frame time. Over a 26-minute play session the old NonAlloc-only patch would have had 4022 false-empty ticks; the new patch had zero. Per-tick CPU average on a 5090 dropped from 3.26 µs (vanilla allocating path) to 0.57 µs (new authoritative path), and the `Collider[]` allocation on every call is gone
- Distance-cull restore path was hardcoding `ShadowCastingMode.On` instead of saving each renderer's original mode. `ShadowsOnly` shadow-proxy meshes got converted to visible `On` renderers on F10-off / auto-tune sweeps; turned latent on shipped REPO (no `ShadowsOnly` small MRs to hit) but manifested as a floating "second flashlight" on v0.4.0 once that build added shadow-proxy renderers on the avatar. Now each watchlisted renderer's original mode is captured into a dict at build-time and restored verbatim
- Fixed local-avatar shadow-proxy renderers (flashlight proxy, body/head proxies) rendering visibly on v0.4.0. `PlayerAvatarVisuals.ApplyLocalVisibilityBody` owns `shadowCastingMode` on the local avatar's renderer set; its `ApplyLocalVisibility` early-exit gate (`if (localVisibility == newVisibility) return;`) means once `localVisibility` settles at `ShadowsOnly`, any outside write to those renderers persists; the game never re-cascades. Our `CaptureDistanceCullWatchlist` and `ApplyTinyRendererCull` were scanning scene-wide `MeshRenderer`s, catching the avatar's children in their prefab-default `On` state before the game's first `Update` tick cascaded `ShadowsOnly`, and later stamping that `On` back on F10-off / preset change / auto-tune. `GetComponentInParent<PlayerAvatar>()` couldn't filter them out either: the renderers live under inspector-linked `playerAvatarVisuals` / `flashlightController` / `playerCosmetics` roots that sit OUTSIDE `PlayerAvatar.transform`. Now we pre-build a `HashSet<Renderer>` from those linked fields before each scene scan and skip anything in it; the game keeps full ownership
- Fixed the main-menu decor truck disappearing on F10 (mod toggle off) while its attached lights kept rendering. `_vanillaFarClip` was a single static captured once per `EnvironmentDirector.Setup`, so the in-game vanilla far-clip value (~15m) was stamped onto the menu camera (vanilla far ~230m) on restore, clipping the truck out of view. Tracked per-camera via a `Dictionary<Camera, float>` now: each camera records its own pre-mod `farClipPlane` on the first write and restores from that. Cameras the mod never touched are left alone
- Player avatar preview at 1024² + MSAA + SMAA stopped applying when transitioning between the pause menu and the cosmetics customization menu. The `!expressionAvatar && nonExpressionCount > 1` bail in `PlayerAvatarMenuAAPatch.ApplyToMenu` was tripping on transient overlap: when the menu page changed, the outgoing PAM stays alive in the scene for one extra `Update` tick before its `parentPage`-null self-destruct fires (`PlayerAvatarMenu.Update` line 120-124 in t20). The new PAM's `Start` postfix ran while the dying PAM was still findable, count = 2, bail tripped, preview stayed at vanilla resolution until F10-cycle forced `ReapplyAll`. Filter now counts only PAMs whose `parentPage` is alive AND `activeInHierarchy`: dying PAMs (parentPage destroyed → `== null`) and `worldAvatar` / `iconMakerAvatar` variants (no `parentPage` assignment in `Awake`) drop out for free, so truck-lobby protection still works. Cap raised to 2 to allow a real pause+customize overlay
- Death cam rendered a white frame on every death. `EnvironmentDirector.FogLogic` in t20 explicitly skips its own `MainCamera.farClipPlane` write when `SpectateCamera.State.Death` is active (line 248-251): `StateDeath` sets `nearClipPlane = 70f, farClipPlane = 90f` so the camera renders the body's 70-90m orbit slice, and any further far-plane write inverts the frustum. Our `PostfixFogLogic` (which runs after vanilla in the same Update tick) was stamping `fogEnd + 10f` (e.g. 54m on a 44m-fog level) over the 70m near plane every frame; frustum became 70 → 54, no geometry rendered, screen cleared to fog color. Centralized the death-state check at `UpscalerManager.SetModFarClip`, the chokepoint every QualityPatch / UpscalerManager farClip write routes through. Resumes normal writes once the player respawns. Worth backporting to public; same inversion can happen on any level whose `Level.FogEndDistance + 10f` is less than 70m

## 1.5.2

- Shadow-budget tick was calling `Object.FindObjectsOfType<Light>()` every 100ms, same pattern as the 1.5.1 flashlight-controller scan. Cached the item-glow list on scene load alongside the other watchlists; per-tick scan gone
- Fixed: menu/preview avatars (pause-menu portrait, expression wheel) had their cosmetic Updates (PlayerExpression, PlayerAvatarEyelids, AnimNoise, FlashlightLightAim/Tilt, PlayerDeathEffects, PlayerReviveEffects, OverchargeVisuals) incorrectly throttled by the per-player fog-distance gate from 1.4.0. Preview avatars sit at world positions like `(0,0,-2000)`, far enough from `Camera.main` that the gate flagged them past-fog and skipped their Updates, freezing the preview's expressions / eyelids / bone poses. Surfaced as a regression at low framerate (where the cpuPatches auto-gate flips on most often). Throttle now early-bails for any transform without a `PlayerAvatar` in its parent chain
- PhysGrabObjectGrabArea.Update was calling `playerGrabbers.ToList()` every frame even when nothing was actively grabbing the object; that's an empty List per instance per frame, all gen0 garbage. Added a fast-path that skips the entire Update when both `playerGrabbing` and `listOfAllGrabbers` are empty. Per-call cost dropped from ~0.6 µs to ~0.2 µs
- AudioListenerFollow.Update was rebuilding `LayerMask.GetMask(new string[] { "LowPassTrigger" })` and allocating a Collider[] from OverlapSphere on every 15Hz tick. Layer mask cached statically, OverlapSphere swapped to NonAlloc

## 1.5.1

- Flashlight shadow budget tick was calling `Object.FindObjectsOfType<FlashlightController>()` every 100ms; on a 7000+ object scene that's ~9ms per tick and the source of a lot of 0.1% low spikes. Cached the controller list on scene load / player spawn; per-tick cost dropped from 0.93ms/frame amortized to 0.001ms/frame on a large map. Worst-frame times dropped ~15ms in testing
- Distance shadow cull pass now processes a 1000-renderer slice per tick instead of scanning all 5000+ entries every 100ms. Full watchlist re-evaluated every ~500ms. Per-tick cost dropped from 2.3ms to 0.5ms. The 10% hysteresis band absorbs any latency from chunking at the boundary
- Auto preset on CPU-bound systems now unlocks all 7 perf optimizations instead of just 2. Autotune was saving `perfLevel=0` for CPU-bound users where "0" meant "Ultra visual tier" in the autotune code but "don't cut anything" in the perf-opt gating; the two fields had opposite semantics. Now forces `perfLevel=3` when `cpuBound` regardless of shadow tier, unlocking Explosion / Item / Animated / Particle / TinyRenderer shadow culling on Auto for weak-CPU users
- Point Light Shadows perf opt (new): distant point lights past fog + their own light range get their shadow casting killed, restored when the player comes back in range. Gated at Medium preset and above. Worth it in lights-heavy scenes where 8+ point-shadow lights mean 48+ cubemap passes per frame
- Autotune upscaler pick: CPU-bound non-NVIDIA systems with headroom (≥1.10× above target refresh) now pick FSR Temporal over SMAA: better edge AA at ~0.5-1ms CPU cost the user can afford when they're above target. Tight-budget CPU-bound stays on SMAA
- Switching preset from Custom to Auto no longer reverts to Custom on the next launch. The probe's sweep was mutating individual settings (upscaler mode, fog) which triggered `OnSettingTweaked`'s "tweak → Custom" fallback. Probe now brackets sweep + restore in a preset-revert suppression counter
- F9 Cost Probe: sweep cells run uncapped (VSync off + no target frame rate) so sweep numbers reflect real hardware cost instead of collapsing onto the user's FPS cap. Baseline still runs at the user's actual settings. Restored on natural exit, abort, or exception
- F9 Cost Probe: added `Auto` as the first sweep cell so users see autotune's exact resolved config in the current scene instead of interpolating between the discrete Potato / Low / Med / High / Ultra cells
- F9 Cost Probe: added a `Mod-internal cost` section with Stopwatch-measured spans around the mod's hot paths (per-tick shadow passes, camera hooks, LateUpdate, ApplyCAS, SceneOptimizer.Apply). Surfaces where the mod's per-frame cost actually lives; caught the flashlight-budget 9ms tick above
- F9 Cost Probe: report now appends `autotune.json` and `settings.json` at the tail so one clipboard paste gives full diagnostic context instead of three separate file requests
- F9 Cost Probe: waits for autotune to complete before starting its own sweep; a mistimed F9 press used to collide with autotune's phase 0 CPU-ceiling test
- F9 Cost Probe: clipboard copy verifies via round-trip and falls back to `wl-copy` / `xclip` / `xsel` on native Linux when Unity's `systemCopyBuffer` silently no-ops. Log line and `Done` status reflect actual outcome
- F9 Cost Probe: Vanilla (F10) sweep cell now resets `RenderSettings.fogStartDistance` / `fogEndDistance` via `RestoreVanillaQuality`. Previously inherited the 0.3× fog from the preceding fog-matrix cell, so the vanilla sample ran at sub-vanilla fog and looked wrong on-screen
- F9 Cost Probe and optimizer benchmark progress bars interpolate wall-clock between milestones instead of jumping per-phase

## 1.5.0

- Fixed flashlight shadow disappearing on Medium and below, and persisting dead through preset changes. Three systems were fighting over `spotlight.shadows`: the `FlashlightController.Start` Harmony patch, the flashlight foreach in `SetItemLightShadows`, and `ApplyZeroIntensityShadows` catching the flashlight during the pause-menu Hidden state. Plus a duplicate unsaved zap in `QualityPatch.ApplyRangeTieredLightShadows` that never restored. Consolidated ownership to `UpdateFlashlightShadowBudget`. Potato drops the flashlight shadow entirely (it's the "cut everything" preset); every other preset keeps the 4 closest
- Fixed Ultra's `shadowDistance=150m` / `lightDistance=75m` clamping to ~5m after a Custom-preset fog-slider session. `ApplyFogClamps` was reading a stale `ResolvedEffectiveFogEnd` left behind by the previous preset's multiplier; now recomputes from the captured vanilla baseline on every call
- Custom-preset per-feature toggles (Explosion Shadows, Item Light Shadows, Animated Light Shadows, Particle Shadows, Small Object Shadows, Distance Shadow Culling, Flashlight Shadow Budget) apply immediately instead of sitting silent until the next preset swap or level load. The seven `PerfXxx` setters saved to disk but never called `NotifyChanged()`, so `PerfSettingsWatcher` never saw the flip
- F9 Cost Probe is a toggle in the mod menu now, next to Debug Overlay. Previously you had to hand-edit `diagnosticsEnabled` in `settings.json`; fine for me, not fine for testers
- Item Light Shadows toggle description corrected: after the flashlight refactor it only affects handheld glow props, so the description no longer claims it hits flashlights
- Off-screen shadow caster reduction: distance-cull watchlist bounds bumped from 2m to 3m (5m on Potato), permanent tiny-renderer kill from 0.3m to 0.5m (1m on Potato). Potato also cuts shadows at 50% of shadow distance instead of 70%. Pulls more mid-sized props into the cull pool without touching architectural geometry
- F9 Cost Probe: baseline now forces mod on so the breakdown reflects gameplay-with-mod cost instead of duplicating the Vanilla sweep cell when the user pressed F10 before F9. User's actual `ModEnabled` state is restored on exit; previously hardcoded to `true`, which silently switched the mod back on for users who wanted it off
- F9 Cost Probe: replaced `mesh.triangles.LongLength` with `mesh.GetIndexCount` for the scene triangle count. Game assets ship with `isReadable=false` so the array path threw hundreds of "Not allowed to access triangles/indices" Unity errors per probe run. GPU metadata path gives the same number without the readability requirement
- Log cleanup: seventeen spammy LogInfo lines dropped to LogDebug (DLSS re-init chatter, shader bundle load, NGX bridge callback, DLSS eval-OK success logging, per-preset scene-optimizer breakdowns, avatar-preview setup, fog apply, shadow-res tiered). Retained LogInfo on milestones: `Mod ENABLED/DISABLED`, preset resolve summary, `Upscaler active`, restore-state diagnostics, benchmark results, probe output
- F9 is now an opt-in diagnostic. Off by default; flip `diagnosticsEnabled` to `true` via the new menu toggle (or in `settings.json`), load a save, press F9 and the full probe runs (~90s), copying a report to the clipboard when it finishes. Built for sending me "here's what's going on with my machine" data when someone needs support
- Probe baseline now samples your real settings (preset, upscaler, fog, AA, whatever you play with) so the profiler markers / per-camera timings / script cost rankings reflect actual frame time at actual settings. The preset × fog × upscaler sweep that comes after still normalizes to Ultra + DLAA + fog 1.0× so the individual cells compare cleanly across users and builds
- Report header grew to include GPU VRAM + graphics API, system memory, OS, monitor refresh rate, and mod flag state (`modEnabled`, `optEnabled`, `cpuPatches`); should be enough for one-shot diagnosis without needing follow-up questions
- Multiplayer breakdown section: per-PlayerAvatar distance from main camera, shadow-casting renderer count, flashlight budget state (within / culled / past fog), and the cosmetic-component totals that get throttled past fog. Tells you at a glance whether a busy lobby is hitting the budget caps or the cosmetic throttle is kicking in
- Player input is locked for the duration of the probe (~90s) so movement / look / grab can't perturb the measurement. F9 to abort still works because it bypasses the game's input-disable flag. Probe only starts in a gameplay level; pressing F9 in the main menu does nothing
- Cosmetic throttle expanded: AnimNoise, FlashlightLightAim, FlashlightTilt, PlayerDeathEffects, PlayerReviveEffects now skip their Update when the player is past fog end. Scales with player count: a 20-player lobby with 18 past-fog players saves on the order of 0.1 ms per frame on the list together. Deliberately excluded: PlayerHealthGrab / PlayerDeathHead / PlayerTumble (fire RPCs and mutate gameplay state) and FlashlightBob / FlashlightSprint (already early-return for remote players)
- F9 report gained GC tracking: gen0 / gen1 collection counts over the sample window plus the Mono heap delta, plus the worst single-frame time in ms. Gives a direct line on whether 0.1% lows are GC pauses vs. steady-state cost

## 1.4.0

- Shadow and light distance now clamp to fog end instead of being independent per-preset values. Ultra's 150m shadows behind a 40m fog wall was pure waste; the geometry's invisible anyway. Shadow caps at fog × 1.1, light at fog × 1.2, the overshoot keeps casters right at the fog line from popping as you walk past
- Fog slider lower bound opened to 0.3×; 1.3.0's changelog claimed this already happened but the setter clamp was still blocking it, and the fog apply path had a `> 1f` gate that silently ignored anything under vanilla. Presets and auto-tune stay above 0.5× ("playable floor") so dragging fog into your face stays a deliberate choice
- Potato preset defaults to fog 0.85×: small atmospheric reduction, small extra savings on top of the distance cascade
- Small renderers (bounds < 2m) stop casting shadows past 70% of effective shadow distance, re-enable when closer, 10% hysteresis band kills flicker at the boundary. Cuts off-screen shadow-map work that the game pays for on distant props
- Per-light shadow map resolution is bucketed by range across every preset: <5m → 256, 5-10m → 512, 10-20m → 1024, >20m → 2048. Flashlight keeps 4096 on Ultra only. Potato caps at 1024. No more 4K shadow maps on 3m Button Lights
- ParticleSystem.cullingMode set to Automatic on every system so off-screen and non-emitting systems skip their per-frame update. A typical level registers 230+ systems with 1 actively emitting; the other 229 were ticking for nothing
- F11 toggles the optimization layer. Unlike F10 (which cuts the whole mod, including DLSS / SMAA), F11 leaves the visual features on but reverts every shadow / physics / render hack to vanilla. On-screen note reads "OPTIMIZATIONS OFF (F11)" while active
- F10 (mod off) now returns to true vanilla state. Tiny-renderer culling, animated-light shadows, zero-intensity lights, GPU instancing, particle culling mode, and per-range shadow resolution all save their original state and restore it on disable; previously most of those were one-way. A `restore-state` log line prints `OK` or `LEAK` on every F10 so any regression is obvious
- Pause-menu avatar preview gets the treatment it was missing. The 320×320 / 209×418 render texture the game hands it bumps to 1024×(matching aspect) with 4× MSAA and SMAA via PostProcessLayer; vanilla's jagged edges come from rendering the avatar at native RT resolution with no AA. The camera is gated on the hosting MenuPage being active so it costs zero frame time when you're not looking at it. Expression / icon-maker / world-avatar variants stay vanilla (they exist during gameplay, one per player in some cases, and menu-grade rendering on them is pure waste)
- CPU-bound Auto now trims its shadow budget by 7 (Ultra 25 → 18, etc.). The preset budget was sized for GPU-bound systems with headroom; on CPU-bound Auto it was throwing ~10 extra shadow draws a frame at a scene already choking on them
- `PhysGrabObject.Update` skips entirely when the Rigidbody's sleeping and the object isn't being grabbed; 40+ idle objects in a typical scene used to pay for a full Update tick each frame for nothing. Grab-list bookkeeping still runs when grabbed
- Concurrent flashlight shadow maps now capped at the 4 closest to the main camera. 20 flashlights × 2048² shadow maps is 80 MP/frame of pure rasterization, bigger than a 4K framebuffer, and was wrecking mid-range GPUs in busy lobbies. Distant flashlights keep their lit cone; only the shadow casting goes dark past the budget. Flashlights past fog end skip shadow rendering entirely and don't count against the budget, since their shadows would be invisible anyway
- Avatar preview RT bump is skipped when there's more than one non-expression PlayerAvatarMenu in the scene. The truck lobby with 8 players used to bump every preview RT, multiplying render cost linearly with lobby size for icons the user barely looks at. Single-player pause menu still gets the sharp preview
- Player avatar renderers now stop casting shadows past fog × 1.1, same pattern as the small-prop distance cull. 20 skinned-mesh avatars contributing to the directional shadow map adds up fast on weak GPUs, and the contribution is invisible when the avatar is behind fog anyway
- Forced `updateWhenOffscreen = false` on every player avatar's SkinnedMeshRenderer. Unity only skips bone matrix updates when this is false; vanilla left it true on some avatars, paying for off-screen player animations every frame
- `PlayerAvatarEyelids.Update`, `PlayerExpression.Update`, and `PlayerAvatarOverchargeVisuals.Update` skip entirely when the player is past fog end. The blendshape spring math is the most expensive of the three; three players past fog used to pay for it three times a frame for visuals nobody could see

## 1.3.0

- DLSS evaluation now runs on a private D3D12 device with D3D11↔D3D12 shared-texture interop, working around the D3D11 DLSS path being blocked by the driver on RTX 50-series (Blackwell). Existing RTX 20/30/40 cards use the same code path
- DLSS gets projection-matrix jitter the same way FSR Temporal does; sub-pixel accumulation now functions, which is the whole point of a temporal upscaler
- DLSS now requests Preset E (CNN) instead of the transformer default, more forgiving of Unity's built-in-RP motion vectors which aren't perfectly jitter-clean
- Fixed DLSS output flashing white the first frame after enable (output RT was undefined until DLSS wrote the first frame over it)
- Fixed DLSS staying black when a scale change caused Unity to recycle an RT's native pointer into a new texture; shared-handle cache now cleared on re-init
- Sharpening slider no longer rebuilds the whole upscaler pipeline every tick, since it's a per-frame shader uniform
- Shadow budget system: caps how many small point lights cast shadows at once, closest to camera get priority. Fades shadow strength in/out for smooth transitions instead of pop-in. Configurable per preset (Potato=5, Ultra=25) or manually via Shadow Limit slider (0=unlimited)
- Tiered shadow map resolution: directional lights use global resolution with cascades instead of a forced custom value, small decorative lights get 512 instead of 4096, infrastructure lights cap at 2048. Massive shadow cost reduction with no visible quality loss
- Shadow cascades: Low/1, Medium/2, High+Ultra/4. Fixes directional light shadow quality (window lighting, outdoor shadows) which was previously stuck at 1 cascade
- Disabled shadows on zero-intensity lights: mines and other inactive light sources were generating shadow maps for nothing
- FSR Temporal now jitters the projection matrix for proper sub-pixel accumulation: sharper edges and better temporal stability
- Fixed FXAA darkening the image: keepAlpha wasn't set, so luminance was bleeding into the alpha channel during compositing
- Max FPS is now a smooth slider (30-360 + Unlimited) instead of preset options; set exact values for adaptive sync
- Performance toggle labels changed from Off/On to Keep/Disable for clarity
- Auto-tune now weights 1% and 0.1% lows into its decision (50% avg + 30% 1%-low + 20% 0.1%-low). Stuttery systems actually step down now instead of sliding by on a good average
- Auto-tune targets monitor refresh rate directly; the old `refresh × 1.05` padding was fighting the 1%-low safety the benchmark was already applying, which is how a 5090 on 240Hz ended up with LOD 2 and 8x AF
- Fixed CPU-bound stepdown using GPU-cost predictions to size CPU savings. New ladder only touches the knobs that actually reduce draw-call count (shadow distance, light count, light distance) and leaves shadow quality / LOD / AF at Ultra where they belong. Strong-CPU systems now also get a bonus tier that raises sharpness, LOD, and shadow distance above vanilla Ultra when benchmark headroom allows
- Scene-complexity factor: benchmark accounts for sparse scenes (menu, small rooms) vs. packed ones so real gameplay doesn't blow the budget
- Intel Arc iGPU (Meteor Lake / Lunar Lake "Intel Arc Graphics") is now detected as integrated. Discrete Arc A380/A750/A770/B580 still come through as regular dGPUs and get FSR
- Live status line at the top of the graphics menu: CPU-bound/GPU-bound tag, frame time, fps, render resolution, upscaler. Updates 4×/sec while the menu's open
- Auto-Tune button label now tells you what clicking will actually do (`AUTO-TUNE BENCHMARK (15s)` in-game, `AUTO-TUNE - WILL QUEUE (START A GAME)` in the menu, `AUTO-TUNE QUEUED (WILL RUN ON NEXT LEVEL)` after queuing). Tap while queued to cancel
- Shadow Limit and Draw Distance sliders moved the "0 = unlimited" / "0 = auto" hint into the label instead of burying it in the description
- Fog Distance slider opened up below 1.0× so it's actually a performance knob; upper bound stays at 1.1× because anything farther would give a gameplay advantage
- Pixelation moved from the Upscaling group to Post Processing, which is what it actually is
- Potato preset now always runs at 50% render scale (matches vanilla Potato) instead of flipping to 100% on CPU-bound systems

## 1.2.0

- Added CPU optimization patches: EnemyDirector loop throttling, RoomVolumeCheck NonAlloc, SemiFunc result caching, PhysGrabObject list iteration bugfix, LightManager allocation-free cleanup
- CPU patches auto-enable based on frame time: active when needed (>8ms), dormant on fast systems where Harmony overhead would cost more than the savings
- Added F11 optimizer benchmark: measures vanilla vs GPU/GC vs all optimizations with 2-pass averaging, writes `optimizer_benchmark.txt`
- Fixed auto-tune misclassifying high-FPS systems as CPU-bound; threshold now 95% above 120fps, 85% below
- Fixed auto-tune on Proton/DXVK: detects when CPU ceiling is below target refresh regardless of ratio
- Fixed CPU-bound auto-tune maxing GPU settings on weak GPUs; stepdown cascade now applies to both CPU and GPU settings when budget is tight
- Fixed divide-by-zero in CPU patch gate during scene transitions
- CPU-impacting settings step down before pure-GPU settings (fog, AF) in the auto-tune ladder
- Auto-tune unlocks FPS cap during measurement and restores it after
- Fixed DLSS motion vector warning on F10 toggle: upscaler disposed before camera depth mode restore
- F10 now restores vanilla pixelated resolution correctly
- F10/F11/auto-tune transitions use the game's glitch effect to mask settings switching
- Overlay rewritten: native HUD text with scanlines when in-game, OnGUI fallback in menus. Bottom-left, slide-up animation, smoothed FPS counter
- Added CPU and system info to startup log and benchmark results

## 1.1.2

- Rewrote CPU/GPU bottleneck detection: old method didn't work on D3D11, so every system was incorrectly tagged as GPU-bound. Now runs a two-phase benchmark to measure the actual bottleneck
- Fixed CPU-bound auto-tune trashing shadow quality for no gain: shadow resolution is GPU-only, so the CPU-bound ladder no longer touches it. Only reduces shadow distance and light count, and never below vanilla defaults
- Benchmark now shows a progress bar with percentage instead of raw frame counts
- Fixed DLSS not loading on some NVIDIA systems (driver store folder naming mismatch)
- Fixed DLSS showing as available on non-RTX GPUs (Quadro P4000, GTX series)
- Fixed items dropping when pulled from inventory with auto-hold enabled

## 1.1.1

- Fixed Auto preset stripping flashlight/explosion/particle shadows on high-end hardware; shadow optimizations now only kick in when the benchmark shows the PC actually needs them
- Fixed DLSS not loading on some NVIDIA systems (driver store folder naming mismatch)
- Fixed DLSS showing as available on non-RTX GPUs (Quadro P4000, GTX series)
- Fixed items dropping when pulled from inventory with auto-hold enabled

## 1.1.0

- Added resolution selector: shows resolutions matching your monitor's aspect ratio, from 720p up to native. Render scale works relative to this.
- Added "Auto" preset: runs a benchmark and stores results in `autotune.json`, separate from your settings. Other presets are never touched. Re-benchmarks on mod updates or hardware changes.
- Reworked presets: Potato and Low run at 50% through the game's native scaling (no render pipeline overhead, should beat vanilla FPS). Medium at 75% with SMAA. Upscalers only at High+.
- Fixed iGPU getting broken auto-tune results (upscaler Off + 50% was running through the full custom pipeline for no reason)
- Cut the upscaler path from 3 render textures down to 1. Non-upscaler presets use zero.
- Depth texture only generated when DLSS/FSR needs it; frees up bandwidth on iGPUs
- Fixed DLSS render scale slider locking to 100% after a preset switch
- Textures locked to Full; lowering them doesn't help in R.E.P.O., the textures are tiny
- FSR minimum raised to 50% (below that it falls apart)
- Debug overlay scales with resolution instead of using fixed pixel sizes
- Auto-tune button now sets preset to Auto automatically
- FPS counter skipped when debug overlay is off

## 1.0.0

- DLSS 4 Super Resolution + DLAA support (NVIDIA RTX, DLL bundled)
- FSR Temporal upscaling for any GPU (AMD, Intel, NVIDIA)
- CAS sharpening pass
- Anti-aliasing options: SMAA and FXAA (removed TAA to avoid temporal conflicts)
- Quality presets: Potato, Low, Medium, High, Ultra, Custom
- Auto-benchmark on first launch, targets monitor refresh rate
- CPU vs GPU bottleneck detection: adjusts the right settings for each
- Full graphics settings menu (replaces vanilla Graphics page)
- Shadow quality, shadow distance, LOD bias, texture filtering, texture quality
- Light distance, fog distance, draw distance controls
- Post-processing toggles (motion blur, chromatic aberration, lens distortion, grain)
- Per-layer fog culling for GPU savings
- CPU performance optimizations: NonAlloc physics, cached components, GC reduction
- GPU auto-detection (NVIDIA/AMD/Intel, VRAM, performance tier)
- F10 toggle for vanilla comparison (fully reverts all mod changes)
- Auto-tune can be triggered from the pause menu
- Vanilla display settings (window mode, vsync, fps, gamma) preserved in menu
- Fixed extraction point flicker
- Fixed black screen when switching presets mid-level
- Fixed flashlight shadows not disabling on Potato preset
- Fixed AA not applying correctly on preset switch
- Fixed auto-benchmark running during loading screens and main menu

# 3. Architecture

## 3.1 Layering

Dependencies point downwards only. A module may depend on modules in lower layers and never on modules at the same or higher layer unless listed explicitly. The rule is enforced by an architecture test (see [Testing](05-testing-strategy.md#architecture-tests)).

```
 ┌──────────────────────────────────────────────────────────────────────────┐
 │ L6  Applications      Games, samples, BrutalEngine.Editor (post-v1)      │
 ├──────────────────────────────────────────────────────────────────────────┤
 │ L5  Runtime host      BrutalEngine.Runtime  (app lifecycle, main loop,   │
 │                       module registry, splash/credits, config)           │
 ├──────────────────────────────────────────────────────────────────────────┤
 │ L4  Gameplay features Physics2D · Animation · UI · Particles · Networking│
 │                       Tilemaps · Lighting/PostFx (Rendering2D extensions) │
 ├──────────────────────────────────────────────────────────────────────────┤
 │ L3  Engine services   World (ECS + hierarchy + scenes) · Rendering2D     │
 │                       (sprites, cameras, text) · Audio (mixer) · Input   │
 │                       (actions) · Assets (database, packs, hot reload)   │
 ├──────────────────────────────────────────────────────────────────────────┤
 │ L2  Abstractions      Platform (window, events, devices, clock, files)   │
 │     + backends        Graphics (render interface) · AudioDevice          │
 │                       Backends: Platform.Sdl3 · Graphics.Vulkan ·        │
 │                       Audio.Sdl3                                         │
 ├──────────────────────────────────────────────────────────────────────────┤
 │ L1  Engine core       BrutalEngine.Core (diagnostics, handles, results,  │
 │                       time, logging facade, allocator tracking, ids)     │
 ├──────────────────────────────────────────────────────────────────────────┤
 │ L0  BRUTAL            Core · Interop · Memory · Collections · Strings ·  │
 │                       Numerics · Maths · Concurrency · Logging · bindings│
 └──────────────────────────────────────────────────────────────────────────┘
```

Rules that keep the layering honest:

- **No backend type leaks upward.** Nothing from `Brutal.VulkanApi`, `Brutal.SdlApi` or any other native binding namespace appears in a public signature above L2. L3 and above see only `BrutalEngine.Graphics`, `BrutalEngine.Platform` and `BrutalEngine.Audio` abstractions. That keeps future Metal, D3D12 or SDL_GPU backends possible and keeps games portable.
- **Backends are chosen in the runtime host**, not referenced by gameplay code. A game references `BrutalEngine` (a meta-package) and gets the default backends for its platform.
- **Tools (cooker, editor) sit at L6** and may use any layer, but runtime modules never reference tool assemblies.

## 3.2 Application host and main loop

A game supplies an `IGame` (or derives from `Game`) and calls `BrutalEngine.Run<TGame>(options)`. The host owns `Main`-thread concerns, module start-up order and shutdown.

### Lifecycle

```
Boot ─► Configure (options, CLI args, config file) ─► Platform init ─► Graphics device ─►
Audio device ─► Asset database ─► Modules (in dependency order) ─► Splash (BRUTAL logo) ─►
Game.Initialize ─► [ Frame loop ] ─► Game.Shutdown ─► Modules shutdown (reverse order) ─►
Leak check (debug) ─► Exit code
```

Every step reports failure as a typed error with a user-facing message, for example "No Vulkan 1.3 GPU found", "Vulkan loader missing" or "Audio device unavailable (continuing without audio)". Failures are classified as fatal or degradable. Audio is degradable. Graphics is fatal.

### Frame loop: fixed-step simulation, variable-rate rendering

```
while running:
    platform.PumpEvents()                       # main thread, required by macOS/SDL
    input.BeginFrame(events)                    # snapshot device state → action state
    accumulator += clamp(realDelta, 0, maxFrameDelta)
    while accumulator >= fixedStep:             # default 1/60 s, configurable
        world.RunSchedule(FixedUpdate)          # gameplay, physics, animation, networking tick
        accumulator -= fixedStep
    alpha = accumulator / fixedStep
    world.RunSchedule(Update)                   # per-frame logic (UI, cameras, effects)
    renderer.Extract(world, alpha)              # copy render data into frame packet (interpolated)
    renderer.Prepare()                          # sort, batch, upload (jobs)
    renderer.Submit()                           # record + submit GPU work, present
    assets.ProcessCompletions()                 # async loads, hot reload swaps
```

- **Fixed step** gives deterministic simulation, stable physics and replayable tests. Rendering interpolates between the last two fixed states using `alpha`, so motion is smooth at any refresh rate.
- **Spiral-of-death protection:** `maxFrameDelta` caps catch-up steps. When the cap is hit, a counter is raised and shown on the debug overlay.
- **Extract, prepare, submit** are separate phases from day one. Extract is the only phase that reads the world. That lets rendering move to its own thread later (pipelined frame N+1 simulation with frame N rendering) without changing public APIs.

### Threading model

| Thread | Owns |
|--------|------|
| Main | OS events, window, input sampling, world schedules, extract phase. SDL requires this on macOS |
| Job workers (BRUTAL `JobScheduler`) | Parallel systems within a schedule, render prepare (culling, sorting, batching), asset decoding |
| Audio (SDL audio callback / stream thread) | Mixer. Communicates with the main thread only through lock-free single-producer queues. Never allocates, never locks |
| Asset I/O | Async file reads and decompression, completing on the main thread |
| Render submit (post-v1 option) | Command recording and submission, enabled by the extract/prepare/submit split |

Thread-affinity rules are asserted in debug builds. Calling a main-thread API from a worker throws `WrongThreadException` with the API name.

## 3.3 Memory

- **Steady-state frames allocate zero managed memory.** Engine systems use BRUTAL `NativeList`, spans and pooled unmanaged storage. A test fails if a 1,000-frame steady-state run of the reference scenes allocates any managed bytes (`GC.GetAllocatedBytesForCurrentThread` and the per-thread totals of job workers).
- **Unmanaged memory goes through engine allocators** built on `Brutal.Memory`: arena or frame allocators for per-frame scratch (reset each frame), pool allocators for fixed-size objects, and a general heap. Debug builds wrap every allocator with tracking (call-site, size, frame), so a leak report at shutdown fails tests.
- **GPU memory** goes through VMA. Every GPU object is registered with the device's resource tracker, so leaked buffers, images and pipelines are reported by name at shutdown.
- **Ownership is explicit.** Owning types implement `IDisposable` and are documented as owners. Handles are non-owning value types. Disposal order is the reverse of creation and is centralised in the module registry.

## 3.4 Handles and resource identity

Engine objects exposed to games (entities, textures, sounds, bodies, fonts and so on) are **generational handles**: 32-bit index + 32-bit generation, wrapped in a typed `readonly struct` (e.g. `TextureHandle`). Stale handles are detected, never dereferenced. That eliminates use-after-free across the managed/native boundary, makes handles trivially serialisable, and keeps them valid across hot reload of game code.

## 3.5 Error handling

| Situation | Mechanism |
|-----------|-----------|
| Programmer error (bad argument, wrong thread, stale handle, use after dispose) | Throw (`ArgumentException`, `ObjectDisposedException`, `StaleHandleException` and so on) immediately. Never ignored |
| Expected runtime failure (asset missing, decode error, device not present, network unreachable) | `Result<T, TError>` / `bool TryX(out ...)` with a typed error. Never exceptions in hot paths |
| Native status code | Checked at every call site through a single helper per library (`VkCheck`, `SdlCheck`, `B2Check` and so on) that converts to a typed error with the native message. An analyzer flags unchecked native results |
| Recoverable GPU events | `VK_ERROR_OUT_OF_DATE_KHR` and `VK_SUBOPTIMAL_KHR` rebuild the swapchain. Minimised windows skip rendering. `VK_ERROR_DEVICE_LOST` raises a structured device-lost event: the engine tears down and recreates GPU resources from the asset database (tested by fault injection) |
| Unhandled exception | Caught at the host boundary. The engine writes a crash report (exception, log tail, system info, engine version, recent frame timings) to the user data directory and shows an OS message box, then exits non-zero |

## 3.6 World model (summary, decision pending)

The recommendation, with full trade-offs, is in [ADR-0006](adr/0006-world-model.md): **an archetype ECS core with first-class hierarchy and scene/prefab support**, written in-house on BRUTAL memory primitives, with:

- components as unmanaged structs (no managed references, so handles are used for assets),
- systems registered into named schedules (`FixedUpdate`, `Update`, `PostUpdate` and so on) with explicit ordering and read/write declarations, which lets the scheduler run non-conflicting systems in parallel on the job system,
- structural changes (spawn, despawn, add/remove component) during iteration queued in command buffers and applied at sync points,
- `Parent`/`Children` relationships plus transform propagation as a built-in system,
- scenes and prefabs as serialised entity templates (JSON in development, binary when cooked),
- queries and serialisers emitted by the source generator: no reflection, AOT-safe.

The in-game UI keeps its own retained tree (section 3.15), because UI is naturally hierarchical and benefits from a node model. UI elements can reference entities and vice versa.

Until ADR-0006 is accepted, M1 work below the world layer (platform, graphics, assets) can proceed. World-dependent work cannot.

## 3.7 Platform layer (`BrutalEngine.Platform`, backend `Platform.Sdl3`)

- **Window:** create, resize, fullscreen (exclusive, borderless), multiple monitors, high-DPI (separate logical and pixel sizes, with scale-change events), vsync mode, cursor modes, clipboard, drag-and-drop files.
- **Events:** SDL events translated into an engine event stream (a value-type ring buffer, no allocation), consumed by input and the host.
- **Devices:** keyboard (scancode and keycode), text input with IME composition, mouse (with relative mode), gamepads (SDL gamepad database, hot-plug, rumble), touch and pen as optional capabilities.
- **System:** high-resolution clock, power and suspend events, user and app data paths (`SDL_GetPrefPath`), display enumeration, OS message boxes.
- **File system abstraction:** a virtual file system with mount points (`res://` for packs or raw assets, `user://` for saves and settings). All engine file access goes through it, which makes packs, hot reload and tests (in-memory file system) uniform.
- **Headless mode:** SDL's dummy/offscreen drivers plus an offscreen graphics surface, used by tests, servers and CI.

## 3.8 Graphics: render interface (`BrutalEngine.Graphics`) and Vulkan backend

The render interface is low-level and explicit, at about the level of WebGPU. It has no 2D concepts.

- **Objects:** `GraphicsDevice`, `Swapchain`, `Buffer`, `Texture`, `TextureView`, `Sampler`, `ShaderModule`, `PipelineLayout`, `GraphicsPipeline`, `ComputePipeline`, `BindGroup` / `BindGroupLayout`, `CommandList`, `Fence` / `QueryPool`. All are handles owned by the device.
- **Passes:** render passes are described per frame (attachments, load/store ops). The backend maps them to Vulkan 1.3 **dynamic rendering**. A small **frame graph** orders passes, inserts barriers (`synchronization2`) and handles transient render targets for lighting and post-processing. Explicit barriers never leak into the public API.
- **Frames in flight:** two by default, with per-frame upload rings and deletion queues. A resource is destroyed only after the GPU frame that last used it has completed.
- **Uploads:** a staging ring plus a transfer queue when available, falling back to the graphics queue.
- **Shaders:** cooked SPIR-V plus reflection data produced by the cooker (bindings, push constants, vertex inputs). The runtime never parses shader source.
- **Capabilities:** the device reports features and limits. Optional features (e.g. MSAA levels, BC/ASTC formats) are negotiated, never assumed.
- **Debug:** object names and command labels via `VK_EXT_debug_utils`. Validation layers are on in debug builds and tests. GPU timestamps for each pass are shown on the overlay.
- **macOS:** the instance opts into `VK_KHR_portability_enumeration` and the device enables `VK_KHR_portability_subset` when present, the same way BRUTAL's `samples/Hello/HelloVulkan.cs` does. The backend respects the portability subset's restrictions (checked by tests running on MoltenVK).
- **Device selection:** prefer a discrete GPU, allow override via config or CLI, and log every candidate with the reason it was rejected.

Adding a backend means implementing `BrutalEngine.Graphics` and passing the same conformance test suite (section 5).

## 3.9 2D renderer (`BrutalEngine.Rendering2D`)

- **Cameras:** orthographic 2D cameras with viewport, zoom, rotation, follow and limits. **Pixel-perfect mode** renders to a virtual-resolution target with integer upscale and snapping. HD mode uses a resolution-independent scale with letterbox, crop or expand policies.
- **Sprites:** batched by texture/material, sorted by layer, then order key, then y-sort (optional). Atlases are produced by the cooker. Large scenes are culled per camera in a job. The target is **100,000 sprites at 60 FPS on minimum-spec hardware**, measured by benchmark.
- **Materials:** a small set of built-in materials (unlit, lit, SDF text, custom). Custom materials are Slang shaders with declared parameters.
- **Shapes and debug draw:** lines, rectangles, circles and polygons (anti-aliased), used by debug overlays and tools.
- **Render layers and targets:** per-layer render targets for effects, render-to-texture, screen capture.
- **Text:** see 3.12.
- **Lighting and post-processing (M4):** point, spot and global 2D lights. Normal maps for sprites. Shadow casting from occluder polygons (shadow-map-per-light or ray-marched 1D shadow maps, chosen in M4 after benchmarking both). The post chain (bloom, colour grading LUT, vignette, chromatic aberration, CRT/scanline presets for pixel art) runs on the frame graph.

## 3.10 Input (`BrutalEngine.Input`)

- Raw device state is sampled each frame from platform events.
- **Action mapping:** games define actions (button, axis1D, axis2D) in an input map asset. Bindings support keyboard, mouse, gamepad and composite (WASD → axis2D), with deadzones and processors.
- **Contexts** (gameplay, menu, text entry) are stacked and consume or pass through input.
- **Rebinding at runtime**, with save/load of user bindings to `user://`.
- **Fixed-step correctness:** button presses that happen between fixed ticks are never lost. Press and release edges are latched until consumed by a fixed tick.
- Input recording and replay (deterministic tests, bug repros).

## 3.11 Audio (`BrutalEngine.Audio`, backend `Audio.Sdl3`)

- **Device layer:** an SDL3 audio stream (`SDL_OpenAudioDeviceStream`) at the device's preferred format. The backend handles device changes and loss, reopening the default device on change.
- **Mixer (ours):** runs on the audio thread. 32-bit float, voice pool (fixed capacity, voice stealing by priority), buses (master, music, SFX, UI, voice, plus user buses) with volume, pan, mute and solo, a small DSP set (low-pass and high-pass filters, simple reverb, compressor/limiter on master), sample-accurate scheduling, and 2D spatialisation (pan and attenuation relative to a listener).
- **Sources:** short sounds fully decoded in memory (cooked to PCM or ADPCM). Music and long ambience streamed from compressed data (Ogg Vorbis or Opus, decoder chosen in spike S6).
- **Thread safety:** the game thread sends commands (play, stop, set parameter) through a lock-free queue. The mixer publishes state (voice finished, levels) back the same way. The audio callback must never allocate, lock or block. This is enforced by an audio-thread assertion in debug builds and tested by a stress test.
- **FMOD** is a post-v1 optional backend behind the same `IAudioEngine` interface.

## 3.12 Text (`BrutalEngine.Text`)

- Fonts are loaded with FreeType. The cooker pre-bakes glyph atlases for declared sizes. The runtime can rasterise missing glyphs on demand into dynamic atlases (needed for user-entered text and localisation).
- Two rendering modes: **bitmap** (pixel fonts, exact pixel output) and **SDF** (FreeType's SDF render mode) for scalable HD text, with outlines and shadows.
- Layout: line breaking (UAX #14 subset), alignment, kerning, rich-text tags (colour, size, icon inline), fallback font chains.
- Complex-script shaping (Arabic, Hebrew, Indic) needs HarfBuzz. Spike S8 decides whether `Ahwoo.Brutal.Sdl.Ttf`'s HarfBuzz is usable or whether we bundle HarfBuzz ourselves. Until then, v1 is documented as supporting left-to-right scripts without complex shaping. This is listed as a risk, not hidden.

## 3.13 Assets (`BrutalEngine.Assets`, tool `BrutalEngine.Cook`)

- **Source assets** live in the game's `assets/` folder. Each has a sidecar `.meta` JSON with a stable **asset GUID** and importer settings. References between assets use GUIDs, so renames and moves never break them.
- **Importers** (cook-time, in the cooker): images (PNG, TGA, Aseprite `.ase/.aseprite`), atlases, fonts (TTF/OTF), audio (WAV, Ogg, FLAC → cooked formats), shaders (Slang → SPIR-V + reflection), tilemaps (Tiled `.tmj/.tmx`, LDtk), animations, input maps, localisation tables, scenes and prefabs.
- **Cooker CLI** (`brutal-cook`, a dotnet tool): incremental (content-hash plus importer-version cache), parallel, deterministic (byte-identical output for identical input, tested), produces **versioned binary packs** with a table of contents, per-asset compression and a dependency graph. Errors are reported in a machine-readable format for CI and the future editor.
- **Runtime asset database:** async loading with priorities, reference counting by handle, dependency loading, load groups (for scenes and levels), memory budgets, and unload on zero refs at safe points.
- **Development mode:** loads source assets directly, using the cooker's importers in-process, and watches files. Changed assets are re-imported and **hot-swapped** in place (handles stay valid, dependants are notified). The release build contains no importer code.
- **Format versioning:** every cooked format has a version. The runtime refuses incompatible versions with a clear error. Pack readers are fuzzed.

## 3.14 Tilemaps (`BrutalEngine.Tilemaps`)

Chunked tile layers (renders only visible chunks, rebuilds only dirty chunks), tile sets with per-tile metadata, animated tiles, auto-tiling rules, collision shape generation into Physics2D (merged into optimal polygons), and Tiled/LDtk import through the cooker. Isometric and hexagonal orientations are on the M2 list, but can slip to M3 if needed without blocking M2's exit criteria.

## 3.15 Physics (`BrutalEngine.Physics2D`)

- Wraps Box2D 3.1 (`Ahwoo.Brutal.Box2D`). Box2D world, body and shape ids are hidden behind engine handles.
- Runs in `FixedUpdate`. Body transforms sync to and from ECS transforms (kinematic and dynamic rules). Contact, sensor and trigger events are delivered as ECS events after the step.
- Collision layers and masks, raycasts, shape casts and overlap queries.
- Box2D v3 multithreading is wired to the engine job system through Box2D's task callbacks, instead of spawning its own threads.
- A determinism test (same inputs → same state hash across runs and across all three OSes) runs in CI. If cross-platform determinism fails, that's a documented limitation, not a silent one.

## 3.16 Animation (`BrutalEngine.Animation`)

- **Sprite-sheet animation:** clips (frames, durations, events), imported from Aseprite tags and atlases.
- **Property animation:** curves animating component fields (position, colour, scale) via source-generated accessors.
- **Skeletal 2D:** bones, slots, attachments, mesh deformation and skinning (done on the GPU when supported), IK constraints and blending/layers. The runtime format is our own. Importers for authoring tools are chosen in M3 (open question Q3 in the [Roadmap](09-roadmap.md)). Spine's official runtimes may only be used by holders of a Spine Editor licence, and it is unclear how that applies to an importer or a reimplemented runtime. Spine support therefore needs a licence review before any work starts.
- **State machines:** parameters, transitions, blend trees (1D), and events back to gameplay.

## 3.17 In-game UI (`BrutalEngine.UI`)

- Retained element tree with a **flexbox-subset layout engine**, styles/themes (as assets), and widgets (label, image, button, toggle, slider, text field, scroll view, list, grid, panel, modal).
- **Gamepad and keyboard navigation** with explicit and spatial focus, plus mouse and touch.
- Text via `BrutalEngine.Text`, including IME composition in text fields.
- Data binding to game state through source-generated bindings (no reflection).
- Rendering through Rendering2D on a dedicated UI layer, with resolution scaling policies and safe areas.
- Localisation: every user-visible string goes through `Brutal.Localization` (`LString`). The UI re-lays out on language change, and the tests include pseudo-localisation (elongated strings) to catch clipped layouts.

## 3.18 Particles (`BrutalEngine.Particles`)

CPU particle systems first (emitters, modules for spawn, lifetime, velocity, forces, colour/size over life, collision against Physics2D, sub-emitters), simulated in jobs with SoA storage and rendered as batched sprites. A GPU compute path for very large counts is added in M4 only if the benchmark targets need it, with the same asset format.

## 3.19 Networking (`BrutalEngine.Networking`)

Delivered last (M5) because it touches everything. Layers:

1. **Transport** (reliable/unreliable channels, connection management, encryption, NAT considerations). Spike S7 chooses between `Ahwoo.Brutal.RakNet` (mature, but upstream unmaintained, so security risk) and an in-house UDP transport on `System.Net.Sockets` with a well-specified reliability layer. The choice is recorded as an ADR.
2. **Replication:** server-authoritative entity replication with interest management, delta compression, snapshot interpolation for remote entities, and client-side prediction with reconciliation for the local player, built on the fixed-step loop and input replay.
3. **Testing:** a network simulator (latency, jitter, loss, reordering), headless server builds, fuzzed packet parsers.

## 3.20 Diagnostics

- **Logging facade** over `Brutal.Logging`: structured, category-filtered, zero-allocation formatting for common types, with sinks for console, file (rotated) and the overlay.
- **Debug overlay (ImGui):** frame timings (CPU per schedule and system, GPU per pass), memory (managed, unmanaged by allocator, GPU by heap), entity and asset browsers, log console, input state, audio voices. Compiled out of release builds by default.
- **Profiling markers:** `using (Profile.Scope("name"u8))` with zero allocation, captured in a ring buffer, exportable to Chrome trace format.
- **RenderDoc:** capture hotkey in development builds via `Ahwoo.Brutal.RenderDoc`.
- **Crash reports:** see 3.5.

## 3.21 Hot-reload readiness

C# hot reload isn't in v1, but these rules keep it possible:

- No static mutable state in engine assemblies that holds references to game types. Registries are instance-owned by the host.
- Game types (components, systems) are registered through generated registration code into a registry that can be cleared and rebuilt.
- Component data lives in unmanaged memory keyed by stable type ids (hashed full type names plus a schema version), so data can survive a reload of the game assembly and be migrated when its layout changes.
- Game code is loadable from a separate assembly. In development the host can load it into a collectible `AssemblyLoadContext`. In NativeAOT release builds it is linked statically.

## 3.22 NativeAOT and trimming rules

- All engine assemblies set `IsAotCompatible=true`. Any trim or AOT warning fails the build.
- No `System.Reflection.Emit`, no reflection-based serialisation, and no `Type.GetType(string)` lookups on game types. The source generator does that work at compile time.
- An AOT smoke test publishes every sample per platform in CI and runs it headless (section 6).

## 3.23 BRUTAL licence compliance features

- `Splash` module: shows the official, unaltered BRUTAL logo (bundled from Ahwoo's branding page), for a legible minimum duration, before gameplay starts. Additional game logos can follow it. The splash is configurable but on by default.
- `Credits` API: provides the required BRUTAL copyright line plus third-party notices for a game's credits screen.
- The publish step copies `NOTICE.txt` and every `LICENSES/` file of every native package actually used into the build output, and a test checks the output.

# Design: debug overlay (`BrutalEngine.DebugOverlay`)

## Purpose

`BrutalEngine.DebugOverlay` is M1's development overlay: Dear ImGui driven by our own input backend and drawn by our own renderer on the render interface, with a frame-timing panel and a log panel (`docs/09-roadmap.md:61`; [Architecture 3.20](../03-architecture.md#320-diagnostics)). It is compiled out of release builds by default (`docs/03-architecture.md:234`): the Runtime host installs it only in development builds ([runtime note section 9](runtime.md#9-the-debug-overlay)).

This note is the API sketch for the overlay's code issues (section 9). Signatures are the contract; XML docs, `PublicAPI.Unshipped.txt` entries, argument validation and tests follow the usual rules ([Coding standards](../07-coding-standards.md#public-api-design)). It references, and doesn't redefine, the [platform note](platform.md) (events, windows, clipboard, cursors), the [render interface note](graphics.md) (frames, transient memory, pipelines, device loss), the [Vulkan backend note](graphics-vulkan.md) (clip-space convention), the [world note](world.md) (system timings), the [logging note](logging.md) (`MemoryLogSink`) and the [runtime note](runtime.md) (the frame loop and the hook).

Decisions this note depends on:

- [ADR-0013](../adr/0013-imgui-without-brutal-backends.md): only `Ahwoo.Brutal.ImGui`; our input backend and our renderer are engine types, not implementations of BRUTAL's `IPlatformBackend`/`IRendererBackend`. It leaves one choice to this note, the input source, settled in section 2.
- [ADR-0011](../adr/0011-brutal-objectpool-dependency.md): DebugOverlay may carry the `Microsoft.Extensions.ObjectPool` closure that `Ahwoo.Brutal.Strings` brings.
- [ADR-0012](../adr/0012-m1-shader-cooking.md): the overlay's shader is a committed `.slang` source with its cooked SPIR-V, embedded in the assembly, with its layout declared in C#.
- Decision (maintainer, 2026-11-27): a docs-only design note first; the overlay's implementation issues are drafted from it after it merges.

Choices this note makes are marked "Decision (this note)" with their reason; merging the note approves them.

**Sources.** BRUTAL citations are paths in the BRUTAL repository. `Ahwoo.Brutal.ImGui` 2026.10.3 was built from BRUTAL commit `4ac91e105e816361938b4250cbcedae239749ce2` (its nuspec's `repository` element), and `Brutal.ImGui/` is unchanged from that commit to the tree these lines were read from (`git diff --stat 4ac91e1 HEAD -- Brutal.ImGui` is empty), so every cited line is what the package ships. Facts marked **observed** were read from the published `Ahwoo.Brutal.ImGui.Native` 2026.10.3 package with `readelf` on Linux on 2026-11-27. Dear ImGui's own C++ sources aren't in the BRUTAL tree; where the note relies on their behaviour it says so and names the test that proves it.

## Layering

`BrutalEngine.DebugOverlay` is an L4 host (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:40`): it may read L3 services and L2 diagnostics and may reference backends, but it isn't a backend, so its public surface may not expose ImGui types (`docs/design/architecture-tests.md:58`). It references `BrutalEngine.Core`, `BrutalEngine.Platform`, `BrutalEngine.Graphics`, `BrutalEngine.World` (for `SystemTiming`) and `Ahwoo.Brutal.ImGui`, and **no backend**: the input comes from Platform's abstraction (section 2) and the drawing from Graphics. It can't reference Runtime (L5), so no public signature here uses `FrameTiming`, `RenderTarget`, `FrameContext` or `EngineContext`; Runtime adapts them (section 6).

## 1. Public surface

```csharp
namespace BrutalEngine.DebugOverlay;

public sealed class Overlay : IDisposable
{
    public static Result<Overlay> Create(OverlayOptions options);
    public bool IsVisible { get; }
    public void SetVisible(bool visible);                  // takes effect at the next BeginFrame
    public Scancode? ToggleKey { get; }                    // options.ToggleKey; any thread (immutable)
    public OverlayCapture Capture { get; }                 // after the last BeginFrame; None while hidden
    public void BeginFrame(in OverlayInput input);         // section 2: feed this frame's batch, start an ImGui frame
    public void Layout(in OverlayStats stats);             // section 5: build the panels, finish the ImGui frame
    public Result<OverlayRecordStats> Record(CommandList commands, in OverlayTarget target);   // section 4
    public void Dispose();
}

public sealed record OverlayOptions
{
    public required GraphicsDevice Device { get; init; }  // not owned; its owner thread must be the platform's (section 1)
    public required IPlatform Platform { get; init; }     // not owned: clipboard and cursors
    public required IWindow Window { get; init; }         // not owned: the window whose events, size and scale it uses
    public required Logger Logger { get; init; }          // category "DebugOverlay"
    public MemoryLogSink? LogTail { get; init; }          // the log panel's source; null: the panel says so
    public Scancode? ToggleKey { get; init; } = Scancode.Grave;   // null: no key; SetVisible only
    public bool StartVisible { get; init; }               // false: shown with the toggle key
    public int FrameHistory { get; init; } = 240;         // frame deltas kept for the graph, 16..4096
}

public readonly ref struct OverlayInput
{
    public required PlatformEventBatch Events { get; init; }   // IPlatform.Events of this frame
    public required TimeSpan Delta { get; init; }              // the frame's simulated delta (Runtime: FrameTiming.Delta)
}

public readonly record struct OverlayCapture(bool Keyboard, bool Mouse, bool TextInput)
{
    public static OverlayCapture None { get; }                  // all false
}

public readonly ref struct OverlayStats
{
    public int FixedSteps { get; init; }                        // FixedUpdate runs this frame
    public long SpiralCount { get; init; }                      // frames whose delta hit MaxFrameDelta
    public ReadOnlySpan<OverlayScheduleTimings> Schedules { get; init; }
    public bool GpuTimingAvailable { get; init; }               // the device enabled TimestampQueries
    public long GpuFrameNumber { get; init; }                   // the frame GpuPasses belong to; 0: none yet
    public ReadOnlySpan<PassTiming> GpuPasses { get; init; }
}

public readonly record struct OverlayScheduleTimings(string Name, int Runs, ReadOnlyMemory<SystemTiming> Systems);

public readonly record struct OverlayTarget(TextureViewHandle View, int Width, int Height, TextureFormat Format); // size in pixels

public readonly record struct OverlayRecordStats(
    int DrawCalls, int Vertices, int Indices, int TexturesCreated, int TexturesUpdated, int TexturesDestroyed,
    int PipelinesCreated)
{
    public bool CreatedResources => TexturesCreated + PipelinesCreated > 0;
}
```

- **Why `Overlay` and not `DebugOverlay`.** A type named `DebugOverlay` in namespace `BrutalEngine.DebugOverlay` is shadowed by the namespace in every file under `BrutalEngine.*`: name lookup from `BrutalEngine.Runtime` reaches the namespace `BrutalEngine.DebugOverlay` before any `using`, giving CS0118, the clash that named `EcsWorld` and `GameHost` ([runtime note section 1](runtime.md#1-entry-points)).
- **Only engine and BCL types in signatures.** `GraphicsDevice`, `CommandList`, `TextureViewHandle`, `TextureFormat` and `PassTiming` are Graphics types ([graphics note](graphics.md)); `IPlatform`, `IWindow`, `PlatformEventBatch` and `Scancode` are Platform's; `SystemTiming` is World's (`docs/design/world.md:371`); `Logger` and `MemoryLogSink` are Core's. No `Brutal.ImGuiApi` type appears (the binding-surface rule, [architecture tests](architecture-tests.md#rules)).
- **Three calls per frame, in this order:** `BeginFrame` after the event pump, `Layout` after the frame's schedules ran (so their timings are current), `Record` inside the open graphics frame, after every other renderer (so it draws on top). `OverlayStats` and `OverlayInput` are `ref struct`s over the caller's spans, so a frame passes them without allocating.
- **Creation** returns a failed `Result` for expected failures: the native ImGui library is missing or doesn't load (`DllNotFoundException`, `EntryPointNotFoundException` or `BadImageFormatException` from the first native call, caught and mapped to `ErrorKind.Unavailable`, "the ImGui native library couldn't be loaded"); the native library doesn't match the binding's data layout (`ImGui.DebugCheckVersionAndDataLayout`, `Brutal.ImGui/Generated/Functions/ImGui.gen.cs:11255`, returns false: `Unsupported`); a GPU object can't be created (the device's `Result`, unchanged: `OutOfMemory`, `Unavailable` when lost, `InvalidData` when the driver rejects the shader). Misuse throws: null options or a null required member (`ArgumentNullException`), `FrameHistory` outside its range (`ArgumentOutOfRangeException`), the wrong thread (`WrongThreadException`), a disposed device (`ObjectDisposedException`). A failed `Create` disposes everything it created (context, GPU objects, owner registration, native buffers) before returning.
- **Ownership.** The overlay owns its ImGui context, its GPU objects and its native buffers, and disposes them in `Dispose`. It doesn't own the device, platform, window, logger or sink, which must outlive it: the host disposes the overlay before the device and the window. A second `Dispose` does nothing; every other member then throws `ObjectDisposedException`, except `ToggleKey`.
- **Threading.** The overlay has one owner thread, the thread that calls `Create`. It must be the device's owner thread (`docs/design/graphics.md:782`) and also the platform's main thread, which every `IWindow` member and `IPlatform`'s clipboard and cursor members require (`src/BrutalEngine.Platform/IWindow.cs:29-30`; [platform note section 9](platform.md#9-threading)); in the Runtime host both are the main thread. `Create` checks both before it creates anything, by reading `GraphicsDevice.Statistics` (`src/BrutalEngine.Graphics/GraphicsDevice.cs:207`) and `IWindow.State` (`src/BrutalEngine.Platform/IWindow.cs:101`), each of which throws `WrongThreadException` (naming its own member) off its owner thread; so a device and a window owned by different threads always fail `Create`. The overlay then captures Core's `ThreadAffinity`: every member except `ToggleKey` checks it and throws `WrongThreadException` naming the API.
- **Allocation.** `BeginFrame`, `Layout`, `Record`, `Capture`, `IsVisible` and `SetVisible` allocate no managed memory in steady state (section 5), unless another overlay holds the ImGui lock: entering a contended `Lock` allocates on the waiting thread (section 3). `Create` (the log panel's buffers included, section 5), `Dispose`, a frame that creates a texture or a pipeline (reported by `OverlayRecordStats.CreatedResources`), clipboard use and logging a failure allocate.

## 2. Input backend

### Platform's events, not Platform.Sdl3

Decision (this note), the choice ADR-0013 left open: the overlay reads **Platform's backend-neutral events** (`PlatformEventBatch`), the window's snapshots (`IWindow`) and `IPlatform`'s clipboard and cursor members, and never references `BrutalEngine.Platform.Sdl3`. Reasons:

- **One reader of SDL's queue.** Platform.Sdl3 dequeues events with `SDL_PeepEvents(SDL_GETEVENT)` (`docs/design/platform.md:282`), so a second reader of SDL's queue would take events the host never sees, or see none. The host's batch is the frame's only copy, readable by every consumer until the next pump (`docs/design/platform.md:286`).
- **Nothing to call.** Platform.Sdl3's public surface is a factory and its options (`docs/design/platform.md:582`); reading it "directly" would mean calling SDL's bindings from the overlay, duplicating the backend's translation and its status checks.
- **Headless tests.** Synthetic events from `PlatformEvent`'s factories (`docs/design/platform.md:205-214`) and the test platform of the runtime note (R1a's `FakePlatform` and fake windows, `docs/design/runtime.md:523-528`) drive every input rule without SDL or a display.
- **Any backend.** A later platform backend gets the overlay for free.

### Coordinates and scale

Decision (this note): ImGui lays out in **content units**, the platform note's UI space ("scale once, from pixels", `docs/design/platform.md:125`). At every `BeginFrame` the overlay reads the window's snapshots (`IWindow.PixelSize`, `PixelDensity`, `DisplayScale`; `src/BrutalEngine.Platform/IWindow.cs:58`, `:83`, `:95`) and sets:

| ImGui IO field | Value |
|----------------|-------|
| `DisplaySize` (`Brutal.ImGui/Generated/Structs/ImGuiIO.gen.cs:67`) | `PixelSize / DisplayScale` per side, unrounded: the window in content units |
| `DisplayFramebufferScale` (`:81`) | `(DisplayScale, DisplayScale)`, so `DisplaySize × DisplayFramebufferScale` is `PixelSize` and ImGui's clip rectangles project to pixels (section 4); ImGui's own docs say it also sets font density (`:71-72`) |
| `DeltaTime` (`:94`) | `OverlayInput.Delta` in seconds as a `float`, at least 0.0001: the host's first frame after a rebase can have a zero delta |
| mouse positions | window coordinates (`docs/design/platform.md:243`) × `PixelDensity` / `DisplayScale` |

`ImGuiStyle.FontSizeBase` (`Brutal.ImGui/Generated/Structs/ImGuiStyle.gen.cs:45`) stays 13 content units with `FontScaleDpi` (`:84`) at 1, because content units already carry the display scale. At 200 % on Windows (3840x2160 pixels, `DisplayScale` 2) ImGui lays out 1920x1080 units and draws 26-pixel text; on macOS (best-effort, unverified, [ADR-0015](../adr/0015-tested-platforms-windows-linux.md)) the same numbers come out of points and pixel density 2. A window with a zero pixel side (minimised) gets its events fed but no ImGui frame: `Capture` is `None` and `Layout` and `Record` do nothing that frame.

### Events

`BeginFrame` feeds the batch's events in order to the context's IO. Events whose `Window` is neither the overlay's window nor default are skipped.

| `PlatformEventKind` | ImGui call |
|---------------------|------------|
| `MouseMotion` | `AddMousePosEvent(x, y)` in content units (`Brutal.ImGui/Generated/Functions/ImGuiIO.gen.cs:80`) |
| `MouseButtonDown`, `MouseButtonUp` | `AddMousePosEvent` at the event's position, then `AddMouseButtonEvent(n, down)` (`:100`) with `Left` 0, `Right` 1, `Middle` 2, `X1` 3, `X2` 4 (ImGui's numbering); `None` is skipped |
| `MouseWheel` | `AddMouseWheelEvent(-X, Y)` (`:124`): Platform's `+X` scrolls right (`docs/design/platform.md:221`), ImGui's `wheel_x > 0` scrolls left (`Brutal.ImGui/Generated/Functions/ImGuiIO.gen.cs:105-106`); `+Y` is "away from the user" on both sides |
| `WindowMouseLeft` | `AddMousePosEvent(-float.MaxValue, -float.MaxValue)`, ImGui's "no mouse" position |
| `WindowFocusGained`, `WindowFocusLost` | `AddFocusEvent(true/false)` (`:189`) |
| `KeyDown`, `KeyUp` | modifiers first, then the key (below) |
| `TextInput` | each Unicode scalar of `PlatformEventBatch.GetText` (`src/BrutalEngine.Platform/PlatformEventBatch.cs:57`) decoded with `Rune.DecodeFromUtf8` and passed to `AddInputCharacter` (`Brutal.ImGui/Generated/Functions/ImGuiIO.gen.cs:215`). Not `AddInputCharactersUTF8` (`:259`): it reads a NUL-terminated string, and the batch's text isn't terminated |
| every other kind | ignored (window and display changes are read from the snapshots instead) |

**Keys.** Each `KeyDown`/`KeyUp` first sends ImGui's four modifier keys from `KeyEventData.Modifiers` (`AddKeyEvent(ImGuiKey.Mod_Ctrl, ...)`, `Mod_Shift`, `Mod_Alt`, `Mod_Super`; `Brutal.ImGui/Generated/Enums/ImGuiKey.gen.cs:1912`, `:1938`, `:1964`, `:1990`), then `AddKeyEvent(key, down)` (`Brutal.ImGui/Generated/Functions/ImGuiIO.gen.cs:27`). Decision (this note): the key is chosen **by keycode when it is a character ImGui names** (`a`-`z`, `0`-`9` and the eleven punctuation keys `'` `,` `-` `.` `/` `;` `=` `[` `\` `]` and `` ` ``), otherwise **by scancode** (navigation, editing, function, lock, modifier and keypad keys: every keyboard member of `ImGuiKey` from `Tab` to `Oem102`, `Brutal.ImGui/Generated/Enums/ImGuiKey.gen.cs:70-1192`). So ImGui's shortcuts (Ctrl+C) follow the key's label on any layout, while keys that have no character map by position. A key with neither mapping is skipped. Repeats (`IsRepeat`) are passed on; ImGui ignores an unchanged state and repeats keys itself. The mapping is one internal table, tested row by row against both enums.

### Capture: what the game's input may read

After feeding the batch, `BeginFrame` calls `ImGui.NewFrame` (`Brutal.ImGui/Generated/Functions/ImGui.gen.cs:169`), then sets `Capture` from ImGui's IO: `Keyboard` = `WantCaptureKeyboard`, `Mouse` = `WantCaptureMouse`, `TextInput` = `WantTextInput` (`Brutal.ImGui/Generated/Structs/ImGuiIO.gen.cs:1272`, `:1257`, `:1286`). The contract for the game's input, which the M2 input design implements:

- **Timing.** `Capture` describes the batch just passed to `BeginFrame`, and stays valid until the next one. The host calls the overlay's `BeginFrame` before any other module's (section 6), so an input module reads it for the same batch.
- **Meaning** (ImGui's own rule for these flags, `Brutal.ImGui/Generated/Structs/ImGuiIO.gen.cs:1246-1248` for the mouse, `:1261-1263` for the keyboard): while `Mouse` is set the game ignores mouse presses, wheel and motion of that batch; while `Keyboard` is set it ignores key presses and text. Releases are never ignored, so a press that began in the game ends in the game. The overlay never filters the batch itself: it is read-only and shared.
- **The toggle key** is the overlay's: its events are never fed to ImGui, and the game's input ignores `Overlay.ToggleKey`'s events while an overlay is installed.
- **No public path in M1.** M1 has no game input service, so R5 keeps the capture inside Runtime's module. The M2 input design adds the read path through a Runtime- or Input-owned type, because no DebugOverlay type may be reachable from code that survives a release publish (section 6).

M1's panels have no text fields and keyboard navigation is off (`ConfigFlags` stays `None`, `Brutal.ImGui/Generated/Enums/ImGuiConfigFlags.gen.cs:34`), so `Keyboard` is set only while a widget holds focus (the log panel's level menu). **Text input:** when `TextInput` becomes true and `IWindow.IsTextInputActive` is false, the overlay calls `StartTextInput` and remembers it did; when it becomes false (or the overlay hides or is disposed) it calls `StopTextInput` only if it started it, so it never stops text input the game started (`src/BrutalEngine.Platform/IWindow.cs:125`, `:201`, `:208`). A failed `Result` is logged once at `Warning`.

### Visibility and dropped batches

- **Hidden** costs nothing: `BeginFrame` only scans the batch for the toggle key's `KeyDown` (not a repeat, on the overlay's window) and touches no ImGui state; `Capture` is `None`; `Layout` and `Record` return at once. The toggle and `SetVisible` take effect at the next `BeginFrame` (the toggle mid-batch: the events after it are fed).
- **Becoming visible** clears ImGui's input state first (`ClearInputKeys`, `ClearInputMouse`; `Brutal.ImGui/Generated/Functions/ImGuiIO.gen.cs:358`, `:372`), sends the window's current focus (`WindowState.InputFocus`) and the "no mouse" position, then feeds the rest of the batch. **Hiding** stops text input the overlay started and restores the cursor (below).
- **Batches the host didn't pass** (frames that skip module hooks, such as the runtime note's minimised wait, `docs/design/runtime.md:310`, or the splash phase) can hold the release of a key ImGui saw pressed. The overlay keeps the focus it last told ImGui; whenever `IWindow.State`'s `InputFocus` differs from it at `BeginFrame`, it sends the focus and, on gaining focus, clears ImGui's keys and mouse before the batch. Restoring or refocusing a window therefore never leaves a key held. The host's side of the contract is in section 6.
- **A skipped `Layout`.** If `BeginFrame` finds the previous ImGui frame still open (a host that called `BeginFrame` but not `Layout`), it ends it with `ImGui.EndFrame` (`Brutal.ImGui/Generated/Functions/ImGui.gen.cs:194`) and discards it before starting the next, so ImGui's frame calls stay balanced. A `Record` without a `Layout` since the last `BeginFrame` draws nothing.

### Clipboard and cursors

- **Clipboard.** The overlay sets `ImGuiPlatformIO.Platform_GetClipboardTextFn` and `Platform_SetClipboardTextFn` (`Brutal.ImGui/Generated/Structs/ImGuiPlatformIO.gen.cs:42`, `:58`; signatures `byte* (ImGuiContext*)` and `void (ImGuiContext*, byte*)`, `Brutal.ImGui/Manual/StructExtensions/ImGuiPlatformIO.cs:8`, `:11`) to two static `[UnmanagedCallersOnly]` methods taken with `&`, which NativeAOT supports, rather than delegates through `Marshal.GetFunctionPointerForDelegate` as BRUTAL's GLFW backend does (`Brutal.ImGui.Backends/PlatformBackends/GlfwBackend.cs:94-95`). The overlay is found through `Platform_ClipboardUserData` (`Brutal.ImGui/Generated/Structs/ImGuiPlatformIO.gen.cs:74`), a `GCHandle` it allocates at `Create` and frees at `Dispose`, so no static field refers to it. The getter calls `IPlatform.GetClipboardText` (`src/BrutalEngine.Platform/IPlatform.cs:211`), encodes it as NUL-terminated UTF-8 into a native buffer the overlay owns (from an `INativeAllocator`, grown as needed, valid until the next call, as ImGui requires), and returns it; a failed `Result` returns an empty string. The setter decodes the NUL-terminated text and calls `SetClipboardText` (`:220`). An exception must not cross an `[UnmanagedCallersOnly]` boundary (the runtime ends the process), so both catch everything and log it once at `Warning`. Clipboard use allocates: it is user-driven, not a steady frame.
- **Cursors.** `BackendFlags` gets `HasMouseCursors` (`Brutal.ImGui/Generated/Enums/ImGuiBackendFlags.gen.cs:68`). After `Render`, `Layout` reads `ImGui.GetMouseCursor()` (`Brutal.ImGui/Generated/Functions/ImGui.gen.cs:11008`) and, **only while `Capture.Mouse` is set**, applies it when it changed: `None` (`Brutal.ImGui/Generated/Enums/ImGuiMouseCursor.gen.cs:35`) becomes `SetCursorVisible(false)`; the others map one to one onto `SystemCursor` (`docs/design/platform.md:174`): `Arrow` to `Default`, `TextInput` to `Text`, `ResizeAll` to `Move`, `ResizeNS` to `ResizeNs`, `ResizeEW` to `ResizeEw`, `ResizeNESW` to `ResizeNesw`, `ResizeNWSE` to `ResizeNwse`, `Hand` to `Pointer`, `Wait`, `Progress` and `NotAllowed` to the same names. When the overlay stops capturing the mouse, hides or is disposed, and it had changed the cursor, it sets `Default` and visible once. A game that sets its own cursor sets it again after that; M2's input design may add an owner for the cursor. Failed `Result`s are logged once.
- **Not done in M1:** IME composition (`Platform_SetImeDataFn`, `Brutal.ImGui/Generated/Structs/ImGuiPlatformIO.gen.cs:123`, stays null; the platform delivers committed text only, `docs/design/platform.md:169`), gamepad navigation, `HasSetMousePos` (Platform has no mouse warp), docking and multi-viewports (`DockingEnable` and `ViewportsEnable` stay off, `Brutal.ImGui/Generated/Enums/ImGuiConfigFlags.gen.cs:140`, `:159`, although the native library is ImGui's docking branch, `Brutal.ImGui/Generated/Functions/ImGui.gen.cs:12`).

## 3. ImGui context and global state

**The current context is process-wide.** ImGui keeps it in the native global `GImGui`, and every ImGui call works on it. In `libimgui.so` from `Ahwoo.Brutal.ImGui.Native` 2026.10.3, `GImGui` is an 8-byte `GLOBAL OBJECT` in `.bss`, not a thread-local symbol (**observed**, `readelf -sW`), so one thread switching context changes it for every thread. Its allocator functions are process globals too (`GImAllocatorAllocFunc`, `GImAllocatorFreeFunc`, observed). The Windows DLL wasn't inspected; it is built from the same sources, and the rule below doesn't depend on it.

Decision (this note): **any number of overlays per process, each with its own context, serialised by one process-wide lock.** Every overlay member that calls ImGui, the clipboard callbacks and the device-loss callbacks do so in an internal scope that:

1. enters `ImGuiSerialisation.Lock`, a `static readonly System.Threading.Lock`;
2. saves `ImGui.GetCurrentContext()` and calls `ImGui.SetCurrentContext(own)` (`Brutal.ImGui/Generated/Functions/ImGui.gen.cs:72`, `:91`);
3. on exit, restores the saved context, then leaves the lock.

| Option not chosen | Why not |
|-------------------|---------|
| One overlay per process, counted in a `static int` as `SdlPlatform` does | The headless host runs real Runtime frames with the fake graphics backend in parallel tests (`docs/design/runtime.md:537-540`), with the overlay on by default (`EngineOptions.DebugOverlay`, `docs/design/runtime.md:64`); a one-per-process rule would fail them or force them into one collection |
| `SetCurrentContext` around every call without a lock | `GImGui` isn't thread-local (observed above), so two overlays on two test threads would draw into each other's context |

- **The static-state rule holds.** The lock is `static readonly` (allowed, `docs/design/architecture-tests.md:76`) and holds no game type, so hot-reload (3.21) isn't affected: it guards a native global, not managed state. Everything else, including the context pointer, the texture table and every buffer, is instance state.
- **Cost.** An uncontended `Lock.Enter` allocates nothing; a contended one allocates on the waiting thread. Only parallel overlays contend, which happens only in tests; tests that measure allocation run in a non-parallel collection (section 8).
- **Re-entrancy.** `Lock` is re-entrant, so the clipboard callbacks, called by ImGui inside a widget call under the lock on the same thread, can take the scope again without deadlocking. No overlay call waits for anything while holding it.
- **Context set-up at `Create`:** `ImGui.CreateContext()` (`Brutal.ImGui/Generated/Functions/ImGui.gen.cs:26`) with its own font atlas; `IniFilename` and `LogFilename` set to null (`Brutal.ImGui/Generated/Structs/ImGuiIO.gen.cs:122`, `:135`), because the default writes `imgui.ini` into the process's working directory (`:111-113`), outside the VFS and the user data directory; `ConfigFlags` `None`; `BackendFlags` `HasMouseCursors | RendererHasVtxOffset | RendererHasTextures` (`Brutal.ImGui/Generated/Enums/ImGuiBackendFlags.gen.cs:68`, `:104`, `:124`); the dark style (`StyleColorsDark`, `Brutal.ImGui/Generated/Functions/ImGui.gen.cs:627`); `io.Fonts.AddFontDefault()` (`Brutal.ImGui/Generated/Functions/ImFontAtlas.gen.cs:79`), ImGui's built-in font, so no font file ships; `TexDesiredFormat = RGBA32` (`Brutal.ImGui/Generated/Structs/ImFontAtlas.gen.cs:61`). `Dispose` calls `ImGui.DestroyContext(own)` (`Brutal.ImGui/Generated/Functions/ImGui.gen.cs:49`) inside the scope.
- **ImGui's assertions.** The linux-x64 library imports no `__assert_fail` (observed). That suggests `IM_ASSERT` is compiled out, but it is inferred, not observed: a custom `IM_ASSERT` handler would leave no such import either. Either way the overlay doesn't rely on ImGui catching its misuse natively. The overlay's own frame state (section 2, "A skipped `Layout`") keeps its calls balanced, and tests run every path.
- **`ImString`'s shared arena is never used.** BRUTAL's `ImString` builds strings converted from `string`, `char` spans and interpolation (`$"..."`) in `ImString.SharedStorage` (`Brutal.ImGui/ImString/ImString.cs:16`, `:30-33`, `:43`; `Brutal.ImGui/ImString/ImString.Interpolation.cs:26`): one static 8 MiB arena, not thread-safe, which never grows and throws `ArgumentOutOfRangeException` when full (`Brutal.ImGui/ImString/ArenaStringBuilder.cs:10`, `:15`), and which the application must clear once per frame (`samples/Hello/HelloImGui.cs:93`). Decision (this note): the overlay passes ImGui only UTF-8 literals (`"Log"u8`) and slices of its own NUL-terminated buffers, through the constructor that wraps a span without copying (`Brutal.ImGui/ImString/ImString.cs:20-23`); never a `string`, a `char` span or an interpolated `ImString`. So it doesn't depend on, or race, whoever clears the arena, and a test proves the rule (section 8).

## 4. Renderer

### Textures (`RendererHasTextures`)

ImGui 1.92 asks the renderer to create, update and destroy textures (the font atlas, which grows as new glyph sizes are baked) through `ImDrawData.Textures` (`Brutal.ImGui/Generated/Structs/ImDrawData.gen.cs:231`). At the start of `Record`, the renderer walks that list and handles each `ImTextureData` by its `Status` (`Brutal.ImGui/Generated/Structs/ImTextureData.gen.cs:74`; values `Brutal.ImGui/Generated/Enums/ImTextureStatus.gen.cs:33`, `:50`, `:67`, `:85`, `:102`):

| `Status` | The renderer |
|----------|--------------|
| `WantCreate` | Checks `Format` is `RGBA32` (`Brutal.ImGui/Generated/Structs/ImTextureData.gen.cs:147`; `Brutal.ImGui/Generated/Enums/ImTextureFormat.gen.cs:38`), since the overlay asks for it, and fails `Record` with `Unsupported` otherwise. Creates a `Rgba8Unorm` texture of `Width` x `Height` (`Brutal.ImGui/Generated/Structs/ImTextureData.gen.cs:171`, `:195`) with `Sampled \| CopyDestination` usage, uploads every texel with `WriteTexture` from `GetPixelsAt(0, 0)` with `BytesPerRow = GetPitch()` (`Brutal.ImGui/Generated/Functions/ImTextureData.gen.cs:69`, `:99`), creates a persistent bind group (the texture's default view and the shared sampler), stores the table index plus one as `TexID` (`SetTexID`, `:146`) and sets `OK` (`SetStatus`, `:164`) |
| `WantUpdates` | One `WriteTexture` of `UpdateRect` (`Brutal.ImGui/Generated/Structs/ImTextureData.gen.cs:291`) from `GetPixelsAt(x, y)` with the same pitch, then `OK`. A single bounding rectangle, as BRUTAL's Vulkan backend does (`Brutal.ImGui.Backends/RendererBackends/VulkanBackend.cs:478-483`), rather than each entry of `Updates`: one ring copy per texture per frame |
| `WantDestroy` | Destroys the texture and its bind group at once, sets `TexID` to `Invalid` and `Destroyed`. The render interface already defers the native destruction until the frames that used it completed (`docs/design/graphics.md:66`), so the renderer doesn't wait `UnusedFrames` frames as BRUTAL's Vulkan backend must (`Brutal.ImGui.Backends/RendererBackends/VulkanBackend.cs:652`) |
| `OK`, `Destroyed` | nothing |

- **Write visibility.** Writes made inside the open frame are copied at the start of that frame, before any of its passes (`docs/design/graphics.md:456`), so a texture created or updated in `Record` is complete for the same frame's draws.
- **Texture ids.** `TexID` is an index into the overlay's managed table of `(TextureHandle, BindGroupHandle)` entries, never a GPU handle, so a stale id can't reach the device: a draw command whose `ImDrawCmd.GetTexID()` (`Brutal.ImGui/Generated/Functions/ImDrawCmd.gen.cs:25`) isn't a live entry is a bug in the overlay and throws `InvalidOperationException`.
- **Linear `Rgba8Unorm`.** The atlas's colour channels are white and its alpha is coverage, which must stay linear, so the texture is never an sRGB format.
- **Failures.** A failed `CreateTexture`, `WriteTexture` or `CreateBindGroup` leaves that texture's `Status` unchanged, so it is retried next frame, makes `Record` return the error (draws that need it are skipped), and is logged once per error kind at `Warning`. A lost device returns `Unavailable` from every create and write (`docs/design/graphics.md:749`), which takes the same path until recovery.

### Draw data

`Layout` ends with `ImGui.Render()` (`Brutal.ImGui/Generated/Functions/ImGui.gen.cs:218`); `Record` reads `ImGui.GetDrawData()` (`:241`):

1. **Geometry.** One `AllocateTransient` for `TotalVtxCount × 20` bytes with `TransientUsage.Vertex` and one for `TotalIdxCount × 2` bytes, rounded up to 4, with `Index` (`docs/design/graphics.md:439`; counts at `Brutal.ImGui/Generated/Structs/ImDrawData.gen.cs:105`, `:85`). Each draw list's `VtxBuffer` and `IdxBuffer` (`Brutal.ImGui/Generated/Structs/ImDrawList.gen.cs:80`, `:62`) are copied in list order. `ImDrawVert` is 20 bytes: `pos` at 0, `uv` at 8, `col` at 16 (`Brutal.ImGui/Generated/Structs/ImDrawVert.gen.cs:25`, `:41`, `:56`, `:71`); indices are 16-bit (`IdxBuffer` is `ImVector<ushort>`, `Brutal.ImGui/Generated/Structs/ImDrawList.gen.cs:62`). An empty frame (`TotalVtxCount` 0) records nothing.
2. **The pass.** `BeginRenderPass` with one colour attachment, `OverlayTarget.View` with `LoadOp.Load` and `StoreOp.Store` (the overlay draws over the frame), labelled with the device's `DebugLabel` "DebugOverlay", so it gets a GPU timing like any pass (`docs/design/graphics.md:632`). The viewport is the whole target.
3. **Per command** (`Brutal.ImGui/Generated/Structs/ImDrawCmd.gen.cs:49`, `:95`, `:117`, `:140`, `:163`): the clip rectangle is projected as BRUTAL's Vulkan backend does, `(ClipRect - DisplayPos) × FramebufferScale` (`Brutal.ImGui.Backends/RendererBackends/VulkanBackend.cs:218-230`), clamped to `[0, Width] x [0, Height]` of the target; an empty rectangle skips the command; otherwise `SetScissor` (`docs/design/graphics.md:648`) with the rectangle's floor (minimum) and ceiling (maximum) as integers, `SetBindGroup(0, ...)` for the command's texture when it changed, and `DrawIndexed(ElemCount, 1, IdxOffset + list's first index, VtxOffset + list's first vertex)` (`docs/design/graphics.md:651`, `:670`). `RendererHasVtxOffset` lets one list exceed 65,536 vertices with 16-bit indices. A non-zero `UserCallback` throws `InvalidOperationException`: no overlay panel adds one.
4. **Stats.** `Record` returns the draw, vertex, index, texture and pipeline counts. `CreatedResources` tells the host the frame did load-time work (section 6).

**Projection.** The render interface's clip space is **y-up** (`docs/design/graphics-vulkan.md:307`), while ImGui's coordinates grow downwards from `DisplayPos`. So the vertex shader computes `clip = pos × Scale + Translate` with `Scale = (2 / DisplaySize.X, -2 / DisplaySize.Y)` and `Translate = (-1 - DisplayPos.X × Scale.X, 1 - DisplayPos.Y × Scale.Y)`. BRUTAL's Vulkan backend uses `(2 / w, 2 / h)` and `-1 - pos × scale` (`Brutal.ImGui.Backends/RendererBackends/VulkanBackend.cs:313-319`) because raw Vulkan is y-down; copying it would draw the overlay upside down here.

### Pipelines and the shader

- **One shader**, `src/BrutalEngine.DebugOverlay/Shaders/overlay.slang`, cooked by `tools/BrutalEngine.Cook` into committed SPIR-V embedded in the assembly ([ADR-0012](../adr/0012-m1-shader-cooking.md)): vertex entry `vertexMain`, fragment entry `fragmentMain`; vertex inputs `float2 position` (location 0), `float2 uv` (1), `float4 colour` (2, `VertexFormat.Unorm8x4`, `docs/design/graphics.md:555`, which reads ImGui's packed `col` as R, G, B, A in byte order); output `colour × texture(uv)`. It takes no matrix, so S4's matrix-layout watch item doesn't apply.
- **Layout, declared in C#:** bind group 0 is `SampledTexture` at binding 0 and `Sampler` at binding 1, both fragment-visible; one 24-byte vertex-stage push-constant range: `Scale` (2 floats), `Translate` (2 floats), `DecodeSrgb` (uint), 4 reserved bytes. One sampler: linear minification and magnification, `ClampToEdge`.
- **Blend** as ImGui's reference backends: colour `SourceAlpha` / `OneMinusSourceAlpha`, alpha `One` / `OneMinusSourceAlpha` (`Brutal.ImGui.Backends/RendererBackends/VulkanBackend.cs:987-991`; `docs/design/graphics.md:545`); no depth; cull `None`; `TriangleList`.
- **One pipeline per target format**, created the first time `Record` sees the format, in a table indexed by `TextureFormat` (no allocation to look up), and counted in `PipelinesCreated`. A swapchain that is `Recreated` with a new format therefore just reaches a new entry. A format whose `GetFormatSupport` lacks `ColorTarget` or `Blendable` makes `Record` return `Unsupported` without recording.
- **sRGB.** Decision (this note): ImGui's style colours are sRGB-encoded values. On an `*Srgb` target the hardware encodes what the shader writes, so writing those values unchanged would brighten every colour; the vertex shader therefore decodes the vertex colour's RGB to linear with the exact sRGB transfer function when `DecodeSrgb` is 1, which `Record` sets for `Rgba8UnormSrgb` and `Bgra8UnormSrgb`, and passes it through on UNORM targets. Displayed flat colours then equal ImGui's on both kinds of swapchain; anti-aliased edges and translucent windows blend in linear light on sRGB targets, a small, accepted difference from ImGui's gamma-space reference backends. A view of the sRGB image as UNORM isn't an option: the render interface has no format-reinterpreting views. ImGui's `IsSRGB` config flag (`Brutal.ImGui/Generated/Enums/ImGuiConfigFlags.gen.cs:177`) stays unset: nothing here relies on what core does with it.

### Device loss

The overlay is one of the owners the graphics note names for M1 (`docs/design/graphics.md:752`). It registers at `Create` (`RegisterOwner`, `:726`) and disposes the registration in `Dispose`:

- **`OnDeviceLost`** (`docs/design/graphics.md:744`; no device calls): drops every handle (pipelines, layouts, sampler, shader module, the texture table) and, inside the ImGui scope, sets every texture whose `TexID` it issued to `TexID = Invalid` and `Status = Destroyed`, the state BRUTAL's Vulkan backend leaves when it frees device objects mid-run (`Brutal.ImGui.Backends/RendererBackends/VulkanBackend.cs:656-671`, `:769-771`).
- **`OnDeviceRecreated`** (`docs/design/graphics.md:745`): recreates the shader module, layouts and sampler, and returns the first failure, which `Recover` reports as fatal ([graphics note section 12](graphics.md#12-device-lost)). Pipelines are recreated lazily by the next `Record`.
- **Re-upload.** Dear ImGui's core turns a still-used `Destroyed` texture back into `WantCreate` at the next `NewFrame`, keeping its pixels; this is Dear ImGui 1.92's core behaviour for a backend that freed resources mid-run, cited from Dear ImGui's sources (not in the BRUTAL tree) and not observed. O2b's device-loss test proves it (section 8). If it doesn't hold, the renderer sets `WantCreate` itself in `OnDeviceRecreated`: an internal change, no API impact.

## 5. Panels and the zero-allocation rule

`Layout` builds two ImGui windows, positioned on first use only, then calls `Render` and updates the cursor (section 2).

**Frame timings.**

- The frame: the last delta in ms and fps, min, average and max over the last `FrameHistory` deltas (a ring of `float`s the overlay owns), a `PlotLines` graph of them (`Brutal.ImGui/Generated/Functions/ImGui.gen.cs:6923`), this frame's `FixedSteps`, and `SpiralCount` (`docs/design/runtime.md:298`).
- CPU per schedule and system: one section per `OverlayScheduleTimings`, with its name, its `Runs` this frame and, from its `Systems` (each system's `Elapsed` in its last run, in execution order, `docs/design/world.md:359`), a table of system names and milliseconds plus their sum. For `FixedUpdate`, which runs `Runs` times a frame, the table is labelled as the last step's.
- GPU per pass: when `GpuTimingAvailable` is false, the line "GPU timing unavailable" (`docs/design/graphics.md:714`); when it is true but `GpuFrameNumber` is 0, "GPU timings pending"; otherwise a table of each pass's label name (`GraphicsDevice.GetDebugLabelName`, no allocation, `docs/design/graphics.md:705`) and milliseconds, headed with the frame they belong to (`TryGetPassTimings` gives a frame about `FramesInFlight` frames old, `docs/design/graphics.md:713`).

**Log.**

- Source: `OverlayOptions.LogTail`, the host's `MemoryLogSink` (`docs/design/runtime.md:494`); without one, the window says "No log tail". While the window is open and not collapsed, each `Layout` copies the tail with `CopyTo` (`src/BrutalEngine.Core/Logging/Sinks/MemoryLogSink.cs:164`) into a `MemoryLogRecord[CapacityEntries]` and a `byte[CapacityBytes]` (`:65`, `:69`), which always receive everything ([logging note](logging.md#memorylogsink)). `Create` allocates them, and the filter's index buffer below, when `LogTail` is set, sized from the sink's capacities, so no frame allocates them. A closed or collapsed window copies nothing.
- A minimum-level menu (`Trace` to `Critical`, default `Information`) filters into an `int[CapacityEntries]` of the shown records' indices; the lines are drawn through ImGui's list clipper (`Brutal.ImGui/Generated/Functions/ImGuiListClipper.gen.cs`), so only visible lines cost anything. Each line is `HH:mm:ss.fff LEVEL [Category] message`, UTC as in the log file, coloured by level; a message is shown up to its first line break, followed by ` …` when it has more, so every line has the clipper's fixed height (the whole message is in the log file). The view follows new lines while scrolled to the bottom. `DroppedCount` (`src/BrutalEngine.Core/Logging/Sinks/MemoryLogSink.cs:73`) is shown as "N dropped".
- ImGui's built-in font covers ASCII; other characters draw as ImGui's fallback glyph.

**Zero allocation in steady frames.** The managed rule of [Architecture 3.3](../03-architecture.md#33-memory) applies to `BeginFrame`, `Layout` and `Record` (native memory inside ImGui isn't managed allocation):

- **Text** goes through one internal UTF-8 writer over a buffer the overlay owns: numbers with `IUtf8SpanFormattable.TryFormat` and `CultureInfo.InvariantCulture` (`double` with `F2`, `long`, `int`), timestamps with `DateTimeOffset`'s UTF-8 `TryFormat`, category names with `Encoding.UTF8.GetBytes(ReadOnlySpan<char>, Span<byte>)`, labels as UTF-8 literals; each piece ends with a NUL byte. The writer is reset at the start of every `Layout`.
- **Every text widget is `ImGui.PInvoke.TextUnformatted(begin, end)`** with explicit end pointers (`Brutal.ImGui/Generated/PInvoke/TextUnformatted.gen.cs:31`): no format string, and no NUL needed for message slices taken straight from the copied log buffer. Not `ImGui.TextColored`, whose text is a printf format (`Brutal.ImGui/Generated/PInvoke/TextColored.gen.cs:29`), so a log message containing `%s` would read stray arguments; colours use `PushStyleColor`/`PopStyleColor` (`Brutal.ImGui/Generated/Functions/ImGui.gen.cs:2074`, `:2096`) around it.
- **Panel models are pure.** The numbers and lines each panel shows are computed by internal model types (`TimingPanelModel`, `LogPanelModel`) from `OverlayStats` and the copied records, separately from the ImGui calls that draw them, so their content is unit-tested without reading pixels.
- **Proof:** allocation tests run 1,000 `BeginFrame`/`Layout`/`Record` frames with both panels open, a full log tail and every level, measured with `GC.GetAllocatedBytesForCurrentThread`, and assert 0 bytes (section 8).

## 6. Runtime hook (R5)

R5 implements, inside Runtime, the hook the runtime note sketched (`docs/design/runtime.md:554-556`):

- **The switch and the branch.** The host creates the module only inside `if (RuntimeFeatures.IsDevelopmentBuild && options.DebugOverlay)`, and only when there is a window and a device (never under `HeadlessMode.NoSurface`). The trimmer removes the branch, and so every DebugOverlay type, from builds with the switch off (`docs/design/runtime.md:555`).
- **`DebugOverlayModule`**, internal, an `EngineModule` named `DebugOverlay` with no dependencies and `OnStartFailure = Degradable`: an overlay that can't start (a missing native library, an unsupported format) logs a `Warning` and the game runs without it. The host adds it before calling `IGame.ConfigureModules`, so it starts first and its `BeginFrame` runs before every other module's; the host calls its `Record` after every other module's (`docs/design/runtime.md:264`).
- **`Start`** creates the overlay from the context: `Device = Graphics`, `Platform`, `Window`, `Logger`, `LogTail = context.LogTail`; it allocates the timing buffers below. **`Stop`** disposes it, before the host disposes the device and the window.
- **Per frame, only in `HostPhase.Running`** (never over the splash, BRUTAL LICENSE 2.2, `docs/design/runtime.md:556`):
  - `BeginFrame(frame)`: `overlay.BeginFrame(new OverlayInput { Events = Platform.Events, Delta = frame.Timing.Delta })`, then it keeps `overlay.Capture` for M2's input path (section 2).
  - `Prepare(frame)`: fills `OverlayStats`: `FixedSteps = frame.Timing.Steps`, `SpiralCount = FrameClock.SpiralCount`, one `OverlayScheduleTimings` each for `FixedUpdate` (with `Runs = Steps`), `Update` and `PostUpdate` (`Runs = 1`) from `Schedule.GetTimings` into `SystemTiming[]` buffers sized from `SystemCount` (grown, allocating, only on the non-steady frame after a system was added, `docs/design/runtime.md:352`), and the GPU passes from `TryGetPassTimings(Graphics.CompletedFrameNumber, ...)` into a 64-entry `PassTiming[]` allocated at `Start` (`GpuTimingAvailable` from `Capabilities.Features`); when that returns false it passes the last frame that returned true. Then `overlay.Layout(stats)`.
  - **More than 64 passes.** The graphics note (`docs/design/graphics.md:706-715`) doesn't say what `TryGetPassTimings` does when a frame has more timings than `destination` holds. The overlay's rule assumes the usual span contract, the first `destination.Length` timings written and `true`: R5 passes the `written` entries, and when `written` is 64 it logs once at `Information` that passes after the 64th aren't shown (the method reports no total, so they can't be counted). R5's test scripts 65 labelled passes on the fake backend and fails if the method returns `false` instead, so a different graphics rule is caught rather than freezing the panel on an old frame.
  - `Record(frame, commands, target)`: when `target.Status` is `Acquired` or `Recreated`, `overlay.Record(commands, new OverlayTarget(image.View, image.Width, image.Height, image.Format))`. If the stats say `CreatedResources`, it calls `MarkFrameNonSteady()`. That is the overlay's only load-time work inside a frame: the log buffers are allocated by `Create` (in `Start`), and the remaining allocating paths, clipboard use and logging a failure, are user-driven or error paths that a steady-state measurement doesn't run. A failed `Result` is logged once per error kind at `Warning`; the run continues.
- **Batches the module doesn't see.** The module sees every batch of a frame that reaches the modules' `BeginFrame` in `Running`. Batches pumped during the splash or a minimised wait (`docs/design/runtime.md:310`) aren't passed; the overlay's focus resync (section 2) is what makes that safe, so R5 adds no loop change.
- **Release check.** R5's NativeAOT check (`docs/design/runtime.md:580`): a sample published with `Configuration=Release` (the switch off) and `IlcGenerateMapFile=true`; a CI step fails if the map names any type in namespace `BrutalEngine.DebugOverlay`. A second publish with the switch forced on must name them, so a check that can't see the types can't pass.

## 7. Packaging and natives

- **Package:** `Ahwoo.Brutal.ImGui` **2026.10.3**, the newest of the two versions on nuget.org (2026.10.2, 2026.10.3; flat-container index, 2026-11-27), added to `Directory.Packages.props` by the first overlay issue. It isn't in the local NuGet cache yet; its nuspec was read from nuget.org on 2026-11-27. Its only target group is `net10.0` (`lib/net10.0/Brutal.ImGui.dll`), which `net11.0` consumes.
- **Its dependencies** (nuspec, all with `exclude="Build,Analyzers"`): `Ahwoo.BRUTAL.ImGui.Native` 2026.10.3, `CommunityToolkit.HighPerformance` 8.4.2, `Ahwoo.BRUTAL.Core` 2026.10.4, `Ahwoo.BRUTAL.Localization` 2026.10.3, `Ahwoo.BRUTAL.Memory` 2026.10.2, `Ahwoo.BRUTAL.Numerics` 2026.10.2 and `Ahwoo.BRUTAL.Strings` 2026.10.2. `Localization` brings `Ahwoo.BRUTAL.Logging` 2026.10.3, and `Strings` brings `Microsoft.Extensions.ObjectPool`, pinned to 11.0.0 by [ADR-0011](../adr/0011-brutal-objectpool-dependency.md) (`Directory.Packages.props:29-30`), which allows the closure in DebugOverlay. No GLFW or OpenGL package is in it ([ADR-0013](../adr/0013-imgui-without-brutal-backends.md)). BRUTAL's packages ship Roslyn analyzers that have reached consumers despite that `exclude` (seen with the first Graphics.Vulkan reference), so the first issue builds the whole solution after adding the reference.
- **Natives** (`Ahwoo.BRUTAL.ImGui.Native` 2026.10.3, which has no dependencies): `runtimes/win-x64/native/imgui.dll` (1,151,488 bytes) and `runtimes/linux-x64/native/libimgui.so` (1,561,600 bytes), the tested targets; `runtimes/osx-arm64/native/libimgui.dylib` (1,269,936 bytes), best-effort and unverified ([ADR-0015](../adr/0015-tested-platforms-windows-linux.md)); and `build/natives/lib/win-x64/imgui.lib`, a static import library that isn't copied to output. The binding loads them as `imgui` (`[LibraryImport("imgui")]`, e.g. `Brutal.ImGui/Generated/PInvoke/TextUnformatted.gen.cs:30`). The Linux library needs `libstdc++.so.6`, `libm.so.6`, `libgcc_s.so.1` and `libc.so.6` (observed, `readelf -d`).
- **Licences.** The native package's `LICENSES/BRUTAL-IMGUI-THIRD-PARTY-NOTICES.txt` lists Dear ImGui (MIT) and stb (MIT or public domain). It reaches a game's `LICENSES/` folder with BRUTAL's other notices: the samples' target in M1, the M2 publish step after ([runtime note, licence duties](runtime.md#licence-duties)).
- **Release output.** The trimmer removes the overlay's managed code from release builds (section 6), but NuGet still copies `imgui.dll` or `libimgui.so` next to every game. Leaving the native out when the development switch is off belongs to the M2 publish step (`docs/design/runtime.md:555`), as an item filter on the native package's runtime assets with a test that a release publish contains no `imgui` file. Until then a shipped game carries an unused 1.2 to 1.6 MB library.
- **CI tier.** DebugOverlay is a native-backed module: the first issue adds `DebugOverlay` to the full-tier source pattern (`.github/scripts/ci-tier.sh:14`) and a case to `ci-tier-test.sh` (docs/06, CI tiers).

## 8. Testing

`tests/BrutalEngine.DebugOverlay.Tests` (xUnit v3, created with the project). ImGui runs on the CPU, so everything except the GPU cases runs in `build-test` on Windows and Linux, against the real native library: no display, no Vulkan.

| Area | Tests |
|------|-------|
| Context (O1a) | `Create`/`Dispose`, a second `Dispose`, use after dispose, options validation (each null member), the wrong thread for every member, a device and a window owned by different threads failing `Create` with nothing created; the previous current context restored after every member (a sentinel context made current before the call); two overlays on two threads released together by a `Barrier`, each driving 1,000 raw ImGui frames (`NewFrame`, `Begin`, `End`, `Render`; `Brutal.ImGui/Generated/Functions/ImGui.gen.cs:169`, `:749`, `:782`, `:218`) through the internal context scope, each context ending with only its own windows (O1a has no `BeginFrame` or `Layout` yet); `IniFilename` null and no `imgui.ini` written; a native-library failure through an internal loader seam returning `Unavailable` and leaving no context behind |
| Input (O1b, O1c) | Table-driven over synthetic events: each kind of section 2's table reaches the IO as stated (ImGui's `io` state read back after `NewFrame`: mouse position in content units at `DisplayScale` 1, 1.5 and 2 with `PixelDensity` 1 and 2, buttons by number, the wheel's X sign, the leave position, focus, each text scalar for 1- to 4-byte UTF-8); every key row of the mapping (keycode characters, and scancodes for the rest), modifiers sent before the key, an unmapped key skipped, another window's events skipped; `Capture` follows `WantCapture*` after `NewFrame` and is `None` while hidden or minimised; text input started and stopped only by the overlay, never stopping the game's; the display size and scale fields; a zero delta clamped |
| Visibility and resync (O1d) | The toggle key mid-batch (events after it fed, the key itself never), repeats ignored, `SetVisible` at the next frame; showing clears keys and mouse; a key held when a batch is dropped (focus lost, then regained with no `KeyUp`) reads released; a `BeginFrame` without `Layout` keeps ImGui's frame calls balanced over 100 frames |
| Clipboard and cursors (O1d) | Copy and paste round-trips through `FakePlatform`'s clipboard, including non-ASCII text and a failing platform (empty string, one warning); a throwing platform doesn't cross the native boundary; each `ImGuiMouseCursor` maps to its `SystemCursor`, applied only while capturing the mouse and only on change; `None` hides; release restores `Default` once |
| Pipelines and device loss (O2a) | With the G2b fake backend: one pipeline per format and reuse, an unsupported format returning `Unsupported`, the layout and push-constant range as declared; a shader or layout creation failing at `Create` (scripted in the fake backend) leaves no context, GPU object or owner registration behind; a loss scripted in the fake backend drops every handle without a device call, `Recover` recreates them, a recreation failure is returned; `Dispose` leaves the device leak-free (`ThrowIfLeaks`) |
| Textures (O2b) | The first frame creates the atlas (`Rgba8Unorm`, the right size, every texel written, `OK`); a new font size baked mid-run sends `WantUpdates` as one write of `UpdateRect`; atlas growth destroys the old texture; a failed create keeps `WantCreate`, returns the error, logs once and succeeds next frame; after a loss scripted in the fake backend and `Recover`, the next frame recreates and re-uploads the atlas (the core behaviour section 4 cites); a stale `TexID` throws |
| Draw recording (O2c) | From the fake backend's recorded plan: transient sizes and copied bytes for two and more draw lists; index and vertex offsets per command; scissor projection with rows at `FramebufferScale` 1 and 2, a rectangle partly outside the target (clamped), fully outside and zero-sized (skipped), fractional edges (floor and ceiling); the projection's push constants (y-up) and `DecodeSrgb` 1 on both `*Srgb` formats and 0 on both UNORM ones; `LoadOp.Load`; a failed `AllocateTransient` returns its error and records no draw; a lost frame's no-op encoders; stats counts; an empty frame records nothing |
| Panels (O3a, O3b) | O3a: `FrameHistory` 15 and 4097 rejected, 16 and 4096 accepted; two overlays driven from two threads released together by a `Barrier` for 1,000 `BeginFrame`/`Layout`/`Record` frames each, each seeing only its own windows. The UTF-8 writer: number formats under a culture with a changed `NegativeSign` and separators, NUL after each piece, overflow refused; `TimingPanelModel`: min, average and max over a partly filled and a wrapped ring, the "last step" label, the three GPU states, label names; `LogPanelModel`: each minimum level against records of every level, the first-line cut with ` …` (a line break right at the end and in a multi-byte character's neighbourhood), `DroppedCount`, no copy while closed |
| Allocation (O3b; a non-parallel collection) | 1,000 frames of `BeginFrame`, `Layout` and `Record` on the fake backend with both panels open, a full tail of every level and three schedules allocate 0 bytes after warm-up, also under Windows dynamic coverage as CI runs it; a hidden overlay's frames allocate 0 bytes |
| `ImString` arena (O3b) | 200 frames with a full log tail, without anyone clearing `ImString.SharedStorage`, never throw: the panels would fill the 8 MiB arena in under 100 frames if any text went through it |
| GPU (O2d; `Gpu` trait: lavapipe in `vulkan-smoke`, and the RTX 2070 SUPER locally) | Conformance-style ([graphics note section 15](graphics.md#15-the-conformance-contract)): validation on and clean, `ThrowIfLeaks`, readback through a `Readback` buffer, compared with golden images produced on lavapipe by the conformance suite's image diff. For `Bgra8UnormSrgb`, `Bgra8Unorm`, `Rgba8UnormSrgb` and `Rgba8Unorm`: a deterministic frame of both panels (fixed stats, a fixed log tail with a fixed clock) over a cleared target; a flat-colour panel background read back equal to ImGui's style colour on all four formats (the sRGB rule): exactly on UNORM targets, within 1 per channel on sRGB targets (the splash's precision policy and its reason, `docs/design/runtime.md:402`); scissor clipping at a target smaller than the window. Device loss isn't forced on the GPU here: the front end's fault seam is internal to Graphics and its conformance suite (`docs/design/graphics.md:754`), so the overlay's recovery is proven on the fake backend (O2b) and the real device's by G12 |
| NativeAOT (O2d) | The AOT smoke harness gains an `overlay-offscreen` scenario: one overlay frame recorded into an offscreen target, read back and checked against the same golden image, so the `LibraryImport` bindings, the `[UnmanagedCallersOnly]` callbacks and the embedded SPIR-V are proven under NativeAOT on both CI OSes |
| Runtime (R5) | With the headless host and the fake backend: the module starts first and records last, is absent for `NoSurface` and when the switch or `DebugOverlay` is off, does nothing during the splash, degrades on a failed start (exit 0, a warning, a `Degraded` status), marks a frame non-steady when the overlay created resources, and passes `FixedUpdate`'s `Runs`; the release map-file check of section 6 |

Mutation self-checks, for example: drop the minus sign of `-X` for the wheel; map letters by scancode; leave the focus resync out; set `Capture` before `NewFrame`; take the scissor from `DisplaySize` instead of the projected clip rectangle; reuse BRUTAL's y-down projection; skip the sRGB decode; write the whole texture on `WantUpdates`; restore no context on exit.

## 9. Issue split

Replaces the plan rows O1-O3 and the runtime note's R5 row (`docs/design/runtime.md:607`). **Units**, measured at `860394b`: a public options record is 25 lines plus 5 per property, with 9 PublicAPI lines plus 2 per property (`src/BrutalEngine.Graphics.Vulkan/VulkanBackendOptions.cs`, 45 lines for 4 properties; `src/BrutalEngine.Graphics.Vulkan/PublicAPI.Unshipped.txt:14-34`); a positional record struct is about 35 lines with 7 PublicAPI lines plus 3 per parameter (`src/BrutalEngine.Graphics/SwapchainImage.cs`, 33 lines and 22 PublicAPI lines for 5 parameters); a public `ref struct` view is 15 lines plus 11 per member (`src/BrutalEngine.Graphics/Backend/FramePlan.cs`, 59 lines for 4 members); a class member with contract docs is 14 lines; an event translation is 10.6 lines per kind with tests 2.1 times the source (`src/BrutalEngine.Platform.Sdl3/SdlEventTranslator.cs`, 328 lines for 31 kinds; its tests, 697 lines); a mapping table row is 1 line, with 1 test row; internal rules with docs are about 20 lines each, with tests 1.1 to 1.6 times the source. "Other" is project files, CI and shader source.

| Issue | Scope | Blocked by | Src + docs | API | Tests | Other | Total |
|-------|-------|------------|-----------:|----:|------:|------:|------:|
| O1a feat(debug-overlay): create the overlay and its ImGui context | Creates `src/BrutalEngine.DebugOverlay` and its tests; the package version; `Overlay.Create`/`Dispose`; `OverlayOptions` with `Device`, `Platform`, `Window`, `Logger`; the serialised context scope; context set-up (section 3); the thread checks; the native-library failure | this note; P1; G2b; R1a (fake platform and windows) | class 60 + scope 50 + set-up 60 + validation 20 + options 25 + 4 × 5 = 235 | 3 + 9 + 4 × 2 = 20 | lifecycle, options, threads, scope and set-up 245 + thread-pair rows 10 = 255 | csproj 2 × 35 + tier pattern and test case 10 = 80 | ~590 |
| O1b feat(debug-overlay): feed mouse, focus and text to ImGui | `OverlayInput`; `BeginFrame` and the frame state machine; coordinates and scale; the mouse, wheel, leave, focus and text rows of section 2 | O1a; R1a (fake platform and windows) | struct 15 + 2 × 11 + BeginFrame 14 + 7 kinds × 10.6 + frame state and scale 50 + focus 15 = 191 | 1 + 2 × 2 + 1 = 6 | 75 × 2.1 + frame state 60 + scale 40 = 258 |  | ~455 |
| O1c feat(debug-overlay): feed keys to ImGui and report capture | The key table (about 120 rows), modifiers, the two key kinds; `OverlayCapture`, `Capture`; text input start and stop | O1b | table 120 + 20 + modifiers 15 + 2 kinds × 10.6 + record 35 + Capture 14 + text input 25 = 250 | 7 + 3 × 3 + 1 + 1 = 18 | 120 rows + modifiers 50 + capture 60 + text input 40 = 270 |  | ~538 |
| O1d feat(debug-overlay): toggle the overlay and wire the clipboard and cursors | `IsVisible`, `SetVisible`, `ToggleKey`, the `ToggleKey` and `StartVisible` options; showing, hiding and the focus resync; the clipboard callbacks and buffer; cursor mapping | O1c | 3 members × 14 + options 2 × 5 + resync 30 + clipboard 90 + cursor table 12 + 33 = 217 | 3 + 2 × 2 = 7 | visibility 100 + clipboard 90 + cursors 70 = 260 |  | ~484 |
| O2a feat(debug-overlay): create the overlay's shader, pipelines and device-loss owner | `overlay.slang` and its cooked SPIR-V; shader module, layouts, sampler; the per-format pipeline table; `IGpuResourceOwner` registration and recreation of these objects | O1a; G2 (pipelines, bind groups); T1 (the cook tool) | load 20 + objects 50 + table 60 + owner 45 = 175 | 0 | 250 | `overlay.slang` 40 | ~465 |
| O2b feat(debug-overlay): honour ImGui's texture requests | The texture table and section 4's status table; atlas loss and re-upload | O2a, O1b | table 50 + create 40 + update 30 + destroy 25 + format 10 + loss 50 + registration 20 = 225 | 0 | 300 |  | ~525 |
| O2c feat(debug-overlay): record ImGui draw data through the render interface | `OverlayTarget`, `OverlayRecordStats`, `Record`; geometry, scissor, draws, projection, sRGB flag | O2b; G10 (the fake backend's recorded plan) | target 35 + stats 45 + Record 14 + geometry 40 + scissor 25 + draws 30 + projection 20 + checks 15 = 224 | 7 + 3 × 4 + 7 + 3 × 7 + 1 + 1 = 49 | 300 |  | ~573 |
| O3a feat(debug-overlay): show frame timings | `OverlayStats` (`FixedSteps`, `SpiralCount`, `Schedules`), `OverlayScheduleTimings`, `Layout`, the `FrameHistory` option and its range check; the UTF-8 writer; `TimingPanelModel` and its window (frame and CPU parts) | O1b; W6 (`SystemTiming`) | struct 15 + 3 × 11 + record 35 + Layout 14 + option 5 + range check 5 + graph 40 + schedules 50 + spiral 15 + writer 60 + render and cursor hook 20 = 292 | 1 + 3 × 2 + 7 + 3 × 3 + 1 + 2 = 26 | writer 80 + model 150 + allocation 50 + state 40 + `FrameHistory` rows 15 + two-thread frames 45 = 380 |  | ~698 |
| O3b feat(debug-overlay): show GPU pass times and the log | The three GPU members and table; the `LogTail` option; `LogPanelModel`, copy buffers, level menu, clipper; the allocation and arena tests | O3a | 3 × 11 + GPU 40 + option 5 + copy 20 + menu 30 + filter 25 + lines 60 + dropped 10 + scroll 15 = 238 | 3 × 2 + 2 = 8 | GPU 70 + log 150 + allocation 50 + arena 30 = 300 |  | ~546 |
| O2d test(debug-overlay): prove the overlay on the GPU and under NativeAOT | The GPU golden cases on lavapipe and the RTX 2070 SUPER; golden images (binary PNGs, not counted); the `overlay-offscreen` AOT smoke scenario | O2c, O3b; G9f; C1 | 0 | 0 | harness 60 + formats 80 + scissor 40 = 180 | smoke scenario 120 + wiring 30 = 150 | ~330 |
| R5 feat(runtime): install the debug overlay in development builds | `DebugOverlayModule`, its registration, phases, buffers and non-steady marking (section 6); `EngineOptions.DebugOverlay`; the release map-file check | R1e, R1f (`MarkFrameNonSteady`); O2c, O3b | module 150 | 2 | 200 + pass-limit row 10 = 210 | CI check 50 | ~412 |

- **Totals:** about 5,600 lines over eleven issues, against about 2,200 in the plan rows they replace; the plan rows counted source alone, and recent notes' rows came in 1.4 to 2.6 times low for that reason.
- **Order.** The O issues share one project and its `PublicAPI.Unshipped.txt`, so they run one at a time: O1a, O1b, O1c, O1d, O2a, O2b, O2c, O3a, O3b, then O2d; R5 follows in the Runtime lane once O2c and O3b have merged. O3a can run before O2a if the Graphics blockers are late: it needs only O1b and W6.
- **Blockers outside this note** are the runtime note's R1a, R1e and R1f, the graphics note's G2, G2b, G10 and C1 (the conformance suite), the Vulkan backend note's G9f, the world note's W6, the platform note's P1, and T1, the issue that creates `tools/BrutalEngine.Cook` (as in the runtime and Vulkan backend notes).

## Failure modes

| Failure | Result |
|---------|--------|
| ImGui's native library missing or unloadable | `Create`: `Unavailable`; Runtime: the module is `Degraded`, the game runs |
| Native library and binding layouts differ | `Create`: `Unsupported` |
| A GPU object can't be created at `Create` | `Create` returns the device's error after undoing what it created |
| A texture or pipeline can't be created or written in a frame | `Record` returns the error, logged once per kind; retried next frame |
| Device lost | Creates and writes return `Unavailable`; encoders are no-ops; `OnDeviceLost` drops handles; recreation after `Recover` |
| Recreating the overlay's objects fails | `OnDeviceRecreated` returns it; `Recover` fails (fatal, graphics note section 12) |
| Unsupported target format | `Record`: `Unsupported`, nothing recorded |
| Clipboard or cursor call refused, or the clipboard throws | logged once at `Warning`; an empty paste |
| Text input start or stop refused | logged once at `Warning` |
| Wrong thread; use after `Dispose`; invalid options | `WrongThreadException`; `ObjectDisposedException`; `ArgumentException` |
| A draw command with a user callback or an unknown texture id | `InvalidOperationException` (an overlay bug) |

## Consistency with the ADRs, the layer map and the runtime note

| Source | This note |
|--------|-----------|
| ADR-0013: only `Ahwoo.Brutal.ImGui`; our own backends; renderer only through Graphics; the input source left open | Section 7's package only; section 2 decides Platform's events; section 4 draws only through `GraphicsDevice` and `CommandList` |
| ADR-0011: the ObjectPool closure is allowed in DebugOverlay | The closure arrives through `Strings` (section 7); Core, Platform, Graphics and World gain nothing |
| ADR-0012: Slang, cooked offline, committed and embedded; layouts in C# | Section 4's shader and layout |
| Layer map: L4 host, no binding types in public signatures | No backend reference at all, and no ImGui type in section 1 |
| Runtime note section 9: registered first, recorded last, never over the splash, skipped for `NoSurface`, development builds only; "the input type is O1's and O3's to define" | Section 6. The note defines the inputs as `OverlayInput` and `OverlayStats` per frame; the log tail, the same `MemoryLogSink` every frame, is passed once in `OverlayOptions.LogTail` so its copy buffers are sized from its capacities and allocated at `Create`, never in a frame |
| Runtime note section 9: the module passes "the pumped events, `FrameTiming`, `SpiralCount`, per-system timings, per-pass GPU timings" | `OverlayInput` (events, delta) and `OverlayStats` (steps, spiral, schedules, passes); `FrameTiming` itself can't cross the layer, so R5 copies its fields |
| Graphics note section 12: the overlay's font atlas and pipelines are M1 owners | Section 4, "Device loss" |
| Architecture 3.20: the overlay also lists memory, entity and asset browsers, input state and audio voices | Not in M1: the roadmap's M1 line is frame-timing and log panels (`docs/09-roadmap.md:61`) |

## Not verified

- The note is design only; nothing in it has run. The **observed** facts (the linux-x64 library's symbols and dependencies, the package contents) were read from the published packages, not exercised.
- Whether `GImGui` is thread-local in the Windows `imgui.dll` wasn't checked; the lock makes the design independent of it.
- Dear ImGui's re-request of a backend-destroyed texture (section 4, "Device loss") and the 1.92 font rasterisation density following `DisplayFramebufferScale` (section 2) are cited from Dear ImGui's documentation and BRUTAL's ports, not observed: O2b's loss test proves the first; the second needs the manual DPI checks the platform note lists (`docs/design/platform.md:620`), extended to the overlay. No open issue extends that checklist to the overlay, so the check belongs to the end-of-M1 hardware session on the reference machine (Windows at 150 % and 200 %, as #97 checks the platform's size model); #390, which closes out M1, records its result in this section.
- Scale other than 1, multi-monitor DPI changes and IME aren't testable in CI; the content-unit rules (section 2) rest on the platform note's, which are themselves verified by P3.
- macOS (`libimgui.dylib`) is best-effort and unverified, with no CI ([ADR-0015](../adr/0015-tested-platforms-windows-linux.md)).

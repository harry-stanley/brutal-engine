# Design: platform layer (`BrutalEngine.Platform`, backend `BrutalEngine.Platform.Sdl3`)

## Purpose

The platform layer is everything the engine needs from the operating system: windows, events, displays and DPI, headless mode, a virtual file system, user and app paths, a clock and message boxes ([Architecture 3.7](../03-architecture.md#37-platform-layer-brutalengineplatform-backend-platformsdl3)). `BrutalEngine.Platform` is the backend-neutral abstraction (L2 library). `BrutalEngine.Platform.Sdl3` implements it on SDL3 through BRUTAL's bindings (L2 backend, [ADR-0003](../adr/0003-sdl3-platform.md)). L3 and above see only `BrutalEngine.Platform` types; no SDL or other binding type appears in its public surface (enforced by the binding-surface rule in [architecture tests](architecture-tests.md#rules)).

This note is the API sketch for M1's Platform issues. Signatures are the contract; XML docs, `PublicAPI.Unshipped.txt` entries, argument validation and tests follow the usual rules ([Coding standards](../07-coding-standards.md#public-api-design)). Where a member says "main thread", calling it from any other thread throws `WrongThreadException` (see [threading](#9-threading)).

BRUTAL citations are paths in the BRUTAL repository, re-checked against the sources pinned with `Ahwoo.Brutal.Sdl` 2026.10.3. Behaviour marked **observed** was seen on Windows 11 with the RTX 2070 SUPER (SDL 3.4, a scratch probe run for this note) or in CI run 45599521930 (the #41 `vulkan-smoke` logs).

## 1. Platform, windows and sizes

### The platform root

```csharp
namespace BrutalEngine.Platform;

public interface IPlatform : IDisposable
{
    string BackendName { get; }                 // "SDL3"
    string VideoDriver { get; }                 // the driver really in use: "windows", "x11", "wayland", "cocoa", "offscreen", "dummy"
    HeadlessMode Headless { get; }              // derived from VideoDriver, see section 3
    TimeProvider Clock { get; }                 // section 7
    string BaseDirectory { get; }               // section 6
    Result<string> GetUserDataDirectory();      // section 6

    Result<IWindow> CreateWindow(WindowOptions options);
    PlatformEventBatch PumpEvents();            // section 2
    PlatformEventBatch Events { get; }          // the batch of the last pump
    bool WaitForEvents(TimeSpan timeout);       // section 2

    IReadOnlyList<DisplayInfo> Displays { get; }
    Result<IReadOnlyList<DisplayMode>> GetFullscreenModes(DisplayId display);

    Result<string> GetClipboardText();
    Result SetClipboardText(string text);
    Result SetCursor(SystemCursor cursor);
    Result SetCursorVisible(bool visible);
    Result ShowMessageBox(MessageBoxRequest request);   // section 8
}
```

- **Created by a backend, owned by the host.** `SdlPlatform.Create` ([section 11](#11-what-platformsdl3-exposes)) returns it; the host (Runtime, a sample or a test) disposes it after every window and after the graphics device. There is no global instance.
- **One per process.** SDL's video subsystem and hints are process-wide, so a second live `SdlPlatform` is misuse (`InvalidOperationException`). The backend counts live instances in a `static int`, which the static-state rule allows (primitive).
- **Thread-safety:** main thread only, except `BackendName`, `VideoDriver`, `Headless`, `Clock` and `BaseDirectory` (immutable, any thread).
- **Allocation:** `PumpEvents`, `Events`, `WaitForEvents`, `Displays` and every `IWindow` getter allocate nothing. `CreateWindow`, clipboard, cursor creation (first use of each shape), display changes, message boxes and failures (the error message) allocate.
- **Dispose** destroys any window the host forgot (logging a warning for each), then quits SDL. A second `Dispose` does nothing; every other member then throws `ObjectDisposedException`.

### Windows: an object, not a handle

```csharp
public interface IWindow : IDisposable
{
    WindowId Id { get; }                        // matches PlatformEvent.Window
    WindowSize Size { get; }                    // logical units (window coordinates)
    WindowSize PixelSize { get; }               // drawable pixels
    float PixelDensity { get; }                 // pixels per logical unit
    float DisplayScale { get; }                 // UI content scale
    WindowState State { get; }
    WindowMode Mode { get; }
    DisplayId Display { get; }
    bool IsTextInputActive { get; }
    bool IsRelativeMouseMode { get; }
    IVulkanSurfaceSource? VulkanSurface { get; } // section 4; null unless created with WindowSurface.Vulkan

    Result SetTitle(string title);
    Result SetSize(WindowSize size);
    Result SetMode(WindowMode mode);            // Windowed or BorderlessFullscreen
    Result SetExclusiveFullscreen(DisplayMode mode);
    Result StartTextInput();
    Result StopTextInput();
    Result SetRelativeMouseMode(bool enabled);
}

public readonly record struct WindowId(uint Value);           // 0 = no window (events not tied to one)
public readonly record struct WindowSize(int Width, int Height);
public readonly record struct DisplayId(uint Value);          // 0 = invalid

[Flags] public enum WindowState { None = 0, Visible = 1, Minimized = 2, Maximized = 4, InputFocus = 8, MouseFocus = 16, Occluded = 32 }
public enum WindowMode { Windowed = 0, BorderlessFullscreen, ExclusiveFullscreen }
public enum WindowSurface { None = 0, Vulkan }

public sealed record WindowOptions
{
    public string Title { get; init; } = "BrutalEngine";
    public WindowSize Size { get; init; } = new(1280, 720);   // logical; each side 1..16384
    public WindowMode Mode { get; init; } = WindowMode.Windowed; // ExclusiveFullscreen is rejected here: use SetExclusiveFullscreen
    public bool Resizable { get; init; } = true;
    public bool HighPixelDensity { get; init; } = true;      // ask for full-resolution pixels on high-DPI displays
    public WindowSurface Surface { get; init; } = WindowSurface.Vulkan;
    public DisplayId? Display { get; init; }                 // null: the primary display
}
```

**Why an object.** A window owns a native resource with a strict destruction order (its graphics surface first, then the window, then the platform), and there are only a handful per process. [Architecture 3.3](../03-architecture.md#33-memory) gives owners `IDisposable` and keeps handles for non-owning references to many pooled objects; a window is the former. An interface also lets a window carry capabilities (`VulkanSurface`) without a lookup table. Events refer to windows by `WindowId`, a plain value, so the event struct stays unmanaged and allocation-free.

- `CreateWindow` returns a failed `Result` for expected failures: the OS or driver refused (`ErrorKind.Native` with SDL's message), or the requested surface can't work in this mode (`ErrorKind.Unsupported`, [section 3](#3-headless-mode)). Invalid options (`null`, sizes out of range, `ExclusiveFullscreen`, an unknown display) throw `ArgumentException`.
- **Getters are snapshots** taken at creation and updated only by `PumpEvents`, so they stay consistent for a whole frame and cost no native call. Setters ask the OS; the snapshot changes when the resulting event is pumped (SDL applies some changes asynchronously).
- **Setters** return `Result`: the OS may refuse (fullscreen on the `dummy` driver, relative mouse mode without a mouse). `BE1001` flags a discarded one.
- Main thread only. After `Dispose`, every member throws `ObjectDisposedException`; a second `Dispose` does nothing.

### Logical size, pixel size and scale

| Member | Meaning | SDL source |
|--------|---------|-----------|
| `Size` | Window size in logical units: what layout, mouse coordinates and `SetSize` use | `SDL_GetWindowSize` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:6330`) |
| `PixelSize` | Drawable size in pixels: what the swapchain uses | `SDL_GetWindowSizeInPixels` (`:6386`) |
| `PixelDensity` | `PixelSize / Size`, e.g. 2.0 on a Retina display | `SDL_GetWindowPixelDensity` (`:6091`) |
| `DisplayScale` | How much bigger UI should be drawn: the pixel density times the OS's content scale (e.g. 1.5 at 150 % on Windows) | `SDL_GetWindowDisplayScale` (`:6101`) |
| `DisplayInfo.ContentScale` | The display's own content scale | `SDL_GetDisplayContentScale` (`:5990`) |

Each change is its own event: `WindowResized` (new `Size`), `WindowPixelSizeChanged` (new `PixelSize`), `WindowDisplayScaleChanged` (new `DisplayScale`; `PixelDensity` is re-read with it) and `WindowDisplayChanged` (moved to another display). Proven behaviour the consumers must handle:

- **SDL reports a size change right after creating a window** (`samples/HelloWindow/HelloWindowApp.cs:137`; **observed** as `WindowPixelSizeChanged` on the `windows`, `offscreen` and `dummy` drivers), so consumers compare sizes instead of reacting to the event alone.
- **A minimised window can report a zero pixel size** (`samples/HelloWindow/SwapchainPlanner.cs:26`, `samples/HelloWindow/HelloWindowApp.cs:125`). `State` has `Minimized`, and renderers skip frames while it is set or `PixelSize` has a zero side.

### Displays

```csharp
public sealed record DisplayInfo(DisplayId Id, string Name, DisplayRect Bounds, DisplayRect UsableBounds, float ContentScale, DisplayMode CurrentMode, bool IsPrimary);
public readonly record struct DisplayRect(int X, int Y, int Width, int Height);   // logical desktop coordinates
public readonly record struct DisplayMode(int Width, int Height, float PixelDensity, float RefreshRate); // pixels; 0 refresh = unknown
```

`Displays` is a snapshot list rebuilt (allocating) at creation and whenever a display event is pumped (`DisplayAdded`, `DisplayRemoved`, `DisplayContentScaleChanged`, `DisplayCurrentModeChanged`). Sources: `SDL_GetDisplays` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:5905`, array freed with `SDL_free`, `:41`), `SDL_GetPrimaryDisplay` (`:5915`), `SDL_GetDisplayName` (`:5937`), `SDL_GetDisplayBounds` (`:5950`), `SDL_GetDisplayUsableBounds` (`:5960`), `SDL_GetCurrentDisplayMode` (`:6051`); `GetFullscreenModes` uses `SDL_GetFullscreenDisplayModes` (`:6000`). `SetMode(BorderlessFullscreen)` is `SDL_SetWindowFullscreen` (`:6536`); `SetExclusiveFullscreen` is `SDL_SetWindowFullscreenMode` (`:6111`) then `SDL_SetWindowFullscreen`.

### Input-related window state

- **Text input** is per window: `StartTextInput`/`StopTextInput` (`SDL_StartTextInput` `Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:9101`, `SDL_StopTextInput` `:9131`). Only committed text is delivered (`TextInput` events). IME composition UI is out of scope for M1.
- **Relative mouse mode** is per window (`SDL_SetWindowRelativeMouseMode`, `:9302`); motion events then carry deltas while the cursor is hidden and confined.
- **Cursors and the clipboard** are on `IPlatform`: `SetCursor` creates each `SystemCursor` shape once (`SDL_CreateSystemCursor` `:9372`, `SDL_SetCursor` `:9382`) and frees them at `Dispose`; `SetCursorVisible` uses `SDL_ShowCursor`/`SDL_HideCursor` (`:9422`, `:9432`). Clipboard text is UTF-8 through `SDL_SetClipboardText`/`SDL_GetClipboardText` (`:5525`, `:5538`; the returned buffer is copied, then `SDL_free`d).

```csharp
public enum SystemCursor { Default = 0, Text, Wait, Crosshair, Progress, ResizeNwse, ResizeNesw, ResizeEw, ResizeNs, Move, NotAllowed, Pointer }
```

## 2. Event model

### The event struct

`PlatformEvent` is a tagged union: a 16-byte header plus a 24-byte payload whose meaning depends on `Kind`, 40 bytes in all (`[StructLayout(LayoutKind.Explicit, Size = 40)]`). It is unmanaged: no strings or references, so a buffer of them is one flat array that the GC never scans.

```csharp
public readonly struct PlatformEvent
{
    public PlatformEventKind Kind { get; }      // offset 0 (ushort)
    public WindowId Window { get; }             // offset 4; default for platform-wide events
    public long Timestamp { get; }              // offset 8: Clock.GetTimestamp() when the pump translated it

    // Payload views (offset 16). Reading the view that doesn't match Kind throws InvalidOperationException.
    public KeyEventData Key { get; }            // KeyDown, KeyUp
    public TextEventData Text { get; }          // TextInput: a range in the batch's text buffer
    public MouseMotionData MouseMotion { get; } // MouseMotion
    public MouseButtonData MouseButton { get; } // MouseButtonDown, MouseButtonUp
    public MouseWheelData MouseWheel { get; }   // MouseWheel
    public WindowSize Size { get; }             // WindowResized, WindowPixelSizeChanged
    public float Scale { get; }                 // WindowDisplayScaleChanged, DisplayContentScaleChanged
    public DisplayId Display { get; }           // WindowDisplayChanged, Display*

    // Factories for backends (one per payload shape), e.g.:
    public static PlatformEvent CreateKey(PlatformEventKind kind, WindowId window, long timestamp, KeyEventData key);
}

public readonly record struct KeyEventData(Scancode Scancode, Keycode Keycode, KeyModifiers Modifiers, bool IsRepeat);
public readonly record struct TextEventData(int Offset, int Length);
public readonly record struct MouseMotionData(float X, float Y, float DeltaX, float DeltaY, MouseButtons Buttons);
public readonly record struct MouseButtonData(MouseButton Button, byte Clicks, float X, float Y);
public readonly record struct MouseWheelData(float X, float Y, float MouseX, float MouseY); // "natural" direction already applied
```

`PlatformEventKind` (`ushort`, `None = 0`) for M1: `QuitRequested`; window: `WindowShown`, `WindowHidden`, `WindowMoved`, `WindowResized`, `WindowPixelSizeChanged`, `WindowDisplayScaleChanged`, `WindowDisplayChanged`, `WindowMinimized`, `WindowMaximized`, `WindowRestored`, `WindowFocusGained`, `WindowFocusLost`, `WindowMouseEntered`, `WindowMouseLeft`, `WindowOccluded`, `WindowExposed`, `WindowCloseRequested`; keyboard and text: `KeyDown`, `KeyUp`, `TextInput`; mouse: `MouseMotion`, `MouseButtonDown`, `MouseButtonUp`, `MouseWheel`; displays: `DisplayAdded`, `DisplayRemoved`, `DisplayContentScaleChanged`, `DisplayCurrentModeChanged`. New kinds are appended, never renumbered. Every other SDL event (gamepads, touch, pen, drops, IME editing, audio devices) is ignored in M1.

- **Mouse positions** are in the window's logical units (as SDL's float coordinates are); multiply by `PixelDensity` for pixels.
- **Timestamps** come from `IPlatform.Clock` at translation, not from SDL's event timestamp, whose time base is SDL's own. Events of one pump keep the OS order, and that order (not the timestamp) is what input latching ([Architecture 3.10](../03-architecture.md#310-input-brutalengineinput)) relies on.

### Keys

- `Scancode` (`enum : ushort`) is the physical key. Values are the USB HID keyboard usage IDs, which are also SDL's values (`SdlScancode.A = 4`, `Brutal.Sdl/Generated/Enums/SdlScancode.gen.cs:10`), so translation is a range check plus a cast. A Platform.Sdl3 test asserts every member's value equals the SDL member of the same name.
- `Keycode` (`readonly record struct` over `uint`) is the key in the current layout: a Unicode code point for keys that produce one, otherwise the scancode with bit 30 set. That is SDL's encoding (`SdlKeycode.A = 97`, `F1 = 1073741882`, `Brutal.Sdl/Generated/Enums/SdlKeycode.gen.cs:19,75`; mask `Sdl.Constants.KeycodeScancodeMask = 0x40000000`, `Brutal.Sdl/Generated/Sdl.Constants.gen.cs:788`), documented as ours. Members: `IsCharacter`, `Character` (a `Rune`), `FromScancode(Scancode)`.
- `KeyModifiers` (`[Flags] enum : ushort`) uses SDL's bit values (`Brutal.Sdl/Generated/Sdl.Constants.gen.cs:388-402`: `LeftShift = 1` ... `Scroll = 0x8000`), with `Shift`, `Control`, `Alt` and `Gui` as both-sides combinations.
- `MouseButton` (`Left = 1, Middle, Right, X1, X2`) and `MouseButtons` (`[Flags]`, `Left = 1 ... X2 = 16`) match SDL's numbering (`SdlMouseButtonFlags`).

### One buffer per pump

```csharp
public readonly ref struct PlatformEventBatch
{
    public ReadOnlySpan<PlatformEvent> Events { get; }
    public bool HasDeferredEvents { get; }                    // more were waiting; they come next pump
    public ReadOnlySpan<byte> GetText(in PlatformEvent textEvent); // the UTF-8 bytes of a TextInput event
}

public sealed class PlatformEventBuffer   // filled by backends; read through PlatformEventBatch
{
    public PlatformEventBuffer(int eventCapacity, int textCapacityBytes);
    public int Capacity { get; }
    public int Count { get; }
    public bool HasRoom { get; }            // room for one more event, and at least TextReserveBytes of text
    public const int TextReserveBytes = 1024;
    public long DeferredPumpCount { get; }  // pumps that stopped because the buffer was full
    public long TruncatedTextCount { get; } // TextInput events cut to fit
    public void Clear();
    public void Add(in PlatformEvent platformEvent);     // throws InvalidOperationException when full
    public void AddText(WindowId window, long timestamp, ReadOnlySpan<byte> utf8);
    public PlatformEventBatch AsBatch();
}
```

- **Who pumps:** the host, once per frame on the main thread (`IPlatform.PumpEvents`, [Architecture 3.2](../03-architecture.md#frame-loop-fixed-step-simulation-variable-rate-rendering)). Each pump clears the buffer, lets SDL collect OS events (`SDL_PumpEvents`, `Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:9507`), then dequeues and translates one event at a time with the raw `SDL_PollEvent` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:9579`) while `HasRoom` holds. Input and the host then read the same batch through `Events`.
- **Capacity:** `PlatformOptions.EventCapacity`, default 1,024 events (40 KiB), and `TextCapacityBytes`, default 8 KiB; both fixed at creation. A frame with more events than that is a hitch, not normal input.
- **Overflow policy: defer, never drop.** When the buffer is full, the pump stops dequeuing: the rest stay in SDL's own queue, in order, and arrive at the next pump (`HasDeferredEvents`, `DeferredPumpCount`). Dropping either end loses edges: dropping the newest loses key releases (stuck keys), dropping the oldest loses presses whose releases survive. Deferring only delays them by a frame. The one lossy case is text: SDL 3's text events have no length limit (`SdlTextInputEvent.text` is a `byte*`, `Brutal.Sdl/Generated/Sdl.Structs.gen.cs:348`), so a single event longer than the remaining text space (at least 1 KiB, because the pump stops below `TextReserveBytes`) is cut at a code-point boundary and counted in `TruncatedTextCount`.
- **Lifetime:** a batch, and the spans and text it hands out, are valid until the next `PumpEvents` or `Dispose` (`WaitForEvents` keeps the event it receives aside and doesn't touch the current batch). Reading one after that is a bug; the `ref struct` keeps a batch from being stored in a field.
- **Zero allocation:** the event array and text buffer are allocated once at creation; translating, adding and reading allocate nothing. An allocation test pumps 1,000 batches of every kind and asserts 0 bytes ([section 12](#12-testing)).
- **Waiting:** `WaitForEvents(timeout)` blocks in `SDL_WaitEventTimeout` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:9599`) and keeps the event it receives for the next pump, which hands it out first. It returns false on timeout. Hosts use it while every window is minimised, as HelloWindow does (`samples/HelloWindow/HelloWindowApp.cs:125-134`).
- **Quit:** `QuitRequested` (all windows closed, or the OS asked) and `WindowCloseRequested` are plain events; closing is the host's decision.

## 3. Headless mode

```csharp
public enum HeadlessMode
{
    None = 0,   // real windows on the desktop
    Offscreen,  // invisible windows; Vulkan presents through VK_EXT_headless_surface where a driver offers it
    NoSurface,  // invisible windows with no graphics surface: platform tests, servers
}
```

The host asks for it with `PlatformOptions.Headless`. The backend picks the SDL video driver before `SDL_Init` by setting the `SDL_VIDEO_DRIVER` hint (`Sdl.Constants.HintVideoDriver`, `Brutal.Sdl/Generated/Sdl.Constants.gen.cs:316`; `SDL_SetHint`, `Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:11814`), as HelloWindow does (`samples/HelloWindow/HelloWindowApp.cs:73-77`):

| Mode | SDL driver | Windows | Linux | macOS |
|------|-----------|---------|-------|-------|
| `None` | SDL's default | `windows` | `x11` or `wayland` | `cocoa` |
| `Offscreen` | `offscreen` | windows work; Vulkan surfaces don't (below) | works (Mesa lavapipe in CI) | works (bundled MoltenVK in CI) |
| `NoSurface` | `dummy` | works | works | works |

Rules:

- **The driver in use wins.** An `SDL_VIDEO_DRIVER` environment variable overrides the hint, and `SDL_SetHint` then returns false (**observed**: with the variable set to `dummy`, the hint `offscreen` was refused and `dummy` was used). So a refused hint is not an error. After `SDL_Init` the backend reads `SDL_GetCurrentVideoDriver` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:5882`), exposes it as `VideoDriver`, and derives `Headless` from it (`offscreen` is `Offscreen`, `dummy` is `NoSurface`, anything else `None`). If the host asked for a headless mode and the driver doesn't give it, `SdlPlatform.Create` fails with `ErrorKind.Unsupported`, naming the driver and the variable. A host that asked for `None` gets whatever the user forced, and can read it from `Headless`.
- **`NoSurface` has no Vulkan.** SDL's `dummy` driver refuses `SDL_WINDOW_VULKAN` (**observed**: "Vulkan support is either not configured in SDL or not available in current SDL video driver (dummy) or platform"). `CreateWindow` with `WindowSurface.Vulkan` therefore fails fast with `ErrorKind.Unsupported` without calling SDL. Platform tests use `WindowSurface.None`.
- **`Offscreen` and Vulkan on Windows fail fast.** SDL's offscreen driver creates surfaces with `VK_EXT_headless_surface`, and no Windows driver we have offers it (NVIDIA on the RTX 2070 SUPER, Mesa lavapipe for Windows; `samples/HelloWindow/README.md:62-64`). SDL doesn't fail the window: on Windows it creates it and lists only `VK_KHR_surface` as required (**observed**, with "Installed Vulkan doesn't implement the VK_EXT_headless_surface extension" on SDL's log), while on Linux and macOS it lists `VK_KHR_surface, VK_EXT_headless_surface` (**observed**, CI run 45599521930, HelloWindow's "Instance extensions" lines on ubuntu-24.04 and macos-15). So for an `Offscreen` window with `WindowSurface.Vulkan`, the backend reads SDL's required extensions right after creating the window and, if `VK_EXT_headless_surface` isn't among them, destroys the window and returns `ErrorKind.Unsupported`: "Headless Vulkan windows need VK_EXT_headless_surface, which no installed Vulkan driver offers. Windows drivers don't; run windowed." The rule is about the extension, not the OS, so a future Windows driver that offers it simply works. Windows GPU tests run windowed (the CI runner has a desktop session).
- **Swapchain extent.** A headless surface reports an undefined current extent (`0xFFFFFFFF`), so the swapchain takes its size from `PixelSize` (`samples/HelloWindow/SwapchainPlanner.cs:19-20`). That is Graphics' rule; Platform guarantees `PixelSize` is real in every mode.
- Headless platforms never show message boxes ([section 8](#8-message-boxes-and-fatal-errors)).

## 4. Vulkan surface source

Decision (maintainer, 2026-11-26): add one documented L2 edge Graphics → Platform; the window exposes a backend-neutral surface source (`nint` and spans only), implemented by Platform.Sdl3. This section owns that capability's definition; `docs/design/graphics.md` (#54) references it and builds swapchain creation on it.

```csharp
namespace BrutalEngine.Platform;

public interface IVulkanSurfaceSource
{
    int RequiredInstanceExtensionCount { get; }
    ReadOnlySpan<byte> GetRequiredInstanceExtension(int index);   // UTF-8 name, no NUL terminator
    Result<nint> CreateVulkanSurface(nint instance);              // returns the VkSurfaceKHR
}
```

- **Extension names** are what the instance must enable to create surfaces for this window, e.g. `VK_KHR_surface` and `VK_KHR_win32_surface`. They come from `SDL_Vulkan_GetInstanceExtensions` (`Brutal.Sdl/Manual/Libraries/Sdl.Vulkan.cs:9-17`), called through the raw binding once at window creation and copied into storage the window owns: BRUTAL's convenience wrapper allocates a `string[]` per call (`:47-59`). The names are ASCII, valid UTF-8, and the same for every window of one platform, so a graphics backend reads them from its first window before creating its instance. Getters allocate nothing and run on any thread. `index` outside `0..Count-1` throws `ArgumentOutOfRangeException`. The spans stay valid while the window is alive.
- **`CreateVulkanSurface(instance)`** calls the raw `SDL_Vulkan_CreateSurface` (`Brutal.Sdl/Manual/Libraries/Sdl.Vulkan.cs:19-32`) with no allocation callbacks, not BRUTAL's wrapper, which throws instead of returning a status (`:61-67`). `instance` is the `VkInstance`, which must have been created with every required extension. It returns the `VkSurfaceKHR` as `nint`: a non-dispatchable 64-bit handle fits because every supported RID is 64-bit, and it is what BRUTAL's binding passes (`Brutal.Sdl/Manual/Libraries/Sdl.Vulkan.cs:7`). Expected failure is a `Result`, never an exception: SDL's error text with `ErrorKind.Native`. Misuse throws: `instance == 0` (`ArgumentException`), a disposed window (`ObjectDisposedException`), the wrong thread (`WrongThreadException`).
- **Ownership.** The caller owns the surface. The graphics backend that created it destroys it with `vkDestroySurfaceKHR` from its own instance (as HelloWindow does, `samples/HelloWindow/VulkanContext.cs:87-90`), after its swapchain and **before the window is disposed** and before the instance is destroyed. The window can't observe `vkDestroySurfaceKHR`, so it can't check this: the order is a documented rule kept by the host's reverse-order shutdown (graphics before platform, [Architecture 3.2](../03-architecture.md#lifecycle)). One live surface per window.
- **Threads.** `CreateVulkanSurface` is main thread only (window-system calls; on macOS the Metal layer must be made there). The extension getters are any thread.
- **Lifetime.** Exists exactly as long as its window: `IWindow.VulkanSurface` returns the same object until `Dispose`, after which its members throw `ObjectDisposedException`. It is `null` for a window created with `WindowSurface.None`.
- **A window that can't present doesn't exist.** A headless window that would need `VK_EXT_headless_surface` without a driver offering it fails at `CreateWindow` with `ErrorKind.Unsupported` ([section 3](#3-headless-mode)), so every `IVulkanSurfaceSource` handed out can at least attempt a surface. Whether a queue family can present to it is Graphics' check.

**The layer edge.** Approving this note approves one listed same-layer edge, which P1 adds to `EngineLayers` (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:51-55`) and to the edge table in [architecture tests](architecture-tests.md#the-layer-map) with this reason:

| Edge | Reason |
|------|--------|
| Graphics → Platform | the render interface creates swapchains for Platform windows through `IVulkanSurfaceSource` ([platform](platform.md#4-vulkan-surface-source)) |

Only Graphics gets the edge. `Graphics.Vulkan` must not reference Platform: the map allows a backend exactly one same-layer edge, to the abstraction it implements (`tests/BrutalEngine.Architecture.Tests/LayerMap.cs:108-115`), and the layering rule checks direct references (`tests/BrutalEngine.Architecture.Tests/LayeringRule.cs:65`). So Graphics hands the backend the surface source through a Graphics-owned type, and no Graphics.Vulkan signature or call names an `IWindow` or `IVulkanSurfaceSource`.

## 5. Virtual file system

All engine file access goes through mount points ([Architecture 3.7](../03-architecture.md#37-platform-layer-brutalengineplatform-backend-platformsdl3)). The VFS is pure managed code (`System.IO` for directories), lives in `BrutalEngine.Platform` (namespace `BrutalEngine.Platform.Vfs`), and needs no backend. The in-memory file system is a public type here, not a test-only helper (Decision (maintainer, 2026-11-26): TestUtilities moves to M1, and its in-memory VFS lands with Platform).

### Paths

```text
path    = scheme "://" [ segment *( "/" segment ) ]
scheme  = lower *( lower / digit / "-" )       ; 1-32 characters, e.g. res, user, test-assets
segment = 1*( allowed character )              ; at most 255 UTF-16 code units
```

- **Allowed characters:** any Unicode scalar except U+0000 to U+001F, U+007F, and `\ / : * ? " < > |`. A segment may not end in `.` or a space, or be a Windows device name (`CON`, `PRN`, `AUX`, `NUL`, `COM1`-`COM9`, `LPT1`-`LPT9`, in any case, with or without an extension). The whole path is at most 1,024 UTF-16 code units. These are the rules of the strictest supported OS, so a path that works on one OS works on all three.
- **Normalisation:** `.` segments are removed, repeated `/` are collapsed and a trailing `/` is dropped. Nothing else changes: no case folding, no Unicode normalisation, and `\` is rejected rather than converted, so Windows-only paths fail everywhere.
- **Traversal rejection:** a `..` segment is invalid anywhere. Paths are never resolved against a parent, so no valid path can name anything outside its mount.
- **Comparison** is ordinal and case-sensitive on every OS.

```csharp
namespace BrutalEngine.Platform.Vfs;

public readonly struct VfsPath : IEquatable<VfsPath>
{
    public static VfsPath Parse(string path);                  // ArgumentException if invalid (for literals in code)
    public static bool TryParse(string? path, out VfsPath result); // for data and user input
    public string Scheme { get; }                              // "res"
    public string RelativePath { get; }                        // "textures/hero.png"; "" for the mount root
    public string Name { get; }                                // last segment, "" for the root
    public VfsPath Combine(string relative);                   // relative is parsed with the same rules
    public bool IsDefault { get; }                             // default(VfsPath) is invalid: passing it throws ArgumentException
    public override string ToString();                         // "res://textures/hero.png"
}
```

`VfsPath` is immutable and thread-safe. `Parse`/`TryParse` allocate (the normalised text and its two parts); paths are built at load time, not per frame.

### Mounts and operations

```csharp
public sealed class VirtualFileSystem
{
    public void Mount(string scheme, IFileSystem fileSystem);     // duplicate or invalid scheme: InvalidOperationException / ArgumentException
    public bool Unmount(string scheme);
    public bool IsMounted(string scheme);

    public Result<bool> Exists(VfsPath path);
    public Result<VfsEntryKind> GetEntryKind(VfsPath path);
    public Result<byte[]> ReadAllBytes(VfsPath path);
    public Result<Stream> OpenRead(VfsPath path);                 // the caller disposes the stream
    public Result WriteAllBytes(VfsPath path, ReadOnlySpan<byte> contents); // atomic replace, creates parent directories
    public Result Delete(VfsPath path);                           // a file, or an empty directory
    public Result CreateDirectory(VfsPath path);
    public Result<IReadOnlyList<VfsEntry>> List(VfsPath directory); // one level, sorted ordinally
}

public interface IFileSystem   // what a mount implements; relativePath is a normalised VfsPath.RelativePath
{
    bool IsReadOnly { get; }
    Result<VfsEntryKind> GetEntryKind(string relativePath);
    Result<byte[]> ReadAllBytes(string relativePath);
    Result<Stream> OpenRead(string relativePath);
    Result WriteAllBytes(string relativePath, ReadOnlySpan<byte> contents);
    Result Delete(string relativePath);
    Result CreateDirectory(string relativePath);
    Result<IReadOnlyList<VfsEntry>> List(string relativePath);
}

public enum VfsEntryKind { None = 0, File, Directory }
public readonly record struct VfsEntry(string Name, VfsEntryKind Kind, long Length); // Length is 0 for directories
```

Mount points:

| Scheme | Mounted by | Backed by | Writable |
|--------|-----------|-----------|----------|
| `res://` | the host (Runtime) | `DirectoryFileSystem` over `IPlatform.BaseDirectory`, or an absolute directory the host passes (the game's `assets/` folder in development). Packs arrive with Assets in M2 | no |
| `user://` | the host | `DirectoryFileSystem` over `IPlatform.GetUserDataDirectory()` | yes |
| any other scheme | tests, tools | usually `InMemoryFileSystem` | as created |

Errors (`EngineError.Kind`):

| Failure | Kind |
|---------|------|
| No file system mounted at the scheme; the entry doesn't exist; a file was expected and a directory found, or the reverse | `NotFound` |
| Write, delete or create on a read-only mount; a file too large for `ReadAllBytes` (over `Array.MaxLength`) | `Unsupported` |
| Access denied by the OS | `Unavailable` |
| Any other OS I/O error (sharing violation, disk full, deleting a non-empty directory) | `Io`, with the OS message and `HResult` as `NativeCode` |
| Programming errors: `default(VfsPath)`, `null` arguments, mounting a scheme twice | exceptions, as above |

Backends:

- **`DirectoryFileSystem(DirectoryFileSystemOptions options)`** with `RootDirectory` (absolute, must exist), `IsReadOnly` and `VerifyCase` (default true). Paths are joined under the root after VFS validation, so they can't leave it lexically; symbolic links inside the root are followed. `VerifyCase` makes every path segment match the on-disk name exactly, also on case-insensitive file systems (Windows, default macOS), so a game that loads `Hero.png` as `hero.png` fails on the developer's Windows machine instead of only on Linux. It costs a directory listing per path segment per call, which suits M1's load-time I/O; the asset database (M2) revisits it. `WriteAllBytes` writes a temporary sibling, flushes it to disk, then renames it over the target, so a crash never leaves a half-written save.
- **`InMemoryFileSystem(bool isReadOnly = false)`**: a dictionary of byte arrays, for tests and generated content. `WriteAllBytes` copies the bytes; `OpenRead` returns a read-only `MemoryStream` over a copy. Tests seed it by writing, then may make it read-only with `Freeze()`.

Threading and allocation:

- `VirtualFileSystem` is thread-safe: asset I/O threads read while the main thread mounts ([Architecture 3.2](../03-architecture.md#threading-model)). Mounts are an immutable array replaced under a lock; operations read it lock-free. `InMemoryFileSystem` is thread-safe (a lock). `DirectoryFileSystem` is thread-safe as far as the OS is: concurrent writers of one path race, and the last rename wins.
- File I/O is not a hot path, and it allocates: results, byte arrays, streams, entry lists, error messages and `VerifyCase` listings. Nothing here runs in a steady-state frame. Asynchronous reads arrive with the asset database (M2), behind the same paths.

## 6. Paths

- **`BaseDirectory`** is the absolute directory holding the application, from `SDL_GetBasePath` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:9742`; BRUTAL's marshaller copies SDL's string and doesn't free it, which is right because SDL owns it, `Brutal.Sdl/CustomStringMarshaller.cs:91-97`). It is read once at creation; if SDL fails, creation fails with `ErrorKind.Native`. `res://` maps here by default.
- **`GetUserDataDirectory()`** is `SDL_GetPrefPath(Organization, Application)` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:9757`; it returns a `byte*` that is copied and then freed with `SDL_free`, `:41`). It is resolved on first call, not at creation, because SDL creates the directory: a test or tool that never asks never creates a folder in the user's profile. Later calls return the cached value. Failure is `ErrorKind.Io` with SDL's message. `PlatformOptions.UserDataDirectory` (absolute) replaces it, so tests pass a temporary directory. `Organization` and `Application` become folder names, so each must match `[A-Za-z0-9 ._-]{1,64}`, otherwise `ArgumentException`.
- **The rotating file log sink bypasses the VFS** (Decision (maintainer, 2026-11-25)): logging must work before the VFS exists and while crashing. The host passes it an absolute directory: `Path.Join(platform.GetUserDataDirectory().Value, "logs")`, or a directory of its own when that fails.
- Both strings are OS paths for `System.IO` and native code, never `VfsPath`s.

## 7. Clock

The clock is the BCL's `TimeProvider`, which the engine already uses for log timestamps (`LoggerOptions.TimeProvider`, [logging](logging.md#key-types)):

- `IPlatform.Clock` is `PlatformOptions.Clock`, defaulting to `TimeProvider.System`. Frame timing uses `GetTimestamp()` and `TimestampFrequency` (or `GetElapsedTime`): monotonic, high-resolution (`TimeProvider.System` reads `Stopwatch.GetTimestamp()`), and allocation-free.
- Tests inject a hand-written `TimeProvider` subclass that returns scripted timestamps ([Testing strategy](../05-testing-strategy.md#test-conventions): time is injected). No new dependency is needed.
- **No platform clock type and no SDL clock.** `SDL_GetPerformanceCounter`/`Frequency` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:14232`, `:14242`) would read the same kind of counter through a native call per read, and a second interface would duplicate `TimeProvider`. Event timestamps use the same clock ([section 2](#the-event-struct)), so one time base serves the loop, input and logs. (This drops the SDL clock first planned for P5.)

## 8. Message boxes and fatal errors

```csharp
public sealed record MessageBoxRequest(MessageBoxKind Kind, string Title, string Message)
{
    public IWindow? Parent { get; init; }
}
public enum MessageBoxKind { Information = 0, Warning, Error }
```

- `IPlatform.ShowMessageBox(request)` shows a modal OS box with an OK button (`SDL_ShowSimpleMessageBox`, `Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:12235`; flags from `SdlMessageBoxFlags`) and blocks until it is dismissed. Main thread only. A refusal is a `Result` (`ErrorKind.Native`). On a headless platform it returns `ErrorKind.Unavailable` at once without calling SDL, so CI never blocks on a dialog.
- **Crash reports** ([Architecture 3.5](../03-architecture.md#35-error-handling)) run after the host boundary has caught the exception, on the main thread. If the platform still exists, Runtime calls `ShowMessageBox` with `MessageBoxKind.Error`. If it doesn't (it failed to start, or was already disposed), Runtime calls `SdlPlatform.TryShowFatalError(string title, string message)`, a stateless static that needs no `SdlPlatform` instance, never throws, and returns false if no box could be shown. Runtime skips both when running headless or under CI. P5 verifies on the three OSes that `SDL_ShowSimpleMessageBox` works without `SDL_Init`, which `TryShowFatalError` relies on. Both must be shown only after the crash report is on disk.
- Message boxes allocate (marshalling), which is fine on these paths.

## 9. Threading

| API | Threads |
|-----|---------|
| `IPlatform`: creation, `Dispose`, `CreateWindow`, `PumpEvents`, `Events`, `WaitForEvents`, displays, clipboard, cursors, message boxes, `GetUserDataDirectory` | main thread only |
| `IPlatform.BackendName`, `VideoDriver`, `Headless`, `Clock`, `BaseDirectory` | any thread (immutable) |
| Every `IWindow` member, `IVulkanSurfaceSource.CreateVulkanSurface` | main thread only |
| `IVulkanSurfaceSource` extension getters | any thread |
| `PlatformEventBatch` | the pumping thread, until the next pump |
| `VfsPath`, `VirtualFileSystem`, `InMemoryFileSystem`, `DirectoryFileSystem` | thread-safe |

- **The main thread is the thread that created the platform.** On macOS it must also be the process's main thread when the `cocoa` driver is used: SDL requires it ([Architecture 3.2](../03-architecture.md#threading-model)). `SdlPlatform.Create` checks this with `SDL_IsMainThread` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:11978`) when the driver is `cocoa`, and fails with `ErrorKind.Unsupported` instead of letting Cocoa abort the process. P3 verifies on macos-15 that `SDL_IsMainThread` reports the process's main thread there.
- **Misuse is detected on every call, in every build:** each main-thread member starts with a thread check that throws `WrongThreadException` naming the API. That is stricter than 3.2's "asserted in debug builds", because a wrong-thread SDL call in a release build is undefined behaviour rather than a clean failure, and the check is one integer comparison, which stays within the zero-allocation, no-measurable-cost budget.
- **Shared types in Core.** Graphics and World need the same check, so it lands in `BrutalEngine.Core`, added by P1:

```csharp
namespace BrutalEngine.Core;

public sealed class WrongThreadException : InvalidOperationException
{
    public WrongThreadException(string apiName, int expectedThreadId, int actualThreadId);
    public string ApiName { get; }
    public int ExpectedThreadId { get; }
    public int ActualThreadId { get; }
}

public readonly struct ThreadAffinity    // captures one thread; default(ThreadAffinity) is invalid and its checks throw InvalidOperationException
{
    public static ThreadAffinity CurrentThread();
    public int ThreadId { get; }
    public bool IsCurrentThread { get; }
    public void ThrowIfNotCurrentThread(string apiName);   // WrongThreadException; allocates only when throwing
}
```

- **Tests and SDL threads.** xUnit runs tests on worker threads, not the process's main thread. The design assumes SDL's video functions work from the thread that initialised them on Windows and Linux. On macOS, `cocoa` needs the process's main thread, so native-boundary tests use the `dummy` driver (and `offscreen` where they need Vulkan), which don't use Cocoa. **Assumptions to verify in P3** on all three runners, before anything builds on them: SDL initialises and runs `dummy` (and `offscreen`, in `vulkan-smoke`) from a test worker thread. If it doesn't, the choice between a main-thread test harness and macOS-only exclusions comes back to the maintainer.
- Because SDL is process-wide and the platform is one per process, Platform.Sdl3's native tests share one platform per test assembly through a collection fixture ([Testing strategy](../05-testing-strategy.md#test-conventions)).

## 10. Native status checks and the BRUTAL log bridge

**`SdlCheck`** (internal to Platform.Sdl3) is the single helper for SDL statuses ([Architecture 3.5](../03-architecture.md#35-error-handling)):

```csharp
internal static class SdlCheck
{
    public static Result Check(byte status, string call);              // SDL bool: 0 is failure
    public static Result<nint> NotNull(nint pointer, string call);     // pointer-returning calls
    public static EngineError Error(string call);                      // ErrorKind.Native, "<call> failed: <SDL_GetError text>"
}
```

- BRUTAL's raw layer returns SDL's bool as a `byte` (`Brutal.Sdl/Manual/Libraries/Sdl.Init.cs:9-10`), and its convenience wrappers throw `SdlException` on failure (`Sdl.Init.Initialize`, `Sdl.Video.CreateWindow` at `Brutal.Sdl/Generated/Libraries/Sdl.Video.gen.cs:647-652`, `Sdl.Vulkan.CreateSurface`). Engine code calls the raw `Sdl.PInvoke` functions and checks every status through `SdlCheck`, so expected failures become `Result`s and the message carries `SDL_GetError` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:2134`). `BE1001` sees only `Result` and `VkResult`, so an unchecked `byte` status is a review item in P3-P6.
- The error text is read only on failure, so successful calls allocate nothing. SDL keeps the error per thread, and all calls are on the main thread.
- Teardown (`SDL_DestroyWindow` `:6834`, `SDL_Quit` `:11968`) returns nothing to check.

**The log bridge (P6).** BRUTAL's modules log through its process-global `LogSystem` (`Brutal.Logging/LogSystem.cs:21-39`; SDL's category is created at `Brutal.Sdl/Sdl.cs:151`, Vulkan's at `Brutal.Vulkan/Manual/Partials/Vulkan.cs:5`) and those messages are dropped unless `LogSystem.Initialize` runs. Platform.Sdl3 is the first backend to start, so it installs the bridge: an `ILoggerProvider` that forwards every BRUTAL category into the engine `Logger` given in `PlatformOptions.Logger`, under categories `Brutal.Sdl`, `Brutal.Vulkan` and so on. Rules:

- One per process, because `LogSystem` is global: the platform installs it at creation and disables it at `Dispose` (`LogSystem.IsEnabled = false`). Graphics.Vulkan doesn't install a second one.
- BRUTAL's `Trace` level stays off: every SDL wrapper logs a trace per call (for example `Brutal.Sdl/Manual/Libraries/Sdl.Vulkan.cs:15`), and enabled BRUTAL messages are materialised as strings, so enabling it would allocate on every native call in the frame loop. BRUTAL checks `IsEnabled` before formatting (`Brutal.Logging/LogCategory.cs:94-95`), so a disabled level costs nothing.
- Forwarded messages allocate (BRUTAL's own formatting). They are warnings and errors, not steady-state frame traffic.

## 11. What Platform.Sdl3 exposes

The backend may expose SDL types, but its public surface is a factory and options, nothing else. The backend-neutral options live in Platform:

```csharp
namespace BrutalEngine.Platform;

public sealed record PlatformOptions
{
    public required string Organization { get; init; }     // [A-Za-z0-9 ._-]{1,64}
    public required string Application { get; init; }      // same rule
    public HeadlessMode Headless { get; init; }            // default None
    public int EventCapacity { get; init; } = 1024;        // 64..65536
    public int TextCapacityBytes { get; init; } = 8192;    // 2048..1048576 (at least twice TextReserveBytes)
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public string? UserDataDirectory { get; init; }        // absolute; replaces SDL_GetPrefPath
    public Logger? Logger { get; init; }                   // category "Platform"; also receives BRUTAL's logs
}
```

```csharp
namespace BrutalEngine.Platform.Sdl3;

public sealed class SdlPlatform : IPlatform
{
    public static Result<SdlPlatform> Create(PlatformOptions options, SdlPlatformOptions? sdlOptions = null);
    public static bool TryShowFatalError(string title, string message);
    // plus the IPlatform members
}

public sealed record SdlPlatformOptions
{
    public string? VulkanLibraryPath { get; init; }        // absolute; sets SDL_HINT_VULKAN_LIBRARY before SDL_Init
}
```

- `Create` sets the hints, calls `SDL_Init(SDL_INIT_VIDEO)` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:11928`) and reads the driver. Expected failures are `Result`s: no display or video driver, the requested headless mode unavailable, the main-thread rule broken on macOS (all `Unsupported` or `Native`). Misuse throws: invalid options, a second live instance.
- `VulkanLibraryPath` points SDL at the Vulkan loader that BRUTAL already uses, so both load one library: the bundled macOS loader, or an explicit `--vulkan-loader`. The constant is `Sdl.Constants.HintVulkanLibrary` (`Brutal.Sdl/Generated/Sdl.Constants.gen.cs:350`), as in `samples/HelloWindow/BundledVulkan.cs:117-140`. Choosing and preloading the loader is Graphics.Vulkan's job; the host passes the path it chose. If the user forced the hint through the environment, the refused hint is logged, not an error (as for the video driver).
- No other public type: the window, surface source, event translation, cursors and `SdlCheck` are internal. Nothing exposes an SDL type, so the backend's public surface needs no binding types at all.

## Failure modes

| Failure | Result |
|---------|--------|
| SDL can't start video, no display, required headless driver unavailable, macOS off the main thread | `SdlPlatform.Create` fails: `Unsupported` or `Native` with SDL's message |
| Window creation refused by the OS or driver | `CreateWindow` fails: `Native` |
| Vulkan surface requested on `NoSurface`, or on `Offscreen` without `VK_EXT_headless_surface` | `CreateWindow` fails: `Unsupported` ([section 3](#3-headless-mode)) |
| `SDL_Vulkan_CreateSurface` fails | `CreateVulkanSurface` fails: `Native` |
| A setter, clipboard, cursor or message box refused | that call's `Result` fails: `Native` (`Unavailable` for message boxes when headless) |
| User data directory can't be created | `GetUserDataDirectory` fails: `Io` |
| More events than the buffer holds | deferred to the next pump and counted; nothing lost ([section 2](#one-buffer-per-pump)) |
| One text event longer than the free text space | cut at a code-point boundary, counted in `TruncatedTextCount` |
| VFS errors | `Result`s with `NotFound`, `Unsupported`, `Unavailable`, `Io` ([section 5](#mounts-and-operations)) |
| Wrong thread | `WrongThreadException` |
| Use after `Dispose`, invalid arguments, second platform, reading the wrong event payload | `ObjectDisposedException`, `ArgumentException`, `InvalidOperationException` |

## Consistency with Architecture 3.7

The design follows 3.7 except for the points below. None of them touches an ADR.

| 3.7 says | This note | Why |
|----------|-----------|-----|
| Events in "a value-type ring buffer" | A fixed per-pump buffer of value-type events, cleared each pump; overflow is deferred in SDL's queue | Every consumer reads the frame's events in the same frame on the main thread, so nothing needs a ring's history; deferring loses nothing, where a ring overwrites |
| Text input with IME composition; gamepads; touch and pen; drag-and-drop files | Committed text only; the rest isn't translated in M1 | Out of M1's scope by this issue. Gamepads and drops need a variable-length payload buffer like text's; composition UI belongs with UI text fields (M3) |
| Power and suspend events | Not in M1 | No M1 consumer; SDL's background events are mobile-only |
| Vsync mode | In Graphics (the swapchain's present mode) | Presentation is the render interface's (`docs/design/graphics.md`, #54) |
| Thread checks "asserted in debug builds" (3.2) | Checked in every build | [Section 9](#9-threading) |

## 12. Testing

- **Platform (pure, every OS, `tests/BrutalEngine.Platform.Tests`):** every rule above as unit tests: options validation, `PlatformEvent` payload views (including the wrong-view exception), `Keycode`, `PlatformEventBuffer` (capacity, `HasRoom` and the text reserve, deferral, truncation on code-point boundaries for 2-, 3- and 4-byte sequences, `Clear`), `VfsPath` grammar and every rejection, `VirtualFileSystem` routing and error kinds, `InMemoryFileSystem`, and `DirectoryFileSystem` on temporary directories (atomic replace, read-only mounts, `VerifyCase` on all three OSes, which covers both case-sensitive and case-insensitive file systems).
- **Property tests (CsCheck):** `VfsPath` normalisation is idempotent; a parsed path never contains `..`, `\`, `//` or a trailing `/`, and round-trips through `ToString`; random strings either fail to parse or yield a path that a `DirectoryFileSystem` resolves strictly inside its root; random buffer fills against a reference model keep every event, in order, across deferral.
- **Allocation tests:** filling and reading 1,000 batches of every event kind (pure, in Platform.Tests), and in Platform.Sdl3 1,000 `PumpEvents` calls over events pushed with `SDL_PushEvent` plus every `IWindow` getter, allocate 0 managed bytes. Also run under Windows dynamic coverage, as CI does.
- **Native boundary (Platform.Sdl3, every OS, `build-test` job):** with the `dummy` driver and `WindowSurface.None`: create and dispose, the one-per-process rule, the driver derivation from `SDL_VIDEO_DRIVER`, window creation and sizes, setters' results, the startup size event, the `NoSurface`+Vulkan failure, clipboard round-trip, displays. Event translation is table-driven over synthetic `SdlEvent` values (pure), plus an `SDL_PushEvent` round trip. Thread checks are tested from a second thread.
- **Vulkan surfaces** need a Vulkan driver, so they run in the `vulkan-smoke` job (or the conformance project that `docs/design/graphics.md`, #54, defines): `offscreen` surfaces on Linux and macOS, windowed on the Windows runner, and the `Offscreen`+Vulkan `Unsupported` failure on Windows. Locally they run on the RTX 2070 SUPER.
- **Manual (reference machine, release QA checklist, [Testing strategy](../05-testing-strategy.md#test-layers)):** DPI change while running (moving a window between displays with different scales, changing Windows' scale setting), multi-monitor fullscreen modes, IME text commits, minimise and restore. These can't run on CI runners (one virtual display, fixed DPI) and are unverified until done by hand.

## 13. Issue split

Adjusted from the planned Platform issues P1-P6: events become their own issue, and the clock needs no SDL code.

| Id | Scope | Blocked by |
|----|-------|-----------|
| P1 | Create `src/BrutalEngine.Platform` + tests: `IPlatform`, `IWindow`, `WindowOptions`, sizes, ids, states, modes, `DisplayInfo`/`DisplayMode`, `SystemCursor`, `MessageBoxRequest`, `HeadlessMode`, `PlatformOptions`, `IVulkanSurfaceSource`; in Core, `WrongThreadException` and `ThreadAffinity`; the `Graphics → Platform` edge in `EngineLayers` and [architecture tests](architecture-tests.md#the-layer-map); the Architecture.Tests reference | this note |
| P1b | Events: `PlatformEvent` and payloads, `PlatformEventKind`, `Scancode`, `Keycode`, `KeyModifiers`, mouse types, `PlatformEventBuffer`, `PlatformEventBatch`; allocation and property tests | P1 |
| P2 | The VFS: `VfsPath`, `VirtualFileSystem`, `IFileSystem`, `DirectoryFileSystem`, `InMemoryFileSystem`; property tests | P1b (same project) |
| P3 | Create `src/BrutalEngine.Platform.Sdl3`: `SdlPlatform.Create`, `SdlCheck`, hints, the driver and headless rules, the macOS main-thread check, windows (create, sizes, setters, fullscreen), `IVulkanSurfaceSource`, `VulkanLibraryPath`; `dummy`-driver tests on 3 OSes; surface tests in `vulkan-smoke`; verify the macOS worker-thread assumption first | P1, ADR-0011 |
| P4 | SDL event translation into `PlatformEventBuffer`, deferral, `WaitForEvents`, snapshot updates from events | P3, P1b |
| P5 | Base and user paths, displays and fullscreen modes, clipboard, cursors, message boxes and `TryShowFatalError` | P4 (same project) |
| P6 | The BRUTAL log bridge | P5 (same project) |

P1, P1b and P2 share `PublicAPI.Unshipped.txt` and run in sequence; P3 can start once P1 merges, alongside P1b and P2, because it's a different project (P4 waits for P1b).

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

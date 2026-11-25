# Design: runtime host (`BrutalEngine.Runtime`)

## Purpose

`BrutalEngine.Runtime` is the L5 host from [Architecture 3.2](../03-architecture.md#32-application-host-and-main-loop): it owns `Main`-thread concerns, the start-up and shutdown order, the module registry, the fixed-step frame loop, the BRUTAL splash ([3.23](../03-architecture.md#323-brutal-licence-compliance-features)) and crash reports ([3.5](../03-architecture.md#35-error-handling)). It is a host in the layer map (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:43`), so it may reference the backends: it is where they are chosen ([3.1](../03-architecture.md#31-layering)). The `BrutalEngine` meta-package references it. No binding type appears in its public surface ([architecture tests](architecture-tests.md#rules)).

This note is the API sketch for M1's Runtime issues (section 12). Signatures are the contract; XML docs, `PublicAPI.Unshipped.txt` entries, argument validation and tests follow the usual rules ([Coding standards](../07-coding-standards.md#public-api-design)). It references, and doesn't redefine, the contracts of the [platform note](platform.md) (windows, events, the clock, message boxes, `WrongThreadException`), the [render interface note](graphics.md) (frames, swapchains, device loss), the [logging note](logging.md) and the [world note](world.md) (`EcsWorld`, schedules).

- Decision (maintainer, 2026-11-26): the M1 public APIs are settled in design notes first. Merging this note approves the Runtime API, and the Runtime code issues are drafted from it.
- Decision (maintainer, 2026-11-26): TestUtilities moves to M1; its headless host lands with Runtime (section 8).
- Decision (maintainer, 2026-11-25): the rotating file log sink writes to an absolute directory the host provides, bypassing the VFS (section 7).

## 1. Entry points

```csharp
namespace BrutalEngine.Runtime;

public interface IGame
{
    void ConfigureModules(ModuleRegistry modules);   // at the Modules step, before any module starts
    Result Initialize(EngineContext context);        // after the splash: systems, entities, first loads
    void Shutdown(EngineContext context);            // after the last frame, before modules stop
}

public abstract class Game : IGame                   // no-op virtual defaults; Initialize returns Result.Success
{
    public virtual void ConfigureModules(ModuleRegistry modules);
    public virtual Result Initialize(EngineContext context);
    public virtual void Shutdown(EngineContext context);
}

public static class GameHost
{
    public static int Run<TGame>(EngineOptions options) where TGame : IGame, new();   // (int)Execute(...).ExitCode
    public static HostRunResult Execute<TGame>(EngineOptions options) where TGame : IGame, new();
    public static HostRunResult Execute(IGame game, EngineOptions options);
}
```

A game's `Main` is one line: `return GameHost.Run<MyGame>(new EngineOptions { Organization = "Acme", Application = "Rocket", CommandLine = args });`.

- **Why `GameHost` and not `BrutalEngine.Run`** (docs/03, `docs/03-architecture.md:42`): a type named `BrutalEngine` in `BrutalEngine.Runtime` hides the root namespace for every file in that namespace, so `BrutalEngine.Core.Logger` there fails with CS0426 ("The type name 'Core' does not exist in the type 'BrutalEngine'"; observed with a scratch compile on .NET SDK 10.0.404). It is the same kind of clash that named `EcsWorld` in the world note.
- `Run` and `Execute` throw only for misuse found before anything starts: null or invalid options (`ArgumentException`), or a second host with the default SDL platform already running in the process (`InvalidOperationException`; SDL allows one platform per process, [platform note section 1](platform.md#the-platform-root)). `GameHost` counts those runs in a `static int`, which the static-state rule allows. Everything after that, including exceptions from game code, ends in a `HostRunResult`.
- `Execute` is the same code path with the detail tests need. The headless host (section 8) returns it too.

```csharp
public sealed record EngineOptions
{
    public required string Organization { get; init; }         // PlatformOptions rules: [A-Za-z0-9 ._-]{1,64}
    public required string Application { get; init; }          // same rule; also the default window title
    public string? ApplicationVersion { get; init; }           // null: the entry assembly's informational version
    public IReadOnlyList<string> CommandLine { get; init; } = []; // the arguments the engine parses (below)
    public HeadlessMode Headless { get; init; }                // platform note section 3
    public int? FrameLimit { get; init; }                      // exit after this many frames; null: run until asked
    public string? UserDataDirectory { get; init; }            // absolute; replaces SDL_GetPrefPath
    public string? ContentDirectory { get; init; }             // absolute res:// root; null: IPlatform.BaseDirectory
    public WindowOptions Window { get; init; } = new();        // platform note; Title defaults to Application
    public GraphicsSettings Graphics { get; init; } = new();
    public FrameLoopOptions Loop { get; init; } = new();       // section 4
    public WorldOptions World { get; init; } = new();          // world note; the host sets Allocator and Clock
    public SplashOptions Splash { get; init; } = new();        // section 5
    public LoggingOptions Logging { get; init; } = new();      // section 7
    public CrashReportOptions CrashReports { get; init; } = new(); // section 6
    public bool DebugOverlay { get; init; } = true;            // section 9: development builds only
    public bool? LeakCheck { get; init; }                      // null: development builds only (section 2)
    public INativeAllocator? Allocator { get; init; }          // null: a HeapAllocator. Not owned; tracked when LeakCheck
    public HostBackends Backends { get; init; } = new();
}

public sealed record GraphicsSettings
{
    public AdapterSelection Adapter { get; init; } = AdapterSelection.Auto;   // graphics note section 2
    public bool? Validation { get; init; }                     // null: development builds only
    public PresentMode PresentMode { get; init; } = PresentMode.Fifo;
    public int FramesInFlight { get; init; } = 2;              // 1..3
    public string? VulkanLoaderPath { get; init; }             // absolute; see "Backends" below
}

public sealed record HostBackends                              // backends are chosen here (Architecture 3.1)
{
    public Func<PlatformOptions, Result<IPlatform>>? CreatePlatform { get; init; } // null: SDL3
    public IGraphicsBackend? Graphics { get; init; }           // null: Vulkan
}
```

**Backends.** M1 has one backend of each kind, so a game never names one; `HostBackends` exists for the headless host and for servers. With the defaults, the host first calls the Vulkan backend's public factory with `GraphicsSettings.VulkanLoaderPath`. Its name and options are fixed by the Vulkan backend note (#55); what Runtime needs from it is the `IGraphicsBackend` and the absolute path of the loader it preloaded (or none). The host passes that path to `SdlPlatform.Create` as `SdlPlatformOptions.VulkanLibraryPath`, so SDL and BRUTAL load one library (`docs/design/platform.md:577`). A custom `CreatePlatform` gets no loader path. Under `HeadlessMode.NoSurface` no graphics backend is created at all.

**Command line.** `CommandLine` holds the arguments the game wants the engine to read; a game that has its own options removes them first. Each engine option may appear once. An unknown option, a repeated one, or a bad value is a usage error: the host prints the error and the usage text to standard error and returns exit code 64 before anything starts. Names match exactly (ordinal), and enum-like values match one listed token, never `Enum.TryParse`.

| Option | Sets | Notes |
|--------|------|-------|
| `--headless` | `Headless = Offscreen` | HelloWindow's flag (`samples/HelloWindow/HelloWindowOptions.cs:13-25`) |
| `--no-graphics` | `Headless = NoSurface` | servers and simulation tests: no window, no device, no splash |
| `--frames <n>` | `FrameLimit` | `n` from 1 to `int.MaxValue` |
| `--size <w>x<h>` | `Window.ContentSize` | content units, each side 1 to 16384 |
| `--gpu <selection>` | `Graphics.Adapter` | `AdapterSelection.TryParse` (`docs/design/graphics.md:140`) |
| `--validation` | `Graphics.Validation = true` | |
| `--vulkan-loader <path>` | `Graphics.VulkanLoaderPath` | absolute |
| `--log-level <level>` | `Logging.MinimumLevel` | `trace`, `debug`, `information`, `warning`, `error`, `critical` |
| `--user-data <dir>` | `UserDataDirectory` | absolute; CI and portable installs |

HelloWindow's test-only flags (`--exercise-resize`, `--require-cpu`, `--require-driver`, `--inject-validation-error`) stay in the sample, which maps what it can onto these when it is rewritten (S1).

**Config file.** After the platform starts, the host reads `user://engine.json` if it exists: one JSON object of dotted keys with string or boolean values, read with `Utf8JsonReader` (no reflection, NativeAOT-safe). Keys: `window.size` (`"1280x720"`), `window.mode` (`"windowed"` or `"borderless"`), `graphics.adapter`, `graphics.presentMode` (`"fifo"`, `"fifo-relaxed"`, `"mailbox"`, `"immediate"`), `graphics.validation`, `log.level`. **Precedence:** `EngineOptions` from code, then the file, then the command line. The file belongs to the player, so a bad file never stops the game: an unreadable file, invalid JSON, an unknown key or a bad value is logged as a `Warning` and that key (or the whole file) is ignored. Options that act before the platform exists (`Headless`, `UserDataDirectory`, the loader path) are code and command line only.

**Exit codes.** The same numbers as HelloWindow (`samples/HelloWindow/ExitCode.cs:7-19`), so its CI checks survive the rewrite, plus one for crashes:

```csharp
public enum HostExitCode
{
    Success = 0,
    ValidationMessages = 2,   // Graphics validation was on and reported an error or warning
    StartupFailed = 3,        // a fatal start-up step failed (section 2)
    RuntimeFailure = 4,       // a fatal failure after start-up (device recovery failed), or leaks found
    Usage = 64,               // bad command line (sysexits EX_USAGE)
    Crashed = 70,             // unhandled exception, crash report written (sysexits EX_SOFTWARE)
}

public sealed record HostRunResult
{
    public required HostExitCode ExitCode { get; init; }
    public HostFailure? Failure { get; init; }                 // the fatal error, if any
    public Exception? CrashException { get; init; }
    public string? CrashReportPath { get; init; }
    public long FramesRun { get; init; }
    public long SteadyStateAllocatedBytes { get; init; }       // section 4
    public long SteadyStateFrames { get; init; }
    public IReadOnlyList<HostFailure> Degradations { get; init; } = [];
}

public readonly record struct HostFailure(HostStep Step, EngineError Error);
```

`ValidationMessages` replaces a `Success` only: a run that already failed keeps its code.

## 2. Lifecycle

The order follows docs/03 (`docs/03-architecture.md:47-50`). Each step that can fail expectedly reports an `EngineError` ([errors](errors.md)) wrapped in a `HostFailure` naming the step:

```csharp
public enum HostStep
{
    None = 0,
    Configure = 10, Logging = 20, GraphicsBackend = 30, Platform = 40, UserData = 50, Window = 60,
    GraphicsDevice = 70,
    // 80 and 90 are reserved for the audio device and the asset database (below)
    Modules = 100, Splash = 110, GameInitialize = 120, Running = 130,
    GameShutdown = 140, ModulesStop = 150, Teardown = 160, LeakCheck = 170,
}
```

| Step | Does | Failure | Class |
|------|------|---------|-------|
| `Configure` | Validate options, parse the command line | Usage error (exit 64) | fatal |
| `Logging` | Logger with console and memory sinks (section 7) | none (misuse only) | |
| `GraphicsBackend` | Vulkan backend factory, loader preload (skipped for `NoSurface`) | "Vulkan loader missing" (`Unavailable`) | fatal |
| `Platform` | `SdlPlatform.Create` with the loader path and `PlatformOptions.Logger` | No video driver, headless mode unavailable (`Unsupported`, `Native`) | fatal |
| `UserData` | `GetUserDataDirectory`, mount `res://` and `user://`, read `engine.json`, attach the log file | `Io` | **degradable**: a fallback directory (section 7), no `user://` mount |
| `Window` | `CreateWindow(Window)` (skipped for `NoSurface`) | `Native`, `Unsupported`, `NotFound` | fatal |
| `GraphicsDevice` | `GraphicsDevice.Create` with the window as `CompatibleWindow`, then `CreateSwapchain` | "No Vulkan 1.3 GPU found" (`Unsupported`), validation layer missing (`Unavailable`) | fatal |
| `Modules` | `IGame.ConfigureModules`, then start every module in dependency order (section 3) | the module's `Result` | per module |
| `Splash` | Decode the logos, create the splash's GPU resources (section 5) | `Unsupported`, `OutOfMemory` | fatal (a licence duty is never skipped silently, `samples/HelloWindow/HelloWindowApp.cs:259-263`) |
| `GameInitialize` | `IGame.Initialize` (after the splash frames, section 4) | the game's `Result` | fatal |
| `Running` | The frame loop | device lost and `Recover()` failed (`docs/design/graphics.md:748`) | fatal: exit 4 |
| `GameShutdown`, `ModulesStop` | `IGame.Shutdown`, then each started module's `Stop` in reverse start order | exceptions are logged and shutdown continues | |
| `Teardown` | Splash resources, swapchain, device, window, platform, in that order | none | |
| `LeakCheck` | `GraphicsDevice.ThrowIfLeaks()` before its `Dispose`, `TrackingAllocator.ThrowIfLeaks()` after the world's | leaks: logged at `Error`, exit 4 | when `LeakCheck` |

- **Fatal vs degradable** follows docs/03: "Audio is degradable. Graphics is fatal." (`docs/03-architecture.md:53`). A fatal failure is logged at `Critical`, shown in a message box with its user-facing message (`MessageBoxKind.Error`, skipped when headless, under CI, i.e. the `CI` environment variable is set, or when the platform never started, in which case `SdlPlatform.TryShowFatalError` is used, `docs/design/platform.md:469`), and ends the run with exit 3 before `Running`, or 4 from it. A degradable failure is logged at `Warning`, recorded in `EngineContext.Degradations`, and the run continues without that service.
- **Partial teardown.** A failure at any step undoes exactly the steps that completed, in reverse: modules that started are stopped, the device is disposed before the window and the window before the platform (the surface rule of `docs/design/platform.md`, [section 4](platform.md#4-vulkan-surface-source)), and the logger last. A step that never ran is never undone.
- **Reserved steps.** The audio device and the asset database are absent in M1. Their steps are numbered 80 and 90, between `GraphicsDevice` and `Modules`, so they will start in the docs/03 order without renumbering `HostStep` (its values are spaced for that) and their services will be ready before any module starts. Audio will be degradable ("continuing without audio"), the asset database fatal.
- **`NoSurface` runs** skip `GraphicsBackend`, `Window`, `GraphicsDevice` and the splash; the loop simulates without rendering.
- **Leak check** is on in development builds (section 9's switch) unless `LeakCheck` says otherwise. The host then wraps the engine allocator in a `TrackingAllocator` and passes it to the world and the device. The graphics device's own leak report covers GPU objects (`docs/design/graphics.md`, [section 13](graphics.md#13-errors-and-diagnostics)).

```csharp
public sealed class EngineContext       // given to the game and to every module; main thread only (section 10)
{
    public EngineOptions Options { get; }            // after the command line and engine.json were applied
    public Logger Logger { get; }
    public IPlatform Platform { get; }
    public IWindow? Window { get; }                  // null for NoSurface
    public GraphicsDevice? Graphics { get; }         // null for NoSurface
    public SwapchainHandle Swapchain { get; }        // the window's; null handle for NoSurface
    public EcsWorld World { get; }
    public VirtualFileSystem Files { get; }          // res:// always; user:// unless UserData degraded
    public ModuleRegistry Modules { get; }
    public FixedStepClock FrameClock { get; }        // section 4: step counts, spiral counter
    public MemoryLogSink LogTail { get; }            // section 7
    public HostPhase Phase { get; }
    public IReadOnlyList<HostFailure> Degradations { get; }
    public void RequestExit();                       // any thread: the loop ends after the current frame
}

public enum HostPhase { Starting = 0, Splash, Running, ShuttingDown }
```

## 3. Module registry

A module is an engine or game service with a start and a stop, started after the core services and stopped before them. Platform, graphics and the world aren't modules: their order is fixed by the lifecycle above, and modules depend on them implicitly.

```csharp
public interface IEngineModule
{
    ModuleInfo Info { get; }                 // read once, when the module is added
    Result Start(EngineContext context);     // main thread; may allocate; failure: Info.OnStartFailure
    void Stop(EngineContext context);        // reverse start order; must not throw (exceptions are logged)
}

public sealed record ModuleInfo
{
    public required string Name { get; init; }                 // [A-Za-z0-9._-]{1,64}, unique
    public IReadOnlyList<string> DependsOn { get; init; } = [];  // names of modules that must start first
    public ModuleFailurePolicy OnStartFailure { get; init; } = ModuleFailurePolicy.Fatal;
}

public enum ModuleFailurePolicy { Fatal = 0, Degradable }

public interface IFrameModule                // optional; the frame loop calls these in start order (section 4)
{
    void BeginFrame(in FrameContext frame);  // after the event pump: e.g. input snapshots
    void Extract(in FrameContext frame);     // the only phase that reads the world for rendering
    void Prepare(in FrameContext frame);
    void Record(in FrameContext frame, CommandList commands, in RenderTarget target);
    void EndFrame(in FrameContext frame);    // after submit: e.g. completing asynchronous loads
}

public abstract class EngineModule : IEngineModule, IFrameModule   // virtual no-ops; Start returns Success
{
    protected EngineModule(ModuleInfo info);
    public ModuleInfo Info { get; }
    public virtual Result Start(EngineContext context);
    public virtual void Stop(EngineContext context);
    public virtual void BeginFrame(in FrameContext frame);
    public virtual void Extract(in FrameContext frame);
    public virtual void Prepare(in FrameContext frame);
    public virtual void Record(in FrameContext frame, CommandList commands, in RenderTarget target);
    public virtual void EndFrame(in FrameContext frame);
}

public sealed class ModuleRegistry
{
    public void Add(IEngineModule module);           // inside IGame.ConfigureModules only
    public T Get<T>() where T : class, IEngineModule; // InvalidOperationException if absent or not started
    public bool TryGet<T>([NotNullWhen(true)] out T? module) where T : class, IEngineModule;
    public IReadOnlyList<ModuleStatus> Statuses { get; }   // in start order once started
}

public readonly record struct ModuleStatus(string Name, ModuleState State, EngineError? Error);
public enum ModuleState { Registered = 0, Started, Degraded, Stopped }
```

- **Registration** happens at the `Modules` step: the host adds its own modules first (the debug overlay, section 9), then calls `IGame.ConfigureModules`. `Add` at any other time throws `InvalidOperationException`; a null module, or an invalid or duplicate name, throws `ArgumentException`.
- **Order.** At the `Modules` step the registry sorts topologically by `DependsOn`; ties keep registration order, so the order is deterministic, as for world schedules. An unknown dependency name or a cycle is misuse: `InvalidOperationException` naming it, thrown before any module starts (it ends the run as a crash, section 6, because it is a bug in the game or engine, not a runtime condition).
- **Start failure.** A `Fatal` module's failed `Result` stops start-up with a partial teardown: the modules already started are stopped in reverse, then the core services (section 2), and the run exits 3. A `Degradable` module's failure marks it `Degraded` and logs a `Warning`; every module that depends on it, directly or not, isn't started either and is marked `Degraded` (or fails start-up, if it is itself `Fatal`). An exception from `Start` is a crash, not a failed result.
- **Stop** runs in reverse start order, only for `Started` modules, after `IGame.Shutdown`. An exception is logged at `Error` with the module name, and the next module still stops.
- **Frame hooks** are called on every started module that implements `IFrameModule`, in start order. The registry keeps them in an array built at the `Modules` step, so calling them allocates nothing. The one exception is `Record` for the debug overlay, which the host calls after every other module so the overlay draws on top.
- **Backends are not modules.** Platform and graphics backends are chosen by `HostBackends` and created in their fixed steps; the meta-package `BrutalEngine` brings the defaults.

## 4. The frame loop

### The fixed-step clock

The loop's time-keeping is a pure type with no platform or GPU dependency, so it is unit- and property-tested in isolation (R2):

```csharp
public sealed record FrameLoopOptions
{
    public int FixedUpdateRate { get; init; } = 60;                                // steps per second, 1..1000
    public TimeSpan MaxFrameDelta { get; init; } = TimeSpan.FromMilliseconds(250);  // one step .. 1 s
}

public sealed class FixedStepClock
{
    public FixedStepClock(FrameLoopOptions options, long timestampFrequency);   // TimeProvider.TimestampFrequency
    public FrameTiming Advance(long timestamp);   // once per frame; the first call after creation or Rebase: 0 steps
    public void Rebase(long timestamp);           // resume after a pause without catching up
    public TimeSpan FixedDelta { get; }           // 1 / FixedUpdateRate, rounded to 100 ns
    public long FrameCount { get; }
    public long StepCount { get; }
    public long SpiralCount { get; }              // frames whose delta hit MaxFrameDelta
}

public readonly record struct FrameTiming(int Steps, float Alpha, TimeSpan Delta, bool Clamped);
```

- **Exact integer accumulation.** The accumulator counts timestamp ticks multiplied by the rate: each frame adds `min(timestamp - last, maxTicks) * FixedUpdateRate`, and each step subtracts `timestampFrequency`. So 60 steps per second need no rounded 1/60 s and never drift, and the step count over any run is exactly `floor(sum of clamped deltas * rate / frequency)`. `Alpha = accumulator / frequency` (in `[0, 1)`), the interpolation factor between the last two fixed states (docs/03, `docs/03-architecture.md:58-70`).
- **Spiral-of-death protection:** each frame's delta is clamped to `MaxFrameDelta` before it is accumulated, so one frame runs at most `ceil(MaxFrameDelta * rate)` steps (15 at the defaults). A clamped frame sets `Clamped` and raises `SpiralCount`, which the overlay shows (`docs/03-architecture.md:74`).
- **Misuse:** a timestamp older than the previous one throws `ArgumentOutOfRangeException`; options outside the ranges above (including `MaxFrameDelta` shorter than one step) throw from the constructor. The constructor also rejects a frequency for which `maxTicks * rate` could overflow.
- **Injected clock:** the host reads `IPlatform.Clock.GetTimestamp()` (the BCL `TimeProvider`, [platform note section 7](platform.md#7-clock)), so tests drive the whole loop with scripted timestamps.
- Not thread-safe (the loop owns it); `Advance` and `Rebase` allocate nothing.

### One frame

```text
while not exit requested and FrameLimit not reached:
    platform.PumpEvents()                                  # main thread
    QuitRequested, or WindowCloseRequested for the main window -> request exit
    if window minimised (State has Minimized or a zero PixelSize):
        platform.WaitForEvents(1 s) until restored or exit requested; clock.Rebase(now); continue
    timing = clock.Advance(clock time)
    modules.BeginFrame
    if phase == Running:
        repeat timing.Steps: world.RunSchedule(FixedUpdate, clock.FixedDelta)
        world.RunSchedule(Update, timing.Delta)
        world.RunSchedule(PostUpdate, timing.Delta)        # the transform system (world note)
    if graphics:
        graphics.BeginFrame(out commands)                  # DeviceLost: see below
        status = commands.AcquireSwapchainImage(swapchain, out image)
        if phase == Running: modules.Extract(alpha); modules.Prepare
        if phase == Splash: splash.Record(commands, image) else modules.Record(commands, image)
        overlay.Record (development builds)
        graphics.SubmitFrame(commands)
    else:
        platform.WaitForEvents(time until the next fixed step)   # NoSurface pacing
    modules.EndFrame
    phase transitions (below)
```

- **Phases.** The loop starts in `Splash`: events are pumped and frames presented, but no schedule runs and no module renders, because the game isn't initialised. When the splash completes (section 5), the host calls `IGame.Initialize` between two frames, rebases the clock (initialisation time is not simulated), and switches to `Running`. A run without a splash (`NoSurface`, or the splash disabled) goes straight to `Running`.
- **Schedules.** `FixedUpdate` runs `Steps` times with the fixed delta, then `Update` and `PostUpdate` once with the frame's delta (`EcsWorld.RunSchedule(ScheduleLabel, TimeSpan)`, [world note section 7](world.md#7-schedules)). docs/03's pseudo-code has no `PostUpdate`; it runs here because the world note pins its transform system there.
- **Extract, prepare, record** are separate module phases from day one (`docs/03-architecture.md:58-70`); `Extract` is the only one that reads the world, so rendering can move to its own thread later without API changes.

```csharp
public readonly struct FrameContext
{
    public long FrameNumber { get; }          // frames run so far
    public FrameTiming Timing { get; }        // Alpha for interpolation
    public HostPhase Phase { get; }
    public EngineContext Engine { get; }
}

public readonly record struct RenderTarget(AcquireStatus Status, SwapchainImage Image); // Image valid when Status is Acquired or Recreated
```

- **Swapchain results** (`AcquireStatus`, [graphics note section 3](graphics.md#3-swapchains)): `Recreated` reaches modules through `RenderTarget.Status` so they resize their targets; `Minimised` and `Skipped` still submit the frame (the device clears an unwritten image, `docs/design/graphics.md:263`); `SurfaceLost` makes the host destroy and recreate the swapchain after `SubmitFrame`; `DeviceLost` is handled below.
- **Device loss.** When `BeginFrame`, `AcquireSwapchainImage` or `SubmitFrame` reports `DeviceLost`, the host finishes the frame (encoder calls in a lost frame are no-ops) and calls `GraphicsDevice.Recover()` before the next one. Owners (the splash, the overlay, game renderers) recreate their resources through `IGpuResourceOwner` ([graphics note section 12](graphics.md#12-device-lost)). A failed recovery is a fatal `Running` failure: the host writes a crash report with the error in place of an exception (section 6), because the device state is worth reporting, then shuts down normally (destroying objects stays valid on a lost device, and every wait is bounded by `GpuTimeout`) and exits 4.
- **Minimised windows.** The loop blocks in `WaitForEvents` (as HelloWindow does, `samples/HelloWindow/HelloWindowApp.cs:142-151`), so simulation pauses with rendering, and `Rebase` drops the paused time instead of clamping it into a spiral. Running simulation while minimised (dedicated servers, networking in M5) is a later option behind `FrameLoopOptions`.
- **Frame limit.** `FrameLimit` counts frames that ran (rendered, or simulated under `NoSurface`), splash frames included, as HelloWindow's `--frames` does. Minimised waits don't count.
- **Zero allocation.** A steady-state frame allocates no managed memory: the loop has no LINQ, closures or string building; module and frame-hook lists are arrays; `FrameContext` and `RenderTarget` are passed by `in`; `FixedStepClock`, the platform pump, the render-interface frame calls and `Schedule.Run` are all allocation-free by their notes. The host measures every iteration with `GC.GetAllocatedBytesForCurrentThread()`, except the first frame, phase changes and frames that recreated a swapchain or recovered the device, and reports the total in `HostRunResult` and in its exit log line (`managed bytes allocated over N steady-state frames`, the line HelloWindow's CI already reads).

## 5. The splash

The BRUTAL logo is shown before gameplay, unaltered except for scaling, for long enough to be legible (BRUTAL `LICENSE.txt:23-28`; docs/03, `docs/03-architecture.md:256`). The M0 sample proved the rules; Runtime promotes its types instead of reinventing them.

```csharp
public sealed record SplashOptions
{
    public bool Enabled { get; init; } = true;                                    // false: see "Turning it off"
    public TimeSpan BrutalLogoDuration { get; init; } = TimeSpan.FromSeconds(2);  // at least 2 s
    public IReadOnlyList<SplashLogo> GameLogos { get; init; } = [];               // shown after BRUTAL's, in order
    public TimeSpan GameLogoDuration { get; init; } = TimeSpan.FromSeconds(2);    // each; 0 < d <= BrutalLogoDuration
}

public sealed class SplashLogo                       // immutable; thread-safe
{
    public static Result<SplashLogo> FromPng(ReadOnlySpan<byte> png);            // InvalidData, Unsupported
    public static SplashLogo FromRgba(int width, int height, ReadOnlySpan<byte> rgba); // straight alpha, sRGB
    public int Width { get; }
    public int Height { get; }
}
```

**What is promoted from HelloWindow** (all become `internal` in Runtime, with their tests moved from `tests/HelloWindow.Tests`):

| M0 type | In Runtime |
|---------|-----------|
| `SplashLayout.Fit` (`samples/HelloWindow/Splash/SplashLayout.cs:19`) | Unchanged: centred, scaled down to fit inside a 32-pixel margin (`samples/HelloWindow/HelloWindowApp.cs:15`), never up, aspect kept |
| `SplashTimeline` (`samples/HelloWindow/Splash/SplashTimeline.cs:70`) | Unchanged rules: `MinimumSeconds = 2.0` (`:20`), headless runs count `HeadlessFrames = 120` (`:27`). The minimum and frame count come from `BrutalLogoDuration` (frames: `ceil(seconds * 60)`), one timeline per logo |
| `LogoCompositor.Composite` (`samples/HelloWindow/Splash/LogoCompositor.cs:22`) | Unchanged: composite onto black once at load, so drawing needs no blending |
| `SplashStatistics.Presented` (`samples/HelloWindow/Splash/SplashStatistics.cs:24`) | Unchanged: the exit log line `splash: X frames over Y s, scene: Z frames` |
| `SplashCapabilities.Choose` (`samples/HelloWindow/Splash/SplashCapabilities.cs:26`) | **Not promoted.** It picks between `vkCmdBlitImage` and `vkCmdCopyImage` on Vulkan format features (`using Brutal.VulkanApi;`, `samples/HelloWindow/Splash/SplashCapabilities.cs:1-2`). The render interface has no blit, and drawing a quad works on every swapchain (below), so there is no capability to choose. Its role, failing set-up loudly instead of skipping, stays: any splash set-up failure is a fatal `Splash` step |
| `SplashLogo.Load` (SDL_LoadPNG, `samples/HelloWindow/Splash/SplashLogo.cs:38`) | Replaced by a managed PNG decoder (below): Runtime may not call native code |

**Decoding.** Native interop is for backend projects only ([Coding standards](../07-coding-standards.md#native-interop-rules-engine-side)), so Runtime can't call `SDL_LoadPNG`. It decodes PNGs itself: an internal decoder over the BCL's `ZLibStream`, for 8-bit truecolour and truecolour-with-alpha (colour types 2 and 6), non-interlaced, all five filter types, CRC-checked. That is exactly what the BRUTAL logo is (712x168, 8-bit RGBA, non-interlaced), and the documented limit for game logos; anything else returns `Unsupported`, and a corrupt file `InvalidData`. No new dependency. (The alternative, an image-decoding member on `IPlatform`, would change the platform note; cooked textures replace both in M2.)

**Drawing through the render interface.** At the `Splash` step the host composites each logo onto black (`LogoCompositor`, RGBA order), uploads it once with `WriteTexture` into a `Sampled | CopyDestination` texture, and creates one pipeline: a quad from the vertex index, a linear sampler, opaque output, and push constants holding the destination rectangle. Its SPIR-V is cooked from a `splash.slang` source by `tools/BrutalEngine.Cook` and embedded in Runtime ([ADR-0012](../adr/0012-m1-shader-cooking.md)). Each splash frame begins a render pass on the acquired image with `LoadOp.Clear` to opaque black and draws the logo into `SplashLayout.Fit`'s rectangle. The logo texture is `Rgba8UnormSrgb` on an sRGB swapchain and `Rgba8Unorm` on a UNORM one, so a 1:1 draw at the fitted (integer) rectangle samples texel centres and should reproduce the composited bytes exactly, as M0's blit did on the reference machine (`samples/HelloWindow/README.md:69`); R3's GPU test checks it. The pipeline is recreated when `Recreated` reports a new swapchain format. The splash registers as an `IGpuResourceOwner` (`docs/design/graphics.md:750`), so device loss during the splash recreates the texture and pipeline and the timeline carries on.

**Timing and skipping.**

- The BRUTAL logo comes first and lasts `BrutalLogoDuration` (at least 2.0 s, `ArgumentOutOfRangeException` below that). Nothing skips it before then. When its time is up it ends by itself; with a longer `BrutalLogoDuration`, a key press, mouse button or (later) gamepad button ends the rest of it, which is docs/01's "skippable-after-legible splash" (`docs/01-vision-and-scope.md:77`).
- Windowed runs time each logo on a **splash clock that advances only between presented frames**: the sum of `FrameTiming.Delta` over frames that presented it. M0 timed from the first frame by the wall clock, so a window minimised during the splash used up the logo's time unseen; pausing with the window closes that gap. `Offscreen` runs count frames (the first 120 at the default, `samples/HelloWindow/HelloWindowApp.cs:112`), so CI exercises the splash and the game deterministically.
- **Game logos** follow, each for `GameLogoDuration` and skippable at any time. They are drawn by the same code and fitted the same way, then scaled down further if needed so that each covers no more pixels than the BRUTAL logo did in the same window, and no game logo lasts longer than BRUTAL's: the BRUTAL logo is then never less prominent than another party's (LICENSE 2.2).
- **`NoSurface` runs** have no splash: the product "is not capable of displaying graphics" there, the exception LICENSE 2.2 itself names.

**Turning it off.** The splash is on by default and configurable (docs/03 3.23). `Enabled = false` skips it, logs a `Warning` naming BRUTAL LICENSE 2.2, and is the game developer's decision and responsibility, as docs/01 says. There is no command-line option for it, so a player can't remove it.

**Shipping the logo.** `src/BrutalEngine.Runtime/Splash/LOGO_BRUTAL_WHT.png`, byte-for-byte BRUTAL's `docs/branding/LOGO_BRUTAL_WHT.png` (SHA-256 `6e259ecd95b6e0cbc52744b59ffeee3646be9ba4d8b640d9864938c49fdb9556`), is embedded in `BrutalEngine.Runtime.dll` as a manifest resource, so it can't be missing from a build, and a test pins the hash. HelloWindow keeps its copy until the rewrite (S1) deletes it.

### Licence duties

| Duty | Met by |
|------|--------|
| BRUTAL LICENSE 2.2: the logo, before gameplay, unaltered but for scaling, legible, no less prominent than other logos (BRUTAL `LICENSE.txt:23-28`) | [The splash](#5-the-splash): drawn before `IGame.Initialize`, embedded byte for byte, scaled only, at least 2.0 s of presented frames, game logos capped. Turning it off is logged |
| BRUTAL LICENSE 2.3: `NOTICE.txt` alongside the product (`LICENSE.txt:30-31`) | Every engine package already carries `NOTICE.txt` (`src/Directory.Build.props:51-52`). Copying it next to a game's executable is the publish step's job, M2 (`docs/06-ci-cd-and-release.md:129`); in M1 the samples do it with their own target (`samples/HelloWindow/HelloWindow.csproj:36-122`) |
| BRUTAL LICENSE 1.0: the copyright and permission notice in all copies (`LICENSE.txt:12-13`) | BRUTAL's packages carry `LICENSE.txt`; copied into game output by the M2 publish step, by the samples' target in M1 |
| BRUTAL LICENSE 2.1: the copyright notice in the credits (`LICENSE.txt:20-21`) | The credits API (`docs/03-architecture.md:257`), not in M1: it lands in Runtime with M3's UI, which can show a credits screen. `CREDITS.txt` generation moves from HelloWindow into the M2 publish step |
| BRUTAL LICENSE 4.0: third-party licences in `LICENSES/` (`LICENSE.txt:41-43`) | The M2 publish step (`docs/03-architecture.md:258`); the samples' target in M1 |
| docs/03 3.23: splash module, credits API, publish copies and a test | Splash: [this section](#5-the-splash). Credits: M3. Publish copies and their test: M2 |

No engine package is published to NuGet in M1, so every M1 product is a sample built in this repository, and the samples' targets meet 1.0, 2.1, 2.3 and 4.0 until the M2 publish step replaces them.

## 6. Crash reports

docs/03 (`docs/03-architecture.md:108`): an unhandled exception is caught at the host boundary, a crash report is written to the user data directory, an OS message box is shown, and the process exits non-zero.

```csharp
public sealed record CrashReportOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxReports { get; init; } = 10;           // newest kept; 1..1000
    public bool ShowMessageBox { get; init; } = true;    // never when headless or under CI
}
```

- **Caught where.** `Execute` wraps everything after `Configure` in one `try`/`catch` on the main thread, so exceptions from game code, systems, modules and the engine all arrive there. `OutOfMemoryException` is caught too, on a best-effort basis. A stack overflow can't be caught by .NET and isn't reported. Exceptions on other threads reach an `AppDomain.UnhandledException` handler that the host registers at `Logging` and removes at exit. It writes the report from that thread with no message box, and then the runtime ends the process with its own exit code, not 70. (M1 starts no engine threads; BRUTAL's and the BCL's might.)
- **What is safe after a crash.** Nothing that might touch the GPU or game state: no `IGame.Shutdown`, no module `Stop`, no world access, no GPU call through `GraphicsDevice` (a lost or hung device can block `WaitIdle` forever), and no platform or device disposal (the surface must go before the window, and the device might hang). The host writes the report from data it already holds, shows the message box, flushes and disposes the log sinks, and returns `Crashed` (70); process exit reclaims the rest. A crash during shutdown follows the same rule.
- **Contents** (UTF-8 text, so a player can read and paste it):

```text
BrutalEngine crash report
Application: <Organization>/<Application> <ApplicationVersion>
Engine: BrutalEngine.Runtime <informational version>
Time: <UTC, ISO 8601>   Run: <run id>
Step: <HostStep>   Phase: <HostPhase>   Frame: <FrameNumber>

== Exception ==        Exception.ToString(): type, message, stack, inner exceptions (or "== Error ==": a HostFailure)
== System ==           OS description and architecture, .NET version, processor count, video driver, headless mode,
                       window pixel size, graphics adapter (name, driver, API version), all cached at start-up
== Frame timings ==    the last 120 frame deltas in ms, FixedUpdateRate, SpiralCount, StepCount
== Log tail ==         MemoryLogSink.CopyTo, oldest first, one "timestamp LEVEL [Category] message" line each
== Log file ==         its path, or the file sink's Fault
```

- The log tail is read with `MemoryLogSink.CopyTo(Span<MemoryLogRecord>, Memory<byte>)` (`src/BrutalEngine.Core/Logging/Sinks/MemoryLogSink.cs:164`) into buffers of `CapacityEntries` records and `CapacityBytes` bytes, which always receive everything ([logging note](logging.md#memorylogsink)). Allocating on this path is fine. The frame timings come from a ring of 120 `long`s the loop fills, allocated at start-up. The report doesn't try to say whether the process runs JIT or NativeAOT: no reliable runtime check exists.
- **Location.** `<user data>/crashes/crash-<yyyyMMdd-HHmmss>-<run id>.txt`, written with `System.IO`, not the VFS, for the same reason as the log file: it must work while things are failing (`docs/design/platform.md:447`). Without a user data directory, the fallback directory of section 7. After writing, only the newest `MaxReports` files matching `crash-*.txt` are kept.
- **Message box**, only after the report is on disk (platform note section 8): `IPlatform.ShowMessageBox` with `MessageBoxKind.Error`, the title "<Application> stopped unexpectedly", and the report's path; or `SdlPlatform.TryShowFatalError` if the platform never started or the default platform isn't in use. Skipped when headless, when `CI` is set, or when `ShowMessageBox` is false.
- **Exit code** `Crashed` (70). `HostRunResult` carries the exception and the report path.

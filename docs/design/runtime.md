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
    public string? UserDataDirectory { get; init; }            // fully qualified; replaces SDL_GetPrefPath
    public string? ContentDirectory { get; init; }             // fully qualified res:// root; null: IPlatform.BaseDirectory
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
    public string? VulkanLoaderPath { get; init; }             // fully qualified; see "Backends" below
}

public sealed record HostBackends                              // backends are chosen here (Architecture 3.1)
{
    public Func<PlatformOptions, Result<IPlatform>>? CreatePlatform { get; init; } // null: SDL3
    public IGraphicsBackend? Graphics { get; init; }           // null: Vulkan
    public Func<string, string, bool>? ShowFatalError { get; init; } // title, message; null: see section 2
}
```

"Fully qualified" means `Path.IsPathFullyQualified` is true, as the Vulkan backend note requires for its paths: on Windows, `\x` and `C:x` are rooted but not fully qualified. Any other path throws `ArgumentException` (from code) or is a usage error (from the command line).

**Backends.** M1 has one backend of each kind, so a game never names one; `HostBackends` exists for the headless host and for servers. With the defaults, the host first calls `VulkanBackend.Create(new VulkanBackendOptions { AppDirectory = AppContext.BaseDirectory, Logger = logger, LoaderPath = Graphics.VulkanLoaderPath })`, the factory of the Vulkan backend note, `docs/design/graphics-vulkan.md` (#79, section "The backend's public surface"). `AppContext.BaseDirectory` is used because `IPlatform.BaseDirectory` doesn't exist yet at that step. The host passes the result's `LoaderPath` (the preloaded loader's absolute path, or null for the system loader) unchanged to `SdlPlatform.Create` as `SdlPlatformOptions.VulkanLibraryPath`, so SDL and BRUTAL load one library (`docs/design/platform.md:577`). A custom `CreatePlatform` gets no loader path. Under `HeadlessMode.NoSurface` no graphics backend is created at all, as #79 allows for a host without graphics.

**Command line.** `CommandLine` holds the arguments the game wants the engine to read; a game that has its own options removes them first. Each engine option may appear once. An unknown option, a repeated one, or a bad value is a usage error: the host prints the error and the usage text to standard error and returns exit code 64 before anything starts. Names match exactly (ordinal), and enum-like values match one listed token, never `Enum.TryParse`.

| Option | Sets | Notes |
|--------|------|-------|
| `--headless` | `Headless = Offscreen` | HelloWindow's flag (`samples/HelloWindow/HelloWindowOptions.cs:13-25`) |
| `--no-graphics` | `Headless = NoSurface` | servers and simulation tests: no window, no device, no splash |
| `--frames <n>` | `FrameLimit` | `n` from 1 to `int.MaxValue` |
| `--size <w>x<h>` | `Window.ContentSize` | content units, each side 1 to 16384 |
| `--gpu <selection>` | `Graphics.Adapter` | `AdapterSelection.TryParse` (`docs/design/graphics.md:140`) |
| `--validation` | `Graphics.Validation = true` | |
| `--vulkan-loader <path>` | `Graphics.VulkanLoaderPath` | fully qualified |
| `--log-level <level>` | `Logging.MinimumLevel` | `trace`, `debug`, `information`, `warning`, `error`, `critical` |
| `--user-data <dir>` | `UserDataDirectory` | fully qualified; CI and portable installs |

HelloWindow's test-only flags (`--exercise-resize`, `--require-cpu`, `--require-driver`, `--inject-validation-error`) stay in the sample, which maps what it can onto these when it is rewritten (S1).

**Config file.** After the platform starts, the host reads `user://engine.json` if it exists: one JSON object of dotted keys with string or boolean values, read with `Utf8JsonReader` (no reflection, NativeAOT-safe). Keys: `window.size` (`"1280x720"`), `window.mode` (`"windowed"` or `"borderless"`), `graphics.adapter`, `graphics.presentMode` (`"fifo"`, `"fifo-relaxed"`, `"mailbox"`, `"immediate"`), `graphics.validation`, `log.level`. **Precedence:** `EngineOptions` from code, then the file, then the command line. The file belongs to the player, so a bad file never stops the game: an unreadable file, invalid JSON, an unknown key or a bad value is logged as a `Warning` and that key (or the whole file) is ignored. Two keys are valid but can still fail later, and degrade instead: a `graphics.adapter` from the file that matches no suitable adapter (checked with `GraphicsDevice.EnumerateAdapters` before `Create`) is replaced by `AdapterSelection.Auto`, and a `graphics.validation: true` from the file whose `Create` fails with `Unavailable` is retried once without validation. Each logs a `Warning` and is recorded as a degradation of the `GraphicsDevice` step. The same values from code or the command line stay fatal, as the graphics note requires (`docs/design/graphics.md:140`: an override never falls back silently). Options that act before the platform exists (`Headless`, `UserDataDirectory`, the loader path) are code and command line only.

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
    public string? CrashReportPath { get; init; }               // null: no crash, reports disabled, or the write failed
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
    World = 95,
    Modules = 100, Splash = 110, GameInitialize = 120, Running = 130,
    GameShutdown = 140, ModulesStop = 150, Teardown = 160, LeakCheck = 170,
}
```

| Step | Does | Failure | Class |
|------|------|---------|-------|
| `Configure` | Validate options, parse the command line | Usage error (exit 64) | fatal |
| `Logging` | Logger with console and memory sinks (section 7) | none (misuse only) | |
| `GraphicsBackend` | `VulkanBackend.Create`: loader choice and preload (skipped for `NoSurface`) | "Vulkan loader missing", a loader BRUTAL didn't bind (`Unavailable`); `setenv` failed (`Native`) | fatal |
| `Platform` | `SdlPlatform.Create` with the loader path and `PlatformOptions.Logger` | No video driver, headless mode unavailable (`Unsupported`, `Native`) | fatal |
| `UserData` | `GetUserDataDirectory`, mount `res://` and `user://`, read `engine.json`, attach the log file | `Io` | **degradable**: a fallback directory (section 7), no `user://` mount |
| `Window` | `CreateWindow(Window)` (skipped for `NoSurface`) | `Native`, `Unsupported`, `NotFound` | fatal |
| `GraphicsDevice` | `GraphicsDevice.Create` with the window as `CompatibleWindow`, then `CreateSwapchain` | "No Vulkan 1.3 GPU found" (`Unsupported`), validation layer missing (`Unavailable`) | fatal (degradable only for `engine.json` values, section 1) |
| `World` | `new EcsWorld(World with { Allocator, Clock })`: the engine allocator and `IPlatform.Clock` | misuse only | |
| `Modules` | `IGame.ConfigureModules`, then start every module in dependency order (section 3) | the module's `Result` | per module |
| `Splash` | Decode the logos, create the splash's GPU resources (section 5) | `Unsupported`, `OutOfMemory` | fatal (a licence duty is never skipped silently, `samples/HelloWindow/HelloWindowApp.cs:259-263`) |
| `GameInitialize` | `IGame.Initialize` (after the splash frames, section 4), then `Build()` on `FixedUpdate`, `Update` and `PostUpdate` | the game's `Result`; a `Build` error is misuse (a crash) | fatal |
| `Running` | The frame loop | device lost and `Recover()` failed (`docs/design/graphics.md:748`), or a `TimedOut` loss (section 4) | fatal: exit 4 |
| `GameShutdown`, `ModulesStop` | `IGame.Shutdown`, then each started module's `Stop` in reverse start order | exceptions are logged and shutdown continues | |
| `Teardown` | Splash resources, the world (`EcsWorld.Dispose`), the swapchain, the device, the window, the platform, in that order | none | |
| `LeakCheck` | `GraphicsDevice.ThrowIfLeaks()` after the swapchain is destroyed and before the device's `Dispose`; `TrackingAllocator.ThrowIfLeaks()` after the world and the device are disposed | leaks: logged at `Error`, exit 4 | when `LeakCheck` |

- **Fatal vs degradable** follows docs/03: "Audio is degradable. Graphics is fatal." (`docs/03-architecture.md:53`). A fatal failure is logged at `Critical`, shown in a message box with its user-facing message (the rule below), and ends the run with exit 3 before `Running`, or 4 from it. A degradable failure is logged at `Warning`, recorded in `EngineContext.Degradations`, and the run continues without that service.
- **Which message box.** No box when the platform is headless, when the `CI` environment variable is set, or (for crashes) when `CrashReportOptions.ShowMessageBox` is false. Otherwise a live platform, default or custom, always gets `IPlatform.ShowMessageBox` with `MessageBoxKind.Error`. When there is no live platform (it never started, or it is already disposed), the host calls `HostBackends.ShowFatalError` if set; if it is null and the default SDL platform was requested, `SdlPlatform.TryShowFatalError` (`docs/design/platform.md:469`); with a custom platform and no `ShowFatalError`, nothing. The headless host sets `ShowFatalError` to its recorder, so tests never open a real dialog. A box that fails is logged and ignored.
- **Partial teardown.** A failure at any step undoes exactly the steps that completed, in reverse: modules that started are stopped, the world is disposed, the device is disposed before the window and the window before the platform (the surface rule of `docs/design/platform.md`, [section 4](platform.md#4-vulkan-surface-source)), and the logger last. A step that never ran is never undone. After a `TimedOut` device loss the GPU steps are abandoned instead of undone (section 4).
- **Reserved steps.** The audio device and the asset database are absent in M1. Their steps are numbered 80 and 90, between `GraphicsDevice` and `Modules`, so they will start in the docs/03 order without renumbering `HostStep` (its values are spaced for that) and their services will be ready before any module starts. Audio will be degradable ("continuing without audio"), the asset database fatal.
- **`NoSurface` runs** skip `GraphicsBackend`, `Window`, `GraphicsDevice` and the splash; the loop simulates without rendering.
- **The world is the host's.** It is created at `World`, before any module starts, so modules and the game can register systems into it, and disposed in `Teardown` after every module has stopped and before the leak check. Its allocator is the engine allocator (tracked when the leak check is on); its clock is the platform's.
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
    public void MarkFrameNonSteady();                // this frame did load-time work: leave it out of the measurement
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
    public FixedStepClock(FrameLoopOptions options, long timestampFrequency, long startTimestamp);
    public FrameTiming Advance(long timestamp);   // once per frame: measures from the baseline, then moves it
    public void Rebase(long timestamp);           // new baseline, accumulator cleared: resume without catching up
    public TimeSpan FixedDelta { get; }           // 1 / FixedUpdateRate, rounded to 100 ns
    public long FrameCount { get; }
    public long StepCount { get; }
    public long SpiralCount { get; }              // frames whose delta hit MaxFrameDelta
}

public readonly record struct FrameTiming(int Steps, float Alpha, TimeSpan Delta, bool Clamped);
```

- **State:** a `baseline` timestamp (`startTimestamp` at construction) and an `accumulator`, both `long`. The accumulator counts timestamp ticks multiplied by the rate, so one fixed step is exactly `timestampFrequency` units and 60 steps per second need no rounded 1/60 s. `maxTicks = MaxFrameDelta` in timestamp ticks, rounded down.
- **`Advance(t)`**, exactly: `elapsed = t - baseline`; `baseline = t`; `Clamped = elapsed > maxTicks`; `used = min(elapsed, maxTicks)`; `accumulator += used * FixedUpdateRate`; `Steps = accumulator / timestampFrequency` (integer division); `accumulator -= Steps * timestampFrequency` (the remainder is kept for the next frame); `Alpha = accumulator / timestampFrequency` as a `float` in `[0, 1)`, the interpolation factor between the last two fixed states (docs/03, `docs/03-architecture.md:58-70`); `Delta = used` as a `TimeSpan`. `FrameCount` grows by 1, `StepCount` by `Steps`, and `SpiralCount` by 1 when `Clamped`. There is no special first call: an `Advance` at the baseline's own timestamp returns 0 steps because no time has passed.
- **`Rebase(t)`** sets `baseline = t` and `accumulator = 0`, and changes no counter. The time before `t` is never simulated, and the next `Advance` measures from `t`.
- **The property R2 tests:** over the `Advance` calls since construction or the last `Rebase`, with `used_i` as above, `StepCount` grew by exactly `floor(sum(used_i) * FixedUpdateRate / timestampFrequency)` and the accumulator holds the remainder, `sum(used_i) * FixedUpdateRate mod timestampFrequency`. So steps never drift, however the time is split into frames.
- **Spiral-of-death protection:** clamping each frame's delta to `MaxFrameDelta` before it is accumulated caps one frame at `ceil(MaxFrameDelta * rate)` steps (15 at the defaults; the carried remainder can add at most one). `SpiralCount`, the number of clamped frames, is what the overlay shows (`docs/03-architecture.md:74`).
- **Misuse:** a `t` before the baseline throws `ArgumentOutOfRangeException` (from `Advance` and `Rebase`); options outside the ranges above (including `MaxFrameDelta` shorter than one step, `maxTicks * rate < timestampFrequency`) and a non-positive frequency throw from the constructor, which also rejects values for which `maxTicks * rate + timestampFrequency` would overflow a `long`.
- **Injected clock:** the host reads `IPlatform.Clock.GetTimestamp()` (the BCL `TimeProvider`, [platform note section 7](platform.md#7-clock)), so tests drive the whole loop with scripted timestamps.
- Not thread-safe (the loop owns it); `Advance` and `Rebase` allocate nothing.

### One frame

```text
while not exit requested and FrameLimit not reached:
    platform.PumpEvents()                                  # main thread
    QuitRequested, or WindowCloseRequested for the main window -> request exit
    if window minimised (State has Minimized or a zero PixelSize):
        platform.WaitForEvents(1 s) until restored or exit requested; frameClock.Rebase(now); continue
    timing = frameClock.Advance(platform.Clock.GetTimestamp())
    modules.BeginFrame
    if phase == Running:
        repeat timing.Steps: world.RunSchedule(FixedUpdate, frameClock.FixedDelta)
        world.RunSchedule(Update, timing.Delta)
        world.RunSchedule(PostUpdate, timing.Delta)        # the transform system (world note)
    if graphics:
        graphics.BeginFrame(out commands)                  # DeviceLost: see below
        if phase == Running: modules.Extract(alpha); modules.Prepare
        status = commands.AcquireSwapchainImage(swapchain, out image)   # after Prepare: only Record needs it
        if phase == Splash: splash.Record(commands, image) else modules.Record(commands, image)
        if phase == Running: overlay.Record (development builds; never over the splash)
        graphics.SubmitFrame(commands)
    else:
        platform.WaitForEvents(time until the next fixed step)   # NoSurface pacing
    modules.EndFrame
    phase transitions (below)
```

- **Phases.** The loop starts in `Splash`: events are pumped and frames presented, but no schedule runs and no module renders, because the game isn't initialised. When the splash completes (section 5), the host calls `IGame.Initialize` between two frames, builds the three schedules (so no `Build` allocates inside a measured frame), rebases the clock (initialisation time is not simulated), and switches to `Running`. A run without a splash (`NoSurface`, or the splash disabled) goes straight to `Running`.
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

- **Swapchain results** (`AcquireStatus`, [graphics note section 3](graphics.md#3-swapchains)): `Recreated` reaches modules through `RenderTarget.Status` so they resize their targets; `Minimised` and `Skipped` have no image but the frame is still submitted, because a frame always completes (`docs/design/graphics.md:284-285`); `SurfaceLost` makes the host destroy and recreate the swapchain after `SubmitFrame`; `DeviceLost` is handled below. A frame is **presented** when its acquire returned `Acquired` or `Recreated` and its `SubmitFrame` returned `Ready`; only presented frames count for the splash (section 5).
- **Device loss.** When `BeginFrame`, `AcquireSwapchainImage` or `SubmitFrame` reports `DeviceLost`, the host finishes the frame (encoder calls in a lost frame are no-ops) and reads the `DeviceLost` event's `TimedOut` (`docs/design/graphics.md:732`).
  - **A reported loss** (`TimedOut` false): the host calls `GraphicsDevice.Recover()` before the next frame. Owners (the splash, the overlay, game renderers) recreate their resources through `IGpuResourceOwner` ([graphics note section 12](graphics.md#12-device-lost)). If recovery fails, the host writes a crash report with the error in place of an exception (section 6), then shuts down normally and exits 4. That shutdown is safe on a device the driver reported lost: `vkDeviceWaitIdle` returns `VK_ERROR_DEVICE_LOST` instead of waiting, and the backend ignores it and destroys its objects (`docs/design/graphics-vulkan.md`, #79, sections 5 and 12).
  - **A hung GPU** (`TimedOut` true, a fence wait ran out of `GpuTimeout`): no `Recover()`, because it disposes the old device, and no normal device disposal either. Only fence waits are bounded by `GpuTimeout` (`docs/design/graphics.md:113`); `GraphicsDevice.Dispose` waits for idle (`docs/design/graphics.md:80`) and the backend's `Dispose` calls `vkDeviceWaitIdle`, which has no timeout (#79, section 5), so on a hung GPU either could block forever. The host writes the crash report, runs `IGame.Shutdown` and the modules' `Stop` (which must not touch the device), then **abandons** the device, the swapchain, the window and the platform on purpose (the surface rule forbids disposing the window under a live device), logs at `Error` that it did, and exits 4. Process exit reclaims them. Neither note specifies this; it is the host's rule.
- **Minimised windows.** The loop blocks in `WaitForEvents` (as HelloWindow does, `samples/HelloWindow/HelloWindowApp.cs:142-151`), so simulation pauses with rendering, and `Rebase` drops the paused time instead of clamping it into a spiral (the spiral counter doesn't move). Running simulation while minimised (dedicated servers, networking in M5) is a later option behind `FrameLoopOptions`.
- **Frame limit.** `FrameLimit` counts frames that ran (rendered, or simulated under `NoSurface`), splash frames included, as HelloWindow's `--frames` does. Minimised waits don't count.
- **Zero allocation.** A steady-state frame allocates no managed memory: the loop has no LINQ, closures or string building; module and frame-hook lists are arrays; `FrameContext` and `RenderTarget` are passed by `in`; `FixedStepClock`, the platform pump, the render-interface frame calls and `Schedule.Run` are all allocation-free by their notes. The host measures every iteration with `GC.GetAllocatedBytesForCurrentThread()` and adds it to `SteadyStateAllocatedBytes` unless the frame is **non-steady**. The host marks a frame non-steady itself when it is the first frame of a phase, when it recreated a swapchain or recovered the device, when it pumped a display event (`DisplayAdded`, `DisplayRemoved`, `DisplayContentScaleChanged` or `DisplayCurrentModeChanged`, which rebuild `IPlatform.Displays` and allocate, `docs/design/platform.md:46`), and when a schedule's `SystemCount` differs from the previous frame's (an added system makes the next `Run` rebuild, which allocates). Game and module code calls `EngineContext.MarkFrameNonSteady()` for a frame in which it did load-time work: creating GPU objects, adding sync points, loading. The total and the steady frame count go into `HostRunResult` and the exit log line (`managed bytes allocated over N steady-state frames`, the line HelloWindow's CI already reads).

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
| `SplashTimeline` (`samples/HelloWindow/Splash/SplashTimeline.cs:70`) | **Promoted and changed**, so its tests move and change too. Kept: the 2.0 s floor (`MinimumSeconds`, `:20`), counting frames when headless (`HeadlessFrames = 120` at the default, `:27`), never ending early, never coming back once complete. Changed: (1) one timeline per logo, with its duration from `SplashOptions` (frames when headless: `ceil(seconds * 60)`); (2) windowed timelines are fed the presented-time clock below instead of raw timestamps, so `ShowsSplash(frame, timestamp)` becomes `Presented(TimeSpan delta)` plus `IsComplete`; (3) `Skip()`, which ends the logo only once its 2.0 s floor has passed (the BRUTAL logo) or at any time (game logos) |
| `LogoCompositor.Composite` (`samples/HelloWindow/Splash/LogoCompositor.cs:22`) | Unchanged: composite onto black once at load, so drawing needs no blending |
| `SplashStatistics.Presented` (`samples/HelloWindow/Splash/SplashStatistics.cs:24`) | Unchanged: the exit log line `splash: X frames over Y s, scene: Z frames` |
| `SplashCapabilities.Choose` (`samples/HelloWindow/Splash/SplashCapabilities.cs:26`) | **Not promoted.** It picks between `vkCmdBlitImage` and `vkCmdCopyImage` on Vulkan format features (`using Brutal.VulkanApi;`, `samples/HelloWindow/Splash/SplashCapabilities.cs:1-2`). The render interface has no blit, and drawing a quad works on every swapchain (below), so there is no capability to choose. Its role, failing set-up loudly instead of skipping, stays: any splash set-up failure is a fatal `Splash` step |
| `SplashLogo.Load` (SDL_LoadPNG, `samples/HelloWindow/Splash/SplashLogo.cs:38`) | Replaced by a managed PNG decoder (below): Runtime may not call native code |

**Decoding.** Native interop is for backend projects only ([Coding standards](../07-coding-standards.md#native-interop-rules-engine-side)), so Runtime can't call `SDL_LoadPNG`. It decodes PNGs itself with an **internal** `PngDecoder` over the BCL's `ZLibStream`; no new dependency. The decoder is internal: its only public entry is `SplashLogo.FromPng`, which games need because in M1 Runtime is the only engine code that can turn a PNG into pixels without native calls (cooked textures replace it in M2). The alternative, an image-decoding member on `IPlatform`, would change the platform note. `FromPng` takes untrusted bytes, so the decoder is bounded:

| Rule | Failure |
|------|---------|
| Input at most 64 MiB; the 8-byte signature first | `Unsupported` / `InvalidData` |
| Chunks: each length at most 2³¹ - 1 and inside the input; every CRC-32 checked, ancillary chunks included; `IHDR` first (length 13, once), `IDAT` chunks contiguous with at least one, `IEND` last with length 0 and nothing after it | `InvalidData` |
| `IHDR`: bit depth 8, colour type 2 (RGB) or 6 (RGBA), interlace 0 | others `Unsupported`; compression or filter method not 0, a zero size: `InvalidData` |
| Width and height at most 8,192 each, and `width × height` at most 16,777,216 pixels (64 MiB of RGBA), computed with checked arithmetic **before** any pixel buffer is allocated | `Unsupported` |
| The concatenated `IDAT` data is inflated into a buffer of exactly `height × (1 + width × bytesPerPixel)` bytes; the stream is then read to its end, which makes zlib verify the Adler-32 checksum, and any further output byte, or `IDAT` data left after the zlib stream's end, is an error. A decompression bomb can therefore never produce more than that buffer | short, long or corrupt output: `InvalidData` (`ZLibStream`'s `InvalidDataException` is caught and mapped) |
| Filter types 0-4 per row; any other filter byte | `InvalidData` |
| Unknown critical chunks (upper-case first letter) other than `PLTE`, which is ignored for these colour types | `Unsupported` |
| `tRNS` on colour type 2: pixels equal to its RGB value get alpha 0, the others 255 (`tRNS` on type 6 is invalid PNG: `InvalidData`). `gAMA`, `cHRM`, `iCCP`, `sRGB`, `sBIT` and other ancillary chunks are skipped: pixels are taken as sRGB-encoded, which is what `LogoCompositor` assumes | as stated |

The BRUTAL logo is 712x168, 8-bit RGBA, non-interlaced and `IDAT`-only, so it is well inside these rules.

**Drawing through the render interface.** At the `Splash` step the host composites each logo onto black (`LogoCompositor`, RGBA order), uploads it once with `WriteTexture` into a `Sampled | CopyDestination` texture, and creates one pipeline per colour-target format: a quad from the vertex index, a linear sampler, opaque output, and push constants holding the destination rectangle. The internal splash renderer records into any colour-target view with its size and format; the loop gives it the acquired swapchain image, and the GPU test gives it an offscreen texture (section 11). Its SPIR-V is cooked from a `splash.slang` source by `tools/BrutalEngine.Cook` and embedded in Runtime ([ADR-0012](../adr/0012-m1-shader-cooking.md)). Each splash frame begins a render pass on the acquired image with `LoadOp.Clear` to opaque black and draws the logo into `SplashLayout.Fit`'s rectangle. The logo texture is `Rgba8UnormSrgb` on an sRGB swapchain and `Rgba8Unorm` on a UNORM one, so a 1:1 draw at the fitted (integer) rectangle samples texel centres and should reproduce the composited bytes exactly, as M0's blit did on the reference machine (`samples/HelloWindow/README.md:69`); the splash GPU test checks it. **Precision policy:** UNORM targets must match byte for byte. The Vulkan specification allows a small error in sRGB conversion, so on sRGB targets the test accepts a difference of at most 1 per channel, with that reason in a comment next to the tolerance; anything more fails. No other tolerance is allowed. The pipeline is recreated when `Recreated` reports a new swapchain format. The splash registers as an `IGpuResourceOwner` (`docs/design/graphics.md:750`), so device loss during the splash recreates the texture and pipeline and the timeline carries on.

**Timing and skipping.**

- The BRUTAL logo comes first and lasts `BrutalLogoDuration` (at least 2.0 s, `ArgumentOutOfRangeException` below that). Nothing skips it during its first 2.0 s of presented time. When its time is up it ends by itself; with a longer `BrutalLogoDuration`, a key press, mouse button or (later) gamepad button ends the rest of it, which is docs/01's "skippable-after-legible splash" (`docs/01-vision-and-scope.md:77`).
- Windowed runs time each logo on a **splash clock that advances only between presented frames**: the sum of `FrameTiming.Delta` over frames that presented it (acquire `Acquired` or `Recreated`, and `SubmitFrame` `Ready`: section 4). Headless frame counts count the same frames. M0 timed from the first frame by the wall clock, so a window minimised during the splash used up the logo's time unseen; pausing with the window closes that gap. `Offscreen` runs count frames (the first 120 at the default, `samples/HelloWindow/HelloWindowApp.cs:112`), so CI exercises the splash and the game deterministically.
- **Game logos** follow, each for `GameLogoDuration` and skippable at any time. The debug overlay never draws during the splash (section 9), so nothing covers a logo. They are drawn by the same code and fitted the same way, then scaled down further if needed so that each covers no more pixels than the BRUTAL logo did in the same window, and no game logo lasts longer than BRUTAL's: the BRUTAL logo is then never less prominent than another party's (LICENSE 2.2).
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
| docs/03 3.23: splash module, credits API, publish copies and a test | Splash: [this section](#5-the-splash) and [testing](#11-testing). Credits: M3. Publish copies and their test: M2 |

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
- **What is safe after a crash.** Nothing that might touch the GPU or game state: no `IGame.Shutdown`, no module `Stop`, no world access, no GPU call through `GraphicsDevice` (device disposal waits for idle without a bound, section 4), and no platform or device disposal (the surface must go before the window, and the device might hang). The host writes the report from data it already holds, shows the message box, flushes and disposes the log sinks, and returns `Crashed` (70); process exit reclaims the rest. A crash during shutdown follows the same rule.
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
- **Message box**, only after the report is written or has failed (platform note section 8), chosen by section 2's rule: the title "<Application> stopped unexpectedly", and the report's path, or "No crash report could be written; see the log." when there is none.
- **Each stage is guarded on its own**, so the crash path never throws out of `Execute`:
  - *Building the text:* each section is built in its own `try`. If `Exception.ToString()` throws (game exception types can override it), the section holds the exception's type name, its `Message` if that can be read, and a line saying `ToString` threw, with that exception's type. Any other failing section is replaced by one line naming the failure.
  - *Writing:* creating the directory, writing the file and trimming old reports are one guarded stage. On failure, `CrashReportPath` is null and the reason goes to standard error (itself guarded) and the log.
  - *The message box:* a failed or throwing call is ignored after a line on standard error.
  - *Flushing and disposing the log sinks:* guarded the same way.
- **Exit code** `Crashed` (70), whatever happened in those stages. `HostRunResult` carries the exception, and `CrashReportPath` is null when reports are disabled or the write failed.

## 7. Logging

The host builds one `Logger` at the `Logging` step and passes it everywhere (no global instance, [logging note](logging.md#rules)):

```csharp
public sealed record LoggingOptions
{
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;
    public bool Console { get; init; } = true;
    public bool File { get; init; } = true;                  // <user data>/logs/engine.log
    public int MemoryTailEntries { get; init; } = 1000;      // the crash report's and the overlay's tail
    public int MemoryTailBytes { get; init; } = 256 * 1024;
    public IReadOnlyList<ILogSink> AdditionalSinks { get; init; } = []; // the game's; not owned by the host
}
```

- **Default sinks:** `ConsoleLogSink` (standard output and error), `MemoryLogSink(MemoryTailEntries, MemoryTailBytes)`, exposed as `EngineContext.LogTail`, and the rotating file sink. Categories: `Runtime` for the host, `Game` for the game, a module's name for each module, and `Platform`, `Graphics` and `Brutal.*` from the services ([platform note section 10](platform.md#10-native-status-checks-and-the-brutal-log-bridge)).
- **The file sink needs a directory that only exists later.** A logger's sinks are fixed when it is created (`LoggerOptions.Sinks`, `src/BrutalEngine.Core/Logging/LoggerOptions.cs:24`), but the user data directory comes from the platform, which is created with the logger. So the third sink is an internal `DeferredFileLogSink`: until the `UserData` step it drops entries (the memory sink has them); at that step the host creates a `RotatingFileLogSink` with `Directory = Path.Join(userDataDirectory, "logs")` (absolute, as the decision requires), replays the memory tail into it as `LogEntry` values (a public `ref struct` with init properties, `src/BrutalEngine.Core/Logging/LogEntry.cs:16`), and forwards from then on. Attaching happens on the main thread before the host starts anything that logs from another thread, so no entry is doubled. Boot entries are lost only if more were logged before `UserData` than the memory tail holds (`MemoryTailEntries`, `MemoryTailBytes`); the host then writes the tail's `DroppedCount` at that moment as the file's first line ("N earlier boot entries didn't fit in the memory tail"), so a gap is never silent.
- **Fallback directory.** If `GetUserDataDirectory` fails, `UserData` degrades and logs and crash reports go to `Path.Join(Path.GetTempPath(), Organization, Application)`. If the file sink faults later, it drops lines by design; the host reports its `Fault` (`src/BrutalEngine.Core/Logging/Sinks/RotatingFileLogSink.cs:175`) once in the console log, at exit, and in a crash report.
- **The BRUTAL log bridge** (P6) needs nothing from the host beyond `PlatformOptions.Logger`: Platform.Sdl3 installs it at creation and disables it at `Dispose` (`docs/design/platform.md:534`). The host disposes the platform before the logger.
- **Order at exit:** the logger is flushed and disposed last, after the platform, and the host then disposes the sinks it created (not `AdditionalSinks`).

## 8. The headless host

TestUtilities' "headless host stub" (`docs/04-repo-layout.md:64`) lands with R1a and R1e in `tests/TestUtilities` (namespace `BrutalEngine.TestUtilities`; whichever issue needs the project first creates it). It runs the real `GameHost.Execute` with test backends.

```csharp
namespace BrutalEngine.TestUtilities;

public sealed class HeadlessHost
{
    public HeadlessHost(HeadlessHostOptions options);
    public FakePlatform Platform { get; }
    public ManualTimeProvider Time { get; }
    public HostRunResult Run<TGame>(int frames) where TGame : IGame, new();
    public HostRunResult Run(IGame game, int frames);
}

public sealed record HeadlessHostOptions
{
    public EngineOptions Engine { get; init; } = new() { Organization = "Tests", Application = "Headless" };
    public IGraphicsBackend? Graphics { get; init; }       // null: NoSurface; set: G2b's fake backend
    public TimeSpan FrameTime { get; init; } = TimeSpan.FromSeconds(1.0 / 60);   // clock advance per pump (with graphics)
}

public sealed class FakePlatform : IPlatform            // scripted events, fake windows, recorded message boxes
{
    public void Enqueue(in PlatformEvent platformEvent);
    public IReadOnlyList<MessageBoxRequest> MessageBoxes { get; }
    // plus the IPlatform members
}

public sealed class ManualTimeProvider : TimeProvider
{
    public void Advance(TimeSpan delta);
    // GetTimestamp and TimestampFrequency return the scripted time
}
```

- **What it fakes:** the operating system and, optionally, the GPU. `FakePlatform` has no SDL: its windows have the sizes and states the test sets, a fake `IVulkanSurfaceSource` that only a fake backend accepts, events the test enqueues, a base directory and a user data directory in fresh temporary folders, and message boxes recorded instead of shown. Graphics is the in-memory fake backend from the render interface's split (G2b). The real Vulkan backend can't use a fake surface, so real-GPU coverage comes from the conformance project's offscreen devices (the splash test, section 11) and from samples on the real `SdlPlatform` in `vulkan-smoke`.
- **Which setting wins.** `Run(game, frames)` sets `Engine.FrameLimit` to `frames`, replacing any value in `Engine`. The headless host always replaces `Engine.Backends` with its own (`CreatePlatform` returning its `FakePlatform`, `Graphics` from `HeadlessHostOptions.Graphics`, `ShowFatalError` recording into `FakePlatform.MessageBoxes`). With `Graphics` null it forces `Headless = NoSurface`; with a backend it keeps `Engine.Headless` (`None` or `Offscreen`, which the fake platform only reports). **Time:** `ManualTimeProvider` is the platform's clock. With graphics, each `PumpEvents` advances it by `FrameTime` (standing in for present pacing). Under `NoSurface` the pump doesn't advance it, and `WaitForEvents(timeout)` advances it by the timeout and returns at once, so pacing costs no real time. Tests can also call `Advance` themselves, for example inside a module, to script a long frame.
- **What really runs:** everything in `BrutalEngine.Runtime`: option and command-line handling, the lifecycle and its failure paths, the module registry, the frame loop and `FixedStepClock`, world schedules on a real `EcsWorld`, the splash timeline (and its drawing, with a graphics backend), crash reports into the temporary user data directory, logging, the leak check and the allocation measurement.
- Tests are deterministic and parallel: no SDL, no wall clock, no process-wide state (the one-host-per-process count covers only the default SDL platform).

## 9. The debug overlay

The overlay (ImGui, `BrutalEngine.DebugOverlay`, an L4 host: `tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:40`) is "compiled out of release builds by default" (`docs/03-architecture.md:234`).

```csharp
public static class RuntimeFeatures
{
    [FeatureSwitchDefinition("BrutalEngine.Runtime.DevelopmentBuild")]
    public static bool IsDevelopmentBuild { get; }    // AppContext switch; default true
}
```

- **Development build** is one AppContext switch. Runtime ships an MSBuild props file (packed under `buildTransitive/`, and imported by the in-repo samples) that sets it to false when the game's `Configuration` is `Release` (`RuntimeHostConfigurationOption` with `Trim="true"`). Overriding the property gives a release build with the overlay or a debug build without it. The switch decides the defaults of `DebugOverlay`, `GraphicsSettings.Validation` and `LeakCheck`.
- **Compiled out.** The host installs the overlay only inside `if (RuntimeFeatures.IsDevelopmentBuild && options.DebugOverlay)`. `[FeatureSwitchDefinition]` lets the trimmer and NativeAOT treat the property as the constant `false` in release builds, so the branch and every overlay type it reaches are removed from the executable. The native ImGui library is still copied by NuGet; leaving it out of release output belongs to the M2 publish step.
- **The hook.** DebugOverlay is below Runtime, so it can't implement `IEngineModule`. Runtime wraps O1's public overlay type in an internal `DebugOverlayModule` (an `IFrameModule`), registered first and recorded last (section 3), skipped for `NoSurface`. It records only in the `Running` phase, never over the splash (LICENSE 2.2: the logo unaltered and no less prominent). It passes the overlay its inputs each frame: the pumped events, `FrameTiming`, `SpiralCount`, per-system timings (`Schedule.GetTimings`), per-pass GPU timings (`GraphicsDevice.TryGetPassTimings`) and `LogTail`. The input type is O1's and O3's to define.

## 10. Threading

- **The main thread is the one that calls `Run` or `Execute`.** The host captures Core's `ThreadAffinity` there ([platform note section 9](platform.md#9-threading)); on macOS the platform also requires it to be the process's main thread and fails start-up otherwise. Every `IGame` and `IEngineModule` callback, every frame hook and every schedule runs on it.
- Every `EngineContext` and `ModuleRegistry` member checks the thread and throws `WrongThreadException` naming the API (docs/03, `docs/03-architecture.md:87`), in every build, as Platform, Graphics and World do. The exception is `RequestExit`, which any thread may call (a volatile flag the loop reads once per frame).
- `FixedStepClock` is not thread-safe (owned by the loop). `EngineOptions` and its records, `SplashLogo` and `HostRunResult` are immutable and thread-safe. The crash handler for other threads (section 6) reads only cached start-up data, the thread-safe `MemoryLogSink` and the frame-timing ring (a torn read there costs one wrong number in a report).
- **No static mutable state** beyond `GameHost`'s `static int` run count (section 1): the host's state lives in a per-run instance, and `RuntimeFeatures` reads its switch through `AppContext`, as the static-state rule requires ([architecture tests](architecture-tests.md#rules)).

## 11. Testing

| Area | Tests (`tests/BrutalEngine.Runtime.Tests` unless noted) |
|------|------|
| Fixed-step clock (R2) | Unit tests for each rule; CsCheck property tests over random timestamp sequences, rates and limits: total steps equal `floor(sum(min(delta, max)) * rate / frequency)` exactly; `Alpha` in `[0, 1)`; `SpiralCount` equals the number of clamped frames; a delta split across frames gives the same steps as one frame when nothing is clamped; the same input always gives the same output (determinism); `Rebase` never adds steps. Allocation test: 1,000 `Advance` calls allocate 0 bytes |
| Lifecycle and modules (R1) | With the headless host: the step order recorded by probe modules; a failure injected at every fatal step gives exit 3 or 4 and undoes exactly the completed steps in reverse; degradable failures continue and are listed; dependency order, ties, cycles, unknown names, a degraded dependency; `Stop` exceptions don't stop shutdown; misuse throws |
| Options (R1b) | Command line: every option, every error (64) with a table of bad inputs; `engine.json`: every key, invalid JSON, unknown keys and bad values ignored with warnings, precedence code < file < command line |
| Frame loop (R1) | Scripted clock: `FixedUpdate` runs exactly the clock's steps; phases switch once; minimised windows wait and rebase; `FrameLimit`; device loss with the fake backend's fault seam calls `Recover` and owners; a failed recovery exits 4. Allocation: 1,000 steady-state frames with a world schedule and a frame module allocate 0 bytes, also under Windows dynamic coverage |
| Logging (R1b) | Deferred file sink: boot lines before `UserData` reach the file once, in order; the fallback directory; the `Fault` report |
| Splash (R3) | The promoted unit tests (layout, timeline, compositor, statistics); the presented-time clock (a minimised period doesn't count); game-logo area and duration caps; skip rules (nothing skips the first 2 s); PNG decoder: the BRUTAL logo decodes to the same RGBA bytes as `SDL_LoadPNG` gives (a hash recorded from the M0 path), every filter type, CRC and truncation errors, and a CsCheck round trip against a test-side encoder over the BCL's `ZLibStream`; the embedded logo's SHA-256; GPU (conformance project, lavapipe in CI, the RTX 2070 SUPER locally): a 1:1 splash frame read back equals the logo composited onto black, byte for byte, and a scaled one stays inside the fitted rectangle on black |
| Crash reports (R4) | Without killing the test host: the headless host runs games that throw in `Initialize`, in a system and in `Shutdown`; each returns 70 with the report in the temporary user data directory, every section present, the log tail ending with the last line logged, the message box recorded only when not headless, and no graphics call after the throw (the fake backend counts calls); retention keeps `MaxReports`; the other-thread handler is invoked directly, not by crashing |
| Architecture | Runtime is referenced by `tests/BrutalEngine.Architecture.Tests` (layering, binding surface, static state) |
| NativeAOT | The rewritten HelloWindow (S1) and SpriteStress publish on Runtime and run in `vulkan-smoke`; the overlay switch is checked by publishing with it off and asserting no `BrutalEngine.DebugOverlay` type is in the output's symbol map (R5) |

Mutation self-checks, for example: accumulate the unclamped delta; leave a started module unstopped after a later failure; let a key press skip the BRUTAL logo before 2 s; call `GraphicsDevice.WaitIdle` on the crash path.

## 12. Issue split

| Issue | Scope | Blocked by | Est. |
|-------|-------|------------|------|
| R2 feat(runtime): run a fixed-step loop with interpolation | Creates `src/BrutalEngine.Runtime` + tests: `FrameLoopOptions`, `FixedStepClock`, `FrameTiming` (section 4); the Architecture.Tests reference | this note | ~450 |
| R1 feat(runtime): host the engine's lifecycle, modules and frame loop | `IGame`, `Game`, `GameHost`, `EngineOptions` (without `Splash`, `CrashReports` and `DebugOverlay`, which R3, R4 and R5 add), `GraphicsSettings`, `HostBackends`, `HostExitCode`, `HostRunResult`, `HostFailure`, `HostStep`, `EngineContext`, `HostPhase`, the module types (section 3), `FrameContext`, `RenderTarget`, the loop (section 4), device-loss recovery, `RuntimeFeatures` and its props file (section 9), the leak check, console and memory logging; `tests/TestUtilities` with `HeadlessHost`, `FakePlatform`, `ManualTimeProvider` | R2; P5 (`SdlPlatform` complete); G5 and G2b (device, swapchain, fake backend); W6 (schedules); the Vulkan backend factory from #55's split | ~750 |
| R1b feat(runtime): read the command line and engine.json, and log to a file | The command-line table and usage errors, `engine.json`, `LoggingOptions`, `DeferredFileLogSink`, the fallback directory (sections 1, 7) | R1 | ~550 |
| R3 feat(runtime): show the BRUTAL splash before gameplay | `SplashOptions`, `SplashLogo`, the promoted splash types and their tests, the PNG decoder, `splash.slang` and its cooked SPIR-V, the embedded logo, the splash phase (section 5); HelloWindow's copies stay until S1 | R1; G9 (pipelines, draws); T1 (shader cooking) | ~700 |
| R4 feat(runtime): write crash reports | `CrashReportOptions`, the boundary catch, the other-thread handler, the report writer, retention, message boxes (section 6) | R1b (log tail and file directory) | ~500 |
| R5 feat(runtime): install the debug overlay in development builds | `EngineOptions.DebugOverlay`, `DebugOverlayModule`, the release-build trimming check (section 9) | R1; O1 (and O3 for the panels' inputs) | ~250 |

Order: R2, R1, then R1b, R3 and R5 as their other blockers land (one at a time, because they share Runtime's `PublicAPI.Unshipped.txt`), then R4. S1 (HelloWindow on Runtime) needs R3; S2 (SpriteStress) needs R1.

## Failure modes

| Failure | Result |
|---------|--------|
| Invalid options; a second host in the process | `ArgumentException` / `InvalidOperationException` from `Run`, before anything starts |
| Bad command line | exit 64, usage on standard error |
| Bad `engine.json` | `Warning`, keys ignored |
| A fatal start-up step fails | `Critical` log, message box, partial teardown, exit 3 |
| A degradable step or module fails | `Warning`, `Degradations`, the run continues |
| Module dependency cycle or unknown name | `InvalidOperationException`, reported as a crash (70) |
| Device lost (reported) | `Recover()`; failure: crash report, normal shutdown, exit 4 |
| Device lost (`TimedOut`, a hung GPU) | crash report, no `Recover` and no device disposal (abandoned and logged), exit 4 |
| Unhandled exception on the main thread | crash report, message box, exit 70, no GPU calls; a failing report stage is skipped, never thrown |
| Unhandled exception on another thread | crash report; the runtime ends the process |
| Validation messages, leaks | exit 2, exit 4 |
| Wrong thread | `WrongThreadException` |

## Consistency with docs/03

| docs/03 says | This note | Why |
|--------------|-----------|-----|
| `BrutalEngine.Run<TGame>(options)` (3.2) | `GameHost.Run<TGame>(options)` | Section 1: a `BrutalEngine` type hides the namespace |
| Audio device and asset database in the lifecycle (3.2) | Reserved steps 80 and 90 | Not in M1 |
| `world.RunSchedule(Update)` only (3.2) | `PostUpdate` runs after `Update` | The world note's transform system |
| "Minimised windows skip rendering" (3.5) | Simulation pauses too | M0's behaviour; a keep-simulating option comes with networking |
| Thread checks "asserted in debug builds" (3.2) | Checked in every build | As Platform, Graphics and World |
| Crash: report, message box, exit (3.5) | No shutdown callbacks or GPU teardown after a crash | A crashed or lost device can hang them |
| Splash from M0: `SDL_LoadPNG` and a blit | Managed PNG decoding and a textured quad | Section 5: no native calls in Runtime, no blit in the render interface |
| Configure from "options, CLI args, config file" | `user://engine.json` only, applied after the platform starts | Section 1 |

## Not verified

- The note is design only; nothing in it has run. The splash rules it keeps were verified by M0 (`samples/HelloWindow/README.md`); its drawing path is new and is proven by R3e's GPU test.
- `[FeatureSwitchDefinition]` removing the overlay branch under NativeAOT is the documented .NET behaviour, not yet observed here: R5 checks it.
- `VulkanBackend.Create`, `VulkanBackendOptions` and the shutdown and device-loss behaviour cited from `docs/design/graphics-vulkan.md` are from #79, still in review; if that note changes, the names here follow it.
- That zlib's end-of-stream check (Adler-32) is reached by reading `ZLibStream` to its end is zlib's documented behaviour; R3c's corrupted-checksum test proves it.
- Windows and Linux are the tested targets in M1. macOS keeps its code paths (the main-thread rule, the bundled loader) but is unverified.

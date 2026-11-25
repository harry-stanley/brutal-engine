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

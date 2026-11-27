# Design: asset database (`BrutalEngine.Assets`)

## Purpose

`BrutalEngine.Assets` finds, loads, reference-counts, budgets and unloads a game's assets: by stable GUID, asynchronously by priority, with their dependencies, in load groups, from versioned packs in release builds and from source files with hot reload in development builds ([Architecture 3.13](../03-architecture.md#313-assets-brutalengineassets-tool-brutalenginecook), `docs/03-architecture.md:182-189`; [ADR-0005](../adr/0005-offline-asset-cooking.md); [roadmap M2](../09-roadmap.md#m2-2d-essentials-03), `docs/09-roadmap.md:73`). It is an L3 library (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:30`): it references Core, Platform, Graphics and Audio, all lower layers, and no L3 peer. No binding type appears in its public surface.

This note is the API sketch for the Assets code issues (section 13) and the contract the asset pipeline note (#283) implements. Signatures are the contract; XML docs, `PublicAPI.Unshipped.txt` entries, argument validation and tests follow the usual rules ([Coding standards](../07-coding-standards.md#public-api-design)). It references, and doesn't redefine, the [platform note](platform.md) (the VFS), the [render interface note](graphics.md) (textures, uploads, device loss), the [runtime note](runtime.md) (modules, the frame loop) and the audio note (#281, written after this one), whose types it names but doesn't design.

- Decision (maintainer, 2026-11-27): **layering.** Assets owns asset identity, typed handles, the database (reference counts, asynchronous loads, groups, budgets, hot reload), the pack format and its reader, a runtime loader contract (cooked bytes to an object) and a source-import seam (a source file and its `.meta` to cooked bytes, called in development builds). Loaders for types that L2 modules own (GPU textures through Graphics, audio clips and streams through Audio) live in Assets. Rendering2D, Text and Input register their own loaders through new listed same-layer edges `Rendering2D -> Assets`, `Text -> Assets` and `Input -> Assets`; the first code PR that needs each edge adds it. Assets never references those peers. Tilemaps (L4) needs no edge.
- Decision (maintainer, 2026-11-27): **importers in development builds only.** `BrutalEngine.Importers` (an L6 tool, `tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:48`) implements the import seam. A game references it in its Debug configuration only, so Release output contains no importer code (section 7). Runtime modules never reference tools (`tests/BrutalEngine.Architecture.Tests/LayeringRule.cs:62`), and Assets doesn't: the game hands the database an importer object.
- Decision (maintainer, 2026-11-27): **host-agnostic.** The note names the start and stop order and the per-frame call (section 3). Until Runtime's reserved asset-database step 90 (`docs/design/runtime.md:149`, `:177`) is implemented after the 0.2.0 release, a game wraps the database in its own `EngineModule`. A database that fails to start is fatal.
- Decision (maintainer, 2026-11-27): **uncompressed textures in M2:** `Rgba8UnormSrgb`, and `R8Unorm` for glyph atlases (section 9). Compressed formats are a later, optional addition.

**The project (ADR-0016).** `src/BrutalEngine.Assets/BrutalEngine.Assets.csproj` declares exactly `<BrutalEngineMilestone>2</BrutalEngineMilestone>` and never sets `IsPackable` or `BrutalEngineReleaseMilestone` ([ADR-0016](../adr/0016-trunk-based-milestones.md), decision 3), so the 0.2.0 release doesn't pack it. Its CHANGELOG entries go under `## Next: 0.3.0 (not in the next release)` (decision 5), never `[Unreleased]`. It takes no BRUTAL package and no new NuGet package: compression is the BCL's `System.IO.Compression` (Deflate and Brotli, `docs/02-tech-stack.md:70`), checksums are a CRC-32 of its own (section 6), file watching is the BCL's `FileSystemWatcher` (section 7). Platform, Graphics and Runtime are frozen until 0.2.0 is released (ADR-0016 decision 4), so nothing here needs a change in them: asynchronous I/O runs on the database's own workers over the synchronous, thread-safe VFS (section 5).

## 1. Identity

```csharp
namespace BrutalEngine.Assets;

public readonly struct AssetId : IEquatable<AssetId>, IComparable<AssetId>, ISpanFormattable, IUtf8SpanFormattable
{
    public bool IsNone { get; }                                       // default(AssetId) is "no asset"
    public static AssetId NewId();                                    // a random (version 4) GUID
    public static AssetId Parse(string text);                         // FormatException, ArgumentNullException
    public static bool TryParse(ReadOnlySpan<char> text, out AssetId id);
    public static AssetId FromGuid(Guid guid);                        // Guid.Empty gives default
    public static AssetId ReadBytes(ReadOnlySpan<byte> source);       // 16 bytes, canonical order; ArgumentException if shorter
    public void WriteBytes(Span<byte> destination);                   // 16 bytes, canonical order
    public Guid ToGuid();
    public int CompareTo(AssetId other);
    public bool Equals(AssetId other);
    public override string ToString();                                // "0f8fad5b-d9cb-469f-a165-70867728950e", allocates
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider);
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider);
    // plus ==, !=, <, <=, >, >= and GetHashCode
}
```

- **The value is a GUID** (128 bits), as ADR-0005 and docs/03 3.13 say. It is stored as two `ulong`s holding its **canonical bytes**: the 16 bytes in the order of the text form (RFC 9562 network order, what `Guid.TryWriteBytes(destination, bigEndian: true, out _)` writes). `WriteBytes`/`ReadBytes` use that order, packs store it (section 6), and `CompareTo` compares the bytes as unsigned values from the first, so the order of ids equals the ordinal order of their text forms. A second implementation can sort ids without knowing .NET's `Guid` layout, whose own `CompareTo` orders differently.
- **Text form:** 36 characters, lower-case hexadecimal, hyphens after characters 8, 13, 18 and 23 (`Guid` format `D`). `Parse` accepts exactly that shape with either letter case; braces, whitespace, other `Guid` formats and the all-zero id are rejected (`TryParse` returns false, `Parse` throws `FormatException`). `ToString` and `TryFormat` always write lower case; `format` must be empty or `"D"` (`FormatException`). `TryFormat` allocates nothing.
- **Generation:** `NewId` is `Guid.NewGuid()` (122 random bits from the OS generator). Random ids need no coordination, so two people adding assets on separate branches never collide, and they reveal nothing about when or where an asset was made. Ids are generated once, when a `.meta` file is created (the asset pipeline note, #283), and never change: renames and moves keep them.
- Immutable value, any thread, no allocation except `ToString` and `Parse`'s exception.

```csharp
public readonly struct AssetKind : IEquatable<AssetKind>
{
    public uint Value { get; }                       // the four ASCII bytes, little-endian
    public static AssetKind FromCode(string code);    // ArgumentException unless 4 characters of [A-Za-z0-9]
    public static bool TryFromCode(ReadOnlySpan<char> code, out AssetKind kind);
    public static AssetKind FromValue(uint value);    // ArgumentException unless each byte is [A-Za-z0-9]
    public bool IsDefault { get; }
    public bool Equals(AssetKind other);
    public override string ToString();               // the code, e.g. "TXTR"; allocates
    public static AssetKind Texture { get; }          // TXTR (section 9)
    public static AssetKind Bytes { get; }            // BLOB (section 4)
    public static AssetKind AudioClip { get; }        // ACLP (section 9)
    public static AssetKind AudioStream { get; }      // ASTM (section 9)
    // plus ==, != and GetHashCode
}
```

- **A kind is a four-character code**, stored as the little-endian `u32` of its ASCII bytes, so a hex dump of a pack shows it as text (`BLOB` is `42 4C 4F 42`, value `0x424F4C42`). The static properties compute their value on each read (no static field, per the static-state rule).
- **Who names kinds.** Engine kinds are four upper-case letters or digits. Assets declares the four above. Each module that registers a loader declares its own kind in its note; this note reserves `INPM` for Input's input maps and `FONT`, `ATLS` and `TMAP` for Text, Rendering2D and Tilemaps, so those notes need not coordinate. Game-defined kinds must contain at least one lower-case letter, so they can never collide with an engine kind.
- **References between assets** are always `AssetId`s. In cooked data they are the 16 canonical bytes. Every asset lists the ids it depends on in its pack entry (section 6) or in its import result (section 4), and the database loads those first (section 3). Inside its own payload, a kind stores ids wherever its format puts them; its loader resolves them through the load context (section 4).

## 2. Handles

```csharp
public readonly struct AssetHandle<T> : IEquatable<AssetHandle<T>> where T : class
{
    internal AssetHandle(Handle value);            // only the database creates handles
    public bool IsNull { get; }                    // default is null
    public bool Equals(AssetHandle<T> other);
    public override string ToString();             // "Asset<TextureAsset>(index:generation)", allocates
    // plus == and != and GetHashCode
}

public enum AssetState { Loading = 1, Loaded, Failed }
```

- **Typed generational handles** over Core's `Handle` (`src/BrutalEngine.Core/PublicAPI.Unshipped.txt:20`) from one `HandleAllocator` (`:28`) in the database ([handles](handles.md)), as docs/03 3.4 asks. `T` is the loaded object's type, so a texture handle can't be read as an input map. A handle is 8 bytes and unmanaged, so ECS components can hold it (`docs/03-architecture.md:114`).
- **One handle per loaded asset.** While an asset has references, every `Load` of its id returns the same handle and adds one reference. A handle never changes while the asset is alive, including across hot reload and device-loss recreation (sections 7 and 9), so the game keeps the handle and reads the current object through it each time (`TryGet` allocates nothing). An object reference kept across a swap is the old version, already released.
- **Reference counting is explicit:** `Load`, `LoadNow` and `AddReference` add one, `Release` removes one (section 3). Handles are non-owning values that copy freely, as docs/03 3.3 says of all handles, so they aren't `IDisposable`: a disposable struct is released twice as soon as it is copied. `AssetGroup` (section 3) is the disposable owner for sets of references.
- **Before loading** a handle reports `Loading`: `TryGet` returns false. **After a failure** it reports `Failed`: `GetError` returns the error, the reference is still counted and the game still releases it. In development mode a failed asset whose source changes is imported again (section 7).
- **Staleness.** When the count reaches zero, the asset is unloaded at the next safe point (section 3) and its handle is freed: every copy is stale from then on, and passing one to any database member throws `StaleHandleException`. A `Load` between the last `Release` and the safe point revives the asset with the same handle and nothing is unloaded. `default` throws `ArgumentException`. A handle from another database is detected only when its index or generation doesn't match there (the documented limit of `Handle`).

## 3. The database

```csharp
public sealed record AssetDatabaseOptions
{
    public required VirtualFileSystem Files { get; init; }        // read from any thread (thread-safe)
    public required Logger Logger { get; init; }                   // category "Assets"
    public GraphicsDevice? Graphics { get; init; }                 // set: the database registers as a device owner (section 9)
    public IAssetImporter? Importer { get; init; }                 // set: development mode (section 7)
    public AssetDevelopmentOptions Development { get; init; } = new();
    public int WorkerCount { get; init; } = 1;                     // 0..8; 0 = no threads (section 5)
    public long MaxInFlightBytes { get; init; } = 64L << 20;       // > 0 (section 5)
    public long MaxAssetBytes { get; init; } = 1L << 30;           // 1..Array.MaxLength; larger assets fail
    public IReadOnlyList<AssetBudget> Budgets { get; init; } = [];  // section 8
    public TimeProvider Clock { get; init; } = TimeProvider.System; // hot-reload debounce only
}

public sealed class AssetDatabase : IDisposable
{
    public static Result<AssetDatabase> Create(AssetDatabaseOptions options);
    public bool IsDevelopmentMode { get; }
    public void RegisterLoader<T>(IAssetLoader<T> loader) where T : class;   // one per kind
    public Result MountPack(AssetPack pack);
    public void UnmountPack(AssetPack pack);

    public bool Contains(AssetId id);
    public bool TryGetKind(AssetId id, out AssetKind kind);
    public bool TryResolve(VfsPath path, out AssetId id);          // a res:// source path to its id; false for other schemes

    public Result<AssetHandle<T>> Load<T>(AssetId id, AssetPriority priority = AssetPriority.Normal) where T : class;
    public Result<AssetHandle<T>> LoadNow<T>(AssetId id) where T : class;
    public void AddReference<T>(AssetHandle<T> handle) where T : class;
    public void Release<T>(AssetHandle<T> handle) where T : class;

    public AssetState GetState<T>(AssetHandle<T> handle) where T : class;
    public bool TryGet<T>(AssetHandle<T> handle, [NotNullWhen(true)] out T? asset) where T : class;
    public T Get<T>(AssetHandle<T> handle) where T : class;          // InvalidOperationException unless Loaded
    public EngineError GetError<T>(AssetHandle<T> handle) where T : class;   // InvalidOperationException unless Failed
    public AssetId GetId<T>(AssetHandle<T> handle) where T : class;
    public int GetVersion<T>(AssetHandle<T> handle) where T : class;  // +1 per swap (sections 7, 9)
    public int GetReferenceCount<T>(AssetHandle<T> handle) where T : class;

    public AssetGroup CreateGroup(string name);
    public void ProcessCompletions();
    public void RescanSources();                                    // development mode only (section 7)
    public event EventHandler<AssetReloadedEventArgs>? Reloaded;
    public AssetStatistics Statistics { get; }
    public AssetBudgetUsage GetBudgetUsage(AssetKind kind);
    public void Dispose();
}

public enum AssetPriority { Low = -1, Normal = 0, High = 1 }
```

- **`Create`** validates the options (misuse: `ArgumentException`, `ArgumentOutOfRangeException`), captures Core's `ThreadAffinity` (`src/BrutalEngine.Core/PublicAPI.Unshipped.txt:253`, `:313`) as its owner thread, registers the built-in `BytesLoader` (section 4), starts its workers and, when `Graphics` is set, registers with the device (section 9). In development mode it scans `res://` for sources before returning (section 7), so `Contains` and `Load` work at once. It fails with the scan's error (`Io`, or the importer's) and starts nothing then.
- **Loaders** are registered before or after `Create`'s return, at any time before the first load of their kind. A second loader for a kind, or a loader whose traits need a device when `Graphics` is null, throws `InvalidOperationException`. The database holds loaders and never disposes them.
- **Packs:** `MountPack` adds a pack's entries to the catalogue. An id already in another mounted pack fails with `InvalidData` naming both packs, and nothing is mounted (overlay packs for patches are a later addition, a pack-format question). In development mode a source shadows a pack entry of the same id (logged at `Debug`). `UnmountPack` throws `InvalidOperationException` while any asset from the pack has references: the database re-reads loaded assets after device loss (section 9), so their pack must stay. The caller owns each `AssetPack` and disposes it after unmounting it or disposing the database.
- **`Load`** fails synchronously, with no reference taken, when the id is unknown (`NotFound`), no loader is registered for its kind (`Unsupported`), or `T` isn't the type its kind's loader produces (`InvalidData`: data named the wrong kind). Otherwise it returns the handle, adds a reference and, if the asset isn't loaded or loading, queues it at `priority`. A second `Load` at a higher priority raises a queued request; dependencies inherit the highest priority of their dependants. Within a priority, requests run in the order they were made. Every load-time failure after that (I/O, checksums, decompression, the loader, a dependency) puts the handle in `Failed`. A failed asset isn't retried by `Load` (it returns the same handle, still `Failed`, with one more reference) or `LoadNow` (it returns the stored error); only a source change retries it (section 7).
- **`LoadNow`** loads the asset and its missing dependencies on the calling thread, blocking until done, and returns a handle in `Loaded` with one reference, or the failure with no reference taken. Requests already queued for those assets are taken off the queue and run inline; one a worker has already started is waited for, never run twice, and run again inline if its completion is stale (below). It exists for start-up and loading screens; in a measured frame it makes the frame non-steady.
- **Dependencies.** Before an asset is handed to its loader, the database takes one reference on each id it depends on (owned by the dependant, released when it unloads) and loads them first. The dependency walk runs on the owner thread over catalogue metadata, colouring nodes, so a cycle fails every asset on it with `InvalidData` naming the cycle; cycles are a cook-time error too (#283). A dependency that fails fails its dependants with the dependency's kind and the message "dependency <id> failed: ...".
- **Stale completions.** When a request is dispatched it records the version (`GetVersion`) of every dependency it is handed and, in development mode, its own change generation (section 7). `ProcessCompletions` compares both before it publishes a result. If a dependency was swapped or recreated meanwhile (sections 7 and 9), or a change to the asset itself was detected, the result isn't published: an object the loader returned is released with `Replaced`, and the request goes back on the queue at its priority with its references kept (no `Reloaded`, no `LoadsCompleted`). A dependency that is no longer `Loaded` fails the dependant as above. So no asset ends up `Loaded` from a dependency object that was replaced while it loaded, or from source bytes that changed under its import. An `OwnerThread` loader reads its dependencies when it runs, on the owner thread, so for it only the bytes read on the worker can be stale.
- **Replaced objects outlive the requests that hold them.** An old object handed to an in-flight request is released (`Replaced`) only once every such request has completed or been cancelled, in the `ProcessCompletions` that applies them, so a `Worker` loader never reads a released object; `TryGet` returns the new object meanwhile. `DeviceLost` releases run at once (section 9), because the device's objects are already gone; the stale-completion rule then re-queues the dependant.
- **Safe points.** Assets whose count reached zero are unloaded only in `ProcessCompletions` (and `Dispose`): the loader's `Release` runs, the dependency references are released (their own unloads run in the same call), and the handle is freed. A request still queued is dropped; one in flight is cancelled through its token, and if its result arrives anyway it is released at once.
- **`ProcessCompletions`** is the per-frame call (docs/03 3.2, `docs/03-architecture.md:70`): it applies finished worker results (publishing objects, states and errors), runs main-thread loaders (section 4), applies hot-reload swaps and raises `Reloaded` (section 7), then runs the unloads. With nothing completed and nothing to unload it allocates nothing.
- **Statistics:**

```csharp
public readonly record struct AssetStatistics
{
    public int Live { get; init; }              // assets with a handle (any state)
    public int Loading { get; init; }
    public int Failed { get; init; }
    public long ResidentBytes { get; init; }    // section 8's sizes, all kinds
    public long InFlightBytes { get; init; }    // section 5's bound
    public long LoadsCompleted { get; init; }   // since Create; failures included
    public long Reloads { get; init; }          // swaps, sections 7 and 9
}

public sealed class AssetReloadedEventArgs : EventArgs
{
    public AssetId Id { get; }
    public AssetKind Kind { get; }
    public AssetReloadReason Reason { get; }
    public EngineError? Error { get; }          // set when the new version failed and the old one stays
}

public enum AssetReloadReason { SourceChanged = 0, DependencyChanged, DeviceRecreated }
```

### Load groups

```csharp
public sealed class AssetGroup : IDisposable     // main thread; owned by the caller of CreateGroup
{
    public string Name { get; }
    public Result<AssetHandle<T>> Load<T>(AssetId id, AssetPriority priority = AssetPriority.Normal) where T : class;
    public int Count { get; }                    // distinct ids in the group
    public int LoadedCount { get; }
    public int FailedCount { get; }
    public bool IsComplete { get; }              // nothing in the group is Loading
    public void Dispose();                       // releases every reference; idempotent
}
```

A group holds one reference per distinct id (a second `Load` of an id returns the same handle and takes no second reference), so a level loads its assets through a group, shows progress from `LoadedCount` and `Count`, and releases everything with `Dispose`. A disposed group's `Load` throws `ObjectDisposedException`. Disposing the database disposes its groups. `Name` (1-64 characters) is for messages and the debug overlay.

### Host integration

Start after the platform (and its `res://` mount), the graphics device and the audio engine, and before every module that loads assets; dispose after those modules stop and before the graphics device and audio engine are disposed (docs/03's order, `docs/03-architecture.md:47-50`; Runtime's step 90). Per frame, call `ProcessCompletions` once after the frame is submitted (Runtime's `EndFrame` hook, "after submit: e.g. completing asynchronous loads", `docs/design/runtime.md:232`), so GPU uploads it makes go into the next frame (section 9). Device recovery happens inside `GraphicsDevice.Recover`, which the host calls outside a frame (`docs/design/graphics.md:748`). Until Runtime implements step 90, the game wraps it:

```csharp
sealed class GameAssetsModule(IAssetImporter? importer) : EngineModule(new ModuleInfo { Name = "game.assets" })
{
    private AssetPack? _pack;
    public AssetDatabase? Assets { get; private set; }

    public override Result Start(EngineContext context)
    {
        Result<AssetDatabase> created = AssetDatabase.Create(new AssetDatabaseOptions
        {
            Files = context.Files, Logger = context.Logger, Graphics = context.Graphics, Importer = importer,
        });
        if (!created.TryGetValue(out AssetDatabase? assets)) return created.Error;   // Fatal: ModuleInfo's default
        if (context.Graphics is not null) assets.RegisterLoader(new TextureLoader(context.Graphics));
        Result mounted = Result.Success;
        if (!assets.IsDevelopmentMode)
        {
            Result<AssetPack> pack = AssetPack.Open(context.Files, VfsPath.Parse("res://game.bepack"));
            mounted = pack.TryGetValue(out _pack) ? assets.MountPack(_pack) : pack.ToResult();
        }
        if (mounted.IsFailure) { assets.Dispose(); _pack?.Dispose(); _pack = null; return mounted; }  // Stop isn't called
        Assets = assets;
        return Result.Success;
    }

    public override void EndFrame(in FrameContext frame) => Assets!.ProcessCompletions();

    public override void Stop(EngineContext context)
    {
        Assets?.Dispose();
        _pack?.Dispose();
    }
}
```

`EngineModule`, `ModuleInfo`, `EngineContext` and `FrameContext` are Runtime's (`docs/design/runtime.md:235-246`, `:217-222`, `:183-200`, `:335-341`); `ModuleFailurePolicy.Fatal` is `ModuleInfo`'s default (`:221`), so a failed `Start` ends start-up; a module whose `Start` failed is never stopped (`:262`), so it disposes what it created itself. Modules that load assets list `DependsOn = ["game.assets"]`. The game passes `importer` from `#if DEBUG` code (section 7). Mounting packs under `res://` for path-based reads is section 6's `AssetPackFileSystem`.

## 4. The loader contract and the import seam

```csharp
public interface IAssetLoader<T> where T : class
{
    AssetKind Kind { get; }
    AssetLoaderTraits Traits { get; }                       // read once, at registration
    Result<T> Load(in AssetLoadContext context);
    void Release(T asset, AssetReleaseReason reason);       // owner thread
}

public readonly record struct AssetLoaderTraits
{
    public AssetLoadThread Thread { get; init; }           // default Worker
    public AssetContentMode Content { get; init; }         // default Bytes
    public bool RecreateOnDeviceLoss { get; init; }        // section 9; needs AssetDatabaseOptions.Graphics
}

public enum AssetLoadThread { Worker = 0, OwnerThread }
public enum AssetContentMode { Bytes = 0, Stream }
public enum AssetReleaseReason { Unloaded = 0, Replaced, DeviceLost, DatabaseDisposed }

public readonly ref struct AssetLoadContext
{
    public AssetId Id { get; }
    public AssetKind Kind { get; }
    public string Name { get; }                             // the source path, or the id's text; for messages and debug names
    public ReadOnlySpan<byte> Data { get; }                 // Bytes mode: the uncompressed cooked payload; empty in Stream mode
    public AssetContentSource? Content { get; }             // Stream mode only
    public ReadOnlySpan<AssetId> Dependencies { get; }      // ascending; every one Loaded
    public TDep GetDependency<TDep>(AssetId id) where TDep : class;              // InvalidOperationException if not listed or of another type
    public AssetHandle<TDep> GetDependencyHandle<TDep>(AssetId id) where TDep : class;
    public CancellationToken CancellationToken { get; }    // cancelled when the request is dropped or the database disposed
}

public sealed class AssetContentSource             // thread-safe; owned by the database; valid while the asset is loaded
{
    public long Length { get; }
    public Result<Stream> OpenRead();               // a new read-only, seekable stream each call; the caller disposes it;
                                                    // after the asset is released: ErrorKind.Unavailable (open streams
                                                    // keep working until their pack is disposed)
}
```

- **A loader turns cooked bytes into an object.** In `Bytes` mode the database reads, checks and decompresses the payload (section 6) and passes it in `Data`, valid only during the call: the loader copies what it keeps. In `Stream` mode (long audio) nothing is read up front: the loader keeps the `AssetContentSource` and reads from it later, from any thread; a `Stream` asset must be stored uncompressed (section 6), or the load fails with `InvalidData`. In development mode the source serves the imported bytes from memory.
- **Threads.** `Worker` loaders run on a database worker and must be thread-safe (several workers may call them at once). `OwnerThread` loaders, which call the graphics device, run in `ProcessCompletions` or `LoadNow`, after the bytes were read on a worker. `Release` always runs on the owner thread. Both may allocate: loads are load-time work.
- **Dependencies** are all `Loaded` before `Load` is called. The context hands out the loaded objects for immediate use and handles for keeping; the handle needs no reference of its own, because the dependant's reference keeps the dependency alive. When a dependency is swapped, its loaded dependants are loaded again (section 7) and those still loading are re-queued (section 3's stale completions), so values a loader copied out of a dependency are never stale.
- **Failures** are the loader's `Result`: `InvalidData` for a bad payload, `Unsupported` for a valid one this device or version can't use, the device's kinds for GPU failures. A loader throws only for its own bugs; an exception from `Load` fails the asset with `ErrorKind.Unspecified` and the exception's message, and is logged at `Error` with the stack.
- **`Release` reasons:** `Unloaded` (count reached zero), `Replaced` (a hot-reload or recreation swap installed a new object), `DeviceLost` (drop GPU handles without calling the device, section 9; the object's fields must stay readable, because a worker may still be reading them), `DatabaseDisposed`. A loader whose objects own nothing makes it a no-op.

The built-in loader for raw bytes, registered by `Create`, also serves tests:

```csharp
public sealed class BytesAsset { public ReadOnlyMemory<byte> Data { get; } }     // immutable; any thread
public sealed class BytesLoader : IAssetLoader<BytesAsset>                     // stateless; thread-safe
{
    public BytesLoader();
    public AssetKind Kind { get; }                                             // BLOB
    public AssetLoaderTraits Traits { get; }                                   // Worker, Bytes
    public Result<BytesAsset> Load(in AssetLoadContext context);               // copies Data into a new array
    public void Release(BytesAsset asset, AssetReleaseReason reason);          // no-op
}
```

**Input's loader fits as sketched** by the input note (#286, its section 9): `InputMapLoader : IAssetLoader<InputMap>` with `Kind` `INPM`, default traits (`Worker`, `Bytes`), `Load` returning `InputMap.FromJson(context.Data)` (it takes a `ReadOnlySpan<byte>`; its `Result<InputMap>` is the loader's result), no dependencies, and a no-op `Release`. Input registers it from its own assembly through the `Input -> Assets` edge.

### The import seam (development builds)

```csharp
public interface IAssetImporter                 // implemented by BrutalEngine.Importers (#283); thread-safe
{
    Result<IReadOnlyList<AssetSource>> ReadMeta(VirtualFileSystem files, VfsPath metaPath);
    Result<ImportedAsset> Import(AssetImportRequest request);
}

public readonly record struct AssetSource(AssetId Id, AssetKind Kind, VfsPath SourcePath);

public sealed record AssetImportRequest
{
    public required AssetSource Source { get; init; }
    public required VfsPath MetaPath { get; init; }
    public required VirtualFileSystem Files { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed record ImportedAsset
{
    public required ReadOnlyMemory<byte> Data { get; init; }          // exactly what a pack entry holds, uncompressed
    public IReadOnlyList<AssetId> Dependencies { get; init; } = [];
    public IReadOnlyList<VfsPath> ReadPaths { get; init; } = [];      // every other file the import read (section 7)
}
```

- **`ReadMeta`** parses one `.meta` sidecar (its schema is #283's) and returns the assets it declares: usually one, several when one source yields several assets (an Aseprite file's texture and animation). **`Import`** reads the source through the VFS and returns the cooked payload a pack entry would hold for that id, its dependency ids and every other file it read. The cooker writes the same payloads into packs, so both paths share importer code by construction (ADR-0005's consequence).
- **Threads and failures.** Both members are called from database workers, concurrently when `WorkerCount > 1`, and from the owner thread for `Create`'s initial scan and with `WorkerCount = 0`, so implementations are thread-safe. Expected failures are `Result`s (`InvalidData` for a bad source or `.meta`, the VFS's kinds for I/O); `Data` larger than `MaxAssetBytes` fails the asset with `Unsupported`.
- **Ownership:** the game creates the importer and keeps it until the database is disposed; the database holds it and never disposes it. Results are owned by the database once returned.

## 5. Asynchronous I/O

- **Workers.** `WorkerCount` dedicated background threads ("BrutalEngine.Assets.Worker 0", ...), started by `Create` and joined by `Dispose`. Each takes the highest-priority runnable request (all dependencies loaded) and reads its stored bytes: from a pack (section 6) or, in development mode, through the importer. It verifies the checksum, decompresses, runs a `Worker` loader, and posts the result to a completion queue the owner thread drains in `ProcessCompletions`. Docs/03 says asset I/O completes on the main thread (`docs/03-architecture.md:84`); this is that thread.
- **Reads go through the VFS**, synchronously, on the workers: `VirtualFileSystem` and every `IFileSystem` are thread-safe (`docs/design/platform.md:442`), so the asynchronous reads that note promises "with the asset database (M2), behind the same paths" (`:444`) need no Platform change. Pack reads use the pack's own stream (section 6).
- **`WorkerCount = 0`** starts no thread: `ProcessCompletions` runs queued requests inline, in priority order, before applying completions. Tests and tools use it, because every step then runs on one thread in a fixed order.
- **Cancellation.** Each request has a `CancellationTokenSource`; the token reaches the importer and the loader. A worker checks it before reading, before decompressing and before loading. Cancelled work is never published.
- **Bound on in-flight memory.** `InFlightBytes` counts the stored and uncompressed bytes of requests read but not yet applied by `ProcessCompletions`. A worker doesn't start a request that would take it over `MaxInFlightBytes` unless nothing is in flight, so one asset larger than the bound still loads, alone. Pack entries know their sizes before the read; an import's size is counted when it returns. The bound keeps a level's worth of queued loads from holding gigabytes between two frames.

## 6. The pack format

A pack is one file (by convention `<name>.bepack`) holding cooked assets. Everything is little-endian, offsets are from the start of the file, and the layout is fully determined by the contents, so the same assets always give the same bytes. The cooker (#283) writes packs; a reference writer in the tests writes them independently (section 12).

```text
[header: 64 bytes][entries: EntryCount x 64 bytes][dependency ids: DependencyCount x 16 bytes][paths: PathTableLength bytes]
[zero padding to a multiple of 16][entry 0 data][zero padding to 16][entry 1 data] ... [last entry data]   <- end of file
```

**Header** (64 bytes at offset 0):

| Offset | Size | Field | Rule |
|-------:|-----:|-------|------|
| 0 | 8 | Magic | `89 42 45 50 4B 0D 0A 1A` (`\x89BEPK\r\n\x1a`): the high byte catches 7-bit transfers, the CR LF pair line-ending conversion, `1A` DOS `type` |
| 8 | 4 | FormatVersion | `u32`, 1 |
| 12 | 4 | Flags | `u32`, 0 |
| 16 | 8 | FileLength | `u64`, the file's length in bytes |
| 24 | 4 | EntryCount | `u32`, at most 1,048,576 |
| 28 | 4 | DependencyCount | `u32`, the dependency ids in the table, at most 16,777,216 |
| 32 | 4 | PathTableLength | `u32`, at most 67,108,864 |
| 36 | 4 | TocCrc32 | `u32`, CRC-32 of the bytes from 64 to the end of the path table |
| 40 | 8 | DataStart | `u64`, the end of the path table rounded up to a multiple of 16 |
| 48 | 12 | Reserved | 0 |
| 60 | 4 | HeaderCrc32 | `u32`, CRC-32 of bytes 0-59 |

**Entry** (64 bytes each, sorted by `AssetId`, strictly ascending):

| Offset | Size | Field | Rule |
|-------:|-----:|-------|------|
| 0 | 16 | Id | canonical bytes (section 1), not all zero |
| 16 | 4 | Kind | `u32`, four ASCII bytes of `[A-Za-z0-9]` |
| 20 | 1 | Compression | 0 none, 1 Deflate (raw RFC 1951, `DeflateStream`), 2 Brotli (RFC 7932, `BrotliDecoder`) |
| 21 | 3 | Reserved | 0 |
| 24 | 8 | DataOffset | `u64`: `DataStart` for the first entry, then the previous entry's end rounded up to a multiple of 16 |
| 32 | 8 | StoredLength | `u64`, bytes in the file; equals `Length` when Compression is 0 |
| 40 | 8 | Length | `u64`, uncompressed bytes; 0 only with Compression 0 |
| 48 | 4 | DataCrc32 | `u32`, CRC-32 of the stored bytes |
| 52 | 4 | FirstDependency | `u32`, index into the dependency table: the sum of the previous entries' `DependencyCount` |
| 56 | 2 | DependencyCount | `u16` |
| 58 | 2 | PathLength | `u16`, at most 1,024; 0 means the asset has no path |
| 60 | 4 | PathOffset | `u32`, into the path table: the sum of the previous entries' `PathLength` |

- **Dependency table:** each entry's ids, ascending, without duplicates, none equal to the entry's own id or all zero. An id need not be in the same pack (packs can depend on each other).
- **Path table:** UTF-8, no terminators. A path is the asset's source path relative to `res://`, already normalised: `VfsPath.TryParse("res://" + path)` succeeds and its `RelativePath` equals the path (`src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:605-612`; the rules are in [platform note section 5](platform.md#5-virtual-file-system)). Paths are unique within a pack, and no path is a directory prefix of another (`a` and `a/b`).
- **Data:** the last entry's data ends at `FileLength`; padding bytes are zero. Entries are in id order, so the order of data in the file is fixed too.
- **CRC-32** is the zlib and PNG polynomial (reflected `0xEDB88320`, initial value and final XOR `0xFFFFFFFF`; `"123456789"` gives `0xCBF43926`), a small table-driven function in Assets, because the BCL's `Crc32` is in the separate `System.IO.Hashing` package. It catches truncation and bit rot, not tampering.
- **Compression is chosen per entry by the cooker** (docs/03 3.13, docs/02's Deflate and Brotli, `docs/02-tech-stack.md:70`); the reader accepts any valid stream. Already-compressed payloads (Ogg) and streamed entries are stored with 0.

### Versions

`FormatVersion` 1 is the only version. 0 is never written: `InvalidData`. Above 1: `Unsupported`, "pack format 2 is newer than this runtime reads (1); update the engine or re-cook with its cooker". A future version 2 reader decides whether to read version 1 and says so when it is written; there is no minor version, because every reader rule above is exact. A non-zero `Flags` or reserved byte is `InvalidData`: version 1 defines none, and a later version that does bumps `FormatVersion`. Each kind's payload carries its own version (section 9's textures; the owning note for the others).

### What the reader checks, in order

1. The stream is seekable and at least 64 bytes long (otherwise `Unsupported`, `InvalidData`). The header is read into a stack buffer: magic, version, `HeaderCrc32`, flags and reserved bytes, `FileLength` equal to the stream's length, each count within its limit.
2. `TocEnd = 64 + 64 x EntryCount + 16 x DependencyCount + PathTableLength`, computed in 64 bits, is at most `FileLength`, and `DataStart` is `TocEnd` rounded up to 16 and at most `FileLength`. Only then is the table of contents allocated (at most 64 MiB + 256 MiB + 64 MiB by the limits, and never more than the file) and read, and `TocCrc32` checked.
3. Every entry: ids ascending, kind characters, compression and reserved bytes, `DataOffset` exactly as specified, `DataOffset + StoredLength <= FileLength`, `Length` and `StoredLength` at most `Array.MaxLength` (2,147,483,591), the dependency and path ranges exactly as specified and inside their tables, each path valid UTF-8, normalised and unique. The last entry ends at `FileLength` (or `DataStart == FileLength` with no entries).
4. On each read (not at open): `DataCrc32` over the stored bytes, then decompression into a buffer of exactly `Length` bytes that must be filled exactly: `BrotliDecoder.TryDecompress` must succeed with `Length` written, and a `DeflateStream` must deliver `Length` bytes and then end. More or fewer is `InvalidData`, so a decompression bomb can't allocate past the declared `Length`, which the database has already checked against `MaxAssetBytes`.

Every failure is a `Result` with `InvalidData` (or `Unsupported` for a version or an unseekable stream), naming the pack and the field: "game.bepack: entry 12 DataOffset 4096, expected 4112". The pack is never partly opened.

### A worked example

Two entries: `11111111-2222-4333-8444-555555555555`, kind `BLOB`, path `hello.txt`, the 12 bytes `Hello, pack!` stored uncompressed; and `aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee`, kind `BLOB`, path `greet/b.bin`, 32 bytes of `A` stored as 5 bytes of raw Deflate, depending on the first. The table of contents ends at 228, so data starts at 240; the second entry's data starts at 252 rounded up, 256; the file is 261 bytes.

```text
0000  89 42 45 50 4b 0d 0a 1a 01 00 00 00 00 00 00 00   magic; version 1; flags 0
0010  05 01 00 00 00 00 00 00 02 00 00 00 01 00 00 00   FileLength 261; EntryCount 2; DependencyCount 1
0020  14 00 00 00 63 8c 66 b7 f0 00 00 00 00 00 00 00   PathTableLength 20; TocCrc32 b7668c63; DataStart 240
0030  00 00 00 00 00 00 00 00 00 00 00 00 d7 75 34 64   reserved; HeaderCrc32 643475d7
0040  11 11 11 11 22 22 43 33 84 44 55 55 55 55 55 55   entry 0: id
0050  42 4c 4f 42 00 00 00 00 f0 00 00 00 00 00 00 00   BLOB; none; DataOffset 240
0060  0c 00 00 00 00 00 00 00 0c 00 00 00 00 00 00 00   StoredLength 12; Length 12
0070  7e e8 76 e1 00 00 00 00 00 00 09 00 00 00 00 00   DataCrc32 e176e87e; deps 0 + 0; path 9 bytes at 0
0080  aa aa aa aa bb bb 4c cc 8d dd ee ee ee ee ee ee   entry 1: id
0090  42 4c 4f 42 01 00 00 00 00 01 00 00 00 00 00 00   BLOB; Deflate; DataOffset 256
00a0  05 00 00 00 00 00 00 00 20 00 00 00 00 00 00 00   StoredLength 5; Length 32
00b0  9a 78 33 8d 00 00 00 00 01 00 0b 00 09 00 00 00   DataCrc32 8d33789a; deps 0 + 1; path 11 bytes at 9
00c0  11 11 11 11 22 22 43 33 84 44 55 55 55 55 55 55   dependency table: entry 1 depends on entry 0
00d0  68 65 6c 6c 6f 2e 74 78 74 67 72 65 65 74 2f 62   paths: "hello.txt" "greet/b
00e0  2e 62 69 6e 00 00 00 00 00 00 00 00 00 00 00 00   .bin"; padding to 240
00f0  48 65 6c 6c 6f 2c 20 70 61 63 6b 21 00 00 00 00   entry 0 data "Hello, pack!"; padding to 256
0100  73 74 c4 0f 00                                    entry 1 data: raw Deflate of 32 x "A"
```

### Reading packs

```csharp
namespace BrutalEngine.Assets.Packs;

public sealed class AssetPack : IDisposable          // thread-safe; owns its stream
{
    public static Result<AssetPack> Open(VirtualFileSystem files, VfsPath path);
    public static Result<AssetPack> Open(Stream stream, string name);   // takes the stream: disposed on failure too
    public string Name { get; }                       // the path's text, or name; for messages
    public int EntryCount { get; }
    public AssetPackEntry GetEntry(int index);        // ArgumentOutOfRangeException
    public ReadOnlySpan<AssetId> GetDependencies(int index);
    public bool TryFind(AssetId id, out int index);   // binary search; no allocation
    public bool TryFindPath(string path, out int index);
    public Result ReadContent(int index, Span<byte> destination);   // destination.Length == Length, else ArgumentException
    public Result<Stream> OpenContent(int index);     // Compression 0 only (else Unsupported); a read-only seekable view
    public void Dispose();
}

public readonly record struct AssetPackEntry
{
    public AssetId Id { get; init; }
    public AssetKind Kind { get; init; }
    public PackCompression Compression { get; init; }
    public long Length { get; init; }
    public long StoredLength { get; init; }
    public string? Path { get; init; }                // null when PathLength is 0
}

public enum PackCompression : byte { None = 0, Deflate = 1, Brotli = 2 }
```

- **Open** runs the checks above and builds the path index; it allocates the table of contents, the dependency ids and the path strings, once. `GetEntry`, `GetDependencies` and `TryFind` allocate nothing.
- **Reads are thread-safe:** the pack keeps the one stream it opened and serialises each seek-and-read under its own lock; checksums and decompression run outside it. `ReadContent` allocates nothing beyond the decoder's state for compressed entries; `OpenContent` returns a view whose reads take the same lock, so a streamed music track and the workers share the file. The stream from `VirtualFileSystem.OpenRead` (`src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:621`) must be seekable, as the directory and in-memory file systems' streams are.
- **Dispose** closes the stream; reads and open views fail with `ObjectDisposedException` afterwards. The caller disposes a pack after unmounting it from every database and file system.

### Packs under `res://`

```csharp
public sealed class AssetPackFileSystem : IFileSystem    // read-only; thread-safe; owns nothing
{
    public static Result<AssetPackFileSystem> Create(AssetPackFileSystemOptions options);
    public bool IsReadOnly { get; }                                    // true
    public Result<VfsEntryKind> GetEntryKind(string relativePath);
    public Result<byte[]> ReadAllBytes(string relativePath);
    public Result<Stream> OpenRead(string relativePath);
    public Result<IReadOnlyList<VfsEntry>> List(string relativePath);
    public Result WriteAllBytes(string relativePath, ReadOnlySpan<byte> contents);   // Unsupported
    public Result Delete(string relativePath);                                        // Unsupported
    public Result CreateDirectory(string relativePath);                               // Unsupported
}

public sealed record AssetPackFileSystemOptions
{
    public required IReadOnlyList<AssetPack> Packs { get; init; }
    public IFileSystem? Fallback { get; init; }    // loose files, e.g. a DirectoryFileSystem over the application directory
}
```

- **Paths** are the entries' paths; directories are implied by them. `ReadAllBytes` returns the uncompressed payload in a new array, `OpenRead` the entry's view (uncompressed entries) or a read-only `MemoryStream` over the payload, `GetEntryKind` and `List` answer from the index (sorted ordinally, `Length` the uncompressed size), and writes, deletes and `CreateDirectory` fail with `Unsupported`, as for any read-only mount (`docs/design/platform.md:430`). It implements Platform's `IFileSystem` exactly as merged (`src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:573-581`).
- **Fallback:** a path no pack holds is passed to `Fallback`; `List` merges both, and pack entries win on equal names. Without a fallback, it is `NotFound`. A path in two packs, or one that is a file in one pack and a directory in another, fails `Create` with `InvalidData`.
- **Mounting.** In a release build the game opens its packs through the host's `res://` (the application directory, `docs/design/platform.md:421`), then replaces that mount: `files.Unmount("res")`, then `files.Mount("res", packFileSystem)` with a fallback over the same directory (`DirectoryFileSystem`, `:437`), so `res://` reads see packed files first and loose files second (`VirtualFileSystem.Mount`/`Unmount`, `src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:620`, `:623`). The open packs keep their streams across the remount. When Runtime implements step 90 it does this itself.

## 7. Development mode

- **Sources.** With an `Importer` the database is in development mode. The host mounts the game's `assets/` folder as `res://` in development (`docs/design/platform.md:421`). `Create` walks `res://` recursively through `VirtualFileSystem.List` and, for every `<file>.meta` sidecar (ADR-0005; next to the file it describes), calls `ReadMeta` and adds the declared assets to the catalogue. Two sidecars that declare the same id are logged at `Error` naming both paths, and that id fails to load (`InvalidData`) until one is fixed. A source without a sidecar is ignored with a `Warning`; creating sidecars is the cooker's (#283).
- **Loads** call `Import` on a worker instead of reading a pack; the rest of the pipeline (dependencies, loaders, budgets) is the same. A source's dependencies are known only when its import returns, so section 3's dependency walk runs then, on the owner thread, before the loader is called. Import results aren't cached across runs (that is the cooker's incremental cache, #283).
- **Watching. Decision (this note): `FileSystemWatcher` as a change signal, with content hashes as the test.** The VFS has no change notifications and its listings carry no modification times (`VfsEntry` is name, kind and length, `src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:593-600`), so polling through it would have to hash the whole tree; Platform is frozen, so the VFS can't grow a watch API now. `AssetDevelopmentOptions.WatchDirectory` is the absolute OS directory backing `res://`. A watcher (subdirectories included) records changed relative paths into a bounded set (4,096 paths; beyond that, or on the watcher's `Error` event, such as a buffer overflow, a full rescan is flagged). After `Debounce` (default 200 ms, on `Clock`) with no new event, a worker re-reads every affected `.meta`, source and `ReadPaths` file through the VFS and compares a SHA-256 of their bytes with the hashes recorded at the last import, so editors' save patterns (temporary files, double writes) never cause a spurious reload. `RescanSources()` flags a full rescan explicitly; with `WatchDirectory` null it is the only trigger, which is how tests drive hot reload over an `InMemoryFileSystem` (section 12).

```csharp
public sealed record AssetDevelopmentOptions
{
    public string? WatchDirectory { get; init; }                        // absolute; null: RescanSources only
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(200);   // 0..10 s
}
```

- **Which bytes are hashed.** Before calling `Import`, the worker reads the `.meta`, the source and the previous import's `ReadPaths` through the VFS and hashes them; those are the hashes recorded for that import. They are taken before the importer's own reads, so an edit that lands during the import differs from them and is caught by the next check: at worst one extra import, never a lost edit. A path an import reports that wasn't hashed before it ran (a first import, or a newly read file) can only be hashed when `Import` returns, possibly after an edit the importer missed, so such an import runs once more at once with every path hashed first, and only that run's result is published. If the `ReadPaths` still grow after 4 runs, the asset fails with `InvalidData`.
- **Change generations.** Every catalogued asset has a change generation. Before applying completions, `ProcessCompletions` takes the paths the watcher recorded since the last call (or `RescanSources`' flag, which counts as every path) and increments the generation of every asset whose `.meta`, source or `ReadPaths` include one, whether it is loaded, failed or still loading. A request records the generation at dispatch, so an import in flight when its files change is re-queued by section 3's stale-completion rule, not published. The debounce and the hash comparison then decide which loaded or failed assets are imported again.

- **Hot swap.** A changed asset that is loaded or failed is imported again at `Normal` priority; one still loading has its request re-queued (above). In `ProcessCompletions` its slot gets the new object, the old one is released with `Replaced`, `GetVersion` goes up by one and `Reloaded` is raised (`SourceChanged`). The handle and the reference count don't change (docs/03 3.13: "handles stay valid"). Changed dependency lists take references on new dependencies before the swap and release dropped ones after it; a new cycle fails the new version.
- **Dependants are notified by being loaded again:** every loaded asset that depends on a swapped one, transitively, is loaded again from its own payload once its dependencies are swapped, in dependency order, and raises `Reloaded` with `DependencyChanged`; dependants still loading are re-queued by section 3's stale-completion rule. So a loader that copied a value out of a dependency (a font reading its atlas size) never keeps a stale copy. Systems that cache derived data compare `GetVersion`, which allocates nothing.
- **Failed reloads keep the old version:** the asset stays `Loaded` with its old object, the error is logged at `Error` and raised in `Reloaded` with `Error` set. A sidecar edited to another kind fails the new version (`InvalidData`). A deleted source keeps the loaded object (`Warning`); new loads of its id fail with `NotFound`. A new or moved sidecar updates the catalogue, so moves keep the id and its handles.
- **What Release builds contain.** The game's project references `BrutalEngine.Importers` with `Condition="'$(Configuration)' == 'Debug'"` and creates the importer in `#if DEBUG` code, so a Release build neither compiles against nor ships it; #283 designs that reference and the Importers packaging. Assets itself contains no importer code: only the seam's interfaces and the watcher, which is BCL. **The check:** a CI step on the M2 sample game's Release publish (JIT and NativeAOT) fails if the output contains `BrutalEngine.Importers.dll`, if the JIT publish's `.deps.json` names `BrutalEngine.Importers`, or if `dotnet msbuild <game> -getItem:ProjectReference -p:Configuration=Release` lists it; the sample's issue adds it (section 13).

## 8. Memory budgets

```csharp
public readonly record struct AssetBudget(AssetKind Kind, long MaxBytes, AssetBudgetAction Action = AssetBudgetAction.Warn);
public enum AssetBudgetAction { Warn = 0, FailLoad }
public readonly record struct AssetBudgetUsage
{
    public AssetKind Kind { get; init; }
    public long Bytes { get; init; }
    public long MaxBytes { get; init; }              // 0: no budget for the kind
    public bool IsExceeded { get; init; }
}
```

- **Sizes are CPU-side** until Graphics has a GPU memory query (a Graphics addition after the 0.2.0 release): an asset counts its uncompressed payload `Length` while it is loaded or loading. For M2's uncompressed textures that is also their GPU texel size (section 9). `Stream` assets count 0: their data stays in storage, and their decode buffers are Audio's to report.
- **Exceeding a budget.** `Warn`: the load completes, a `Warning` is logged once each time the kind crosses its budget, and `GetBudgetUsage` reports `IsExceeded`. `FailLoad`: a load that would take the kind over its budget fails with `OutOfMemory` ("TXTR budget 256 MiB exceeded: 270 MiB"), checked when the request is dispatched (pack entries, whose sizes are known) and again when it completes (both sources). Nothing is evicted: assets with references are in use, and assets without are already unloaded at the next safe point.
- One budget per kind (`ArgumentException` for duplicates, `MaxBytes <= 0`); kinds without a budget are unlimited.

## 9. GPU resources and audio

**Cooked texture payload** (kind `TXTR`, payload version 1), written by the image importers (#283):

| Offset | Size | Field | Rule |
|-------:|-----:|-------|------|
| 0 | 2 | Version | `u16`, 1 (above: `Unsupported`; 0: `InvalidData`) |
| 2 | 2 | Format | `u16`, a `TextureFormat` value: `Rgba8UnormSrgb` (4) or `R8Unorm` (1) (`src/BrutalEngine.Graphics/PublicAPI.Unshipped.txt:805`, `:797`); others `Unsupported` |
| 4 | 4 | Width | `u32`, 1..16,384 texels |
| 8 | 4 | Height | `u32`, 1..16,384 texels |
| 12 | 4 | Reserved | 0 |
| 16 | W x H x 4 or 1 | Texels | rows top to bottom, tightly packed; the payload is exactly this long |

```csharp
namespace BrutalEngine.Assets.Loaders;

public sealed class TextureAsset                 // immutable; any thread may read it, the device's thread uses the handles
{
    public TextureHandle Texture { get; }
    public TextureViewHandle View { get; }        // the texture's default view
    public int Width { get; }                      // texels
    public int Height { get; }
    public TextureFormat Format { get; }
}

public sealed class TextureLoader : IAssetLoader<TextureAsset>
{
    public TextureLoader(GraphicsDevice device);                            // the database's device; doesn't own it
    public AssetKind Kind { get; }                                          // TXTR
    public AssetLoaderTraits Traits { get; }                                // OwnerThread, Bytes, RecreateOnDeviceLoss
    public Result<TextureAsset> Load(in AssetLoadContext context);
    public void Release(TextureAsset asset, AssetReleaseReason reason);     // Destroy, except with DeviceLost
}
```

- **Load** (owner thread, in `ProcessCompletions` or `LoadNow`): checks the payload (`InvalidData`), checks the size against the device's `MaxTextureDimension2D` (`Unsupported`), then `CreateTexture` with `Usage = Sampled | CopyDestination`, one mip and `DebugName` the asset's name (`docs/design/graphics.md:296`, `:320-330`), and `WriteTexture` of the texels (`:391`). Writes made between frames go into pending upload blocks copied at the start of the next submitted frame (`:454`), so a texture is usable from the next submitted frame on. If the write fails the texture is destroyed and the failure returned. Premultiplied or straight alpha is the importer's choice (#283); the loader uploads texels as given. `Release` destroys the texture (its view goes with it, `:67`) except with `DeviceLost`.
- **Device loss.** With `Graphics` set, `Create` registers an internal `IGpuResourceOwner` implementation (`docs/design/graphics.md:740-744`) with `GraphicsDevice.RegisterOwner` (`:724`), so docs/03 3.5's "recreates GPU resources from the asset database" (`docs/03-architecture.md:107`) happens through the device's contract (`docs/design/graphics.md:750`: "M2's asset database registers as one owner and reloads what it loaded"), and `Dispose` disposes the registration. `OnDeviceLost` releases every object whose loader has `RecreateOnDeviceLoss` with `DeviceLost` (handles are dropped, the device isn't called). `OnDeviceRecreated` loads each of them again, dependencies first, reading the payload again from its pack or importer, swaps it in (`Reloaded` with `DeviceRecreated`, `GetVersion` + 1) and reloads their dependants as in section 7, all on the owner thread inside `Recover`, which raises those `Reloaded` events too. Recreation raises the version, so a dependant still on a worker when `Recover` runs is re-queued by section 3's stale-completion rule and loads against the recreated object. A data or I/O failure fails that asset and is logged; recovery continues. A device failure (`Unavailable`, `OutOfMemory`) is returned, and the host treats it as fatal (`docs/design/graphics.md:748`). The GraphicsDevice front end is specified in the render interface note and is still in review; Assets cites the note, not code.
- **Audio** owns the decoded clip type `AudioClip` (PCM in `INativeAllocator` memory), the streaming source contract and their creation APIs (#281). Assets owns two loaders that call that API:

```csharp
public sealed class AudioClipLoader : IAssetLoader<AudioClip>          // thread-safe
{
    public AudioClipLoader(INativeAllocator allocator);                 // where clip PCM lives; the caller owns it
    public AssetKind Kind { get; }                                      // ACLP
    public AssetLoaderTraits Traits { get; }                            // Worker, Bytes
    public Result<AudioClip> Load(in AssetLoadContext context);
    public void Release(AudioClip asset, AssetReleaseReason reason);    // disposes the clip
}

public sealed class AudioStreamAsset                                   // immutable; thread-safe
{
    public AssetContentSource Content { get; }                         // the encoded stream, stored uncompressed in packs
}

public sealed class AudioStreamLoader : IAssetLoader<AudioStreamAsset> // stateless; thread-safe
{
    public AudioStreamLoader();
    public AssetKind Kind { get; }                                         // ASTM
    public AssetLoaderTraits Traits { get; }                               // Worker, Stream
    public Result<AudioStreamAsset> Load(in AssetLoadContext context);     // keeps context.Content
    public void Release(AudioStreamAsset asset, AssetReleaseReason reason); // no-op; the source closes itself
}
```

- **Clips:** `Load` passes `context.Data` (the cooked clip payload, whose layout #281 defines with its creation API) to Audio's clip creation and returns its `Result<AudioClip>`; `Release` disposes the clip. **Streams:** each playback needs its own decoder state, so the asset is the source, not a decoder: `Load` keeps `context.Content`, and the game creates one Audio streaming source per playback through Audio's API from `Content.OpenRead()`'s stream. What Assets requires of #281's API: clip creation from a `ReadOnlySpan<byte>` and an `INativeAllocator`, callable on any thread, returning `Result<AudioClip>`; disposing a clip on the owner thread while a voice plays it is safe (Audio's rule); a streaming source created over a seekable read-only `Stream` that it disposes, read on Audio's decoder thread. If #281's creation API also needs its engine object, `AudioClipLoader`'s constructor takes it as well; the issue that adds the loaders follows the merged audio note.

## 10. Threading, ownership and allocation

**Threads.** `AssetDatabase` and `AssetGroup` are owner-thread only (the thread that called `Create`, the main thread in a host): **every** member checks `ThreadAffinity.ThrowIfNotCurrentThread` (`src/BrutalEngine.Core/PublicAPI.Unshipped.txt:257`) in every build and throws `WrongThreadException` naming the API, as in Platform and Graphics (stricter than docs/03's debug-only assertion, `docs/03-architecture.md:87`). `Reloaded` is raised on that thread. Workers never call the database; they post results.

| Type | Threads | Ownership | Allocation |
|------|---------|-----------|------------|
| `AssetId`, `AssetKind`, `AssetHandle<T>`, `AssetSource`, `AssetPackEntry`, `AssetBudget`, `AssetBudgetUsage`, `AssetStatistics`, `AssetLoaderTraits`, the enums | any (immutable values) | values | none, except `ToString` and `Parse` failures |
| `AssetDatabaseOptions`, `AssetDevelopmentOptions`, `AssetImportRequest`, `ImportedAsset`, `AssetPackFileSystemOptions` | any (immutable; read by the call that takes them) | the caller's | at creation |
| `AssetDatabase` | owner thread | the creator; `Dispose` cancels and joins the workers, disposes the groups, releases every object with `DatabaseDisposed` (dependants first), then disposes the device registration | the list below |
| `AssetGroup` | owner thread | the caller of `CreateGroup`; the database disposes it at the latest | `Load`; `Dispose` |
| `AssetLoadContext` | the loader's call only (`ref struct`) | the database's | none |
| `AssetContentSource`, `AssetPack`, `AssetPackFileSystem` | any (thread-safe) | the database's; the caller's (owns its stream); the caller's (owns nothing) | open and reads |
| Loaded objects (`TextureAsset`, `BytesAsset`, `AudioStreamAsset`, `AudioClip`, others' types) | as their type says | the database: released through their loader; the game never disposes them | at load |
| Loaders, the importer | `Load` per traits, `Release` owner thread; importer on workers | the caller's; the database holds them | at load and import |

**Owner-thread allocation.** Steady-state frames allocate nothing in: `ProcessCompletions` with nothing completed, nothing to unload and no reload; `TryGet`, `Get`, `GetState`, `GetVersion`, `GetId`, `GetReferenceCount`, `Contains`, `TryGetKind`; `AddReference`; `Release` (the pending-unload list grows only past its capacity, reserved at `Create` for 256 entries); `Statistics`; `GetBudgetUsage`; handle copies and equality; `AssetId` comparison and `TryFormat`. These allocate and belong to load time (Runtime's `MarkFrameNonSteady`, `docs/design/runtime.md:199`): `Create`, `MountPack`, `Load` and `LoadNow` (requests, slot growth), `CreateGroup` and group loads, completions that publish results, unloads that run loader `Release`s, reloads and `Reloaded` events, failures (their messages). Workers allocate per load (buffers, decoder state, the loader's object); their memory is bounded by section 5.

**Unmanaged memory** is the loaders' own (clip PCM through the `INativeAllocator` given to `AudioClipLoader`; GPU memory through the device). The database's payload buffers are managed arrays, released after each load.

## 11. Failure modes

| Failure | Result |
|---------|--------|
| Unknown id; no loader for its kind; `T` not the kind's type | `Load`/`LoadNow` fail synchronously: `NotFound`, `Unsupported`, `InvalidData`; no reference taken |
| A pack or source file missing or unreadable | The handle `Failed` with the VFS's kind (`NotFound`, `Unavailable`, `Io`); `MountPack`/`AssetPack.Open` fail with it |
| Corrupt pack: bad magic, checksums, offsets, counts, paths, sorting, compression | `AssetPack.Open` fails `InvalidData` naming the field (section 6); a bad entry checksum or stream fails that asset only |
| Newer pack format or texture payload version | `Unsupported`, with the versions in the message |
| Duplicate id across packs or sidecars | `MountPack` fails `InvalidData`; sidecars: `Error` logged, the id fails `InvalidData` |
| Dependency cycle, or a failed dependency | Every asset on the cycle, and every dependant, `Failed` (`InvalidData`; the dependency's kind) |
| Loader or importer failure | The handle `Failed` with its `Result`'s error; an exception becomes `Unspecified` and is logged with its stack |
| Asset over `MaxAssetBytes`; a `FailLoad` budget exceeded | `Unsupported`; `OutOfMemory` |
| Hot reload fails | Old version kept, `Error` logged, `Reloaded` with `Error` |
| Device recreation fails | That asset `Failed`; a device failure is returned to `Recover` (fatal) |
| `Create` fails (development scan, options) | `Result` failure, nothing started; misuse is an exception; the host treats a failed start as fatal |
| Wrong thread; a stale or default handle; more `Release`s than references | `WrongThreadException`; `StaleHandleException`, `ArgumentException`; `InvalidOperationException` |
| A second loader for a kind; `UnmountPack` with live assets; `Get`/`GetError` in the wrong state; `RescanSources` outside development mode; use after `Dispose` | `InvalidOperationException`; `ObjectDisposedException` |

## 12. Testing

All in `tests/BrutalEngine.Assets.Tests` on Windows and Linux CI, without a GPU except the texture loader's, over `InMemoryFileSystem` (`src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:582-592`) and a fake importer.

- **Unit tests** for every rule: `AssetId` text form (every rejection: braces, whitespace, wrong hyphens, all-zero, upper-case accepted, lower-case written), ordering equal to the text order, canonical bytes against hand-written vectors; `AssetKind` characters each side of every range; the CRC-32 against the published check value; every reader check of section 6 with one row per field and per side of each limit, the version rule (0, 1, 2), and a row proving a rejected pack is never partly opened; database rules (synchronous failures, priorities and raising, the same handle per id, revival before the safe point, staleness after it, cycles, failed dependencies, `LoadNow` stealing and waiting, groups, budgets in both modes, the in-flight bound with an asset larger than it, cancellation at each worker stage, every misuse exception).
- **Pack round trips** through a reference writer in the test project, written from section 6's tables, not from the reader: random entry sets (kinds, sizes 0 and over-sampled boundaries, each compression, dependencies, paths with nesting) written and read back with CsCheck; the worked example's 261 bytes as a committed fixture that must open and read exactly as section 6 says; `TryFind` against a sorted-list model.
- **Reference counts against a model:** CsCheck sequences of `Load`, `AddReference`, `Release`, group loads and disposes, `ProcessCompletions`, source changes and failures, with `WorkerCount = 0`, against a model written separately; after every step each handle's state, count, validity and version, `Statistics` and the loader's release log equal the model's.
- **Hot reload** over `InMemoryFileSystem` with `RescanSources`: a change swaps the object and keeps the handle; dependants reload in order; a failed reload keeps the old version; a moved sidecar keeps the id; an unchanged rewrite (same bytes) doesn't reload. Two in-flight rows, each with `WorkerCount = 1` and a loader or importer held on a gate (waits bounded by the test's token): a dependency swapped while a dependant's `Worker` loader is held, where the dependant's first result is released with `Replaced` and never published, the old dependency object is released only after that, and the dependant ends `Loaded` against the new version; and a source edited while its first import is held, then `RescanSources`, where the asset ends `Loaded` with the edited bytes. Also: an edit between the pre-import hash and the importer's read causes one extra import; an import whose `ReadPaths` grow runs again, and one whose `ReadPaths` grow 4 times fails. One test drives a real `FileSystemWatcher` over a temporary directory, bounded by the test's token, never by sleeps.
- **Concurrency:** two and eight workers loading a random graph, with results compared to `WorkerCount = 0`; `AssetPack` reads from several threads at once (a barrier, then concurrent `ReadContent` and `OpenContent` views).
- **Allocation tests** (`GC.GetAllocatedBytesForCurrentThread`, background GC off in the project, as in Core): 1,000 frames of `ProcessCompletions` with nothing to do, every read member of section 10's list, and `AddReference`/`Release` pairs allocate 0 bytes.
- **Fuzz targets** (SharpFuzz, nightly, docs/05's fuzz tier, `docs/05-testing-strategy.md:19`): `AssetPack.Open` plus every `ReadContent`, and the table of contents alone; the corpus is seeded from the reference writer's outputs and the cooker's fixtures. Crashes become regression rows.
- **GPU:** the texture loader against the graphics conformance device on lavapipe in CI and the RTX 2070 SUPER locally (each PR says where it ran): upload and read back texels, and device-loss recovery through the device's fault-injection seam, recreating and reloading dependants, including a dependant held on a worker during `Recover`, which is re-queued and ends `Loaded` against the recreated texture.
- **Cook-to-load round trips** with #283's cooker (ADR-0005): cook fixture sources, mount the pack, load every asset, and compare with a development-mode import of the same sources, byte for byte.
- **Architecture:** `BrutalEngine.Assets` joins `tests/BrutalEngine.Architecture.Tests` (layering, binding surface, static state, the ship gate).

Mutation self-checks, for example: compare ids as signed bytes; skip the `DataOffset` alignment rule; accept a decompressed stream longer than `Length`; unload at `Release` instead of at the safe point; skip reloading dependants.

## 13. Issue split

Sized line by line (source with every XML doc section, `PublicAPI.Unshipped.txt` lines, tests about 1:1). The issues share one project and one `PublicAPI.Unshipped.txt`, so they run in this order unless noted.

| Issue | Scope | Blocked by | Src + docs | API | Tests | Est. |
|-------|-------|------------|-----------:|----:|------:|-----:|
| A1 feat(assets): add the Assets project with asset ids and kinds | Creates `src/BrutalEngine.Assets` (milestone 2, ADR-0016) and its tests, the Architecture.Tests reference and the solution line; `AssetId`, `AssetKind` | this note | 210 | 45 | 220 | ~475 |
| A2 feat(assets): open and validate packs | `Crc32`, `PackCompression`, `AssetPackEntry`, `AssetPack.Open` with every header and table check, `TryFind`, `TryFindPath`, `GetDependencies`; the reference writer and the worked-example fixture | A1 | 230 | 25 | 240 | ~495 |
| A3 feat(assets): read pack contents | `ReadContent` with checksums and both decompressors, `OpenContent` views, the thread-safety tests, the round-trip property tests | A2 | 140 | 5 | 220 | ~365 |
| A4 feat(assets): serve packs as a file system | `AssetPackFileSystem` and its options, the fallback, the remount example as a test | A3 | 160 | 15 | 200 | ~375 |
| A5 feat(assets): add handles and the loader contract | `AssetHandle<T>`, `AssetState`, `IAssetLoader<T>`, `AssetLoaderTraits` and its enums, `AssetLoadContext`, `AssetContentSource`, `BytesAsset`, `BytesLoader` | A1 | 220 | 50 | 160 | ~430 |
| A6 feat(assets): add the asset database with synchronous loads | Options, `Create`, `Dispose`, `RegisterLoader`, `MountPack`/`UnmountPack`, the catalogue, `LoadNow`, dependencies and cycles, reference counts, safe-point unloads, the read members; the reference-model property test | A3, A5 | 270 | 40 | 240 | ~550 |
| A7 feat(assets): load assets asynchronously | `Load`, priorities, workers and `WorkerCount = 0`, the completion queue, owner-thread loaders, cancellation, the in-flight bound, `AssetStatistics`; concurrency and allocation tests | A6 | 250 | 20 | 260 | ~530 |
| A8 feat(assets): add load groups and memory budgets | `AssetGroup`, `AssetBudget` and its types, `GetBudgetUsage` | A7 | 150 | 30 | 200 | ~380 |
| A9 feat(assets): load source assets in development mode | The import seam types, `ReadMeta` scanning, imports on workers, duplicate sidecars; a fake importer | A7 | 200 | 35 | 220 | ~455 |
| A10 feat(assets): hot-reload changed sources | `AssetDevelopmentOptions`, `RescanSources`, the watcher and debounce, hashes, swaps, dependant reloads, `Reloaded` and its types, `GetVersion` | A9 | 230 | 25 | 260 | ~515 |
| A11 feat(assets): load textures and recreate them after device loss | `TextureAsset`, `TextureLoader`, the payload checks, the device-owner registration and recovery; GPU tests | A7; the GraphicsDevice front end on `main` | 200 | 20 | 230 | ~450 |
| A12 feat(assets): load audio clips and streams | `AudioClipLoader`, `AudioStreamAsset`, `AudioStreamLoader` against the merged audio API | A7; #281 merged and Audio's clip and stream APIs on `main` | 90 | 15 | 120 | ~225 |
| A13 test(assets): fuzz the pack reader | SharpFuzz targets (the tool docs/05 names; not yet in `Directory.Packages.props`), the seeded corpus and the nightly job | A3 | 80 | 0 | 60 | ~140 + CI |

A6 and A7 are the largest parts: reference counts, the catalogue and unloading only make sense together, as do the workers and the completion queue. The `Rendering2D -> Assets`, `Text -> Assets` and `Input -> Assets` edges are added by those modules' first loader issues (Input's is the input note's I10), each blocked by A5 and A6. The Release check of section 7 is part of the M2 sample game's issue, blocked by #283's packaging. Each issue's CHANGELOG entry goes under `## Next: 0.3.0 (not in the next release)`.

## Consistency with ADR-0005 and Architecture 3.13

| They say | This note | Why |
|----------|-----------|-----|
| "Asset decoding" runs on job workers (docs/03 3.2, `docs/03-architecture.md:82`) | Decoding runs on the database's own workers | The engine has no job system yet; a worker pool can be swapped for jobs behind the same API |
| Thread checks "asserted in debug builds" (3.2) | Checked in every build | As in Platform and Graphics |
| "Dependants are notified" (3.13) | Dependants are loaded again, then `Reloaded` is raised for them | A loader may have copied values out of the dependency |
| Packs mounted at `res://` (`docs/03-architecture.md:129`) | The game remounts `res://` until Runtime's step 90 | Runtime is frozen until the 0.2.0 release |

Every ADR-0005 rule holds: the cooker writes versioned packs, development builds import in-process through the same importers, identity is a GUID in `.meta` sidecars, cook-to-load round trips and pack fuzzing are tested (section 12). Nothing here deviates from an ADR.

**Layer edges this note needs:** none for Assets itself (Core, Platform, Graphics and Audio are lower layers). Three listed same-layer edges come with their modules' loader issues, each added to `EngineLayers` (today's only L3 edge is Rendering2D → Text, `tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:62`) and to the table in [architecture tests](architecture-tests.md#the-layer-map):

| Edge | Reason |
|------|--------|
| Rendering2D → Assets | Rendering2D registers its atlas loader (kind `ATLS`, section 1) |
| Text → Assets | Text registers its font loader (kind `FONT`) |
| Input → Assets | Input registers its input-map loader (the input note, section 9) |

## Not verified

- The note is design only; nothing in it has run. The worked pack example was produced and checked by a scratch reference writer and reader written from section 6, outside the repository.
- `DirectoryFileSystem` and the `GraphicsDevice` front end (textures, uploads, device-owner registration) are cited from their notes; their code is in review.
- That the BCL's Deflate and Brotli encoders give identical bytes on Windows and Linux for the same runtime version: the reader doesn't depend on it, the cooker's determinism does (#283).
- `FileSystemWatcher` behaviour (inotify watch limits on Linux, buffer overflows on Windows) is taken from its documentation; the rescan fallback doesn't depend on it, and A10 tests a real watcher on both CI OSes.
- Audio's creation APIs don't exist yet (#281); section 9 states what Assets requires of them.

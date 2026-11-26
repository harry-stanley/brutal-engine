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
- **`LoadNow`** loads the asset and its missing dependencies on the calling thread, blocking until done, and returns a handle in `Loaded` with one reference, or the failure with no reference taken. Requests already queued for those assets are taken off the queue and run inline; one a worker has already started is waited for, never run twice. It exists for start-up and loading screens; in a measured frame it makes the frame non-steady.
- **Dependencies.** Before an asset is handed to its loader, the database takes one reference on each id it depends on (owned by the dependant, released when it unloads) and loads them first. The dependency walk runs on the owner thread over catalogue metadata, colouring nodes, so a cycle fails every asset on it with `InvalidData` naming the cycle; cycles are a cook-time error too (#283). A dependency that fails fails its dependants with the dependency's kind and the message "dependency <id> failed: ...".
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
- **Dependencies** are all `Loaded` before `Load` is called. The context hands out the loaded objects for immediate use and handles for keeping; the handle needs no reference of its own, because the dependant's reference keeps the dependency alive. When a dependency is swapped, its loaded dependants are loaded again (section 7), so values a loader copied out of a dependency are never stale.
- **Failures** are the loader's `Result`: `InvalidData` for a bad payload, `Unsupported` for a valid one this device or version can't use, the device's kinds for GPU failures. A loader throws only for its own bugs; an exception from `Load` fails the asset with `ErrorKind.Unspecified` and the exception's message, and is logged at `Error` with the stack.
- **`Release` reasons:** `Unloaded` (count reached zero), `Replaced` (a hot-reload or recreation swap installed a new object), `DeviceLost` (drop GPU handles without calling the device, section 9), `DatabaseDisposed`. A loader whose objects own nothing makes it a no-op.

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

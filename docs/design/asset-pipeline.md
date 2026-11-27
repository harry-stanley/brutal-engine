# Design: asset pipeline (`BrutalEngine.Importers`, `brutal-cook assets`)

## Purpose

The asset pipeline turns a game's source assets (images, Aseprite files, audio, fonts, shaders, tilemaps, input maps) into the cooked payloads the runtime loads: offline into versioned packs with the `brutal-cook assets` command, and in-process in development builds through the same importer code ([ADR-0005](../adr/0005-offline-asset-cooking.md); [Architecture 3.13](../03-architecture.md#313-assets-brutalengineassets-tool-brutalenginecook), `docs/03-architecture.md:184-186`; [roadmap M2](../09-roadmap.md#m2-2d-essentials-03), `docs/09-roadmap.md:73`). It defines the `.meta` sidecar files, the importer library `BrutalEngine.Importers`, every M2 importer's input and output, and the cooker's `assets` command, so the importer and cooker issues (section 9) can be drafted from it.

The runtime side is the [asset database note](assets.md) (#282). This note uses its `AssetId` and `AssetKind` ([section 1](assets.md#1-identity)), writes packs in its format ([section 6](assets.md#6-the-pack-format)), writes its texture payload ([section 9](assets.md#9-gpu-resources-and-audio)) and implements its import seam ([section 4](assets.md#the-import-seam-development-builds)) unchanged; it redefines none of them.

- Decision (maintainer, 2026-11-27): **layering.** The import seam lives in Assets and `BrutalEngine.Importers` implements it. A game references Importers in its Debug configuration only, so Release output contains no importer code (section 7).
- Decision (maintainer, 2026-11-27): **parsers.** Aseprite, Tiled and LDtk are read by in-house parsers, with input-size limits and fuzz targets (section 6). No new package.
- Decision (maintainer, 2026-11-27): **textures** are uncompressed in M2: `Rgba8UnormSrgb`, and `R8Unorm` for glyph atlases (the asset database note's texture payload). Compressed formats are a later, optional addition.
- Decision (maintainer, 2026-11-27): **tilemaps** are orthogonal only in M2; isometric and hexagonal maps come in M3, as `docs/03-architecture.md:193` allows.
- **Audio decoding** follows ADR-0017, which is in progress (#279). The decision as recorded in #279: `Ahwoo.Brutal.Sdl.Mixer` is used as a decoder only, in the Audio backend and in the cook-time audio importer; WAV, Ogg Vorbis, FLAC and MP3; no Opus in 0.3; short sounds are cooked to PCM, and music ships as Ogg Vorbis and streams. If the merged ADR differs, the audio importer issues follow the ADR and this note is corrected in the same PR.
- **Payload ownership.** The module that owns a runtime type owns its cooked payload. Textures (`TXTR`) are the asset database note's; this note's image importers write them. Sprite atlases (`ATLS`) and materials are the 2D renderer note's; fonts (`FONT`: pre-baked glyph atlases and metrics) the text note's; tile data (`TMAP`) the tilemap note's; audio clips (`ACLP`) the audio note's; input maps (`INPM`) the input note's. None of those is written yet except the input note, which is in review. For them this note says what each importer produces and names the owner, but fixes no byte layout. This note owns the importers (their settings and algorithms, such as the atlas packer), `.meta` files, the shader payload `SHDR` (section 3) and the `assets` command with its pack writing.

**Projects.** All three are L6 tools, which may use any layer and which runtime modules never reference (`tests/BrutalEngine.Architecture.Tests/LayeringRule.cs:62`):

| Project | Role | Layer map |
|---------|------|-----------|
| `tools/BrutalEngine.Importers` | `.meta` files, the importer contract and registry, every importer except shaders, the import seam's implementation. Packed as a library in 0.3.0 (section 7) | listed (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:48`) |
| `tools/BrutalEngine.Importers.Shaders` | The Slang shader importer, separate so that only shader work restores the 99 MB Slang native package (`docs/02-tech-stack.md:32`). ADR-0012 keeps that package out of the general build and test jobs, so this project and its tests build wherever M1 builds the cook tool (the shader job and local cooking), however M1's issue arranges it | new: L6 `Tool`, with the edges `Importers.Shaders -> Importers` and `Cook -> Importers.Shaders` |
| `tools/BrutalEngine.Cook` | The `brutal-cook` tool. Created in M1 with only a Slang shader command (ADR-0012); this note adds the `assets` and `meta` commands. No public API: every type is internal, tested through `BrutalEngine.Cook.Tests` | listed (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:47`), with the edge `Cook -> Importers` (`:72`) |

Same-layer edges between tools must be listed (`tests/BrutalEngine.Architecture.Tests/LayeringRule.cs:72`), so the shader project's issue adds its two edges to `EngineLayers` and to the [layer map table](architecture-tests.md#the-layer-map). `BrutalEngine.Importers.Shaders` doesn't end in a BRUTAL type name: a `BrutalEngine.Importers.Slang` namespace would hide BRUTAL's static `Slang` class from the project's own files.

**Dependencies.** `Ahwoo.Brutal.Stb` decodes images (`docs/02-tech-stack.md:34`); its nuspec depends on `Ahwoo.Brutal.Strings`, so Importers takes `Microsoft.Extensions.ObjectPool`, which [ADR-0011](../adr/0011-brutal-objectpool-dependency.md) accepts for L6 tools. `Ahwoo.Brutal.Sdl.Mixer` decodes audio (ADR-0017, in progress). Fonts are rasterised by the Text module, which uses `Ahwoo.Brutal.FreeType` (`docs/02-tech-stack.md:33`), so baked and runtime glyphs come from one rasteriser. `Ahwoo.Brutal.Slang` compiles shaders (`docs/02-tech-stack.md:32`). JSON is `System.Text.Json` with source-generated contexts (`docs/02-tech-stack.md:64`), compression the BCL's (`docs/02-tech-stack.md:70`). No other package. Each version the first user adds to `Directory.Packages.props` is for a package no 0.2.0 project resolves, so it doesn't touch the release freeze ([ADR-0016](../adr/0016-trunk-based-milestones.md), decision 4); the issue proves it by showing no releasing project's `packages.lock.json` changes.

**Frozen modules.** Core, Platform, Graphics, World, Runtime and DebugOverlay are frozen until 0.2.0 is released (ADR-0016). The pipeline needs no change in them: it uses Platform's VFS, the `DirectoryFileSystem` of the [platform note](platform.md#5-virtual-file-system) (`docs/design/platform.md:437`; its code is in review) and Graphics' enum values as published. Two later pieces do need frozen files and wait for the tag: running the cooker from `dotnet publish` (`docs/06-ci-cd-and-release.md:150`, Runtime's MSBuild targets) and the ship gate's extension to `tools/` (section 7). This note's code issues are milestone 2 work, so their CHANGELOG entries go under `## Next: 0.3.0 (not in the next release)`; this docs PR adds none.

## 1. Meta files

Every source asset has a sidecar next to it named `<file>.meta` (`hero.png` and `hero.png.meta`). It holds the ids of the assets cooked from the source, the importer that cooks it and that importer's settings. Sidecars are source files: they are committed, and only the cooker writes them.

```json
{
  "version": 1,
  "importer": "aseprite",
  "settingsVersion": 1,
  "settings": {
    "ignoreLayers": [
      "guides"
    ]
  },
  "assets": [
    {
      "name": "",
      "id": "0f8fad5b-d9cb-469f-a165-70867728950e",
      "kind": "ATLS"
    },
    {
      "name": "page-0",
      "id": "7c9e6679-7425-40de-944b-e07fc1f90ae7",
      "kind": "TXTR"
    }
  ]
}
```

| Property | Rule |
|----------|------|
| `version` | The sidecar format, 1. Missing or 0: `InvalidData`; above 1: `Unsupported` |
| `importer` | An importer id in the registry (section 2): `^[a-z][a-z0-9-]{0,31}$`. An unknown id: `Unsupported` |
| `settingsVersion` | 1 to the importer's `SettingsVersion`. Higher: `Unsupported` (the sidecar is newer than the cooker); lower: the importer migrates it (`NormalizeSettings`) |
| `settings` | A JSON object the importer validates. Unknown properties are `InvalidData`, so a typo never silently does nothing. Properties at their default value are omitted in the canonical form |
| `compression` | Optional: `auto` (the default, omitted), `none`, `deflate` or `brotli`; overrides the command's `--compression` for every asset of this source (section 4) |
| `assets` | 1 to 1,024 entries. The first has `name` `""` (the main asset); the others follow in ordinal order of `name`, which matches `^[a-z0-9][a-z0-9._-]{0,63}$` and is unique. `id` is an `AssetId` text form, accepted and rejected exactly as `AssetId.Parse` does ([section 1](assets.md#1-identity)), unique in the sidecar. `kind` is a four-character `AssetKind` code and must equal the kind the importer produces for that output |

- **Reading.** At most 1 MiB, nesting depth at most 32, UTF-8 without a byte-order mark. Deserialised through a source-generated `JsonSerializerContext` into internal records, then checked against the table; every failure is `InvalidData` with a diagnostic naming the property, line and column (section 4). `settings` is kept as a `JsonElement` for the importer.
- **Canonical form.** The cooker writes sidecars with `Utf8JsonWriter` (`Indented = true`, `IndentSize = 2`, `NewLine = "\n"`), the properties in the order of the table, `compression` only when it isn't `auto`, `settings` as the importer's `NormalizeSettings` returns it, ids in lower case, and one final `\n`. A rewrite of an unchanged sidecar gives the same bytes.
- **Creation for new files.** A source whose extension an importer claims with `CreatesMetaAutomatically` (every importer but shaders) and that has no sidecar gets one from `brutal-cook assets`: the importer's default settings, a new id for the main asset (`AssetId.NewId`, random, as the asset database note decides), and ids for the further outputs the first import asks for (section 2). Files no importer claims are never assets (Tiled tilesets, LDtk level files, Slang modules that are only imported, and so on): they are read as dependencies of the sources that use them. Sources whose importer doesn't create sidecars automatically (shaders) get one from `brutal-cook meta <file>`. With `--locked`, nothing is created or changed: a missing or outdated sidecar is an error, so CI never mints ids.
- **Missing sidecar.** The cooker creates it, or with `--locked` reports `BEC1001` and fails. In development mode, the database ignores the source with a `Warning` ([section 7](assets.md#7-development-mode)).
- **Corrupt sidecar.** `BEC1002` naming the property; the source's assets fail, and so does the cook (exit 65). In development mode `ReadMeta` returns `InvalidData`, which fails the database's start-up scan with the file named ([section 3](assets.md#3-the-database)), the same answer as the cook.
- **Duplicate id** in two sidecars: `BEC1003` naming both files, and the cook fails. In development mode the database logs both and fails that id ([section 7](assets.md#7-development-mode)).
- **Renames and moves.** The sidecar moves with its source, so the ids don't change and every reference survives (references are ids, never paths). A sidecar with no source beside it is an orphan: `BEC1005` in every mode, and while any orphan exists the cooker creates no sidecar in that run. So a source moved without its sidecar is reported instead of quietly getting a new id. The cooker never deletes a sidecar.

## 2. The importer library

```csharp
namespace BrutalEngine.Importers;

public interface IImporter                        // implementations are thread-safe: called concurrently for different sources
{
    ImporterInfo Info { get; }                    // read once, at registration
    Result<ReadOnlyMemory<byte>> NormalizeSettings(JsonElement settings, int settingsVersion);  // canonical JSON at the current version; allocates
    Result Import(ImportContext context);         // the context's thread only; allocates per import
}

public sealed record ImporterInfo                 // immutable; any thread
{
    public required string Id { get; init; }                         // ^[a-z][a-z0-9-]{0,31}$, e.g. "image"
    public required int Version { get; init; }                       // >= 1; raised whenever the output bytes can change
    public required int SettingsVersion { get; init; }               // >= 1; the "settings" schema in .meta files
    public required IReadOnlyList<string> Extensions { get; init; }  // lower case with the dot: ".png", ".atlas.json"
    public required AssetKind MainKind { get; init; }                // the kind of the output named ""
    public required ReadOnlyMemory<byte> DefaultSettings { get; init; }  // canonical JSON object, usually {}
    public bool CreatesMetaAutomatically { get; init; } = true;
    public string Fingerprint { get; init; } = "";                   // native versions the output depends on, e.g. the Slang build tag
}

public sealed class ImporterRegistry              // Add and Freeze on one thread; frozen, it is immutable and thread-safe
{
    public ImporterRegistry();
    public static ImporterRegistry CreateDefault();                  // every built-in importer except shaders; not frozen
    public void Add(IImporter importer);                             // InvalidOperationException: duplicate id or extension, or frozen
    public void Freeze();                                            // idempotent; lookups before it throw InvalidOperationException
    public bool IsFrozen { get; }
    public IReadOnlyList<IImporter> Importers { get; }               // ordinal by Info.Id
    public bool TryGetById(string id, [NotNullWhen(true)] out IImporter? importer);
    public bool TryGetForPath(VfsPath source, [NotNullWhen(true)] out IImporter? importer);  // longest matching extension; ordinal, lower case
}
```

- **Declaring an importer.** `Info` declares the id, the code version, the settings version, the claimed extensions, the main kind and the fingerprint. `ImporterRegistry.CreateDefault` returns a new registry each call (no static state) holding the built-in importers of section 3, which are internal types; the shader importer is public in its own project and added with `Add`. The longest suffix wins, so `.inputmap.json` and `.atlas.json` are claimed while plain `.json` isn't. Lookups lower-case the file name with the invariant culture.
- **Versions.** `Version` is part of every cache key (section 4): raising it re-imports every source of that importer. An importer raises it in the same commit as any change to its output bytes, and a golden-output test (section 8) fails until it does, so stale cache entries can't survive a code change. `SettingsVersion` versions the settings schema only.
- **Dependencies** are declared at import time, through the context: the files read (`ReadFile`, `ResolveAsset`) and each output's asset dependencies (`ImportOutput.Dependencies`).

```csharp
public sealed class ImportContext                 // one per import; the importer's call only; not thread-safe
{
    public VfsPath SourcePath { get; }                               // res://...
    public AssetId Id { get; }                                       // the main asset's id
    public JsonElement Settings { get; }                             // canonical, current SettingsVersion
    public ImportLimits Limits { get; }
    public CancellationToken CancellationToken { get; }
    public Result<ReadOnlyMemory<byte>> ReadSource();                // the source's bytes; at most Limits.MaxSourceBytes
    public Result<ReadOnlyMemory<byte>> ReadFile(VfsPath path);      // res:// only; recorded with its hash; counted by Limits
    public Result<AssetId> ResolveAsset(VfsPath source, string name = "");   // reads <source>.meta (recorded); NotFound if absent
    public Result<AssetId> GetOutputId(string name);                 // the id this source's .meta gives the output (see below)
    public void AddOutput(ImportOutput output);                      // ArgumentException: unknown or repeated name, wrong kind
    public void Report(ImportDiagnostic diagnostic);                 // warnings and infos; and the error a failed Result explains
}

public readonly record struct ImportOutput(string Name, AssetKind Kind, ReadOnlyMemory<byte> Data, IReadOnlyList<AssetId> Dependencies);

public sealed record ImportLimits                 // immutable; any thread
{
    public long MaxSourceBytes { get; init; } = 256L << 20;
    public int MaxReadFiles { get; init; } = 256;
    public long MaxReadBytes { get; init; } = 1L << 30;              // every ReadFile and ReadSource of one import together
    public int MaxOutputs { get; init; } = 1024;
    public long MaxOutputBytes { get; init; } = 1L << 30;           // per output; the database's MaxAssetBytes default
}

public readonly record struct ImportDiagnostic
{
    public required DiagnosticSeverity Severity { get; init; }
    public required string Code { get; init; }                       // "BEC2201"; section 4
    public required string Message { get; init; }
    public VfsPath? File { get; init; }                              // null: the source
    public int Line { get; init; }                                   // 1-based; 0: not applicable
    public int Column { get; init; }                                 // 1-based; 0: not applicable
    public long Offset { get; init; }                                // byte offset in binary files; -1: not applicable (the default)
}

public enum DiagnosticSeverity { Error = 0, Warning, Info }
```

- **Output ids.** An importer asks `GetOutputId` for every output it writes and every id it embeds (an atlas lists its page textures' ids). The id comes from the sidecar. A name the sidecar doesn't have yet gets a new id only when the run allows it (`AllowNewOutputs`, the cooker without `--locked`), and the cooker then writes it into the sidecar; otherwise `InvalidData` with `BEC1011` ("output 'page-1' has no id: run brutal-cook assets"). A sidecar output the import no longer produces is removed by the cooker, or reported (`BEC1010`) with `--locked`.
- **Reads go through the context only**, so every input of an import is recorded with its SHA-256 for the cache (section 4) and for hot reload (`ImportedAsset.ReadPaths`, [section 4](assets.md#the-import-seam-development-builds)). An importer never opens files, reads the environment or asks the clock.

**Determinism rules** for every importer, checked in review and by the determinism tests of section 5:

1. **No wall clock, randomness or environment** in output: no time, no random numbers (ids come from the sidecar), no machine name, user name, current directory or absolute path. Paths in outputs and diagnostics are relative to `res://`.
2. **No unordered iteration** reaches output: `Dictionary` and `HashSet` order never decides an order in bytes. Sort by ordinal string comparison or by `AssetId` first.
3. **Invariant culture everywhere:** parsing and formatting with `CultureInfo.InvariantCulture` or `Utf8Parser`/`Utf8Formatter`, `StringComparison.Ordinal`, lower-casing with `ToLowerInvariant`.
4. **Exact arithmetic.** Outputs are computed with integers, or with IEEE `+ - * /` and `Math.Sqrt` (correctly rounded on every OS) in a fixed order. No `Math.Sin`, `Exp`, `Pow` or other functions the C runtime computes, whose last bits differ between Windows and Linux, and no parallel reductions.
5. **Fixed native versions.** Native decoders are the pinned packages; anything else their output depends on (the Slang build tag) is in `Fingerprint`, and therefore in the cache key.
6. **Pure function.** An import's output depends only on the source, the sidecar, the files it read and the importer's version. Internal parallelism, if any, partitions work deterministically and merges in order.

**The development runtime** reaches importers through the asset database's seam:

```csharp
public sealed class SourceAssetImporter : IAssetImporter    // thread-safe; holds the registry, never disposes anything
{
    public SourceAssetImporter(SourceAssetImporterOptions options);  // ArgumentException: registry not frozen; nothing else can fail
    public Result<IReadOnlyList<AssetSource>> ReadMeta(VirtualFileSystem files, VfsPath metaPath);
    public Result<ImportedAsset> Import(AssetImportRequest request);
}

public sealed record SourceAssetImporterOptions
{
    public required ImporterRegistry Registry { get; init; }        // frozen
    public required Logger Logger { get; init; }                     // category "Importers"
    public ImportLimits Limits { get; init; } = new();
    public long MaxRetainedBytes { get; init; } = 64L << 20;          // the multi-output memo below
}
```

- **`ReadMeta`** parses the sidecar (section 1) and returns one `AssetSource` per entry of `assets`: its id, its kind and the source path (the sidecar's path without `.meta`). It never reads the source, so the database's start-up scan stays fast.
- **`Import`** runs `SourceImport.Run` (below) for the request's source with `AllowNewOutputs = false` and returns the requested id's output: `Data`, its `Dependencies`, and as `ReadPaths` every file the import read except the source and its sidecar (other sidecars read by `ResolveAsset` included, so moving a referenced image's sidecar reloads its users). Diagnostics are logged: warnings at `Warning`, the error in the failed `Result`. An id the sidecar lists but the import didn't produce fails with `InvalidData` ("run brutal-cook assets").
- **The multi-output memo.** Importing an Aseprite file produces its atlas and its pages together, and the database asks for each id separately. The importer keeps the last result of each source, keyed by the sidecar path and the hashes of every input it read, bounded by `MaxRetainedBytes` (least recently used first). A request re-hashes the recorded inputs and reuses the result only if all match, so hot reload never sees a stale memo. A per-source lock makes concurrent requests for two outputs of one source run the import once.
- The game creates it in Debug-only code (section 7) and hands it to the database options' `Importer`; it is thread-safe, as the seam requires.

**One import, shared by both paths.** The cooker and `SourceAssetImporter` both call:

```csharp
public static class SourceImport                  // stateless; thread-safe
{
    public static SourceImportResult Run(SourceImportRequest request);   // expected failures in Error; allocates per import
}

public sealed record SourceImportRequest
{
    public required VirtualFileSystem Files { get; init; }
    public required VfsPath MetaPath { get; init; }
    public required ImporterRegistry Registry { get; init; }        // frozen
    public ImportLimits Limits { get; init; } = new();
    public bool AllowNewOutputs { get; init; }                       // the cooker without --locked
    public CancellationToken CancellationToken { get; init; }
}

public sealed record SourceImportResult
{
    public EngineError? Error { get; init; }                         // null: success; otherwise Outputs is empty
    public AssetMeta? Meta { get; init; }                            // the sidecar as it should be (new outputs, migrated settings); null if unreadable
    public bool MetaChanged { get; init; }                           // its canonical bytes differ from the file
    public required IReadOnlyList<ImportOutput> Outputs { get; init; }   // ordinal by Name
    public required IReadOnlyList<ImportRead> Reads { get; init; }   // ordinal by path; the source and its sidecar included
    public required IReadOnlyList<ImportDiagnostic> Diagnostics { get; init; }   // in report order
}

public readonly record struct ImportRead(VfsPath Path, long Length, ReadOnlyMemory<byte> Sha256);
```

`Run` reads and checks the sidecar, finds its importer, normalises the settings, runs the import with a fresh context, then checks the outputs: every sidecar entry produced with its declared kind, sizes within `Limits`, dependency ids not all zero and not the output's own. Expected failures (a bad sidecar, an unknown importer, the importer's own failed `Result`, a broken output rule) set `Error` with its `ErrorKind` and keep every diagnostic reported so far, so the cooker can print them all; when the importer reported no error diagnostic, `Run` adds one from the error's message. `Reads` is kept on failure too, so a failed source is re-imported only when one of its inputs changes. An importer that throws fails the import with `ErrorKind.Unspecified` and `BEC0002` carrying the exception's message (the cooker prints the stack with `--verbose`). `SourceAssetImporter.Import` turns `Error` into its failed `Result`.

```csharp
namespace BrutalEngine.Importers.Meta;

public sealed record AssetMeta                     // immutable; any thread
{
    public required string Importer { get; init; }
    public required int SettingsVersion { get; init; }
    public required ReadOnlyMemory<byte> Settings { get; init; }     // canonical JSON object
    public AssetCompression Compression { get; init; }               // Auto when absent
    public required IReadOnlyList<AssetMetaEntry> Assets { get; init; }   // main first, then ordinal by Name
    public static Result<AssetMeta> Read(ReadOnlySpan<byte> utf8Json, VfsPath path);   // InvalidData, Unsupported; path for messages
    public static AssetMeta CreateNew(ImporterInfo importer);        // the defaults and a new main id
    public AssetMeta WithAssets(IReadOnlyList<AssetMetaEntry> assets);   // ArgumentException unless main first, ordinal, unique
    public byte[] ToJson();                                          // the canonical form of section 1
}

public readonly record struct AssetMetaEntry(string Name, AssetId Id, AssetKind Kind);

public enum AssetCompression { Auto = 0, None, Deflate, Brotli }
```

Section 1's rules are `Read`'s; the version and importer checks against the registry are `SourceImport.Run`'s. Nothing in these types is BRUTAL's.

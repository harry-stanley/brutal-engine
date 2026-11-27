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

## 3. Importers

| Importer id | Extensions | Outputs (name: kind) | Payload owner |
|-------------|-----------|----------------------|---------------|
| `image` | `.png`, `.tga` | `""`: `TXTR` | the asset database note, section 9 |
| `sprite-atlas` | `.atlas.json` | `""`: `ATLS`; `page-<n>`: `TXTR` | the 2D renderer note; textures as above |
| `aseprite` | `.ase`, `.aseprite` | `""`: `ATLS`; `page-<n>`: `TXTR` | the 2D renderer note; textures as above |
| `audio` | `.wav`, `.ogg`, `.flac`, `.mp3` | `""`: `ACLP` or `ASTM` | the audio note (`ACLP`); `ASTM` is the Ogg file itself |
| `font` | `.ttf`, `.otf` | `""`: `FONT`; `atlas-<n>`: `TXTR` (`R8Unorm`) | the text note; textures as above |
| `shader` | `.slang` (no automatic sidecar) | `""`: `SHDR` | this note |
| `tiled` | `.tmj`, `.tmx` | `""`: `TMAP` | the tilemap note |
| `ldtk` | `.ldtk` | `""`: `TMAP` (the world), `level-<iid>`: `TMAP` | the tilemap note |
| `input-map` | `.inputmap.json` | `""`: `INPM` | the input note |

Kind codes are the asset database note's ([section 1](assets.md#1-identity)): it declares `TXTR`, `ACLP` and `ASTM` and reserves `INPM`, `FONT`, `ATLS` and `TMAP` for their modules. This note declares `SHDR`, because it defines that payload; the shader loader belongs to the 2D renderer note, whose material system creates pipelines from it. For the payloads other notes own, the importer serialises what this section lists in the owner's layout, preferably through a writer type the owning module exposes, so the layout lives in one assembly; the owner's note decides. Each such importer issue is blocked by its owner's note (section 9).

**Images (`image`).** PNG and TGA only. Before decoding, a managed header check reads PNG's signature and `IHDR` (width and height) or TGA's 18-byte header (colour-map type 0 or 1, image type 1, 2, 3, 9, 10 or 11, width, height, pixel depth 8, 15, 16, 24 or 32) and enforces section 6's limits, because `Stb.LoadFromMemory(ReadOnlySpan<byte>, int)` (`Brutal.Stb/Stb.cs:42`) detects the format itself and has no size query in BRUTAL's binding. The colour-map byte rules out every other format stb knows, whose signatures put other values there, and the decoded width and height must equal the header's (else `InvalidData`), so stb can only have decoded the checked format. It is called with `desiredChannels = 4`; a null result (`StbImage8.IsNull`, `Brutal.Stb/StbImage8.cs:35`) is `InvalidData` with a fixed message, because `Stb.TryGetFailureReason` (`Brutal.Stb/Stb.cs:316`) reads stb's process-wide failure string, which a concurrent import can overwrite; the image is freed with `Dispose` (`Brutal.Stb/StbImage8.cs:30`). Grey, RGB, palette and 16-bit sources come out as 8-bit RGBA. **Colour space:** every M2 image is sRGB colour (`Rgba8UnormSrgb`); colour profiles and `gAMA` chunks are ignored, and linear data such as normal maps waits for a format addition to the texture payload. **Alpha: decision (this note): premultiplied.** Every colour texture the pipeline writes is premultiplied, so linear filtering and atlas padding never bleed colour from transparent texels: `c' = (c * a + 127) / 255` in integers per channel, with `a` unchanged; renderers blend with `BlendState.PremultipliedAlpha` (the render interface note). Glyph atlases are coverage (`R8Unorm`) and aren't premultiplied. The output is the asset database note's texture payload, version 1, written as its table says; settings: none in M2.

**Sprite atlases (`sprite-atlas`).** A definition file names the images to pack:

```json
{ "version": 1, "sprites": [ "hero/*.png", "ui/button.png" ] }
```

Entries are paths relative to the definition's folder; `*` matches within one path segment, matches are sorted ordinally, and each image is read with `ReadFile`. Members need no sidecar, and the cooker doesn't create one for an image that an atlas definition in the tree includes; a member that has one anyway is also cooked on its own (`BEC2102`, info). Settings: `padding` (0-16 px, default 2), `extrude` (0-8 px of repeated edge texels, default 1), `trim` (crop fully transparent borders, default true), `maxPageSize` (64-8,192, a power of two, default 4,096), `powerOfTwo` (default true). **The packer** is MaxRects with best-short-side fit (Jylänki, "A Thousand Ways to Pack the Bin", 2010): rectangles are padded and extruded first, sorted by longer side descending, then area descending, then name ordinally; each goes to the free rectangle with the smallest leftover short side, ties broken by smaller y, then smaller x, then the earlier free rectangle in a list that keeps insertion order. A sprite that doesn't fit starts a new page; one larger than `maxPageSize` is `BEC2101`. A finished page shrinks to the smallest power of two (or multiple of 4) that holds it. Everything is integer arithmetic in a fixed order, so the layout depends only on the images and settings. The atlas output holds, per region: its name (the member path without extension), page, rectangle, trim offset and original size; and the page textures' ids. The pages are premultiplied `TXTR` outputs.

**Aseprite (`aseprite`).** An in-house parser of the `.ase`/`.aseprite` format as Aseprite documents it (`docs/ase-file-specs.md` in the Aseprite repository): the header (the file size must equal the file's length), frames and their durations, layer chunks (`0x2004`) with visibility, opacity and grouping, cels (`0x2005`: raw, linked, and zlib-compressed images decompressed to exactly width x height x bytes per pixel), the palette (`0x2019`), tags (`0x2018`: name, frame range, direction, repeat count) and slices (`0x2022`: bounds, nine-slice centre, pivot, per frame). Colour depths 32 (RGBA), 16 (grey and alpha) and 8 (indexed, with the transparent index). Ignored: user data, colour profiles (sRGB assumed) and old palette chunks. `Unsupported`: tilemap layers and tileset chunks (`0x2023`), and layer blend modes other than normal, so a file never cooks to something that looks different from the editor. **Composition** flattens the visible layers bottom to top for each frame with straight-alpha source-over in 8-bit integers (layer and cel opacity multiplied in with `(x * y + 127) / 255`), then premultiplies. Settings: `ignoreLayers` (names), `padding`, `extrude`, `trim`, `maxPageSize`, `powerOfTwo` as for atlases. The frames are packed by the same packer; the atlas output holds a region per frame (index order, with its duration in milliseconds), the tags, and a region per slice key with its nine-slice centre and pivot. Animation clips built from tags are M3's Animation work, reading the same parse.

**Audio (`audio`).** Decoding uses SDL_mixer through BRUTAL's binding, as ADR-0017 records it: the source bytes are wrapped with `SDL_IOFromConstMem` (`Brutal.Sdl/Generated/Sdl.PInvoke.gen.cs:2916`), a decoder is created with `MIX_CreateAudioDecoder_IO` (`Brutal.Sdl.Mixer/Generated/SdlMixer.PInvoke.gen.cs:1018`), its format read with `MIX_GetAudioDecoderFormat` (`:1048`), the PCM pulled with `MIX_DecodeAudio` (`:1058`) as 32-bit float at the source's rate and channel count, and the decoder destroyed with `MIX_DestroyAudioDecoder` (`:1028`). Every status is checked; a decoder that can't open is `InvalidData` naming the format. Whether `MIX_Init` (`:21`) and SDL's audio subsystem are needed, and which calls are safe off the main thread, are the facts ADR-0017's prototype measures; the importer issue follows them. Sources: 1 or 2 channels, 8,000-192,000 Hz (others `Unsupported`). Setting `mode`: `clip`, `stream` or `auto` (the default). **The threshold: decision (this note): `auto` cooks a sound of at most 10 seconds as a clip and a longer one as a stream** (10 s of 48 kHz stereo float is 3.7 MiB, short sound effects stay in memory, music and ambience stream). A **clip** (`ACLP`) is the decoded PCM, with sample rate and channel count, in the audio note's layout. A **stream** (`ASTM`) is the source's bytes unchanged, stored uncompressed as the asset database note requires; only Ogg Vorbis sources can stream, checked by an in-house Ogg page and Vorbis identification-header reader, then by a full decode so a corrupt file fails at cook time. A WAV, FLAC or MP3 source longer than the threshold in `auto` mode is `BEC2301`: "streams ship as Ogg Vorbis (ADR-0017): convert the file, or set mode clip". Loop points and playback state are Audio's.

**Fonts (`font`).** TrueType and OpenType files (collections are `Unsupported`). Settings: `sizes` (1-16 pixel sizes, 4-512), `mode` (`bitmap` or `sdf`), `characters` (`ascii`, `latin1`, or a list of code point ranges), `sdfSpread` (1-32 px), `padding`, `hinting` (`normal`, `light`, `none`). For each size the importer rasterises every requested glyph through the Text module's rasteriser (the text note), so pre-baked and runtime glyphs are the same, packs them with the packer of the sprite atlases into `R8Unorm` pages, and writes the font output: per size, the line metrics, each glyph's code point, advance, bearing and atlas region, and the kerning pairs, in the text note's layout. Characters missing from the font are listed in a `BEC2401` warning.

**Shaders (`shader`, in `BrutalEngine.Importers.Shaders`).** Settings: `entryPoints`, 1-16 names (required). The importer compiles with the function M1's shader command uses: the first shader-importer issue moves that function from `tools/BrutalEngine.Cook` into this project without changing its output (M1's re-cook job, which compares the committed SPIR-V byte for byte, proves it), and the shader command calls it through the `Cook -> Importers.Shaders` edge, so the command's behaviour, ADR-0012's subject, is unchanged. What the pipeline requires of it: a global session (`Slang.CreateGlobalSession`, `Brutal.Slang/Slang.cs:59`) per importer instance (the importer is `IDisposable` and releases it), a session per import (`CreateSession`, `Brutal.Slang/Generated/InterfacesExt.gen.cs:89`) targeting SPIR-V (`CompileTarget.Spirv`, `Brutal.Slang/Generated/Enumerations.gen.cs:261`) with no debug information (`DebugInformation`, `Brutal.Slang/Generated/Enumerations.gen.cs:49`, set to `DebugInfoLevel.None`, `:434`), so no absolute path or source text reaches the SPIR-V, and the assets root as the only search path; the module loaded from the source bytes (`LoadModuleFromSource`, `Brutal.Slang/ISessionExtensions.cs:80`) under its `res://`-relative name, linked (`Link`, `Brutal.Slang/Generated/InterfacesExt.gen.cs:222`), and the code taken with `GetTargetCode` (`Brutal.Slang/Generated/Interfaces.gen.cs:568`). The files the module imported, from `GetDependencyFileCount` and `GetDependencyFilePath` (`Brutal.Slang/Generated/Interfaces.gen.cs:638`, `:641`), are read again through `ReadFile` so they are hashed; one outside the assets root is `BEC2502`. Slang's diagnostics (`error[E…] --> path:line:col`, `docs/spikes/README.md:96`) become `BEC2501` diagnostics at that position.

**Reflection** comes from `GetLayout` (`Brutal.Slang/Generated/Interfaces.gen.cs:495`), whose `ProgramLayout` is the `Reflection` the raw entry points take (`Brutal.Slang/Generated/GlobalUsings.gen.cs:59`); the binding's layout types are empty structs, so the importer calls those entry points directly, as spike S4 did (`docs/spikes/README.md:99`): parameters with `spReflection_GetParameterCount`, `spReflection_GetParameterByIndex`, `spReflectionParameter_GetBindingIndex` and `spReflectionParameter_GetBindingSpace` (`Brutal.Slang/Generated/Slang.gen.cs:1175`, `:1178`, `:1070`, `:1073`), their categories and binding-range types with `spReflectionTypeLayout_GetParameterCategory` and `spReflectionTypeLayout_getBindingRangeType` (`:621`, `:665`), push-constant sizes with `spReflectionTypeLayout_GetSize` (`:561`), entry points with `spReflection_getEntryPointCount`, `spReflection_getEntryPointByIndex`, `spReflectionEntryPoint_getName` and `spReflectionEntryPoint_getStage` (`:1236`, `:1239`, `:1091`, `:1115`), vertex inputs through the entry point's parameters (`spReflectionEntryPoint_getParameterCount`, `:1106`; `spReflectionVariableLayout_GetTypeLayout`, `:849`), and compute group sizes with `GetComputeThreadGroupSize` (`Brutal.Slang/EntryPointLayoutExtensions.cs:14`). Each binding maps to the render interface's `BindingType` (`docs/design/graphics.md:575`): constant buffers to `UniformBuffer`, read-write structured buffers to `StorageBuffer`, structured buffers to `ReadOnlyStorageBuffer`, textures to `SampledTexture`, read-write textures to `StorageTexture`, sampler states to `Sampler`. Anything else (combined texture-samplers, unbounded arrays, acceleration structures) is `BEC2503`, `Unsupported`. Vertex inputs map to the shader-side `VertexFormat` (`docs/design/graphics.md:553`): `float` to `Float32` through `float4` to `Float32x4`, `uint` to `Uint32`, `uint2` to `Uint32x2`, `int` to `Sint32`; a buffer-side format such as `Unorm8x4` is the material's choice, not the shader's.

**The shader payload (`SHDR`, payload version 1).** Little-endian; tables follow the header in this order, the SPIR-V starts at the next multiple of 4, and the string table (UTF-8, no terminators) ends the payload:

| Offset | Size | Field | Rule |
|-------:|-----:|-------|------|
| 0 | 2 | Version | `u16`, 1 (above: `Unsupported`; 0: `InvalidData`) |
| 2 | 2 | Flags | `u16`, 0 |
| 4 | 4 | CodeOffset | `u32`, a multiple of 4: the end of the vertex-input table rounded up |
| 8 | 4 | CodeLength | `u32`, a multiple of 4 and at least 20; SPIR-V, starting with `0x07230203` |
| 12 | 2 | EntryPointCount | `u16`, 1-16 |
| 14 | 2 | BindingCount | `u16`, 0-256 |
| 16 | 2 | VertexInputCount | `u16`, 0-32 |
| 18 | 2 | PushConstantSize | `u16`, a multiple of 4, 0-256 (0: none) |
| 20 | 2 | PushConstantStages | `u16`, `ShaderStages` bits; 0 exactly when the size is 0 |
| 22 | 2 | Reserved | 0 |
| 24 | 4 | StringTableOffset | `u32`: `CodeOffset + CodeLength` |
| 28 | 4 | StringTableLength | `u32`; the payload ends at `StringTableOffset + StringTableLength` |
| 32 | 16 | SourceHash | the first 16 bytes of a SHA-256 over the compiler tag, then the source's and each imported file's `res://` path and bytes, in ordinal path order |
| 48 | 4 | CompilerTagOffset | `u32`, into the string table: Slang's build tag (`GetBuildTag`, `Brutal.Slang/IGlobalSessionExtensions.cs:17`) |
| 52 | 2 | CompilerTagLength | `u16` |
| 54 | 2 | Reserved | 0 |

- **Entry point** (16 bytes each, ordinal by name): `Stage` `u16` (one `ShaderStages` bit, `docs/design/graphics.md:509`), `ThreadGroupX`, `Y`, `Z` `u16` each (compute only; 0 otherwise), `NameOffset` `u32`, `NameLength` `u16`, reserved `u16`.
- **Binding** (16 bytes each, ascending by set, then binding): `Set` `u16`, `Binding` `u16`, `Type` `u8` (a `BindingType` value), reserved `u8`, `Count` `u16` (1 for non-arrays), `Visibility` `u16` (`ShaderStages` bits of the entry points that use it), `NameLength` `u16`, `NameOffset` `u32`.
- **Vertex input** (12 bytes each, ascending by location; the vertex entry point's): `Location` `u16`, `Format` `u8` (a `VertexFormat` value), reserved `u8`, `NameOffset` `u32`, `NameLength` `u16`, reserved `u16`.
- **Strings** are stored in the order their fields appear: the compiler tag, then names in table order. Reserved bytes and padding are zero.
- The loader passes the code to `CreateShaderModule` (`docs/design/graphics.md:479`) as one `ShaderCode` with `ShaderCodeFormat.SpirV` (`docs/design/graphics.md:493`), and builds bind group layouts and the push-constant range (`docs/design/graphics.md:508`) from the tables: the reflection blobs the render interface note expects from M2 (`docs/design/graphics.md:498`). A later code format (for another backend) is a payload version 2.
- **Staleness.** `SourceHash` and the compiler tag identify what a blob was cooked from. The cooker's cache and the development database re-import a shader whose inputs change, which is where docs/02's "stale shaders are detected" (`docs/02-tech-stack.md:55`) holds; a release runtime has no source to compare and checks the payload version only.
- **Built-in shaders** stay on ADR-0012's path (committed SPIR-V, layouts declared in C#) until the ADR that moves them to reflection.

**Tilemaps (`tiled`, `ldtk`).** Orthogonal maps only (the decision above); isometric, staggered and hexagonal maps are `BEC2601`, `Unsupported`.

- **Tiled:** JSON maps (`.tmj`) and tilesets (`.tsj`), and XML (`.tmx`, `.tsx`), format version 1.10 or later; older: `Unsupported`. Finite and infinite (chunked) maps; tile layer data in CSV or base64, uncompressed, zlib or gzip (decoded to exactly width x height x 4 bytes); zstd is `Unsupported`. Global tile ids keep their flip bits (horizontal, vertical, diagonal); the hexagonal 120° bit is `BEC2601`. Embedded and external tilesets, each tileset image resolved to its texture's id with `ResolveAsset` (so the image needs its own sidecar), image-collection tilesets `Unsupported` in M2. Object layers (rectangles, points, ellipses, polygons, polylines, tile objects; templates `Unsupported`), group layers flattened with their offsets, typed custom properties (bool, int, float, string, colour, file resolved to an asset id, object reference), layer offset, parallax, opacity and visibility, per-tile properties, animations and collision objects.
- **LDtk:** project files (`.ldtk`) with `jsonVersion` 1.5.0 or later, and external level files (`.ldtkl`, read with `ReadFile`). Each level is an output named `level-<iid>`, from the level's unique identifier in lower case, so renaming or reordering levels keeps their ids; the main output is the world (level list, positions, layer and tileset definitions). Layer instances: Tiles, IntGrid, AutoLayer (the tiles LDtk has already generated) and Entities with their typed fields. Tileset images resolved as for Tiled.
- **Output** (both): the map size, tile size, layers in order, cells as tileset-and-tile ids with flip bits, chunked as the tilemap note specifies, tilesets with their texture ids, properties and objects, in the tilemap note's layout. Auto-tiling rules are the Tilemaps module's runtime feature, not the importer's.

**Input maps (`input-map`).** The JSON form is the input note's (section 9 there): the importer validates the source with `InputMap.FromJson` and writes `InputMap.ToJson`'s canonical bytes as the `INPM` payload, with no dependencies. `FromJson`'s failure is reported as `BEC2701` with its message. Settings: none.

**Not in M2.** Animations (sprite-sheet clips from Aseprite tags, property curves, skeletal data) come with M3's Animation note; localisation tables with M3's localisation work; scenes and prefabs after the 0.2.0 release, with World's generated serialisers (World is frozen until then). Each adds an importer through the same registry.

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

public readonly record struct ImportOutput(string Name, AssetKind Kind, ReadOnlyMemory<byte> Data, IReadOnlyList<AssetId> Dependencies);   // immutable; any thread

public sealed record ImportLimits                 // immutable; any thread
{
    public long MaxSourceBytes { get; init; } = 256L << 20;
    public int MaxReadFiles { get; init; } = 256;
    public long MaxReadBytes { get; init; } = 1L << 30;              // every ReadFile and ReadSource of one import together
    public int MaxOutputs { get; init; } = 1024;
    public long MaxOutputBytes { get; init; } = 1L << 30;           // per output; the database's MaxAssetBytes default
}

public readonly record struct ImportDiagnostic    // immutable; any thread
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

public sealed record SourceImportRequest          // immutable; any thread
{
    public required VirtualFileSystem Files { get; init; }
    public required VfsPath MetaPath { get; init; }
    public required ImporterRegistry Registry { get; init; }        // frozen
    public ImportLimits Limits { get; init; } = new();
    public bool AllowNewOutputs { get; init; }                       // the cooker without --locked
    public CancellationToken CancellationToken { get; init; }
}

public sealed record SourceImportResult           // immutable; any thread
{
    public EngineError? Error { get; init; }                         // null: success; otherwise Outputs is empty
    public AssetMeta? Meta { get; init; }                            // the sidecar as it should be (new outputs, migrated settings); null if unreadable
    public bool MetaChanged { get; init; }                           // its canonical bytes differ from the file
    public required IReadOnlyList<ImportOutput> Outputs { get; init; }   // ordinal by Name
    public required IReadOnlyList<ImportRead> Reads { get; init; }   // ordinal by path; the source and its sidecar included
    public required IReadOnlyList<ImportDiagnostic> Diagnostics { get; init; }   // in report order
}

public readonly record struct ImportRead(VfsPath Path, long Length, ReadOnlyMemory<byte> Sha256);   // immutable; any thread
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

public readonly record struct AssetMetaEntry(string Name, AssetId Id, AssetKind Kind);   // immutable; any thread

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

**Shaders (`shader`, in `BrutalEngine.Importers.Shaders`).** Settings: `entryPoints`, 1-16 names (required). The importer compiles with the function M1's shader command uses: the first shader-importer issue moves that function from `tools/BrutalEngine.Cook` into this project without changing its output (M1's re-cook job, which compares the committed SPIR-V byte for byte, proves it), and the shader command calls it through the `Cook -> Importers.Shaders` edge, so the command's behaviour, ADR-0012's subject, is unchanged. What the pipeline requires of it: a global session per importer instance, created by `Create` (below), a session per import (`CreateSession`, `Brutal.Slang/Generated/InterfacesExt.gen.cs:89`) targeting SPIR-V (`CompileTarget.Spirv`, `Brutal.Slang/Generated/Enumerations.gen.cs:261`) with no debug information (`DebugInformation`, `Brutal.Slang/Generated/Enumerations.gen.cs:49`, set to `DebugInfoLevel.None`, `:434`), so no absolute path or source text reaches the SPIR-V, and the assets root as the only search path; the module loaded from the source bytes (`LoadModuleFromSource`, `Brutal.Slang/ISessionExtensions.cs:80`) under its `res://`-relative name, linked (`Link`, `Brutal.Slang/Generated/InterfacesExt.gen.cs:222`), and the code taken with `GetTargetCode` (`Brutal.Slang/Generated/Interfaces.gen.cs:568`). The files the module imported, from `GetDependencyFileCount` and `GetDependencyFilePath` (`Brutal.Slang/Generated/Interfaces.gen.cs:638`, `:641`), are read again through `ReadFile` so they are hashed; one outside the assets root is `BEC2502`. Slang's diagnostics (`error[E…] --> path:line:col`, `docs/spikes/README.md:96`) become `BEC2501` diagnostics at that position.

```csharp
namespace BrutalEngine.Importers.Shaders;

public sealed class SlangShaderImporter : IImporter, IDisposable   // thread-safe; one lock serialises its Slang calls (below)
{
    public static Result<SlangShaderImporter> Create();             // Native: Slang's global session can't be created; allocates
    public ImporterInfo Info { get; }                                // "shader", ".slang", SHDR, CreatesMetaAutomatically false, Fingerprint the build tag
    public Result<ReadOnlyMemory<byte>> NormalizeSettings(JsonElement settings, int settingsVersion);   // allocates
    public Result Import(ImportContext context);                     // ObjectDisposedException after Dispose; allocates per import
    public void Dispose();                                           // idempotent; waits for a running import, then releases the global session
}
```

`Create` calls `Slang.TryCreateGlobalSession` (`Brutal.Slang/Slang.cs:68`) and returns a failed status as `ErrorKind.Native` with its code: a missing or broken native library is an expected failure on a machine without the package, so it isn't an exception. **Threading.** Neither BRUTAL's binding nor Slang's header says which calls may run concurrently on one global session, so the importer holds one lock around every Slang call, from creating an import's session to releasing it: shader imports of one importer run one at a time, and other importers' imports run beside them. Each import releases its proxies with `Slang.Release` (`Brutal.Slang/Slang.cs:204`) on its own thread, children before parents, as the binding requires (`Brutal.Slang/Slang.cs:9-11`); `Dispose` takes the same lock, so it waits for a running import, then releases the global session. The importer issue may drop the lock only with a stress test that shows concurrent sessions are safe. A game or the cooker adds the importer with `registry.Add(created.Value)` before `Freeze` and disposes it after the last import.

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

## 4. The assets command

```text
Usage: brutal-cook assets [options] <assets-dir>
       brutal-cook meta [--importer <id>] <source-file>...

assets   Import every source under <assets-dir> and write one pack.
  -o, --output <file>          The pack to write (required). Replaced atomically; untouched when the cook fails.
      --cache <dir>            The incremental cache. Default: <output>.cache, next to the pack.
      --no-cache               Import every source; read and write no cache.
  -j, --jobs <n>               Parallel imports, 1-64. Default: the processor count, at most 16.
      --locked                 Never create or change a .meta file; one that would change is an error (for CI).
      --reference-pack <file>  A pack whose ids this pack's assets may depend on. Repeatable.
      --compression <mode>     auto (default), none, deflate or brotli. A .meta's "compression" overrides it.
      --manifest <file>        Also write the pack's manifest (JSON, below).
      --diagnostics <format>   text (default, on stderr) or json (one object per line, on stdout).
      --warnings-as-errors     Fail on warnings too.
      --verbose                Also print infos, timings and exception stacks (text only).
  -h, --help                   Print this text and exit 0.

meta     Create the sidecar of each <source-file>, for importers that don't create one automatically (shaders).
      --importer <id>          The importer. Default: the one claiming the file's extension.

Exit codes: 0 success; 64 usage error; 65 asset errors; 66 <assets-dir> or a source file is missing;
70 internal error; 73 the pack or the cache can't be created; 74 I/O error, or the cache is in use; 130 cancelled.
```

- **The tool's shape.** M1 creates `brutal-cook` with subcommands, its shader command first, and with distinct exit codes per failure class, 64 for usage errors. The `assets` and `meta` commands add to it, using the same `sysexits.h` numbering. M1's own details (its `tools/Directory.Build.props`, option parsing, the shader command's options) are M1's issue's; nothing here changes them. A `meta` run that finds an existing sidecar leaves it as it is and reports `BEC1012` (info).
- **Steps.** (1) Mount `<assets-dir>` as `res://` through a read-only `DirectoryFileSystem` and walk it in ordinal order. (2) Read every sidecar; report orphans, duplicates and missing sidecars (section 1). (3) Import every source, from the cache when it hits, on `--jobs` workers. (4) Without `--locked`, write the changed sidecars (each atomically, through a temporary file and a rename). (5) Check dependencies. (6) Write the pack, then the manifest. Any error stops before step 6: no pack is written and the old one is kept, but cache entries of sources that imported cleanly are saved, so the next run is still incremental.
- **The incremental cache.** One cache entry per source, in `<cache>/v1/sources/<hex SHA-256 of the source's relative path>`, holds the key and the outputs; payloads live once each in `<cache>/v1/payloads/<hex SHA-256>` (content-addressed, so identical outputs are stored once), with their compressed forms beside them under the codec and its settings. The **key** is: the cache format version; the cooker's version; the importer's id, `Version` and `Fingerprint`; the canonical sidecar bytes; and every recorded read (path, length, SHA-256). A run hashes each recorded file again and hits only if every one matches: content hashes, never timestamps, which differ between checkouts and which the VFS doesn't report. Compressed forms are keyed by codec, level and the .NET runtime version as well (section 5). Entries are written to a temporary file and renamed, so a crash leaves no half entry; an unreadable entry is a miss (`BEC3010`, info). A lock file held for the whole run makes a second cooker on the same cache exit 74. A successful run deletes entries of sources it didn't visit and payloads nothing references. `--no-cache` imports everything and touches no cache.
- **Dependency tracking.** File dependencies are the reads each import records: Tiled tilesets, LDtk level files, Slang imports, atlas members and the sidecars `ResolveAsset` read. They invalidate the cache entry and drive hot reload. Asset dependencies are the `Dependencies` of each output; after all imports, each must name an output of this cook or an entry of a `--reference-pack` (`BEC3001` otherwise), and the graph of this cook's outputs must be acyclic (`BEC3002`, naming the cycle in id order), as the asset database note expects of cooked packs ([section 3](assets.md#3-the-database)). An id that is both cooked and in a reference pack is `BEC3003`, because mounting both would fail.
- **Parallel import, deterministic output.** Sources are sorted ordinally and imported by a pool of `--jobs` workers; each result is stored by its source's index, never in completion order. Diagnostics are printed in source order as soon as every earlier source is done. The pack's entries are sorted by `AssetId`, as the format requires, so neither scheduling nor the OS's file order can change a byte. Payloads go to the cache's payload store as they finish (to a temporary store with `--no-cache`), so memory holds at most `--jobs` imports.
- **Pack writing** follows the asset database note's [section 6](assets.md#6-the-pack-format), version 1, exactly: the header, the 64-byte entries sorted by id, the dependency table (each output's ids ascending, without duplicates), the path table, zero padding, the data in entry order. The main output of a source gets the source's path relative to `res://` as its path; other outputs get none (`PathLength` 0), because paths must be unique. **Compression** per entry: `none` stores; `deflate` is raw Deflate through `DeflateStream` with `ZLibCompressionOptions { CompressionLevel = 9, CompressionStrategy = Default }`; `brotli` is `BrotliEncoder` with quality 9 and window 22. `auto` chooses Deflate when the payload is at least 1 KiB and Deflate saves at least an eighth of it, and stores otherwise. Streamed audio (`ASTM`) and already-compressed payloads are always stored, as the reader requires. Levels are numbers, not `CompressionLevel` names, so a runtime that remaps the names can't change the output. The file is written to `<output>.tmp` and renamed over the output.
- **The manifest** (`--manifest`) lists the pack and every entry, canonical as the sidecars are: `{"version":1,"pack":{"length":…,"sha256":…},"entries":[{"id":…,"kind":…,"path":…,"compression":…,"length":…,"storedLength":…,"payloadSha256":…,"storedSha256":…},…]}`, entries in pack order, no absolute paths and no times. The determinism job (section 5) compares manifests.

**Diagnostics.** With `--diagnostics json`, stdout carries only one JSON object per line, with these properties in this order, each omitted when it doesn't apply:

```json
{"severity":"error","code":"BEC2202","file":"sprites/hero.aseprite","offset":4096,"asset":"0f8fad5b-d9cb-469f-a165-70867728950e","message":"cel 3 of frame 2: zlib data decodes to 4100 bytes, expected 4096"}
{"severity":"error","code":"BEC1002","file":"sprites/hero.png.meta","line":4,"column":13,"message":"\"settingsVersion\" must be a positive integer"}
{"severity":"info","code":"BEC0001","message":"cooked 120 assets from 87 sources (3 imported, 84 from the cache): 2 errors, 0 warnings"}
```

`severity` is `error`, `warning` or `info`; `code` is `BEC` and four digits; `file` is relative to `<assets-dir>` with `/` separators; `line` and `column` are 1-based (text formats); `offset` is a byte offset (binary formats); `asset` is the id when one is known. The last line is always the `BEC0001` summary. Text diagnostics on stderr use MSBuild's canonical form, so IDEs and CI annotate them: `assets/sprites/hero.png.meta(4,13): error BEC1002: "settingsVersion" must be a positive integer`. Codes are stable and never reused:

| Range | Area | Codes this note defines |
|-------|------|-------------------------|
| `BEC0xxx` | The command | `0001` summary; `0002` an importer threw |
| `BEC1xxx` | Sidecars | `1001` missing (`--locked`); `1002` malformed; `1003` duplicate id; `1004` unknown importer; `1005` orphan; `1006` newer version or settings version; `1007` output kind mismatch; `1010` would change (`--locked`); `1011` output has no id; `1012` already exists (`meta`) |
| `BEC2xxx` | Importers: `20` images, `21` atlases and the packer, `22` Aseprite, `23` audio, `24` fonts, `25` shaders, `26` tilemaps, `27` input maps | `2101` sprite larger than a page; `2102` atlas member also cooked alone; `2301` long sound that can't stream; `2401` missing characters; `2501` compile error; `2502` import outside the assets root; `2503` unsupported binding; `2601` unsupported orientation or flag; `2701` invalid input map. Each importer issue lists the rest of its range in its importer's documentation |
| `BEC3xxx` | Dependencies, packs, the cache | `3001` unresolved dependency; `3002` cycle; `3003` id also in a reference pack; `3010` unreadable cache entry |

Errors set exit code 65 (70 for `BEC0002`). With `--diagnostics json`, stdout carries nothing but those lines (and `--help` prints only its text), so a CI step or an editor can parse it line by line.

## 5. Determinism

**What is guaranteed.** For one cooker build and one .NET runtime version, the same sources and sidecars give a byte-identical pack and manifest whatever `--jobs` is, whether the cache is cold, warm or off, in whatever order the OS lists files, from whatever directory and absolute location, at whatever time, under whatever user and culture. This follows from section 2's importer rules and section 4's ordering and pinned encoder settings, and is tested on every run of the determinism job on one OS.

**Across Windows and Linux** the same holds for payloads (uncompressed bytes) of managed importers and of native decoders whose arithmetic is integer (stb for PNG and TGA, SDL_mixer's WAV and FLAC paths), and therefore for packs cooked with `--compression none`. It is expected but not yet shown for three native paths, and the job decides: Slang (ADR-0012 already requires the first Linux re-cook PR to show Windows and Linux SPIR-V byte-identical), FreeType's rasterisation, and the floating-point Vorbis and MP3 decoders when they cook clips (ADR-0017's prototype compares decoded checksums across OSes, #279). Compressed bytes depend on the BCL's Deflate and Brotli encoders, which are native code inside the runtime; the asset database note lists their cross-OS equality as unverified, and the reader doesn't depend on it. The cooker pins their numeric settings (section 4) and the job checks them rather than assuming them.

**How CI shows it.** A `cook-determinism` workflow job (its issue in section 9) runs on Linux and Windows in the same workflow run, so both use the SDK and runtime `global.json` selects (each prints `dotnet --version`, and the comparison fails if they differ). It cooks a committed fixture project, `tests/fixtures/assets`, that contains every importer's fixtures, with `--locked`. On Linux it cooks twice, cold with `--jobs 1` and warm with `--jobs 8` from another directory, and compares bytes. Each OS uploads its manifest and pack; a final job compares them entry by entry and fails on any difference. Shader fixtures join the job with the shader payload (P34), and its Windows leg then restores the Slang native package: the Windows cook ADR-0012 asks for when it requires cross-OS SPIR-V to be shown equal. The job runs whenever a PR changes `tools/`, the fixture project or the assets reader (a path set added to `.github/scripts/ci-tier.sh` with its self-test cases), and nightly. Like the other required checks, a failure blocks the PR.

**If the OSes disagree,** the job prints each differing entry with its kind, its source and both hashes, and says whether the payload or only the stored (compressed) bytes differ. A payload difference is a bug in an importer or a native decoder that isn't portable: it is fixed at its source, never tolerated by excluding the entry. If a native decoder can't be made portable (a float decoder, say), its importer issue stops with `needs-decision`, offering to cook those payloads on one OS only or to ship them encoded and decode at load time. A difference only in stored bytes means the BCL's encoders aren't portable; the job stays red and the maintainer chooses between cooking release packs on one OS, storing them uncompressed, and an in-house encoder. The check itself is never loosened.

## 6. Security

Sources are untrusted input: a mod, a downloaded asset pack or a pull request can carry a crafted file. Every parser enforces fixed limits before it allocates, computes sizes in 64-bit or checked arithmetic, decompresses into a buffer of exactly the declared size and fails on more or fewer bytes (as the pack reader does), recurses only with a depth counter, and reports a limit as `InvalidData` naming the field and the limit. Limits are constants of each parser, not options, each tested on both sides (section 8). The generic per-import limits are `ImportLimits` (section 2).

| Parser | Limits |
|--------|--------|
| `.meta` | 1 MiB; depth 32; 1,024 assets; strings 1,024 bytes |
| PNG, TGA (header check, then stb) | the source 256 MiB; width and height 1-16,384 (the texture payload's range); width x height at most 67,108,864 texels (256 MiB of RGBA); PNG signature and `IHDR` first; TGA fields as section 3 |
| Atlas definitions | 64 KiB; 4,096 sprites; 64 pages; every page within `maxPageSize` |
| Aseprite | 256 MiB; header size equals the file length; canvas 1-8,192 per side; 4,096 frames; 256 layers, group nesting 16; 65,536 chunks per frame; cel sizes within 16,384 and inside 2x the canvas; zlib output exactly width x height x bytes per pixel; 256 palette entries; 1,024 tags and slices; strings 1,024 bytes; composed frames together at most 256 MiB |
| Audio (Ogg reader, then SDL_mixer) | 512 MiB; 1-2 channels; 8,000-192,000 Hz; 30 minutes; a clip at most 256 MiB decoded; Ogg pages within the format's 65,307 bytes with valid capture patterns and checksums; Vorbis header packets 1 MiB |
| Fonts (then FreeType) | 64 MiB; 16 sizes of 4-512 px; 65,536 glyphs per size; 16 pages of at most 4,096 px |
| Shaders (then Slang) | 4 MiB per file; 256 imported files, 64 MiB in total; 16 entry points; 256 bindings; SPIR-V 16 MiB; strings 1,024 bytes |
| Tiled JSON and XML | 64 MiB per file; depth 64; map 1-16,384 tiles per side and 16,777,216 cells per layer; 1,024 layers, group nesting 16; 256 tilesets; 1,048,576 objects, 4,096 points per polygon; 256 properties per object; strings 64 KiB; tile data decoded to exactly width x height x 4 bytes. XML: `DtdProcessing.Prohibit`, `XmlResolver = null`, `MaxCharactersInDocument` 64 Mi, no entity expansion |
| LDtk | 64 MiB per file; 256 external level files; 4,096 levels; 256 layer instances per level; 16,777,216 cells per layer; 1,048,576 entities; depth 64 |
| Input maps | the input note's 1 MiB and its rules |

All JSON is read with `Utf8JsonReader` or `JsonDocument` with `MaxDepth` set, comments and trailing commas rejected. Native decoders (stb, SDL_mixer, FreeType, Slang) only receive input that passed the managed checks above; their own robustness is upstream's, and a crash in one found by fuzzing goes to the BRUTAL issue draft, not to a workaround here.

**Fuzz targets** (SharpFuzz, nightly, 30 minutes per target, docs/05's fuzz tier, `docs/05-testing-strategy.md:19`): `AssetMeta.Read`; the image header check plus decode; the atlas definition reader; the Aseprite parser and compositor; Tiled JSON; Tiled XML; LDtk; the Ogg and Vorbis header reader; the audio importer end to end. Each target runs the whole importer over an in-memory file system, so native code is exercised too (SharpFuzz instruments managed code only, so coverage guidance stops at the native boundary). The corpus is seeded from the fixtures of section 8; every crash becomes a regression row in the importer's tests.

## 7. Packaging

- **The dotnet tool.** `BrutalEngine.Cook` is packed as a .NET tool (`PackAsTool`, `ToolCommandName` `brutal-cook`) in 0.3.0, carrying Importers, Importers.Shaders and their natives, as ADR-0012 defers to M2. Its package size (the Slang native alone is about 99 MB, `docs/02-tech-stack.md:32`) and whether it ships as one package or one per RID are questions for the publishing note.
- **The importer libraries.** `BrutalEngine.Importers` and `BrutalEngine.Importers.Shaders` are packed as ordinary libraries in 0.3.0, with `PublicAPI.Unshipped.txt` and XML docs like every engine library. `BrutalEngine.Cook` is an executable with no public API and no PublicAPI files.
- **The ship gate today.** Nothing under `tools/` can pack for 0.2.0: the root `Directory.Build.props` makes every project unpackable unless `src/Directory.Build.props` says otherwise (`Directory.Build.props:32-33`), and CI's Pack step packs the whole solution (`.github/workflows/ci.yml:191`) and then `check-packages.sh`, which expects only `src/*/*.csproj` (`.github/scripts/check-packages.sh:70`), fails on any package it doesn't expect (`.github/scripts/check-packages.sh:109`). So a tool project that set `IsPackable` early would fail CI. The architecture tests' ship-gate rule reads `src/` only (`tests/BrutalEngine.Architecture.Tests/Repository.cs:48-50`).
- **The gate for 0.3.0** must cover the three tool projects the way it covers libraries: each declares `BrutalEngineMilestone` 2; `IsPackable` is derived from it as `src/Directory.Build.targets` does; and `ShipGateRule` and `check-packages.sh` include the packable `tools/*/*.csproj`. Where tools read `BrutalEngineReleaseMilestone` (today set only in `src/Directory.Build.props`, which is frozen until the 0.2.0 tag) is a question for the publishing note; this note names it and doesn't solve it. The packaging issue is blocked by that note and lands after the tag.
- **Debug-only reference.** A game references the importers in its Debug configuration only and creates the importer in `#if DEBUG` code, so a Release build neither compiles against nor ships it:

```xml
<ItemGroup Condition="'$(Configuration)' == 'Debug'">
  <PackageReference Include="BrutalEngine.Importers" />
  <!-- Optional: in-process shader import and hot reload; brings the Slang native package. -->
  <PackageReference Include="BrutalEngine.Importers.Shaders" />
</ItemGroup>
```

```csharp
#if DEBUG
static IAssetImporter? CreateImporter(Logger logger)
{
    ImporterRegistry registry = ImporterRegistry.CreateDefault();
    registry.Freeze();
    return new SourceAssetImporter(new SourceAssetImporterOptions { Registry = registry, Logger = logger });
}
#else
static IAssetImporter? CreateImporter(Logger logger) => null;
#endif
```

  The constructor has no expected failure: an unfrozen registry is misuse and throws. Without the shader package, a shader sidecar's import fails with `Unsupported` ("importer 'shader' isn't registered"), and the game uses cooked shaders from a pack. The Release check is the asset database note's ([section 7](assets.md#7-development-mode)): the M2 sample game's Release publish must contain no importer assembly.

## 8. Testing

All on Windows and Linux CI, in `tests/BrutalEngine.Importers.Tests`, `tests/BrutalEngine.Importers.Shaders.Tests` (run only in the shader job that restores Slang) and `tests/BrutalEngine.Cook.Tests`, over `InMemoryFileSystem` (`src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:582`) unless a test needs real files.

- **Fixtures.** Committed source files under `tests/fixtures/assets/` (the cook fixture project) and each test project's `Fixtures/`. Each directory has a `LICENSES.md` listing every file, its author and licence; fixtures are made for the tests (CC0 or MIT, by the contributor) or taken from sources whose licence allows redistribution, never from games. Binary fixtures are small (under 64 KiB each) and include hand-made edge cases: every PNG colour type and bit depth, every TGA image type, each Aseprite colour depth and cel type, Tiled maps in every encoding and compression, LDtk with external levels.
- **Unit tests per importer** for every rule of sections 1-3 and every limit of section 6, a row on each side of each limit, with expected values written from outside the code under test: hand-computed texels (premultiplication rounding at 0, 127, 128 and 255), packer layouts worked by hand for small sets, Aseprite composition of two layers at known opacities, tile ids with every flip bit.
- **Golden outputs.** Each fixture's cooked payload is committed beside it (`<fixture>.golden.bin` for this note's formats, and for owners' formats once their notes land). A golden test fails when output bytes change; the fix is to raise the importer's `Version` and regenerate the goldens in the same commit, which the review checks. Golden files are produced by the importer, so they guard against change, not correctness; correctness comes from the unit tests above and a reference reader in the tests for `SHDR`, written from section 3's table.
- **Packer properties** (CsCheck): random sprite sets never overlap, keep padding, stay within pages, and give the same layout for every input order.
- **`.meta` round trips:** `Read` then `ToJson` gives canonical bytes; random valid sidecars round-trip; every rule's rejection; settings migration from version 1 when an importer reaches version 2.
- **Determinism tests:** the fixture project cooked with `--jobs 1` and `--jobs 8`, cold and warm cache and `--no-cache`, from two directories, gives identical bytes; a test importer that reads files in a shuffled order still gives sorted outputs; plus the cross-OS job of section 5.
- **Cache tests:** each key input changed alone causes exactly that source's re-import (counted through a test importer), an unchanged rewrite (same bytes) doesn't, a corrupt entry is a miss, a second process on a locked cache exits 74.
- **Command tests:** every option and exit code; the JSON diagnostics against hand-written expected lines; `--locked` with a missing, outdated or orphan sidecar; a failed cook leaving the old pack untouched.
- **Cook-to-load round trips** (ADR-0005), in `tests/BrutalEngine.Integration.Tests`: cook the fixture project, open the pack with `AssetPack.Open` and mount it in an `AssetDatabase`, load every asset through its loader, and compare with a development-mode database using `SourceAssetImporter` over the same sources, byte for byte, as the asset database note's [section 12](assets.md#12-testing) asks.
- **Fuzz targets** of section 6, nightly.
- **Mutation self-checks**, for example: iterate a dictionary when ordering pack entries; skip re-hashing a recorded read on a cache hit; drop the colour-map check of the TGA header; accept a zlib cel longer than its size; premultiply with truncation instead of rounding.

## 9. Issue split

Sized from measured files on `main`, so every row can be recounted:

- `VfsPath.cs` plus `VfsPathParser.cs` are 528 source lines for 23 `PublicAPI` lines with 801 test lines (a public parser: about 23 lines per API line, tests 1.5x).
- `VirtualFileSystem.cs` is 330 lines for 13 API lines with 952 test lines (a thread-safe service: about 25 lines per API line, tests 2.9x).
- `Entity.cs` is 105 lines for 14 members with 135 test lines (a value type: 7.5 lines per member, tests 1.3x). A positional record struct of three fields is 8 API lines (`VfsEntry`, `src/BrutalEngine.Platform/PublicAPI.Unshipped.txt:593-600`).
- `CopyValidator.cs` (in `src/BrutalEngine.Graphics/Validation/`) is internal and fully documented: 565 lines for 29 checks, each throwing with a message that names the field, the value and the bound, with 921 test lines in its three test files (internal rule checking: about 19.5 lines per checked rule, tests 1.6x).

Public types are counted with the first three units, and their API lines are listed. Internal code (every built-in importer, the shader payload writer and the whole cook tool, none of which has API lines, section 7) is counted with the fourth: one rule per check or limit that sections 1-6 give it, plus the named fixed parts (a reader or writer, an importer class of about 40 lines), with tests at 1.6x, or 2.9x for concurrent code. Fixture and golden files are binary or generated and not counted. The Importers rows share one `PublicAPI.Unshipped.txt` and the `CreateDefault` list (one line per importer), so they run in order unless the column says otherwise. Every row is blocked by this note. Assets issues are named by what they add, because that note's own split may still be renumbered.

| Issue | Scope | Blocked by | Arithmetic: src + tests + API | Est. |
|-------|-------|------------|-------------------------------|-----:|
| P1 feat(importers): add the Importers project and the `.meta` model | Project, test project, solution and Architecture.Tests lines; `AssetMeta`'s properties, `AssetMetaEntry`, the JSON context; reading limits (size, depth, byte-order mark) and the format version | the Assets issue that adds `AssetId` and `AssetKind` | src scaffolding 40 + 5 members x 7.5 (38) + entry 33 + context 40 + 5 rules x 19.5 (98) = 249; tests (71 x 1.3) + (138 x 1.6) = 313; API 26 | ~588 |
| P2 feat(importers): check `.meta` properties and add diagnostics | Section 1's `importer`, `settingsVersion`, `settings`, `compression` and string rules; `ImportDiagnostic`, `DiagnosticSeverity` | P1 | src 6 rules x 19.5 (117) + diagnostic 62 + severity 22 = 201; tests (117 x 1.6) + (84 x 1.3) = 296; API 25 | ~522 |
| P3 feat(importers): check `.meta` asset entries | Section 1's `assets` rules: count, main entry first, ordinal names, name pattern, unique names and ids, id text, kind codes | P2 | src 8 rules x 19.5 (156) = 156; tests x 1.6 = 250 | ~406 |
| P4 feat(importers): write canonical `.meta` files | `ToJson`, `CreateNew`, `WithAssets` and its checks, `AssetCompression`; the canonical form's rules | P3 | src 3 members x 25 (75) + 5 rules x 19.5 (98) + enum 26 = 199; tests (75 x 1.5) + (98 x 1.6) + (26 x 1.3) = 303; API 9 | ~511 |
| P5 feat(importers): add the importer contract | `IImporter`, `ImporterInfo`, `ImportOutput`, `ImportLimits` | P4 | src interface 70 + info 40 + output 40 + limits 47 = 197; tests (157 x 1.3) = 204; API 56 | ~457 |
| P6 feat(importers): add the importer registry | `ImporterRegistry` with every lookup rule and `Freeze` (frozen, so tested as a parser, not a concurrent service) | P5 | src 9 public members x 23 (207) = 207; tests x 1.5 = 311; API 9 | ~527 |
| P7 feat(importers): add the import context's reads | `ImportContext`'s properties, `ReadSource`, `ReadFile`, `ResolveAsset`: `res://` only, the read limits, recorded hashes | P6 | src 4 members x 7.5 (30) + 6 rules x 19.5 (117) = 147; tests (30 x 1.3) + (117 x 1.6) = 226; API 8 | ~381 |
| P8 feat(importers): add the import context's outputs | `GetOutputId` with `AllowNewOutputs` and `BEC1011`, `AddOutput`'s checks, the output limits, `Report` | P7 | src 6 rules x 19.5 (117) = 117; tests x 1.6 = 187; API 3 | ~307 |
| P9 feat(importers): run one import for the cooker and the runtime | `SourceImport.Run`'s pipeline (sidecar, importer lookup, settings, context), `SourceImportRequest`, `SourceImportResult`, `ImportRead` | P8 | src records 142 + 5 rules x 19.5 (98) = 240; tests (142 x 1.3) + (98 x 1.6) = 341; API 48 | ~629 |
| P10 feat(importers): check import outputs and failures | `Run`'s output rules, failure handling (kept diagnostics and reads, the synthesised error diagnostic, `BEC0002`) | P9 | src 10 rules x 19.5 (195) = 195; tests x 1.6 = 312 | ~507 |
| P11 feat(importers): implement the asset database's import seam | `SourceAssetImporter`, its options, `ReadMeta`, `Import` | P10; the Assets issue that adds the seam types | src service 100 + options 40 = 140; tests (100 x 2.9) + (40 x 1.3) = 342; API 19 | ~501 |
| P12 feat(importers): reuse multi-output imports in development mode | The memo: its key, re-hashing, the byte bound, least-recently-used eviction, the per-source lock | P11 | src 5 rules x 19.5 (98) = 98; tests x 2.9 = 284 | ~382 |
| P13 feat(importers): check PNG and TGA headers | Section 3's header checks and section 6's image limits; fixtures | P10 | src 12 rules x 19.5 (234) = 234; tests x 1.6 = 374 | ~608 |
| P14 feat(importers): import PNG and TGA images | stb decoding and its checks, premultiplication, the texture payload writer, the importer; goldens | P13 | src 3 rules x 19.5 (59) + premultiply 30 + payload writer 60 + importer 40 = 189; tests x 1.6 = 302 | ~491 |
| P15 feat(importers): import input maps | The `input-map` importer | P10; the input note's `FromJson`/`ToJson` on `main` | src 3 rules x 19.5 (59) + importer 40 = 99; tests x 1.6 = 158 | ~257 |
| P16 feat(importers): add the atlas packer | MaxRects placement: the sort keys, best-short-side scoring, the tie-breaks, free-rectangle splitting and pruning; the property tests | P10 | src 8 rules x 19.5 (156) + free list 60 = 216; tests x 1.6 = 346 | ~562 |
| P17 feat(importers): add atlas pages, trimming and extrusion | New pages, `BEC2101`, trimming, extrusion, padding, page shrinking, the packer settings | P16 | src 9 rules x 19.5 (176) = 176; tests x 1.6 = 282 | ~458 |
| P18 feat(importers): read atlas definitions | `.atlas.json`: size, version, member paths and globbing, ordinal order, the sprite limit, members' sidecars (`BEC2102`) | P10 | src 9 rules x 19.5 (176) = 176; tests x 1.6 = 282 | ~458 |
| P19 feat(importers): import sprite atlases | Pages as `TXTR` outputs, the page limit, the atlas output, the importer | P14, P17, P18; the 2D renderer note merged | src 3 rules x 19.5 (59) + atlas output 60 + importer 40 = 159; tests x 1.6 = 254 | ~413 |
| P20 feat(importers): parse Aseprite headers and frames | The header (file size, magic, canvas, colour depth, frame count) and frame framing (magic, chunk count, duration) | P10 | src 9 rules x 19.5 (176) + reader 40 = 216; tests x 1.6 = 346 | ~562 |
| P21 feat(importers): parse Aseprite chunks, layers and palettes | Chunk framing and skipping, layer chunks with their `Unsupported` cases, group nesting, palettes | P20 | src 12 rules x 19.5 (234) = 234; tests x 1.6 = 374 | ~608 |
| P22 feat(importers): parse Aseprite cels | Raw, linked and zlib cels, their bounds and exact sizes | P21 | src 7 rules x 19.5 (137) = 137; tests x 1.6 = 219 | ~356 |
| P23 feat(importers): parse Aseprite tags and slices | Tags (range, direction, repeat), slices (keys per frame, nine-slice centre, pivot), their limits | P22 | src 8 rules x 19.5 (156) = 156; tests x 1.6 = 250 | ~406 |
| P24 feat(importers): import Aseprite sprites | Composition, `ignoreLayers`, the composed-size limit, packing, the atlas output, the importer | P17, P23; the 2D renderer note merged | src 6 rules x 19.5 (117) + atlas output 80 + importer 40 = 237; tests x 1.6 = 379 | ~616 |
| P25 feat(importers): decode audio with SDL_mixer | The decoder calls with every status checked, the format checks, the size and duration limits | P10; ADR-0017 merged | src 8 rules x 19.5 (156) + wrapper 40 = 196; tests x 1.6 = 314 | ~510 |
| P26 feat(importers): cook audio clips | `mode`, the threshold, `BEC2301`, the clip limit, the clip output, the importer | P25; the audio note merged | src 5 rules x 19.5 (98) + clip output 40 + importer 40 = 178; tests x 1.6 = 285 | ~463 |
| P27 feat(importers): cook streamed music | The Ogg page and Vorbis header reader with its checks, the full-decode check, the stream output | P26 | src 7 rules x 19.5 (137) + page CRC 30 = 167; tests x 1.6 = 267 | ~434 |
| P28 feat(importers): check font settings and limits | Section 3's font settings and section 6's font limits, collections `Unsupported` | P17; the text note merged | src 12 rules x 19.5 (234) = 234; tests x 1.6 = 374 | ~608 |
| P29 feat(importers): bake fonts | Rasterising through Text, packing `R8Unorm` pages, `BEC2401`, the font output, the importer | P28; Text's rasteriser on `main` | src 1 rule x 19.5 (20) + rasterising 60 + font output 60 + importer 40 = 180; tests x 1.6 = 288 | ~468 |
| P30 feat(importers): add the shader importer project and compile Slang | `tools/BrutalEngine.Importers.Shaders` and tests, the two layer edges, the compile function moved from the cook tool (a rename plus its call site), the session rules with every status checked | P10; M1's cook tool on `main` | src project 40 + call site 30 + 6 rules x 19.5 (117) = 187; tests x 1.6 = 299 | ~486 |
| P31 feat(importers): hash shader imports and report Slang diagnostics | Imported files re-read through `ReadFile`, `BEC2502`, the file limits, `BEC2501` positions | P30 | src 6 rules x 19.5 (117) = 117; tests x 1.6 = 187 | ~304 |
| P32 feat(importers): reflect shader bindings and push constants | Section 3's parameter walk, the `BindingType` mapping, `BEC2503`, push-constant sizes and stages, the binding limit | P31 | src 12 rules x 19.5 (234) = 234; tests x 1.6 = 374 | ~608 |
| P33 feat(importers): reflect shader entry points and vertex inputs | Entry points, stages and group sizes, the `VertexFormat` mapping, their limits | P32 | src 10 rules x 19.5 (195) = 195; tests x 1.6 = 312 | ~507 |
| P34 feat(importers): write the shader payload | `SHDR` writer, `SlangShaderImporter` (6 API lines), the reference reader in the tests | P33 | src writer 120 + importer 60 = 180; tests x 1.6 = 288; API 6 | ~474 |
| P35 feat(importers): add the tilemap model and its output | The model both tilemap importers fill, the tilemap output | P10; the tilemap note merged | src model 100 + output 60 = 160; tests x 1.6 = 256 | ~416 |
| P36 feat(importers): decode tile data | CSV, base64, zlib and gzip with exact sizes, zstd `Unsupported`, flip bits, `BEC2601` | P35 | src 9 rules x 19.5 (176) + plumbing 30 = 206; tests x 1.6 = 330 | ~536 |
| P37 feat(importers): import Tiled JSON maps and tile layers | `.tmj`: version, orientation, map and layer limits, finite and infinite maps, depth and size limits | P36 | src 9 rules x 19.5 (176) + reader 40 = 216; tests x 1.6 = 346 | ~562 |
| P38 feat(importers): import Tiled tilesets and group layers | Embedded and external (`.tsj`) tilesets, images through `ResolveAsset`, per-tile data, group flattening and nesting | P37 | src 10 rules x 19.5 (195) = 195; tests x 1.6 = 312 | ~507 |
| P39 feat(importers): import Tiled objects | The six object shapes, templates `Unsupported`, the object and point limits | P38 | src 9 rules x 19.5 (176) = 176; tests x 1.6 = 282 | ~458 |
| P40 feat(importers): import Tiled custom properties | The seven property types, files resolved to ids, object references, the property limit | P39 | src 10 rules x 19.5 (195) = 195; tests x 1.6 = 312 | ~507 |
| P41 feat(importers): import Tiled XML maps | `.tmx`: the XML hardening, map, layer and data elements onto the same model | P40 (same model, shared tests) | src 4 rules x 19.5 (78) + element readers 90 = 168; tests x 1.6 = 269 | ~437 |
| P42 feat(importers): import Tiled XML tilesets and objects | `.tsx`, object and property elements | P41 | src 2 rules x 19.5 (39) + element readers 90 = 129; tests x 1.6 = 206 | ~335 |
| P43 feat(importers): import LDtk projects and levels | `.ldtk`, `.ldtkl`, version and limits, level outputs by iid, the world output, tileset images | P36 | src 9 rules x 19.5 (176) + outputs 60 = 236; tests x 1.6 = 378 | ~614 |
| P44 feat(importers): import LDtk layer instances | Tiles, IntGrid, AutoLayer and Entities layers, their limits | P43 | src 7 rules x 19.5 (137) = 137; tests x 1.6 = 219 | ~356 |
| P45 feat(importers): import LDtk entity fields | Typed fields, arrays, entity references | P44 | src 10 rules x 19.5 (195) = 195; tests x 1.6 = 312 | ~507 |
| K1 feat(cook): add the assets command line | Usage, the options and their checks, the exit codes | P10; M1's cook tool on `main` | src 7 rules x 19.5 (137) + 13 options x 5 (65) + exit codes 30 = 232; tests x 1.6 = 371 | ~603 |
| K2 feat(cook): print text and JSON diagnostics | Both forms, property order and omission, the summary, `--warnings-as-errors`, `--verbose` | K1 | src 7 rules x 19.5 (137) + writer 40 = 177; tests x 1.6 = 283 | ~460 |
| K3 feat(cook): scan sources and check sidecars | Mounting, the ordinal walk, orphans, duplicates, missing sidecars, `--locked` | K2; Platform's `DirectoryFileSystem` on `main` | src 8 rules x 19.5 (156) + walker 40 = 196; tests x 1.6 = 314 | ~510 |
| K4 feat(cook): create and update `.meta` files | Creation, atlas members, new and removed outputs, atomic writes, the `meta` command | K3 | src 8 rules x 19.5 (156) = 156; tests x 1.6 = 250 | ~406 |
| K5 feat(cook): check asset dependencies | Unresolved ids, reference packs, cycles, ids in both | K4; the Assets issue that adds `AssetPack.Open` | src 6 rules x 19.5 (117) + graph 40 = 157; tests x 1.6 = 251 | ~408 |
| K6 feat(cook): import in parallel with ordered output | The worker pool, results by index, ordered diagnostics, cancellation, the memory bound | K5 | src 6 rules x 19.5 (117) + pool 40 = 157; tests x 2.9 = 455 | ~612 |
| K7 feat(cook): add the cache index | Keys, hits by content hash, entry files, unreadable entries, removal of unvisited sources | K6 | src 11 rules x 19.5 (215) = 215; tests x 1.6 = 344 | ~559 |
| K8 feat(cook): add the cache's payload store | Content-addressed payloads and compressed forms, atomic writes, the lock file, collection | K7 | src 6 rules x 19.5 (117) + store 40 = 157; tests x 2.9 = 455 | ~612 |
| K9 feat(cook): write packs | Section 4's pack writing, the atomic replace, the round trip through `AssetPack` | K6; the Assets issue that adds `ReadContent` | src 9 rules x 19.5 (176) + writer 60 = 236; tests x 1.6 = 378 | ~614 |
| K10 feat(cook): compress pack entries and write manifests | The codecs and their pinned settings, `auto`, stored kinds, the sidecar override, the manifest | K9 | src 7 rules x 19.5 (137) + manifest 40 = 177; tests x 1.6 = 283 | ~460 |
| K11 ci: check cooked packs are byte-identical on Windows and Linux | The fixture project, the `cook-determinism` job, its compare script and self-test, the `ci-tier.sh` paths and cases | K10, P14, P15 (the job grows as importers land) | scripts 80 + self-test 80 + workflow 40 + fixtures index 50 | ~250 |
| K12 test(cook): cook-to-load round trips | The integration tests of section 8 | K9, P11; the Assets issue that adds the database's loads | tests 300 + harness 50 | ~350 |
| K13 test(importers): fuzz the importers | Section 6's targets, the seeded corpus, the nightly job | P14, P24, P27, P42, P45 | 9 targets x 30 + job 40 | ~310 |
| K14 build(cook): pack the dotnet tool and the importer libraries | `PackAsTool`, the ship gate over `tools/` (section 7) | K10; the publishing note merged; the 0.2.0 tag | sized from the publishing note | — |

Rows land at about 250-630 lines. The rows under 400 (P7, P8, P12, P15, P22, P31, P42, P44) are halves of one type or format whose other half would push a single row over 650, or, for P15, a row with its own blocker; the rows at 600-630 (P9, P13, P21, P24, P28, P32, P43, K1, K6, K8, K9) are the ones where a further split would cut one rule table in half. Altogether about 27,800 lines in 58 sized issues. Each issue's CHANGELOG entry goes under `## Next: 0.3.0 (not in the next release)`. Order: P1-P12 first (the seam unblocks development mode), K1-K10 once M1's cook tool is on `main`, importers as their owners' notes merge.

## Consistency with the ADRs

- **ADR-0005:** the cooker writes versioned packs; development builds import in-process through the same importer code (`SourceImport.Run` serves both); identity is a GUID in `.meta` sidecars; cooked output is deterministic and tested (section 5); cook-to-load round trips and fuzzing are tested (sections 6 and 8). It holds.
- **ADR-0012:** the shader command is unchanged; its compile function moves into Importers.Shaders with byte-identical output, proven by M1's own re-cook job; built-in shaders keep committed SPIR-V and C# layouts until their own ADR. Slang stays out of the general build jobs because the shader importer has its own project. The packaging ADR-0012 defers to M2 is section 7. It holds.
- **ADR-0017 (in progress, #279):** used as #279 records it. The 10-second threshold and the "streams are Ogg Vorbis sources" rule are this note's, within that decision.
- **ADR-0011:** Importers takes `Microsoft.Extensions.ObjectPool` through Stb, as allowed for L6 tools. **ADR-0016:** milestone 2 work, nothing packed for 0.2.0, no frozen file changed before the tag (section 7).
- **docs/03 3.13** lists importers "in the cooker"; here they are a library the cooker and the development runtime share, which is ADR-0005's consequence. Animations, localisation tables, scenes and prefabs are deferred (section 3). docs/03 3.11's "cooked to PCM or ADPCM" becomes PCM only, per #279's decision.

Nothing here deviates from an ADR.

## Not verified

- The note is design only; nothing in it has run. BRUTAL APIs are cited from the BRUTAL source, not observed: in particular stb's format detection (the TGA colour-map argument of section 3) is from stb's documented signatures, and the Slang reflection calls follow what spike S4 used on Windows (`docs/spikes/README.md:95`).
- Slang's thread-safety on one global session isn't documented; the shader importer serialises its calls until a stress test shows otherwise (section 3).
- SDL_mixer's lifetime and threading rules, and whether its float decoders agree across OSes, are ADR-0017's prototype's to measure (#279).
- Cross-OS equality of Slang, FreeType and the BCL's compressors (section 5): the determinism job decides.
- The owners' payload layouts (atlases, fonts, tile data, audio clips) don't exist yet; their notes do that.
- The Text rasteriser, `DirectoryFileSystem` and the Assets project are not on `main` yet; the rows that need them say so.

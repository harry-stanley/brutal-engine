# Design: the cook tool (`tools/BrutalEngine.Cook`, `brutal-cook`)

## Purpose

`brutal-cook` turns source assets into what the runtime loads, offline. In M1 it has one verb, `shader`, which compiles a Slang source to SPIR-V ([ADR-0012](../adr/0012-m1-shader-cooking.md)). The M2 cooker adds its verbs (`assets`, ...) to the same dispatcher and inherits `tools/Directory.Build.props` unchanged.

It is an L6 tool (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs`): runtime modules never reference it, and games never ship it or `Ahwoo.Brutal.Slang`. It runs with the JIT; nothing requires a NativeAOT build in M1. The tool and its tests build from `BrutalEngine.Tools.slnx`, not `BrutalEngine.slnx`, so the engine's build and test jobs never restore the ~99 MB Slang native package.

## Command line

```
brutal-cook <verb> [arguments]
brutal-cook --help
brutal-cook shader <source.slang> --entry <name>:<vertex|fragment|compute> [--entry ...]
                   --output-dir <dir> [--matrix-layout column-major|row-major]
brutal-cook shader --manifest <shaders.cook.json> [--manifest ...] [--check]
brutal-cook shader --help
```

- **Verbs** are an immutable table from the exact (ordinal) verb name to its entry point. No verb or an unknown one, including `--help` followed by anything, prints usage to standard error and exits 64. `brutal-cook --help` alone prints usage to standard output and exits 0.
- **`shader`** takes one source path and its options in any order. `--help` anywhere prints the verb's usage to standard output and exits 0. Relative paths resolve against the working directory.
  - `--entry` is repeatable and at least one is required. The name is an ASCII identifier (`[A-Za-z_][A-Za-z0-9_]*`): it names an output file, so it can't contain path characters. Names must differ ignoring case, because file systems may ignore case. The stage token is exactly `vertex`, `fragment` or `compute` (ordinal; `Vertex` and `vertex ` are errors).
  - `--output-dir` is required and created if missing.
  - `--matrix-layout` defaults to `column-major`. The GPU round-trip test on the first shader that takes a matrix settles the convention; a change goes through this option, never the backend.
  - Every option takes one value and may appear once. Anything else starting with `-` is an unknown option.
- **Outputs:** one SPIR-V module per entry, `<output-dir>/<source file name without extension>.<entry>.spv`, overwritten if present. Each written path is printed on standard output.
- **`--manifest`** (repeatable) selects the manifest form: it cooks every shader the manifests list (see *Manifests*), each output next to its source. It takes no source, `--entry`, `--output-dir` or `--matrix-layout` (the manifest gives them), so any other argument is a usage error; the same manifest may be given twice.
- **`--check`** (manifest form only, at most once) compiles every listed shader in memory and compares each module with the file it would write, writing nothing. A matching file is printed on standard output as `matches: <path>`; a missing, unreadable or different one on standard error as `differs: <path> (missing; cooked M bytes)`, `(can't be read: ...)` or `(expected N bytes, cooked M bytes, first difference at byte K)`, where N is the file's length and K the first byte offset at which the two differ (the shorter length when one is a prefix of the other). Every shader is compared before it exits 6.
- In the manifest form every manifest is read and every source checked before Slang loads, so a bad manifest (64) or a missing source (3) writes nothing. Compilation then goes in manifest order, one Slang global session per source; the first compile failure (2) or write failure (4) stops the run.

## Exit codes

| Code | Name | Meaning |
|------|------|---------|
| 0 | `Success` | Every output was written, or every checked file matches (or help was printed) |
| 2 | `CompileFailed` | Slang reported errors. Its diagnostics (`error[E...]` and `--> path:line:col`) go to standard error unchanged; nothing is written |
| 3 | `InputNotFound` | The source isn't an existing file, or a manifest can't be read. Checked before Slang loads |
| 4 | `OutputFailed` | The output directory can't be created or a file can't be written. Files written before the failure stay |
| 5 | `ToolFailure` | The Slang native library didn't load, or a global session, the `spirv_1_6` profile, a compile request or an entry point's code was missing |
| 6 | `CheckFailed` | `--check` found a missing, unreadable or different file; each is named on standard error and nothing was written |
| 64 | `Usage` | Bad command line (the error and the usage go to standard error), or an invalid manifest (one line naming the manifest, the JSON path and the rule) |

1 is never used, so a crash can't pass for a known failure. New verbs add codes from 7 up.

## Compilation

One global session per run (`Slang.TryCreateGlobalSession`) and one compile request per source, through BRUTAL's compile-request API (`Brutal.Slang/Generated/Structs.gen.cs`): target `CompileTarget.Spirv` with the `spirv_1_6` profile (SPIR-V 1.6, which Vulkan 1.3 drivers must accept), the matrix layout, the source added by file path, and one `AddEntryPoint` per entry in command-line order.

- **Strings** passed to Slang are UTF-8 with a trailing NUL. BRUTAL's `RefString8` hands Slang a raw pointer and checks nothing (spike S4).
- **Code** is copied out of `GetEntryPointCode`, which Slang owns, before the request is disposed. Nothing is written until every entry has compiled, so a failed compile leaves existing outputs untouched.
- **The global session** is a COM proxy that the garbage collector never releases (`Brutal.Slang/Slang.cs`). It is released with `Slang.Release` in a `finally` on every path: success, compile failure, a tool failure and an exception (an undefined stage or layout value reaching the compiler).
- **Warnings** on a successful compile also go to standard error.

## Determinism

The same source, entries and options give byte-identical SPIR-V across runs and processes. The source path doesn't reach the module: the same file cooked through an absolute and a relative path gives the same bytes (observed on linux-x64 with `Ahwoo.Brutal.Slang` 2026.10.3, Slang 2026.7). The CI `cook` job compares cooked output with the committed SPIR-V byte for byte (see *Manifests*). The committed fixture SPIR-V is cooked on linux-x64. Whether Windows produces the same bytes is still unverified: ADR-0012 (Consequences) requires the PR that adds the job to show it, and a difference is a reason to revisit ADR-0012, never to loosen the check.

## Manifests

Built-in shaders are committed as `.slang` sources next to their consumer, with their SPIR-V, which the consumer embeds. Each consumer lists its shaders in a `Shaders/shaders.cook.json` of its own, so no file is shared between consumers. The first is the cook tool's test fixture, `tests/BrutalEngine.Cook.Tests/Fixtures/Shaders/shaders.cook.json`, so the CI job has real input before any engine shader exists:

```json
{
  "version": 1,
  "shaders": [
    {
      "source": "sprite.slang",
      "entries": [
        { "name": "vertexMain", "stage": "vertex" },
        { "name": "fragmentMain", "stage": "fragment" }
      ],
      "matrixLayout": "column-major"
    }
  ]
}
```

- Read with `Utf8JsonReader` (no reflection) as standard JSON: no comments, trailing commas or second value. A string that isn't valid UTF-8 is invalid JSON. `ShaderManifest.TryRead` reads only the bytes it is given, so reading never loads Slang or writes a file.
- `version` (required) is the number `1`. `shaders` (required) lists at least one shader; each has `source` and `entries` (required, at least one) and `matrixLayout` (optional, `column-major` by default).
- `source` is relative to the manifest's directory, uses `/` on every OS, and must name a file inside that directory: not empty, not absolute, no `\`, NUL or `:`, no segment ending in `.` or a space (other than `.` and `..`), no `..` that leaves the directory, not a bare directory or `.slang`. The `:` and trailing-character rules keep a manifest meaning the same file on every OS: `C:a.slang` is a drive path only on Windows, and Windows drops a segment's trailing dots and spaces. Symbolic links aren't resolved, so the check is on the path as written.
- Entry names and stage and layout tokens follow the command line's rules: ASCII identifiers, unique ignoring case within a shader; exactly `vertex`, `fragment`, `compute`, `column-major`, `row-major`.
- Two sources in one directory whose names match ignoring case and extension (`a.slang` and `A.hlsl`) would write the same files, so the second is an error.
- Every property must be known and given once (names are case-sensitive). Each broken rule is a usage error (64) naming the manifest and the JSON path, e.g. `brutal-cook shader: Shaders/shaders.cook.json: $.shaders[1].entries[0].stage: unknown stage 'pixel'; expected vertex, fragment or compute.`
- Outputs go next to each source: `<source directory>/<source file name without extension>.<entry>.spv`.

After changing a shader, re-cook its manifest and commit the new `.spv` in the same commit:

```
dotnet run --project tools/BrutalEngine.Cook -c Release -- shader --manifest <consumer>/Shaders/shaders.cook.json
```

**The CI check.** The `cook` job in `.github/workflows/ci.yml` (ubuntu-24.04, every fast- and full-tier run) restores `BrutalEngine.Tools.slnx` in locked mode, checks its formatting, builds and tests it, then runs `brutal-cook shader --check` over every manifest that `.github/scripts/find-shader-manifests.sh` prints (each tracked `shaders.cook.json` under `src/`, `tests/`, `samples/` and `tools/`). A changed byte in a committed `.spv`, or a `.slang` edited without re-cooking, fails it with exit 6 and the `differs:` lines; it also fails if the finder finds no manifest. `CI passed` requires the job in the fast and full tiers. It is the only job that restores the Slang native.

## Testing

`tests/BrutalEngine.Cook.Tests` (in `BrutalEngine.Tools.slnx`; the CI `cook` job runs them on Linux):

- `CookCommandLineTests`: every valid form and usage error, exact stage and layout tokens, identifier and case-insensitive duplicate rules; the manifest form's options.
- `ShaderManifestTests`: valid manifests and a row for each rule above, each asserting the manifest path and the JSON path in the error; invalid UTF-8; sources that leave the manifest's directory, read from a real tree, writing nothing; and a valid manifest on a real tree (sources in the directory, a subdirectory and a missing one), whose whole recursive listing is unchanged after reading.
- `DispatcherTests`: no verb, unknown verbs, `--help`, and that a verb gets only the arguments after it.
- `ShaderCommandTests` (real Slang): two entries give two modules starting with the SPIR-V magic number with the right execution models; a compute entry; two runs are byte-identical; overwriting; the matrix layout reaches Slang; a syntax error exits 2 with Slang's diagnostic and writes nothing; missing source, unwritable output and a Slang load failure.
- `ShaderCommandTests`, manifest form: every listed shader cooked next to its source, byte-identical to the committed fixture SPIR-V (SPIR-V 1.6, the right execution models); two manifests; the manifest's matrix layout reaches Slang; compile errors (2) in both modes; a missing or unreadable manifest and a missing source (3), a source outside the manifest's directory and a later invalid manifest (64), all before Slang loads; an unwritable output (4). `--check`: matching files (0); a changed byte, a shorter and a longer file, a missing and an unreadable file (6, each named with its sizes and first difference); and that it never writes (contents, file times and the directory's time unchanged).
- `find-shader-manifests-test.sh` (run by CI's tier job): which tracked paths the finder prints and in what order, untracked and build-output copies ignored, raw names, and its failures.
- `SlangSessionTests`: a counting seam around `Slang.Release` proves the session is released once on every outcome.

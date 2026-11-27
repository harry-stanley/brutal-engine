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
brutal-cook shader --help
```

- **Verbs** are an immutable table from the exact (ordinal) verb name to its entry point. No verb or an unknown one, including `--help` followed by anything, prints usage to standard error and exits 64. `brutal-cook --help` alone prints usage to standard output and exits 0.
- **`shader`** takes one source path and its options in any order. `--help` anywhere prints the verb's usage to standard output and exits 0. Relative paths resolve against the working directory.
  - `--entry` is repeatable and at least one is required. The name is an ASCII identifier (`[A-Za-z_][A-Za-z0-9_]*`): it names an output file, so it can't contain path characters. Names must differ ignoring case, because file systems may ignore case. The stage token is exactly `vertex`, `fragment` or `compute` (ordinal; `Vertex` and `vertex ` are errors).
  - `--output-dir` is required and created if missing.
  - `--matrix-layout` defaults to `column-major`. The GPU round-trip test on the first shader that takes a matrix settles the convention; a change goes through this option, never the backend.
  - Every option takes one value and may appear once. Anything else starting with `-` is an unknown option.
- **Outputs:** one SPIR-V module per entry, `<output-dir>/<source file name without extension>.<entry>.spv`, overwritten if present. Each written path is printed on standard output.

## Exit codes

| Code | Name | Meaning |
|------|------|---------|
| 0 | `Success` | Every output was written (or help was printed) |
| 2 | `CompileFailed` | Slang reported errors. Its diagnostics (`error[E...]` and `--> path:line:col`) go to standard error unchanged; nothing is written |
| 3 | `InputNotFound` | The source isn't an existing file. Checked before Slang loads |
| 4 | `OutputFailed` | The output directory can't be created or a file can't be written. Files written before the failure stay |
| 5 | `ToolFailure` | The Slang native library didn't load, or a global session, the `spirv_1_6` profile, a compile request or an entry point's code was missing |
| 64 | `Usage` | Bad command line; the error and the usage go to standard error |

1 is never used, so a crash can't pass for a known failure. New verbs add codes from 6 up.

## Compilation

One global session per run (`Slang.TryCreateGlobalSession`) and one compile request per source, through BRUTAL's compile-request API (`Brutal.Slang/Generated/Structs.gen.cs`): target `CompileTarget.Spirv` with the `spirv_1_6` profile (SPIR-V 1.6, which Vulkan 1.3 drivers must accept), the matrix layout, the source added by file path, and one `AddEntryPoint` per entry in command-line order.

- **Strings** passed to Slang are UTF-8 with a trailing NUL. BRUTAL's `RefString8` hands Slang a raw pointer and checks nothing (spike S4).
- **Code** is copied out of `GetEntryPointCode`, which Slang owns, before the request is disposed. Nothing is written until every entry has compiled, so a failed compile leaves existing outputs untouched.
- **The global session** is a COM proxy that the garbage collector never releases (`Brutal.Slang/Slang.cs`). It is released with `Slang.Release` in a `finally` on every path: success, compile failure, a tool failure and an exception (an undefined stage or layout value reaching the compiler).
- **Warnings** on a successful compile also go to standard error.

## Determinism

The same source, entries and options give byte-identical SPIR-V across runs and processes. The source path doesn't reach the module: the same file cooked through an absolute and a relative path gives the same bytes (observed on linux-x64 with `Ahwoo.Brutal.Slang` 2026.10.3, Slang 2026.7). The CI re-cook job compares cooked output with the committed SPIR-V byte for byte. Whether Windows and Linux produce the same bytes is still unverified (ADR-0012, Consequences).

## Cooking the built-in shaders

Built-in shaders are committed as `.slang` sources next to their consumer, with their SPIR-V, which the consumer embeds. After changing one, re-cook it and commit the new `.spv` in the same commit:

```
dotnet run --project tools/BrutalEngine.Cook -c Release -- shader <dir>/sprite.slang \
    --entry vertexMain:vertex --entry fragmentMain:fragment --output-dir <dir>
```

## Testing

`tests/BrutalEngine.Cook.Tests` (in `BrutalEngine.Tools.slnx`):

- `CookCommandLineTests`: every valid form and usage error, exact stage and layout tokens, identifier and case-insensitive duplicate rules.
- `DispatcherTests`: no verb, unknown verbs, `--help`, and that a verb gets only the arguments after it.
- `ShaderCommandTests` (real Slang): two entries give two modules starting with the SPIR-V magic number with the right execution models; a compute entry; two runs are byte-identical; overwriting; the matrix layout reaches Slang; a syntax error exits 2 with Slang's diagnostic and writes nothing; missing source, unwritable output and a Slang load failure.
- `SlangSessionTests`: a counting seam around `Slang.Release` proves the session is released once on every outcome.

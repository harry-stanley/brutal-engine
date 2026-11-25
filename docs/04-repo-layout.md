# 4. Repository layout

The repository is a single monorepo for engine libraries, tools, tests, samples and docs. Projects are created when their milestone starts, not up front. Empty projects are not allowed.

```
brutal-engine/
├── BrutalEngine.slnx                 # solution (XML .slnx format)
├── global.json                       # pins the .NET 10 SDK and selects the Microsoft.Testing.Platform test runner
├── Directory.Build.props             # shared MSBuild settings (see below)
├── Directory.Build.targets
├── Directory.Packages.props          # central package versions (all Ahwoo.Brutal.* pinned here)
├── NuGet.config                      # nuget.org only, with package source mapping
├── .editorconfig                     # formatting + analyzer severities
├── .gitattributes                    # LF in repo, binary assets, LFS patterns
├── .gitignore
├── LICENSE                           # MIT (engine)
├── NOTICE.txt                        # BRUTAL notice (required, redistributed)
├── THIRD-PARTY-NOTICES.md            # everything else we redistribute (MoltenVK, Vulkan loader …)
├── README.md  CONTRIBUTING.md  SECURITY.md  CODE_OF_CONDUCT.md  CHANGELOG.md
│
├── src/                              # shipped engine libraries (one folder = one NuGet package)
│   ├── BrutalEngine/                 # meta-package: references the default module set
│   ├── BrutalEngine.Core/
│   ├── BrutalEngine.Platform/
│   ├── BrutalEngine.Platform.Sdl3/
│   ├── BrutalEngine.Graphics/
│   ├── BrutalEngine.Graphics.Vulkan/
│   ├── BrutalEngine.Audio/
│   ├── BrutalEngine.Audio.Sdl3/
│   ├── BrutalEngine.Input/
│   ├── BrutalEngine.Assets/
│   ├── BrutalEngine.World/
│   ├── BrutalEngine.Rendering2D/
│   ├── BrutalEngine.Text/
│   ├── BrutalEngine.Tilemaps/
│   ├── BrutalEngine.Physics2D/
│   ├── BrutalEngine.Animation/
│   ├── BrutalEngine.UI/
│   ├── BrutalEngine.Particles/
│   ├── BrutalEngine.Networking/
│   ├── BrutalEngine.DebugOverlay/    # ImGui overlay, SDL3 platform backend, render-interface renderer
│   ├── BrutalEngine.Runtime/         # host, main loop, module registry, splash, credits, publish targets
│   ├── BrutalEngine.Generators/      # Roslyn source generators (netstandard2.0, packed as analyzer)
│   └── BrutalEngine.Analyzers/       # Roslyn analyzers + code fixes (netstandard2.0)
│
├── natives/                          # natives BRUTAL doesn't ship
│   └── macos-vulkan/                 # build scripts + pinned versions for Vulkan loader + MoltenVK
│
├── tools/
│   ├── BrutalEngine.Cook/            # `brutal-cook` dotnet tool: importers, cooker, pack writer
│   ├── BrutalEngine.Importers/       # importer library shared by the cooker and dev-mode runtime
│   └── BrutalEngine.Editor/          # post-v1
│
├── tests/
│   ├── BrutalEngine.Core.Tests/      # one unit-test project per src project (mirrors names)
│   ├── …
│   ├── BrutalEngine.Graphics.Conformance/   # backend conformance suite (runs per backend)
│   ├── BrutalEngine.Rendering2D.Golden/     # golden-image tests + reference images
│   ├── BrutalEngine.Integration.Tests/      # headless engine boot, scenes, cook→load round trips
│   ├── BrutalEngine.Architecture.Tests/     # layering and dependency rules
│   ├── BrutalEngine.Aot.Smoke/              # NativeAOT publish + run harness
│   ├── BrutalEngine.Fuzz/                   # SharpFuzz targets
│   ├── BrutalEngine.Benchmarks/             # BenchmarkDotNet
│   └── TestUtilities/                       # shared fixtures: headless host, in-memory VFS, image diff
│
├── samples/                          # each milestone ends with a shipped sample
│   ├── HelloWindow/  HelloSprites/  …
│   └── Games/                        # complete small games used as release gates
│
├── templates/                        # `dotnet new brutalgame` template package
├── docs/                             # this plan, ADRs, then the manual (docfx)
└── .github/
    ├── workflows/                    # ci.yml, nightly.yml, release.yml, natives.yml, docs.yml
    ├── ISSUE_TEMPLATE/  pull_request_template.md  CODEOWNERS  dependabot.yml
```

## Package granularity and versioning

- One `src/` folder is one NuGet package, with the `BrutalEngine.*` package ID equal to the root namespace.
- **All packages version in lockstep** (same version number, released together). Users never solve a compatibility matrix between engine packages.
- macOS Vulkan natives are shipped in `BrutalEngine.Natives.MoltenVK` (osx-arm64 only), referenced by `BrutalEngine.Graphics.Vulkan` for that RID.
- The `BrutalEngine` meta-package is what games normally reference. Advanced users can reference individual modules.

## Shared build settings

Three layers of `Directory.Build.props`. Each one imports the one above it.

| File | Applies to | Sets |
|------|------------|------|
| `Directory.Build.props` | everything | `net10.0`, latest C#, nullable, explicit usings, unsafe allowed, **warnings as errors**, `AnalysisLevel=latest-recommended`, code style enforced in build, deterministic/CI builds, locked restores (`packages.lock.json`, locked mode on CI), NuGet audit of all (incl. transitive) packages, authors/copyright/repository, `IsPackable=false` |
| `src/Directory.Build.props` | shipped libraries | `IsPackable`, `IsAotCompatible`, XML docs, package ID = root namespace = project name, MIT licence expression, README + `NOTICE.txt` packed, SourceLink + `.snupkg` symbols, package validation, MinVer versioning (`v` tag prefix, 0.1 minimum), public API tracking (`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`), `InternalsVisibleTo` for the matching test project only |
| `tests/Directory.Build.props` | test projects | xUnit v3 on Microsoft.Testing.Platform v2 (executable test apps), CsCheck, code coverage and TRX report extensions, not packable |

`Directory.Packages.props` pins every package version centrally, with transitive pinning on. Runtime identifiers are set only by executables (samples, games, AOT smoke tests), not by libraries. Analyzer and generator projects will target `netstandard2.0`, as Roslyn requires.

## Large binary assets

Reference images and sample game assets use **Git LFS** (patterns in `.gitattributes`). The repo must stay clonable and buildable without LFS for library-only contributors: only the golden tests and samples need LFS content, and they skip with a clear message when it's missing.

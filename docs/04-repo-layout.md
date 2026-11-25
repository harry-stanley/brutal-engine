# 4. Repository layout

The repository is a single monorepo for engine libraries, tools, tests, samples and docs. Projects are created when their milestone starts, not up front. Empty projects are not allowed.

```
brutal-engine/
├── BrutalEngine.slnx                 # solution (XML .slnx format)
├── global.json                       # pins the .NET 10 SDK
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

## Shared build settings (`Directory.Build.props`)

```xml
<TargetFramework>net10.0</TargetFramework>
<LangVersion>latest</LangVersion>
<Nullable>enable</Nullable>
<ImplicitUsings>disable</ImplicitUsings>        <!-- explicit usings in public libraries -->
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<AnalysisLevel>latest-recommended</AnalysisLevel>
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
<GenerateDocumentationFile>true</GenerateDocumentationFile>
<IsAotCompatible>true</IsAotCompatible>         <!-- src/ projects only -->
<Deterministic>true</Deterministic>
<ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
<RestoreLockedMode Condition="'$(CI)' == 'true'">true</RestoreLockedMode>
<EnablePackageValidation>true</EnablePackageValidation>
<PackageLicenseExpression>MIT</PackageLicenseExpression>
<PublishRepositoryUrl>true</PublishRepositoryUrl>   <!-- SourceLink; symbols as .snupkg -->
<RuntimeIdentifiers>win-x64;linux-x64;osx-arm64</RuntimeIdentifiers>
```

Test projects relax only `IsAotCompatible` and the XML-documentation requirement. Analyzer and generator projects target `netstandard2.0`, as Roslyn requires.

## Large binary assets

Reference images and sample game assets use **Git LFS** (patterns in `.gitattributes`). The repo must stay clonable and buildable without LFS for library-only contributors: only the golden tests and samples need LFS content, and they skip with a clear message when it's missing.

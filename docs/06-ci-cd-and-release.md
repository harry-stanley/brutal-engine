# 6. CI/CD and release

The source of truth is `github.com/harry-stanley/brutal-engine`. CI runs on GitHub Actions. Runner images are pinned by label version (no `-latest`), and third-party actions are pinned by commit SHA (Dependabot keeps them up to date).

## Runners

| OS | Hosted runner | Used for |
|----|---------------|----------|
| Windows | `windows-2025` (x64) | Build, unit, native-boundary, software-Vulkan GPU tests, AOT smoke |
| Linux | `ubuntu-24.04` (x64) | Same, plus primary golden-image tests (lavapipe), coverage, docs, packaging |
| macOS | `macos-15` (arm64) | Build, unit, native-boundary, AOT smoke, `.app` bundling. GPU tests depend on spike S3 |

### Self-hosted runners

The maintainer's hardware is registered as self-hosted runners with labels `self-hosted, gpu, <os>, <gpu-vendor>`:

| Runner | OS | GPU |
|--------|----|-----|
| `win-nvidia` | Windows 11 | NVIDIA GeForce RTX 2070 SUPER |
| `linux-nvidia` | Arch Linux (bare metal) | NVIDIA GeForce RTX 2070 SUPER |
| *(none)* | macOS | No Mac available (Q6) |

If Windows and Arch are dual-boot on the same machine, only one of them is online at a time. Nightly jobs therefore use a timeout with a "runner offline" notice, never a silent pass.

Arch is a rolling release, so its runner also catches driver and Mesa regressions early. The release-support baseline for Linux stays the glibc minimum recorded in spike S1, tested on `ubuntu-24.04`. Because the repository is **public**, self-hosted runners **never run untrusted pull-request code**:

- Self-hosted jobs run only on `schedule` (nightly), on `workflow_dispatch`, and on pushes to `main`. They never run on `pull_request` events.
- Runners use ephemeral mode with a dedicated, unprivileged OS user, and workspaces are cleaned per job.
- This follows GitHub's own guidance, which recommends against self-hosted runners for public repositories unless they are isolated like this.

## Workflows

### `ci.yml`: every PR and every push to `main`

1. **Lint:** `dotnet format --verify-no-changes`, markdown link check on docs, licence-header check.
2. **Build matrix** (3 OSes): restore in locked mode, then `dotnet build -c Release` (warnings are errors), then `dotnet build -c Debug`.
3. **Test matrix** (3 OSes): unit, property, native-boundary, integration, allocation, leak, determinism, plus GPU conformance and golden tests on software Vulkan. Test results go to the job summary. Failed golden images upload their diff artifacts.
4. **AOT smoke** (3 OSes): publish one sample with NativeAOT for the runner's RID and run it headless.
5. **Pack:** `dotnet pack`, then package validation, public API check and SourceLink verification. Packages are uploaded as artifacts (never published from CI on PRs).
6. **Security:** CodeQL (C#) and the dependency-review action (blocks new vulnerable or disallowed-licence dependencies).

All jobs are required status checks on `main`. Branch protection: PRs required, 1 approving review (the maintainer), linear history, no force pushes, required checks up to date before merge.

**Every merge into `main` is a squash merge**, so `main` has one commit per PR and its history stays clean. The repository allows only squash merging (merge commits and rebase merging are disabled), uses the PR title as the squash commit title, and deletes head branches after merge.

### `nightly.yml`: scheduled, plus manual dispatch

- Everything in CI with all samples AOT-published.
- Self-hosted real-GPU conformance and golden suites on all three OSes.
- Benchmarks (results stored in a `benchmarks` data branch and compared against a baseline. A regression above 10% opens an issue).
- Fuzzing (time-boxed per target, with the corpus cached).
- Soak tests on self-hosted machines.

### `natives.yml`: manual dispatch / version bump

Builds the natives BRUTAL doesn't ship (macOS Vulkan loader and MoltenVK) from pinned upstream tags. Records checksums, signs the macOS binaries (once a Developer ID is available), and produces the `BrutalEngine.Natives.MoltenVK` package. Builds are reproducible from the recorded tags.

### `release.yml`: on a `v*` tag

1. Verify the tag is on `main` and CI is green for that commit.
2. Build, test and pack (version from MinVer = the tag).
3. Generate release notes from `CHANGELOG.md` for that version.
4. Publish packages to nuget.org using **NuGet trusted publishing** (GitHub OIDC, no long-lived API key). If that's unavailable for the account, fall back to an API key held in a protected `release` environment that needs manual approval.
5. Create the GitHub Release with packages, symbols and sample game builds attached.
6. Deploy the docs site (docfx) for that version.

### `docs.yml`

Builds the docfx site on every PR (broken links and missing API docs fail it), and deploys to GitHub Pages on release.

## Versioning

- **Semantic Versioning 2.0.** During milestones M0 to M5, versions are `0.y.z`. Breaking changes are allowed between minors and must be listed under "Breaking" in the changelog with migration notes. **1.0.0** is released at the end of M6, and after that breaking changes go only into major versions, after at least one minor release with `[Obsolete]` warnings.
- Pre-releases: `x.y.z-preview.N` from release branches when needed.
- `CHANGELOG.md` follows *Keep a Changelog*. Each PR updates the `Unreleased` section.
- The supported BRUTAL version range is stated per engine release. BRUTAL bumps are their own PRs.

## Game publishing pipeline (what game developers get)

`BrutalEngine.Runtime` ships MSBuild targets so a game runs:

```
dotnet publish -c Release -r <rid>
```

and gets:

| Platform | Output |
|----------|--------|
| Windows | Folder or zip: NativeAOT `.exe`, native DLLs, cooked packs, `NOTICE.txt`, `licenses/`. Optional Authenticode signing |
| Linux | Folder or `.tar.gz` with an executable and `$ORIGIN` rpath for natives. Optional AppImage. Documented runtime dependencies (`libvulkan1`, glibc minimum) |
| macOS | A proper `.app` bundle: `Contents/MacOS`, `Contents/Frameworks` (dylibs, MoltenVK, loader), `Contents/Resources` (packs, ICD JSON, `NOTICE.txt`), `Info.plist`, entitlements. Optional codesign, notarisation (`notarytool`) and stapling |

The asset cooker runs as part of publish (incremental). Each platform's output is smoke-tested in CI on that platform for the samples.

## Code signing

Pipelines are designed for signing from the start but run **unsigned** until certificates exist:

- **macOS:** Developer ID Application certificate + notarisation. Secrets live in a protected environment, and signing steps are skipped with a visible warning when they're absent. Unsigned macOS builds trigger Gatekeeper warnings, and the docs explain the workaround for testers.
- **Windows:** Authenticode (e.g. Azure Trusted Signing or an OV/EV certificate). Same skip-with-warning behaviour.
- NuGet packages are signed by nuget.org's repository signature. Author signing can be added later.

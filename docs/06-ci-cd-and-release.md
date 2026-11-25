# 6. CI/CD and release

The source of truth is `github.com/harry-stanley/brutal-engine`. CI runs on GitHub Actions. Runner images are pinned by label version (no `-latest`), and third-party actions are pinned by commit SHA (Dependabot keeps them up to date).

## Runners

| OS | Hosted runner | Used for |
|----|---------------|----------|
| Windows | `windows-2025` (x64) | Build, unit, native-boundary, software-Vulkan GPU tests, AOT smoke |
| Linux | `ubuntu-24.04` (x64) | Same, plus primary golden-image tests (lavapipe), coverage, docs, packaging |
| macOS | `macos-15` (arm64) | Build, unit, native-boundary, `.app` bundling, and the Vulkan + NativeAOT smoke on bundled MoltenVK (the runner's paravirtual Metal GPU) |

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

### `ci.yml`: every PR and every push to `main`, plus nightly

1. **Lint:** `dotnet format --verify-no-changes`, markdown link check on docs, licence-header check.
2. **Build matrix** (3 OSes): restore in locked mode, then `dotnet build -c Release` (warnings are errors), then `dotnet build -c Debug`.
3. **Test matrix** (3 OSes): unit, property, native-boundary, integration, allocation, leak, determinism, plus GPU conformance and golden tests on software Vulkan. Test results go to the job summary. Failed golden images upload their diff artifacts.
4. **AOT smoke:** publish `tests/BrutalEngine.Aot.Smoke` (later, one sample) with NativeAOT for the runner's RID and run it headless, with its JIT build and negative test. Linux runs in every tier. Windows and macOS run in the full tier (see *CI tiers*).
5. **Pack:** `dotnet pack`, then package validation, public API check and SourceLink verification. Packages are uploaded as artifacts (never published from CI on PRs).
6. **Security** (separate workflows so they can also run on a schedule): `codeql.yml` (C# and Actions, `security-and-quality` queries, also weekly) and `dependency-review.yml` (blocks new dependencies with moderate or worse known vulnerabilities). Dependabot (`.github/dependabot.yml`) proposes weekly NuGet, .NET SDK and Actions updates.

The aggregate `CI passed` job, `CodeQL (csharp)`, `CodeQL (actions)` and `Dependency review` are the required status checks on `main`. Branch protection (ruleset "Protect Main"): PRs required, squash merges only, no force pushes or deletion, required checks must pass.

Required approvals are 0 for now. PRs are opened under the maintainer's own account, and GitHub doesn't let a PR author approve their own PR. Only accounts with write access (currently just the maintainer) can merge, so fork PRs can't be merged by their authors. If a second identity starts opening PRs (for example, a bot account for automated work), set the required approvals to 1 and require approval of the most recent push.

#### CI tiers

CI is split so a typical PR gets feedback in about two minutes, while native and GPU coverage still runs before and after every merge:

| Tier | When | Adds |
|------|------|------|
| **Fast** | PRs whose changed paths can't affect native interop, NativeAOT or GPU tests | Format, build and test on all 3 OSes, pack, and the Linux Vulkan + NativeAOT smoke |
| **Full** | PRs touching native-relevant paths, every push to `main`, nightly (15:00 UTC), manual runs | The fast tier plus the Windows and macOS Vulkan + NativeAOT smoke |

- The `Select CI tier` job classifies a PR's changed paths, including the old name of renamed files, with `.github/scripts/ci-tier.sh`. The paths that force the full tier are listed in that script: backend modules (Platform, Graphics, Audio, Runtime), the smoke harness, `.github/actions` and `.github/scripts`, `ci.yml`, `global.json`, NuGet config, and any `Directory.Build`/`Directory.Packages` file.
- The job runs `ci-tier-test.sh` first, so a broken rule fails CI instead of silently skipping coverage. Add a case there when the list changes.
- Nothing is skipped: the smoke matrix is computed by the tier job, so `CI passed` still requires every job it needs to succeed.
- Anything the fast tier misses is caught by the full run on the merge to `main`.
- **Time budget:** a fast-tier PR should finish in under 3 minutes, and no single step should take over 2 minutes on a warm cache. A step that grows past that gets cached, prebuilt or moved to the nightly run.

#### Caching

The repository's Actions cache is limited to 10 GB, and GitHub evicts the least recently used entries beyond that. A cache saved by a PR can be read only by that PR, while `main`'s caches can be read by every PR. So large caches are saved only from `main`:

| Cache | Size | Saved by | Notes |
|-------|------|----------|-------|
| NuGet packages (`.github/actions/dotnet-restore`), Windows and macOS | About 650-720 MB per OS | The build job on `main` only | Keyed on every `packages.lock.json`. PRs read `main`'s copy. A PR that changes a lock file restores from nuget.org. Linux uses no cache, because downloading and extracting it took longer than restoring from nuget.org |
| macOS loader and validation-layer build (`setup-macos-vulkan`) | About 7 MB | Any run that misses | Saves a 6-13 min build. Keyed on the pins, the build script and the runner OS |
| Windows lavapipe, loader and validation layers (`setup-software-vulkan`) | Tens of MB | Any run that misses | Keyed on the Mesa and LunarG SDK pins. Replaces downloading the Mesa archive and the LunarG SDK installer on every run |

Caches unused for 7 days expire. The nightly full-tier run on `main` restores all of them, so they stay warm.

**Every merge into `main` is a squash merge**, so `main` has one commit per PR and its history stays clean. The repository allows only squash merging (merge commits and rebase merging are disabled), uses the PR title as the squash commit title, and deletes head branches after merge.

### `nightly.yml`: scheduled, plus manual dispatch

- Everything in CI with all samples AOT-published.
- Self-hosted real-GPU conformance and golden suites on all three OSes.
- Benchmarks (results stored in a `benchmarks` data branch and compared against a baseline. A regression above 10% opens an issue).
- Fuzzing (time-boxed per target, with the corpus cached).
- Soak tests on self-hosted machines.

### `natives.yml`: manual dispatch / version bump

Builds the natives BRUTAL doesn't ship (macOS Vulkan loader and MoltenVK) from pinned upstream tags. Records checksums, signs the macOS binaries (once a Developer ID is available), and produces the `BrutalEngine.Natives.MoltenVK` package. Builds are reproducible from the recorded tags.

It will also build the CI-only **Khronos validation layers for macOS** once, and publish them as a pinned, SHA-256-checked release asset that `setup-macos-vulkan` downloads, so CI never compiles them. Until then, `setup-macos-vulkan` builds them from the pinned commit (about 10.5 min) and caches the result, keyed on the pins, the build script and the runner OS. A warm restore takes about 5 s. A cold build happens only when one of those changes, or the first time a new cache scope (a PR, or `main`) runs the full tier. Homebrew was rejected for this: it can't be pinned, and it installs its own Vulkan loader where BRUTAL's resolver looks, so the job would no longer prove that the bundled loader is the one used.

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

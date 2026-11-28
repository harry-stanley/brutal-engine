# 6. CI/CD and release

The source of truth is `github.com/harry-stanley/brutal-engine`. CI runs on GitHub Actions. Runner images are pinned by label version (no `-latest`), and third-party actions are pinned by commit SHA (Dependabot keeps them up to date).

## Runners

| OS | Hosted runner | Used for |
|----|---------------|----------|
| Windows | `windows-2025` (x64) | Build, unit, native-boundary, software-Vulkan GPU tests, AOT smoke |
| Linux | `ubuntu-24.04` (x64) | Same, plus primary golden-image tests (lavapipe), coverage, docs, packaging |

macOS isn't in CI ([ADR-0015](adr/0015-tested-platforms-windows-linux.md)): Windows and Linux are the tested targets. In M0, `macos-15` ran the build, the tests and the Vulkan + NativeAOT smoke on bundled MoltenVK; `.github/actions/setup-macos-vulkan` is kept, unused, so that can be restored.

### Self-hosted runners

**Not set up.** Decision (maintainer, 2026-11-26): M1 has no self-hosted runner. Graphics conformance runs on software Vulkan in CI, and real-GPU conformance runs locally on the reference machine (RTX 2070 SUPER) for each graphics PR, with the results recorded in the PR. This is revisited at the close of M1. The plan below is the later target.

The maintainer's hardware will be registered as self-hosted runners with labels `self-hosted, gpu, <os>, <gpu-vendor>`:

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
2. **Build matrix** (Windows and Linux): restore in locked mode, then `dotnet build -c Release` (warnings are errors). Linux also runs `dotnet build -c Debug`: Debug differs from Release only in `DEBUG`-conditional code, which isn't OS-specific.
3. **Test matrix** (Windows and Linux): unit, property, native-boundary, integration, allocation, leak and determinism tests. The job has no Vulkan driver, so it excludes tests marked with the `Gpu` trait (`--filter-not-trait "Category=Gpu"`): GPU tests run on software Vulkan in the `vulkan-smoke` job (item 4). Test results go to the job summary. Failed golden images upload their diff artifacts.
4. **AOT smoke** (the `vulkan-smoke` job): publish `tests/BrutalEngine.Aot.Smoke` and `samples/HelloWindow` with NativeAOT for the runner's RID and run them, with their JIT builds and negative tests (an injected validation error must exit 2; for HelloWindow, a bad option must also exit 64). HelloWindow runs with validation: headless on Linux for 240 frames (`--headless --frames 240`), the first 120 of them the BRUTAL splash; and windowed on Windows, because no Windows driver offers `VK_EXT_headless_surface` (lavapipe included), for 3000 frames with two window resizes on top (`--frames 3000 --exercise-resize`), the splash showing for at least 2.0 s. The run fails unless the scene presented frames after the splash. The published HelloWindow is uploaded as the `hello-window-<rid>` artifact (kept 14 days). The job also runs the `Gpu`-trait tests ([testing strategy](05-testing-strategy.md#gpu-testing-without-gpus)), which the build-and-test job excludes because it has no Vulkan driver: right after the lavapipe setup, a "GPU-trait tests" step builds every test project that declares the trait and runs only those tests (`.github/scripts/gpu-tests.sh`), headless on Linux and windowed on Windows. Each project writes a TRX report of its finished tests into `TestResults/gpu/<project>/`. On Linux, a test host that crashes also leaves the .NET runtime's crash report there (createdump's JSON, with the managed and native stack of every thread and no dump file), and the script prints a summary of it in the log: the crashed thread's stack, and the test method each thread was in. A crashed test host is reported with the project's name and its signal (exit code 128 + N, 139 for SIGSEGV), or as exit code 7, which Microsoft.Testing.Platform returns for a crashed test host when a TRX report is on. When the step fails, the job uploads that directory as the `gpu-test-results-<os>` artifact (kept 14 days). Until a project declares the trait, the step prints "no GPU-trait tests yet". Both OSes run the Khronos validation layer from the LunarG SDK at the same pinned version (1.4.363.1). On Linux, `setup-software-vulkan` keeps Ubuntu's loader and lavapipe, downloads the Linux SDK archive (pinned by version and SHA-256; a mismatch fails the step), keeps only the layer library (its debug sections stripped) and manifest, and points `VK_LAYER_PATH` at them, so the loader can't find another copy. It then loads the layer with `vulkaninfo` and fails unless the loader took the library from that directory and reports the pinned version; the log prints both. Ubuntu's own layers (1.3.275 on ubuntu-24.04), like every layer before 1.4.304, keep the process's devices and instances in a global map that every call reads and that `vkCreateDevice`, `vkDestroyDevice` and instance creation and destruction change without a lock, so GPU tests running in parallel crashed the test host now and then (SIGSEGV, #457). Linux runs in every tier. Windows runs in the full tier (see *CI tiers*).
5. **Pack** (in the Linux build job, from the Release build the tests use): `dotnet pack --no-build`, then package validation, public API check and SourceLink verification. Packages are uploaded as artifacts (never published from CI on PRs).
6. **Cook** (the `cook` job, Linux only, [ADR-0012](adr/0012-m1-shader-cooking.md)): the build-time tools in `BrutalEngine.Tools.slnx`, which no other job builds. It restores them in locked mode (the only job that restores the ~99 MB Slang native), checks their formatting, builds and tests them, then re-cooks every built-in shader: `brutal-cook shader --check` over each committed `shaders.cook.json` that `.github/scripts/find-shader-manifests.sh` lists. Any missing file or byte difference from the committed SPIR-V fails the job, as does finding no manifest ([cook tool design](design/cook.md#manifests)).
7. **Security** (separate workflows so they can also run on a schedule): `codeql.yml` (C# and Actions, `security-and-quality` queries, also weekly; see [Code scanning](#code-scanning)) and `dependency-review.yml` (blocks new dependencies with moderate or worse known vulnerabilities). Dependabot (`.github/dependabot.yml`) proposes weekly NuGet, .NET SDK and Actions updates.

The aggregate `CI passed` job, `CodeQL (csharp)`, `CodeQL (actions)` and `Dependency review` are the required status checks on `main`. Branch protection (ruleset "Protect Main"): PRs required, squash merges only, no force pushes or deletion, required checks must pass.

Required approvals are 0 for now. PRs are opened under the maintainer's own account, and GitHub doesn't let a PR author approve their own PR. Only accounts with write access (currently just the maintainer) can merge, so fork PRs can't be merged by their authors. If a second identity starts opening PRs (for example, a bot account for automated work), set the required approvals to 1 and require approval of the most recent push.

#### Code scanning

CodeQL runs every `security-and-quality` query on all of the code, with two exceptions, each with its reason written next to it. `.github/codeql/codeql-config.yml` excludes the three LINQ suggestions (`cs/linq/missed-where`, `missed-select`, `missed-cast`) everywhere, because [the coding standards](07-coding-standards.md) forbid LINQ on engine hot paths. And a short list of quality rules that misfire on patterns the test rules require (`GC.Collect` before an allocation window, worker threads that catch every exception, explicit double `Dispose`, and others) is filtered on `tests/` only: the C# analysis writes its SARIF without uploading it, `.github/scripts/codeql-filter-sarif.sh` removes a listed rule's result only when every one of its locations is under `tests/`, and `upload-sarif` uploads the rest with the same `/language:csharp` category. Security queries and every unlisted rule still report on tests. The filter fails closed (malformed SARIF, a log with no runs or a run without results, or a missing file fails the job and never uploads a partial or empty file), and its self-test, `codeql-filter-sarif-test.sh`, runs first in every C# CodeQL job. A query-level exclusion can't be scoped to a path, and `paths-ignore` would turn off the security queries for tests too.

#### CI tiers

CI is split so a typical PR gets feedback in about two minutes, while native and GPU coverage still runs before and after every merge, and a merge doesn't repeat what its PR already proved:

| Tier | When | Runs |
|------|------|------|
| **Docs** | PRs that change only documentation nothing builds, tests or packs | Only the tier selection. Format, build, test, pack, cook and smoke are skipped. CodeQL and dependency review still run (they are required checks) |
| **Fast** | PRs whose changed paths can't affect native interop, NativeAOT or GPU tests | Format, build and test on Windows and Linux, pack, the cook job, and the Linux Vulkan + NativeAOT smoke |
| **Full** | PRs touching native-relevant paths or `samples/`, pushes to `main` the merge check can't skip, nightly (15:00 UTC), manual runs | The fast tier plus the Windows Vulkan + NativeAOT smoke, including HelloWindow |
| **Verified** | A push to `main` whose tree is identical to the merged PR's head, which passed the full tier | Only the tier selection. `CI passed` succeeds and links the PR's passing run |
| **Smoke-delta** | A push to `main` whose tree is identical to the merged PR's head, which passed the fast tier | Only the Windows Vulkan + NativeAOT smoke: what the full tier adds to the fast tier |

- The `Select CI tier` job classifies a PR's changed paths, including the old name of renamed files, with `.github/scripts/ci-tier.sh`. The paths that force the full tier are listed in that script: backend modules (Platform, Graphics, Audio, Runtime) and Core, which the smoke harness uses, the test projects of the native backends that hold `Gpu`-trait tests (`BrutalEngine.Platform.Sdl3.Tests` and `BrutalEngine.Graphics.Vulkan.Tests`, so a test-only change still runs the Windows smoke and its GPU tests), the smoke harness, everything under `samples/` (README files included, because the smoke job runs the samples on every OS), `natives/`, `.github/actions` and `.github/scripts`, `ci.yml`, `global.json`, NuGet config, and any `Directory.Build`/`Directory.Packages` file.
- The docs tier applies only when every path is under `docs/`, is one of `CHANGELOG.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md` or `SECURITY.md`, or is a GitHub issue template, the PR template or `CODEOWNERS`. `README.md` and `NOTICE.txt` are not docs-tier paths, because every package includes them.
- Other modules (World, the analyzers) and the test projects not listed above (Core's and World's tests among them, and `tests/TestUtilities`) are fast tier. So are the build-time tools (`tools/`, `BrutalEngine.Tools.slnx` and `tests/BrutalEngine.Cook.Tests`): nothing shipped contains them, and the cook job builds and tests them in every fast and full run.
- The job runs `ci-tier-test.sh` first, so a broken rule fails CI instead of silently skipping coverage. Add a case there when the list changes. It also runs `gpu-tests-test.sh`, the self-test of the `Gpu`-trait discovery the vulkan-smoke job uses, over a scratch tree with a fake `dotnet`. And it runs `check-packages-test.sh`, the self-test of `check-packages.sh`, the check after Pack that the packages are exactly the libraries the ship gate selects (see *Ship gate* under `release.yml`), over scratch trees of project files and empty packages, and `find-shader-manifests-test.sh`, the self-test of the cook job's manifest finder, over scratch git repositories.
- `CI passed` accepts a skipped job only where the tier skips it on purpose: every job in the docs and verified tiers, and format, build-and-test and cook in the smoke-delta tier. In the fast and full tiers, every job it needs must succeed. It fails if the tier can't be selected.
- Anything the fast tier misses is caught on the merge to `main`: by the smoke-delta tier when the trees match, and by the full tier otherwise.

##### The merge check (pushes to `main`)

A squash merge's commit usually has exactly the tree of the PR's head: it does whenever the head contained `main` at merge time. The PR's run tested the head merged with `main` as it was when the run started, with the same inputs, because `global.json` and the lock files pin the toolchain and packages. So the run built and tested that exact tree, with one gap: if `main` gained a change after the run started and reverted it before the merge, the trees still match, but the run tested the head plus that change. The nightly full run is the backstop for that gap. On a push to `main`, `.github/scripts/ci-merge-check.sh` decides how much of the full tier is still needed, using the default `GITHUB_TOKEN` (`contents`, `pull-requests` and `checks`, all read-only):

1. It finds the PR merged into `main` as the pushed commit (`GET /repos/{repo}/commits/{sha}/pulls`, keeping only PRs whose merge commit is this one), and requires exactly one.
2. It compares the pushed commit's tree with the PR head's tree (`GET /repos/{repo}/commits/{sha}`, `.commit.tree.sha`).
3. It reads the GitHub Actions check runs on the PR head (`GET /repos/{repo}/commits/{head}/check-runs?filter=all`, which lists every attempt of every workflow run, not just the latest attempt), every page of them (`gh api --paginate`). Every check run of a job name must have succeeded, so a job that failed and then passed on a re-run, or a cancelled duplicate run, means the full tier. `CI passed` must have succeeded. The PR ran the full tier if the Linux and Windows Vulkan smoke jobs succeeded, and the fast tier if the Linux one did.

Same tree and full tier: **verified**. Same tree and fast tier: **smoke-delta**. Anything else (the tree differs, no single merged PR, the PR's CI didn't pass, a docs-tier PR, an API error, malformed JSON) runs the **full** tier, as does a failure of the check itself. It fails safe: doubt only costs time.

- Pull requests, the nightly schedule and manual runs never use the merge check. The nightly full run on `main` stays unconditional, and catches runner-image drift and the change-then-revert gap above, which a skipped merge can't. CodeQL and dependency review run on every push regardless.
- The tier job runs `ci-merge-check-test.sh` first. It checks every decision against API answers recorded in the shape of real ones, with no network. Its fake `gh` refuses a check-runs request without `--paginate`, and cases spread the check runs over two pages, so a check that reads only the first page fails the self-test. Add a case there when the rules change. The job names the check looks for (`CI passed` and `Vulkan smoke, JIT + NativeAOT (<os>)`) are constants in the script; a rename that misses the script makes every push run the full tier.
- A verified or smoke-delta push saves no NuGet caches and uploads no `packages` artifact, and the `hello-window-<rid>` artifacts come only from the smoke jobs that ran (none when verified, Windows for smoke-delta). The nightly run refreshes `main`'s caches. For artifacts built from a specific `main` commit, run CI manually on `main` (always the full tier).
- **Time budget:** a fast-tier PR should finish in under 3 minutes, and no single step should take over 2 minutes on a warm cache. A step that grows past that gets cached, prebuilt or moved to the nightly run.

#### Caching

The repository's Actions cache is limited to 10 GB, and GitHub evicts the least recently used entries beyond that. A cache saved by a PR can be read only by that PR, while `main`'s caches can be read by every PR. So large caches are saved only from `main`:

| Cache | Size | Saved by | Notes |
|-------|------|----------|-------|
| NuGet packages (`.github/actions/dotnet-restore`), Windows | About 650-720 MB | The build job on `main` only | Keyed on every `packages.lock.json`. PRs read `main`'s copy. A PR that changes a lock file restores from nuget.org. Linux uses no cache, because downloading and extracting it took longer than restoring from nuget.org |
| Windows lavapipe, loader and validation layers (`setup-software-vulkan`) | Tens of MB | Any run that misses | Keyed on the Mesa and LunarG SDK pins. Replaces downloading the Mesa archive and the LunarG SDK installer on every run |
| Linux validation layer (`setup-software-vulkan`) | About 40 MB | Any run that misses | Keyed on the LunarG SDK pin. Replaces downloading the 367 MB Linux SDK archive on every run |

Caches unused for 7 days expire. The nightly full-tier run on `main` restores all of them, so they stay warm.

**Every merge into `main` is a squash merge**, so `main` has one commit per PR and its history stays clean. The repository allows only squash merging (merge commits and rebase merging are disabled), uses the PR title as the squash commit title, and deletes head branches after merge.

### `nightly.yml`: scheduled, plus manual dispatch

- Everything in CI with all samples AOT-published.
- Self-hosted real-GPU conformance and golden suites on Windows and Linux, once the [self-hosted runners](#self-hosted-runners) exist (not in M1).
- Benchmarks (results stored in a `benchmarks` data branch and compared against a baseline. A regression above 10% opens an issue).
- Fuzzing (time-boxed per target, with the corpus cached).
- Soak tests on self-hosted machines.

### `natives.yml`: manual dispatch / version bump

Builds the natives BRUTAL doesn't ship (macOS Vulkan loader and MoltenVK) from pinned upstream tags. Records checksums, signs the macOS binaries (once a Developer ID is available), and produces the `BrutalEngine.Natives.MoltenVK` package. Builds are reproducible from the recorded tags.

The CI-only **Khronos validation layers for macOS** are unused while macOS is out of CI ([ADR-0015](adr/0015-tested-platforms-windows-linux.md)). In M0, `setup-macos-vulkan` built them from the pinned commit (about 10.5 min) and cached the result, keyed on the pins, the build script and the runner OS; a warm restore took about 5 s. If macOS CI is restored, this workflow should build them once and publish them as a pinned, SHA-256-checked release asset that `setup-macos-vulkan` downloads, so CI never compiles them. Homebrew was rejected for this: it can't be pinned, and it installs its own Vulkan loader where BRUTAL's resolver looks, so the job would no longer prove that the bundled loader is the one used.

### `release.yml`: on a `v*` tag

1. Verify the tag is on `main` and CI is green for that commit.
2. Build, test and pack (version from MinVer = the tag).
3. Generate release notes from `CHANGELOG.md` for that version.
4. Publish packages to nuget.org using **NuGet trusted publishing** (GitHub OIDC, no long-lived API key). If that's unavailable for the account, fall back to an API key held in a protected `release` environment that needs manual approval.
5. Create the GitHub Release with packages, symbols and sample game builds attached.
6. Deploy the docs site (docfx) for that version.

**Ship gate** ([ADR-0016](adr/0016-trunk-based-milestones.md), implemented by #254): every engine library's csproj sets `BrutalEngineMilestone`, the milestone that first ships it, and `src/Directory.Build.props` sets `BrutalEngineReleaseMilestone`, the milestone the next release ships. A library packs only when `BrutalEngineMilestone <= BrutalEngineReleaseMilestone`, and a CI step after Pack, `.github/scripts/check-packages.sh`, fails when the packed set differs from the set of projects the rule selects. Until a library of a later milestone exists, no real project takes the gate's exclusion branch, so a second step, "Check the ship gate excludes a later milestone", evaluates `IsPackable` with both properties pinned (releases 1 and 2, each with its own milestone and the next) and fails unless only the later milestone is excluded; pinning keeps it independent of the value the props file releases. Libraries of a later milestone are still built and tested on every PR and are in the source tree at the tag, but the release's packages, notes and assets don't include them. The notes for a tag (step 3) come from the `## [Unreleased]` section of the tagged commit: the version's own heading is added by the PR that follows the tag (see *Versioning*).

### `docs.yml`

Builds the docfx site on every PR (broken links and missing API docs fail it), and deploys to GitHub Pages on release.

## Versioning

- **Semantic Versioning 2.0.** During milestones M0 to M5, versions are `0.y.z`. Breaking changes are allowed between minors and must be listed under "Breaking" in the changelog with migration notes. **1.0.0** is released at the end of M6, and after that breaking changes go only into major versions, after at least one minor release with `[Obsolete]` warnings.
- **Releases are tags on `main`.** Milestone M*n* is released as `v0.(n+1).0`, tagged on `main` (see `release.yml`). Work for the next milestone merges to `main` before that tag, and the ship gate keeps it out of the release ([ADR-0016](adr/0016-trunk-based-milestones.md)).
- Pre-releases: `x.y.z-preview.N` from release branches when needed.
- `CHANGELOG.md` follows *Keep a Changelog*. A PR for the milestone being released next adds its entry to the `Unreleased` section; a PR for the milestone after that adds it to the Next section directly below (`## Next: 0.3.0 (not in the next release)` until `v0.2.0`) ([ADR-0016](adr/0016-trunk-based-milestones.md)).
- **After a release tag**, one PR renames `Unreleased` to the released version and the Next section to `Unreleased`, adds an empty Next section for the milestone after, bumps `BrutalEngineReleaseMilestone` and `MinVerMinimumMajorMinor`, and fixes the compare links. Until that PR merges, next-milestone work doesn't change the packages or samples being released, an existing version in `Directory.Packages.props`, or the build-wide files `Directory.Build.props`, `src/Directory.Build.props`, `src/Directory.Build.targets` and `global.json`. It adds a version to `Directory.Packages.props` only for a package that no releasing library or sample resolves, directly or transitively, because transitive pinning (`Directory.Packages.props:5`) would make it a direct dependency of a released package; no releasing project's `packages.lock.json` may change ([ADR-0016](adr/0016-trunk-based-milestones.md)).
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

The asset cooker runs as part of publish (incremental). The Windows and Linux output is smoke-tested in CI on that platform for the samples. The macOS output is best-effort and unverified until macOS CI returns ([ADR-0015](adr/0015-tested-platforms-windows-linux.md)).

## Code signing

Pipelines are designed for signing from the start but run **unsigned** until certificates exist:

- **macOS:** Developer ID Application certificate + notarisation. Secrets live in a protected environment, and signing steps are skipped with a visible warning when they're absent. Unsigned macOS builds trigger Gatekeeper warnings, and the docs explain the workaround for testers.
- **Windows:** Authenticode (e.g. Azure Trusted Signing or an OV/EV certificate). Same skip-with-warning behaviour.
- NuGet packages are signed by nuget.org's repository signature. Author signing can be added later.

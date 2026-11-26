# 8. Development workflow

## Roles

- **Maintainer** (Harry Stanley): owns `main`, reviews and merges every PR, makes the final call on ADRs, holds signing credentials and release approval.
- **Implementer** (the AI assistant, and later other contributors): writes code on branches, opens PRs, and never pushes to or merges into `main`.

## Flow for every change

1. **Issue first.** Every piece of work has a GitHub issue. Milestone issues are created from the [Roadmap](09-roadmap.md) at the start of each milestone, using the milestone and area labels (`area:graphics`, `area:audio` and so on).
2. **Design before code for new subsystems or public API.** The PR's first commit adds or updates `docs/design/<subsystem>.md`, or an ADR when a decision is being made or changed. For larger subsystems the design doc can go in its own PR, merged before implementation starts.
3. **Branch** from up-to-date `main`: `<type>/<area>-<short-description>`, e.g. `feat/graphics-swapchain`, `fix/audio-device-switch`, `docs/initial-plan`. One feature per branch.
4. **Implement with tests in the same PR**, as a series of small, focused commits (see [Coding standards: Commits](07-coding-standards.md#commits)). Each commit builds and passes the tests on its own. Bug fixes start with the failing test.
5. **Local verification before pushing, for every commit:** `dotnet build` (zero warnings), `dotnet test` (all green), `dotnet format --verify-no-changes`. Run the GPU smoke (`dotnet run --project tests/BrutalEngine.Aot.Smoke -c Release -- vulkan-offscreen`) when the change touches graphics or native code. CI confirms local results; it isn't the place to find them.
   - Push a branch once it's ready for review, and let the PR's own CI run. Don't dispatch extra CI runs per commit. A manual `workflow_dispatch` is for debugging workflow changes, or a failure that can't be reproduced locally.
   - Wait for CI with `gh pr checks <n> --watch` or `gh run watch <id>`, not fixed sleeps.
6. **Open a PR** using the template: what changed, why, how it's tested, public API changes (diff of `PublicAPI.Unshipped.txt`), performance impact, platforms verified, follow-ups. Link the issue.
7. **CI green on Windows and Linux** (macOS isn't in CI, [ADR-0015](adr/0015-tested-platforms-windows-linux.md)). No "fix CI later" merges. PRs get the fast or full tier depending on what they touch ([CI tiers](06-ci-cd-and-release.md#ci-tiers)); the full tier runs again on the merge to `main`.
8. **Review and merge.** The maintainer reviews, and changes are requested and addressed in follow-up commits on the same branch. Then the PR is merged with **squash and merge, always**: one commit on `main` per PR, titled with the PR's conventional-commit title. Merge commits and rebase merges are disabled in the repository settings. The branch is deleted after merge, and local branches are reset to the new `origin/main` (never merged locally).

PR size: aim for under ~800 changed lines excluding generated files and test data. Because PRs are squash-merged, one PR becomes one commit on `main`, so a small PR is what makes a change easy to revert on `main`. Small commits within the PR make it easy to review. Larger features are split into stacked PRs (infrastructure → API → implementation → sample).

## Definition of done

A PR is done when all of these are true:

- [ ] Behaviour matches the design doc or ADR (or the doc is updated in this PR)
- [ ] Unit/integration/GPU tests cover the change at the right layer, including failure paths
- [ ] Zero build warnings, analyzers clean, formatting clean
- [ ] CI green on Windows and Linux
- [ ] No managed allocations added to steady-state frames (allocation tests pass)
- [ ] No leaks (tracking allocators and GPU tracker clean)
- [ ] Every native status code checked
- [ ] Public API: intentional, XML-documented, recorded in `PublicAPI.Unshipped.txt`
- [ ] `CHANGELOG.md` updated: the `Unreleased` section for the next release, or the Next section below it for the milestone after ([ADR-0016](adr/0016-trunk-based-milestones.md))
- [ ] Docs and samples updated if user-facing behaviour changed

## Definition of done for a milestone

- All milestone issues closed or explicitly moved, with a reason.
- The milestone's sample game builds with NativeAOT, is published for win-x64 and linux-x64 by `release.yml`, and passes its scripted playthrough on Windows and Linux: on software Vulkan in CI, and on a real GPU on the reference machine until [self-hosted runners](06-ci-cd-and-release.md#self-hosted-runners) exist. macOS is best-effort and unverified ([ADR-0015](adr/0015-tested-platforms-windows-linux.md)).
- Benchmarks meet the milestone's budgets.
- Manual QA checklist completed on real hardware.
- Release tagged (`v0.N.0`), changelog finalised, docs site updated.

## ADR process

- Any decision that is expensive to reverse (dependency, format, public API shape, threading model, platform support) gets an ADR in `docs/adr/NNNN-title.md`, using the [template](adr/0000-template.md).
- Status flow: `Proposed` → `Accepted` (maintainer approval in the PR) → optionally `Superseded by ADR-NNNN`. Accepted ADRs are never edited except for status and links. Changes need a new ADR.

## Working agreements for the AI implementer

- Inspect before asserting: every BRUTAL API used is confirmed in BRUTAL source or a sample and cited (`path:line`) in the PR description. No invented APIs.
- Report honestly: PR descriptions say what was verified, on which OS, and what wasn't. Unverified platforms are stated, never implied to be passing.
- When a blocker appears (missing binding, upstream bug, runner limitation), stop and raise it on the issue with options, rather than working around it silently.

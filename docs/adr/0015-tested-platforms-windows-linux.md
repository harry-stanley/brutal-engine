# ADR-0015: Windows and Linux are the tested targets; macOS is best-effort and leaves CI

- **Status:** Accepted
- **Date:** 2026-11-26
- **Deciders:** the maintainer

## Context

[Vision and scope](../01-vision-and-scope.md#goals) made Windows, Linux and macOS equal targets, with a feature done only when it passes CI on all three. Since then:

- **There is no Mac.** The only real hardware is the reference machine, an RTX 2070 SUPER on Windows 11 and on a bare-metal Arch Linux install ([Roadmap, open questions](../09-roadmap.md#open-questions-to-resolve-during-milestones): the macOS hardware question is still open). GitHub's `macos-15` runner can show that macOS is broken, but nobody on the project can reproduce or debug a macOS-only failure, and the runner's paravirtual Metal device says nothing about real GPU families, windowed presentation or performance.
- **BRUTAL itself supports only Windows and Linux** ([Vision and scope, supported platforms](../01-vision-and-scope.md#supported-platforms-v1)). On macOS the engine bundles and preloads the Vulkan loader and MoltenVK itself ([Tech stack](../02-tech-stack.md#not-bundled-by-brutal-we-ship-these-ourselves), [Risks R1](../10-risks.md)).
- **M0 did verify macOS on the runner.** HelloWindow and the AOT smoke harness ran JIT and NativeAOT on `macos-15` with the bundled loader and MoltenVK, and the `v0.1.0-alpha` pre-release carries a `hello-window-osx-arm64` build ([Roadmap, M0](../09-roadmap.md#m0-foundations-and-proof-of-platform-01)). That remains true for M0.
- **What macOS costs in CI** at `ac5266d` (`.github/workflows/ci.yml`): a `macos-15` build-and-test job in every fast and full tier, and a `macos-15` Vulkan smoke job in the full and smoke-delta tiers, with its own setup step (`.github/actions/setup-macos-vulkan`) and macOS-only bundle and check steps. On a warm cache the smoke job takes 1-1.5 minutes (72 s in PR run 45664452100, 91 s in PR run 45654756817), and the build-and-test job under a minute. A cache miss rebuilds the validation layers from source, 6-13 minutes ([CI/CD, caching](../06-ci-cd-and-release.md#caching)). Every macOS job is part of `CI passed`, so a macOS-only failure blocks the merge.
- **The required checks name no macOS job.** They are `CI passed`, `CodeQL (csharp)`, `CodeQL (actions)` and `Dependency review` ([CI/CD, workflows](../06-ci-cd-and-release.md#ciyml-every-pr-and-every-push-to-main-plus-nightly)), so removing the macOS jobs needs no repository settings change.

## Options considered

1. **Drop macOS from CI entirely, keep the code.** Windows and Linux are the tested targets. The `osx-arm64` RID, the bundled-loader and MoltenVK code paths, the pinned natives and the setup action stay, unverified. Pros: CI only fails for something someone can debug; the full tier gets one runner cheaper. Cons: macOS regressions go unnoticed until macOS CI returns.
2. **Keep a non-blocking, build-only macOS job.** Pros: catches a macOS compile break. Cons: the build is the same managed code on every OS, so it catches little; a red job nobody can act on gets ignored, and it still uses a runner on every PR.
3. **Keep the macOS jobs as they are, required through `CI passed`.** Pros: the strongest macOS signal short of a Mac. Cons: a macOS-only failure blocks merges, and it can't be debugged without a Mac.

## Decision

Decision (maintainer, 2026-11-26): option 1. `win-x64` and `linux-x64` are the tested targets from M1 on. `osx-arm64` is best-effort: its code and RID are kept, but nothing is built, run or released for it in CI, and no document claims it is tested. Options 2 and 3 are rejected.

## Consequences

- **CI** (`.github/workflows/ci.yml`): build and test run on `windows-2025` and `ubuntu-24.04`. The Vulkan smoke runs on Linux in the fast tier, on Linux and Windows in the full tier, and on Windows only in the smoke-delta tier (the merge check's `SMOKE_FULL_OS` is Windows alone). There is no `hello-window-osx-arm64` artifact and no macOS release archive.
- **Kept, unverified on macOS:** the `osx-arm64` RID in `src/Directory.Build.props`; `natives/macos-vulkan/` and its pins; `.github/actions/setup-macos-vulkan/` (unused by CI); HelloWindow's bundled-loader path; the Vulkan backend's duties on macOS (preload the bundled loader by absolute path, set `VK_DRIVER_FILES`, the portability extensions), which the graphics backend still ports; the macOS paths in the design notes; and the plan for a MoltenVK natives package in M2. Each is marked as unverified on macOS wherever the docs describe it.
- **Regressions:** a change that breaks macOS goes unnoticed. Issues don't put macOS into acceptance criteria, and PRs don't claim macOS verification.
- **Docs:** every remaining macOS statement is M0 history, a description of kept code marked unverified, or this ADR.
- **Restoring macOS CI:** re-add the `macos` matrix entry, the `macos-15` build-and-test entry, the setup step and the macOS-only bundle and check steps from `.github/workflows/ci.yml` as it was in the commit before this change, add `macos-15` back to `SMOKE_FULL_OS` and to the merge check's self-test fixtures (`.github/scripts/ci-merge-check-test.sh`), and use the kept `setup-macos-vulkan` action. The first run after that rebuilds the validation layers (6-13 minutes).
- **Revisit when** a Mac becomes available, or before any release that claims macOS support, whichever comes first; the roadmap's macOS hardware question is due by the end of M2.
- **Amends:**
  - [ADR-0002](0002-render-interface-vulkan-first.md): we still own the macOS native build, but its tests no longer run in CI.
  - [ADR-0009](0009-github-actions-ci.md): CI uses hosted Windows and Linux runners only; macOS-GPU testing waits for a Mac.
  - [ADR-0011](0011-brutal-objectpool-dependency.md): the ObjectPool pin is proven under NativeAOT on `win-x64` and `linux-x64` in CI; `osx-arm64` stays unverified.
  - Their status lines link here. The docs updated with this ADR are [Vision and scope](../01-vision-and-scope.md), [Tech stack](../02-tech-stack.md), [Architecture](../03-architecture.md) (the two lines that said macOS is tested), [Testing strategy](../05-testing-strategy.md), [CI/CD and release](../06-ci-cd-and-release.md), [Roadmap](../09-roadmap.md), [Risks](../10-risks.md), the [docs index](../README.md), the repository README and CONTRIBUTING.md.
  - Not amended: ADR-0003, ADR-0004 and ADR-0012 mention three OSes only as context or to describe the build jobs.

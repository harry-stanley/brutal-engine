<!--
Title: a conventional commit, e.g. "feat(graphics): add swapchain recreation". It becomes the squashed commit on main.
Branch: <type>/<area>-<short-description>. See CONTRIBUTING.md.
-->

Closes #<!-- issue number -->

## What

<!-- What this PR changes. -->

## Why

<!-- The problem it solves. Link the design doc or ADR if there is one. -->

## How tested

<!-- Tests added or changed, and what you ran locally (dotnet build, dotnet test, dotnet format --verify-no-changes, GPU tests). -->

## Public API changes

<!-- Paste the diff of PublicAPI.Unshipped.txt, or write "None". -->

## Performance impact

<!-- Benchmarks, allocation tests, frame-time numbers, or "None expected" with a reason. -->

## Platforms verified

<!-- Tick only platforms you actually built and tested on. Say which GPU/driver for graphics changes. CI covers Windows and Linux; macOS is best-effort, with no CI (ADR-0015). -->

- [ ] Windows
- [ ] Linux
- [ ] macOS

## Follow-ups

<!-- Work left for later, with issue links, or "None". -->

## Definition of done

- [ ] Behaviour matches the design doc or ADR (or the doc is updated in this PR)
- [ ] Unit/integration/GPU tests cover the change at the right layer, including failure paths
- [ ] Zero build warnings, analyzers clean, formatting clean
- [ ] CI green on Windows and Linux
- [ ] No managed allocations added to steady-state frames (allocation tests pass)
- [ ] No leaks (tracking allocators and GPU tracker clean)
- [ ] Every native status code checked
- [ ] Public API: intentional, XML-documented, recorded in `PublicAPI.Unshipped.txt`
- [ ] `CHANGELOG.md` updated: the `Unreleased` section for the next release, or the Next section below it for the milestone after (ADR-0016)
- [ ] Docs and samples updated if user-facing behaviour changed

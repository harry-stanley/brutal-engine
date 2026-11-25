# BrutalEngine documentation

BrutalEngine is an open-source, production-grade 2D game engine for Windows and Linux (macOS is best-effort and unverified, [ADR-0015](adr/0015-tested-platforms-windows-linux.md)), built on the [BRUTAL framework](https://github.com/ahwootech/BRUTAL) and .NET 11.

These documents are the engineering plan. They are the reference for every pull request. If code and plan disagree, either the code is wrong or the plan must be updated in the same PR, with an ADR if a decision changes.

## Reading order

| # | Document | What it settles |
|---|----------|-----------------|
| 1 | [Vision and scope](01-vision-and-scope.md) | Goals, non-goals, quality bar, supported platforms |
| 2 | [Tech stack](02-tech-stack.md) | Every runtime and tooling dependency, with versions and reasons |
| 3 | [Architecture](03-architecture.md) | Layers, modules, main loop, threading, memory, errors, each subsystem |
| 4 | [Repository layout](04-repo-layout.md) | Folders, projects, packages, build configuration |
| 5 | [Testing strategy](05-testing-strategy.md) | Test layers, GPU testing, gates, what "fully tested" means |
| 6 | [CI/CD and release](06-ci-cd-and-release.md) | Workflows, runners, packaging, signing, versioning |
| 7 | [Coding standards](07-coding-standards.md) | C# rules, API design, native interop rules, docs |
| 8 | [Development workflow](08-workflow.md) | Branches, PRs, reviews, definition of done |
| 9 | [Roadmap](09-roadmap.md) | Milestones M0 to M6 with exit criteria |
| 10 | [Risks](10-risks.md) | Known risks, mitigations and the spikes that retire them |
| - | [Architecture decision records](adr/README.md) | One file per decision, with its context and consequences |
| - | [Spike results](spikes/README.md) | What the M0 spikes proved, with links to their code |

## Status

Planning complete, M0 next. Nothing is implemented yet. All ADRs are accepted.

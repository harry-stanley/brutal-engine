# Contributing to BrutalEngine

Thanks for your interest in BrutalEngine. Issues and pull requests are welcome. The project is maintained by one person ([@harry-stanley](https://github.com/harry-stanley)), so please be patient with reviews.

The engine is in its planning stage: the engineering plan in [docs/](docs/README.md) is written, and code starts with milestone M0 (see the [roadmap](docs/09-roadmap.md)). This guide summarises how to contribute. The full rules live in [Coding standards](docs/07-coding-standards.md) and [Development workflow](docs/08-workflow.md). Where this guide and those documents disagree, the documents win.

## The short version

- **Bugs:** open an issue with the bug report form, including a minimal repro.
- **Features and anything bigger than a small fix:** open an issue first and wait for the maintainer to agree the approach before you write code.
- **Security problems:** report them privately. See [SECURITY.md](SECURITY.md).
- **Behaviour:** everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Prerequisites

- **.NET 11 SDK**, 11.0.100 or a later `11.0.1xx` patch. The repository pins it with `global.json` ([ADR-0014](docs/adr/0014-dotnet-11.md)).
- **Vulkan SDK**, for the validation layers used in development and GPU tests.
- **Git LFS**, only if you work on golden-image tests or samples. Library-only contributors can build and run the unit tests without LFS content.
- Windows (x64) or Linux (x64), the tested platforms. macOS (arm64) is best-effort and unverified: CI doesn't build or test it ([ADR-0015](docs/adr/0015-tested-platforms-windows-linux.md)).

See [Tech stack](docs/02-tech-stack.md) for versions and the full dependency list.

## Building and testing

The build scaffold (solution, `global.json`, shared MSBuild settings) arrives in M0. Once it's present, run these from the repository root:

```
dotnet build
dotnet test
dotnet format --verify-no-changes
```

The build must finish with **zero warnings** (warnings are errors), and every test must pass. If your change touches graphics, also run the GPU tests. The [Testing strategy](docs/05-testing-strategy.md) describes the test layers and how GPU tests run without a GPU.

## Workflow

1. **Issue first.** Every piece of work has a GitHub issue. Tiny fixes (typos, broken links) can skip this.
2. **Design before code** for new subsystems or public API: add or update `docs/design/<subsystem>.md`, or write an ADR when a decision is being made or changed (see [Design docs and ADRs](#design-docs-and-adrs)).
3. **Branch** from up-to-date `main`, named `<type>/<area>-<short-description>`, for example `feat/graphics-swapchain`, `fix/audio-device-switch` or `docs/initial-plan`. One feature per branch.
4. **Implement with tests in the same PR.** Bug fixes start with a failing test.
5. **Verify locally** with the commands above before pushing.
6. **Open a pull request** using the template: what changed, why, how it's tested, public API changes, performance impact, platforms verified and follow-ups. Link the issue. Aim for under about 800 changed lines, excluding generated files and test data, and split larger features into stacked PRs.
7. **CI must be green on Windows and Linux** before merge. macOS isn't in CI.
8. **Review and merge.** Address review comments with follow-up commits on the same branch. The maintainer merges every PR with **squash and merge**, so the PR title becomes the commit on `main`.

A PR is ready to merge when it meets the [definition of done](docs/08-workflow.md#definition-of-done). The PR template includes the checklist.

## Commits

- Use [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/): `feat(render): …`, `fix(audio): …`, `test: …`, `docs: …`, `ci: …`, `refactor: …`, `perf: …`, `build: …`, `chore: …`, with the module name as the scope. Mark breaking changes with a `BREAKING CHANGE:` footer.
- **One logical change per commit.** A commit should make sense on its own and be easy to review and revert. Don't put everything in one big commit, and don't split so finely that a commit no longer makes sense alone (a type and its tests land together).
- **Every commit leaves the repository working:** it builds with zero warnings and all tests pass.
- The PR title must be a valid conventional commit too, because it becomes the squashed commit on `main`.
- Add your change to the `Unreleased` section of [CHANGELOG.md](CHANGELOG.md), or to the Next section below it if it's for the milestone after the next release ([ADR-0016](docs/adr/0016-trunk-based-milestones.md)).

See [Coding standards: Commits](docs/07-coding-standards.md#commits) for the full rules.

## Code

The [Coding standards](docs/07-coding-standards.md) cover C# style, public API design, native interop and the analyzers we own. In particular:

- Public API is deliberate: every public member is XML-documented and recorded in `PublicAPI.Unshipped.txt`.
- No managed allocations in steady-state frames and hot paths.
- Every native status code is checked.
- Don't edit BRUTAL. If a binding is missing or wrong, report it upstream at [ahwootech/BRUTAL](https://github.com/ahwootech/BRUTAL).

### AI-assisted contributions

You may use AI tools, but you are responsible for every line you submit: you must understand it, have tested it, and be able to explain it in review. Say in the PR description if a substantial part was AI-generated, and state honestly which platforms you verified.

## Design docs and ADRs

- The engineering plan is in [docs/](docs/README.md). Read the relevant documents before working on a subsystem.
- Each subsystem gets a short design note in `docs/design/<subsystem>.md`, written in the PR that introduces it.
- Any decision that's expensive to reverse (a dependency, a format, public API shape, the threading model, platform support) needs an Architecture Decision Record in [docs/adr/](docs/adr/README.md), using the [template](docs/adr/0000-template.md). ADRs go from `Proposed` to `Accepted` when the maintainer approves the PR. Accepted ADRs are never edited except for status and links, so a change of decision needs a new ADR. See the [ADR process](docs/08-workflow.md#adr-process).

## Licensing of contributions

BrutalEngine is licensed under the [MIT licence](LICENSE) ([ADR-0008](docs/adr/0008-licence.md)). Contributions are accepted on an **inbound = outbound** basis: by submitting a contribution, you agree that it is licensed under the same MIT licence as the rest of the project, and you confirm that you have the right to submit it under that licence.

Don't add third-party code or new dependencies without discussing them in an issue first, so their licences can be reviewed.

### BRUTAL licence

BrutalEngine is built on the [BRUTAL framework](https://github.com/ahwootech/BRUTAL) by Ahwoo Limited, which is used under the BRUTAL License v1.0, not MIT. Contributions to this repository don't change BRUTAL's terms. Every game built with BrutalEngine must meet BRUTAL's obligations: the BRUTAL copyright notice in the credits, the BRUTAL logo shown before gameplay, and [NOTICE.txt](NOTICE.txt) distributed with the game. The engine does all three by default, and changes must keep it that way. See [Vision and scope: Licensing obligations](docs/01-vision-and-scope.md#licensing-obligations).

Contributions to BRUTAL itself go to the [BRUTAL repository](https://github.com/ahwootech/BRUTAL) and follow its own contributing guide and licence.

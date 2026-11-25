# Changelog

All notable changes to BrutalEngine are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until 1.0.0, versions are `0.y.z` and breaking changes can happen between minor releases. They are always listed under "Breaking", with migration notes. See [CI/CD and release: Versioning](docs/06-ci-cd-and-release.md#versioning).

Every pull request adds its user-visible changes to the `Unreleased` section.

## [Unreleased]

### Added

- Engineering plan in `docs/`: vision and scope, tech stack, architecture, repository layout, testing strategy, CI/CD and release, coding standards, development workflow, roadmap and risks.
- Architecture decision records ADR-0001 to ADR-0010 in `docs/adr/`, with an ADR template. ADR-0006 (archetype ECS world model) is accepted.
- Commit rules: Conventional Commits, small focused commits that each build and pass tests, and squash-merge-only PRs.
- Test hardware and self-hosted runner plan for real-GPU testing.
- README, MIT licence and BRUTAL's `NOTICE.txt`.
- Community health files: contributing guide, security policy, code of conduct (Contributor Covenant 2.1), this changelog, pull request and issue templates, and CODEOWNERS.

[Unreleased]: https://github.com/harry-stanley/brutal-engine/commits/main

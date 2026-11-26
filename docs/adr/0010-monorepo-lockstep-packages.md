# ADR-0010: Monorepo with lockstep-versioned NuGet packages

- **Status:** Accepted; amended by [ADR-0016](0016-trunk-based-milestones.md) (each release ships the packages its milestone gate selects; versioning stays lockstep)
- **Date:** 2026-11-25
- **Deciders:** Harry Stanley (maintainer)

## Context

The engine is many modules (core, platform, graphics, audio, world, rendering and so on) shipped as NuGet packages. Users need predictable compatibility between them.

## Options considered

1. **Monorepo, lockstep versions:** one version number for every package, one release. Simple for users and CI. Some packages get version bumps with no changes.
2. **Monorepo, independent versions:** precise semver per package. A compatibility matrix for users, and complex release tooling.
3. **Multi-repo:** isolation. Cross-cutting changes become multi-PR, multi-release operations.

## Decision

A single repository with all packages versioned and released in lockstep, plus a `BrutalEngine` meta-package for the default set.

## Consequences

- Every release ships all packages. Package validation runs for every package against the previous release.

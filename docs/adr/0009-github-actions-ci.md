# ADR-0009: GitHub as source of truth, GitHub Actions CI

- **Status:** Accepted; amended by [ADR-0015](0015-tested-platforms-windows-linux.md) (CI runs on Windows and Linux only)
- **Date:** 2026-11-25
- **Deciders:** Harry Stanley (maintainer)

## Context

The repository started on GitLab. CI needs Windows, Linux and macOS (Apple silicon) runners. GitHub provides hosted runners on all three for public repositories at no cost. The maintainer created `github.com/harry-stanley/brutal-engine` as the canonical repository.

## Options considered

1. **GitHub + Actions:** free 3-OS runners for public repos, a large OSS ecosystem, and nuget.org trusted publishing via OIDC.
2. **GitLab SaaS + self-hosted runners:** keeps the existing remote, but macOS/Windows need paid or self-hosted runners.

## Decision

GitHub is the source of truth and GitHub Actions is the CI system. GitLab is no longer used.

## Consequences

- Real-GPU and macOS-GPU testing use self-hosted runners with the public-repo safeguards in [CI/CD](../06-ci-cd-and-release.md#self-hosted-runners).
- Branch protection and required checks are configured on GitHub at the start of M0.

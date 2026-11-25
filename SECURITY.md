# Security policy

## Reporting a vulnerability

**Please do not report security issues in public issues, pull requests or discussions.**

Report them privately through GitHub's private vulnerability reporting: open the repository's **Security** tab and choose **Report a vulnerability**. Only the maintainer can see the report.

If that option isn't available to you, open a public issue that asks the maintainer ([@harry-stanley](https://github.com/harry-stanley)) for a private contact. Don't include any details of the vulnerability in it.

A useful report includes:

- the affected engine packages and versions (or the commit SHA if you built from source)
- OS, .NET SDK version and, for graphics issues, GPU and driver version
- a description of the issue and its impact
- steps or code to reproduce it

## What to expect

BrutalEngine is maintained by one person, so these are goals rather than guarantees:

- We aim to acknowledge your report within **7 days**.
- We'll keep you updated while we investigate, and agree a disclosure timeline with you before anything is made public.
- Fixes are released as a normal engine release, with a GitHub security advisory describing the issue.
- We're happy to credit you in the advisory unless you'd prefer not to be named.

## Supported versions

The engine is pre-1.0 (`0.x`, see [CI/CD and release: Versioning](docs/06-ci-cd-and-release.md#versioning)). Until 1.0.0, security fixes go into the **latest `0.x` minor release** only. Older minors don't receive fixes, so please update to the latest release before reporting if you can. The support policy for 1.0 and later will be published here with 1.0.0.

| Version | Supported |
|---------|-----------|
| Latest `0.x` minor | Yes |
| Older `0.x` minors | No |

No version has been released yet.

## Scope

In scope: the engine source in this repository and the `BrutalEngine.*` packages built from it, including the tools (such as the asset cooker), the build and publish targets, and the CI and release workflows.

Out of scope:

- **The BRUTAL framework.** Report vulnerabilities in BRUTAL itself to Ahwoo Limited, as described in [BRUTAL's security policy](https://github.com/ahwootech/BRUTAL/blob/main/SECURITY.md). If a BRUTAL issue affects BrutalEngine users, you're welcome to tell us as well so we can track it.
- **Other third-party libraries** we use or redistribute (SDL3, the Vulkan loader, MoltenVK, Box2D and so on). Report those to their upstream projects.
- **Games built with BrutalEngine.** Contact that game's developer.

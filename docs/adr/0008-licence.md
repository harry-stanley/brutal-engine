# ADR-0008: MIT licence for the engine

- **Status:** Accepted
- **Date:** 2026-11-25
- **Deciders:** Harry Stanley (maintainer)

## Context

The engine is public and open source, and should be easy to adopt commercially. BRUTAL itself uses the MIT-based BRUTAL License v1.0, which adds obligations for products built with it (credits, logo splash, NOTICE.txt).

## Options considered

1. **MIT:** maximally permissive, matches BRUTAL's base, simplest for users.
2. **Apache-2.0:** explicit patent grant. Longer notice requirements, and differs from BRUTAL's base.
3. **MPL-2.0:** file-level copyleft, which keeps engine improvements open, but adds friction for commercial users.

## Decision

MIT, copyright Harry Stanley and BrutalEngine contributors.

## Consequences

- The repository's LICENSE is MIT. BRUTAL's NOTICE.txt stays in the repo and is redistributed with every product.
- The engine's own licence adds nothing beyond MIT. BRUTAL's obligations still apply to every game and are made the default (see [Architecture 3.23](../03-architecture.md#323-brutal-licence-compliance-features)).
- Contributions are accepted under MIT (inbound = outbound), stated in CONTRIBUTING.md.

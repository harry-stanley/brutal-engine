# ADR-0001: Target .NET 10 LTS

- **Status:** Superseded by [ADR-0014](0014-dotnet-11.md)
- **Date:** 2026-11-25
- **Deciders:** Harry Stanley (maintainer)

## Context

BRUTAL targets .NET 10 (its README lists the .NET 10 SDK as a prerequisite, and its sources reference net10 APIs explicitly). .NET 10 is an LTS release supported until November 2028. .NET 11 is an STS release with a shorter support window. A public engine needs a stable runtime baseline that its users can also adopt.

## Options considered

1. **.NET 10 LTS:** matches BRUTAL, longest support, what studios standardise on. Misses .NET 11 features.
2. **.NET 11 STS:** newest features. Shorter support, and games built on it must upgrade sooner. It would diverge from BRUTAL's tested target.

## Decision

Target `net10.0` only, with the SDK pinned in `global.json` to `10.0.1xx` (`rollForward: latestFeature`).

## Consequences

- Contributors must install the .NET 10 SDK (the maintainer's machine currently has only SDK 8 and 11).
- Re-evaluate when .NET 12 LTS ships (November 2027). Moving to it is a minor-version change if the API is unaffected, otherwise a major one.

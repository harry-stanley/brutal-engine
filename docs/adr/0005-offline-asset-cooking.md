# ADR-0005: Offline asset cooking with dev-mode hot reload

- **Status:** Accepted
- **Date:** 2026-11-25
- **Deciders:** Harry Stanley (maintainer)

## Context

Shipped games need fast, validated, compact asset loading with no importer code (image decoders, shader compilers) in the release build. Developers need fast iteration.

## Options considered

1. **Offline cooking + dev-mode raw loading with hot reload:** best of both. Two code paths to keep consistent.
2. **Raw files at runtime:** simple. Slow loads, larger builds, importers shipped, unvalidated input in production.

## Decision

A CLI cooker (`brutal-cook`) produces versioned binary packs. Development builds load source assets through the same importers in-process and hot-reload changes. Asset identity is a stable GUID stored in `.meta` sidecar files.

## Consequences

- Importers live in a shared library used by both the cooker and the dev runtime, so both paths share code by construction.
- Integration tests cover cook→load round trips. Cooked output must be deterministic. Pack readers are fuzzed.

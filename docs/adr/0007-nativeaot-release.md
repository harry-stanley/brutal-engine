# ADR-0007: NativeAOT for release builds, JIT for development

- **Status:** Accepted
- **Date:** 2026-11-25
- **Deciders:** Harry Stanley (maintainer)

## Context

Shipped games benefit from fast startup, no JIT warm-up stutter, a single native executable and no installed runtime. BRUTAL uses source-generated `LibraryImport` and function pointers, which are NativeAOT-friendly. Development needs JIT for debugging and future hot reload.

## Options considered

1. **NativeAOT release + JIT dev:** best shipping experience. Forbids runtime reflection and code generation in the engine.
2. **Self-contained JIT publish:** simplest, reflection works. Slower startup, larger output, possible JIT hitches.
3. **Both equally supported:** doubles the test matrix.

## Decision

NativeAOT is the default release output. JIT is used in development. Self-contained JIT publishing remains possible for games that need reflection, but the engine is tested and supported primarily under NativeAOT.

## Consequences

- All engine assemblies are `IsAotCompatible`, and trim/AOT warnings are errors.
- Serialisation, queries and UI binding use source generators.
- Spike S2 confirms BRUTAL packages publish cleanly (risk R4). AOT smoke tests run in CI for every RID.

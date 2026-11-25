# Design: architecture tests (`tests/BrutalEngine.Architecture.Tests`)

## Purpose

Turns the structural rules of the plan into failing tests, so they hold without relying on review:

- the layering in [Architecture 3.1](../03-architecture.md#31-layering),
- "no backend type leaks upward" (3.1),
- hot-reload readiness: no static mutable state in engine assemblies ([Architecture 3.21](../03-architecture.md#321-hot-reload-readiness)).

These are the rules listed under [Testing strategy: architecture tests](../05-testing-strategy.md#architecture-tests). The fourth rule listed there, that every public type and member is in `PublicAPI.*.txt` and has XML docs, is already enforced by the build on every engine library: RS0016/RS0017 (PublicApiAnalyzers) and CS1591 (`GenerateDocumentationFile`) are errors under `TreatWarningsAsErrors`. The architecture tests don't duplicate it.

## Key types

| Type | Role |
|------|------|
| `EngineLayers` | The single, declarative layer map: every module's layer and kind, plus the same-layer edges that are allowed explicitly |
| `LayerMap` | Lookup over the map, and `Validate()`, which checks the map itself (layers in range, backends in L2 with exactly one abstraction, tools in L6, same-layer edges within one layer and acyclic) |
| `Repository` | Finds the repository by walking up from `AppContext.BaseDirectory` to the folder holding `BrutalEngine.slnx`, and lists the engine projects |
| `EngineModules` | Loads every engine library (each non-Roslyn `src/*/*.csproj`) with its layer map entry |
| `LayeringRule`, `BindingSurfaceRule`, `StaticStateRule` | The rules, as pure functions over names or `Type`s that return every violation as a readable line |

The tests use reflection over the assemblies the test project references. Reflection is fine here: the test project is never AOT-compiled. No third-party architecture-testing package is used.

## The layer map

Every module planned in [Repository layout](../04-repo-layout.md) has an entry, so a new project only has to match its planned entry. A test parses the `src/` and `tools/` folders of the layout tree in docs/04 and fails if the map and the doc disagree in either direction.

| Layer | Modules | Kind |
|-------|---------|------|
| L1 | Core | library |
| L2 | Platform, Graphics, Audio | library (abstractions) |
| L2 | Platform.Sdl3, Graphics.Vulkan, Audio.Sdl3 | backend |
| L3 | World, Rendering2D, Text, Input, Assets | library |
| L4 | Tilemaps, Physics2D, Animation, UI, Particles, Networking | library |
| L4 | DebugOverlay | host |
| L5 | Runtime, `BrutalEngine` (meta-package) | host |
| L6 | Cook, Importers, Editor (`tools/`) | tool |

Kinds: a **backend** may expose binding types. A **host** may reference backends, because backends are chosen in the runtime host (3.1). A **tool** may use any layer, and runtime modules never reference tools.

Allowed same-layer edges, each with the doc that justifies it:

| Edge | Reason |
|------|--------|
| Platform.Sdl3 → Platform, Graphics.Vulkan → Graphics, Audio.Sdl3 → Audio | a backend implements its abstraction |
| Rendering2D → Text | Rendering2D draws text (3.1 diagram, 3.9) |
| Tilemaps → Physics2D | tilemaps generate collision shapes into Physics2D (3.14) |
| Particles → Physics2D | particles collide against Physics2D (3.18) |
| `BrutalEngine` → Runtime | the meta-package references the host (docs/04) |
| Cook → Importers | the cooker is built on the shared importer library (docs/04) |

Judgement calls:

- **Audio is L2.** The 3.1 diagram shows an audio device at L2 and the mixer at L3, but docs/04 and 3.11 plan one `BrutalEngine.Audio` package (device abstraction plus our mixer) with the `Audio.Sdl3` backend, and 3.1 names it as an abstraction L3 sees. A backend can't depend upwards on its abstraction, so the package is L2.
- **Text is L3**, beside Rendering2D, which the 3.1 diagram describes as "sprites, cameras, text".
- **DebugOverlay is an L4 host.** It reads L3 services and L2 diagnostics (3.20) and is installed by the L5 runtime in development builds. docs/04 gives it the SDL3 platform backend for ImGui, so it may reference backends. It may not expose ImGui types publicly: it isn't a backend. If it ever needs runtime types, it moves to L5 with a listed DebugOverlay → Runtime edge.
- **The meta-package is an L5 host** with a listed edge to Runtime, rather than a seventh layer.
- Same-layer edges the docs don't state yet (for example Rendering2D → Assets or World) are not pre-allowed. The PR that adds such a dependency lists the edge in `EngineLayers`, with the doc section that justifies it, or moves the shared code down a layer.

## Rules

**Coverage (`ModuleCoverageTests`, `LayerMapTests`).** Every `src/*/*.csproj` except Roslyn components (`*.Analyzers`, `*.Generators`, recognised by name exactly as `src/Directory.Build.props` does) must be referenced by the test project and be in the layer map, so a new module can't silently escape the rules. A `BrutalEngine` or `BrutalEngine.*` name that isn't in the map fails these tests and the layering rule.

**Layering (`LayeringRule`).** For every engine library, each referenced `BrutalEngine.*` assembly must be in a strictly lower layer, or in the same layer through a listed edge. Backends may be referenced only by hosts and tools, and tools only by tools. The rule checks both the compiled assembly references and the `ProjectReference`s in the project file, because the compiler drops a reference that no code uses but the NuGet package dependency remains. Roslyn components are build-time only and are ignored.

**No binding types in public surfaces (`BindingSurfaceRule`).** In every engine library that isn't a backend (so including Core and the L2 abstractions), no public surface exposes a type from a binding namespace. The public surface is every public type, and every protected nested type of an unsealed public type, with:

- its base type, interfaces and generic constraints,
- the types in its public and protected fields, properties, indexers, events, constructors, methods and operators,
- including types reached through arrays, pointers, by-ref parameters, function pointers and generic arguments.

Protected members of sealed types and `private protected` members don't count. Binding namespaces are `Brutal.<Name>Api` and its sub-namespaces. A search for `namespace Brutal.` across the BRUTAL sources found Box2DApi, FmodApi, GlfwApi, GliApi, GltfApi, ImGuiApi, ImPlotApi, JoltApi, KtxApi, NativeFileDialogApi, NotCursesApi, OpenAlApi, OpenGlApi, RakNetApi, RenderDocApi, SdlApi, ShaderCApi, SlangApi, StbApi, TextureApi and VulkanApi, with sub-namespaces such as `Brutal.VulkanApi.Vma`. The one native binding without the suffix, `Brutal.MeshOptimizer`, is listed explicitly.

**No static mutable state (`StaticStateRule`).** No static field in an engine library may be mutable (neither `const` nor `readonly`) unless its type is a primitive, an enum, or an unmanaged or function pointer, none of which can reference a managed game type. Static auto-properties and field-like events are caught through their backing fields, and named after the property. Compiler-generated types (lambda caches, closures, `<PrivateImplementationDetails>`) are skipped: they hold no engine state. `static readonly` fields of mutable types (for example a `static readonly Dictionary`) are not flagged. The documented rule is about mutable fields, and such registries are caught in review against 3.21.

## Failure modes

Each rule collects every violation before failing, and the message lists one per line: the member or edge, what it exposes or references, the rule, and how to fix it. For example:

```
2 violation(s) of "no static mutable fields of non-primitive types in engine assemblies":
  - BrutalEngine.Core.StateProbe.Current (auto-property backing field): static mutable field of type System.Object. Engine assemblies keep mutable state in instances owned by the host, so a game assembly can be reloaded (docs/03-architecture.md 3.21). Move it into an instance, or make it readonly if it never changes.
  - BrutalEngine.Core.StateProbe.Cache: static mutable field of type System.Collections.Generic.List<System.Int32>. ...
```

If the repository can't be found (tests run outside a checkout), the tests fail with that reason instead of passing vacuously. A sanity check also fails if discovery or the docs/04 parser stops finding `BrutalEngine.Core`.

## Testing

- The real engine assemblies are checked by one test per rule.
- Each rule is also unit-tested in isolation, so its negative cases are covered permanently:
  - layering against a small map,
  - binding leaks against fake binding types in `Brutal.FakeApi` (one fake type per leak path: field, property, indexer, return, parameter, by-ref, array, pointer, function pointer, generic argument, event, constructor, operator, base type, interface, type and method constraint, protected member, nested type, delegate) plus types that keep bindings internal,
  - static state against fakes for each kind of mutable field, plus allowed fields and a compiler-generated lambda cache,
  - the map's own validation against broken maps.
- When the rules were written, each one was also shown to fail against real code by temporarily adding a violation to Core (not committed): a `ProjectReference` and type use from Core to a temporary L3 project, a public Core method taking and returning a `Brutal.ProbeApi` type, and static mutable `List<int>` and auto-property fields.

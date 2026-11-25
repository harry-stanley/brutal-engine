# 7. Coding standards

`.editorconfig` and the analyzers enforce most of this mechanically. This document explains the rules the tools can't fully check.

## General C#

- Target `net10.0`, C# `latest`, nullable reference types enabled everywhere, warnings are errors.
- File-scoped namespaces. One public type per file. The file name matches the type name.
- Namespaces match folder paths under the project's root namespace (`BrutalEngine.Rendering2D.Batching`).
- Explicit `using` directives (no implicit usings in shipped libraries), so public API files are self-describing.
- `sealed` by default for classes. Unsealing is a deliberate API decision.
- `readonly struct` / `readonly` members wherever possible. Mutable structs must be documented as such.
- No `async void`. Async is used for I/O and tooling, never inside the frame loop's hot paths.
- No LINQ, lambdas capturing state, `params` arrays, boxing, string concatenation or interpolation into `string` in engine hot paths. Use `"x"u8` literals, spans, BRUTAL `String8` / `StringBuilder`, and BRUTAL collections. Hot paths are marked `[HotPath]`, and our analyzer flags allocations inside them.
- Prefer `Span<T>` / `ReadOnlySpan<T>` parameters over arrays and lists in public APIs that take buffers.

## Public API design

- **Every public member is intentional.** New public API is added to `PublicAPI.Unshipped.txt`, and the reviewer checks that file first. `internal` is the default, and `InternalsVisibleTo` is allowed only for the matching test project.
- **XML docs on every public member:** summary, parameters, return value, exceptions thrown, thread-safety, ownership (who disposes) and allocation behaviour where relevant.
- **Naming** follows the .NET Framework Design Guidelines. Abbreviations are avoided except well-known ones (`Gpu`, `Ui`, `Id`, `Vfs`).
- **No native types in public signatures** above the backend layer (see [Architecture 3.1](03-architecture.md#31-layering)).
- **Handles** for engine objects (see [Architecture 3.4](03-architecture.md#34-handles-and-resource-identity)). **Options structs/records** for anything with more than three configuration parameters, so we can add fields without breaking calls.
- **Errors:** exceptions for misuse, `Try`/`Result` for expected failure (see [Architecture 3.5](03-architecture.md#35-error-handling)). Document every exception a public method can throw.
- **Thread-safety** is part of the contract and documented: "main thread only", "any thread", or "thread-safe".
- **Deprecation:** mark `[Obsolete("Use X instead. Will be removed in vN.0.", DiagnosticId = "BE0xxx")]`, keep it for at least one minor release, and record it in the changelog.

## Native interop rules (engine side)

- Native calls happen **only in backend projects** (`*.Sdl3`, `*.Vulkan`, `Physics2D` internals, `Text` internals and so on), never scattered through higher layers.
- Every call whose native function returns a status is checked through the library's check helper. Our analyzer `BE1001` flags discarded native results.
- Never copy the BRUTAL samples' error handling or Vulkan synchronisation into engine code: they are deliberately minimal. Engine code follows the Vulkan specification and is validated by the layers.
- Every native resource has exactly one owner, is created and destroyed in documented order, and is registered with the leak tracker in debug builds.
- Edit nothing in BRUTAL. If a binding is missing or wrong, report it upstream (issue or PR to ahwootech/BRUTAL) and, if it blocks us, add a minimal, documented, test-covered workaround in our backend project with a link to the upstream issue and a removal condition.
- `unsafe` code is allowed in backends and core data structures. Every `unsafe` block gets a short comment stating why it is sound (bounds, lifetime, aliasing).

## Analyzer rules we own (`BrutalEngine.Analyzers`, IDs `BE1xxx`)

| ID | Rule |
|----|------|
| BE1001 | Status/result value discarded: a call used as a statement whose return type is `Result`, `Result<T>` or `Brutal.VulkanApi.VkResult` (awaited and `?.` calls included). Add more types per project with `brutal_engine.be1001.additional_status_types` in `.editorconfig`. An explicit `_ = Call();` is allowed, with a comment saying why. **Implemented** |
| BE1002 | Allocation in a `[HotPath]` method (new reference type, boxing, closures, LINQ, string building) |
| BE1003 | Static mutable field holding a reference type in an engine assembly |
| BE1004 | Native binding type (`Brutal.*Api`) in a public signature outside backend projects |
| BE1005 | Main-thread-only API called from a method marked `[JobThread]` / `[AudioThread]` |

More are added as patterns appear. Each analyzer has its own tests (positive and negative cases).

## Comments and documentation

- Comments explain *why* and invariants, not *what*.
- `TODO` comments must reference an issue (`// TODO(#123): …`). CI fails on bare TODOs.
- Each subsystem has a short design note in `docs/design/<subsystem>.md` written in the PR that introduces it: purpose, key types, data flow, threading, failure modes, and how it is tested.

## Commits

- **Conventional Commits** (`feat(render): …`, `fix(audio): …`, `test: …`, `docs: …`, `ci: …`, `refactor: …`, `perf: …`, `build: …`, `chore: …`), with scope = module name. A `BREAKING CHANGE:` footer marks breaking changes.
- **One logical change per commit.** A commit should make sense on its own and be small enough to review and revert easily. Examples: "add build configuration", "add CI workflow", "add `Handle` with tests". Never one huge commit that adds everything at once. A commit is not split so finely that it no longer makes sense alone (e.g. a type in one commit and its tests in another).
- **Every commit leaves the repository working.** It builds with zero warnings and all tests pass. A type and its tests land in the same commit. Fix-ups found during review are separate commits on the branch, not rewrites of reviewed commits, unless the reviewer asks for a rebase.
- PRs are squash-merged, so the PR title must be a valid conventional commit. The squashed commit on `main` is the unit of revert there, which is why PRs stay small too (see [Workflow](08-workflow.md)).

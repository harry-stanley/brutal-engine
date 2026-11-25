# Design: generational handles (`BrutalEngine.Core`)

## Purpose

Engine objects exposed to games (entities, textures, sounds, bodies and so on) are referenced by **generational handles**, never by pointers or object references ([Architecture 3.4](../03-architecture.md#34-handles-and-resource-identity)). A handle that outlives its object is detected as stale instead of silently aliasing whatever reuses the slot.

## Key types

| Type | Role |
|------|------|
| `Handle` | Immutable 64-bit value: 32-bit slot index plus 32-bit generation. `default` is `Handle.Null` (generation 0, reserved). `RawValue`/`FromRawValue` for serialisation |
| `HandleAllocator` | Issues handles, validates them (`IsValid`), frees them (`Free`/`TryFree`), and invalidates everything (`Clear`) |
| `StaleHandleException` | Thrown by `Free` when a handle is not live: a programming error, reported immediately |

Typed handles (`TextureHandle`, `EntityId` and so on) will wrap `Handle` in their own modules, so the type system stops handles from being mixed up across pools.

## Rules

- **Generation 0 is reserved for `Null`.** Slots start at generation 1. `new Handle(i, 0)` throws, and `FromRawValue` rejects non-canonical zero-generation values.
- **Freeing advances the slot's generation**, so every outstanding copy of the handle becomes stale at once.
- **FIFO reuse** of freed slots spreads reuse across the pool, so a slot's generation advances as slowly as possible.
- **Retirement instead of wrap-around:** a slot whose generation reaches `uint.MaxValue` is retired when freed and never reused. A stale handle can therefore never become valid again. The cost is one lost slot per 4 billion reuses of that slot.
- **Validity belongs to the allocator.** A handle doesn't know its allocator. Passing a handle to the wrong allocator is detected only if the index or generation doesn't match there, which is why typed handles per pool are the intended public surface.

## Memory and threading

- 9 bytes per slot (generation, free-queue entry, live flag) in managed arrays of primitives, which the GC never scans.
- Managed allocation happens only on growth (doubling, capped at `MaxSlots`). `EnsureCapacity` pre-reserves. A test asserts that steady-state allocate/free cycles allocate 0 bytes.
- Not thread-safe. Each pool is owned by one thread (normally the main thread) or synchronised by its owner.

## Failure modes

| Situation | Behaviour |
|-----------|-----------|
| Pool exhausted (`MaxSlots` live or retired) | `TryAllocate` returns `false`, and `Allocate` throws `InvalidOperationException` |
| Free of a stale or foreign handle | `TryFree` returns `false`, and `Free` throws `StaleHandleException` |
| Free of `Null` | `TryFree` returns `false`, and `Free` throws `ArgumentException` |

## Testing

`tests/BrutalEngine.Core.Tests`: unit tests for every rule above (including generation exhaustion through an internal test seam), a zero-allocation steady-state test, and a CsCheck property test that runs random allocate/free/stale-free sequences against a reference model, checking after every step that live handles are valid, freed handles never become valid again, issued handles are unique for all time, and `Count` matches.

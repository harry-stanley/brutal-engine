# Design: native allocators (`BrutalEngine.Core.Memory`)

## Purpose

Every unmanaged allocation the engine makes goes through one interface, so it can be tracked, and leaks and misuse fail tests instead of corrupting memory in a shipped game ([Architecture 3.3](../03-architecture.md#33-memory)). This is the base layer. Arena/frame and pool allocators build on it in later PRs, and GPU memory stays with VMA.

## Key types

| Type | Role |
|------|------|
| `INativeAllocator` | `Allocate(byteCount, alignment)` and `Free(pointer)`. The compiler fills in the caller file and line, so tracking needs no managed allocation. |
| `HeapAllocator` | General-purpose heap on `NativeMemory.AlignedAlloc` / `AlignedFree`. Stateless and thread-safe. |
| `TrackingAllocator` | Decorator over any `INativeAllocator`. Records each live allocation (size, alignment, call site, sequence), validates every free, and reports leaks. |
| `NativeAllocationInfo` | Snapshot of one live allocation. |
| `NativeMemoryLeakException` | Thrown by `ThrowIfLeaks`. The message is the leak report. |

Why not `Brutal.Memory` directly: BRUTAL's `Mem` has no aligned allocation, and Core stays free of BRUTAL dependencies (it sits below the backends, [Architecture 3.1](../03-architecture.md#31-layering)). Arena and pool allocators may still wrap BRUTAL containers where those fit.

## Rules

- `Allocate` rejects a zero size and any alignment that isn't a power of two, with `ArgumentOutOfRangeException`. It never returns null; running out of memory throws `OutOfMemoryException`.
- `Free(null)` is a no-op. Freeing anything else not allocated by the same allocator is a bug. `HeapAllocator` treats it as undefined behaviour (like C's `free`), and `TrackingAllocator` throws `InvalidOperationException` *before* touching the inner allocator, so the heap isn't corrupted.
- Implementations must repeat `[CallerFilePath]` and `[CallerLineNumber]` on `Allocate`. The attributes aren't inherited, so a call through the concrete type would otherwise record an empty call site (a test covers both call paths).
- Debug builds and all tests that use unmanaged memory wrap their allocators in `TrackingAllocator`, and call `ThrowIfLeaks` at shutdown or test end.

## Memory and threading

- `HeapAllocator` has no state.
- `TrackingAllocator` serialises its bookkeeping with a `System.Threading.Lock`, and calls the inner allocator outside the lock.
- Steady-state allocate/free allocates no managed memory. The tracking table grows (allocates) only past its capacity, so pass `initialCapacity` for hot paths. Reports and snapshots allocate.

## Failure modes

| Failure | Result |
|---------|--------|
| Leak | `ThrowIfLeaks` throws `NativeMemoryLeakException` with the count, bytes, and the oldest 50 allocations with their call sites. |
| Double free / foreign pointer | `TrackingAllocator.Free` throws, and nothing is freed. |
| Invalid size or alignment | `ArgumentOutOfRangeException` before the inner allocator is called. |
| Inner allocator returns null or a still-live address | `InvalidOperationException`: the inner allocator is broken, so the memory isn't handed on. |

## Testing

- Unit tests for each rule and failure mode above, with the leak report checked down to the call site.
- Property tests (CsCheck): any power-of-two alignment is honoured, and random allocate/free sequences keep the counters consistent.
- Concurrency: 8 threads doing 2,000 allocate/free pairs each, then no leaks.
- Allocation tests: 1,000 steady-state allocate/free pairs allocate 0 managed bytes, for both allocators.
- Mutation-checked: removing the byte-count update in `Free`, or the double-free check, fails the suite. The latter crashes the test host with Windows heap corruption, which `dotnet test` reports as a failure.

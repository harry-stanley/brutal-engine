# S5 design notes: recommendations for `BrutalEngine.World`

Spike code (throwaway): `Ecs.cs` (storage, queries, command buffer), `Bench.cs`, `Tests.cs` in this folder.
Measured numbers are in `REPORT.md`. This file separates **measured** facts (M) from **assumed / not prototyped** ones (A).

## 1. Storage layout

**Recommendation: archetype tables split into fixed-size chunks, 64 KB by default, SoA columns inside each chunk.**
Chunks come from a world-level free-list pool of identical blocks (64-byte aligned).

Chunk block layout used in the spike (`Archetype.Layout`, `Ecs.cs`):

```
[Entity ids  : cap x 8 B ] align16
[column 0    : cap x size0] align16
...
[column N-1  : cap x sizeN] align16
[uint changeVersion[N]]           // per-chunk, per-column
```

- `cap = floor(chunkBytes / (8 + sum(sizes)))`, shrunk until the aligned layout fits. Column offsets are per archetype, not per chunk.
- Dense invariant: every chunk but the last is full. Removal swap-moves the archetype's **last** row into the hole. An emptied tail chunk goes back to the pool. No holes means no per-row liveness checks during iteration.

Why chunks rather than one growable table per archetype (both measured, see REPORT):

| Aspect | Table (one growable block) | 16 KB chunks | 64 KB chunks |
|---|---|---|---|
| 1M x 2-comp scalar iterate (M, BDN) | 531 us | 695 us | 588 us |
| SIMD iterate (M, BDN) | 136 us | 234 us | 163 us |
| Change detection granularity | one version per archetype column, useless at 1M (M: 1/1 "chunk" flagged) | about 680 rows | about 2,700 rows |
| Growth | realloc+copy of the whole column set, so a 1M-entity archetype copies about 24 MB in one frame (spike) (A: not benchmarked separately) | O(1) new block | O(1) new block |
| Parallel work unit | must split rows artificially | natural | natural (still split above 16k rows) |
| Pointer stability | moves on growth | stable | stable |
| Memory waste for small archetypes | small | up to 16 KB per archetype | up to 64 KB per archetype. Use a smaller first chunk (A) |

16 KB (the Unity DOTS figure) loses measurably on iteration here: the hardware prefetcher restarts at every column, which spans only about 5 KB. 64 KB keeps per-chunk overhead negligible and still gives useful change-detection granularity. Make `ChunkBytes` a world-creation parameter; the spike's code path is the same for every size.

Production additions (A):
- Columns are aligned to at least 16 B (the spike does this) or to the component's alignment, whichever is larger. Zero-sized tags get no column, only mask bits. The spike gives tags a 1-byte column, which is wasteful.
- Very large components (more than about 1 KB) should go in a side pool. In the spike, a row that doesn't fit a chunk throws.
- Optional "small first chunk" (for example 4 KB) for archetypes with few entities, so 1,000 archetypes don't cost 64 MB.

## 2. Entity id and location map

- `Entity = { uint Index; uint Generation }`, 64 bits, `Generation == 0` means null. This matches architecture section 3.4 (M: stale handles detected; 43k stale handles checked in the randomized test).
- Location records live in an unmanaged array `EntityRecord { int Archetype; int Chunk; int Row; uint Gen }` (16 B) indexed by `Index`. The free list is an unmanaged LIFO stack of indices.
- Generation wrap: the spike skips 0 on wrap. Production should **retire** a slot when its generation would wrap, so an old handle can never alias (A).
- Pack `Chunk`/`Row` into one int if 12 bytes per record is wanted (A).

## 3. Component type identity (hot reload, AOT)

The spike uses `static class TypeId<T> { static readonly int Id }`. It is fast (one static load), but it is **process-global mutable state keyed by game types**, which breaks rule 3.21 ("no static mutable state referencing game types... registry cleared and rebuilt").

Recommendation (A):
- `BrutalEngine.Generators` emits, for every component, a `ComponentInfo { StableId = hash(fullName) + schemaVersion, Size, Align, name (UTF-8), serializer function pointers }` and a registration method.
- A **world-owned registry** maps `StableId` to a dense per-world index.
- Hot paths still need a fast `T -> dense index`. Generate a static `ComponentIndex<T>` slot that the registry *writes* when it is rebuilt. Generic static data in a collectible ALC dies with the ALC, so the engine-side registry has to own the authoritative mapping and re-stamp the slots after a reload.
- `ComponentMask` in the spike is a fixed 256-bit bitset (`ComponentMask`, `Ecs.cs`). Superset tests are 4 ANDs, which is good. If the number of distinct components can grow past 256, use a variable-length bitset or sorted id arrays (A).

## 4. Query API (allocation-free, AOT- and generator-friendly)

Measured shapes, all with **0 B allocated** (M):

1. **Chunk iteration** (core primitive): `foreach (var chunk in world.Chunks(query)) { Span<Pos> p = chunk.Write<Pos>(); ReadOnlySpan<Vel> v = chunk.Read<Vel>(); ... }`
   - `ChunkEnumerator` and `ChunkView` are `ref struct`s.
   - `Write<T>` bumps the change version and `Read<T>` does not, so declared read/write access also drives change detection.
2. **Struct-callback ForEach**: `world.ForEach<TFn, T1, T2>(query, ref fn)` with `TFn : struct, IForEach<T1, T2>`. The JIT/AOT specialises it per struct and it runs within noise of the span loop (M). This is the natural **source-generator target** for systems, and it compiles fine under NativeAOT because there is no reflection and no `MakeGenericType` (A: AOT publish not run in this spike).
3. **Ergonomic per-entity foreach**: `foreach (var e in world.Each<Pos, Vel>(query)) e.A.X += e.B.X;` uses a `ref struct RefPair<T1, T2>` with C# 11 `ref` fields.
   - **BDN: 1.31-1.32 ms on 64K/Table (2.2-2.5x slower than spans, FAILS the 1 ms target); 0.93 ms on 16K** (M). The JIT doesn't enregister the nested enumerator state (chunk enumerator plus 2 spans plus an index).
   - Don't ship this as the primary shape. Instead offer the **two-level** ergonomic form: `foreach (var chunk in q) { var (pos, vel) = chunk.Columns<Pos, Vel>(); for (int i...) }`.
   - Or have `BrutalEngine.Generators` rewrite a user-written `[System] void Move(ref Pos p, in Vel v)` method into an unchecked chunk loop (shape 2). That gives Unity-`IJobEntity`-style ergonomics at span speed (A: generator not built).
4. **Hand SIMD** over reinterpretable columns runs 3-4x faster than scalar (M), because the scalar loop is **compute-bound, not bandwidth-bound**, on this machine. RyuJIT doesn't auto-vectorise these loops. The engine should ship SIMD-friendly math types (BRUTAL `float2`, `float4` and so on) and allow `MemoryMarshal.Cast` to `float` spans. Transform propagation and physics sync are the systems to hand-vectorise first.

Query object (A, only partly measured):
- A `Query` is created once (setup-time allocation) from All/None masks. It holds a cached list of matching archetypes, updated **incrementally**: archetypes are append-only, so a query only scans archetypes created since its last refresh (`World.Refresh`).
- Cache the column index per matched archetype inside the query. The spike looks it up per chunk through `Archetype.ColumnOf[typeId]`, a dense 256-int array per archetype. That's fine at 1-3k chunks but wasteful in memory.
- Fragmentation across 32 archetypes costs little (M, REPORT row "over 32 archetypes").
- Add `Changed<T>` / `Added<T>` filters at chunk level (the spike has `ChunkView.Changed<T>(sinceVersion)`, a wrap-safe compare), plus `Optional<T>`.

## 5. Command buffers

The spike's `CommandBuffer` is an **unmanaged byte stream** (`Mem.Alloc`/`Mem.Realloc`) of `[op][payload]` records: Spawn (archetype + reserved entity + `(typeId, bytes)` pairs), Despawn, Add (typeId + bytes), Remove.
- `Playback()` applies the records in order and resets the length while keeping capacity, so steady state is 0 B (M).
- **Entity reservation at record time**: `cb.Spawn(...)` returns a real `Entity` immediately, so later commands in the same buffer can target it (tested).
- Commands targeting entities that died earlier in playback are **skipped**, not thrown (tested: random Add-on-dead commands).

Measured cost (BDN): 100k spawn + 100k despawn through the buffer takes **4.32-4.36 ms** when despawning in spawn order and **5.95-6.09 ms when despawning in shuffled order (FAIL)**. Stopwatch p99 for in-order is 5.7-6.4 ms. The direct API without a command buffer is 3.5 ms, so the buffer itself adds only about 20 %. The rest is per-entity record lookups, row moves and random access. The target is therefore marginal on a high-end CPU. Optimisations recommended before freezing the API (A, not implemented):
1. **Batched spawn op**. Consecutive `Spawn`s with the same archetype get merged into `SpawnBatch(arch, n)` with payload grouped per column. Playback then does one `AllocRows(n)` per chunk and one `memcpy` per column-run instead of n x (IsAlive + record lookup + per-component decode + copy).
2. A bulk API `cb.SpawnBatch<T1, T2>(count, out Span<Entity>)` that returns column spans to fill directly (no per-entity payload copy).
3. Bulk despawn sorted by (archetype, chunk, row desc), so swap-removes don't move rows that are about to be deleted anyway.
4. Avoid the double `IsAlive` check (`Playback` -> `IsAlive`, then `World.Record` checks again).

For production threading (A):
- One command buffer per system job (or per worker thread), played back at a sync point in **system order**, which keeps the result deterministic.
- Id reservation must be thread-safe: an atomic cursor over a free-list snapshot plus atomic fresh-index growth (Bevy's `reserve_entity` scheme). The spike's `ReserveEntity` is single-threaded.
- Archetype creation at record time (`ArchFor`) mutates the world. With parallel recording, either resolve archetypes at playback or guard archetype creation with a lock (cold path).
- **Pitfall found:** a reserved-but-unplaced entity must not be touched by *direct* structural calls before playback, or it gets placed twice. Give the record an explicit "Pending" state and throw on direct structural calls (cheap check).

## 6. Change detection

- Per-chunk, per-column `uint` version, stamped with `World.Version`, which is incremented per tick or per system run (M: exactly 1 of 7 chunks flagged after writing one chunk; `Read<T>` doesn't flag).
- Compare wrap-safe: `(int)(v - since) > 0`.
- When a swap-remove moves a row into a *different* chunk, every column of the destination chunk is stamped (the spike does this). That's coarse but correct for chunk-level consumers.
- Recommend a separate **structural / "added" version** per chunk, so `Added<T>` can be told apart from `Changed<T>` (A). Physics and render extract need both.
- Per-chunk granularity means a consumer re-examines about 2.7k rows (64 KB chunk) when one changed. For sparse updates (networking), add an optional per-row "dirty" bitset or a change-list component on the archetypes that opt in (A).

## 7. Hierarchy (not prototyped, A)

- `Parent { Entity Value }` component, plus a `Children` relationship stored **outside** the archetype tables: a world-owned unmanaged slab of `NativeList`-like ranges indexed by a `ChildrenHandle` component. Variable-length data in chunks would break fixed row size.
- Make `Parent`/`Children` changes structural commands (`cb.SetParent(child, parent)`) so both sides update atomically at playback.
- Transform propagation:
  - Keep a `HierarchyDepth` component (or sort roots-first into a per-frame, depth-ordered entity list) and propagate level by level. That's parallel within a level, and fast with chunk change versions (skip subtrees whose `LocalTransform` chunks didn't change).
  - Alternative: Flecs-style "cascade" queries ordered by depth.
- Despawning a parent despawns its descendants recursively, inside command-buffer playback.

## 8. Parallel queries on BRUTAL `JobScheduler`

Measured (M, BDN): 1M x 2-comp iterate takes 289-326 us with 4 runners, 178-208 us with 8 and 139-178 us with 16, against 531-695 us single-threaded. That's 3.3-3.9x at 16 runners, so scaling flattens past 8 (probably bandwidth plus Queue/Wait overhead). Steady state allocates 0 B per frame.
- The spike pre-allocates one `IJob` class per runner (`ParallelIntegrate`, `Bench.cs`), builds an unmanaged slice list (archetype, chunk, row range of at most 16k rows), splits it evenly, then calls `Queue` -> `ExecuteJobs` -> `Wait` every frame.
- One process-wide allocation of 6,192 B showed up in an early 300-frame run in Table mode (probably ThreadPool warm-up or growth). In the 1,000-frame run it was 0. The 3.3 allocation test should therefore count **process** bytes (`GC.GetTotalAllocatedBytes(true)`), not just the current thread's.

## 9. BRUTAL APIs: useful vs problematic (source-read; the Mem/JobScheduler behaviour was exercised at runtime)

Useful:
- `Brutal.Memory.Mem.Alloc/AllocZeroed/Realloc/Free/Clear` (`Brutal.Memory/Mem.cs:16, :32, :72, :150, :64`). Thin and zero-overhead over `NativeMemory`. Used for records, free list, chunk-pointer arrays, columns (table mode) and the command-buffer stream.
- `Brutal.Concurrency.Jobs.JobScheduler` (`Brutal.Concurrency/Jobs/JobScheduler.cs:28` ctor, `:65` Queue, `:89` ExecuteJobs, `:79` Wait), `IJob.Run()` (`IJob.cs:8`). Allocation-free per frame in practice: `JobRunner` reuses an `IThreadPoolWorkItem` (`JobRunner.cs:340-341`, `ThreadPool.UnsafeQueueUserWorkItem` at `:144`) and a pre-sized `List<IJob>` (`:53`).

Problematic / gaps:
- **No aligned allocation** in `Mem` (`Mem.cs:16` is `NativeMemory.Alloc`). Chunks want 64-byte alignment, so the spike calls `NativeMemory.AlignedAlloc` directly (`World.RentChunk`). Add `Mem.AlignedAlloc/AlignedFree`, or build the engine's chunk pool allocator (3.3) on `NativeMemory` directly.
- No allocators yet (pool, arena): `Brutal.Memory/Partition` only holds size interfaces. The world's chunk pool *is* the pool allocator 3.3 asks for; build it in `BrutalEngine.Core` with debug tracking.
- `Mem` takes `ByteSize`, which has only an explicit conversion from int, and returns `Brutal.Pointers.Ptr` (`.Pointer` to get `void*`). That makes `(T*)Mem.Alloc(new ByteSize((nuint)n)).Pointer` boilerplate everywhere. Wrap it in engine helpers.
- `NativeList<T>` was **not** used for columns or chunk lists:
  - it's a class with a finalizer (`Brutal.Collections/NativeList/NativeListOfT.cs:12, :15`);
  - `foreach` uses `yield` (`:126-129`, allocates);
  - growth is alloc+copy+free rather than realloc (`NativeList/NativeList.cs:57-62`), starting at capacity 4 (`:18`);
  - known bugs are listed in the skill reference.

  It's fine for setup-time lists, but the ECS needs raw control over layout and growth.
- `JobScheduler` limits:
  - `Queue` throws while jobs are executing (`JobScheduler.cs:65-68`), so there's no work stealing or nested scheduling, and no dependency graph or `ParallelFor`.
  - Jobs run sequentially inside a runner, so load balance relies entirely on our own even slicing.
  - `Wait()` busy-spins (`:79-83`, `JobRunner.Wait` at `:212`).
  - A runner sets the pool thread's priority and never restores it (`JobRunner.cs:289`).
  - A throwing job is not caught (per the skill reference: `DoWork` has no catch).

  The schedule executor ("run non-conflicting systems in parallel") needs a DAG layer of its own on top: batch the ready systems into one `ExecuteJobs` wave, `Wait`, repeat. Wave barriers cost a busy-spin each.

## 10. Pitfalls found in the spike

1. **The scalar inner loop is compute-bound** (no auto-vectorisation). SIMD gave 3-4x (M). The 1 ms target is met by plain scalar code on this CPU, but the 3-component query (about 1.5 ms scalar) is not. Plan SIMD for hot built-in systems.
2. **16 KB chunks cost 18-31 %** on scalar iteration and 44-72 % on SIMD, compared with 64 KB and table storage (M, BDN).
3. **Table storage defeats change detection** (one version per archetype) and has big growth copies. It's the fastest raw iterator, though.
4. **Spawn+despawn exceeds 5 ms when despawn order is random (5.95-6.09 ms) and at p99**, even though in-order passes (4.3 ms) (M). Needs batching and sorted bulk despawn before the API is frozen.
4b. **The ref-pair per-entity enumerator fails the 1 ms iteration target** (1.31 ms) on 64K/Table (M). Use chunk loops or generator rewriting.
5. A reserved entity touched by direct structural calls before playback gets double-placed (found by design review, not by a test failure). Add a Pending state.
6. `TypeId<T>` static generic ids conflict with hot reload (3.21), so ids must come from a world-owned registry.
7. Benchmark noise: the 5950X has 2 CCDs with 32 MB L3 each. The 1M x 2-comp working set is about 24 MB, so results depend on which CCD the thread lands on. Min-spec CPUs with less L3 will be **more** bandwidth-bound than measured here.
8. A dense `int[256]` `ColumnOf`/edge table per archetype costs 3 KB per archetype. Fine for a spike; in production use sorted arrays or small hash maps for edges.

## 11. What is measured vs assumed

- **Measured:** every number in REPORT.md; zero managed allocation for iteration (all shapes), parallel iteration, CB spawn/despawn and add/remove in steady state; correctness of storage, moves, edges, command buffers, stale handles and change versions against a Dictionary model (5 seeds x 4,000 steps, plus a 30,000-step run per storage mode).
- **Assumed / not done:**
  - min-spec hardware (only the reference 5950X was available);
  - NativeAOT publish of the spike;
  - generator output;
  - hierarchy and transform propagation;
  - thread-safe reservation and multi-buffer merge;
  - batched spawn;
  - events;
  - serialisation;
  - the real memory cost of many small archetypes.

# S5 report: archetype ECS storage spike vs ADR-0006 targets

Date: 2026-11-25. Spike dir: `C:/Users/Harry/AppData/Local/hermes/profiles/gamedev/cache/scratch/s5-ecs`
(`Ecs.cs` storage, `Bench.cs` benchmarks, `Tests.cs` randomized test, `Program.cs` modes `test | alloc [frames] | bench <BDN args>`).

## Machine (reference machine only; NOT min-spec)

| Item | Value |
|---|---|
| CPU | AMD Ryzen 9 5950X 16-Core (Zen 3, 2 CCDs x 32 MB L3), 3.4 GHz base; via `Get-CimInstance Win32_Processor` |
| RAM | 32 GB, DDR4 configured 3333 MT/s |
| GPU | RTX 2070 SUPER (irrelevant here) |
| OS | Windows 11 Pro 10.0.26300 |
| Runtime | .NET SDK 10.0.404 (global.json 10.0.100 + latestFeature), runtime 10.0.15, RyuJIT x86-64-v3, Release, TieredPGO on |
| Packages | BenchmarkDotNet 0.15.8, Ahwoo.Brutal.Memory 2026.10.2, Ahwoo.Brutal.Concurrency 2026.10.2 |

**There is no min-spec machine available.** All pass/fail below is on a high-end desktop CPU; a min-spec CPU (less L3, slower DRAM, lower clocks) will be slower. Treat any result within ~2x of a target as **at risk** on min-spec.

## What was built

- **Components and ids:** `where T : unmanaged` components, generational 32+32-bit `Entity`, an unmanaged entity-record location map and an unmanaged free list.
- **Archetypes:** a 256-bit `ComponentMask` per archetype, plus add/remove **graph edge cache** per archetype.
- **Storage variants**, all SoA in unmanaged memory:
  - `Chunk16K`: fixed 16 KB chunks from a pooled, 64 B-aligned block allocator;
  - `Chunk64K`: the same with 64 KB chunks;
  - `Table`: one growable block per archetype.
- **Structural ops:** spawn, despawn, add and remove. A row moves between archetypes, then the source row is swap-removed from the tail.
- **Queries:** cached queries with incremental archetype matching. Three iteration shapes: chunk spans, struct-callback `ForEach`, and per-entity ref-pair `foreach`.
- **Change detection:** per-chunk, per-column change versions; `Write<T>` bumps the version, `Read<T>` doesn't.
- **Command buffer:** an unmanaged byte stream of spawn/despawn/add/remove commands, with entity reservation at record time and dead-target skipping.
- **Parallel query:** chunk slices on BRUTAL `JobScheduler`, with jobs pre-allocated.
- **Memory:** columns, records, free list, chunk tables and the command buffer go through `Brutal.Memory.Mem`. Chunk blocks use `NativeMemory.AlignedAlloc`, because `Mem` has no aligned allocation.

## Correctness (randomized differential test vs Dictionary reference model)

`dotnet bin/Release/net10.0/S5Ecs.dll test`: PASS for both Chunk16K and Table.
- 5 seeds x 4,000 steps, plus a 30,000-step run per mode.
- Mixed operations, direct and via command buffer: spawn (empty / 2-comp / 1-comp), despawn, add (including overwrite), remove (including absent), `Get` writes, and deferred adds targeting dead entities.
- Every 50 steps it verifies:
  - alive count;
  - per-entity presence and values for 4 component types;
  - every despawned handle is reported dead (43,544 stale handles checked in the long run);
  - six query masks: entity counts match, values read through the query match, and each row's record points back to its (archetype, row).
- The long run ends with 21,852 alive entities, 16 archetypes and 34 chunks (at most 6 chunks per archetype), so multi-chunk swap-removes were exercised.
- The change-detection test passes: writing 1 chunk flags exactly 1 of 7 chunks; `Read<T>` flags none.

## BenchmarkDotNet results

Config: `--warmupCount 3 --iterationCount 10 --launchCount 1`, `[MemoryDiagnoser]`. Each op is one "frame" of work. Kernel: `p += v * dt`; 3-comp is `v += a*dt; p += v*dt`; 1-comp is `p.X += 1`.
"Allocated -" means 0 B/op.

### Iteration: 1M entities, queries over 1/2/3 components, plus fragmentation (32 archetypes)

| Method                               | Mode     | Mean       | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------------- |--------- |-----------:|----------:|----------:|------:|--------:|----------:|------------:|
| '1M x Q<Pos> (1 comp)'               | Chunk16K |   285.7 μs |  17.78 μs |  11.76 μs |  0.41 |    0.04 |         - |          NA |
| '1M x Q<Pos,Vel> chunk spans'        | Chunk16K |   695.3 μs |  92.15 μs |  60.95 μs |  1.01 |    0.12 |         - |          NA |
| '1M x Q<Pos,Vel> struct ForEach'     | Chunk16K |   710.4 μs | 100.11 μs |  66.22 μs |  1.03 |    0.12 |         - |          NA |
| '1M x Q<Pos,Vel> ref-pair foreach'   | Chunk16K |   933.4 μs | 108.67 μs |  64.67 μs |  1.35 |    0.14 |         - |          NA |
| '1M x Q<Pos,Vel> unchecked refs'     | Chunk16K |   749.9 μs | 118.27 μs |  78.23 μs |  1.09 |    0.14 |         - |          NA |
| '1M x Q<Pos,Vel> SIMD Vector256'     | Chunk16K |   234.0 μs |   4.04 μs |   2.40 μs |  0.34 |    0.03 |         - |          NA |
| '1M x Q<Pos,Vel,Acc> (3 comp)'       | Chunk16K | 1,617.4 μs | 214.92 μs | 142.16 μs |  2.34 |    0.28 |         - |          NA |
| '1M x Q<Pos,Vel> over 32 archetypes' | Chunk16K |   661.1 μs |  34.45 μs |  20.50 μs |  0.96 |    0.08 |         - |          NA |
|                                      |          |            |           |           |       |         |           |             |
| '1M x Q<Pos> (1 comp)'               | Chunk64K |   245.8 μs |  15.48 μs |  10.24 μs |  0.42 |    0.02 |         - |          NA |
| '1M x Q<Pos,Vel> chunk spans'        | Chunk64K |   587.8 μs |  35.85 μs |  21.33 μs |  1.00 |    0.05 |         - |          NA |
| '1M x Q<Pos,Vel> struct ForEach'     | Chunk64K |   542.2 μs |  21.27 μs |  11.13 μs |  0.92 |    0.04 |         - |          NA |
| '1M x Q<Pos,Vel> ref-pair foreach'   | Chunk64K | 1,323.6 μs |  24.30 μs |  16.07 μs |  2.25 |    0.08 |         - |          NA |
| '1M x Q<Pos,Vel> unchecked refs'     | Chunk64K |   581.1 μs |  15.06 μs |   9.96 μs |  0.99 |    0.04 |         - |          NA |
| '1M x Q<Pos,Vel> SIMD Vector256'     | Chunk64K |   162.9 μs |   2.97 μs |   1.76 μs |  0.28 |    0.01 |         - |          NA |
| '1M x Q<Pos,Vel,Acc> (3 comp)'       | Chunk64K | 1,334.3 μs |  19.99 μs |  11.90 μs |  2.27 |    0.08 |         - |          NA |
| '1M x Q<Pos,Vel> over 32 archetypes' | Chunk64K |   582.7 μs |  10.05 μs |   6.65 μs |  0.99 |    0.03 |         - |          NA |
|                                      |          |            |           |           |       |         |           |             |
| '1M x Q<Pos> (1 comp)'               | Table    |   221.6 μs |   4.51 μs |   2.68 μs |  0.42 |    0.01 |         - |          NA |
| '1M x Q<Pos,Vel> chunk spans'        | Table    |   530.6 μs |  30.29 μs |  18.02 μs |  1.00 |    0.05 |         - |          NA |
| '1M x Q<Pos,Vel> struct ForEach'     | Table    |   536.4 μs |  39.26 μs |  25.97 μs |  1.01 |    0.06 |         - |          NA |
| '1M x Q<Pos,Vel> ref-pair foreach'   | Table    | 1,313.0 μs |  54.51 μs |  36.05 μs |  2.48 |    0.10 |         - |          NA |
| '1M x Q<Pos,Vel> unchecked refs'     | Table    |   656.1 μs |  27.35 μs |  18.09 μs |  1.24 |    0.05 |         - |          NA |
| '1M x Q<Pos,Vel> SIMD Vector256'     | Table    |   136.0 μs |   8.85 μs |   5.85 μs |  0.26 |    0.01 |         - |          NA |
| '1M x Q<Pos,Vel,Acc> (3 comp)'       | Table    | 1,479.9 μs |  60.57 μs |  40.06 μs |  2.79 |    0.11 |         - |          NA |
| '1M x Q<Pos,Vel> over 32 archetypes' | Table    |   530.0 μs |  28.76 μs |  19.02 μs |  1.00 |    0.05 |         - |          NA |

### Parallel: 1M x Q<Pos,Vel> on BRUTAL JobScheduler (slices of at most 16k rows, even split across runners)

| Method                                  | Mode     | Runners | Mean     | Error    | StdDev  | Allocated |
|---------------------------------------- |--------- |-------- |---------:|---------:|--------:|----------:|
| '1M x Q<Pos,Vel> parallel JobScheduler' | Chunk16K | 4       | 326.1 μs |  9.76 μs | 6.45 μs |         - |
| '1M x Q<Pos,Vel> parallel JobScheduler' | Chunk16K | 8       | 207.9 μs |  7.97 μs | 4.74 μs |         - |
| '1M x Q<Pos,Vel> parallel JobScheduler' | Chunk16K | 16      | 177.9 μs | 14.33 μs | 8.53 μs |         - |
| '1M x Q<Pos,Vel> parallel JobScheduler' | Table    | 4       | 289.0 μs |  3.03 μs | 2.00 μs |         - |
| '1M x Q<Pos,Vel> parallel JobScheduler' | Table    | 8       | 178.0 μs |  0.98 μs | 0.65 μs |         - |
| '1M x Q<Pos,Vel> parallel JobScheduler' | Table    | 16      | 138.8 μs |  3.57 μs | 1.87 μs |         - |

(Single-threaded baseline for the same op: 531-695 us, from the table above.)

### Structural: 100k per frame, world holds a 1M-entity background population in the same archetype

The Chunk64K variant was not included in this BDN class (time); it shares the code path with Chunk16K.

| Method                                 | Mode     | Mean     | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------------------------- |--------- |---------:|----------:|----------:|------:|--------:|----------:|------------:|
| '100k spawn+despawn via CB (in order)' | Chunk16K | 4.359 ms | 0.0980 ms | 0.0583 ms |  1.00 |    0.02 |         - |          NA |
| '100k spawn+despawn via CB (shuffled)' | Chunk16K | 5.946 ms | 0.1413 ms | 0.0934 ms |  1.36 |    0.03 |         - |          NA |
| '100k spawn+despawn direct'            | Chunk16K | 3.498 ms | 0.0814 ms | 0.0485 ms |  0.80 |    0.01 |         - |          NA |
| '100k add+remove <Health> via CB'      | Chunk16K | 6.409 ms | 0.0956 ms | 0.0569 ms |  1.47 |    0.02 |         - |          NA |
| '100k add+remove <Health> direct'      | Chunk16K | 5.504 ms | 0.1121 ms | 0.0667 ms |  1.26 |    0.02 |         - |          NA |
|                                        |          |          |           |           |       |         |           |             |
| '100k spawn+despawn via CB (in order)' | Table    | 4.324 ms | 0.1744 ms | 0.1153 ms |  1.00 |    0.04 |         - |          NA |
| '100k spawn+despawn via CB (shuffled)' | Table    | 6.088 ms | 0.3847 ms | 0.2544 ms |  1.41 |    0.07 |         - |          NA |
| '100k spawn+despawn direct'            | Table    | 3.465 ms | 0.0652 ms | 0.0431 ms |  0.80 |    0.02 |         - |          NA |
| '100k add+remove <Health> via CB'      | Table    | 6.131 ms | 0.1058 ms | 0.0700 ms |  1.42 |    0.04 |         - |          NA |
| '100k add+remove <Health> direct'      | Table    | 5.273 ms | 0.1141 ms | 0.0679 ms |  1.22 |    0.03 |         - |          NA |

- "in order": despawn in spawn order.
- "shuffled": despawn in a random permutation, which is worse for locality.
- "direct": world API without a command buffer.
- add/remove: add `Health` to 100k existing entities, then remove it. Each entity moves between archetypes twice.

## Allocation verification (independent of BDN)

`dotnet bin/Release/net10.0/S5Ecs.dll alloc 1000`: 20 warm-up frames, then 1,000 measured frames per op.
- "thread bytes" = `GC.GetAllocatedBytesForCurrentThread()` delta.
- "process bytes" = `GC.GetTotalAllocatedBytes(true)` delta, which covers the JobScheduler workers.

Timings come from `Stopwatch` and are noisier than BDN.

#### Chunk16K: 1000 frames each (after 20 warm-up frames)
| op | median ms | p99 ms | thread bytes | process bytes |
|---|---|---|---|---|
| iter2 spans | 0.766 | 1.435 | 0 | 0 |
| iter2 ForEach | 0.764 | 1.502 | 0 | 0 |
| iter2 ref-pair | 0.986 | 1.947 | 0 | 0 |
| iter2 unchecked | 0.747 | 1.436 | 0 | 0 |
| iter2 SIMD | 0.242 | 0.685 | 0 | 0 |
| iter1 | 0.387 | 1.202 | 0 | 0 |
| iter3 | 1.665 | 2.900 | 0 | 0 |
| parallel8 iter2 | 0.255 | 0.387 | 0 | 0 |
| CB spawn+despawn 100k | 4.750 | 6.439 | 0 | 0 |
| CB add+remove 100k | 6.749 | 9.367 | 0 | 0 |
#### Chunk64K: 1000 frames each (after 20 warm-up frames)
| op | median ms | p99 ms | thread bytes | process bytes |
|---|---|---|---|---|
| iter2 spans | 0.585 | 1.154 | 0 | 0 |
| iter2 ForEach | 0.543 | 1.155 | 0 | 0 |
| iter2 ref-pair | 0.691 | 1.273 | 0 | 0 |
| iter2 unchecked | 0.638 | 1.131 | 0 | 0 |
| iter2 SIMD | 0.170 | 0.254 | 0 | 0 |
| iter1 | 0.240 | 0.531 | 0 | 0 |
| iter3 | 1.614 | 2.946 | 0 | 0 |
| parallel8 iter2 | 0.203 | 0.282 | 0 | 0 |
| CB spawn+despawn 100k | 4.817 | 6.194 | 0 | 0 |
| CB add+remove 100k | 6.794 | 9.845 | 0 | 0 |
#### Table: 1000 frames each (after 20 warm-up frames)
| op | median ms | p99 ms | thread bytes | process bytes |
|---|---|---|---|---|
| iter2 spans | 0.557 | 0.999 | 0 | 0 |
| iter2 ForEach | 0.738 | 1.155 | 0 | 0 |
| iter2 ref-pair | 0.696 | 1.354 | 0 | 0 |
| iter2 unchecked | 0.658 | 1.143 | 0 | 0 |
| iter2 SIMD | 0.131 | 0.173 | 0 | 0 |
| iter1 | 0.222 | 0.472 | 0 | 0 |
| iter3 | 1.526 | 2.435 | 0 | 0 |
| parallel8 iter2 | 0.187 | 0.238 | 0 | 0 |
| CB spawn+despawn 100k | 4.573 | 5.677 | 0 | 0 |
| CB add+remove 100k | 6.562 | 9.355 | 0 | 0 |


All 0 B. One earlier 300-frame run showed 6,192 process bytes once on `parallel8` (Table), which is probably ThreadPool warm-up. It did not repeat in the 1,000-frame run.

## Pass/fail vs ADR-0006 targets (reference machine)

| Target | Result | Verdict |
|---|---|---|
| Iterate 1M entities x 2 components < 1 ms single-threaded | Chunk spans: 695 us (16K) / 588 us (64K) / 531 us (Table). Struct ForEach: 710 / 542 / 536 us. SIMD: 234 / 163 / 136 us. **Ref-pair per-entity foreach: 933 / 1,324 / 1,313 us** | **PASS** for chunk-span, ForEach and SIMD shapes. **FAIL** for the ergonomic ref-pair `foreach` on 64K/Table. At risk on min-spec for scalar 16K (0.70 ms on a 5950X) |
| Spawn + despawn 100k / frame via command buffers < 5 ms | 4.36 ms (16K) / 4.32 ms (Table) despawning in order; **5.95 / 6.09 ms shuffled**. Stopwatch p99 for in-order is 5.7-6.4 ms | **Marginal PASS** in order; **FAIL** shuffled and at p99. Needs batched playback before the API freezes (see DESIGN-NOTES section 5) |
| Zero managed allocations | 0 B/op in every BDN case (MemoryDiagnoser), and 0 B per thread and per process over 1,000 frames for every op, including parallel | **PASS** |
| (informative) 3-comp query | 1,334-1,617 us scalar | Not an ADR target. Over 1 ms scalar; SIMD expected to fix it (not measured for 3-comp) |
| (informative) Parallel 1M x 2 | 178 us (16K, 16 runners), 139 us (Table, 16 runners); about 3.3-3.9x at 16 runners | Works, zero alloc |

### Interpretation

- **Scalar loops are compute-bound, not bandwidth-bound**, on this CPU: SIMD is 3-4x faster on the same data. So the 1 ms target depends on the code shape the generator emits, more than on the storage.
- **16 KB chunks** cost about 10-30 % against 64 KB/Table for scalar spans (695 vs 588/531 us) and about 45-70 % for SIMD (234 vs 163/136 us).
- **The ref-pair enumerator** (`foreach (var e in world.Each<A,B>(q))`) is 2.2-2.5x slower than spans on 64K/Table: its nested enumerator state isn't enregistered. Don't make it the primary API without a generator rewrite (DESIGN-NOTES section 4).
- **Fragmentation** across 32 archetypes costs about nothing.
- **Structural cost** is about 17-22 ns per spawn-or-despawn through the buffer, of which the CB overhead is about 4 ns (direct spawn+despawn is 3.5 ms). Shuffled despawn adds about 40 % from random record and row access.

## How to reproduce

```
cd C:/Users/Harry/AppData/Local/hermes/profiles/gamedev/cache/scratch/s5-ecs
dotnet build -c Release
dotnet bin/Release/net10.0/S5Ecs.dll test
dotnet bin/Release/net10.0/S5Ecs.dll alloc 1000
dotnet bin/Release/net10.0/S5Ecs.dll bench --filter '*' --warmupCount 3 --iterationCount 10 --launchCount 1 --exporters github
```
Raw BDN output: `BenchmarkDotNet.Artifacts/results/*.md|csv|html`, `bdn-log.txt`.

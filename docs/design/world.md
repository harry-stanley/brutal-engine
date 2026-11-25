# Design: world (`BrutalEngine.World`)

## Purpose

`BrutalEngine.World` is the engine's world model: an in-house archetype ECS with built-in hierarchy and 2D transforms, decided in [ADR-0006](../adr/0006-world-model.md) and summarised in [Architecture 3.6](../03-architecture.md#36-world-model). It is an L3 library: it references Core and nothing from Platform, Graphics or any backend (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:26`).

This note is the API sketch for M1's World issues. Signatures are the contract; XML docs, `PublicAPI.Unshipped.txt` entries, argument validation and tests follow the usual rules ([Coding standards](../07-coding-standards.md#public-api-design)). "Owner thread" is defined in section 10; every member below is owner-thread-only unless it says otherwise.

- Decision (maintainer, 2026-11-26): the M1 public APIs are settled in design notes first. Merging this note approves the World API, and the World code issues are drafted from it.
- Decision (maintainer, 2026-11-26): schedules have declared access and conflict detection and run single-threaded in M1; parallel execution comes in M2 behind the same API.
- Decision (maintainer, 2026-11-26): the source generator is packed inside the World package; it generates component registration and query loops in M1, and serialisers in M2.
- Decision (maintainer, 2026-11-26): the world type is `EcsWorld` and the system attribute `[EcsSystem]` (reason below).
- Decision (maintainer, 2026-11-26): public maths types come from the BCL's `System.Numerics` (`Vector2`, `Matrix3x2`), not BRUTAL's Numerics, and World takes no dependency on `Ahwoo.Brutal.Numerics`.
- Decision (maintainer, 2026-11-26): events and messages between systems are deferred past M1, and designed with their first consumer (input or physics).

Evidence: spike S5 ([results](../spikes/README.md#s5-archetype-ecs-prototype-adr-0006)) and its design notes and code on the `spike/m0-local-spikes` branch (`spikes/s5-ecs/`), cited below as `DESIGN-NOTES.md:<line>` and `Ecs.cs:<line>`. Every S5 recommendation is adopted or rejected in the S5 table at the end. BRUTAL citations are paths in the BRUTAL repository at the pinned 2026.10.x packages.

### Package, names and dependencies

- Package and root namespace `BrutalEngine.World`. Hierarchy types live in `BrutalEngine.World.Hierarchy` and transforms in `BrutalEngine.World.Transforms`.
- Decision (maintainer, 2026-11-26): **the world type is `EcsWorld`, not `World`, and the system attribute is `[EcsSystem]`, not `[System]`.** Inside any `BrutalEngine.*` namespace (Runtime, Rendering2D, Physics2D...), the simple name `World` binds to the namespace `BrutalEngine.World` before any `using` is considered, so `World w = new World();` fails with CS0118 ("'World' is a namespace but is used like a type"). Observed with a scratch compile on .NET SDK 10.0.404; the same code in a game's own namespace compiles. `[System]` has the same problem with the `System` namespace.
- Dependencies: `BrutalEngine.Core` (allocators, `Handle`, `StaleHandleException`, and `ThreadAffinity` / `WrongThreadException` from the [platform note's threading section](platform.md#9-threading)) and the BCL. Public maths types come from `System.Numerics` (`Vector2`, `Matrix3x2`). BRUTAL Numerics is not used in World's public API: decision (maintainer, 2026-11-26). World takes no BRUTAL package dependency in M1; `Ahwoo.Brutal.Concurrency` arrives with parallel schedules in M2.

## 1. World creation, ownership and disposal

```csharp
namespace BrutalEngine.World;

public sealed record WorldOptions
{
    public INativeAllocator? Allocator { get; init; }            // null: a HeapAllocator. Not owned; must outlive the world
    public int ChunkBytes { get; init; } = 64 * 1024;            // power of two, 16 KiB to 1 MiB
    public int InitialEntityCapacity { get; init; } = 1024;      // entity slots reserved up front
    public int MaxEntities { get; init; } = 1 << 24;             // live + pending + retired slots, at most int.MaxValue
    public int MaxComponentTypes { get; init; } = 256;           // multiple of 64, at most 4096: width of archetype masks
    public int MaxHierarchyDepth { get; init; } = 64;            // section 8
    public TimeProvider Clock { get; init; } = TimeProvider.System;   // per-system timings only (section 7)
    public string Name { get; init; } = "World";                 // diagnostics and leak reports
}

public sealed class EcsWorld : IDisposable
{
    public EcsWorld(WorldOptions options);
    public string Name { get; }
    public ComponentRegistry Components { get; }                 // section 3
    public ScheduleCollection Schedules { get; }                 // section 7
    public int EntityCount { get; }                              // placed (alive) entities
    public int ArchetypeCount { get; }
    public int ChunkCount { get; }                               // chunks holding rows; pooled empty chunks excluded
    public uint ChangeTick { get; }                              // section 5
    public void AdvanceChangeTick();
    public void TrimMemory();                                    // returns pooled empty chunks to the allocator
    public void Dispose();
    // entity and component members: section 2; queries: section 5; hierarchy: section 8
}
```

- **Validation.** The constructor throws `ArgumentNullException` for null options and `ArgumentOutOfRangeException` naming the option for any value outside the ranges above.
- **Ownership.** The world owns every unmanaged block it allocates: the entity table, chunks and the chunk pool, archetype tables, query caches and the children slab. All of it comes from `Allocator`, which the world does not own (the caller keeps it alive and disposes it, as in [memory](memory.md#rules)). The Runtime host owns the game's world and disposes it in reverse creation order ([Architecture 3.3](../03-architecture.md#33-memory)).
- **Chunk size.** 64 KiB by default, as S5 measured (`DESIGN-NOTES.md:8`, `:36`). One chunk size per world keeps column offsets a per-archetype constant. There is no "small first chunk" in M1 (S5 table): the cost is at most one partly filled chunk per archetype, and a world expected to hold many tiny archetypes can choose 16 KiB.
- **Dispose** frees everything the world allocated and is idempotent. Afterwards every member, and every member of its queries and schedules, throws `ObjectDisposedException`. A `CommandBuffer` owns its own stream (section 6) and must be disposed too; its stream came from the world's allocator, so a leak check sees it.
- **Leak checks.** Tests and debug hosts pass a `TrackingAllocator` and call `ThrowIfLeaks()` after `Dispose` ([memory](memory.md#rules)).

## 2. Entities

```csharp
public readonly struct Entity : IEquatable<Entity>
{
    public Entity(uint index, uint generation);                  // generation 0 throws ArgumentOutOfRangeException
    public static Entity Null { get; }                           // default
    public uint Index { get; }
    public uint Generation { get; }
    public bool IsNull { get; }
    public ulong RawValue { get; }
    public static Entity FromRawValue(ulong rawValue);           // rejects non-canonical zero-generation values
    // Equals, GetHashCode, ==, !=, ToString ("Entity(12v3)")
}
```

`Entity` wraps Core's `Handle` (32-bit index, 32-bit generation, generation 0 is null; [handles](handles.md#rules)), so it is unmanaged and can be stored inside components. It carries no world reference.

**Entity table.** An unmanaged array of 16-byte records `{ uint Generation; int Archetype; int Chunk; int Row; }` indexed by `Index` (S5's layout, `DESIGN-NOTES.md:46`). `Archetype` doubles as the state: `>= 0` alive (placed), `-1` free, `-2` pending, `-3` retired. The table grows by doubling through the allocator up to `MaxEntities`.

- **Reuse is FIFO** through an unmanaged ring of free indices, the [handles](handles.md#rules) rule, so a slot's generation advances as slowly as possible (S5 used a LIFO stack).
- **Retirement instead of wrap:** a slot freed at generation `uint.MaxValue` is retired and never reused, so a stale `Entity` can never become valid again (`DESIGN-NOTES.md:47`).
- **Pending:** an entity reserved by a command buffer (section 6) is pending until playback places it, or until the buffer is cleared or disposed, which frees it with an advanced generation. `IsAlive` is false and `IsPending` true for it, and every direct call on it throws `InvalidOperationException` (S5's double-placement pitfall, `DESIGN-NOTES.md:97`).
- **Exhaustion:** spawning when all `MaxEntities` slots are live, pending or retired throws `InvalidOperationException` naming the option.

**Direct API** (on `EcsWorld`). Structural members throw `InvalidOperationException` while a query enumeration is live, or inside a running system that isn't exclusive (section 7); deferred changes go through command buffers.

```csharp
public Entity Spawn();
public Entity Spawn<T1>(in T1 c1) where T1 : unmanaged, IComponent;
public Entity Spawn<T1, T2>(in T1 c1, in T2 c2) where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent;
// ... up to Spawn<T1, T2, T3, T4>; more components: Spawn, then Add or Set
public void Despawn(Entity entity);                              // also despawns descendants (section 8)
public bool IsAlive(Entity entity);                              // false for Null, stale and pending
public bool IsPending(Entity entity);
public EntityRef Ref(Entity entity);

public bool Has<T>(Entity entity) where T : unmanaged, IComponent;
public ref readonly T Get<T>(Entity entity) where T : unmanaged, IComponent;   // no change stamp
public ref T Write<T>(Entity entity) where T : unmanaged, IComponent;          // stamps T's Changed tick
public bool TryGet<T>(Entity entity, out T value) where T : unmanaged, IComponent;
public void Add<T>(Entity entity, in T value) where T : unmanaged, IComponent; // throws if present
public void Set<T>(Entity entity, in T value) where T : unmanaged, IComponent; // add or replace, stamps
public bool Remove<T>(Entity entity) where T : unmanaged, IComponent;         // false if absent
```

References returned by `Get`/`Write` point into a chunk and are invalidated by any structural change, like a span over a list. `EntityRef` is the ergonomic layer ADR-0006 asks for ("typed `Entity` handle with `Get<T>`/`Set<T>`/`Add<T>`"): a `readonly struct { EcsWorld World; Entity Entity; }` forwarding `Has`, `Get`, `Write`, `TryGet`, `Add`, `Set`, `Remove`, `Despawn` and `IsAlive`. It isn't unmanaged, so it can't be stored in a component; components store `Entity`.

**Misuse:** `Entity.Null` throws `ArgumentException`; a stale entity (dead, or from another world) throws Core's `StaleHandleException`; a pending entity throws `InvalidOperationException`; `Get`/`Write` of a component the entity lacks throws `InvalidOperationException` naming entity and component.

## 3. Components and the registry

**Rules.**

- A component is an `unmanaged` struct that implements `IComponent`, normally through the generator (section 9). Every generic world member is constrained `where T : unmanaged, IComponent`, so using a non-component is a compile error. Components hold no managed references: assets and strings are referenced by handle ([Architecture 3.6](../03-architecture.md#36-world-model)). Component structs are plain mutable data with public fields, documented as such ([Coding standards](../07-coding-standards.md#general-c)).
- No generic component types in M1: the stable id is a hash of a full type name.
- Size at most 1,024 bytes and alignment at most 64 (the chunk alignment); registration throws `ArgumentException` otherwise. An archetype's row (8-byte entity id plus padded columns) must fit 16 rows in a chunk (4 KiB at the default size); the structural change that would create a wider archetype throws `InvalidOperationException`. Bigger data lives outside the world behind a handle (no side pool in M1, S5 table).
- **Tags** are components with no instance fields (C# gives them size 1). They get no column, only a mask bit. `Has`, `Add`, `Set`, `Remove` and query `With`/`Without` work on them; `Get`, `Write`, `TryGet` and chunk `Read`/`Write` throw `InvalidOperationException`.

```csharp
public interface IComponent
{
    static abstract ComponentInfo Info { get; }                  // generated; no reflection, AOT-safe
}

public readonly record struct ComponentInfo
{
    public required StableComponentId StableId { get; init; }
    public required uint SchemaVersion { get; init; }            // >= 1, from [Component(SchemaVersion = n)]
    public required string FullName { get; init; }              // metadata name: "MyGame.Velocity", "MyGame.Outer+Inner"
    public required bool IsTag { get; init; }
}

public readonly record struct StableComponentId(ulong Value)
{
    public static StableComponentId FromFullName(ReadOnlySpan<char> fullName);   // FNV-1a 64 over the UTF-8 bytes
}

public readonly record struct ComponentId                       // dense index, valid only in the registry that issued it
{
    public int Index { get; }
}

public readonly record struct ComponentType(ComponentId Id, ComponentInfo Info, int Size, int Alignment);

public sealed class ComponentRegistry
{
    public int Count { get; }
    public int Capacity { get; }                                 // WorldOptions.MaxComponentTypes
    public ComponentId Register<T>() where T : unmanaged, IComponent;          // idempotent
    public bool TryGetId<T>(out ComponentId id) where T : unmanaged, IComponent;
    public bool TryGetId(StableComponentId stableId, out ComponentId id);
    public ComponentType GetComponentType(ComponentId id);       // ArgumentOutOfRangeException if not issued here
}
```

- **Stable ids** ([Architecture 3.21](../03-architecture.md#321-hot-reload-readiness): hashed full type names plus a schema version). `StableId` is 64-bit FNV-1a over the UTF-8 metadata name; `SchemaVersion` is a separate field, so a type keeps its identity when its layout changes and M2's serialisers can migrate data. FNV-1a because the generator (netstandard2.0) and the runtime must compute identical values with no dependency: `XxHash64` lives in the System.IO.Hashing package, outside the shared framework, and `string.GetHashCode` is randomised per process. A test pins known vectors in both. Registering a type whose `StableId` is already taken by a different `FullName` throws `InvalidOperationException` naming both (a collision; about n²/2⁶⁵ likely for n types). Re-registering the same name with another schema version or tag-ness throws too (migration is a post-v1 hot-reload feature).
- **Registration** happens on the first use of `T` by any world member, or explicitly through `Register<T>()` at setup. The registry computes `Size` and `Alignment` from `T` itself, so generated code can't get them wrong. Dense ids follow registration order and are never persisted; serialisation uses `StableId` (M2). Registering more than `MaxComponentTypes` types throws `InvalidOperationException` naming the option.
- **Fast `T -> ComponentId` without static mutable non-primitive state.** World keeps `internal static class ComponentOrdinal<T> { internal static int Value; }`: a process-wide ordinal, assigned once on first use (`Interlocked.CompareExchange` from a static `int` counter) and never changed. Each registry maps ordinal to dense id through an instance-owned `int[]` that grows only when a type is registered. A lookup is one static load, one bounds-checked array read and one branch.
  - It satisfies the architecture test: `StaticStateRule` flags a static field only if it is neither `const` nor `readonly` and its type is not a primitive, enum or pointer (`tests/BrutalEngine.Architecture.Tests/StaticStateRule.cs:33`, `:47-51`); both fields are `int`. It also meets the intent of 3.21: an `int` can't reference a game type, the generic instantiation over a collectible game type unloads with its `AssemblyLoadContext`, and the authoritative mapping lives in the instance-owned registry, which can be cleared and rebuilt.
  - S5's variant, a generated static slot holding the *per-world dense index* and re-stamped by the registry (`DESIGN-NOTES.md:57`), is rejected: with two worlds (an editor world and a play world, or xUnit test classes running in parallel) one slot can hold only one world's index, so the other world would silently read the wrong column. One process-wide registry is rejected too: 3.21 wants registries instance-owned.
- **Masks.** An archetype's signature is its sorted `ComponentId` list plus a bitmask `MaxComponentTypes` bits wide (four `ulong`s by default, S5's 256-bit mask, `DESIGN-NOTES.md:58`, `Ecs.cs:40`). Query matching is one AND per 64 bits.

## 4. Storage

An **archetype** is a unique set of component ids. It owns a column table (component, offset, size), its per-chunk capacity, a chunk list, per-chunk change metadata and its add/remove edges. Archetypes are append-only: they are never destroyed while the world lives, which incremental query caches rely on (section 5); an empty archetype keeps its metadata but no chunks.

**Chunk layout** (one block of `ChunkBytes`, 64-byte aligned, S5's layout, `DESIGN-NOTES.md:13-21`):

```
[Entity ids   : cap x 8 B]           offset 0
[column 0     : cap x size0]         aligned to max(16, alignment0)
...
[column n-1   : cap x size(n-1)]     tags have no column
```

- `cap` is the largest row count whose aligned layout fits the chunk, computed once per archetype; column offsets are per archetype, not per chunk.
- **Change metadata lives outside the chunk:** per archetype, an unmanaged array of `{ byte* Data; int Count; }` per chunk and two `uint` arrays of chunk x column ticks, `Changed` and `Added`. S5 kept the versions at the end of each chunk (`DESIGN-NOTES.md:18`); outside, a filtered query or the transform system skips unchanged chunks by reading compact arrays instead of touching each 64 KiB block.
- **Chunk pool:** a world-owned free list of whole blocks, allocated with `INativeAllocator.Allocate(ChunkBytes, 64)`. `HeapAllocator` uses `NativeMemory.AlignedAlloc` ([memory](memory.md#key-types)); BRUTAL's `Mem` has no aligned allocation (`DESIGN-NOTES.md:129`). An emptied chunk goes back to the pool, not the allocator, so steady spawn/despawn churn makes no allocator calls; `TrimMemory()` releases pooled blocks.
- **Dense invariant** (`DESIGN-NOTES.md:22`): every chunk but an archetype's last is full, and rows `0..Count-1` are live, so iteration never checks liveness. Removing a row swap-moves the archetype's last row into the hole, updates that entity's record, and returns an emptied last chunk to the pool.
- **Moves** (`Add`/`Remove`/`Set` of a new component): append a row to the target archetype, copy the shared columns (one copy per column), write the new value, swap-remove from the source. Stamps: in the target chunk `Changed` for every column (data arrived) and `Added` for the added component; in a source chunk that received the moved last row, `Changed` for every column (`DESIGN-NOTES.md:103`).
- **Edges:** per archetype, add and remove edges map a component id to the neighbouring archetype through a small open-addressing table in unmanaged memory. S5 used a dense 256-entry `int` table per archetype, 3 KB each (`DESIGN-NOTES.md:158`, `Ecs.cs:69`). A miss looks the signature up in a world-level map, creating the archetype if needed.
- **Allocation:** creating an archetype allocates managed metadata once per new signature. Operations that only reuse existing archetypes allocate nothing managed.
- **Determinism:** archetype indices follow creation order; chunk and row order follow the operations, never addresses or hash order. Internal hash maps use fixed seeds and are never iterated to produce an order.

## 5. Queries

```csharp
public sealed class QueryBuilder                                 // from EcsWorld.Query() or SystemBuilder.Query(); setup only
{
    public QueryBuilder Read<T>() where T : unmanaged, IComponent;            // required, read access
    public QueryBuilder Write<T>() where T : unmanaged, IComponent;           // required, write access
    public QueryBuilder With<T>() where T : unmanaged, IComponent;            // required, no data access (tags)
    public QueryBuilder Without<T>() where T : unmanaged, IComponent;
    public QueryBuilder OptionalRead<T>() where T : unmanaged, IComponent;
    public QueryBuilder OptionalWrite<T>() where T : unmanaged, IComponent;
    public QueryBuilder Changed<T>() where T : unmanaged, IComponent;         // required; chunk filter
    public QueryBuilder Added<T>() where T : unmanaged, IComponent;           // required; chunk filter
    public Query Build();
}

public sealed class Query : IDisposable                          // owned by its world; Dispose releases it early
{
    public EcsWorld World { get; }
    public uint LastRunTick { get; }                             // meaningful once HasRun is true
    public bool HasRun { get; }                                  // a full enumeration has completed
    public ChunkEnumerator GetEnumerator();                      // foreach (ChunkView chunk in query)
    public int CountEntities();                                  // matching rows, ignoring Changed/Added filters
    public void Dispose();
}

public ref struct ChunkEnumerator
{
    public ChunkView Current { get; }
    public bool MoveNext();
    public void Dispose();                                       // foreach calls it; ends the iteration guard
}

public readonly ref struct ChunkView
{
    public int Count { get; }
    public ReadOnlySpan<Entity> Entities { get; }
    public ReadOnlySpan<T> Read<T>() where T : unmanaged, IComponent;  // declared Read, Write or optional and present
    public Span<T> Write<T>() where T : unmanaged, IComponent;         // declared Write; stamps T's Changed tick
    public bool Has<T>() where T : unmanaged, IComponent;               // for optional terms
    public bool Changed<T>() where T : unmanaged, IComponent;           // newer than the query's LastRunTick
    public bool Added<T>() where T : unmanaged, IComponent;
}
```

`EcsWorld.Query()` returns a new `QueryBuilder`. The primary shape is the chunk loop (S5 shape 1, `DESIGN-NOTES.md:64-66`), which runs at span speed with no allocation:

```csharp
Query movers = world.Query().Write<LocalTransform2D>().Read<Velocity>().Without<Frozen>().Build();   // setup

foreach (ChunkView chunk in movers)                              // per frame
{
    Span<LocalTransform2D> transforms = chunk.Write<LocalTransform2D>();
    ReadOnlySpan<Velocity> velocities = chunk.Read<Velocity>();
    for (int i = 0; i < transforms.Length; i++)
    {
        transforms[i].Position += velocities[i].Value * dt;
    }
}
```

- **Shapes.** Chunk loops for hand-written code, and generated system loops for the per-entity form (section 9), which compile to the same chunk loop (S5's struct-callback shape measured within noise of span loops, `DESIGN-NOTES.md:67`). There is **no per-entity enumerator**: S5's `ref` pair enumerator took 1.31 ms for 1M x 2 and failed the 1 ms target (`DESIGN-NOTES.md:69`). There is no public struct-callback interface either: generated code calls the user's method directly.
- **Matching:** an archetype matches when its mask contains every required term (`Read`, `Write`, `With`, `Changed`, `Added`) and none of the `Without` terms.
- **Caching:** a query keeps an unmanaged list of matched archetypes with the column offset of each term per archetype (`DESIGN-NOTES.md:75-76`). Enumeration first scans only the archetypes created since the last refresh, because archetypes are append-only, so it never allocates managed memory.
- **Declared access is enforced:** `Read<T>` on a type the query didn't declare, or `Write<T>` on one not declared `Write`, throws `InvalidOperationException` (one mask test). So a query's terms are exactly what it touches, which is what schedule conflict detection relies on (section 7). An absent optional column: `Has<T>` is false and `Read`/`Write` throw.
- **Iteration guard:** while any chunk enumerator of a world is live, structural changes to that world throw `InvalidOperationException`. Nested enumeration of several queries is allowed. `foreach` always disposes the enumerator; code that calls `GetEnumerator` by hand must dispose it.

**Change detection** (ADR-0006: per-component, per-chunk version counters):

- `EcsWorld.ChangeTick` is a `uint` that starts at 1 and only ever advances by one. It advances (a) before each system runs (section 7), (b) at the start of every `CommandBuffer.Playback()`, so every sync point (the implicit one at the end of each `Run` included) applies its commands with a tick no system has run at, (c) right after a standalone query's enumeration completes (below), and (d) on `AdvanceChangeTick()`. Writes stamp the current tick: `EcsWorld.Write`/`Set` stamp the entity's chunk, and `ChunkView.Write<T>()` stamps T's column when called, whether or not rows are then written. `Read` never stamps (`DESIGN-NOTES.md:66`, `:101`).
- `Changed<T>` and `Added<T>` filter **chunks**: a chunk is yielded when T's tick there is newer than the query's `LastRunTick`. Several filters must all hold. Consumers re-check rows themselves; the granularity is one chunk (about 2,700 rows when a row is 24 bytes and chunks are 64 KiB). The `Added` tick is separate from `Changed` (`DESIGN-NOTES.md:104`), so physics and render extract can tell new rows from modified ones.
- **Standalone queries** (built from `EcsWorld.Query()`): when an enumeration runs to completion, `LastRunTick` becomes the current `ChangeTick` and the world then advances `ChangeTick`. Writes made during the loop stamp a tick that isn't newer than `LastRunTick`, so the query doesn't see its own loop's writes; any write after the loop stamps a later tick and is seen next time. A loop that breaks early changes nothing and sees the same chunks again.
- **System-owned queries** use their system's last-run tick instead, which is the `ChangeTick` current when the system's `Run` returns (section 7). A system therefore never sees its own writes as changes, and sees every write stamped after it finished: by later systems, which run at later ticks, and by sync points, which play back at later ticks. Example: entities that a `PostUpdate` system spawns through its buffer are placed at the end-of-`Run` sync point, after `TransformSystem` ran, so their `Added` ticks are newer than its last run and it writes their `WorldTransform2D` on its next run.
- **First run:** until a query completes its first enumeration, or a system completes its first run, its `Changed`/`Added` filters match every chunk and `ChunkView.Changed<T>()`/`Added<T>()` return true: everything that exists is new to it. `Query.HasRun` and `SystemContext.IsFirstRun` expose this. It is a flag, not a baseline tick, so it can't be defeated by wrap-around.
- **Wrap-safe comparison:** "newer" means `(int)(tick - since) > 0` (`DESIGN-NOTES.md:102`, `Ecs.cs:491-492`), valid while ages stay under 2³¹ ticks. To keep them there, whenever `ChangeTick` crosses a multiple of 2³⁰ the world rewrites every chunk tick, every query's `LastRunTick` and every system's last-run tick older than `ChangeTick - 2³⁰` to that value: one pass over chunk x column ticks, queries and systems, no allocation. At 100 system runs per frame and 60 frames per second that is about every 50 hours, and at 1,000 runs and 240 frames per second about every 75 minutes; tests reach it through an internal seam that sets the tick.

## 6. Command buffers

```csharp
public sealed class CommandBuffer : IDisposable
{
    public CommandBuffer(EcsWorld world, int initialCapacityBytes = 4096);   // stream from the world's allocator
    public EcsWorld World { get; }
    public int CommandCount { get; }
    public int ByteCount { get; }
    public int LastPlaybackSkippedCount { get; }                 // commands dropped for dead targets

    public Entity Spawn();
    public Entity Spawn<T1>(in T1 c1) where T1 : unmanaged, IComponent;
    // ... up to Spawn<T1, T2, T3, T4>
    public void SpawnBatch<T1>(ReadOnlySpan<T1> c1, Span<Entity> entities) where T1 : unmanaged, IComponent;
    public void SpawnBatch<T1, T2>(ReadOnlySpan<T1> c1, ReadOnlySpan<T2> c2, Span<Entity> entities)
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent;
    // ... up to four columns; every span must have entities.Length elements (ArgumentException)
    public void Despawn(Entity entity);
    public void Set<T>(Entity entity, in T value) where T : unmanaged, IComponent;   // add or replace
    public void Remove<T>(Entity entity) where T : unmanaged, IComponent;            // no-op if absent
    public void SetParent(Entity child, Entity parent);          // section 8
    public void ClearParent(Entity child);

    public void Playback();                                      // apply to World, then reset (capacity kept)
    public void Clear();                                         // discard, releasing reserved entities
    public void Dispose();                                       // Clear, then free the stream
}
```

- **Record format:** an unmanaged byte stream of 8-byte-aligned records `[header: op, flags, component count, payload bytes][target Entity][per component: ComponentId + value bytes]` (S5's stream, `DESIGN-NOTES.md:82`). A `SpawnBatch` record holds the entity range and each column's values contiguously. Values are copied at record time. The stream and the playback scratch keep their capacity after `Playback`/`Clear`, so steady-state recording and playback allocate no managed memory (`DESIGN-NOTES.md:83`).
- **Reservation at record time:** `Spawn*` reserves the entity immediately (pending, section 2) and returns it, so later commands in this or a later-played buffer can target it, and it can be stored in components before it exists (`DESIGN-NOTES.md:84`).
- **Archetypes are resolved at playback**, never at record time (`DESIGN-NOTES.md:96`), so recording doesn't mutate the world's archetype tables. That is what lets recording move to job threads in M2 without an API change.
- **Playback order** is recording order, with two batching rules that don't change the result:
  - A run of consecutive spawn records with the same component set becomes one batch: rows are reserved a chunk at a time and each column is copied as contiguous runs (`DESIGN-NOTES.md:88-89`). The archetype lookup is cached across the run.
  - A run of consecutive `Despawn` records is gathered into unmanaged scratch, resolved once, with dead and duplicate targets dropped and descendants appended (section 8), sorted by archetype, then chunk and row descending, and removed. Swap-removes then rarely move a row that is about to be deleted (`DESIGN-NOTES.md:90`). This changes only the positions of surviving rows, deterministically for a given buffer.
  - Each command checks its target's liveness once (S5 checked twice, `DESIGN-NOTES.md:91`).
- **Dead targets are skipped**, not thrown, and counted in `LastPlaybackSkippedCount` (`DESIGN-NOTES.md:85`): another system may legitimately have despawned the target. `Set` adds or replaces and `Remove` of an absent component is a no-op, for the same reason.
- **Misuse at playback throws `InvalidOperationException`:** a target that is still pending because another buffer reserved it and hasn't been played back (wrong playback order), or a hierarchy cycle or depth overflow (section 8). Commands before it stay applied; the rest of the buffer is discarded and its reservations released, so the world stays consistent at command granularity.
- `Clear` and `Dispose` of an unplayed buffer free its reserved entities with advanced generations, so handles to them are stale.
- **Thread rules.** M1: recording and playback happen on the owner thread (checked), and a buffer is not thread-safe. M2: systems record from job threads, each into its own buffer (`DESIGN-NOTES.md:94`), and reservation becomes lock-free: an atomic cursor over a snapshot of the free queue plus atomic growth of fresh indices (`DESIGN-NOTES.md:95`). Playback stays on the owner thread at sync points, in system order.
- **Budget:** 100,000 spawns plus 100,000 despawns per frame through a buffer in under 5 ms on the reference machine (ADR-0006), including **shuffled** despawn order and at p99, the cases S5 failed at about 6 ms (`DESIGN-NOTES.md:87`). The benchmark issue gates it; a miss is reported before the API is frozen.

## 7. Schedules

```csharp
public interface ISystem
{
    void Configure(SystemBuilder builder);                       // once, when its schedule is built
    void Run(in SystemContext context);
}

public sealed class SystemBuilder
{
    public QueryBuilder Query();                                 // the query belongs to the system and declares its access
    public SystemBuilder Reads<T>() where T : unmanaged, IComponent;    // random access: EcsWorld.Get/TryGet/Has
    public SystemBuilder Writes<T>() where T : unmanaged, IComponent;   // random access: EcsWorld.Write/Set
    public SystemBuilder Exclusive();                            // direct structural calls; conflicts with every system
}

public readonly struct SystemContext
{
    public EcsWorld World { get; }
    public CommandBuffer Commands { get; }                       // this system's buffer, played at the next sync point
    public TimeSpan Delta { get; }
    public float DeltaSeconds { get; }
    public uint LastRunTick { get; }                             // ChangeTick when the previous run returned
    public bool IsFirstRun { get; }                              // filters then match every chunk (section 5)
}

public readonly record struct ScheduleLabel(string Name)
{
    public static ScheduleLabel FixedUpdate { get; }
    public static ScheduleLabel Update { get; }
    public static ScheduleLabel PostUpdate { get; }
}

public sealed record SystemOptions
{
    public required string Name { get; init; }                  // unique within the schedule
    public IReadOnlyList<string> After { get; init; } = [];
    public IReadOnlyList<string> Before { get; init; } = [];
}

public sealed class Schedule
{
    public ScheduleLabel Label { get; }
    public int SystemCount { get; }
    public void Add(ISystem system, SystemOptions options);
    public void AddSyncPoint(SystemOptions options);             // ordered like a system
    public ScheduleReport Build();                               // Run builds first if needed
    public void Run(TimeSpan delta);
    public int GetTimings(Span<SystemTiming> destination);       // last run, in execution order
}

public sealed class ScheduleCollection
{
    public Schedule this[ScheduleLabel label] { get; }           // ArgumentException if absent
    public Schedule Create(ScheduleLabel label);                 // ArgumentException if it exists
    public bool TryGet(ScheduleLabel label, [NotNullWhen(true)] out Schedule? schedule);
}

public sealed record ScheduleReport(IReadOnlyList<string> Order, IReadOnlyList<SystemConflict> UnorderedConflicts);
public readonly record struct SystemConflict(string First, string Second, string ComponentName);
public readonly record struct SystemTiming(string Name, TimeSpan Elapsed);
```

On `EcsWorld`: `public void RunSchedule(ScheduleLabel label, TimeSpan delta)` runs `Schedules[label]`. A new world has the three empty schedules `FixedUpdate`, `Update` and `PostUpdate` ([Architecture 3.2](../03-architecture.md#32-application-host-and-main-loop) runs them; the Runtime host decides when), and installs the transform system in `PostUpdate` (section 8).

- **Ordering:** `Build` topologically sorts the systems and sync points by `After`/`Before`; ties go to insertion order. An unknown name throws `ArgumentException`, a duplicate name throws at `Add`, and a cycle throws `InvalidOperationException` listing it. Adding after a build marks the schedule for rebuild on the next `Run`. `Build` calls each system's `Configure`, creates its command buffer, and allocates; `Run` doesn't.
- **Access and conflicts:** a system's access is the union of its queries' terms, `Reads`, `Writes` and `Exclusive`. Two systems conflict when one writes a component the other reads or writes, or either is exclusive. `ScheduleReport.UnorderedConflicts` lists conflicting pairs with no ordering path between them, for the log and the debug overlay. That is not an error: such a pair runs in insertion order in M1, and the M2 executor never runs conflicting systems concurrently and keeps the same order, so results don't depend on threading.
- **Enforcement while a schedule runs:** the world knows the running system. Random access outside its `Reads`/`Writes`/query terms throws `InvalidOperationException`, and so does a direct structural call from a non-exclusive system (use `context.Commands`).
- **Sync points:** every system gets its own command buffer. A sync point plays back, in system order, the buffers of all systems ordered before it that haven't been played. The end of every `Run` is an implicit sync point, and so is the start of each exclusive system.
- **Ticks:** before each system runs, and at the start of each buffer's playback at a sync point, the world advances `ChangeTick`. When a system's `Run` returns, its last-run tick becomes the current `ChangeTick`; `context.LastRunTick` is that value from the previous run, and its queries filter against it (first runs: section 5).
- **Timings:** each system's run is timed with `WorldOptions.Clock.GetTimestamp()`, for the debug overlay's per-system CPU panel. No allocation.
- **Exceptions** thrown by a system propagate unchanged out of `Run`; the run's unplayed command buffers are cleared (reservations released) and the schedule stays usable. Structural state stays consistent, because non-exclusive systems change structure only through buffers.
- **Determinism:** for the same initial world, schedule and inputs, a run produces the same world: system order is fixed as above, buffers play back in system order and then recording order, entity indices are reused FIFO, and iteration order follows archetype creation, chunk and row order. Nothing depends on addresses, hash order or wall-clock time.
- **Parallel execution in M2**, behind this API: the executor runs waves of ready, mutually non-conflicting systems on BRUTAL's `JobScheduler`, one preallocated `IJob` per system (`Brutal.Concurrency/Jobs/IJob.cs:6-9`), with `Queue` (`Brutal.Concurrency/Jobs/JobScheduler.cs:65-74`), `ExecuteJobs` (`Brutal.Concurrency/Jobs/JobScheduler.cs:89`) and `Wait` (`Brutal.Concurrency/Jobs/JobScheduler.cs:79-83`) per wave (S5's proposal, `DESIGN-NOTES.md:146`). `Queue` throws while jobs run (`Brutal.Concurrency/Jobs/JobScheduler.cs:67-68`), which is why the executor works in waves, and `Wait` busy-spins. `JobRunner` doesn't catch a job's exception (`Brutal.Concurrency/Jobs/JobRunner.cs:281-308`), so the executor catches inside each job and rethrows on the owner thread. Chunk-parallel iteration inside one system splits slices of at most 16k rows (`DESIGN-NOTES.md:119`). `ISystem`, `SystemBuilder` and `SystemContext` already contain nothing thread-specific.

## 8. Hierarchy and transforms

### Hierarchy

```csharp
namespace BrutalEngine.World.Hierarchy;

public readonly struct Parent : IComponent                       // engine-managed
{
    public Entity Value { get; }
}

public readonly struct Children : IComponent                     // engine-managed; only internal fields (the slab range)
{
}

// On EcsWorld (namespace BrutalEngine.World):
public void SetParent(Entity child, Entity parent);              // re-parents if child already has a parent
public void ClearParent(Entity child);                           // no-op without a parent
public ReadOnlySpan<Entity> GetChildren(Entity parent);          // insertion order; empty if none
```

- **Storage:** `Parent` is an ordinary component. Children lists live **outside** the chunks, because variable-length data would break fixed row sizes (`DESIGN-NOTES.md:109`): a world-owned unmanaged slab of `Entity` arrays in power-of-two capacity classes with a free list per class. The internal fields of `Children` locate a parent's range. The span from `GetChildren` is invalidated by any structural change.
- **Both sides change together.** `SetParent`, `ClearParent` and despawns update `Parent` and the parent's children list in one operation, directly or at command-buffer playback (`DESIGN-NOTES.md:110`). A parent gains `Children` with its first child and loses it with its last (a structural change). Direct `Add`, `Set`, `Remove` or `Write` of `Parent` or `Children` throws `InvalidOperationException`; use the hierarchy members.
- **Order:** children keep insertion order; removal preserves the order of the rest. Deterministic.
- **Misuse** throws `InvalidOperationException`: parenting an entity to itself or to one of its descendants (found by walking up from `parent`, at most `MaxHierarchyDepth` steps), or a result deeper than `MaxHierarchyDepth`.
- **Recursive despawn:** despawning an entity despawns its descendants, children before parents, in children order, using unmanaged scratch (`DESIGN-NOTES.md:114`). In command-buffer playback the descendants join the sorted despawn run (section 6). To keep the children, `ClearParent` them first. Despawning a child removes it from its parent's list.

### 2D transforms

```csharp
namespace BrutalEngine.World.Transforms;

public struct LocalTransform2D : IComponent                      // mutable data
{
    public Vector2 Position;                                     // System.Numerics
    public float Rotation;                                       // radians, as Matrix3x2.CreateRotation
    public Vector2 Scale;
    public static LocalTransform2D Identity { get; }
    public readonly Matrix3x2 ToMatrix();                        // scale, then rotation, then translation
}

public struct WorldTransform2D : IComponent                      // written by the transform system
{
    public Matrix3x2 Matrix;
}

public sealed class TransformSystem : ISystem { /* Configure and Run, below */ }
```

`Vector2` (8 bytes) and `Matrix3x2` (24 bytes) are the BCL's `System.Numerics` types, so the transform components add no package dependency and are unmanaged. `Matrix3x2` uses the row-vector convention: `Vector2.Transform(v, a * b)` applies `a` first (observed with a scratch run: scale by 2 then translate by 10 maps (1, 0) to (12, 0), the reverse order to (22, 0)). `ToMatrix()` is `Matrix3x2.CreateScale(Scale) * Matrix3x2.CreateRotation(Rotation) * Matrix3x2.CreateTranslation(Position)`.

**Propagation.** The world installs one `TransformSystem` as the last system of `PostUpdate` (name `"TransformSystem"`); games may add more instances, for example in `FixedUpdate` when physics needs world transforms within a tick. Each run:

1. **Roots** (`Read<LocalTransform2D>`, `Write<WorldTransform2D>`, `Without<Parent>`): a chunk loop computing `World = Local.ToMatrix()`, skipping chunks whose `LocalTransform2D` `Changed` and `Added` ticks are not newer than the system's last run. A scene of flat, moving sprites (the SpriteStress case) only takes this path.
2. **Descendants:** for each root with `Children`, a depth-first walk in children order computing `World(child) = Local(child).ToMatrix() * World(parent)`. In M1 every descendant is recomputed every run, through record lookups.

An entity missing `LocalTransform2D` or `WorldTransform2D` is skipped, and its children use the identity as their parent matrix. Games add both components (for example with `Spawn<LocalTransform2D, WorldTransform2D>`); the system never adds components itself. Interpolation between fixed steps belongs to the Runtime and renderer ([Architecture 3.2](../03-architecture.md#32-application-host-and-main-loop)), not the world.

S5 proposed depth-ordered propagation with chunk skipping at every level (`DESIGN-NOTES.md:112`). M1 skips unchanged chunks only for roots: per-level lists or depth tags add bookkeeping to every hierarchy change, and the benchmark issue measures a 10,000-node hierarchy first. If it misses its budget, per-level lists replace the walk internally, with no API change. Hand-written SIMD for the root loop (`DESIGN-NOTES.md:72`, `:150`) waits for the same measurement.

## 9. Generated code

`src/BrutalEngine.Generators`: an incremental Roslyn generator targeting netstandard2.0 with `Microsoft.CodeAnalysis.CSharp` 5.0.0 ([Repository layout](../04-repo-layout.md#shared-build-settings), `docs/04-repo-layout.md:96`). It is packed into the `BrutalEngine.World` package under `analyzers/dotnet/cs`, so referencing World brings it, and it is not a package of its own. `src/Directory.Build.props` says Roslyn components "aren't packed yet" (`src/Directory.Build.props:4-8`); the generator issue updates that comment and packs it from `BrutalEngine.World.csproj`. Repository projects reference it with `OutputItemType="Analyzer"`.

The attributes are public World types: `ComponentAttribute` (`SchemaVersion`, default 1), `EcsSystemAttribute`, and the generic method attributes `WithAttribute<T>`, `WithoutAttribute<T>`, `ChangedAttribute<T>` and `AddedAttribute<T>`.

```csharp
using System.Numerics;
using BrutalEngine.World;
using BrutalEngine.World.Transforms;

[Component]
public partial struct Velocity { public Vector2 Value; }

[Component]
public partial struct Frozen { }                                 // a tag

[EcsSystem]
public sealed partial class MoveSystem
{
    public float Speed = 1f;

    [Without<Frozen>]
    private void Execute(in SystemContext context, ref LocalTransform2D transform, in Velocity velocity)
        => transform.Position += velocity.Value * (Speed * context.DeltaSeconds);
}

// setup: world.Schedules[ScheduleLabel.FixedUpdate].Add(new MoveSystem(), new SystemOptions { Name = "Move" });
```

What it emits:

```csharp
partial struct Velocity : IComponent
{
    static ComponentInfo IComponent.Info => new()
    {
        StableId = new StableComponentId(0x...UL),               // FNV-1a of "MyGame.Velocity", computed at compile time
        SchemaVersion = 1,
        FullName = "MyGame.Velocity",
        IsTag = false,
    };
}

partial class MoveSystem : ISystem
{
    private Query _query = null!;

    void ISystem.Configure(SystemBuilder builder)
    {
        _query = builder.Query().Write<LocalTransform2D>().Read<Velocity>().Without<Frozen>().Build();
        OnConfigure(builder);                                    // optional user partial: extra Reads/Writes
    }

    partial void OnConfigure(SystemBuilder builder);

    void ISystem.Run(in SystemContext context)
    {
        foreach (ChunkView chunk in _query)
        {
            Span<LocalTransform2D> p0 = chunk.Write<LocalTransform2D>();
            ReadOnlySpan<Velocity> p1 = chunk.Read<Velocity>();
            for (int i = 0; i < p0.Length; i++)
            {
                Execute(in context, ref p0[i], in p1[i]);
            }
        }
    }
}
```

- **`Execute` parameters:** an optional leading `in SystemContext`, an optional `Entity`, then 1 to 8 components: `ref` declares `Write`, `in` declares `Read`. Filters come from the method attributes. One `Execute` per system in M1; a system that needs several queries implements `ISystem` by hand.
- **Diagnostics** (errors; `BE2xxx`, beside the analyzers' `BE1xxx` in [Coding standards](../07-coding-standards.md#analyzer-rules-we-own-brutalengineanalyzers-ids-be1xxx)): BE2001 `[Component]` type is not a `partial struct`; BE2002 it isn't unmanaged; BE2003 it is generic or nested in a generic type; BE2004 two components in the compilation share a stable id; BE2005 `SchemaVersion` is 0; BE2006 an `[EcsSystem]` type is not a `sealed partial class` with exactly one `Execute`; BE2007 an invalid `Execute` parameter (by-value or duplicate component, a tag as a parameter, more than 8 components). The registry still validates at runtime, because tests and engine types may implement `IComponent` by hand.
- **Engine components** (`Parent`, `Children`, the transforms) implement `IComponent` by hand, so World doesn't need its own generator to build; a test checks their ids against `StableComponentId.FromFullName`.
- **AOT safety:** no reflection, no `MakeGenericType`, no runtime code generation. Static abstract members bind at compile time and every generic instantiation is over a value type the compiler sees. S5 never published with NativeAOT (`DESIGN-NOTES.md:165`), so the system-generation issue adds a world scenario to `tests/BrutalEngine.Aot.Smoke`, which CI publishes and runs on all three RIDs.
- **M2:** serialisers (scenes and prefabs) and an assembly-level list of components for loading by stable id.

## 10. Threading and allocation

- **Owner thread:** the thread that constructs the `EcsWorld`. The world captures Core's `ThreadAffinity.CurrentThread()`, and every member of `EcsWorld`, `ComponentRegistry`, `Query`, `CommandBuffer`, `Schedule` and `ScheduleCollection` calls `ThrowIfNotCurrentThread(apiName)`, so a call from another thread throws `WrongThreadException` naming the API ([Architecture 3.2](../03-architecture.md#threading-model)). Both types are specified in the [platform note](platform.md#9-threading) and added to Core by its issue P1. The check runs in every build, as in the Platform note: it is one integer comparison.
- `ChunkView` and the spans it returns are `ref struct`s and aren't checked; they can't leave the stack. In M2, jobs receive chunk slices from the executor, and recording into command buffers and read-only lookups inside a running schedule become allowed from job threads. Loosening a check doesn't break callers.
- **Steady-state frames allocate zero managed bytes:** direct component access, structural changes that reuse existing archetypes, query enumeration, command-buffer recording and playback, `Schedule.Run`, the transform system and timings. Unmanaged growth (entity table, chunk pool, query caches, command streams, children slab, playback scratch) goes through `INativeAllocator`, never the GC.
- **Allocating, at setup only:** creating worlds, registering components, creating archetypes (once per new signature), queries, command buffers, schedules and systems, and `Schedule.Build`.
- **Measuring:** single-threaded M1 tests use `GC.GetAllocatedBytesForCurrentThread()`. Once jobs run (M2), allocation tests count the whole process with `GC.GetTotalAllocatedBytes(precise: true)` (`DESIGN-NOTES.md:120`), and because xUnit runs test classes in parallel, those tests go in a collection with parallelisation disabled; otherwise other tests' allocations are counted.

## 11. Failure modes

The M1 World API has no expected runtime failures besides lookups, which use `bool`/`Try*`. Everything else is misuse and throws immediately ([errors](errors.md#purpose), [Architecture 3.5](../03-architecture.md#35-error-handling)). `Result` appears with serialisation in M2.

| Situation | Behaviour |
|-----------|-----------|
| Invalid `WorldOptions`, `SystemOptions` or span lengths | `ArgumentNullException` / `ArgumentOutOfRangeException` / `ArgumentException` naming the value |
| `Entity.Null` passed to a member | `ArgumentException` |
| Stale entity (dead, retired or from another world) in a direct call | `StaleHandleException` |
| Pending entity in a direct call | `InvalidOperationException` |
| `Get`/`Write` of a missing component; data access to a tag | `InvalidOperationException` naming entity and component |
| `Add` of a component the entity already has | `InvalidOperationException` (use `Set`) |
| Structural change during a live enumeration, or from a non-exclusive system | `InvalidOperationException` |
| Undeclared access (chunk view or random access) | `InvalidOperationException` naming system or query and component |
| Component over 1,024 bytes or aligned over 64; archetype row too wide; registry full; stable-id collision; re-registration with another schema | `ArgumentException` / `InvalidOperationException` at registration or at the structural change |
| `MaxEntities` exhausted | `InvalidOperationException` |
| Hierarchy cycle or depth over `MaxHierarchyDepth`; direct write of `Parent`/`Children` | `InvalidOperationException` |
| Schedule: duplicate or unknown system name, ordering cycle | `ArgumentException` / `InvalidOperationException` |
| Playback of a command whose target another unplayed buffer reserved | `InvalidOperationException`; earlier commands stay applied, the rest are discarded |
| Command targeting a dead entity | Skipped and counted (`LastPlaybackSkippedCount`) |
| A system throws | Propagates out of `Schedule.Run`; the run's unplayed buffers are cleared |
| Wrong thread | `WrongThreadException` |
| Any member after `Dispose` | `ObjectDisposedException` |
| Out of native memory | `OutOfMemoryException` from the allocator |

## 12. Testing

`tests/BrutalEngine.World.Tests` (xUnit v3, CsCheck), `tests/BrutalEngine.Generators.Tests`, and `tests/BrutalEngine.Benchmarks` ([Testing strategy](../05-testing-strategy.md#test-layers)):

- **Unit tests** for every rule and every row of the failure table, each world created over a `TrackingAllocator` that is checked with `ThrowIfLeaks()` after `Dispose` (leak tests come free with every test).
- **Reference-model property tests** (CsCheck), as S5 did with 5 seeds x 4,000 steps (`DESIGN-NOTES.md:162`): random sequences of spawn, despawn, add, set, remove, get and stale access against a dictionary model, checking every entity and component value after each step; the same through command buffers (batched spawns, sorted despawns, dead targets, `Clear`); random `SetParent`/`ClearParent`/despawn against a parent-map model; propagation against a naive recursive reference, compared bit for bit because both multiply in the same order.
- **Change detection:** `Changed`/`Added` per chunk with explicit ticks; a system not seeing its own writes; a system seeing rows that a sync point placed after it ran (including an entity spawned through a buffer by an earlier `PostUpdate` system, which gets its `WorldTransform2D` written on the next `PostUpdate` run); standalone `CommandBuffer.Playback()` stamping a tick newer than every completed query; a standalone query seeing a write made after its loop and not its own loop's writes; an early-`break` enumeration seeing the same chunks again; a first run (system and query) matching every chunk, also when `ChangeTick` is past 2³¹; and tick clamping across 2³⁰ through the internal seam, checking chunk ticks, query ticks and system last-run ticks.
- **Determinism:** a seeded scenario run twice produces the same hash over every entity and component byte in iteration order.
- **Allocation:** 1,000 steady-state frames of each hot path (queries, command buffers, schedules, propagation) allocate 0 bytes.
- **Architecture:** the World project is referenced by `tests/BrutalEngine.Architecture.Tests`, so the layering, binding-surface and static-state rules check it ([architecture tests](architecture-tests.md#rules)).
- **Generator:** snippets compiled with `CSharpCompilation` as in `tests/BrutalEngine.Analyzers.Tests/AnalyzerHarness.cs`: emitted source for components and systems, every diagnostic with positive and negative cases, and generated stable ids equal to `StableComponentId.FromFullName`.
- **NativeAOT:** the world scenario in the AOT smoke harness (section 9).
- **Benchmarks** (BenchmarkDotNet, [Tech stack](../02-tech-stack.md#development-and-test-tooling)), against ADR-0006: iterate 1M entities x 2 components under 1 ms (chunk loop and generated system); 100k spawn + 100k despawn per frame through a command buffer under 5 ms in spawn and shuffled order, with p99; the transform system over 100k roots and a 10,000-node hierarchy; 0 bytes allocated. They run on the reference machine (Ryzen 9 5950X, `docs/spikes/README.md:9`) and the numbers go in the PR. Min-spec results stay unverified until a min-spec machine exists ([docs/09 open question Q7](../09-roadmap.md#open-questions-to-resolve-during-milestones)). CI doesn't gate on timings.
- **Mutation self-checks** per issue, for example: swap-remove without updating the moved entity's record; stamping `Changed` on `Read`; skipping retirement at `uint.MaxValue`; dropping the despawn sort's row order; ignoring `After` constraints.

## 13. Issue split

Each World issue edits the same project and `PublicAPI.Unshipped.txt`, so they run one after another. Estimates include tests and docs.

| Issue | Scope | Blocked by | Est. |
|-------|-------|------------|------|
| W1 feat(world): add entities, the entity table and the component registry | Creates `src/BrutalEngine.World` + tests: `WorldOptions`, `EcsWorld` (construction, disposal, counts, owner-thread checks), `Entity`, the entity table (FIFO reuse, retirement, pending state), `IComponent`, `ComponentInfo`, `StableComponentId` (FNV-1a vectors), `ComponentId`, `ComponentType`, `ComponentRegistry` with ordinal slots; Architecture.Tests reference; a ci-tier self-test case (fast tier); updates the two lines of [Architecture](../03-architecture.md) that name BRUTAL memory primitives as World's basis (3.6, `docs/03-architecture.md:112`, and the allocator line of 3.3, `docs/03-architecture.md:92`) to match the ADR-0006 deviation recorded below | this note; P1 (Core `ThreadAffinity`/`WrongThreadException`, [platform note split](platform.md#13-issue-split)) | ~650 |
| W2 feat(world): store components in pooled 64 KiB chunks by archetype | Chunk pool, archetype layout and capacity, columns and tags, edges, signature map, the dense invariant | W1 | ~650 |
| W3 feat(world): spawn, despawn, add and remove components | Direct API of section 2, `EntityRef`, moves and swap-remove with tick stamps, reference-model property test | W2 | ~650 |
| W4 feat(world): query archetypes chunk by chunk with change detection | `QueryBuilder`, `Query`, `ChunkEnumerator`, `ChunkView`, access checks, iteration guard, ticks and clamping | W3 | ~650 |
| W5 feat(world): defer structural changes through command buffers | `CommandBuffer`, reservation, batched spawn, sorted despawn, dead-target policy, property test through buffers | W3 | ~700 |
| W6 feat(world): run systems in named schedules | `ISystem`, `SystemBuilder`, `SystemContext`, `Schedule`, `ScheduleCollection`, ordering, conflicts and report, access enforcement, sync points, timings, determinism test | W4, W5 | ~700 |
| W7 feat(world): add parent and children relationships | `Parent`, `Children`, the children slab, `SetParent`/`ClearParent`/`GetChildren`, recursive despawn, the command-buffer hierarchy ops | W5 | ~600 |
| W8 feat(world): propagate 2D transforms through the hierarchy | `LocalTransform2D`, `WorldTransform2D` on `System.Numerics` (no new package), `TransformSystem` installed in `PostUpdate`, reference test | W6, W7 | ~600 |
| W9 feat(generators): generate component registration | Creates `src/BrutalEngine.Generators` + tests, the attributes in World, component emission, BE2001-BE2005, packing into the World package | W1 | ~650 |
| W10 feat(generators): generate system query loops | `[EcsSystem]` emission, BE2006-BE2007, the AOT smoke world scenario | W9, W6 | ~650 |
| W11 perf(world): benchmark the world against ADR-0006's targets | `tests/BrutalEngine.Benchmarks` and the BenchmarkDotNet central version, the suites in section 12, reference-machine numbers in the PR | W8, W10 | ~400 |

Ordering notes: W1 needs the Core threading types; W6 owns access enforcement, so it needs both queries (W4) and command buffers (W5); W8 waits for W6 because the transform system is a system; W11 benchmarks the generated system shape, so it waits for W10. W9 needs only W1, but it adds the attributes to World's public API, so like every World issue it runs between the others, not beside them.

## ADR-0006 consistency

| ADR-0006 decision | This note |
|-------------------|-----------|
| Unmanaged-struct components, generational ids | Sections 2 and 3 |
| Option A built in-house "on `Brutal.Memory` / `Brutal.Collections`" (`docs/adr/0006-world-model.md:41`) | **Deviation in the implementation basis; option A is kept.** World is still an in-house archetype ECS, but it is built on Core's `INativeAllocator` and World-owned unmanaged arrays, and takes no BRUTAL package in M1. Reasons, measured or read in S5: `Mem` has no aligned allocation, and chunks need 64-byte alignment (`DESIGN-NOTES.md:129`); `NativeList<T>` is a finalizable class with an allocating `foreach` and growth by allocate, copy and free, so the ECS needs its own control of layout and growth (`DESIGN-NOTES.md:132-138`); and the [memory rule](memory.md#rules) routes all unmanaged memory through `INativeAllocator`, so tests can track leaks. The ADR's argument that an adopted library "won't use BRUTAL's memory primitives" (`docs/adr/0006-world-model.md:42`) no longer separates the options; the other reasons for building in-house (layout, allocation behaviour, determinism, generated code, hot reload) still hold. W1 aligns the wording of Architecture 3.6 and 3.3 (section 13) |
| Named schedules, explicit ordering, declared access, parallel on `JobScheduler` when access sets don't conflict (`docs/adr/0006-world-model.md:49`) | Section 7. **Phased, not changed:** M1 runs schedules single-threaded by maintainer decision, with the API and conflict rules final. M2's parallel executor runs on BRUTAL's `JobScheduler` as sketched in section 7, adding the `Ahwoo.Brutal.Concurrency` reference then; M2's design confirms that against the pinned package |
| Command buffers applied at sync points | Sections 6 and 7 |
| `Parent`/`Children` and transform propagation | Section 8 |
| Change detection with per-component, per-chunk versions | Sections 4 and 5 |
| Events/messages between systems | **Not in M1.** Decision (maintainer, 2026-11-26): events and messages are deferred past M1 and designed together with their first consumer (input or physics). The M1 roadmap scope doesn't list them and no M1 consumer needs them |
| Scenes and prefabs; generated serialisers | M2, by maintainer decision (section 9) |
| Generated queries and registration, AOT-safe | Section 9 |
| Ergonomic layer: typed handle with `Get<T>`/`Set<T>`/`Add<T>`, query `foreach` over ref spans | `EntityRef` (section 2); chunk `foreach` with spans (section 5) |
| Targets: 1M x 2 iterate under 1 ms on min-spec, 100k spawn+despawn under 5 ms, 0 B | Benchmarks on the reference machine (section 12); min-spec unverified |

The decided option (A: an in-house archetype ECS with hierarchy) is kept. One part of its implementation basis deviates as listed (no `Brutal.Memory` / `Brutal.Collections`); `JobScheduler` use and events are phased past M1, events by maintainer decision. No other ADR-0006 decision changes.

## S5 recommendations

Every recommendation in S5's `DESIGN-NOTES.md`, with this note's decision.

| Recommendation | Decision | Reason |
|----------------|----------|--------|
| Archetype tables in fixed-size, pooled, 64-byte-aligned chunks of 64 KiB (`DESIGN-NOTES.md:8-9`) | Adopted | Measured: 16 KiB was 18-31% slower; tables defeat change detection (section 4) |
| Chunk layout and capacity formula (`DESIGN-NOTES.md:13-21`) | Adopted, changed | Change ticks moved out of the chunk into per-archetype arrays, so skipping a chunk doesn't touch it (section 4) |
| Dense invariant with swap-remove; emptied tail chunk back to the pool (`DESIGN-NOTES.md:22`) | Adopted | No liveness checks during iteration (section 4) |
| Chunk size as a world-creation parameter (`DESIGN-NOTES.md:36`) | Adopted | `WorldOptions.ChunkBytes`, same code path for every size (section 1) |
| Columns aligned to max(16, alignment); tags without columns (`DESIGN-NOTES.md:39`) | Adopted | S5's 1-byte tag columns waste chunk space (sections 3 and 4) |
| Side pool for components over about 1 KB (`DESIGN-NOTES.md:40`) | Rejected | M1 enforces a 1,024-byte limit with a clear error instead; no M1 consumer needs bigger components, and a side pool can be added behind the same API |
| Optional small first chunk (`DESIGN-NOTES.md:41`) | Deferred | Two layouts per archetype complicate offsets; the real cost of many small archetypes was never measured. A smaller `ChunkBytes` covers worlds that need it (section 1) |
| `Entity` = 32-bit index + 32-bit generation, generation 0 null (`DESIGN-NOTES.md:45`) | Adopted | Matches Core's `Handle` (section 2) |
| Unmanaged 16-byte location records and a free list (`DESIGN-NOTES.md:46`) | Adopted, changed | FIFO reuse instead of LIFO, per the handles rule, so generations advance slowly (section 2) |
| Retire slots instead of wrapping generations (`DESIGN-NOTES.md:47`) | Adopted | Same rule as Core's handles (section 2) |
| Pack chunk and row into one `int` for 12-byte records (`DESIGN-NOTES.md:48`) | Rejected | 16-byte records stay aligned (four per cache line) and keep `Archetype` free to encode the state; record size was never measured as a bottleneck |
| Generated `ComponentInfo` with stable id (name hash + schema version), size, alignment, name; generated registration (`DESIGN-NOTES.md:55`) | Adopted, changed | Size and alignment come from `T` at registration so they can't disagree with the compiler; serialiser pointers wait for M2 (sections 3 and 9) |
| World-owned registry mapping stable id to a dense per-world index (`DESIGN-NOTES.md:56`) | Adopted | Registries must be instance-owned and rebuildable for hot reload (3.21); a dense index keeps masks and column tables small (section 3) |
| Generated static `ComponentIndex<T>` slot re-stamped by the registry (`DESIGN-NOTES.md:57`) | Rejected | One slot can't serve two worlds; replaced by a process-wide, never re-stamped `int` ordinal plus a per-registry map, which passes the static-state rule (section 3) |
| 256-bit mask, variable-length if more types are needed (`DESIGN-NOTES.md:58`) | Adopted, changed | Width fixed per world by `MaxComponentTypes` (256 default, up to 4,096) (section 3) |
| Chunk iteration as the core primitive; `Write` stamps, `Read` doesn't (`DESIGN-NOTES.md:64-66`) | Adopted | Fastest measured shape at 0 B, and declared access drives change detection (section 5) |
| Struct-callback `ForEach` as the generator target (`DESIGN-NOTES.md:67`) | Adopted, changed | Generated systems emit the chunk loop and call the user's method directly; no public callback interface is needed (sections 5 and 9) |
| Don't ship the per-entity `ref` pair enumerator (`DESIGN-NOTES.md:68-69`) | Adopted | It failed the 1 ms target at 1.31 ms (section 5) |
| Two-level form `chunk.Columns<Pos, Vel>()` (`DESIGN-NOTES.md:70`) | Rejected | `Read<T>`/`Write<T>` already give the two-level loop in two lines, with the access mode explicit per column; a tuple of spans isn't possible and a deconstructing ref struct adds API without speed |
| Generator rewrites a `[System]` method into a chunk loop (`DESIGN-NOTES.md:71`) | Adopted | `[EcsSystem]` with `Execute` (section 9) |
| SIMD-friendly BRUTAL maths types; cast columns to `float` spans (`DESIGN-NOTES.md:72`) | Adopted, changed | SIMD-friendly maths types, but the BCL's `System.Numerics` `Vector2`/`Matrix3x2` instead of BRUTAL's, by maintainer decision: no package dependency for World. Columns are spans of unmanaged structs, so `MemoryMarshal.Cast` to `float` spans still works (section 8) |
| Query created once with an incremental archetype cache (`DESIGN-NOTES.md:75`) | Adopted | Archetypes are append-only, so a refresh scans only new ones and never allocates (section 5) |
| Cache column indices per matched archetype (`DESIGN-NOTES.md:76`) | Adopted | Avoids a per-chunk lookup through a per-archetype table, which S5 found wasteful in memory (section 5) |
| `Changed<T>`/`Added<T>` chunk filters and `Optional<T>` (`DESIGN-NOTES.md:78`) | Adopted | Physics sync, render extract and networking need them, and a chunk filter costs one tick compare per chunk (section 5) |
| Unmanaged command stream; playback keeps capacity (`DESIGN-NOTES.md:82-83`) | Adopted | Measured at 0 B in steady state and only about 20% over direct calls (section 6) |
| Reserve entities at record time (`DESIGN-NOTES.md:84`) | Adopted | Later commands, and components, can refer to an entity spawned in the same frame (section 6) |
| Skip commands on dead targets (`DESIGN-NOTES.md:85`) | Adopted, changed | Skips are also counted, so tests and diagnostics can see them (section 6) |
| Batched spawn op and bulk `SpawnBatch` API (`DESIGN-NOTES.md:88-89`) | Adopted | Needed for the 5 ms target (section 6) |
| Sorted bulk despawn (`DESIGN-NOTES.md:90`) | Adopted | Shuffled despawn failed at about 6 ms (section 6) |
| Avoid the double liveness check (`DESIGN-NOTES.md:91`) | Adopted | Part of the per-entity cost that put spawn+despawn over 5 ms (section 6) |
| One command buffer per system, played back in system order (`DESIGN-NOTES.md:94`) | Adopted | Playback order is then fixed by the schedule, not by thread timing, which keeps results deterministic (section 7) |
| Thread-safe, lock-free reservation (`DESIGN-NOTES.md:95`) | Deferred | M2, with parallel recording; M1 records on the owner thread by maintainer decision, with no API change later (section 6) |
| Resolve archetypes at playback, or lock creation (`DESIGN-NOTES.md:96`) | Adopted | Resolved at playback, so recording never mutates archetype tables (section 6) |
| Pending state; direct structural calls on it throw (`DESIGN-NOTES.md:97`) | Adopted | Prevents the double placement S5 found in review, for the cost of one compare (section 2) |
| Per-chunk, per-column ticks stamped with a world version per tick or system run (`DESIGN-NOTES.md:101`) | Adopted | Advanced per system run (sections 5 and 7) |
| Wrap-safe tick comparison (`DESIGN-NOTES.md:102`) | Adopted, changed | Plus periodic clamping, so ages never exceed 2³¹ ticks (section 5) |
| Stamp every column when a row moves into another chunk (`DESIGN-NOTES.md:103`) | Adopted | Coarse but correct: chunk-level consumers must see rows that arrived in a chunk (section 4) |
| Separate structural `Added` version (`DESIGN-NOTES.md:104`) | Adopted | Consumers must tell new rows from modified ones, for example to create a physics body only once (section 5) |
| Optional per-row dirty bitset for sparse updates (`DESIGN-NOTES.md:105`) | Deferred | Its consumer is networking replication (M5); chunk granularity serves M1 |
| `Parent` component, `Children` in a slab outside chunks (`DESIGN-NOTES.md:109`) | Adopted | Variable-length lists in chunks would break fixed row sizes (section 8) |
| Hierarchy changes as structural commands (`DESIGN-NOTES.md:110`) | Adopted | Direct and buffered forms (section 8) |
| Depth-ordered propagation skipping unchanged chunks, or cascade queries (`DESIGN-NOTES.md:112-113`) | Adopted, changed | Chunk skipping for roots; descendants are walked depth-first and recomputed every run in M1, pending the benchmark (section 8) |
| Recursive despawn inside playback (`DESIGN-NOTES.md:114`) | Adopted | Orphans with dangling `Parent` values would be a silent bug; recursing inside playback keeps despawn deferred and deterministic (section 8) |
| Parallel queries with preallocated jobs and slices of at most 16k rows (`DESIGN-NOTES.md:119`) | Deferred | M2, with parallel schedules (section 7) |
| Allocation tests count process bytes when jobs run (`DESIGN-NOTES.md:120`) | Adopted | Per-thread counts miss allocations on job threads; tests that count the process run without xUnit parallelism (section 10) |
| Engine chunk pool on aligned allocation, since `Mem` has none (`DESIGN-NOTES.md:129-130`) | Adopted | Chunks come from `INativeAllocator` with 64-byte alignment (section 4) |
| Don't use BRUTAL `NativeList` for columns or chunk lists (`DESIGN-NOTES.md:132-138`) | Adopted | Class with a finalizer, allocating `foreach`, copy-on-growth; World manages its own unmanaged arrays (section 4) |
| Schedule executor as waves of jobs over `JobScheduler` (`DESIGN-NOTES.md:146`) | Deferred | M2 by maintainer decision; `Queue` throws while jobs run, so waves are the workable shape then (section 7) |
| Hand SIMD for hot built-in systems (`DESIGN-NOTES.md:150`) | Deferred | After the benchmarks show where it pays (section 8) |
| Sorted arrays or small hash maps for edges instead of dense tables (`DESIGN-NOTES.md:158`) | Adopted | A dense table costs 3 KB per archetype and would grow with `MaxComponentTypes` (section 4) |

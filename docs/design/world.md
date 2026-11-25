# Design: world (`BrutalEngine.World`)

## Purpose

`BrutalEngine.World` is the engine's world model: an in-house archetype ECS with built-in hierarchy and 2D transforms, decided in [ADR-0006](../adr/0006-world-model.md) and summarised in [Architecture 3.6](../03-architecture.md#36-world-model). It is an L3 library: it references Core and nothing from Platform, Graphics or any backend (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:26`).

This note is the API sketch for M1's World issues. Signatures are the contract; XML docs, `PublicAPI.Unshipped.txt` entries, argument validation and tests follow the usual rules ([Coding standards](../07-coding-standards.md#public-api-design)). "Owner thread" is defined in section 10; every member below is owner-thread-only unless it says otherwise.

- Decision (maintainer, 2026-11-26): the M1 public APIs are settled in design notes first. Merging this note approves the World API, and the World code issues are drafted from it.
- Decision (maintainer, 2026-11-26): schedules have declared access and conflict detection and run single-threaded in M1; parallel execution comes in M2 behind the same API.
- Decision (maintainer, 2026-11-26): the source generator is packed inside the World package; it generates component registration and query loops in M1, and serialisers in M2.

Evidence: spike S5 ([results](../spikes/README.md#s5-archetype-ecs-prototype-adr-0006)) and its design notes and code on the `spike/m0-local-spikes` branch (`spikes/s5-ecs/`), cited below as `DESIGN-NOTES.md:<line>` and `Ecs.cs:<line>`. Every S5 recommendation is adopted or rejected in the S5 table at the end. BRUTAL citations are paths in the BRUTAL repository at the pinned 2026.10.x packages.

### Package, names and dependencies

- Package and root namespace `BrutalEngine.World`. Hierarchy types live in `BrutalEngine.World.Hierarchy` and transforms in `BrutalEngine.World.Transforms`.
- **The world type is `EcsWorld`, not `World`.** Inside any `BrutalEngine.*` namespace (Runtime, Rendering2D, Physics2D...), the simple name `World` binds to the namespace `BrutalEngine.World` before any `using` is considered, so `World w = new World();` fails with CS0118 ("'World' is a namespace but is used like a type"). Observed with a scratch compile on .NET SDK 10.0.404; the same code in a game's own namespace compiles. Same reason for `[EcsSystem]` instead of `[System]`.
- Dependencies: `BrutalEngine.Core` (allocators, `Handle`, `StaleHandleException`, `ThreadAffinity`, `WrongThreadException`). From the transforms issue on, `Ahwoo.Brutal.Numerics` 2026.10.2 for `float2`/`float3x2`, the vector types [Tech stack](../02-tech-stack.md#brutal-packages-used) names (`docs/02-tech-stack.md:25`). Its nuspec depends on `Ahwoo.BRUTAL.Core`, `CommunityToolkit.HighPerformance` and Microsoft.Extensions.Logging, not on `Ahwoo.Brutal.Strings`, so World stays free of the preview ObjectPool package (`docs/10-risks.md:22`). `Brutal.Numerics` is not a binding namespace, so the binding-surface rule allows it in public signatures (`tests/BrutalEngine.Architecture.Tests/BindingSurfaceRule.cs:32-46`). `Ahwoo.Brutal.Concurrency` arrives with parallel schedules in M2.

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

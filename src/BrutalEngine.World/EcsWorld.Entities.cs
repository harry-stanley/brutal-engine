using System;
using System.Runtime.CompilerServices;
using BrutalEngine.Core;
using BrutalEngine.World.Storage;

namespace BrutalEngine.World;

// The direct entity API: spawning, despawning and counting entities, and reading their components
// (docs/design/world.md, section 2).
public sealed partial class EcsWorld
{
    // The tick that new rows and writes are stamped with. Starts at 1 and stays there until the tick API arrives with
    // queries (docs/design/world.md, section 5).
    private uint _changeTick = 1;

    // What ColumnOf returns for a tag the archetype has (tags have no column) and for a component it lacks.
    private const int TagColumn = -1;
    private const int MissingColumn = -2;

    /// <summary>The number of alive entities: spawned and placed in storage, and not despawned since.</summary>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// Pending entities (reserved by a command buffer and not yet placed) aren't counted. Allocates nothing. Not
    /// thread-safe: like every member, it is for the owner thread only.
    /// </remarks>
    public int EntityCount
    {
        get
        {
            ThrowIfUnusable("EcsWorld.EntityCount");
            return _entities.PlacedCount;
        }
    }

    /// <summary>The world's current change tick, which new rows and writes are stamped with. Starts at 1.</summary>
    internal uint ChangeTick => _changeTick;

    /// <summary>
    /// Spawns an entity with no components: it is alive when this returns, stored in the empty archetype.
    /// </summary>
    /// <returns>
    /// The new entity. Freed slots are reused first in, first out with an advanced generation, so the entity may have
    /// the index of one despawned earlier, but never an earlier entity's generation.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Every slot allowed by <see cref="WorldOptions.MaxEntities"/> is live, pending or retired; the message names the
    /// option. The world is unchanged.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// The allocator can't provide a grown entity table, a new chunk or grown chunk arrays. The entity is rolled back:
    /// <see cref="EntityCount"/>, every row and every other entity are unchanged, and the next spawn succeeds once
    /// memory is available. If the slot was already taken when the failure happened, its generation advances and it
    /// goes to the back of the reuse queue, as for a despawned entity.
    /// </exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread. Nothing changes.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// Allocates no managed memory. Makes an allocator call only when the entity table grows, or when the archetype
    /// needs a new chunk and the world's chunk pool is empty, or the chunk arrays are full; steady spawn and despawn
    /// churn makes none.
    /// </remarks>
    public Entity Spawn()
    {
        ThrowIfUnusable("EcsWorld.Spawn");
        return SpawnRow(_archetypes.Empty, out _, out _);
    }

    /// <summary>
    /// Despawns <paramref name="entity"/>: removes its row and makes it stale, so every copy of it fails the misuse
    /// checks from now on and <see cref="IsAlive"/> returns <see langword="false"/> for it.
    /// </summary>
    /// <param name="entity">An alive entity of this world.</param>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <see cref="Entity.Null"/>.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="entity"/> is stale: despawned already, its slot retired, or its index outside this world's
    /// table. <see cref="StaleHandleException.Handle"/> holds its index and generation.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="entity"/> is pending: reserved by a command buffer and not placed yet.
    /// </exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// <para>
    /// Every exception is thrown before anything changes. The archetype's last row moves into the despawned row's
    /// place, so the moved entity's location changes (references into chunks are invalidated by any structural
    /// change), and a chunk that empties goes back to the world's chunk pool. The slot is reused by a later spawn with
    /// an advanced generation, or retired once its generation reaches <see cref="uint.MaxValue"/>.
    /// </para>
    /// <para>
    /// Once the world has a hierarchy (docs/design/world.md, section 8), despawning an entity also despawns its
    /// descendants; until then it despawns the entity only.
    /// </para>
    /// <para>Allocates no managed memory and makes no allocator call.</para>
    /// </remarks>
    public void Despawn(Entity entity)
    {
        ThrowIfUnusable("EcsWorld.Despawn");
        EntityRecord record = _entities.Validate(entity);
        Archetype archetype = _archetypes[record.Archetype];
        Entity moved = archetype.SwapRemove(record.Chunk, record.Row);
        if (!moved.IsNull)
        {
            // The archetype's last row filled the hole, so its entity now lives where the despawned one did.
            _entities.Move((int)moved.Index, record.Chunk, record.Row);
        }

        _entities.Free((int)entity.Index);
    }

    /// <summary>
    /// Spawns an entity with one component: it is alive when this returns, with <paramref name="c1"/> stored in the
    /// archetype of its component set.
    /// </summary>
    /// <typeparam name="T1">The component type. Registered with <see cref="Components"/> if it isn't yet.</typeparam>
    /// <param name="c1">The component's value, copied into the entity's row. Ignored for a tag, which has no data.</param>
    /// <returns>The new entity, issued as by <see cref="Spawn()"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="T1"/> is new to the world and breaks a registration rule (see
    /// <see cref="ComponentRegistry.Register{T}"/>). The world is unchanged.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T1"/> is new and can't be registered (a stable-id conflict, or the registry is full); or
    /// the archetype is new and its row is too wide for a chunk; or every slot allowed by
    /// <see cref="WorldOptions.MaxEntities"/> is live, pending or retired. The message names the cause. No entity is
    /// spawned; a type registered or an archetype created before the failure stays, as both are permanent.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// The allocator can't provide what the spawn needs. The entity is rolled back as for <see cref="Spawn()"/>.
    /// </exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread. Nothing changes.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// The new row stamps the <c>Changed</c> and <c>Added</c> ticks of every column in its chunk with the world's
    /// current change tick. Allocates no managed memory once the type is registered and the archetype exists (the
    /// first spawn of a component set creates its archetype, which is setup work); allocator calls are as for
    /// <see cref="Spawn()"/>.
    /// </remarks>
    public Entity Spawn<T1>(in T1 c1)
        where T1 : unmanaged, IComponent
    {
        ThrowIfUnusable("EcsWorld.Spawn");
        Span<ComponentId> ids = stackalloc ComponentId[1];
        ids[0] = _components.Register<T1>();
        Archetype archetype = _archetypes.GetOrCreate(ids);
        Entity entity = SpawnRow(archetype, out int chunk, out int row);
        Store(archetype, chunk, row, ids[0], in c1);
        return entity;
    }

    /// <summary>
    /// Spawns an entity with two components: it is alive when this returns, with the values stored in the archetype of
    /// its component set. The order of the type arguments doesn't matter.
    /// </summary>
    /// <typeparam name="T1">The first component type. Registered with <see cref="Components"/> if it isn't yet.</typeparam>
    /// <typeparam name="T2">The second component type, not <typeparamref name="T1"/>. Registered if it isn't yet.</typeparam>
    /// <param name="c1">The first component's value, copied into the entity's row. Ignored for a tag.</param>
    /// <param name="c2">The second component's value, copied into the entity's row. Ignored for a tag.</param>
    /// <returns>The new entity, issued as by <see cref="Spawn()"/>.</returns>
    /// <exception cref="ArgumentException">
    /// The same type is given twice, which is checked before anything else, so nothing changes; or a type is new and
    /// breaks a registration rule, as for <see cref="Spawn{T1}(in T1)"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">As for <see cref="Spawn{T1}(in T1)"/>.</exception>
    /// <exception cref="OutOfMemoryException">As for <see cref="Spawn{T1}(in T1)"/>.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread. Nothing changes.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Stamps and allocates as <see cref="Spawn{T1}(in T1)"/> does.</remarks>
    public Entity Spawn<T1, T2>(in T1 c1, in T2 c2)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
    {
        ThrowIfUnusable("EcsWorld.Spawn");
        ThrowIfSameType<T1, T2>(nameof(c2));
        Span<ComponentId> ids = stackalloc ComponentId[2];
        ids[0] = _components.Register<T1>();
        ids[1] = _components.Register<T2>();
        Archetype archetype = _archetypes.GetOrCreate(ids);
        Entity entity = SpawnRow(archetype, out int chunk, out int row);
        Store(archetype, chunk, row, ids[0], in c1);
        Store(archetype, chunk, row, ids[1], in c2);
        return entity;
    }

    /// <summary>
    /// Spawns an entity with three components: it is alive when this returns, with the values stored in the archetype
    /// of its component set. The order of the type arguments doesn't matter.
    /// </summary>
    /// <typeparam name="T1">The first component type. Registered with <see cref="Components"/> if it isn't yet.</typeparam>
    /// <typeparam name="T2">The second component type. Registered if it isn't yet.</typeparam>
    /// <typeparam name="T3">The third component type. Registered if it isn't yet. The three types are distinct.</typeparam>
    /// <param name="c1">The first component's value, copied into the entity's row. Ignored for a tag.</param>
    /// <param name="c2">The second component's value, copied into the entity's row. Ignored for a tag.</param>
    /// <param name="c3">The third component's value, copied into the entity's row. Ignored for a tag.</param>
    /// <returns>The new entity, issued as by <see cref="Spawn()"/>.</returns>
    /// <exception cref="ArgumentException">
    /// A type is given more than once, which is checked before anything else, so nothing changes; or a type is new and
    /// breaks a registration rule, as for <see cref="Spawn{T1}(in T1)"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">As for <see cref="Spawn{T1}(in T1)"/>.</exception>
    /// <exception cref="OutOfMemoryException">As for <see cref="Spawn{T1}(in T1)"/>.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread. Nothing changes.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Stamps and allocates as <see cref="Spawn{T1}(in T1)"/> does.</remarks>
    public Entity Spawn<T1, T2, T3>(in T1 c1, in T2 c2, in T3 c3)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
    {
        ThrowIfUnusable("EcsWorld.Spawn");
        ThrowIfSameType<T1, T2>(nameof(c2));
        ThrowIfSameType<T1, T3>(nameof(c3));
        ThrowIfSameType<T2, T3>(nameof(c3));
        Span<ComponentId> ids = stackalloc ComponentId[3];
        ids[0] = _components.Register<T1>();
        ids[1] = _components.Register<T2>();
        ids[2] = _components.Register<T3>();
        Archetype archetype = _archetypes.GetOrCreate(ids);
        Entity entity = SpawnRow(archetype, out int chunk, out int row);
        Store(archetype, chunk, row, ids[0], in c1);
        Store(archetype, chunk, row, ids[1], in c2);
        Store(archetype, chunk, row, ids[2], in c3);
        return entity;
    }

    /// <summary>
    /// Spawns an entity with four components: it is alive when this returns, with the values stored in the archetype
    /// of its component set. The order of the type arguments doesn't matter. For more components, add them after
    /// spawning.
    /// </summary>
    /// <typeparam name="T1">The first component type. Registered with <see cref="Components"/> if it isn't yet.</typeparam>
    /// <typeparam name="T2">The second component type. Registered if it isn't yet.</typeparam>
    /// <typeparam name="T3">The third component type. Registered if it isn't yet.</typeparam>
    /// <typeparam name="T4">The fourth component type. Registered if it isn't yet. The four types are distinct.</typeparam>
    /// <param name="c1">The first component's value, copied into the entity's row. Ignored for a tag.</param>
    /// <param name="c2">The second component's value, copied into the entity's row. Ignored for a tag.</param>
    /// <param name="c3">The third component's value, copied into the entity's row. Ignored for a tag.</param>
    /// <param name="c4">The fourth component's value, copied into the entity's row. Ignored for a tag.</param>
    /// <returns>The new entity, issued as by <see cref="Spawn()"/>.</returns>
    /// <exception cref="ArgumentException">
    /// A type is given more than once, which is checked before anything else, so nothing changes; or a type is new and
    /// breaks a registration rule, as for <see cref="Spawn{T1}(in T1)"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">As for <see cref="Spawn{T1}(in T1)"/>.</exception>
    /// <exception cref="OutOfMemoryException">As for <see cref="Spawn{T1}(in T1)"/>.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread. Nothing changes.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Stamps and allocates as <see cref="Spawn{T1}(in T1)"/> does.</remarks>
    public Entity Spawn<T1, T2, T3, T4>(in T1 c1, in T2 c2, in T3 c3, in T4 c4)
        where T1 : unmanaged, IComponent
        where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent
        where T4 : unmanaged, IComponent
    {
        ThrowIfUnusable("EcsWorld.Spawn");
        ThrowIfSameType<T1, T2>(nameof(c2));
        ThrowIfSameType<T1, T3>(nameof(c3));
        ThrowIfSameType<T2, T3>(nameof(c3));
        ThrowIfSameType<T1, T4>(nameof(c4));
        ThrowIfSameType<T2, T4>(nameof(c4));
        ThrowIfSameType<T3, T4>(nameof(c4));
        Span<ComponentId> ids = stackalloc ComponentId[4];
        ids[0] = _components.Register<T1>();
        ids[1] = _components.Register<T2>();
        ids[2] = _components.Register<T3>();
        ids[3] = _components.Register<T4>();
        Archetype archetype = _archetypes.GetOrCreate(ids);
        Entity entity = SpawnRow(archetype, out int chunk, out int row);
        Store(archetype, chunk, row, ids[0], in c1);
        Store(archetype, chunk, row, ids[1], in c2);
        Store(archetype, chunk, row, ids[2], in c3);
        Store(archetype, chunk, row, ids[3], in c4);
        return entity;
    }

    /// <summary>Returns whether <paramref name="entity"/> has a <typeparamref name="T"/> component, data or tag.</summary>
    /// <typeparam name="T">The component type. Not registered by this call.</typeparam>
    /// <param name="entity">An alive entity of this world.</param>
    /// <returns>
    /// <see langword="true"/> if the entity's archetype holds <typeparamref name="T"/>; <see langword="false"/> if it
    /// doesn't, including when <typeparamref name="T"/> isn't registered with this world.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <see cref="Entity.Null"/>.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="entity"/> is stale: despawned, its slot retired, or its index outside this world's table.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="entity"/> is pending: reserved and not placed yet.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Allocates nothing and stamps nothing: one mask test after the entity check.</remarks>
    public bool Has<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        ThrowIfUnusable("EcsWorld.Has");
        EntityRecord record = _entities.Validate(entity);
        return _components.TryGetId<T>(out ComponentId id) && _archetypes[record.Archetype].Signature.Contains(id.Index);
    }

    /// <summary>Returns a read-only reference to <paramref name="entity"/>'s <typeparamref name="T"/> value, in its chunk.</summary>
    /// <typeparam name="T">A data component type (not a tag) that the entity has.</typeparam>
    /// <param name="entity">An alive entity of this world.</param>
    /// <returns>
    /// A reference to the value in the entity's row. It is valid until the next structural change to the world (a
    /// spawn, a despawn, or adding or removing a component), which may move rows; reading through it after that is
    /// undefined.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <see cref="Entity.Null"/>.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="entity"/> is stale: despawned, its slot retired, or its index outside this world's table.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="entity"/> is pending; or it doesn't have <typeparamref name="T"/> (including when the type isn't
    /// registered with this world); or <typeparamref name="T"/> is a tag, which has no data. The message names the
    /// entity and the component.
    /// </exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// Doesn't stamp the component's <c>Changed</c> tick: change filters never see a read. Allocates nothing, except
    /// the exception when it throws.
    /// </remarks>
    public ref readonly T Get<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        ThrowIfUnusable("EcsWorld.Get");
        EntityRecord record = _entities.Validate(entity);
        Archetype archetype = _archetypes[record.Archetype];
        int column = ColumnFor<T>(entity, archetype, "EcsWorld.Get");
        if (column == MissingColumn)
        {
            throw MissingComponent<T>(entity);
        }

        return ref ValueAt<T>(archetype, record.Chunk, record.Row, column);
    }

    /// <summary>Copies <paramref name="entity"/>'s <typeparamref name="T"/> value, if it has one.</summary>
    /// <typeparam name="T">A data component type (not a tag).</typeparam>
    /// <param name="entity">An alive entity of this world.</param>
    /// <param name="value">The value if the entity has <typeparamref name="T"/>; otherwise <c>default</c>.</param>
    /// <returns>
    /// <see langword="true"/> if the entity has <typeparamref name="T"/>; <see langword="false"/> if it doesn't,
    /// including when the type isn't registered with this world.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <see cref="Entity.Null"/>.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="entity"/> is stale: despawned, its slot retired, or its index outside this world's table.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="entity"/> is pending; or <typeparamref name="T"/> is a tag, which has no data (use
    /// <see cref="Has{T}"/>). The message names the entity and the component.
    /// </exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>Doesn't stamp, and allocates nothing, except the exception when it throws.</remarks>
    public bool TryGet<T>(Entity entity, out T value)
        where T : unmanaged, IComponent
    {
        ThrowIfUnusable("EcsWorld.TryGet");
        EntityRecord record = _entities.Validate(entity);
        Archetype archetype = _archetypes[record.Archetype];
        int column = ColumnFor<T>(entity, archetype, "EcsWorld.TryGet");
        if (column == MissingColumn)
        {
            value = default;
            return false;
        }

        value = ValueAt<T>(archetype, record.Chunk, record.Row, column);
        return true;
    }

    /// <summary>Test seam: sets the change tick, so stamps can be told apart. The engine never calls it.</summary>
    /// <param name="tick">The new tick.</param>
    internal void SetChangeTickForTesting(uint tick)
    {
        ThrowIfUnusable("EcsWorld.SetChangeTickForTesting");
        _changeTick = tick;
    }

    /// <summary>
    /// Takes an entity slot, appends its row to <paramref name="archetype"/>, places it, and stamps the chunk's
    /// <c>Changed</c> and <c>Added</c> ticks. The row's column values are uninitialised: the caller writes them before
    /// returning to its own caller.
    /// </summary>
    /// <param name="archetype">The archetype the entity is stored in.</param>
    /// <param name="chunk">The new row's chunk.</param>
    /// <param name="row">The new row within the chunk.</param>
    /// <returns>The new, alive entity.</returns>
    /// <exception cref="InvalidOperationException">No entity slot is left. Nothing changes.</exception>
    /// <exception cref="OutOfMemoryException">
    /// Growing the entity table, or the row's new chunk, failed. The entity is rolled back with
    /// <see cref="EntityTable.CancelPending"/>, which advances the slot's generation and queues it for reuse.
    /// </exception>
    private Entity SpawnRow(Archetype archetype, out int chunk, out int row)
    {
        // Leaves the table unchanged when it throws.
        Entity entity = _entities.Allocate();
        try
        {
            // Leaves the archetype unchanged when it throws.
            (chunk, row) = archetype.AppendRow(entity, _changeTick);
        }
        catch
        {
            // The rollback the entity table prescribes for a step after Allocate that fails: the slot is released, not
            // left pending for good.
            _entities.CancelPending(entity);
            throw;
        }

        _entities.Place((int)entity.Index, archetype.Index, chunk, row);

        // Data arrived in the chunk: every column's Changed and Added ticks move to the current tick.
        archetype.Chunks.GetChanged(chunk).Fill(_changeTick);
        archetype.Chunks.GetAdded(chunk).Fill(_changeTick);
        return entity;
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if <typeparamref name="TFirst"/> and <typeparamref name="TSecond"/> are
    /// the same type. Value types, so the comparison is a constant the JIT folds away.
    /// </summary>
    private static void ThrowIfSameType<TFirst, TSecond>(string paramName)
        where TFirst : unmanaged, IComponent
        where TSecond : unmanaged, IComponent
    {
        if (typeof(TFirst) == typeof(TSecond))
        {
            throw new ArgumentException(
                $"EcsWorld.Spawn was given component {TFirst.Info.FullName} more than once; each component type can appear once per spawn.",
                paramName);
        }
    }

    /// <summary>Copies a spawned component's value into its column of the row; a tag has no column and stores nothing.</summary>
    private static void Store<T>(Archetype archetype, int chunk, int row, ComponentId id, in T value)
        where T : unmanaged
    {
        int column = ColumnOf(archetype, id);
        if (column >= 0)
        {
            ValueAt<T>(archetype, chunk, row, column) = value;
        }
    }

    /// <summary>
    /// The column of the component with <paramref name="id"/> in <paramref name="archetype"/>'s chunks:
    /// <see cref="TagColumn"/> for a tag it has, <see cref="MissingColumn"/> if it lacks the component.
    /// </summary>
    private static int ColumnOf(Archetype archetype, ComponentId id)
    {
        int typeIndex = archetype.Signature.Ids.BinarySearch(id.Index);
        return typeIndex < 0 ? MissingColumn : archetype.Layout.ColumnOf(typeIndex);
    }

    /// <summary>
    /// A reference to the value in <paramref name="column"/> of a row. The column must hold <typeparamref name="T"/>
    /// and the row must be live.
    /// </summary>
    private static unsafe ref T ValueAt<T>(Archetype archetype, int chunk, int row, int column)
        where T : unmanaged
    {
        // Sound: the callers pass a live row (the entity's record, or the row just appended) and the column of T in
        // this archetype's layout, whose values are sizeof(T) bytes each (the registry takes the size from T itself).
        // The column holds Layout.Capacity values from its offset, and row < Capacity, so the value lies inside the
        // chunk's block. The column offset is aligned to at least T's alignment and the block to 64 bytes, so the
        // reference is aligned.
        byte* address = archetype.Chunks.GetData(chunk) + archetype.Layout.ColumnOffsets[column] + ((nint)row * sizeof(T));
        return ref Unsafe.AsRef<T>(address);
    }

    /// <summary>
    /// The column of <typeparamref name="T"/> in <paramref name="archetype"/>, or <see cref="MissingColumn"/> when the
    /// archetype lacks it or the type isn't registered here; throws if <typeparamref name="T"/> is a tag.
    /// </summary>
    private int ColumnFor<T>(Entity entity, Archetype archetype, string apiName)
        where T : unmanaged, IComponent
    {
        if (!_components.TryGetId<T>(out ComponentId id))
        {
            // Not registered here, so the entity can't have it. A tag is still misuse: data access to it never works.
            return T.Info.IsTag ? throw TagAccess<T>(entity, apiName) : MissingColumn;
        }

        int column = ColumnOf(archetype, id);
        if (column == TagColumn || (column == MissingColumn && _components.GetComponentType(id).Info.IsTag))
        {
            throw TagAccess<T>(entity, apiName);
        }

        return column;
    }

    private static InvalidOperationException MissingComponent<T>(Entity entity)
        where T : unmanaged, IComponent =>
        new($"{entity} doesn't have component {T.Info.FullName}.");

    private static InvalidOperationException TagAccess<T>(Entity entity, string apiName)
        where T : unmanaged, IComponent =>
        new($"Component {T.Info.FullName} is a tag, which has no data, so {apiName} can't access it on {entity}; use EcsWorld.Has to test for it.");
}

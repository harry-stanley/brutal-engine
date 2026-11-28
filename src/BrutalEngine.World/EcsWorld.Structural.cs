using System;
using BrutalEngine.Core;
using BrutalEngine.World.Queries;
using BrutalEngine.World.Storage;

namespace BrutalEngine.World;

// Structural changes to live entities: adding, setting and removing components, which moves the entity to another
// archetype unless a set replaces a present value; and EntityRef, which forwards them
// (docs/design/world.md, sections 2 and 4, "Moves").
public sealed partial class EcsWorld
{
    // Counts the world's live chunk enumerators; structural entry points throw while any is live (section 5).
    private readonly IterationGuard _iterationGuard = new();

    /// <summary>The world's iteration guard, which its queries' enumerators enter and exit.</summary>
    internal IterationGuard IterationGuard => _iterationGuard;

    /// <summary>Returns an <see cref="EntityRef"/> for <paramref name="entity"/>, after checking it as the other direct calls do.</summary>
    /// <param name="entity">An alive entity of this world.</param>
    /// <returns>A reference holding this world and <paramref name="entity"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <see cref="Entity.Null"/>.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="entity"/> is stale: despawned, its slot retired, or its index outside this world's table.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="entity"/> is pending: reserved and not placed yet.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// The check is made once, here; each member of the reference checks the entity again, so a reference to an entity
    /// that is despawned later fails as the world's own members would. Allocates nothing and stamps nothing.
    /// </remarks>
    public EntityRef Ref(Entity entity)
    {
        ThrowIfUnusable("EcsWorld.Ref");
        _ = _entities.Validate(entity);
        return new EntityRef(this, entity);
    }

    /// <summary>
    /// Adds a <typeparamref name="T"/> component to <paramref name="entity"/>, which moves the entity to the archetype
    /// of its component set plus <typeparamref name="T"/>. Its other components keep their values.
    /// </summary>
    /// <typeparam name="T">
    /// The component type, data or tag, which the entity doesn't have yet. Registered with <see cref="Components"/> if
    /// it isn't yet.
    /// </typeparam>
    /// <param name="entity">An alive entity of this world.</param>
    /// <param name="value">The component's value, copied into the entity's new row. Ignored for a tag, which has no data.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="entity"/> is <see cref="Entity.Null"/>; or <typeparamref name="T"/> is new to the world and
    /// breaks a registration rule (see <see cref="ComponentRegistry.Register{T}"/>).
    /// </exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="entity"/> is stale: despawned, its slot retired, or its index outside this world's table.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="entity"/> is pending; or it already has <typeparamref name="T"/> (use <c>Set</c> to replace a
    /// value), which the message says, naming the entity and the component; or <typeparamref name="T"/> is new and
    /// can't be registered (a stable-id conflict, or the registry is full); or the target archetype is new and its row
    /// is too wide for a chunk, which the message says, naming the components; or a chunk enumeration of the world is
    /// live (see <see cref="Query.GetEnumerator"/>), which is checked first, so nothing changes.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// The allocator can't provide what the move needs: a new chunk for the target archetype (its block or the grown
    /// chunk arrays), or a grown edge table. The entity stays where it was, with its values and stamps, and every
    /// other entity and chunk is unchanged; the next call succeeds once memory is available.
    /// </exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// <para>
    /// The misuse checks (the entity, then whether it has <typeparamref name="T"/>) run before anything changes. A
    /// failure after them leaves the entity and every row where they were; only a type registered or an archetype
    /// created on the way stays, as both are permanent.
    /// </para>
    /// <para>
    /// <b>The move</b> (docs/design/world.md, section 4, "Moves"): a row is appended to the target archetype, the
    /// shared columns are copied into it and <paramref name="value"/> is written, then the old row is removed by moving
    /// the source archetype's last row into it. References into chunks are invalidated, as by any structural change.
    /// </para>
    /// <para>
    /// <b>Stamps</b>, with the world's current change tick: in the target chunk, the <c>Changed</c> tick of every
    /// column and the <c>Added</c> tick of <typeparamref name="T"/> (a tag has none); a target chunk opened by the move
    /// starts with every tick at the current tick. In the source chunk that received the source archetype's last row,
    /// the <c>Changed</c> tick of every column; nothing in the source when the entity's row was that last row.
    /// </para>
    /// <para>
    /// Allocates no managed memory once <typeparamref name="T"/> is registered and the target archetype exists (the
    /// first move to a component set creates its archetype, which is setup work). Makes an allocator call only when
    /// the target archetype needs a new chunk and the world's chunk pool is empty, or its chunk arrays are full, or
    /// the source archetype caches a new edge.
    /// </para>
    /// </remarks>
    public void Add<T>(Entity entity, in T value)
        where T : unmanaged, IComponent
    {
        ThrowIfStructuralUnusable("EcsWorld.Add");
        EntityRecord record = _entities.Validate(entity);
        Archetype source = _archetypes[record.Archetype];

        // Before registering: a type the entity has is registered already, and an unregistered one it can't have.
        if (_components.TryGetId<T>(out ComponentId present) && source.Signature.Contains(present.Index))
        {
            throw new InvalidOperationException(
                $"{entity} already has component {T.Info.FullName}; use EcsWorld.Set to replace its value.");
        }

        AddByMove(entity, record, source, value);
    }

    /// <summary>
    /// Sets <paramref name="entity"/>'s <typeparamref name="T"/> component to <paramref name="value"/>: replaces the
    /// value in place if the entity has <typeparamref name="T"/>, or adds the component as <see cref="Add{T}"/> does if
    /// it doesn't.
    /// </summary>
    /// <typeparam name="T">
    /// The component type, data or tag. Registered with <see cref="Components"/> if it isn't yet.
    /// </typeparam>
    /// <param name="entity">An alive entity of this world.</param>
    /// <param name="value">The component's value, copied into the entity's row. Ignored for a tag, which has no data.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="entity"/> is <see cref="Entity.Null"/>; or <typeparamref name="T"/> is new to the world and
    /// breaks a registration rule (see <see cref="ComponentRegistry.Register{T}"/>).
    /// </exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="entity"/> is stale: despawned, its slot retired, or its index outside this world's table.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="entity"/> is pending; or <typeparamref name="T"/> is new and can't be registered (a stable-id
    /// conflict, or the registry is full); or the entity lacks <typeparamref name="T"/> and the target archetype is new
    /// and its row is too wide for a chunk, which the message says, naming the components; or a chunk enumeration of the
    /// world is live, whether or not the entity has <typeparamref name="T"/>, which is checked first, so nothing changes.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// The entity lacks <typeparamref name="T"/> and the allocator can't provide what the move needs: a new chunk for
    /// the target archetype (its block or the grown chunk arrays), or a grown edge table. The entity stays where it
    /// was, with its values and stamps, and every other entity and chunk is unchanged; <typeparamref name="T"/> stays
    /// registered and an archetype created on the way stays, as both are permanent.
    /// </exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// <para>
    /// The entity check runs before anything changes. <b>A present component</b> is replaced where it is: the entity
    /// doesn't move, references into chunks stay valid, and only <typeparamref name="T"/>'s <c>Changed</c> tick in the
    /// entity's chunk is stamped with the world's current change tick, as <see cref="Write{T}"/> does. A present tag
    /// has no column, so setting it changes nothing and stamps nothing.
    /// </para>
    /// <para>
    /// <b>An absent component</b> is added by the move and with the stamps <see cref="Add{T}"/> documents: the target
    /// chunk's <c>Changed</c> ticks and <typeparamref name="T"/>'s <c>Added</c> tick, and the <c>Changed</c> ticks of
    /// the source chunk that received the moved row. References into chunks are invalidated.
    /// </para>
    /// <para>
    /// Allocates no managed memory once <typeparamref name="T"/> is registered and, for an absent component, the
    /// target archetype exists. Allocator calls are as for <see cref="Add{T}"/>; replacing a value makes none.
    /// </para>
    /// </remarks>
    public void Set<T>(Entity entity, in T value)
        where T : unmanaged, IComponent
    {
        ThrowIfStructuralUnusable("EcsWorld.Set");
        EntityRecord record = _entities.Validate(entity);
        Archetype source = _archetypes[record.Archetype];
        if (_components.TryGetId<T>(out ComponentId present) && source.Signature.Contains(present.Index))
        {
            // Replaced in place, stamped like a write; a tag has no column, so nothing changes.
            int column = ColumnOf(source, present);
            if (column >= 0)
            {
                ValueAt<T>(source, record.Chunk, record.Row, column) = value;
                source.Chunks.GetChanged(record.Chunk)[column] = _changeTick;
            }

            return;
        }

        AddByMove(entity, record, source, value);
    }

    /// <summary>
    /// Removes <paramref name="entity"/>'s <typeparamref name="T"/> component, if it has one, which moves the entity to
    /// the archetype of its component set minus <typeparamref name="T"/>. Its other components keep their values.
    /// </summary>
    /// <typeparam name="T">The component type, data or tag. Not registered by this call.</typeparam>
    /// <param name="entity">An alive entity of this world.</param>
    /// <returns>
    /// <see langword="true"/> if the entity had <typeparamref name="T"/> and it was removed; <see langword="false"/> if
    /// it didn't have it (including when the type isn't registered with this world), in which case nothing changes and
    /// nothing is stamped.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <see cref="Entity.Null"/>.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="entity"/> is stale: despawned, its slot retired, or its index outside this world's table.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="entity"/> is pending: reserved and not placed yet; or a chunk enumeration of the world is live,
    /// whether or not the entity has <typeparamref name="T"/>, which is checked first, so nothing changes.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// The allocator can't provide what the move needs: a new chunk for the target archetype (its block or the grown
    /// chunk arrays), or a grown edge table. The entity keeps <typeparamref name="T"/>, its row, values and stamps,
    /// and every other entity and chunk is unchanged; an archetype created on the way stays, as archetypes are
    /// permanent.
    /// </exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <remarks>
    /// <para>
    /// The entity check runs before anything changes. The move is the one <see cref="Add{T}"/> makes: a row is
    /// appended to the target archetype, the shared columns are copied into it (<typeparamref name="T"/>'s value is
    /// dropped), and the old row is removed by moving the source archetype's last row into it. References into chunks
    /// are invalidated.
    /// </para>
    /// <para>
    /// <b>Stamps</b>, with the world's current change tick: in the target chunk, the <c>Changed</c> tick of every
    /// column, and no <c>Added</c> tick, as nothing was added; in the source chunk that received the source
    /// archetype's last row, the <c>Changed</c> tick of every column. A target chunk opened by the move starts with
    /// every tick at the current tick.
    /// </para>
    /// <para>
    /// Allocates no managed memory once the target archetype exists. Allocator calls are as for
    /// <see cref="Add{T}"/>.
    /// </para>
    /// </remarks>
    public bool Remove<T>(Entity entity)
        where T : unmanaged, IComponent
    {
        ThrowIfStructuralUnusable("EcsWorld.Remove");
        EntityRecord record = _entities.Validate(entity);
        Archetype source = _archetypes[record.Archetype];
        if (!_components.TryGetId<T>(out ComponentId id) || !source.Signature.Contains(id.Index))
        {
            return false;
        }

        // Leaves the entity where it is when it throws; an archetype it creates is permanent.
        Archetype target = source.WithRemoved(id);
        _ = MoveRow(entity, record, source, target);
        return true;
    }

    /// <summary>
    /// The checks of a structural entry point: the owner thread and disposal, then the iteration guard, before any
    /// argument is looked at.
    /// </summary>
    /// <param name="apiName">The member's name, for the exception message.</param>
    private void ThrowIfStructuralUnusable(string apiName)
    {
        ThrowIfUnusable(apiName);
        _iterationGuard.ThrowIfLive(apiName);
    }

    /// <summary>
    /// Adds a component the entity lacks: registers <typeparamref name="T"/> if needed, moves the entity to the
    /// archetype with it, writes <paramref name="value"/> and stamps <typeparamref name="T"/>'s <c>Added</c> tick in the
    /// target chunk (a tag has no column and no tick).
    /// </summary>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> is new and breaks a registration rule.</exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> can't be registered, or the target archetype is new and its row is too wide.
    /// </exception>
    /// <exception cref="OutOfMemoryException">The move's allocation failed; the entity stays where it was.</exception>
    private void AddByMove<T>(Entity entity, EntityRecord record, Archetype source, in T value)
        where T : unmanaged, IComponent
    {
        // Each of these leaves the entity where it is when it throws; what they register or create is permanent.
        ComponentId id = _components.Register<T>();
        Archetype target = source.WithAdded(id);
        (int chunk, int row) = MoveRow(entity, record, source, target);
        int column = ColumnOf(target, id);
        if (column >= 0)
        {
            ValueAt<T>(target, chunk, row, column) = value;
            target.Chunks.GetAdded(chunk)[column] = _changeTick;
        }
    }

    /// <summary>
    /// Moves an alive entity's row from <paramref name="source"/> to <paramref name="target"/>: appends a row to the
    /// target, copies the shared columns, swap-removes the source row, updates both entities' records, and stamps the
    /// <c>Changed</c> ticks of every column in the target chunk and in the source chunk that received a moved row. The
    /// caller writes and stamps the columns only the target has.
    /// </summary>
    /// <param name="entity">The entity, alive in <paramref name="source"/> at <paramref name="record"/>.</param>
    /// <param name="record">The entity's record.</param>
    /// <param name="source">The entity's archetype.</param>
    /// <param name="target">Another archetype of the world.</param>
    /// <returns>The entity's new chunk and row in <paramref name="target"/>.</returns>
    /// <exception cref="OutOfMemoryException">
    /// The target needs a new chunk and the allocator can't provide it or the grown chunk arrays. It is the only step
    /// that can fail, and it comes first, so nothing changes.
    /// </exception>
    private (int Chunk, int Row) MoveRow(Entity entity, EntityRecord record, Archetype source, Archetype target)
    {
        // Leaves the target unchanged when it throws; nothing else has changed yet.
        (int chunk, int row) = target.AppendRow(entity, _changeTick);
        source.CopySharedColumns(record.Chunk, record.Row, target, chunk, row);
        Entity moved = source.SwapRemove(record.Chunk, record.Row);
        if (!moved.IsNull)
        {
            // The source's last row filled the hole, as in a despawn: its entity now lives there, and data arrived in
            // that chunk.
            _entities.Move((int)moved.Index, record.Chunk, record.Row);
            source.Chunks.GetChanged(record.Chunk).Fill(_changeTick);
        }

        _entities.Place((int)entity.Index, target.Index, chunk, row);

        // Data arrived in the target chunk: every column's Changed tick moves to the current tick.
        target.Chunks.GetChanged(chunk).Fill(_changeTick);
        return (chunk, row);
    }
}

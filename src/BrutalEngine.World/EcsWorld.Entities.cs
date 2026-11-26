using System;
using BrutalEngine.Core;
using BrutalEngine.World.Storage;

namespace BrutalEngine.World;

// The direct entity API: spawning, despawning and counting entities (docs/design/world.md, section 2).
public sealed partial class EcsWorld
{
    // The tick that new rows and writes are stamped with. Starts at 1 and stays there until the tick API arrives with
    // queries (docs/design/world.md, section 5).
    private uint _changeTick = 1;

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

    /// <summary>Test seam: sets the change tick, so stamps can be told apart. The engine never calls it.</summary>
    /// <param name="tick">The new tick.</param>
    internal void SetChangeTickForTesting(uint tick)
    {
        ThrowIfUnusable("EcsWorld.SetChangeTickForTesting");
        _changeTick = tick;
    }

    /// <summary>
    /// Takes an entity slot and appends its row to <paramref name="archetype"/>, then places it. The row's column
    /// values are uninitialised: the caller writes them before returning to its own caller.
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
        return entity;
    }
}

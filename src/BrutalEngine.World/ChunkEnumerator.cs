using System;
using BrutalEngine.Core;
using BrutalEngine.World.Queries;
using BrutalEngine.World.Storage;

namespace BrutalEngine.World;

/// <summary>
/// Enumerates a query's matching chunks, as <c>foreach (ChunkView chunk in query)</c> does (docs/design/world.md,
/// section 5). Made by <see cref="Query.GetEnumerator"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order and filters.</b> Chunks come archetype by archetype, in the order the archetypes were created, and in
/// chunk order within each. Each matching chunk is yielded once, unless the query's <c>Changed</c> or <c>Added</c>
/// filters reject it: after the query's first completed enumeration, a chunk is yielded only when, for every filter, the
/// component's tick in that chunk is newer than <see cref="Query.LastRunTick"/>. A filter on a tag never holds then,
/// because a tag has no ticks. Until then every chunk is yielded.
/// </para>
/// <para>
/// <b>Completion.</b> When <see cref="MoveNext"/> returns <see langword="false"/> the first time, the enumeration has
/// completed: the query's <see cref="Query.LastRunTick"/> becomes the world's <see cref="EcsWorld.ChangeTick"/>,
/// <see cref="Query.HasRun"/> becomes <see langword="true"/>, and the world advances its change tick, so writes made
/// during the loop aren't newer than the last run and later writes are. An enumeration disposed before that (a
/// <c>break</c>, or an exception) changes nothing.
/// </para>
/// <para>
/// <b>Iteration guard.</b> From <see cref="Query.GetEnumerator"/> until <see cref="Dispose"/>, structural changes to the
/// world (spawn, despawn, add, set and remove) throw <see cref="InvalidOperationException"/>. Enumerating other queries,
/// or the same one, inside the loop is allowed. <c>foreach</c> always disposes the enumerator; code that calls
/// <see cref="Query.GetEnumerator"/> itself must dispose it, and must not copy it.
/// </para>
/// <para>
/// <b>Allocation and threading.</b> No member allocates managed memory, except to throw. The enumerator lives on the
/// stack of the world's owner thread, the only thread that can make one.
/// </para>
/// </remarks>
public ref struct ChunkEnumerator
{
    private readonly Query? _query;
    private int _entry;
    private int _chunk;
    private bool _guarded;
    private bool _finished;
    private ChunkView _current;

    /// <summary>Starts before the first chunk. The caller has entered the world's iteration guard for it.</summary>
    internal ChunkEnumerator(Query query)
    {
        _query = query;
        _chunk = -1;
        _guarded = true;
    }

    /// <summary>
    /// The current chunk: the one the last <see cref="MoveNext"/> that returned <see langword="true"/> moved to. Before
    /// the first call and after the end it is a default view, whose members throw.
    /// </summary>
    /// <remarks>Allocates nothing.</remarks>
    public readonly ChunkView Current => _current;

    /// <summary>Moves to the next matching chunk that passes the query's filters.</summary>
    /// <returns>
    /// <see langword="true"/> if there is one; <see langword="false"/> at the end, and for a default enumerator. The
    /// first <see langword="false"/> completes the enumeration (see the type's remarks); after <see cref="Dispose"/>
    /// it returns <see langword="false"/> without completing it.
    /// </returns>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The query or its world was disposed during the enumeration.</exception>
    /// <remarks>Allocates nothing. Takes time proportional to the chunks it skips.</remarks>
    public bool MoveNext()
    {
        if (_query is null || _finished)
        {
            return false;
        }

        _query.ThrowIfUnusable("ChunkEnumerator.MoveNext");
        ArchetypeMatchCache cache = _query.Cache;

        // Bounded: each pass moves to the next chunk or the next entry, and the guard keeps both counts fixed.
        while (_entry < cache.Count)
        {
            Archetype archetype = cache.GetArchetype(_entry);
            _chunk++;
            if (_chunk >= archetype.Chunks.ChunkCount)
            {
                _entry++;
                _chunk = -1;
            }
            else if (_query.PassesFilters(_entry, archetype, _chunk))
            {
                _current = new ChunkView(_query, _entry, _chunk);
                return true;
            }
        }

        _finished = true;
        _current = default;
        _query.CompleteRun();
        return false;
    }

    /// <summary>
    /// Ends the enumeration and its iteration guard: <see cref="MoveNext"/> returns <see langword="false"/> from then on.
    /// Idempotent, and ends no guard for a default enumerator.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The world has no live enumerator to end: a copy of this enumerator was disposed already.
    /// </exception>
    /// <remarks>Allocates nothing. Works after the query or the world is disposed.</remarks>
    public void Dispose()
    {
        _finished = true;
        _current = default;
        if (_guarded)
        {
            _guarded = false;
            _query!.Owner.IterationGuard.Exit();
        }
    }
}

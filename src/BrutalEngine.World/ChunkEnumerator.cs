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
/// <b>Order.</b> Chunks come archetype by archetype, in the order the archetypes were created, and in chunk order
/// within each. Each matching chunk is yielded once.
/// </para>
/// <para>
/// <b>Disposal.</b> <c>foreach</c> always disposes the enumerator; code that calls <see cref="Query.GetEnumerator"/>
/// itself must dispose it, and must not copy it. Don't make structural changes to the world (spawn, despawn, add, set
/// or remove) while an enumeration is live: they move rows between chunks.
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
    private bool _finished;
    private ChunkView _current;

    /// <summary>Starts before the first chunk.</summary>
    internal ChunkEnumerator(Query query)
    {
        _query = query;
        _chunk = -1;
    }

    /// <summary>
    /// The current chunk: the one the last <see cref="MoveNext"/> that returned <see langword="true"/> moved to. Before
    /// the first call and after the end it is a default view, whose members throw.
    /// </summary>
    /// <remarks>Allocates nothing.</remarks>
    public readonly ChunkView Current => _current;

    /// <summary>Moves to the next matching chunk.</summary>
    /// <returns>
    /// <see langword="true"/> if there is one; <see langword="false"/> at the end, after <see cref="Dispose"/>, and for
    /// a default enumerator.
    /// </returns>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The query or its world was disposed during the enumeration.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public bool MoveNext()
    {
        if (_query is null || _finished)
        {
            return false;
        }

        _query.ThrowIfUnusable("ChunkEnumerator.MoveNext");
        ArchetypeMatchCache cache = _query.Cache;

        // Bounded: each pass moves to the next chunk or the next entry, and no chunk or entry is added meanwhile.
        while (_entry < cache.Count)
        {
            Archetype archetype = cache.GetArchetype(_entry);
            _chunk++;
            if (_chunk >= archetype.Chunks.ChunkCount)
            {
                _entry++;
                _chunk = -1;
            }
            else
            {
                _current = new ChunkView(_query, _entry, _chunk);
                return true;
            }
        }

        _finished = true;
        _current = default;
        return false;
    }

    /// <summary>Ends the enumeration: <see cref="MoveNext"/> returns <see langword="false"/> from then on. Idempotent.</summary>
    /// <remarks>Allocates nothing. Works after the query or the world is disposed.</remarks>
    public void Dispose()
    {
        _finished = true;
        _current = default;
    }
}

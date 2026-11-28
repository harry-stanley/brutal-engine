using System;
using BrutalEngine.World.Queries;
using BrutalEngine.World.Storage;

namespace BrutalEngine.World;

/// <summary>
/// One chunk of a query's enumeration: its rows' entities and span access to the columns the query declared
/// (docs/design/world.md, section 5). Yielded by <see cref="ChunkEnumerator"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declared access.</b> <see cref="Read{T}"/> needs a <c>Read</c>, <c>Write</c>, optional, <c>Changed</c> or
/// <c>Added</c> term for the component on the query, and <see cref="Write{T}"/> a <c>Write</c> or
/// <c>OptionalWrite</c> term; <c>With</c> and <c>Without</c> grant no access. An optional component the chunk lacks, or
/// a tag, has no column: <see cref="Has{T}"/> tells whether the chunk has it, and <see cref="Read{T}"/> and
/// <see cref="Write{T}"/> throw for it.
/// </para>
/// <para>
/// <b>Lifetime.</b> Use a view inside the loop that produced it. Spans are valid until the enumeration ends, the next
/// structural change to the world, or the query or the world is disposed.
/// </para>
/// <para>
/// <b>Allocation and threading.</b> No member allocates managed memory, except to throw. A view lives on the stack of the
/// world's owner thread. Every member throws <see cref="InvalidOperationException"/> for a default view, and
/// <see cref="ObjectDisposedException"/> once the query or its world is disposed.
/// </para>
/// </remarks>
public readonly unsafe ref struct ChunkView
{
    private const TermFlags ReadAccess =
        TermFlags.Read | TermFlags.Write | TermFlags.OptionalRead | TermFlags.OptionalWrite | TermFlags.Changed | TermFlags.Added;

    private const TermFlags WriteAccess = TermFlags.Write | TermFlags.OptionalWrite;

    private readonly Query? _query;
    private readonly int _entry;
    private readonly int _chunk;

    /// <summary>A view of chunk <paramref name="chunk"/> of the archetype in cache entry <paramref name="entry"/>.</summary>
    internal ChunkView(Query query, int entry, int chunk)
    {
        _query = query;
        _entry = entry;
        _chunk = chunk;
    }

    /// <summary>The number of rows (entities) in the chunk, at least 1.</summary>
    /// <exception cref="InvalidOperationException">The view is default.</exception>
    /// <exception cref="ObjectDisposedException">The query or its world is disposed.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public int Count => Archetype("ChunkView.Count").Chunks.GetCount(_chunk);

    /// <summary>The chunk's entities, one per row, in row order.</summary>
    /// <exception cref="InvalidOperationException">The view is default.</exception>
    /// <exception cref="ObjectDisposedException">The query or its world is disposed.</exception>
    /// <remarks>Allocates nothing.</remarks>
    public ReadOnlySpan<Entity> Entities
    {
        get
        {
            Archetype archetype = Archetype("ChunkView.Entities");

            // Sound: a chunk starts with its rows' entity ids, one per live row.
            return new ReadOnlySpan<Entity>(archetype.Chunks.GetData(_chunk), archetype.Chunks.GetCount(_chunk));
        }
    }

    /// <summary>Returns the chunk's <typeparamref name="T"/> values, one per row, read-only. Stamps nothing.</summary>
    /// <typeparam name="T">A component the query declares with read access (see the type's remarks).</typeparam>
    /// <returns>The values, <see cref="Count"/> long.</returns>
    /// <exception cref="InvalidOperationException">
    /// The query doesn't declare read access to <typeparamref name="T"/>; or the chunk has no column for it (an optional
    /// component it lacks, or a tag); or the view is default. The message names the component and the cause.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The query or its world is disposed.</exception>
    /// <remarks>Allocates nothing, except to throw.</remarks>
    public ReadOnlySpan<T> Read<T>()
        where T : unmanaged, IComponent
    {
        T* values = Column<T>("ChunkView.Read", ReadAccess, stamp: false, out int count);

        // Sound: Column returns the chunk's live rows of a column holding T.
        return new ReadOnlySpan<T>(values, count);
    }

    /// <summary>
    /// Returns the chunk's <typeparamref name="T"/> values, one per row, writable, and stamps <typeparamref name="T"/>'s
    /// <c>Changed</c> tick in this chunk with the world's <see cref="EcsWorld.ChangeTick"/>, whether or not any row is
    /// then written.
    /// </summary>
    /// <typeparam name="T">A component the query declares with <c>Write</c> or <c>OptionalWrite</c>.</typeparam>
    /// <returns>The values, <see cref="Count"/> long.</returns>
    /// <exception cref="InvalidOperationException">
    /// The query doesn't declare write access to <typeparamref name="T"/>; or the chunk has no column for it; or the view
    /// is default. Nothing is stamped.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The query or its world is disposed.</exception>
    /// <remarks>Allocates nothing, except to throw.</remarks>
    public Span<T> Write<T>()
        where T : unmanaged, IComponent
    {
        T* values = Column<T>("ChunkView.Write", WriteAccess, stamp: true, out int count);

        // Sound: Column returns the chunk's live rows of a column holding T, which the query may write.
        return new Span<T>(values, count);
    }

    /// <summary>Returns whether the chunk's archetype has <typeparamref name="T"/>, data or tag; for optional terms.</summary>
    /// <typeparam name="T">Any component type: the test reads no data, so it needs no declared access.</typeparam>
    /// <returns><see langword="true"/> if the chunk's rows have <typeparamref name="T"/>.</returns>
    /// <exception cref="InvalidOperationException">The view is default.</exception>
    /// <exception cref="ObjectDisposedException">The query or its world is disposed.</exception>
    /// <remarks>Allocates nothing: one mask test.</remarks>
    public bool Has<T>()
        where T : unmanaged, IComponent
    {
        Archetype archetype = Archetype("ChunkView.Has");
        return _query!.Owner.Components.TryGetId<T>(out ComponentId id) && archetype.Signature.Contains(id.Index);
    }

    private Archetype Archetype(string apiName)
    {
        if (_query is null)
        {
            throw new InvalidOperationException($"{apiName}: the view is default; views come from a query's chunk enumerator.");
        }

        _query.ThrowIfUnusable(apiName);
        return _query.Cache.GetArchetype(_entry);
    }

    // The data term that declares T with one of the access flags; throws if there is none.
    private int DataTerm<T>(string apiName, TermFlags access, out Archetype archetype)
        where T : unmanaged, IComponent
    {
        archetype = Archetype(apiName);
        QueryTerms terms = _query!.Terms;
        if (_query.Owner.Components.TryGetId<T>(out ComponentId id))
        {
            for (int term = 0; term < terms.DataTermCount; term++)
            {
                QueryTerm declared = terms.GetDataTerm(term);
                if (declared.Component == id.Index && (declared.Flags & access) != 0)
                {
                    return term;
                }
            }
        }

        throw new InvalidOperationException(
            $"{apiName}<{T.Info.FullName}>: the query doesn't declare {(access == WriteAccess ? "write" : "read")} access to the component.");
    }

    private T* Column<T>(string apiName, TermFlags access, bool stamp, out int count)
        where T : unmanaged, IComponent
    {
        int term = DataTerm<T>(apiName, access, out Archetype archetype);
        int column = _query!.Cache.GetColumn(_entry, term);
        if (column < 0)
        {
            throw new InvalidOperationException(
                $"{apiName}<{T.Info.FullName}>: this chunk has no column for the component: it is an optional component the chunk doesn't have, or a tag, which has no data.");
        }

        if (stamp)
        {
            archetype.Chunks.GetChanged(_chunk)[column] = _query.Owner.ChangeTick;
        }

        // Sound: the cache's offset is T's column in this archetype's layout, which holds Layout.Capacity values of
        // sizeof(T) bytes (the registry takes the size from T), aligned to T; the row count is at most that capacity.
        count = archetype.Chunks.GetCount(_chunk);
        return (T*)(archetype.Chunks.GetData(_chunk) + _query.Cache.GetOffset(_entry, term));
    }
}

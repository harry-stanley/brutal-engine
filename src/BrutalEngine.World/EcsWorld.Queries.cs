using System;
using BrutalEngine.Core;
using BrutalEngine.World.Queries;

namespace BrutalEngine.World;

// Queries: the builder entry point and the world's list of live queries, which the world releases when it is disposed
// (docs/design/world.md, sections 1 and 5).
public sealed partial class EcsWorld
{
    // The live queries, as an intrusive doubly linked list through Query.Previous and Query.Next, so linking and
    // unlinking never allocate or fail. Newest first.
    private Query? _firstQuery;
    private int _liveQueries;

    /// <summary>Starts a query over this world: add terms to the builder, then call <see cref="QueryBuilder.Build"/>.</summary>
    /// <returns>A new builder. Allocates it (setup work).</returns>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    public QueryBuilder Query()
    {
        ThrowIfUnusable("EcsWorld.Query");
        return new QueryBuilder(this);
    }

    /// <summary>Test seam: the number of built queries not yet disposed. The engine never reads it.</summary>
    internal int LiveQueryCount => _liveQueries;

    /// <summary>Checks the owner thread and disposal for a member of a builder or query of this world.</summary>
    /// <param name="apiName">The member's name, for the exception message.</param>
    internal void ThrowIfUnusableFor(string apiName) => ThrowIfUnusable(apiName);

    /// <summary>Checks the owner thread only, for the <c>Dispose</c> of a query of this world.</summary>
    /// <param name="apiName">The member's name, for the exception message.</param>
    internal void ThrowIfNotOwnerThread(string apiName) => _owner.ThrowIfNotCurrentThread(apiName);

    /// <summary>
    /// Builds a query over <paramref name="terms"/> and adds it to the live queries. Its last-run tick slot starts at
    /// the current change tick. The caller has checked the thread and disposal.
    /// </summary>
    /// <param name="terms">One term per component type, each with its merged flags.</param>
    /// <returns>The live query.</returns>
    /// <exception cref="OutOfMemoryException">
    /// The allocator can't grow the tick-consumer list. Nothing is registered or linked, and nothing native is held.
    /// </exception>
    internal Query AddQuery(ReadOnlySpan<QueryTerm> terms)
    {
        Query query = new(this, QueryTerms.Create(terms, _components.Capacity));
        query.Next = _firstQuery;
        if (_firstQuery is not null)
        {
            _firstQuery.Previous = query;
        }

        _firstQuery = query;
        _liveQueries++;
        return query;
    }

    /// <summary>Unlinks a live query and releases its tick slot. Allocates nothing and can't fail.</summary>
    /// <param name="query">A live query of this world.</param>
    internal void RemoveQuery(Query query)
    {
        if (query.Previous is null)
        {
            _firstQuery = query.Next;
        }
        else
        {
            query.Previous.Next = query.Next;
        }

        if (query.Next is not null)
        {
            query.Next.Previous = query.Previous;
        }

        query.Previous = null;
        query.Next = null;
        _liveQueries--;
        _tickConsumers.Release(query.TickSlot);
    }

    // Releases every live query: each is marked disposed and frees its match cache. Their tick slots go with the
    // tick-consumer list, which the world disposes next. The walk is bounded by the count, so a broken link can't hang it.
    private void ReleaseQueries()
    {
        Query? query = _firstQuery;
        int count = _liveQueries;
        _firstQuery = null;
        _liveQueries = 0;
        for (int i = 0; i < count && query is not null; i++)
        {
            Query? next = query.Next;
            query.Previous = null;
            query.Next = null;
            query.Release();
            query = next;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BrutalEngine.Core;
using BrutalEngine.World.Queries;

namespace BrutalEngine.World;

/// <summary>
/// Collects a query's terms, then builds the <see cref="Query"/> (docs/design/world.md, section 5). Made by
/// <see cref="EcsWorld.Query"/>; setup work, not frame work.
/// </summary>
/// <remarks>
/// <para>
/// <b>Terms.</b> Each term names one component type. <see cref="Read{T}"/>, <see cref="Write{T}"/>,
/// <see cref="With{T}"/>, <see cref="Changed{T}"/> and <see cref="Added{T}"/> require the type; <see cref="Without{T}"/>
/// excludes it; <see cref="OptionalRead{T}"/> and <see cref="OptionalWrite{T}"/> give access to it where it is present.
/// A term on a type the world hasn't registered registers it, as <see cref="ComponentRegistry.Register{T}"/> does, and
/// throws what that throws.
/// </para>
/// <para>
/// <b>Rules.</b> A term that breaks one throws <see cref="InvalidOperationException"/> naming the term and the type, and
/// leaves the builder unchanged:
/// </para>
/// <list type="bullet">
/// <item><description>The same term twice for a type is a duplicate.</description></item>
/// <item><description>
/// A type takes at most one access term: one of <c>Read</c>, <c>Write</c>, <c>With</c>, <c>Without</c>,
/// <c>OptionalRead</c> and <c>OptionalWrite</c>.
/// </description></item>
/// <item><description><c>Without</c> with any required term on the same type is both required and excluded.</description></item>
/// <item><description><c>Changed</c> or <c>Added</c> with an optional term on the same type conflicts: a filter requires the type.</description></item>
/// </list>
/// <para>
/// <c>Changed</c> and <c>Added</c> combine with each other and with <c>Read</c>, <c>Write</c> or <c>With</c> on the same
/// type, as <c>Write&lt;T&gt;().Changed&lt;T&gt;()</c> for a system that writes the rows it selects.
/// </para>
/// <para>
/// <b>One build.</b> <see cref="Build"/> can succeed once: afterwards every member throws
/// <see cref="InvalidOperationException"/>. Start another query with <see cref="EcsWorld.Query"/>.
/// </para>
/// <para>
/// <b>Allocation and threading.</b> Terms allocate when the builder's term list grows, and when they register a type;
/// <see cref="Build"/> allocates the query. Every member is for the world's owner thread and throws
/// <see cref="WrongThreadException"/> naming the member on any other thread, and <see cref="ObjectDisposedException"/>
/// once the world is disposed.
/// </para>
/// </remarks>
public sealed class QueryBuilder
{
    private const TermFlags AccessTerms =
        TermFlags.Read | TermFlags.Write | TermFlags.With | TermFlags.Without | TermFlags.OptionalRead | TermFlags.OptionalWrite;

    private const TermFlags RequiredTerms = TermFlags.Read | TermFlags.Write | TermFlags.With | TermFlags.Changed | TermFlags.Added;
    private const TermFlags OptionalTerms = TermFlags.OptionalRead | TermFlags.OptionalWrite;
    private const TermFlags FilterTerms = TermFlags.Changed | TermFlags.Added;

    private readonly EcsWorld _world;
    private readonly List<QueryTerm> _terms = [];
    private bool _built;

    internal QueryBuilder(EcsWorld world) => _world = world;

    /// <summary>Requires <typeparamref name="T"/>, with read access to its column.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The term breaks a rule (see the class remarks), or the builder has built its query.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    public QueryBuilder Read<T>()
        where T : unmanaged, IComponent => Add<T>(TermFlags.Read, "QueryBuilder.Read");

    /// <summary>Requires <typeparamref name="T"/>, with write access to its column.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The term breaks a rule (see the class remarks), or the builder has built its query.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    public QueryBuilder Write<T>()
        where T : unmanaged, IComponent => Add<T>(TermFlags.Write, "QueryBuilder.Write");

    /// <summary>Requires <typeparamref name="T"/>, with no access to its data: for tags, or a type the loop doesn't touch.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The term breaks a rule (see the class remarks), or the builder has built its query.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    public QueryBuilder With<T>()
        where T : unmanaged, IComponent => Add<T>(TermFlags.With, "QueryBuilder.With");

    /// <summary>Excludes <typeparamref name="T"/>: a matching archetype doesn't have it.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The term breaks a rule (see the class remarks), or the builder has built its query.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    public QueryBuilder Without<T>()
        where T : unmanaged, IComponent => Add<T>(TermFlags.Without, "QueryBuilder.Without");

    /// <summary>Gives read access to <typeparamref name="T"/> where it is present, without requiring it.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The term breaks a rule (see the class remarks), or the builder has built its query.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    public QueryBuilder OptionalRead<T>()
        where T : unmanaged, IComponent => Add<T>(TermFlags.OptionalRead, "QueryBuilder.OptionalRead");

    /// <summary>Gives write access to <typeparamref name="T"/> where it is present, without requiring it.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The term breaks a rule (see the class remarks), or the builder has built its query.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    public QueryBuilder OptionalWrite<T>()
        where T : unmanaged, IComponent => Add<T>(TermFlags.OptionalWrite, "QueryBuilder.OptionalWrite");

    /// <summary>
    /// Requires <typeparamref name="T"/>, with read access, and filters chunks to those whose <c>Changed</c> tick for it
    /// is newer than the query's <see cref="Query.LastRunTick"/>.
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The term breaks a rule (see the class remarks), or the builder has built its query.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    public QueryBuilder Changed<T>()
        where T : unmanaged, IComponent => Add<T>(TermFlags.Changed, "QueryBuilder.Changed");

    /// <summary>
    /// Requires <typeparamref name="T"/>, with read access, and filters chunks to those whose <c>Added</c> tick for it is
    /// newer than the query's <see cref="Query.LastRunTick"/>.
    /// </summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The term breaks a rule (see the class remarks), or the builder has built its query.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    public QueryBuilder Added<T>()
        where T : unmanaged, IComponent => Add<T>(TermFlags.Added, "QueryBuilder.Added");

    /// <summary>
    /// Builds the query over the terms so far; with no terms it matches every archetype. Its cache starts empty and is
    /// filled at first use, and its <see cref="Query.LastRunTick"/> starts at the world's current change tick.
    /// </summary>
    /// <returns>The query, owned by the world.</returns>
    /// <exception cref="InvalidOperationException">The builder has built its query already.</exception>
    /// <exception cref="WrongThreadException">Called on a thread other than the owner thread.</exception>
    /// <exception cref="ObjectDisposedException">The world is disposed.</exception>
    /// <exception cref="OutOfMemoryException">
    /// The allocator can't grow the world's list of last-run ticks. Nothing is built, and the builder can build again.
    /// </exception>
    public Query Build()
    {
        ThrowIfUnusable("QueryBuilder.Build");
        Query query = _world.AddQuery(CollectionsMarshal.AsSpan(_terms));
        _built = true;
        return query;
    }

    private QueryBuilder Add<T>(TermFlags term, string apiName)
        where T : unmanaged, IComponent
    {
        ThrowIfUnusable(apiName);
        int component = _world.Components.Register<T>().Index;
        int at = IndexOf(component);
        TermFlags existing = at < 0 ? TermFlags.None : _terms[at].Flags;
        string? conflict = Conflict(existing, term);
        if (conflict is not null)
        {
            throw new InvalidOperationException($"{apiName}<{T.Info.FullName}>: {conflict}");
        }

        if (at < 0)
        {
            _terms.Add(new QueryTerm(component, term));
        }
        else
        {
            _terms[at] = new QueryTerm(component, existing | term);
        }

        return this;
    }

    // The rule a new term breaks against the type's existing ones, as the message's reason, or null if it breaks none.
    private static string? Conflict(TermFlags existing, TermFlags added)
    {
        if ((existing & added) != 0)
        {
            return "the query already has this term for the type.";
        }

        TermFlags combined = existing | added;
        if ((combined & TermFlags.Without) != 0 && (combined & RequiredTerms) != 0)
        {
            return "the type would be both required and excluded.";
        }

        if ((existing & AccessTerms) != 0 && (added & AccessTerms) != 0)
        {
            return "the type already has an access term; give each type one of Read, Write, With, Without, OptionalRead and OptionalWrite.";
        }

        return (combined & OptionalTerms) != 0 && (combined & FilterTerms) != 0
            ? "a Changed or Added filter requires the type, so it can't also be optional."
            : null;
    }

    private int IndexOf(int component)
    {
        for (int i = 0; i < _terms.Count; i++)
        {
            if (_terms[i].Component == component)
            {
                return i;
            }
        }

        return -1;
    }

    private void ThrowIfUnusable(string apiName)
    {
        _world.ThrowIfUnusableFor(apiName);
        if (_built)
        {
            throw new InvalidOperationException($"{apiName}: the builder has built its query already; start another with EcsWorld.Query().");
        }
    }
}

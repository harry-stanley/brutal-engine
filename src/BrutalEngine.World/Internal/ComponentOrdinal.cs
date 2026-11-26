using System;
using System.Threading;

namespace BrutalEngine.World.Internal;

/// <summary>
/// Hands out process-wide component ordinals: one per component type, assigned on first use and never changed.
/// Registries map ordinals to their own dense ids, so a type lookup is one static load and one array read.
/// </summary>
/// <remarks>
/// The state is two kinds of static <see cref="int"/>: this counter and each <see cref="ComponentOrdinal{T}.Value"/>.
/// Neither can reference a game type, so the architecture test's static-state rule allows them, and a game assembly
/// that unloads takes its <see cref="ComponentOrdinal{T}"/> instantiations with it. The authoritative mapping stays in
/// the instance-owned registries. Every member is thread-safe and allocates nothing.
/// </remarks>
internal static class ComponentOrdinal
{
    // The last ordinal handed out; 0 until the first. Ordinals start at 1, so 0 always means "not assigned".
    private static int _last;

    /// <summary>Returns <typeparamref name="T"/>'s ordinal, assigning one on first use.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <returns>The ordinal, at least 1. Every thread gets the same value for the same type.</returns>
    /// <exception cref="InvalidOperationException">The process has used every ordinal.</exception>
    internal static int Get<T>()
        where T : unmanaged, IComponent
    {
        int ordinal = Volatile.Read(ref ComponentOrdinal<T>.Value);
        return ordinal != 0 ? ordinal : Assign<T>();
    }

    /// <summary>
    /// Reserves a new ordinal, never handed out before. <see cref="Get{T}"/> uses it; tests use it to register
    /// made-up types through the registry's internal seam.
    /// </summary>
    /// <returns>The ordinal, at least 1.</returns>
    /// <exception cref="InvalidOperationException">The process has used every ordinal.</exception>
    internal static int Reserve()
    {
        int ordinal = Interlocked.Increment(ref _last);

        // Each type takes one ordinal, plus one per lost first-use race, so this is out of reach in practice; the check
        // keeps a wrapped counter from ever handing out an ordinal twice.
        if (ordinal <= 0)
        {
            _ = Interlocked.Exchange(ref _last, int.MinValue);
            throw new InvalidOperationException("The process has run out of component ordinals.");
        }

        return ordinal;
    }

    private static int Assign<T>()
        where T : unmanaged, IComponent
    {
        int candidate = Reserve();

        // The first thread to publish wins; a thread that lost the race uses the winner's ordinal, and its own
        // candidate stays unused.
        int published = Interlocked.CompareExchange(ref ComponentOrdinal<T>.Value, candidate, 0);
        return published == 0 ? candidate : published;
    }
}

/// <summary>The process-wide ordinal of <typeparamref name="T"/>, or 0 until <see cref="ComponentOrdinal.Get{T}"/> assigns it.</summary>
/// <typeparam name="T">The component type.</typeparam>
internal static class ComponentOrdinal<T>
    where T : unmanaged, IComponent
{
    /// <summary>
    /// The ordinal, or 0 while unassigned. Written once, by <see cref="ComponentOrdinal.Get{T}"/> with a
    /// compare-exchange, and never changed afterwards.
    /// </summary>
    internal static int Value;
}

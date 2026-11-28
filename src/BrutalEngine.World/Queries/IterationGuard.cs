using System;

namespace BrutalEngine.World.Queries;

/// <summary>
/// A world's iteration guard: counts its live chunk enumerators, and makes structural changes throw while any is live
/// (docs/design/world.md, section 5, "Iteration guard"). Nested enumeration only raises the count.
/// </summary>
/// <remarks>
/// <see cref="Enter"/> runs when <see cref="Query.GetEnumerator"/> hands out an enumerator and <see cref="Exit"/> when
/// that enumerator is disposed. Every member allocates nothing, except to throw. Not thread-safe: the owning world's
/// owner thread is its only caller.
/// </remarks>
internal sealed class IterationGuard
{
    private int _live;

    /// <summary>The number of live chunk enumerators of the world.</summary>
    public int LiveCount => _live;

    /// <summary>Records a new live enumerator.</summary>
    /// <exception cref="OverflowException">More than <see cref="int.MaxValue"/> enumerators are live.</exception>
    public void Enter() => _live = checked(_live + 1);

    /// <summary>Records that a live enumerator was disposed.</summary>
    /// <exception cref="InvalidOperationException">
    /// No enumerator is live: more were disposed than started, which happens when an enumerator is copied and both
    /// copies are disposed. The count stays 0.
    /// </exception>
    public void Exit()
    {
        if (_live == 0)
        {
            throw new InvalidOperationException(
                "A chunk enumerator was disposed with none live; a ChunkEnumerator must not be copied, as each copy's Dispose ends the iteration once.");
        }

        _live--;
    }

    /// <summary>Throws if any chunk enumerator of the world is live.</summary>
    /// <param name="apiName">The structural member being called, for the message.</param>
    /// <exception cref="InvalidOperationException">An enumerator is live. The message names the member.</exception>
    public void ThrowIfLive(string apiName)
    {
        if (_live != 0)
        {
            throw new InvalidOperationException(
                $"{apiName} is a structural change, which can't run while a chunk enumeration of the world is live; finish or dispose every query enumerator first.");
        }
    }
}

namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.Barrier"/> record: how many resource transitions follow it
/// (docs/design/graphics.md, "The frame plan" and section 10).
/// </summary>
/// <remarks>
/// <para>
/// The record's trailing part is <see cref="Count"/> <see cref="ResourceTransition"/> elements. The front end's
/// planner writes a barrier record before the work that needs it, and a backend turns the whole record into one
/// native barrier call, so the transitions in one record don't depend on each other.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct BarrierCommand
{
    /// <summary>The number of <see cref="ResourceTransition"/> elements in the trailing part: at least 1.</summary>
    public int Count { get; init; }
}

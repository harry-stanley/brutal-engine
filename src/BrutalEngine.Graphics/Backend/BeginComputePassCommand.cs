namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.BeginComputePass"/> record: the start of a compute pass
/// (docs/design/graphics.md, "The frame plan" and section 9).
/// </summary>
/// <remarks>
/// <para>
/// A compute pass is one synchronisation scope (section 10). The matching
/// <see cref="CommandKind.EndComputePass"/> record ends it. The record has no trailing part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct BeginComputePassCommand
{
    /// <summary>
    /// The pass's label, which names its debug group and its timestamps; <c>default</c> for an unlabelled pass. A
    /// backend reads the name with <c>BackendObjectTable.GetLabel</c>.
    /// </summary>
    public DebugLabel Label { get; init; }

    /// <summary>
    /// The timestamp query index the backend writes the pass's start time to, or -1 for none (an unlabelled pass, or
    /// <c>TimestampQueries</c> not enabled).
    /// </summary>
    public int TimestampQueryIndex { get; init; }
}

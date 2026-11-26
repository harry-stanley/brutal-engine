namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.EndRenderPass"/> or <see cref="CommandKind.EndComputePass"/> record:
/// the end of the open pass (docs/design/graphics.md, "The frame plan").
/// </summary>
/// <remarks>
/// <para>
/// The record's kind says which kind of pass ends; the payload is the same for both. The record has no trailing
/// part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct EndPassCommand
{
    /// <summary>
    /// The timestamp query index the backend writes the pass's end time to, or -1 for none. The front end assigns
    /// two indices per labelled pass (section 11): the begin record's and this one.
    /// </summary>
    public int TimestampQueryIndex { get; init; }
}

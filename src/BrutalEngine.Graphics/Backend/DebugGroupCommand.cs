namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.PushDebugGroup"/> or <see cref="CommandKind.PopDebugGroup"/> record: a
/// debug group opening or closing (docs/design/graphics.md, "The frame plan" and section 9).
/// </summary>
/// <remarks>
/// <para>
/// The record's kind says whether the group opens or closes; the payload is the same for both. Groups balance within
/// a pass and within the frame, which the front end checked when it encoded them. The record has no trailing part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct DebugGroupCommand
{
    /// <summary>
    /// The group's label: for a push, the group it opens; for a pop, the innermost open group, the one it closes. A
    /// backend reads the name with <c>BackendObjectTable.GetLabel</c>.
    /// </summary>
    public DebugLabel Label { get; init; }
}

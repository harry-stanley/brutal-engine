namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.SetBindGroup"/> record: a bind group bound at a group index for the
/// draws or dispatches after it (docs/design/graphics.md, "The frame plan" and section 9).
/// </summary>
/// <remarks>
/// <para>
/// The front end checked that the group matches the bound pipeline layout's group at <see cref="Index"/> before
/// it encoded the record. The record has no trailing part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct SetBindGroupCommand
{
    /// <summary>The group index: at least 0 and less than the pipeline layout's group count.</summary>
    public int Index { get; init; }

    /// <summary>The bind group's id: non-zero, the id the backend returned when it created the group.</summary>
    public ulong BindGroup { get; init; }
}

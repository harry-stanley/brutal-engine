namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.Dispatch"/> record: a compute dispatch (docs/design/graphics.md, "The
/// frame plan" and section 9).
/// </summary>
/// <remarks>
/// <para>
/// The record has no trailing part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct DispatchCommand
{
    /// <summary>The number of workgroups along X: at least 1.</summary>
    public int GroupCountX { get; init; }

    /// <summary>The number of workgroups along Y: at least 1.</summary>
    public int GroupCountY { get; init; }

    /// <summary>The number of workgroups along Z: at least 1.</summary>
    public int GroupCountZ { get; init; }
}

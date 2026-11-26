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
/// The group counts are recorded as the encoder was given them: section 9's encoding rules don't check them. The
/// Vulkan backend passes them to <c>vkCmdDispatch</c>, which takes unsigned counts and requires each to be at most the
/// device's <c>maxComputeWorkGroupCount</c> for that axis.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct DispatchCommand
{
    /// <summary>The number of workgroups along X, as the encoder was given it.</summary>
    public int GroupCountX { get; init; }

    /// <summary>The number of workgroups along Y, as the encoder was given it.</summary>
    public int GroupCountY { get; init; }

    /// <summary>The number of workgroups along Z, as the encoder was given it.</summary>
    public int GroupCountZ { get; init; }
}

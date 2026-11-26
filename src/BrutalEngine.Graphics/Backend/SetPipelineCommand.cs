namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.SetPipeline"/> record: the pipeline the draws or dispatches after it use
/// (docs/design/graphics.md, "The frame plan" and section 9).
/// </summary>
/// <remarks>
/// <para>
/// A render pass binds graphics pipelines and a compute pass compute pipelines; <see cref="IsCompute"/> says which
/// kind the id names, so a backend binds it at the right bind point without a lookup. The record has no trailing
/// part.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct SetPipelineCommand
{
    /// <summary>The pipeline's id: non-zero, the id the backend returned when it created the pipeline.</summary>
    public ulong Pipeline { get; init; }

    /// <summary>
    /// <see langword="true"/> for a compute pipeline; <see langword="false"/> for a graphics pipeline.
    /// </summary>
    public bool IsCompute { get; init; }
}

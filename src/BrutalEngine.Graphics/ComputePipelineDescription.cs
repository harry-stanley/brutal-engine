namespace BrutalEngine.Graphics;

/// <summary>Describes a compute pipeline: its layout and compute shader.</summary>
/// <remarks>
/// A <see langword="sealed"/> record class, like <see cref="GraphicsPipelineDescription"/>: pipelines are created at load
/// time, never per frame (docs/design/graphics.md section 7). A <see langword="null"/> or empty entry point throws
/// <see cref="System.ArgumentException"/> when the pipeline is created.
/// Immutable once initialized, and safe to read from any thread. Equality compares every property; the debug name and
/// entry point compare ordinally. The device keeps no reference to the description. No member allocates, except
/// <see cref="object.ToString"/> and the compiler-generated <c>with</c> copy.
/// </remarks>
public sealed record ComputePipelineDescription
{
    /// <summary>The pipeline's layout. It must be live when the pipeline is created; the pipeline doesn't keep it alive.</summary>
    public required PipelineLayoutHandle Layout { get; init; }

    /// <summary>The compute shader's entry point.</summary>
    public required ShaderStage Compute { get; init; }

    /// <summary>
    /// An optional name for debuggers, validation messages, errors and the leak report (docs/design/graphics.md
    /// section 13); <see langword="null"/> for none.
    /// </summary>
    public string? DebugName { get; init; }
}

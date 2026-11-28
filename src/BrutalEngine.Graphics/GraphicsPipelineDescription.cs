using System;
using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>
/// Describes a graphics pipeline: its layout, shaders, vertex input, rasterization, depth and color targets, all baked
/// at creation.
/// </summary>
/// <remarks>
/// <para>
/// Render target formats are part of the pipeline (<see cref="ColorTargets"/>, <see cref="Depth"/>'s format and
/// <see cref="SampleCount"/>), because dynamic rendering has no render pass objects. Viewport and scissor are always
/// dynamic; everything else is baked, so a pipeline never recompiles at draw time (docs/design/graphics.md section 7).
/// The device checks the description's structure when the pipeline is created: entry points not empty, at least one
/// color target or a depth state, a sample count of 1, 2, 4, 8 or 16, and vertex attribute locations unique with each
/// attribute inside its buffer's stride.
/// </para>
/// <para>
/// A <see langword="sealed"/> record class: it holds arrays and is created at load time, never per frame. Immutable once
/// initialized, and safe to read from any thread. Equality compares every property; the memory properties compare by
/// reference, as <see cref="ReadOnlyMemory{T}"/> does, and the debug name ordinally. The device keeps no reference to the
/// description. No member allocates, except <see cref="ToString"/> and the compiler-generated <c>with</c> copy.
/// </para>
/// </remarks>
public sealed record GraphicsPipelineDescription
{
    /// <summary>The pipeline's layout. It must be live when the pipeline is created; the pipeline doesn't keep it alive.</summary>
    public required PipelineLayoutHandle Layout { get; init; }

    /// <summary>The vertex shader's entry point.</summary>
    public required ShaderStage Vertex { get; init; }

    /// <summary>The fragment shader's entry point, or <see langword="null"/> for none (a depth-only pipeline).</summary>
    public ShaderStage? Fragment { get; init; }

    /// <summary>The vertex buffers the pipeline reads, by slot; empty (the default) when the vertex shader reads none.</summary>
    public ReadOnlyMemory<VertexBufferLayout> VertexBuffers { get; init; }

    /// <summary>How vertices form primitives; <see cref="PrimitiveTopology.TriangleList"/> by default.</summary>
    public PrimitiveTopology Topology { get; init; }

    /// <summary>Culling and winding; no culling and counter-clockwise front faces by default.</summary>
    public RasterState Raster { get; init; }

    /// <summary>The depth target and test, or <see langword="null"/> (the default) for none. There is no stencil in M1.</summary>
    public DepthState? Depth { get; init; }

    /// <summary>The color targets, by attachment index; may be empty only when <see cref="Depth"/> is set.</summary>
    public required ReadOnlyMemory<ColorTargetState> ColorTargets { get; init; }

    /// <summary>The samples per pixel of every target: 1 (the default), 2, 4, 8 or 16.</summary>
    public int SampleCount { get; init; } = 1;

    /// <summary>
    /// An optional name for debuggers, validation messages, errors and the leak report (docs/design/graphics.md
    /// section 13); <see langword="null"/> for none.
    /// </summary>
    public string? DebugName { get; init; }

    /// <summary>
    /// Returns every property in the record's usual form, for example
    /// <c>GraphicsPipelineDescription { Layout = PipelineLayout(1:1), ..., SampleCount = 1, DebugName = sprites }</c>; the
    /// memory properties show their element type and length.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is, so negative values
    /// always use an ASCII minus sign. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"GraphicsPipelineDescription {{ Layout = {Layout}, Vertex = {Vertex}, Fragment = {Fragment}, VertexBuffers = {VertexBuffers}, Topology = {Topology}, Raster = {Raster}, Depth = {Depth}, ColorTargets = {ColorTargets}, SampleCount = {SampleCount}, DebugName = {DebugName} }}");
}

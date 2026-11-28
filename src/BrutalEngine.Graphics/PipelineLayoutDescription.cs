using System;

namespace BrutalEngine.Graphics;

/// <summary>Describes a pipeline layout: the bind group layouts and push constants a pipeline's shaders use.</summary>
/// <remarks>
/// <para>
/// Layouts are declared in C# next to the shaders they serve: M1 has no shader reflection data, so the code that owns a
/// shader declares its layout (docs/design/graphics.md section 7). A later cooker adds reflection blobs, and a conformance
/// check then compares declared layouts with reflected ones.
/// </para>
/// <para>
/// An immutable value: safe to copy and to use from any thread. Equality compares <see cref="BindGroupLayouts"/> by
/// reference, as <see cref="ReadOnlyMemory{T}"/> does, and the debug name ordinally. The device keeps no reference to the
/// description or its <see cref="DebugName"/>. No member allocates, except <see cref="object.ToString"/>.
/// </para>
/// </remarks>
public readonly record struct PipelineLayoutDescription
{
    /// <summary>
    /// The bind group layouts, where the index is the group (Vulkan descriptor set) number that shaders declare. May be
    /// empty. The layouts must be live; the pipeline layout doesn't keep them alive.
    /// </summary>
    public required ReadOnlyMemory<BindGroupLayoutHandle> BindGroupLayouts { get; init; }

    /// <summary>
    /// The push-constant range; the default, with <see cref="PushConstantRange.Size"/> 0, declares none. Its size is at
    /// most <see cref="GraphicsLimits.MaxPushConstantBytes"/>.
    /// </summary>
    public PushConstantRange PushConstants { get; init; }

    /// <summary>
    /// An optional name for debuggers, validation messages, errors and the leak report (docs/design/graphics.md
    /// section 13); <see langword="null"/> for none.
    /// </summary>
    public string? DebugName { get; init; }
}

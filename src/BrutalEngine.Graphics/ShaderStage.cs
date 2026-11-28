namespace BrutalEngine.Graphics;

/// <summary>One programmable stage of a pipeline: an entry point in a shader module.</summary>
/// <param name="Module">The shader module that holds the entry point. It must be live when the pipeline is created.</param>
/// <param name="EntryPoint">
/// The name of the entry point, as the shader source declares it: neither <see langword="null"/> nor empty.
/// </param>
/// <remarks>
/// Used by <see cref="GraphicsPipelineDescription"/> and <see cref="ComputePipelineDescription"/>; which stage it is
/// comes from the property that holds it. Not to be confused with <see cref="ShaderStages"/>, the flags naming stages
/// in a layout (docs/design/graphics.md section 7). The pipeline doesn't keep the module alive: a module may be
/// destroyed once its pipelines are created.
/// An immutable value: safe to copy and to use from any thread. Equality compares the module handle and the entry point
/// ordinally. No member allocates, except <see cref="object.ToString"/>.
/// </remarks>
public readonly record struct ShaderStage(ShaderModuleHandle Module, string EntryPoint);

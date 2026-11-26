namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The kind of a backend object, passed with its id wherever one call serves every kind (debug names, destruction), and
/// naming the table a handle resolves through (docs/design/graphics.md, "The backend interface").
/// </summary>
/// <remarks>
/// <para>
/// One member per handle kind that maps to a backend id. Swapchains and surfaces have their own calls and ids, and debug
/// labels resolve to names, so none of them is a kind here.
/// </para>
/// <para>
/// The numeric values are fixed. They are no native API's values, so backends map them by name, never by casting.
/// </para>
/// </remarks>
public enum BackendObjectKind
{
    /// <summary>A buffer, from <see cref="BufferHandle"/>. The default value.</summary>
    Buffer = 0,

    /// <summary>A texture, from <see cref="TextureHandle"/>.</summary>
    Texture = 1,

    /// <summary>A texture view, from <see cref="TextureViewHandle"/>.</summary>
    TextureView = 2,

    /// <summary>A sampler, from <see cref="SamplerHandle"/>.</summary>
    Sampler = 3,

    /// <summary>A shader module, from <see cref="ShaderModuleHandle"/>.</summary>
    ShaderModule = 4,

    /// <summary>A bind group layout, from <see cref="BindGroupLayoutHandle"/>.</summary>
    BindGroupLayout = 5,

    /// <summary>A pipeline layout, from <see cref="PipelineLayoutHandle"/>.</summary>
    PipelineLayout = 6,

    /// <summary>
    /// A bind group, from <see cref="BindGroupHandle"/>. Persistent bind groups are destroyed one by one; frame-lifetime
    /// bind groups are released by their frame slot's pool reset and never destroyed individually.
    /// </summary>
    BindGroup = 7,

    /// <summary>A graphics pipeline, from <see cref="GraphicsPipelineHandle"/>.</summary>
    GraphicsPipeline = 8,

    /// <summary>A compute pipeline, from <see cref="ComputePipelineHandle"/>.</summary>
    ComputePipeline = 9,
}

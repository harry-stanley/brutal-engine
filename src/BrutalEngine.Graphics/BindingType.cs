namespace BrutalEngine.Graphics;

/// <summary>The kind of resource a bind group layout entry declares, and how shaders access it.</summary>
/// <remarks>
/// Each entry of a bind group must give a resource of its binding's type: a buffer for the buffer types, a texture view
/// for the texture types, a sampler for <see cref="Sampler"/> (docs/design/graphics.md section 8).
/// </remarks>
public enum BindingType
{
    /// <summary>A uniform buffer, read-only. The default value.</summary>
    UniformBuffer = 0,

    /// <summary>A storage buffer that shaders read and write.</summary>
    StorageBuffer = 1,

    /// <summary>A storage buffer that shaders only read.</summary>
    ReadOnlyStorageBuffer = 2,

    /// <summary>A texture view that shaders sample.</summary>
    SampledTexture = 3,

    /// <summary>A texture view that shaders read and write as a storage image.</summary>
    StorageTexture = 4,

    /// <summary>A sampler.</summary>
    Sampler = 5,
}

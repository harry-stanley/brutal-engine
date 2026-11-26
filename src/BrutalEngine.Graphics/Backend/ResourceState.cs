namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// How a frame uses a buffer or a texture range at a point in its command stream: one side of a
/// <see cref="ResourceTransition"/> (docs/design/graphics.md, "The backend interface" and section 10).
/// </summary>
/// <remarks>
/// <para>
/// The front end's planner derives every state from what the frame recorded and inserts each transition; a backend keeps
/// no state of its own and maps each value to a fixed set of pipeline stages, accesses and, for textures, an image layout.
/// The shader states (<see cref="UniformRead"/>, <see cref="StorageRead"/>, <see cref="StorageReadWrite"/> and
/// <see cref="SampledRead"/>) take their stages from the transition's <see cref="ResourceTransition.BeforeStages"/> or
/// <see cref="ResourceTransition.AfterStages"/>.
/// </para>
/// <para>
/// The numeric values are fixed. They are no native API's values, so backends map them by name, never by casting.
/// </para>
/// </remarks>
public enum ResourceState
{
    /// <summary>
    /// No prior use whose contents matter: a texture's contents are discarded when it leaves this state. The default
    /// value, and the state of a new resource.
    /// </summary>
    Undefined = 0,

    /// <summary>Read by a copy.</summary>
    CopySource = 1,

    /// <summary>Written by a copy or a clear.</summary>
    CopyDestination = 2,

    /// <summary>A buffer read as vertex input.</summary>
    VertexBuffer = 3,

    /// <summary>A buffer read as index input.</summary>
    IndexBuffer = 4,

    /// <summary>A buffer read as indirect draw or dispatch arguments.</summary>
    IndirectBuffer = 5,

    /// <summary>A buffer read as a uniform buffer by the transition's shader stages.</summary>
    UniformRead = 6,

    /// <summary>A buffer or texture read as storage by the transition's shader stages.</summary>
    StorageRead = 7,

    /// <summary>A buffer or texture read and written as storage by the transition's shader stages.</summary>
    StorageReadWrite = 8,

    /// <summary>A texture sampled by the transition's shader stages.</summary>
    SampledRead = 9,

    /// <summary>A texture rendered to as a color attachment.</summary>
    ColorTarget = 10,

    /// <summary>A depth texture tested but not written.</summary>
    DepthRead = 11,

    /// <summary>A depth texture tested and written.</summary>
    DepthReadWrite = 12,

    /// <summary>A swapchain image ready to present.</summary>
    Present = 13,
}

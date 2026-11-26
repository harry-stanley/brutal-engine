namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// One barrier element: a buffer or a texture range moving from one <see cref="ResourceState"/> to another at a point in
/// a frame (docs/design/graphics.md, "The backend interface" and section 10).
/// </summary>
/// <remarks>
/// <para>
/// The front end's planner writes these into the frame's command stream as the trailing part of a barrier record, with
/// every resource already resolved to its backend id. A backend turns each one into a native barrier with the stages,
/// accesses and layouts its two states map to; it keeps no state between transitions and may assume the planner's
/// <see cref="Before"/> is the resource's real state.
/// </para>
/// <para>
/// For a buffer, the mip and layer fields are 0 and the transition covers the whole buffer. For a texture they name the
/// range it covers, already inside the texture. <see cref="BeforeStages"/> and <see cref="AfterStages"/> are used only
/// for the shader states (<see cref="ResourceState.UniformRead"/>, <see cref="ResourceState.StorageRead"/>,
/// <see cref="ResourceState.StorageReadWrite"/> and <see cref="ResourceState.SampledRead"/>) and are
/// <see cref="ShaderStages.None"/> otherwise.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes. Immutable and safe to copy between threads; no member
/// allocates.
/// </para>
/// </remarks>
public readonly struct ResourceTransition
{
    /// <summary>The buffer or texture id: non-zero, the id the backend returned when it created the resource.</summary>
    public ulong Resource { get; init; }

    /// <summary><see langword="true"/> when <see cref="Resource"/> is a texture id; <see langword="false"/> for a buffer id.</summary>
    public bool IsTexture { get; init; }

    /// <summary>The state the resource or range is in before the barrier.</summary>
    public ResourceState Before { get; init; }

    /// <summary>The state the resource or range is in after the barrier.</summary>
    public ResourceState After { get; init; }

    /// <summary>The shader stages that used the resource in <see cref="Before"/>, for a shader state.</summary>
    public ShaderStages BeforeStages { get; init; }

    /// <summary>The shader stages that will use the resource in <see cref="After"/>, for a shader state.</summary>
    public ShaderStages AfterStages { get; init; }

    /// <summary>The first mip level the transition covers, for a texture; 0 for a buffer.</summary>
    public int BaseMipLevel { get; init; }

    /// <summary>The number of mip levels the transition covers, at least 1 for a texture; 0 for a buffer.</summary>
    public int MipLevelCount { get; init; }

    /// <summary>The first array layer the transition covers, for a texture; 0 for a buffer.</summary>
    public int BaseArrayLayer { get; init; }

    /// <summary>The number of array layers the transition covers, at least 1 for a texture; 0 for a buffer.</summary>
    public int ArrayLayerCount { get; init; }
}

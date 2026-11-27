namespace BrutalEngine.Cook.Shaders;

/// <summary>The pipeline stage of a shader entry point, as named on the command line (<c>vertex</c>, <c>fragment</c>, <c>compute</c>).</summary>
internal enum ShaderStage
{
    /// <summary>A vertex shader.</summary>
    Vertex,

    /// <summary>A fragment (pixel) shader.</summary>
    Fragment,

    /// <summary>A compute shader.</summary>
    Compute,
}

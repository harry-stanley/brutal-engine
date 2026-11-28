namespace BrutalEngine.Graphics;

/// <summary>How long a bind group lives.</summary>
/// <remarks>
/// docs/design/graphics.md section 8: persistent groups belong to load time; frame groups are made in the frame loop
/// without a managed allocation, and are the way to bind transient allocations.
/// </remarks>
public enum BindGroupLifetime
{
    /// <summary>Lives until destroyed. Creating one allocates a copy of its entries for usage tracking. The default value.</summary>
    Persistent = 0,

    /// <summary>Valid only in the frame it was created in, and released with it. Creating one outside an open frame throws <see cref="System.InvalidOperationException"/>; using one in a later frame throws <see cref="Core.StaleHandleException"/>.</summary>
    Frame = 1,
}

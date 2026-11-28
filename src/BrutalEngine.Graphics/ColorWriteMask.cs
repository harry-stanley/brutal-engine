using System;

namespace BrutalEngine.Graphics;

/// <summary>The channels of a color target a pipeline writes, combined with <c>|</c>.</summary>
/// <remarks>
/// Each flag's value is its Vulkan <c>VkColorComponentFlags</c> value (docs/design/graphics.md section 7). A channel
/// left out keeps the value already in the target.
/// </remarks>
[Flags]
public enum ColorWriteMask
{
    /// <summary>No channels: the target is left unchanged.</summary>
    None = 0,

    /// <summary>The red channel.</summary>
    Red = 1,

    /// <summary>The green channel.</summary>
    Green = 2,

    /// <summary>The blue channel.</summary>
    Blue = 4,

    /// <summary>The alpha channel.</summary>
    Alpha = 8,

    /// <summary>Every channel; <see cref="ColorTargetState"/>'s positional constructor default (<c>default</c> gives <see cref="None"/>).</summary>
    All = Red | Green | Blue | Alpha,
}

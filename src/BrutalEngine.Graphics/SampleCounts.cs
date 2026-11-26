using System;

namespace BrutalEngine.Graphics;

/// <summary>A set of multisample counts, one flag per samples-per-pixel value.</summary>
/// <remarks>
/// Each flag's value is its sample count, as in Vulkan's <c>VkSampleCountFlags</c>. MSAA is only declared in this
/// version: how it is negotiated is fixed (<see cref="GraphicsLimits.ColorSampleCounts"/> and
/// <see cref="FormatSupport.Multisample"/>), and multisampled targets come later (docs/design/graphics.md section 2).
/// </remarks>
[Flags]
public enum SampleCounts
{
    /// <summary>No sample counts.</summary>
    None = 0,

    /// <summary>One sample per pixel: no multisampling.</summary>
    X1 = 1,

    /// <summary>Two samples per pixel.</summary>
    X2 = 2,

    /// <summary>Four samples per pixel.</summary>
    X4 = 4,

    /// <summary>Eight samples per pixel.</summary>
    X8 = 8,

    /// <summary>Sixteen samples per pixel.</summary>
    X16 = 16,
}

using System;
using BrutalEngine.Core;
using BrutalEngine.Platform;

namespace BrutalEngine.TestUtilities.Tests;

/// <summary>A Vulkan surface source that needs no instance extensions; the fake backend never creates a native surface.</summary>
internal sealed class TestSurfaceSource : IVulkanSurfaceSource
{
    public int RequiredInstanceExtensionCount => 0;

    public ReadOnlySpan<byte> GetRequiredInstanceExtension(int index) => throw new ArgumentOutOfRangeException(nameof(index));

    public Result<nint> CreateVulkanSurface(nint instance) => throw new NotSupportedException("The fake backend creates no native surface.");
}

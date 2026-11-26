using System;
using System.IO;
using BrutalEngine.Core.Logging;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests;

public sealed class VulkanBackendOptionsTests
{
    [Fact]
    public void Defaults_NoLoaderPathAndEmptyAllowList()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });

        VulkanBackendOptions options = new() { AppDirectory = AppContext.BaseDirectory, Logger = logger };

        Assert.Null(options.LoaderPath);
        Assert.True(options.ValidationAllowList.IsEmpty);
    }

    [Fact]
    public void Equals_SameAllowListArray_IsEqualButCopiedContentsAreNot()
    {
        // ReadOnlyMemory compares the region, not the contents, as the record's remarks say.
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        ValidationAllowance[] allowList = [new(0x1234, "known layer false positive")];
        VulkanBackendOptions options = new() { AppDirectory = Path.GetTempPath(), Logger = logger, ValidationAllowList = allowList };

        Assert.Equal(options, options with { ValidationAllowList = allowList });
        Assert.NotEqual(options, options with { ValidationAllowList = (ValidationAllowance[])allowList.Clone() });
        Assert.NotEqual(options, options with { LoaderPath = Path.Join(Path.GetTempPath(), "vulkan-1.dll") });
    }

    [Fact]
    public void ValidationAllowance_Equals_ComparesIdAndReasonOrdinally()
    {
        ValidationAllowance allowance = new(-1, "reason");

        Assert.Equal(new ValidationAllowance(-1, "reason"), allowance);
        Assert.NotEqual(new ValidationAllowance(1, "reason"), allowance);
        Assert.NotEqual(new ValidationAllowance(-1, "Reason"), allowance);
    }
}

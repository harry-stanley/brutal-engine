using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Adapters;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Adapters;

/// <summary>The "allocates nothing" sentences of <see cref="QueueFamilyChoice"/> and <see cref="DriverNames"/>, measured.</summary>
public sealed class AdapterAllocationTests
{
    [Fact]
    public void QueueFamilyChoice_EveryPath_AllocatesNothing()
    {
        // Each return path: a graphics and compute family, the first graphics-only fallback, and no passing family,
        // with and without present.
        AdapterCandidate.QueueFamily[][] layouts =
        [
            [Candidates.Family(3, graphics: true, compute: false, present: true), Candidates.Family(4, graphics: true, compute: true, present: true)],
            [Candidates.Family(5, graphics: true, compute: false, present: false)],
            [Candidates.Family(6, graphics: false, compute: true, present: true)],
            [],
        ];
        int checksum = Choose(layouts);

        long before = GC.GetAllocatedBytesForCurrentThread();
        checksum += Choose(layouts);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(2 * -17, checksum);
    }

    [Fact]
    public void DriverNames_EveryKnownDriver_AllocatesNothing()
    {
        // Every value with a table name (the fallback's "Unknown (n)" is documented as allocating), each read once
        // before measuring.
        List<VkDriverId> known = [];
        foreach (VkDriverId driver in Enum.GetValues<VkDriverId>())
        {
            if (!DriverNames.Get(driver).StartsWith("Unknown", StringComparison.Ordinal))
            {
                known.Add(driver);
            }
        }

        VkDriverId[] drivers = known.ToArray();
        int length = 0;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < drivers.Length; i++)
        {
            length += DriverNames.Get(drivers[i]).Length;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(drivers.Length >= 30 && length > 0, $"{drivers.Length} drivers");
    }

    // Folds every result of both members, needing present and not, into one number: (4 + 2) - 2 - 9 - 12 = -17.
    private static int Choose(AdapterCandidate.QueueFamily[][] layouts)
    {
        int sum = 0;
        for (int i = 0; i < layouts.Length; i++)
        {
            sum += (i + 1) * (QueueFamilyChoice.Choose(layouts[i], needsPresent: true) + (2 * QueueFamilyChoice.ChoosePosition(layouts[i], needsPresent: false)));
        }

        return sum;
    }
}

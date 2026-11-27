using System;
using System.Collections.Generic;
using BrutalEngine.Graphics.Vulkan.Devices;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Devices;

public sealed class DeviceExtensionSetTests
{
    // Names written from the Vulkan registry, not from BRUTAL's constants.
    private const string Swapchain = "VK_KHR_swapchain";
    private const string Portability = "VK_KHR_portability_subset";
    private const string KhrMaintenance1 = "VK_KHR_swapchain_maintenance1";
    private const string ExtMaintenance1 = "VK_EXT_swapchain_maintenance1";
    private const string Budget = "VK_EXT_memory_budget";
    private const string Other = "VK_KHR_maintenance5";
    private const string Surface = "VK_KHR_surface";
    private const string KhrSurface1 = "VK_KHR_surface_maintenance1";
    private const string ExtSurface1 = "VK_EXT_surface_maintenance1";

    // Offered by the adapter, enabled on the instance; then the expected names in the table's order, the portability
    // flag, the swapchain maintenance1 extension and the budget flag.
    public static TheoryData<string[], string[], string[], bool, string?, bool> Cases => new()
    {
        // Offscreen-only instance: no swapchain, so no swapchain maintenance1 either.
        { [Swapchain], [], [], false, null, false },
        { [Swapchain, KhrMaintenance1, ExtMaintenance1], [KhrSurface1, ExtSurface1], [], false, null, false },
        { [Other, Swapchain, Portability, Budget], [], [Portability, Budget], true, null, true },

        // With a surface.
        { [Swapchain], [Surface], [Swapchain], false, null, false },
        { [], [Surface], [Swapchain], false, null, false },
        { [Other, Swapchain, Portability], [Surface], [Swapchain, Portability], true, null, false },
        { [Swapchain, Budget], [Surface], [Swapchain, Budget], false, null, true },
        { [Swapchain, KhrMaintenance1], [Surface, KhrSurface1], [Swapchain, KhrMaintenance1], false, KhrMaintenance1, false },
        { [Swapchain, KhrMaintenance1], [Surface], [Swapchain], false, null, false },
        { [Swapchain, ExtMaintenance1], [ExtSurface1, Surface], [Swapchain, ExtMaintenance1], false, ExtMaintenance1, false },
        { [Swapchain, ExtMaintenance1], [Surface], [Swapchain], false, null, false },
        { [ExtMaintenance1, KhrMaintenance1, Swapchain], [Surface, KhrSurface1], [Swapchain, KhrMaintenance1], false, KhrMaintenance1, false },
        { [KhrMaintenance1, ExtMaintenance1, Swapchain], [Surface, ExtSurface1, KhrSurface1], [Swapchain, KhrMaintenance1], false, KhrMaintenance1, false },

        // Each swapchain extension needs its own surface extension: the mixed pairs enable nothing.
        { [Swapchain, KhrMaintenance1], [Surface, ExtSurface1], [Swapchain], false, null, false },
        { [Swapchain, ExtMaintenance1], [Surface, KhrSurface1], [Swapchain], false, null, false },
        { [KhrMaintenance1, ExtMaintenance1, Swapchain], [Surface, ExtSurface1], [Swapchain, ExtMaintenance1], false, ExtMaintenance1, false },

        { [Budget, ExtMaintenance1, Portability, Other, KhrMaintenance1, Swapchain], [Surface, KhrSurface1], [Swapchain, Portability, KhrMaintenance1, Budget], true, KhrMaintenance1, true },
        { [Budget, Portability, ExtMaintenance1, Swapchain], [ExtSurface1, Surface], [Swapchain, Portability, ExtMaintenance1, Budget], true, ExtMaintenance1, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Choose_Cases(string[] offered, string[] instance, string[] extensions, bool portability, string? maintenance1, bool budget)
    {
        DeviceExtensionSet.Selection selection = DeviceExtensionSet.Choose(new HashSet<string>(offered, StringComparer.Ordinal), instance);

        Assert.Equal(extensions, selection.Extensions);
        Assert.Equal((portability, maintenance1, budget), (selection.PortabilitySubset, selection.SwapchainMaintenance1, selection.MemoryBudget));
    }

    [Fact]
    public void Choose_NullOffered_Throws() =>
        Assert.Throws<ArgumentNullException>("offered", () => DeviceExtensionSet.Choose(null!, [Surface]));
}

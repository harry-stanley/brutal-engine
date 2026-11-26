using System;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Instances;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Instances;

public sealed class InstanceExtensionsTests
{
    private const string Validation = "VK_LAYER_KHRONOS_validation";

    // The extension table of docs/design/graphics-vulkan.md section 1, with names written from the Vulkan registry.
    // Columns: offered by the loader, offered by the validation layer, the surface's required extensions, validation,
    // then the expected extensions in order and the portability flag, or the expected "<kind>: <message>".
    [Theory]
    [InlineData("nothing offered", "", "", "", false, "", false, null)]
    [InlineData("surface extensions", "VK_KHR_surface,VK_KHR_win32_surface", "", "VK_KHR_surface,VK_KHR_win32_surface", false, "VK_KHR_surface,VK_KHR_win32_surface", false, null)]
    [InlineData("surface extension listed twice", "VK_KHR_surface,VK_KHR_xcb_surface", "", "VK_KHR_surface,VK_KHR_xcb_surface,VK_KHR_surface", false, "VK_KHR_surface,VK_KHR_xcb_surface", false, null)]
    [InlineData("surface extension not offered", "VK_KHR_surface", "", "VK_KHR_surface,VK_KHR_wayland_surface", false, null, false, "Unsupported: instance extension not offered: VK_KHR_wayland_surface")]
    [InlineData("portability enumeration offered", "VK_KHR_portability_enumeration", "", "", false, "VK_KHR_portability_enumeration", true, null)]
    [InlineData("surface maintenance, KHR preferred", "VK_KHR_surface,VK_KHR_get_surface_capabilities2,VK_EXT_surface_maintenance1,VK_KHR_surface_maintenance1", "", "VK_KHR_surface", false, "VK_KHR_surface,VK_KHR_get_surface_capabilities2,VK_KHR_surface_maintenance1", false, null)]
    [InlineData("surface maintenance, EXT only", "VK_KHR_surface,VK_KHR_get_surface_capabilities2,VK_EXT_surface_maintenance1", "", "VK_KHR_surface", false, "VK_KHR_surface,VK_KHR_get_surface_capabilities2,VK_EXT_surface_maintenance1", false, null)]
    [InlineData("surface maintenance without capabilities2", "VK_KHR_surface,VK_KHR_surface_maintenance1", "", "VK_KHR_surface", false, "VK_KHR_surface", false, null)]
    [InlineData("surface maintenance without a surface", "VK_KHR_surface,VK_KHR_get_surface_capabilities2,VK_KHR_surface_maintenance1", "", "", false, "", false, null)]
    [InlineData("debug utils offered, no validation", "VK_EXT_debug_utils", "", "", false, "VK_EXT_debug_utils", false, null)]
    [InlineData("no validation, validation features offered", "VK_EXT_validation_features", "VK_EXT_debug_utils,VK_EXT_validation_features", "", false, "", false, null)]
    [InlineData("validation", "VK_EXT_debug_utils", "VK_EXT_debug_utils,VK_EXT_validation_features", "", true, "VK_EXT_debug_utils,VK_EXT_validation_features", false, null)]
    [InlineData("validation, debug utils from the layer only", "", "VK_EXT_debug_utils,VK_EXT_validation_features", "", true, "VK_EXT_debug_utils,VK_EXT_validation_features", false, null)]
    [InlineData("validation, debug utils not offered", "", "VK_EXT_validation_features", "", true, null, false, "Unavailable: VK_EXT_debug_utils not offered: validation needs it for the messenger")]
    [InlineData("validation, features only from the loader", "VK_EXT_debug_utils,VK_EXT_validation_features", "VK_EXT_debug_utils", "", true, null, false, "Unavailable: validation layer not installed: VK_LAYER_KHRONOS_validation doesn't offer VK_EXT_validation_features")]
    [InlineData("everything, with validation", "VK_EXT_debug_utils,VK_KHR_get_surface_capabilities2,VK_KHR_portability_enumeration,VK_KHR_surface,VK_KHR_surface_maintenance1,VK_KHR_win32_surface", "VK_EXT_debug_utils,VK_EXT_validation_features", "VK_KHR_surface,VK_KHR_win32_surface", true, "VK_KHR_surface,VK_KHR_win32_surface,VK_KHR_portability_enumeration,VK_KHR_get_surface_capabilities2,VK_KHR_surface_maintenance1,VK_EXT_debug_utils,VK_EXT_validation_features", true, null)]
    public void Plan_Table(string row, string available, string layer, string surface, bool validation, string? extensions, bool portability, string? error)
    {
        InstanceFactory.Options options = new() { Validation = validation, SurfaceExtensions = Split(surface) };

        Result<InstanceExtensions.Selection> result = InstanceExtensions.Plan(Split(available), Split(layer), options);

        if (error is not null)
        {
            Assert.True(result.IsFailure, row);
            Assert.Equal(error, $"{result.Error.Kind}: {result.Error.Message}");
            return;
        }

        Assert.True(result.IsSuccess, $"{row}: {result}");
        InstanceExtensions.Selection selection = result.Value;
        Assert.Equal(Split(extensions!), selection.Extensions);
        Assert.Equal(validation ? [Validation] : [], selection.Layers);
        Assert.Equal(validation, selection.Validation);
        Assert.Equal(portability ? VkInstanceCreateFlags.EnumeratePortabilityBitKHR : VkInstanceCreateFlags.None, selection.Flags);
    }

    [Fact]
    public void Plan_NullOptions_Throws() =>
        Assert.Throws<ArgumentNullException>("options", () => InstanceExtensions.Plan([], [], null!));

    private static string[] Split(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries);
}

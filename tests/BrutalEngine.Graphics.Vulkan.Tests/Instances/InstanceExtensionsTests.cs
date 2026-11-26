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

    [Fact]
    public void MissingLayer_IsUnavailable()
    {
        FakeEnumeration loader = new();
        loader.Extensions[string.Empty].Add("VK_EXT_debug_utils");
        loader.Layers.Add("VK_LAYER_LUNARG_api_dump");

        Result<InstanceExtensions.Selection> result = InstanceExtensions.Select(loader, new InstanceFactory.Options { Validation = true }, new VulkanCallGate());

        Assert.Equal(new EngineError(ErrorKind.Unavailable, "validation layer not installed: VK_LAYER_KHRONOS_validation is missing"), result.Error);

        // The layer's own list is never asked for: the loader would answer VK_ERROR_LAYER_NOT_PRESENT.
        Assert.Equal(["extensions() count", "extensions() fill", "layers count", "layers fill"], loader.Calls);
    }

    [Fact]
    public void Select_Validation_PlansFromTheLayersOwnList()
    {
        FakeEnumeration loader = new();
        loader.Extensions[string.Empty].Add("VK_EXT_debug_utils");
        loader.Layers.AddRange(["VK_LAYER_LUNARG_api_dump", Validation]);
        loader.Extensions[Validation] = ["VK_EXT_debug_utils", "VK_EXT_validation_features"];

        Result<InstanceExtensions.Selection> result = InstanceExtensions.Select(loader, new InstanceFactory.Options { Validation = true }, new VulkanCallGate());

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(["VK_EXT_debug_utils", "VK_EXT_validation_features"], result.Value.Extensions);
        Assert.Equal(["extensions() count", "extensions() fill", "layers count", "layers fill", $"extensions({Validation}) count", $"extensions({Validation}) fill"], loader.Calls);
    }

    [Fact]
    public void Select_NoValidation_ListsNoLayers()
    {
        FakeEnumeration loader = new();

        Result<InstanceExtensions.Selection> result = InstanceExtensions.Select(loader, new InstanceFactory.Options(), new VulkanCallGate());

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(["extensions() count"], loader.Calls);
    }

    [Fact]
    public void EnumerateExtensions_ListGrowsBetweenCalls_RetriesAndReturnsAll()
    {
        FakeEnumeration loader = new() { GrowthsLeft = 1 };
        loader.Extensions[string.Empty].AddRange(["VK_KHR_surface", "VK_EXT_debug_utils"]);

        Result<string[]> result = InstanceExtensions.EnumerateExtensions(loader, null, new VulkanCallGate());

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(["VK_KHR_surface", "VK_EXT_debug_utils", "VK_FAKE_grown_2"], result.Value);
        Assert.Equal(["extensions() count", "extensions() fill", "extensions() count", "extensions() fill"], loader.Calls);
    }

    [Fact]
    public void EnumerateLayers_ListGrowsBetweenCalls_RetriesAndReturnsAll()
    {
        FakeEnumeration loader = new() { GrowthsLeft = 1 };
        loader.Layers.Add(Validation);

        Result<string[]> result = InstanceExtensions.EnumerateLayers(loader, new VulkanCallGate());

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal([Validation, "VK_FAKE_grown_1"], result.Value);
        Assert.Equal(["layers count", "layers fill", "layers count", "layers fill"], loader.Calls);
    }

    [Fact]
    public void EnumerateExtensions_ListKeepsGrowing_FailsWithIncomplete()
    {
        FakeEnumeration loader = new() { GrowthsLeft = int.MaxValue };
        loader.Extensions[string.Empty].Add("VK_KHR_surface");

        Result<string[]> result = InstanceExtensions.EnumerateExtensions(loader, null, new VulkanCallGate());

        Assert.Equal(new EngineError(ErrorKind.Native, "vkEnumerateInstanceExtensionProperties failed", (int)VkResult.Incomplete), result.Error);
        Assert.Equal(2 * InstanceExtensions.MaxEnumerationAttempts, loader.Calls.Count);
    }

    [Fact]
    public void EnumerateLayers_ListKeepsGrowing_FailsWithIncomplete()
    {
        FakeEnumeration loader = new() { GrowthsLeft = int.MaxValue };
        loader.Layers.Add(Validation);

        Result<string[]> result = InstanceExtensions.EnumerateLayers(loader, new VulkanCallGate());

        Assert.Equal(new EngineError(ErrorKind.Native, "vkEnumerateInstanceLayerProperties failed", (int)VkResult.Incomplete), result.Error);
        Assert.Equal(2 * InstanceExtensions.MaxEnumerationAttempts, loader.Calls.Count);
    }

    [Fact]
    public void EnumerateExtensions_EmptyList_SkipsTheFill()
    {
        FakeEnumeration loader = new();

        Result<string[]> result = InstanceExtensions.EnumerateExtensions(loader, null, new VulkanCallGate());

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Empty(result.Value);
        Assert.Equal(["extensions() count"], loader.Calls);
    }

    [Fact]
    public void EnumerateExtensions_UnknownLayer_IsTheGatesError()
    {
        FakeEnumeration loader = new();

        Result<string[]> result = InstanceExtensions.EnumerateExtensions(loader, "VK_LAYER_missing", new VulkanCallGate());

        Assert.Equal(new EngineError(ErrorKind.Unsupported, "vkEnumerateInstanceExtensionProperties failed", (int)VkResult.ErrorLayerNotPresent), result.Error);
    }

    private static string[] Split(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries);
}

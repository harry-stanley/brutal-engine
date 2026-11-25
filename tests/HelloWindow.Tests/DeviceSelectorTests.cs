using System;
using Brutal.VulkanApi;
using Xunit;

namespace HelloWindow.Tests;

public sealed class DeviceSelectorTests
{
    private static readonly int _vulkan13 = VK.API_VERSION_1_3;
    private static readonly int _vulkan12 = VK.API_VERSION_1_2;

    // VkDriverId has aliases with equal values (NvidiaProprietary and NvidiaProprietaryKHR), and Enum.ToString may
    // print either, so expected messages format the IDs the same way the code under test does.
    private static readonly string _nvidia = VkDriverId.NvidiaProprietary.ToString();
    private static readonly string _llvmpipe = VkDriverId.MESALlvmpipe.ToString();
    private static readonly string _moltenVk = VkDriverId.Moltenvk.ToString();

    private static DeviceCandidate Suitable(string name, VkPhysicalDeviceType type, VkDriverId driver = VkDriverId.NvidiaProprietary) =>
        new(name, type, _vulkan13, driver, SupportsSwapchain: true, SupportsDynamicRendering: true, SupportsSynchronization2: true, GraphicsPresentQueueFamily: 0);

    [Fact]
    public void Select_PrefersDiscreteSuitableDevice()
    {
        DeviceCandidate[] candidates =
        [
            Suitable("Integrated", VkPhysicalDeviceType.IntegratedGpu),
            Suitable("Discrete without sync2", VkPhysicalDeviceType.DiscreteGpu) with { SupportsSynchronization2 = false },
            Suitable("Software", VkPhysicalDeviceType.Cpu),
            Suitable("Discrete", VkPhysicalDeviceType.DiscreteGpu),
            Suitable("Second discrete", VkPhysicalDeviceType.DiscreteGpu),
        ];

        DeviceSelection selection = DeviceSelector.Select(candidates, requireCpu: false, requireDriver: null);

        Assert.True(selection.Found);
        Assert.Equal(3, selection.ChosenIndex);
        Assert.Equal(["Discrete without sync2: no synchronization2"], selection.Rejections);
    }

    [Fact]
    public void Select_OnlyLowerPreferenceDevicesSuitable_ChoosesBestOfThem()
    {
        DeviceCandidate[] candidates =
        [
            Suitable("Software", VkPhysicalDeviceType.Cpu),
            Suitable("Virtual", VkPhysicalDeviceType.VirtualGpu),
            Suitable("Integrated", VkPhysicalDeviceType.IntegratedGpu),
        ];

        Assert.Equal(2, DeviceSelector.Select(candidates, requireCpu: false, requireDriver: null).ChosenIndex);
    }

    [Fact]
    public void Select_NoSuitableDevice_ReportsEveryRejectionReason()
    {
        DeviceCandidate[] candidates =
        [
            Suitable("Old", VkPhysicalDeviceType.DiscreteGpu) with { ApiVersion = _vulkan12 },
            Suitable("Headless only", VkPhysicalDeviceType.DiscreteGpu) with { SupportsSwapchain = false, GraphicsPresentQueueFamily = -1 },
            Suitable("No features", VkPhysicalDeviceType.IntegratedGpu) with { SupportsDynamicRendering = false, SupportsSynchronization2 = false },
            Suitable("Software", VkPhysicalDeviceType.Cpu, VkDriverId.MESALlvmpipe),
        ];

        DeviceSelection selection = DeviceSelector.Select(candidates, requireCpu: false, requireDriver: VkDriverId.Moltenvk);

        Assert.False(selection.Found);
        Assert.Equal(-1, selection.ChosenIndex);
        Assert.Equal(
            [
                $"Old: Vulkan 1.2.0 (needs 1.3), driver {_nvidia} (needs {_moltenVk})",
                $"Headless only: no VK_KHR_swapchain, no queue family with graphics and present support, driver {_nvidia} (needs {_moltenVk})",
                $"No features: no dynamicRendering, no synchronization2, driver {_nvidia} (needs {_moltenVk})",
                $"Software: driver {_llvmpipe} (needs {_moltenVk})",
            ],
            selection.Rejections);
        Assert.StartsWith("No Vulkan 1.3 GPU with graphics+present found: Old: Vulkan 1.2.0", selection.FailureMessage, StringComparison.Ordinal);
        Assert.Contains($"; Software: driver {_llvmpipe} (needs {_moltenVk})", selection.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_RequireCpu_RejectsGpus()
    {
        DeviceCandidate[] candidates =
        [
            Suitable("Discrete", VkPhysicalDeviceType.DiscreteGpu),
            Suitable("Software", VkPhysicalDeviceType.Cpu, VkDriverId.MESALlvmpipe),
        ];

        DeviceSelection selection = DeviceSelector.Select(candidates, requireCpu: true, requireDriver: VkDriverId.MESALlvmpipe);

        Assert.Equal(1, selection.ChosenIndex);
        Assert.Equal([$"Discrete: type DiscreteGpu (needs Cpu), driver {_nvidia} (needs {_llvmpipe})"], selection.Rejections);
    }

    [Fact]
    public void Select_NoDevices_ReportsThatTheLoaderFoundNone()
    {
        DeviceSelection selection = DeviceSelector.Select([], requireCpu: false, requireDriver: null);

        Assert.False(selection.Found);
        Assert.Equal("No Vulkan 1.3 GPU with graphics+present found: the Vulkan loader reported no devices", selection.FailureMessage);
    }
}

using System.Globalization;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The adapter description record (docs/design/graphics.md section 2).</summary>
public sealed class GraphicsAdapterInfoTests
{
    [Fact]
    public void ToString_UnderAnotherCulture_ListsEveryPropertyInvariantly()
    {
        var info = new GraphicsAdapterInfo
        {
            Index = -1,
            Name = "llvmpipe (LLVM 19.1.7, 256 bits)",
            Type = AdapterType.Cpu,
            Driver = "MesaLlvmpipe",
            ApiVersion = "Vulkan 1.3.289",
            RejectionReasons = ["no graphics queue", "VK_KHR_swapchain missing"],
        };

        CultureInfo original = CultureInfo.CurrentCulture;
        // Swedish formats negative numbers with U+2212 MINUS SIGN, not the ASCII hyphen.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            Assert.Equal(
                "GraphicsAdapterInfo { Index = -1, Name = llvmpipe (LLVM 19.1.7, 256 bits), Type = Cpu, Driver = MesaLlvmpipe, "
                + "ApiVersion = Vulkan 1.3.289, RejectionReasons = [no graphics queue, VK_KHR_swapchain missing] }",
                info.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ToString_NoRejectionReasons_PrintsAnEmptyList()
    {
        var info = new GraphicsAdapterInfo
        {
            Index = 0,
            Name = "NVIDIA GeForce RTX 2070 SUPER",
            Type = AdapterType.Discrete,
            Driver = "NvidiaProprietary",
            ApiVersion = "Vulkan 1.4.312",
            RejectionReasons = [],
        };

        Assert.EndsWith(", RejectionReasons = [] }", info.ToString(), System.StringComparison.Ordinal);
    }
}

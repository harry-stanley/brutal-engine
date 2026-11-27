using System;
using System.Globalization;
using System.Linq;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Adapters;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Adapters;

public sealed class DriverNamesTests
{
    /// <summary>
    /// The table, written by hand from the Vulkan specification's <c>VkDriverId</c> (the values and the
    /// <c>VK_DRIVER_ID_*</c> names as BRUTAL's generated enum lists them in its doc comment), each suffix in Pascal case.
    /// </summary>
    public static TheoryData<int, string> Table => new()
    {
        { 1, "AmdProprietary" },
        { 2, "AmdOpenSource" },
        { 3, "MesaRadv" },
        { 4, "NvidiaProprietary" },
        { 5, "IntelProprietaryWindows" },
        { 6, "IntelOpenSourceMesa" },
        { 7, "ImaginationProprietary" },
        { 8, "QualcommProprietary" },
        { 9, "ArmProprietary" },
        { 10, "GoogleSwiftshader" },
        { 11, "GgpProprietary" },
        { 12, "BroadcomProprietary" },
        { 13, "MesaLlvmpipe" },
        { 14, "Moltenvk" },
        { 15, "CoreaviProprietary" },
        { 16, "JuiceProprietary" },
        { 17, "VerisiliconProprietary" },
        { 18, "MesaTurnip" },
        { 19, "MesaV3dv" },
        { 20, "MesaPanvk" },
        { 21, "SamsungProprietary" },
        { 22, "MesaVenus" },
        { 23, "MesaDozen" },
        { 24, "MesaNvk" },
        { 25, "ImaginationOpenSourceMesa" },
        { 26, "MesaHoneykrisp" },
        { 27, "VulkanScEmulationOnVulkan" },
        { 28, "MesaKosmickrisp" },
        { 29, "MesaGfxstream" },
        { 30, "ApeSoft" },
    };

    /// <summary>BRUTAL's <c>KHR</c> alias members and the core member each one equals, by name.</summary>
    public static TheoryData<string, string> Aliases => new()
    {
        { "AMDProprietaryKHR", "AMDProprietary" },
        { "AMDOpenSourceKHR", "AMDOpenSource" },
        { "MESARadvKHR", "MESARadv" },
        { "NvidiaProprietaryKHR", "NvidiaProprietary" },
        { "INTELProprietaryWindowsKHR", "INTELProprietaryWindows" },
        { "INTELOpenSourceMESAKHR", "INTELOpenSourceMESA" },
        { "ImaginationProprietaryKHR", "ImaginationProprietary" },
        { "QualcommProprietaryKHR", "QualcommProprietary" },
        { "ARMProprietaryKHR", "ARMProprietary" },
        { "GOOGLESwiftshaderKHR", "GOOGLESwiftshader" },
        { "GGPProprietaryKHR", "GGPProprietary" },
        { "BroadcomProprietaryKHR", "BroadcomProprietary" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Get_EveryMember_HasTableName(int value, string expected)
    {
        Assert.Equal(expected, DriverNames.Get((VkDriverId)value));
    }

    [Fact]
    public void Get_EveryMember_TableCoversThePinnedEnum()
    {
        // A member a newer BRUTAL adds fails here until the table names it. MaxEnum is a sentinel, not a driver.
        int[] members = [.. Enum.GetValues<VkDriverId>().Where(d => d != VkDriverId.MaxEnum).Select(d => (int)d).Distinct().Order()];

        Assert.Equal(members, Table.Select(row => row.Data.Item1).Order());
    }

    [Theory]
    [MemberData(nameof(Aliases))]
    public void Get_Alias_SameName(string alias, string member)
    {
        VkDriverId aliasValue = Enum.Parse<VkDriverId>(alias);
        VkDriverId memberValue = Enum.Parse<VkDriverId>(member);
        string expected = Table.Single(row => row.Data.Item1 == (int)memberValue).Data.Item2;

        Assert.Equal(expected, DriverNames.Get(aliasValue));
        Assert.Equal(DriverNames.Get(memberValue), DriverNames.Get(aliasValue));
    }

    [Fact]
    public void Get_Alias_CoversEveryKhrMember()
    {
        string[] khr = [.. Enum.GetNames<VkDriverId>().Where(n => n.EndsWith("KHR", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

        Assert.Equal(khr, Aliases.Select(row => row.Data.Item1).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(0, "Unknown (0)")]
    [InlineData(31, "Unknown (31)")]
    [InlineData(1000, "Unknown (1000)")]
    [InlineData(-1, "Unknown (-1)")]
    [InlineData(int.MinValue, "Unknown (-2147483648)")]
    [InlineData(0x7FFFFFFF, "Unknown (2147483647)")]
    public void Get_Unknown_Fallback(int value, string expected)
    {
        Assert.Equal(expected, DriverNames.Get((VkDriverId)value));
    }

    [Fact]
    public void Get_Unknown_FormatsWithTheInvariantCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "~";
        CultureInfo.CurrentCulture = culture;
        try
        {
            Assert.Equal("Unknown (-5)", DriverNames.Get((VkDriverId)(-5)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

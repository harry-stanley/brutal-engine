using System.Globalization;
using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Adapters;

/// <summary>
/// The fixed names <c>GraphicsAdapterInfo.Driver</c> reports for each <see cref="VkDriverId"/>
/// (docs/design/graphics-vulkan.md section 3, "Selection").
/// </summary>
/// <remarks>
/// <para>
/// Each name is the Vulkan specification's <c>VK_DRIVER_ID_*</c> suffix in Pascal case, word by word
/// (<c>VK_DRIVER_ID_MESA_LLVMPIPE</c> is <c>MesaLlvmpipe</c>), written from the members of BRUTAL's generated enum
/// (<c>Brutal.Vulkan/Generated/Enums/VkDriverId.gen.cs</c>). The enum is never formatted with <c>ToString</c>: its
/// <c>KHR</c> aliases share values with the core members, so it could print either name. A value the table doesn't
/// know, such as a driver newer than the binding or the <c>MaxEnum</c> sentinel, is named <c>Unknown (&lt;number&gt;)</c>.
/// </para>
/// <para>Stateless and thread-safe. Known values allocate nothing; the fallback allocates its string.</para>
/// </remarks>
internal static class DriverNames
{
    /// <summary>Gets the name of a driver.</summary>
    /// <param name="driver">The driver ID the device reports.</param>
    /// <returns>The table's name, or <c>Unknown (&lt;number&gt;)</c> with the value in the invariant culture.</returns>
    public static string Get(VkDriverId driver) => driver switch
    {
        VkDriverId.AMDProprietary => "AmdProprietary",
        VkDriverId.AMDOpenSource => "AmdOpenSource",
        VkDriverId.MESARadv => "MesaRadv",
        VkDriverId.NvidiaProprietary => "NvidiaProprietary",
        VkDriverId.INTELProprietaryWindows => "IntelProprietaryWindows",
        VkDriverId.INTELOpenSourceMESA => "IntelOpenSourceMesa",
        VkDriverId.ImaginationProprietary => "ImaginationProprietary",
        VkDriverId.QualcommProprietary => "QualcommProprietary",
        VkDriverId.ARMProprietary => "ArmProprietary",
        VkDriverId.GOOGLESwiftshader => "GoogleSwiftshader",
        VkDriverId.GGPProprietary => "GgpProprietary",
        VkDriverId.BroadcomProprietary => "BroadcomProprietary",
        VkDriverId.MESALlvmpipe => "MesaLlvmpipe",
        VkDriverId.Moltenvk => "Moltenvk",
        VkDriverId.CoreaviProprietary => "CoreaviProprietary",
        VkDriverId.JUICEProprietary => "JuiceProprietary",
        VkDriverId.VerisiliconProprietary => "VerisiliconProprietary",
        VkDriverId.MESATurnip => "MesaTurnip",
        VkDriverId.MESAV3Dv => "MesaV3dv",
        VkDriverId.MESAPanvk => "MesaPanvk",
        VkDriverId.SAMSUNGProprietary => "SamsungProprietary",
        VkDriverId.MESAVenus => "MesaVenus",
        VkDriverId.MESADozen => "MesaDozen",
        VkDriverId.MESANvk => "MesaNvk",
        VkDriverId.ImaginationOpenSourceMESA => "ImaginationOpenSourceMesa",
        VkDriverId.MESAHoneykrisp => "MesaHoneykrisp",
        VkDriverId.VulkanScEmulationOnVulkan => "VulkanScEmulationOnVulkan",
        VkDriverId.MESAKosmickrisp => "MesaKosmickrisp",
        VkDriverId.MESAGfxstream => "MesaGfxstream",
        VkDriverId.ApeSoft => "ApeSoft",
        _ => string.Create(CultureInfo.InvariantCulture, $"Unknown ({(int)driver})"),
    };
}

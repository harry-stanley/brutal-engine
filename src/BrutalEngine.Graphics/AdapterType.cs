namespace BrutalEngine.Graphics;

/// <summary>The kind of hardware (or software) behind an adapter.</summary>
/// <remarks>
/// Without an override, suitable adapters are preferred in the order <see cref="Discrete"/>, <see cref="Integrated"/>,
/// <see cref="Virtual"/>, <see cref="Cpu"/>, then <see cref="Other"/>, ties going to the first enumerated
/// (docs/design/graphics.md section 2). For Vulkan each value corresponds to one <c>VkPhysicalDeviceType</c>, but
/// the numeric values differ (Vulkan's integrated GPU is 1 and discrete GPU 2), so backends map them by name, never
/// by casting.
/// </remarks>
public enum AdapterType
{
    /// <summary>A type the backend doesn't classify. The default value.</summary>
    Other = 0,

    /// <summary>A separate GPU with its own memory.</summary>
    Discrete = 1,

    /// <summary>A GPU built into or tightly coupled with the CPU, sharing its memory.</summary>
    Integrated = 2,

    /// <summary>A GPU exposed by a virtual machine or another virtualisation layer.</summary>
    Virtual = 3,

    /// <summary>A software renderer running on the CPU, such as Mesa's llvmpipe.</summary>
    Cpu = 4,
}

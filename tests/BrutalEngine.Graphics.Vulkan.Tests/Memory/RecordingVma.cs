using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Graphics.Vulkan.Memory;

namespace BrutalEngine.Graphics.Vulkan.Tests.Memory;

/// <summary>
/// Observes the factory's <c>vmaCreateAllocator</c> call: counts it and copies the description while its pointers are
/// valid, then either forwards it to VMA or returns a scripted result with a made-up handle.
/// </summary>
internal sealed unsafe class RecordingVma : VmaAllocatorFactory.INative
{
    /// <summary>The handle a scripted success writes.</summary>
    public const nint ScriptedHandle = 0x5150;

    /// <summary>Gets the scripted result; ignored when <see cref="Forward"/> is set.</summary>
    public VkResult Result { get; init; } = VkResult.Success;

    /// <summary>Gets whether calls reach the real <c>vmaCreateAllocator</c>.</summary>
    public bool Forward { get; init; }

    public int Calls { get; private set; }

    /// <summary>Gets the last description, as passed; its pointers are stale after the call.</summary>
    public VmaAllocatorCreateInfo Info { get; private set; }

    /// <summary>Gets a copy of the last description's functions, or <see langword="null"/> if it had none.</summary>
    public VmaVulkanFunctions? Functions { get; private set; }

    public VkResult CreateAllocator(VmaAllocatorCreateInfo* info, VmaAllocatorHandle* allocator)
    {
        Calls++;
        Info = *info;
        Functions = info->PVulkanFunctions.IsNull() ? null : *info->PVulkanFunctions.Pointer;
        if (Forward)
        {
            return Vma.PInvoke.vmaCreateAllocator(info, allocator);
        }

        if (Result == VkResult.Success)
        {
            *allocator = new VmaAllocatorHandle(ScriptedHandle);
        }

        return Result;
    }
}

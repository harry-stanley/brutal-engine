using System;
using Brutal.VulkanApi;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Vulkan;

/// <summary>
/// The pure half of the backend's status checking (docs/design/graphics-vulkan.md section 13): maps a
/// <see cref="VkResult"/> to an <see cref="ErrorKind"/> and builds the <see cref="EngineError"/> for a failed call.
/// </summary>
/// <remarks>
/// Stateless and thread-safe. <see cref="KindOf"/> and <see cref="Error"/> allocate nothing: every message is a
/// constant string per <see cref="VulkanCall"/>, and the code goes into <see cref="EngineError.NativeCode"/>.
/// The per-device call gate is its only caller on the frame path; swapchain calls get their own mapping (section 4)
/// in a later issue.
/// </remarks>
internal static class VkCheck
{
    /// <summary>Maps a Vulkan or VMA result code to the engine's error kind.</summary>
    /// <param name="result">Any value, named in BRUTAL's enum or not.</param>
    /// <returns>
    /// <see cref="ErrorKind.OutOfMemory"/> for out of host, device or pool memory (pool memory reaches the gate only
    /// once its retry failed); <see cref="ErrorKind.Unavailable"/> for <c>DEVICE_LOST</c>;
    /// <see cref="ErrorKind.Timeout"/> for <c>TIMEOUT</c>; <see cref="ErrorKind.Unsupported"/> for
    /// <c>FORMAT_NOT_SUPPORTED</c>, <c>FEATURE_NOT_PRESENT</c>, <c>EXTENSION_NOT_PRESENT</c>, <c>LAYER_NOT_PRESENT</c>
    /// and <c>INCOMPATIBLE_DRIVER</c>; <see cref="ErrorKind.InvalidData"/> for <c>INVALID_SHADER_NV</c>; and
    /// <see cref="ErrorKind.Native"/> for every other value, including success codes other than <c>SUCCESS</c>
    /// (<c>NOT_READY</c>, <c>INCOMPLETE</c>, <c>SUBOPTIMAL_KHR</c>, ...) and values BRUTAL's enum doesn't name. A
    /// caller that expects one of those success codes tests for it before checking.
    /// </returns>
    public static ErrorKind KindOf(VkResult result) => result switch
    {
        VkResult.ErrorOutOfHostMemory or VkResult.ErrorOutOfDeviceMemory or VkResult.ErrorOutOfPoolMemory => ErrorKind.OutOfMemory,
        VkResult.ErrorDeviceLost => ErrorKind.Unavailable,
        VkResult.Timeout => ErrorKind.Timeout,
        VkResult.ErrorFormatNotSupported or VkResult.ErrorFeatureNotPresent or VkResult.ErrorExtensionNotPresent
            or VkResult.ErrorLayerNotPresent or VkResult.ErrorIncompatibleDriver => ErrorKind.Unsupported,
        VkResult.ErrorInvalidShaderNV => ErrorKind.InvalidData,
        _ => ErrorKind.Native,
    };

    /// <summary>Builds the error for a call that returned <paramref name="result"/>.</summary>
    /// <param name="result">The call's result; anything but <c>SUCCESS</c>.</param>
    /// <param name="call">The call that returned it.</param>
    /// <returns>
    /// An error with <see cref="KindOf"/>'s kind, the call's constant message and the code in
    /// <see cref="EngineError.NativeCode"/>. For <see cref="VulkanCall.CreateShaderModule"/>,
    /// <see cref="VulkanCall.CreateGraphicsPipelines"/> and <see cref="VulkanCall.CreateComputePipelines"/>, a
    /// <see cref="ErrorKind.Native"/> kind becomes <see cref="ErrorKind.InvalidData"/>: the driver rejected the code
    /// or the state (section 9). Allocates nothing.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="result"/> is <c>SUCCESS</c>, or <paramref name="call"/> isn't a <see cref="VulkanCall"/> member.
    /// </exception>
    public static EngineError Error(VkResult result, VulkanCall call)
    {
        if (result == VkResult.Success)
        {
            ThrowSuccessIsNotAnError(result);
        }

        string message = MessageOf(call);
        ErrorKind kind = KindOf(result);
        if (kind == ErrorKind.Native && call is VulkanCall.CreateShaderModule or VulkanCall.CreateGraphicsPipelines or VulkanCall.CreateComputePipelines)
        {
            kind = ErrorKind.InvalidData;
        }

        return new EngineError(kind, message, (int)result);
    }

    /// <summary>
    /// Converts the exception a BRUTAL wrapper threw into an error. Only for the three wrapper call sites rule 2 allows
    /// (<c>Vulkan.CreateInstance</c>, <c>PhysicalDevice.CreateDevice</c>, <c>AllocateCommandBuffers</c>), never on
    /// the frame path.
    /// </summary>
    /// <param name="error">The exception the wrapper threw; its <c>Result</c> becomes the code.</param>
    /// <param name="call">The wrapper's name, for the message (e.g. <c>"vkCreateInstance"</c>).</param>
    /// <returns>An error with <see cref="KindOf"/>'s kind, a message naming the call and BRUTAL's message, and the code.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> or <paramref name="call"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="call"/> is empty.</exception>
    /// <remarks>Allocates the message string: creation-time only.</remarks>
    public static EngineError FromException(VkResultError error, string call)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrEmpty(call);
        return new EngineError(KindOf(error.Result), call + " failed: " + error.Message, (int)error.Result);
    }

    /// <summary>The constant message for <paramref name="call"/>: string literals, so nothing is built per call.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="call"/> isn't a <see cref="VulkanCall"/> member.</exception>
    private static string MessageOf(VulkanCall call) => call switch
    {
        VulkanCall.EnumerateInstanceExtensionProperties => "vkEnumerateInstanceExtensionProperties failed",
        VulkanCall.EnumerateInstanceLayerProperties => "vkEnumerateInstanceLayerProperties failed",
        VulkanCall.CreateInstance => "vkCreateInstance failed",
        VulkanCall.CreateDebugUtilsMessenger => "vkCreateDebugUtilsMessengerEXT failed",
        VulkanCall.EnumerateDeviceExtensionProperties => "vkEnumerateDeviceExtensionProperties failed",
        VulkanCall.GetImageFormatProperties2 => "vkGetPhysicalDeviceImageFormatProperties2 failed",
        VulkanCall.CreateDevice => "vkCreateDevice failed",
        VulkanCall.GetSurfaceSupport => "vkGetPhysicalDeviceSurfaceSupportKHR failed",
        VulkanCall.GetSurfaceCapabilities => "vkGetPhysicalDeviceSurfaceCapabilitiesKHR failed",
        VulkanCall.GetSurfaceFormats => "vkGetPhysicalDeviceSurfaceFormatsKHR failed",
        VulkanCall.GetSurfacePresentModes => "vkGetPhysicalDeviceSurfacePresentModesKHR failed",
        VulkanCall.CreateSwapchain => "vkCreateSwapchainKHR failed",
        VulkanCall.GetSwapchainImages => "vkGetSwapchainImagesKHR failed",
        VulkanCall.AcquireNextImage => "vkAcquireNextImageKHR failed",
        VulkanCall.QueuePresent => "vkQueuePresentKHR failed",
        VulkanCall.WaitForPresentFence => "vkWaitForFences (present fence) failed",
        VulkanCall.CreateCommandPool => "vkCreateCommandPool failed",
        VulkanCall.AllocateCommandBuffers => "vkAllocateCommandBuffers failed",
        VulkanCall.CreateFence => "vkCreateFence failed",
        VulkanCall.CreateSemaphore => "vkCreateSemaphore failed",
        VulkanCall.WaitForFences => "vkWaitForFences failed",
        VulkanCall.GetFenceStatus => "vkGetFenceStatus failed",
        VulkanCall.ResetFences => "vkResetFences failed",
        VulkanCall.ResetCommandPool => "vkResetCommandPool failed",
        VulkanCall.BeginCommandBuffer => "vkBeginCommandBuffer failed",
        VulkanCall.EndCommandBuffer => "vkEndCommandBuffer failed",
        VulkanCall.QueueSubmit2 => "vkQueueSubmit2 failed",
        VulkanCall.DeviceWaitIdle => "vkDeviceWaitIdle failed",
        VulkanCall.CreateVmaAllocator => "vmaCreateAllocator failed",
        VulkanCall.CreateBuffer => "vmaCreateBuffer failed",
        VulkanCall.CreateImage => "vmaCreateImage failed",
        VulkanCall.CreateImageView => "vkCreateImageView failed",
        VulkanCall.CreateSampler => "vkCreateSampler failed",
        VulkanCall.CreateDescriptorSetLayout => "vkCreateDescriptorSetLayout failed",
        VulkanCall.CreateDescriptorPool => "vkCreateDescriptorPool failed",
        VulkanCall.ResetDescriptorPool => "vkResetDescriptorPool failed",
        VulkanCall.AllocateDescriptorSets => "vkAllocateDescriptorSets failed",
        VulkanCall.FreeDescriptorSets => "vkFreeDescriptorSets failed",
        VulkanCall.CreateShaderModule => "vkCreateShaderModule failed",
        VulkanCall.CreatePipelineLayout => "vkCreatePipelineLayout failed",
        VulkanCall.CreatePipelineCache => "vkCreatePipelineCache failed",
        VulkanCall.CreateGraphicsPipelines => "vkCreateGraphicsPipelines failed",
        VulkanCall.CreateComputePipelines => "vkCreateComputePipelines failed",
        VulkanCall.CreateQueryPool => "vkCreateQueryPool failed",
        VulkanCall.GetQueryPoolResults => "vkGetQueryPoolResults failed",
        VulkanCall.SetDebugUtilsObjectName => "vkSetDebugUtilsObjectNameEXT failed",
        _ => ThrowUnknownCall(call),
    };

    private static string ThrowUnknownCall(VulkanCall call) =>
        throw new ArgumentOutOfRangeException(nameof(call), call, "Not a VulkanCall member.");

    private static void ThrowSuccessIsNotAnError(VkResult result) =>
        throw new ArgumentOutOfRangeException(nameof(result), result, "VK_SUCCESS is not a failure.");
}

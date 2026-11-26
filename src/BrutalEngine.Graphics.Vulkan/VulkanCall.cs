namespace BrutalEngine.Graphics.Vulkan;

/// <summary>
/// One member per result-returning Vulkan or VMA call the backend makes (docs/design/graphics-vulkan.md section 12),
/// in the note's order. The call gate names the call in every error, and the fault injector targets calls by member.
/// </summary>
/// <remarks>
/// An issue that adds a result-returning call adds its member here in the same commit (rule 1 in section 13). Values
/// are contiguous from 0, so the fault injector can index arrays by member.
/// </remarks>
internal enum VulkanCall
{
    // Instance and adapters (sections 1-3).

    /// <summary><c>vkEnumerateInstanceExtensionProperties</c>.</summary>
    EnumerateInstanceExtensionProperties,

    /// <summary><c>vkEnumerateInstanceLayerProperties</c>.</summary>
    EnumerateInstanceLayerProperties,

    /// <summary><c>vkCreateInstance</c>, through BRUTAL's <c>Vulkan.CreateInstance</c> wrapper.</summary>
    CreateInstance,

    /// <summary><c>vkCreateDebugUtilsMessengerEXT</c>.</summary>
    CreateDebugUtilsMessenger,

    /// <summary><c>vkEnumerateDeviceExtensionProperties</c>.</summary>
    EnumerateDeviceExtensionProperties,

    /// <summary><c>vkGetPhysicalDeviceImageFormatProperties2</c>.</summary>
    GetImageFormatProperties2,

    /// <summary><c>vkCreateDevice</c>, through BRUTAL's <c>PhysicalDevice.CreateDevice</c> wrapper.</summary>
    CreateDevice,

    // Surfaces and swapchains (section 4).

    /// <summary><c>vkGetPhysicalDeviceSurfaceSupportKHR</c>.</summary>
    GetSurfaceSupport,

    /// <summary><c>vkGetPhysicalDeviceSurfaceCapabilitiesKHR</c>.</summary>
    GetSurfaceCapabilities,

    /// <summary><c>vkGetPhysicalDeviceSurfaceFormatsKHR</c>.</summary>
    GetSurfaceFormats,

    /// <summary><c>vkGetPhysicalDeviceSurfacePresentModesKHR</c>.</summary>
    GetSurfacePresentModes,

    /// <summary><c>vkCreateSwapchainKHR</c>.</summary>
    CreateSwapchain,

    /// <summary><c>vkGetSwapchainImagesKHR</c>.</summary>
    GetSwapchainImages,

    /// <summary><c>vkAcquireNextImageKHR</c>.</summary>
    AcquireNextImage,

    /// <summary><c>vkQueuePresentKHR</c>.</summary>
    QueuePresent,

    /// <summary><c>vkWaitForFences</c> on a present fence (swapchain maintenance1).</summary>
    WaitForPresentFence,

    // Frames (section 5).

    /// <summary><c>vkCreateCommandPool</c>.</summary>
    CreateCommandPool,

    /// <summary><c>vkAllocateCommandBuffers</c>, through <c>Brutal.Vulkan.Abstractions</c>' wrapper.</summary>
    AllocateCommandBuffers,

    /// <summary><c>vkCreateFence</c>.</summary>
    CreateFence,

    /// <summary><c>vkCreateSemaphore</c>.</summary>
    CreateSemaphore,

    /// <summary><c>vkWaitForFences</c> on a frame slot's fence.</summary>
    WaitForFences,

    /// <summary><c>vkGetFenceStatus</c>.</summary>
    GetFenceStatus,

    /// <summary><c>vkResetFences</c>.</summary>
    ResetFences,

    /// <summary><c>vkResetCommandPool</c>.</summary>
    ResetCommandPool,

    /// <summary><c>vkBeginCommandBuffer</c>.</summary>
    BeginCommandBuffer,

    /// <summary><c>vkEndCommandBuffer</c>.</summary>
    EndCommandBuffer,

    /// <summary><c>vkQueueSubmit2</c>.</summary>
    QueueSubmit2,

    /// <summary><c>vkDeviceWaitIdle</c>.</summary>
    DeviceWaitIdle,

    // Memory and resources (sections 6-7).

    /// <summary><c>vmaCreateAllocator</c>.</summary>
    CreateVmaAllocator,

    /// <summary><c>vmaCreateBuffer</c>.</summary>
    CreateBuffer,

    /// <summary><c>vmaCreateImage</c>.</summary>
    CreateImage,

    /// <summary><c>vkCreateImageView</c>.</summary>
    CreateImageView,

    /// <summary><c>vkCreateSampler</c>.</summary>
    CreateSampler,

    // Descriptors and pipelines (sections 8-9).

    /// <summary><c>vkCreateDescriptorSetLayout</c>.</summary>
    CreateDescriptorSetLayout,

    /// <summary><c>vkCreateDescriptorPool</c>.</summary>
    CreateDescriptorPool,

    /// <summary><c>vkResetDescriptorPool</c>.</summary>
    ResetDescriptorPool,

    /// <summary><c>vkAllocateDescriptorSets</c>.</summary>
    AllocateDescriptorSets,

    /// <summary><c>vkFreeDescriptorSets</c>.</summary>
    FreeDescriptorSets,

    /// <summary><c>vkCreateShaderModule</c>. Native failures become <c>InvalidData</c> (section 13).</summary>
    CreateShaderModule,

    /// <summary><c>vkCreatePipelineLayout</c>.</summary>
    CreatePipelineLayout,

    /// <summary><c>vkCreatePipelineCache</c>.</summary>
    CreatePipelineCache,

    /// <summary><c>vkCreateGraphicsPipelines</c>. Native failures become <c>InvalidData</c> (section 13).</summary>
    CreateGraphicsPipelines,

    /// <summary><c>vkCreateComputePipelines</c>. Native failures become <c>InvalidData</c> (section 13).</summary>
    CreateComputePipelines,

    // Timestamps and diagnostics (sections 3, 11).

    /// <summary><c>vkCreateQueryPool</c>.</summary>
    CreateQueryPool,

    /// <summary><c>vkGetQueryPoolResults</c>.</summary>
    GetQueryPoolResults,

    /// <summary><c>vkSetDebugUtilsObjectNameEXT</c>.</summary>
    SetDebugUtilsObjectName,
}

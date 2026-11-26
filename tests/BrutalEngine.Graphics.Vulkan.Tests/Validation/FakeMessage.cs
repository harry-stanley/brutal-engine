using System;
using Brutal;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Instances;

namespace BrutalEngine.Graphics.Vulkan.Tests.Validation;

/// <summary>Invokes <see cref="ValidationCallback.Invoke"/> through its function pointer, as the layer does.</summary>
internal static unsafe class FakeMessage
{
    public const VkDebugUtilsMessageSeverityFlagsEXT Error = VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT;
    public const VkDebugUtilsMessageSeverityFlagsEXT Warning = VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT;
    public const VkDebugUtilsMessageTypeFlagsEXT Validation = VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT;

    /// <summary>The message id the default name stands for.</summary>
    public const int Id = 0x1234;

    /// <summary>Sends one message with the default name and text.</summary>
    public static bool Send(nint userData, VkDebugUtilsMessageSeverityFlagsEXT severity, VkDebugUtilsMessageTypeFlagsEXT types, int id = Id) =>
        Send(userData, severity, types, id, "VUID-Test-name\0"u8, "Test message text.\0"u8);

    /// <summary>Sends one message. <paramref name="name"/> and <paramref name="message"/> end with a NUL.</summary>
    public static bool Send(
        nint userData,
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT types,
        int id,
        ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> message)
    {
        // Sound: both spans are pinned for the call, NUL-terminated by the caller, and the data struct is a local.
        fixed (byte* pName = name)
        fixed (byte* pMessage = message)
        {
            VkDebugUtilsMessengerCallbackDataEXT data = new() { MessageIdName = pName, MessageIdNumber = id, Message = pMessage };
            delegate* unmanaged[Cdecl]<VkDebugUtilsMessageSeverityFlagsEXT, VkDebugUtilsMessageTypeFlagsEXT, VkDebugUtilsMessengerCallbackDataEXT*, void*, Bool32> callback = &ValidationCallback.Invoke;
            return callback(severity, types, &data, (void*)userData);
        }
    }
}

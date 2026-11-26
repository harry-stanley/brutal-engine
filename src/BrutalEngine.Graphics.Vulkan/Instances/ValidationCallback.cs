using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Instances;

/// <summary>
/// The debug messenger's callback (docs/design/graphics-vulkan.md section 1): a static
/// <see cref="UnmanagedCallersOnlyAttribute"/> function, which captures nothing, so its state comes from
/// <c>pUserData</c>, a <see cref="ValidationSink"/>'s <see cref="ValidationSink.UserData"/>.
/// </summary>
/// <remarks>
/// The layer calls it on any thread, including inside <c>vkCreateInstance</c> and <c>vkDestroyInstance</c> when the
/// create info is chained into the instance. It never lets an exception escape (one escaping an
/// <see cref="UnmanagedCallersOnlyAttribute"/> method ends the process), allocates nothing, and always returns
/// <c>VK_FALSE</c>, so the layer never aborts the call that caused the message.
/// </remarks>
internal static unsafe class ValidationCallback
{
    /// <summary>The severities the messenger subscribes to: warnings and errors.</summary>
    public const VkDebugUtilsMessageSeverityFlagsEXT Severities =
        VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT | VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT;

    /// <summary>The message types the messenger subscribes to: general, validation and performance.</summary>
    public const VkDebugUtilsMessageTypeFlagsEXT Types =
        VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT
        | VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT
        | VkDebugUtilsMessageTypeFlagsEXT.PerformanceBitEXT;

    /// <summary>The messenger description that reports to a sink: <see cref="Severities"/>, <see cref="Types"/>, this callback and the sink's handle.</summary>
    /// <param name="sink">The sink; must outlive every messenger and instance created with the result.</param>
    /// <returns>The create info, with no chain.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="sink"/> is released.</exception>
    public static VkDebugUtilsMessengerCreateInfoEXT CreateInfo(ValidationSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        nint userData = sink.UserData;
        ObjectDisposedException.ThrowIf(userData == 0, sink);
        return new VkDebugUtilsMessengerCreateInfoEXT
        {
            MessageSeverity = Severities,
            MessageType = Types,
            PfnUserCallback = new PfnVkDebugUtilsMessengerCallbackEXT(&Invoke),
            UserData = (void*)userData,
        };
    }

    /// <summary>Receives one message: <c>PFN_vkDebugUtilsMessengerCallbackEXT</c>.</summary>
    /// <param name="severity">The message's severity.</param>
    /// <param name="types">The message's types.</param>
    /// <param name="data">The message; read only during the call.</param>
    /// <param name="userData">
    /// A sink's <see cref="ValidationSink.UserData"/>; 0 does nothing, and so does a freed handle until the runtime reuses
    /// its slot (<see cref="ValidationSink.Release"/>).
    /// </param>
    /// <returns><c>VK_FALSE</c>, always.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static Bool32 Invoke(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT types,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        ValidationSink? sink = null;

        // Catches everything, failed logging included: nothing may unwind into the layer's native frames. The only
        // recovery is counting the lost message as an error, which cannot throw.
        try
        {
            if (userData is not null)
            {
                // A freed handle's slot reads as null only until the runtime reuses it, which can be the very next
                // allocation; then the cast reaches whatever now owns the slot. Release's contract (only after
                // vkDestroyInstance returned) means no callback arrives with a freed handle.
                sink = GCHandle.FromIntPtr((nint)userData).Target as ValidationSink;
                sink?.Receive(severity, types, data);
            }
        }
        catch (Exception)
        {
            sink?.CountFailure();
        }

        return false;
    }
}

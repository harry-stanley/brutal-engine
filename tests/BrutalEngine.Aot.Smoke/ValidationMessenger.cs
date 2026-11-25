using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Brutal;
using Brutal.VulkanApi;

namespace BrutalEngine.Aot.Smoke;

/// <summary>Counts and prints messages from the Khronos validation layer.</summary>
/// <remarks>
/// The callback is an <see cref="UnmanagedCallersOnlyAttribute"/> function pointer, which can't capture state,
/// so the counters are static. This is a single-run harness process, not engine code.
/// </remarks>
internal static unsafe class ValidationMessenger
{
    private static int _errors;
    private static int _warnings;

    /// <summary>Number of error-severity messages so far.</summary>
    public static int Errors => Volatile.Read(ref _errors);

    /// <summary>Number of warning-severity messages so far.</summary>
    public static int Warnings => Volatile.Read(ref _warnings);

    /// <summary>Creates the messenger description: warnings and errors of every message type.</summary>
    /// <returns>The create info, with the callback set.</returns>
    public static VkDebugUtilsMessengerCreateInfoEXT CreateInfo() => new()
    {
        MessageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT | VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT,
        MessageType = VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT
            | VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT
            | VkDebugUtilsMessageTypeFlagsEXT.PerformanceBitEXT,
        PfnUserCallback = new PfnVkDebugUtilsMessengerCallbackEXT(&Callback),
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Bool32 Callback(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT type,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        bool isError = (severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0;
        _ = isError ? Interlocked.Increment(ref _errors) : Interlocked.Increment(ref _warnings);

        string message = data->Message == null ? string.Empty : Marshal.PtrToStringUTF8((nint)data->Message) ?? string.Empty;
        Console.WriteLine($"[validation {(isError ? "ERROR" : "WARNING")} {type}] {message}");

        // Returning false tells the layer not to abort the call that triggered the message.
        return false;
    }
}

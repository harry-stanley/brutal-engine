using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Brutal;
using Brutal.VulkanApi;

namespace BrutalEngine.Aot.Smoke;

/// <summary>Counts and prints one instance's messages from the Khronos validation layer.</summary>
/// <remarks>
/// The callback is a static <see cref="UnmanagedCallersOnlyAttribute"/> function pointer, which can't capture state, so
/// the counters belong to this object and reach the callback through <c>pUserData</c>: a normal <see cref="GCHandle"/>
/// to it, as the engine's validation sink does (docs/design/graphics-vulkan.md section 1). Running it here proves that
/// path under NativeAOT. Dispose only after <c>vkDestroyInstance</c> returned: the chained messenger may call back until
/// then.
/// </remarks>
internal sealed unsafe class ValidationMessenger : IDisposable
{
    private GCHandle _handle;
    private int _errors;
    private int _warnings;

    /// <summary>Creates the messenger state and the handle its callback recovers it from.</summary>
    public ValidationMessenger() => _handle = GCHandle.Alloc(this, GCHandleType.Normal);

    /// <summary>Number of error-severity messages so far.</summary>
    public int Errors => Volatile.Read(ref _errors);

    /// <summary>Number of warning-severity messages so far.</summary>
    public int Warnings => Volatile.Read(ref _warnings);

    /// <summary>Creates the messenger description: warnings and errors of every message type, reporting to this object.</summary>
    /// <returns>The create info, with the callback and user data set.</returns>
    /// <exception cref="ObjectDisposedException">The handle is freed.</exception>
    public VkDebugUtilsMessengerCreateInfoEXT CreateInfo()
    {
        ObjectDisposedException.ThrowIf(!_handle.IsAllocated, this);
        return new VkDebugUtilsMessengerCreateInfoEXT
        {
            MessageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT | VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT,
            MessageType = VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT
                | VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT
                | VkDebugUtilsMessageTypeFlagsEXT.PerformanceBitEXT,
            PfnUserCallback = new PfnVkDebugUtilsMessengerCallbackEXT(&Callback),
            UserData = (void*)GCHandle.ToIntPtr(_handle),
        };
    }

    /// <summary>Frees the handle once; only after the instance is destroyed.</summary>
    public void Dispose()
    {
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Bool32 Callback(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT type,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        // Nothing may unwind into the layer's native frames: an exception escaping this method ends the process.
        try
        {
            if (userData is not null && GCHandle.FromIntPtr((nint)userData).Target is ValidationMessenger messenger)
            {
                bool isError = (severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0;
                _ = isError ? Interlocked.Increment(ref messenger._errors) : Interlocked.Increment(ref messenger._warnings);

                string message = data == null || data->Message == null ? string.Empty : Marshal.PtrToStringUTF8((nint)data->Message) ?? string.Empty;
                Console.WriteLine($"[validation {(isError ? "ERROR" : "WARNING")} {type}] {message}");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Validation callback failed: {exception.Message}");
        }

        // Returning false tells the layer not to abort the call that triggered the message.
        return false;
    }
}

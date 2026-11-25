using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Brutal;
using Brutal.VulkanApi;
using BrutalEngine.Core.Logging;

namespace HelloWindow;

/// <summary>Counts and logs messages from the Khronos validation layer.</summary>
/// <remarks>
/// <para>
/// The callback is an <see cref="UnmanagedCallersOnlyAttribute"/> function pointer, which can't capture state, so it
/// finds its messenger through the callback's user data (<c>pUserData</c>): a <see cref="GCHandle"/> to this object,
/// allocated by the constructor and freed by <see cref="Dispose"/>. Nothing is static, so two messengers never share
/// counts.
/// </para>
/// <para>
/// Thread safety: the layer may call the callback from any thread, hence the interlocked counters.
/// </para>
/// <para>
/// Lifetime: dispose only after every Vulkan object given <see cref="CreateInfo"/> is destroyed, the debug messenger
/// and the instance (whose creation chain uses it for <c>vkCreateInstance</c> and <c>vkDestroyInstance</c>). A
/// callback after that would read a freed handle.
/// </para>
/// </remarks>
internal sealed unsafe class ValidationMessenger : IDisposable
{
    private readonly LogCategory _log;
    private GCHandle _self;
    private int _errors;
    private int _warnings;

    /// <summary>Initializes a new instance of the <see cref="ValidationMessenger"/> class.</summary>
    /// <param name="log">Where messages are logged: errors at <see cref="LogLevel.Error"/>, warnings at
    /// <see cref="LogLevel.Warning"/>.</param>
    public ValidationMessenger(LogCategory log)
    {
        _log = log;
        _self = GCHandle.Alloc(this);
    }

    /// <summary>Gets the number of error-severity messages so far.</summary>
    public int Errors => Volatile.Read(ref _errors);

    /// <summary>Gets the number of warning-severity messages so far.</summary>
    public int Warnings => Volatile.Read(ref _warnings);

    /// <summary>Creates the messenger description: warnings and errors of every message type, with this messenger as
    /// the callback's user data.</summary>
    /// <returns>The create info, with the callback and user data set.</returns>
    /// <exception cref="ObjectDisposedException">The messenger is disposed.</exception>
    public VkDebugUtilsMessengerCreateInfoEXT CreateInfo()
    {
        ObjectDisposedException.ThrowIf(!_self.IsAllocated, this);
        return new()
        {
            MessageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT | VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT,
            MessageType = VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT
                | VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT
                | VkDebugUtilsMessageTypeFlagsEXT.PerformanceBitEXT,
            PfnUserCallback = new PfnVkDebugUtilsMessengerCallbackEXT(&Callback),
            UserData = (void*)GCHandle.ToIntPtr(_self),
        };
    }

    /// <summary>Frees the handle the callback finds this messenger by. A second call does nothing.</summary>
    public void Dispose()
    {
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Bool32 Callback(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT type,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        // Every create info this sample passes carries a live handle (see the remarks); anything else is ignored rather
        // than crashing the layer's thread.
        if (userData != null && GCHandle.FromIntPtr((nint)userData).Target is ValidationMessenger messenger)
        {
            messenger.Report(severity, type, data);
        }

        // Returning false tells the layer not to abort the call that triggered the message.
        return false;
    }

    private void Report(VkDebugUtilsMessageSeverityFlagsEXT severity, VkDebugUtilsMessageTypeFlagsEXT type, VkDebugUtilsMessengerCallbackDataEXT* data)
    {
        bool isError = (severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0;
        _ = isError ? Interlocked.Increment(ref _errors) : Interlocked.Increment(ref _warnings);

        // Sound: the layer passes a NUL-terminated UTF-8 message that stays valid for the duration of the callback, and
        // the logger copies it before returning. Invalid UTF-8 is replaced, so the span is logged as it is.
        ReadOnlySpan<byte> message = data == null || data->Message == null ? default : MemoryMarshal.CreateReadOnlySpanFromNullTerminated(data->Message);
        _log.Log(isError ? LogLevel.Error : LogLevel.Warning, $"validation ({type}): {message}");
    }
}

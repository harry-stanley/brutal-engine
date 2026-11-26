using System;
using System.Runtime.InteropServices;
using System.Threading;
using Brutal.VulkanApi;
using BrutalEngine.Core.Logging;

namespace BrutalEngine.Graphics.Vulkan.Instances;

/// <summary>
/// Counts and logs one instance's validation messages (docs/design/graphics-vulkan.md section 1). The messenger's
/// <c>pUserData</c> is <see cref="UserData"/>, a normal (not pinned) <see cref="GCHandle"/> to this sink, which
/// <see cref="ValidationCallback"/> turns back into the sink, so two instances in one process never share counters.
/// </summary>
/// <remarks>
/// <para>
/// Subscribed messages are warnings and errors of the general, validation and performance types. A message whose
/// <c>messageIdNumber</c> is on the allow-list is logged at <see cref="LogLevel.Debug"/> with its reason and counted
/// only in <see cref="Allowed"/>; other errors are logged at <see cref="LogLevel.Error"/> and counted in
/// <see cref="Errors"/>, other warnings at <see cref="LogLevel.Warning"/> in <see cref="Warnings"/>.
/// </para>
/// <para>
/// Ownership: one sink per <c>VkInstance</c>, created before the instance and released after it. Teardown is the
/// messenger, then <c>vkDestroyInstance</c>, then <see cref="Release"/>: until <c>vkDestroyInstance</c> returns, the
/// layer may still call back with <see cref="UserData"/>.
/// </para>
/// <para>
/// Thread-safety: the layer calls back on any thread, so the counters are <see cref="Interlocked"/> and every member is
/// safe to call concurrently. The constructor allocates the allow-list copy and the handle; receiving a message
/// allocates nothing (the category logs UTF-8 spans without allocating).
/// </para>
/// </remarks>
internal sealed class ValidationSink
{
    private readonly LogCategory _category;
    private readonly ValidationAllowance[] _allowList;
    private readonly HandleTracker? _tracker;
    private nint _userData;
    private long _errors;
    private long _warnings;
    private long _allowed;

    /// <summary>Creates a sink and the handle that <see cref="UserData"/> carries.</summary>
    /// <param name="category">Where messages are logged: the device logger's <c>Graphics.Validation</c> category.</param>
    /// <param name="allowList">The tolerated messages; copied, so later changes to the memory have no effect.</param>
    /// <param name="tracker">A test's handle counter, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">An allow-list entry's reason is <see langword="null"/>, empty or white space.</exception>
    public ValidationSink(LogCategory category, ReadOnlyMemory<ValidationAllowance> allowList, HandleTracker? tracker = null)
    {
        ReadOnlySpan<ValidationAllowance> entries = allowList.Span;
        for (int i = 0; i < entries.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(entries[i].Reason))
            {
                throw new ArgumentException($"Validation allow-list entry {i} (message id {entries[i].MessageIdNumber}) has no reason.", nameof(allowList));
            }
        }

        _category = category;
        _allowList = entries.ToArray();
        _tracker = tracker;
        _userData = GCHandle.ToIntPtr(GCHandle.Alloc(this, GCHandleType.Normal));
        tracker?.OnAllocated();
    }

    /// <summary>Gets the number of error messages, plus messages whose logging threw.</summary>
    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>Gets the number of warning messages.</summary>
    public long Warnings => Interlocked.Read(ref _warnings);

    /// <summary>Gets the number of allow-listed messages, which <see cref="Errors"/> and <see cref="Warnings"/> never count.</summary>
    public long Allowed => Interlocked.Read(ref _allowed);

    /// <summary>Gets the messenger's <c>pUserData</c>: <see cref="GCHandle.ToIntPtr"/> of the handle; 0 once released.</summary>
    public nint UserData => Volatile.Read(ref _userData);

    /// <summary>Gets whether <see cref="Release"/> has run.</summary>
    public bool IsReleased => UserData == 0;

    /// <summary>Frees the handle once; later calls do nothing.</summary>
    /// <remarks>
    /// Only after <c>vkDestroyInstance</c> returned: a callback that arrives with a freed handle finds no sink and does
    /// nothing, but a slot the runtime has reused for another object could be reached instead.
    /// </remarks>
    public void Release()
    {
        nint userData = Interlocked.Exchange(ref _userData, 0);
        if (userData == 0)
        {
            return;
        }

        GCHandle.FromIntPtr(userData).Free();
        _tracker?.OnReleased();
    }

    /// <summary>Counts and logs one message. Called by <see cref="ValidationCallback"/>.</summary>
    /// <param name="severity">The message's severity bits.</param>
    /// <param name="types">The message's type bits.</param>
    /// <param name="data">The layer's message, or <see langword="null"/>.</param>
    /// <remarks>Throws only what the category's logger throws; the callback catches it and calls <see cref="CountFailure"/>.</remarks>
    internal unsafe void Receive(VkDebugUtilsMessageSeverityFlagsEXT severity, VkDebugUtilsMessageTypeFlagsEXT types, VkDebugUtilsMessengerCallbackDataEXT* data)
    {
        bool isError = (severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0;
        if (!isError && (severity & VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT) == 0)
        {
            return;
        }

        ReadOnlySpan<byte> kind = KindOf(types);
        if (kind.IsEmpty)
        {
            return;
        }

        // Sound: the layer's strings are NUL-terminated and live until the callback returns, which is longer than
        // these spans, used only inside this call. A null data or string reads as empty.
        int id = data is null ? 0 : data->MessageIdNumber;
        ReadOnlySpan<byte> name = data is null ? default : MemoryMarshal.CreateReadOnlySpanFromNullTerminated(data->MessageIdName);
        ReadOnlySpan<byte> message = data is null ? default : MemoryMarshal.CreateReadOnlySpanFromNullTerminated(data->Message);
        for (int i = 0; i < _allowList.Length; i++)
        {
            if (_allowList[i].MessageIdNumber == id)
            {
                _ = Interlocked.Increment(ref _allowed);
                _category.Debug($"Allowed Vulkan {kind} message {name} ({id}): {message} Reason: {_allowList[i].Reason}");
                return;
            }
        }

        if (isError)
        {
            _ = Interlocked.Increment(ref _errors);
            _category.Error($"Vulkan {kind} message {name} ({id}): {message}");
        }
        else
        {
            _ = Interlocked.Increment(ref _warnings);
            _category.Warning($"Vulkan {kind} message {name} ({id}): {message}");
        }
    }

    /// <summary>Counts a message that couldn't be logged as one more error.</summary>
    internal void CountFailure() => Interlocked.Increment(ref _errors);

    // The subscribed types, the most specific first; empty for a type the messenger doesn't subscribe to.
    private static ReadOnlySpan<byte> KindOf(VkDebugUtilsMessageTypeFlagsEXT types) =>
        (types & VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT) != 0 ? "validation"u8
        : (types & VkDebugUtilsMessageTypeFlagsEXT.PerformanceBitEXT) != 0 ? "performance"u8
        : (types & VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT) != 0 ? "general"u8
        : default;

    /// <summary>
    /// Counts the handles of the sinks that were given it, so a test proves each handle is freed once. A debug seam
    /// owned by the test, never static.
    /// </summary>
    /// <remarks>Thread-safe; allocates nothing.</remarks>
    internal sealed class HandleTracker
    {
        private int _live;

        /// <summary>Gets the number of allocated handles not yet freed.</summary>
        public int Live => Volatile.Read(ref _live);

        /// <summary>Records an allocated handle.</summary>
        internal void OnAllocated() => Interlocked.Increment(ref _live);

        /// <summary>Records a freed handle.</summary>
        internal void OnReleased() => Interlocked.Decrement(ref _live);
    }
}

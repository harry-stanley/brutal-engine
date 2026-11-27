using System;
using Brutal.VulkanApi;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;

namespace BrutalEngine.Graphics.Vulkan.Devices;

/// <summary>
/// Names objects for debugging tools and validation messages with <c>vkSetDebugUtilsObjectNameEXT</c>
/// (docs/design/graphics-vulkan.md section 3, "Debug names"), as <c>IBackendDevice.SetDebugName</c> promises: best
/// effort, never fails, allocates nothing.
/// </summary>
/// <remarks>
/// <para>
/// Names are set only when the instance enabled <c>VK_EXT_debug_utils</c> and the device table holds the entry point:
/// the table loads it through <c>vkGetDeviceProcAddr</c> (<c>Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:1370-1371</c>),
/// which leaves it null for an extension that isn't enabled. Otherwise <see cref="Set"/> does nothing.
/// </para>
/// <para>
/// Each name is copied into a <see cref="BufferSize"/>-byte stack buffer with a terminating NUL (<see cref="Copy"/>). The
/// call's result goes through the device's gate, so device loss is still detected, but a failure is swallowed: the
/// first one is logged at <c>Debug</c>, later ones aren't.
/// </para>
/// <para>
/// Not thread-safe: the device's owner thread calls it. Allocates nothing, except the first failure's <c>Debug</c>
/// entry when that level is enabled.
/// </para>
/// </remarks>
internal static class DebugNames
{
    /// <summary>The size of the name buffer: at most this minus one bytes of a name are kept.</summary>
    public const int BufferSize = 256;

    /// <summary>Copies a name with a terminating NUL, cut when it doesn't fit.</summary>
    /// <param name="utf8Name">The name as UTF-8, without a NUL; copied as given when it fits.</param>
    /// <param name="buffer">Where the name and its NUL go; at least one byte.</param>
    /// <returns>
    /// The bytes copied before the NUL: the whole name when it is shorter than <paramref name="buffer"/>; otherwise the
    /// longest prefix that leaves room for the NUL and doesn't end inside a UTF-8 sequence.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> is empty.</exception>
    public static int Copy(ReadOnlySpan<byte> utf8Name, Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            throw new ArgumentException("The buffer must hold at least the NUL.", nameof(buffer));
        }

        int length = Math.Min(utf8Name.Length, buffer.Length - 1);

        // A cut name drops the sequence the cut lands in: step back over its continuation bytes (10xxxxxx) to its lead
        // byte, at most three, the longest a UTF-8 sequence has after its lead.
        for (int step = 0; step < 3 && length < utf8Name.Length && length > 0 && (utf8Name[length] & 0xC0) == 0x80; step++)
        {
            length--;
        }

        utf8Name[..length].CopyTo(buffer);
        buffer[length] = 0;
        return length;
    }

    /// <summary>Names an object of a device; does nothing when the device can't name objects.</summary>
    /// <param name="core">The live device.</param>
    /// <param name="type">The object's type.</param>
    /// <param name="handle">The object's handle.</param>
    /// <param name="utf8Name">The name as UTF-8, without a NUL; valid during the call only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The device can name objects and is disposed.</exception>
    public static void Set(VulkanDeviceCore core, VkObjectType type, ulong handle, ReadOnlySpan<byte> utf8Name)
    {
        ArgumentNullException.ThrowIfNull(core);
        if (core.Naming.Enabled)
        {
            Set(new DeviceNamer(core.Device), core.Naming, type, handle, utf8Name);
        }
    }

    /// <summary><see cref="Set(VulkanDeviceCore, VkObjectType, ulong, ReadOnlySpan{byte})"/> through a given namer.</summary>
    /// <typeparam name="TNamer">Makes the native call; the real one is a struct, so nothing is boxed.</typeparam>
    /// <param name="namer">Makes the native call.</param>
    /// <param name="naming">The device's naming state.</param>
    /// <param name="type">The object's type.</param>
    /// <param name="handle">The object's handle.</param>
    /// <param name="utf8Name">The name as UTF-8, without a NUL; valid during the call only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="naming"/> is <see langword="null"/>.</exception>
    internal static unsafe void Set<TNamer>(TNamer namer, Naming naming, VkObjectType type, ulong handle, ReadOnlySpan<byte> utf8Name)
        where TNamer : INamer
    {
        ArgumentNullException.ThrowIfNull(naming);
        if (!naming.Enabled)
        {
            return;
        }

        // Sound: the buffer and the description are stack locals, valid for the whole call, which copies the name.
        byte* buffer = stackalloc byte[BufferSize];
        _ = Copy(utf8Name, new Span<byte>(buffer, BufferSize));
        VkDebugUtilsObjectNameInfoEXT info = new() { ObjectType = type, ObjectHandle = unchecked((nint)handle), ObjectName = buffer };
        VulkanCallGate gate = naming.Gate;
        VkResult result = gate.TryTakeFault(VulkanCall.SetDebugUtilsObjectName, out VkResult injected) ? injected : namer.SetName(&info);
        Result checkedResult = gate.Check(result, VulkanCall.SetDebugUtilsObjectName);
        if (checkedResult.TryGetError(out EngineError error) && !naming.FailureLogged)
        {
            naming.FailureLogged = true;
            naming.Log.Debug($"{error.Message} ({error.NativeCode}); the name is dropped, and later failures aren't logged");
        }
    }

    /// <summary>A device's naming state.</summary>
    /// <param name="enabled">Whether the device can name objects.</param>
    /// <param name="gate">The device's gate.</param>
    /// <param name="log">Where the first failure is logged; <c>default</c> for nowhere.</param>
    /// <remarks>Not thread-safe: the device's owner thread uses it.</remarks>
    internal sealed class Naming(bool enabled, VulkanCallGate gate, LogCategory log)
    {
        /// <summary>Gets whether the device can name objects: <c>VK_EXT_debug_utils</c> is enabled and its entry point loaded.</summary>
        public bool Enabled { get; } = enabled;

        /// <summary>Gets the device's gate.</summary>
        public VulkanCallGate Gate { get; } = gate ?? throw new ArgumentNullException(nameof(gate));

        /// <summary>Gets where the first failure is logged.</summary>
        public LogCategory Log { get; } = log;

        /// <summary>Gets or sets whether a failure has been logged.</summary>
        public bool FailureLogged { get; set; }
    }

    /// <summary>Makes the native call, so tests can observe it or make it fail.</summary>
    internal unsafe interface INamer
    {
        /// <summary><c>vkSetDebugUtilsObjectNameEXT</c>.</summary>
        /// <param name="info">The name's description; its pointers are valid during the call only.</param>
        /// <returns>The command's result.</returns>
        public VkResult SetName(VkDebugUtilsObjectNameInfoEXT* info);
    }

    /// <summary>The real namer: the device's raw function table.</summary>
    /// <param name="device">The live device, whose table holds the entry point.</param>
    internal readonly unsafe struct DeviceNamer(Device device) : INamer
    {
        /// <inheritdoc/>
        public VkResult SetName(VkDebugUtilsObjectNameInfoEXT* info) =>
            device.PInvoke.vkSetDebugUtilsObjectNameEXT.Invoke(device.Handle, info);
    }
}

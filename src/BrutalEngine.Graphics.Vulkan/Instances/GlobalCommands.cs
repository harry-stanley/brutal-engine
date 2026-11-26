using System;
using System.Text;
using System.Threading;
using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Instances;

/// <summary>
/// Vulkan's global commands, the ones the backend makes before an instance exists: <c>vkEnumerateInstanceExtensionProperties</c>,
/// <c>vkEnumerateInstanceLayerProperties</c> and <c>vkCreateInstance</c>, and the process-wide <see cref="Gate"/> that
/// serializes them (docs/design/graphics-vulkan.md section 1, "Global commands are serialized").
/// </summary>
/// <remarks>
/// <para>
/// The Vulkan spec puts no external synchronization on these commands, but overlapping them crashes the process with the
/// NVIDIA proprietary driver on Linux (loader 1.4.363): each of them loads the drivers, and while one thread is still
/// inside the driver's <c>vk_icdNegotiateLoaderICDInterfaceVersion</c>, another jumps through a loader function pointer
/// that isn't set yet. So the backend makes every one of them through these helpers, which hold <see cref="Gate"/> for
/// the length of the command: devices created on several threads at once take turns, one global command at a time.
/// <c>vkCreateInstance</c> includes everything BRUTAL's <c>Vulkan.CreateInstance</c> does after it (loading the instance
/// table, enumerating the physical devices).
/// </para>
/// <para>
/// <c>vkDestroyInstance</c> is not a global command and stays outside the gate: destroying instances concurrently with
/// creation and enumeration never crashed in the repro. Global commands that another library in the process makes
/// itself don't pass through the gate either.
/// </para>
/// <para>
/// Thread-safe: any thread may call the helpers; a call blocks while another thread holds the gate. The helpers
/// allocate nothing themselves. The gate is static because the race is process-wide, so per-device gates wouldn't
/// exclude each other; it is a readonly field that holds no game type, which the static-state rule allows
/// (docs/design/architecture-tests.md).
/// </para>
/// <para>
/// The backend makes the commands through a <see cref="ISource"/>: <see cref="LoaderSource"/> in production, so tests
/// can script the loader's answers or observe the calls.
/// </para>
/// </remarks>
internal static class GlobalCommands
{
    /// <summary>The process-wide gate that every global command runs inside; re-entrant on one thread.</summary>
    internal static readonly Lock Gate = new();

    /// <summary>
    /// <c>vkCreateInstance</c>'s name, for a converted error's message. It lives here because a string that is exactly a
    /// global command's name is what a lookup by name passes, which the backend's source scan allows only in this file.
    /// </summary>
    internal const string CreateInstanceName = "vkCreateInstance";

    /// <summary><c>vkEnumerateInstanceExtensionProperties</c> through <paramref name="source"/>, inside <see cref="Gate"/>.</summary>
    /// <typeparam name="TSource">The global commands; the real ones are a struct, so nothing is boxed.</typeparam>
    /// <param name="source">The global commands.</param>
    /// <param name="layerName">As <see cref="ISource.EnumerateExtensions"/>.</param>
    /// <param name="count">As <see cref="ISource.EnumerateExtensions"/>.</param>
    /// <param name="properties">As <see cref="ISource.EnumerateExtensions"/>.</param>
    /// <returns>The command's result.</returns>
    public static VkResult EnumerateExtensions<TSource>(TSource source, string? layerName, ref int count, Span<VkExtensionProperties> properties)
        where TSource : ISource
    {
        using (Gate.EnterScope())
        {
            return source.EnumerateExtensions(layerName, ref count, properties);
        }
    }

    /// <summary><c>vkEnumerateInstanceLayerProperties</c> through <paramref name="source"/>, inside <see cref="Gate"/>.</summary>
    /// <typeparam name="TSource">The global commands; the real ones are a struct, so nothing is boxed.</typeparam>
    /// <param name="source">The global commands.</param>
    /// <param name="count">As <see cref="ISource.EnumerateLayers"/>.</param>
    /// <param name="properties">As <see cref="ISource.EnumerateLayers"/>.</param>
    /// <returns>The command's result.</returns>
    public static VkResult EnumerateLayers<TSource>(TSource source, ref int count, Span<VkLayerProperties> properties)
        where TSource : ISource
    {
        using (Gate.EnterScope())
        {
            return source.EnumerateLayers(ref count, properties);
        }
    }

    /// <summary><c>vkCreateInstance</c> through <paramref name="source"/>, inside <see cref="Gate"/>, which is released when it throws.</summary>
    /// <typeparam name="TSource">The global commands; the real ones are a struct, so nothing is boxed.</typeparam>
    /// <param name="source">The global commands.</param>
    /// <param name="createInfo">As <see cref="ISource.CreateInstance"/>.</param>
    /// <returns>The instance, owned by the caller.</returns>
    /// <exception cref="VkResultError">As <see cref="ISource.CreateInstance"/>.</exception>
    public static Instance CreateInstance<TSource>(TSource source, Instance.CreateInfo createInfo)
        where TSource : ISource
    {
        using (Gate.EnterScope())
        {
            return source.CreateInstance(createInfo);
        }
    }

    /// <summary>
    /// The global commands, so tests can script the loader. The enumerations mirror their Vulkan commands: an empty span
    /// asks for the count, a non-empty one is filled with up to <c>count</c> entries.
    /// </summary>
    internal interface ISource
    {
        /// <summary><c>vkEnumerateInstanceExtensionProperties</c>.</summary>
        /// <param name="layerName">The layer, or <see langword="null"/> for the loader's list.</param>
        /// <param name="count">In: the span's capacity (ignored for an empty span). Out: the count, or how many were written.</param>
        /// <param name="properties">Empty to ask for the count; otherwise the entries to fill.</param>
        /// <returns>The command's result.</returns>
        public VkResult EnumerateExtensions(string? layerName, ref int count, Span<VkExtensionProperties> properties);

        /// <summary><c>vkEnumerateInstanceLayerProperties</c>.</summary>
        /// <param name="count">As <see cref="EnumerateExtensions"/>.</param>
        /// <param name="properties">As <see cref="EnumerateExtensions"/>.</param>
        /// <returns>The command's result.</returns>
        public VkResult EnumerateLayers(ref int count, Span<VkLayerProperties> properties);

        /// <summary><c>vkCreateInstance</c>, through BRUTAL's <c>Vulkan.CreateInstance</c>.</summary>
        /// <param name="createInfo">BRUTAL's description; its pointers stay valid until this returns.</param>
        /// <returns>The instance, owned by the caller.</returns>
        /// <exception cref="VkResultError">
        /// <c>vkCreateInstance</c> failed, or BRUTAL's constructor failed after the instance existed (see
        /// <see cref="InstanceFactory.IsCreateInstanceFailure"/>).
        /// </exception>
        public Instance CreateInstance(Instance.CreateInfo createInfo);
    }

    /// <summary>
    /// The real global commands, through BRUTAL's raw global bindings
    /// (<c>Brutal.Vulkan/Generated/PInvoke/VulkanApi/VulkanApi.gen.cs:50-55</c>, <c>:73-77</c>) and its
    /// <c>Vulkan.CreateInstance</c> (<c>Brutal.Vulkan/Manual/Partials/Vulkan.cs:10-13</c>). The loader must already be
    /// bound: <c>Vulkan.PInvoke</c> doesn't run the <c>Vulkan</c> class's static constructor, which registers BRUTAL's
    /// import resolver, so a <see cref="VulkanBackend"/> must have been created first.
    /// </summary>
    internal readonly struct LoaderSource : ISource
    {
        /// <inheritdoc/>
        public unsafe VkResult EnumerateExtensions(string? layerName, ref int count, Span<VkExtensionProperties> properties)
        {
            Span<byte> name = stackalloc byte[VK.MAX_EXTENSION_NAME_SIZE];
            if (layerName is not null)
            {
                // Throws for a name longer than the Vulkan limit; the only caller passes InstanceExtensions.ValidationLayer.
                name[Encoding.UTF8.GetBytes(layerName, name[..^1])] = 0;
            }

            // Sound: every pointer is to a pinned local or span that outlives the call, the count says how many entries
            // properties holds (an empty span pins as null, the count query), and the name is NUL-terminated.
            fixed (byte* pName = name)
            fixed (int* pCount = &count)
            fixed (VkExtensionProperties* pProperties = properties)
            {
                return global::Brutal.VulkanApi.Vulkan.PInvoke.vkEnumerateInstanceExtensionProperties(layerName is null ? null : pName, pCount, pProperties);
            }
        }

        /// <inheritdoc/>
        public unsafe VkResult EnumerateLayers(ref int count, Span<VkLayerProperties> properties)
        {
            // Sound: as EnumerateExtensions.
            fixed (int* pCount = &count)
            fixed (VkLayerProperties* pProperties = properties)
            {
                return global::Brutal.VulkanApi.Vulkan.PInvoke.vkEnumerateInstanceLayerProperties(pCount, pProperties);
            }
        }

        /// <inheritdoc/>
        public Instance CreateInstance(Instance.CreateInfo createInfo) => global::Brutal.VulkanApi.Vulkan.CreateInstance(createInfo);
    }
}

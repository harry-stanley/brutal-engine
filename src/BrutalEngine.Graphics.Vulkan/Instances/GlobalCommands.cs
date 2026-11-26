using System;
using System.Text;
using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Instances;

/// <summary>
/// Vulkan's global commands, the ones the backend makes before an instance exists: <c>vkEnumerateInstanceExtensionProperties</c>,
/// <c>vkEnumerateInstanceLayerProperties</c> and <c>vkCreateInstance</c> (docs/design/graphics-vulkan.md section 1).
/// </summary>
/// <remarks>
/// The backend makes them through a <see cref="ISource"/>: <see cref="LoaderSource"/> in production, so tests can
/// script the loader's answers or observe the calls.
/// </remarks>
internal static class GlobalCommands
{
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

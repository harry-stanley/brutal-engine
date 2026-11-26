using System;
using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Instances;

/// <summary>
/// A device's <c>VkInstance</c> (docs/design/graphics-vulkan.md section 1), created by <see cref="InstanceFactory"/>
/// through BRUTAL's <c>Instance</c> wrapper, with the extensions and layers it was created with.
/// </summary>
/// <remarks>
/// <para>
/// Owned by the device that created it, which disposes it last, after everything created from it. Not thread-safe: the
/// device's owner thread uses it. Members allocate nothing.
/// </para>
/// <para>
/// <see cref="Dispose"/> is idempotent through this class's own flag, because BRUTAL's <c>Instance.Dispose</c> destroys
/// the instance and frees its function table again on every call (<c>Brutal.Vulkan/Manual/Classes/Instance.cs:152-160</c>;
/// upstream issue draft #20).
/// </para>
/// </remarks>
internal sealed class VulkanInstance : IDisposable
{
    private readonly Instance _instance;
    private readonly InstanceExtensions.Selection _selection;
    private bool _disposed;

    /// <summary>Takes ownership of a created instance.</summary>
    /// <param name="instance">The instance; disposed by <see cref="Dispose"/>.</param>
    /// <param name="selection">What it was created with.</param>
    internal VulkanInstance(Instance instance, InstanceExtensions.Selection selection)
    {
        _instance = instance;
        _selection = selection;
    }

    /// <summary>Gets BRUTAL's wrapper, for its function table and the physical devices it enumerated.</summary>
    /// <exception cref="ObjectDisposedException">The instance is disposed.</exception>
    public Instance Brutal
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _instance;
        }
    }

    /// <summary>Gets the raw handle.</summary>
    /// <exception cref="ObjectDisposedException">The instance is disposed.</exception>
    public VkInstance Handle => Brutal.Handle;

    /// <summary>Gets whether the validation layer and synchronization validation are enabled.</summary>
    public bool Validation => _selection.Validation;

    /// <summary>Gets the enabled extensions, in the order they were planned.</summary>
    public ReadOnlySpan<string> EnabledExtensions => _selection.Extensions;

    /// <summary>Gets the enabled layers: the validation layer or none.</summary>
    public ReadOnlySpan<string> EnabledLayers => _selection.Layers;

    /// <summary>Gets the instance create flags.</summary>
    public VkInstanceCreateFlags Flags => _selection.Flags;

    /// <summary>Gets whether <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Whether an extension is enabled.</summary>
    /// <param name="name">The extension name; compared ordinally.</param>
    /// <returns><see langword="true"/> if it was enabled at creation.</returns>
    public bool IsExtensionEnabled(string name) => EnabledExtensions.Contains(name);

    /// <summary>Destroys the instance once; later calls do nothing.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Set first, so a throwing destroy is never retried against a freed function table.
        _disposed = true;
        _instance.Dispose();
    }
}

using System;
using Brutal.VulkanApi;

namespace BrutalEngine.Graphics.Vulkan.Instances;

/// <summary>
/// A device's <c>VkInstance</c> (docs/design/graphics-vulkan.md section 1), created by <see cref="InstanceFactory"/>
/// through BRUTAL's <c>Instance</c> wrapper, with the extensions and layers it was created with and, with validation,
/// its debug messenger and <see cref="ValidationSink"/>.
/// </summary>
/// <remarks>
/// <para>
/// Owned by the device that created it, which disposes it last, after everything created from it. Not thread-safe: the
/// device's owner thread uses it, except the validation counters, which any thread may read. Members allocate nothing.
/// </para>
/// <para>
/// <see cref="Dispose"/> is idempotent through this class's own flag, because BRUTAL's <c>Instance.Dispose</c> destroys
/// the instance and frees its function table again on every call (<c>Brutal.Vulkan/Manual/Classes/Instance.cs:152-160</c>;
/// upstream issue draft #20). It destroys the messenger, then the instance, then releases the sink, whose handle the
/// chained messenger uses until <c>vkDestroyInstance</c> returns.
/// </para>
/// </remarks>
internal sealed class VulkanInstance : IDisposable
{
    private readonly Instance _instance;
    private readonly InstanceExtensions.Selection _selection;
    private readonly VkDebugUtilsMessengerEXT _messenger;
    private readonly ValidationSink? _sink;
    private bool _disposed;

    /// <summary>Takes ownership of a created instance and, with validation, its messenger and sink.</summary>
    /// <param name="instance">The instance; disposed by <see cref="Dispose"/>.</param>
    /// <param name="selection">What it was created with.</param>
    /// <param name="messenger">The persistent messenger, or the null handle without validation; destroyed first.</param>
    /// <param name="sink">The messenger's sink, or <see langword="null"/> without validation; released last.</param>
    internal VulkanInstance(Instance instance, InstanceExtensions.Selection selection, VkDebugUtilsMessengerEXT messenger, ValidationSink? sink)
    {
        _instance = instance;
        _selection = selection;
        _messenger = messenger;
        _sink = sink;
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

    /// <summary>Gets the validation errors so far, including after disposal; 0 without validation.</summary>
    public long ValidationErrors => _sink?.Errors ?? 0;

    /// <summary>Gets the validation warnings so far, including after disposal; 0 without validation.</summary>
    public long ValidationWarnings => _sink?.Warnings ?? 0;

    /// <summary>Gets the allow-listed validation messages so far, including after disposal; 0 without validation.</summary>
    public long ValidationAllowed => _sink?.Allowed ?? 0;

    /// <summary>Gets the sink, for tests and the device; <see langword="null"/> without validation.</summary>
    internal ValidationSink? Sink => _sink;

    /// <summary>Whether an extension is enabled.</summary>
    /// <param name="name">The extension name; compared ordinally.</param>
    /// <returns><see langword="true"/> if it was enabled at creation.</returns>
    public bool IsExtensionEnabled(string name) => EnabledExtensions.Contains(name);

    /// <summary>Destroys the messenger, then the instance, then releases the sink, once; later calls do nothing.</summary>
    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Set first, so a throwing destroy is never retried against a freed function table.
        _disposed = true;
        if (!_messenger.IsNull())
        {
            // Sound: the messenger was created from this instance's table, which is still loaded; no allocator was used.
            _instance.PInvoke.vkDestroyDebugUtilsMessengerEXT.Invoke(_instance.Handle, _messenger, null);
        }

        _instance.Dispose();

        // Only now: vkDestroyInstance may call the chained messenger, which reaches the sink through its handle. If the
        // destroy threw, the instance may still exist, so the handle is kept.
        _sink?.Release();
    }
}

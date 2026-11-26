using System;
using System.Collections.Generic;
using System.Text;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Instances;

namespace BrutalEngine.Graphics.Vulkan.Tests.Instances;

/// <summary>
/// A scripted loader for <see cref="InstanceExtensions"/>: lists per layer, behaving as the Vulkan commands do (an empty
/// span asks for the count; a fill writes up to its capacity and returns <c>VK_INCOMPLETE</c> when more are left; an
/// unknown layer is <c>VK_ERROR_LAYER_NOT_PRESENT</c>). Each of the next <see cref="GrowthsLeft"/> count queries is
/// followed by one more entry in the queried list, as when a layer is installed between the two calls, and each of the
/// next <see cref="ShrinksLeft"/> by one entry fewer, as when one is removed. <see cref="CreateInstance"/> never
/// creates one: it fails as BRUTAL's wrapper does, with <c>VK_ERROR_INITIALIZATION_FAILED</c>. Every call is recorded.
/// </summary>
internal sealed class FakeEnumeration : GlobalCommands.ISource
{
    /// <summary>Gets the extension lists: key <c>""</c> is the loader's own list, any other key a layer's.</summary>
    public Dictionary<string, List<string>> Extensions { get; } = new(StringComparer.Ordinal) { [""] = [] };

    public List<string> Layers { get; } = [];

    public int GrowthsLeft { get; set; }

    public int ShrinksLeft { get; set; }

    /// <summary>Gets the calls in order, e.g. <c>"extensions() count"</c>, <c>"layers fill"</c>, <c>"create"</c>.</summary>
    public List<string> Calls { get; } = [];

    public unsafe VkResult EnumerateExtensions(string? layerName, ref int count, Span<VkExtensionProperties> properties)
    {
        string key = layerName ?? string.Empty;
        Calls.Add($"extensions({key}) {(properties.IsEmpty ? "count" : "fill")}");
        if (!Extensions.TryGetValue(key, out List<string>? list))
        {
            return VkResult.ErrorLayerNotPresent;
        }

        if (Counted(list, ref count, properties.IsEmpty))
        {
            return VkResult.Success;
        }

        count = Math.Min(properties.Length, list.Count);
        fixed (VkExtensionProperties* entries = properties)
        {
            for (int i = 0; i < count; i++)
            {
                Write(list[i], entries[i].ExtensionName);
            }
        }

        return count < list.Count ? VkResult.Incomplete : VkResult.Success;
    }

    public unsafe VkResult EnumerateLayers(ref int count, Span<VkLayerProperties> properties)
    {
        Calls.Add($"layers {(properties.IsEmpty ? "count" : "fill")}");
        if (Counted(Layers, ref count, properties.IsEmpty))
        {
            return VkResult.Success;
        }

        count = Math.Min(properties.Length, Layers.Count);
        fixed (VkLayerProperties* entries = properties)
        {
            for (int i = 0; i < count; i++)
            {
                Write(Layers[i], entries[i].LayerName);
            }
        }

        return count < Layers.Count ? VkResult.Incomplete : VkResult.Success;
    }

    public Instance CreateInstance(Instance.CreateInfo createInfo)
    {
        Calls.Add("create");
        throw new VkResultError(VkResult.ErrorInitializationFailed, "Vulkan call to vkCreateInstance failed with error: VK_ERROR_INITIALIZATION_FAILED");
    }

    private bool Counted(List<string> list, ref int count, bool countOnly)
    {
        if (!countOnly)
        {
            return false;
        }

        count = list.Count;
        if (GrowthsLeft > 0)
        {
            GrowthsLeft--;
            list.Add($"VK_FAKE_grown_{list.Count}");
        }
        else if (ShrinksLeft > 0 && list.Count > 0)
        {
            ShrinksLeft--;
            list.RemoveAt(list.Count - 1);
        }

        return true;
    }

    // Sound: the destination is a 256-byte name buffer inside an entry the caller has pinned.
    private static unsafe void Write(string name, byte* destination)
    {
        Span<byte> buffer = new(destination, VK.MAX_EXTENSION_NAME_SIZE);
        buffer[Encoding.UTF8.GetBytes(name, buffer)] = 0;
    }
}

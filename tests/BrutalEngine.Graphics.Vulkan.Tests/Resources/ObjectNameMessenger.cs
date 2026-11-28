using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Brutal;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Instances;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Resources;

/// <summary>
/// A second debug messenger on an instance that records the objects each validation error names (<c>pObjects</c>, with
/// the names the layer tracked from <c>vkSetDebugUtilsObjectNameEXT</c>). The backend's sink logs only the message
/// text, which on the 1.4 layers no longer lists the objects.
/// </summary>
internal sealed unsafe class ObjectNameMessenger : IDisposable
{
    private readonly Instance _instance;
    private readonly VkDebugUtilsMessengerEXT _messenger;
    private GCHandle _self;

    public ObjectNameMessenger(VulkanInstance instance)
    {
        _instance = instance.Brutal;
        _self = GCHandle.Alloc(this);
        VkDebugUtilsMessengerCreateInfoEXT info = new()
        {
            MessageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT,
            MessageType = VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT,
            PfnUserCallback = new PfnVkDebugUtilsMessengerCallbackEXT(&Invoke),
            UserData = (void*)GCHandle.ToIntPtr(_self),
        };
        VkDebugUtilsMessengerEXT messenger = default;
        Assert.Equal(VkResult.Success, _instance.PInvoke.vkCreateDebugUtilsMessengerEXT.Invoke(_instance.Handle, &info, null, &messenger));
        _messenger = messenger;
    }

    /// <summary>Gets each object every error named so far: its type, handle and name (empty when unnamed).</summary>
    public List<(VkObjectType Type, ulong Handle, string Name)> Objects { get; } = [];

    public void Dispose()
    {
        _instance.PInvoke.vkDestroyDebugUtilsMessengerEXT.Invoke(_instance.Handle, _messenger, null);
        _self.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Bool32 Invoke(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT types,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        // Nothing may unwind into the layer: a failure here loses the record, which the test's assertion then reports.
        try
        {
            if (GCHandle.FromIntPtr((nint)userData).Target is ObjectNameMessenger self && data is not null)
            {
                for (int i = 0; i < data->ObjectCount; i++)
                {
                    VkDebugUtilsObjectNameInfoEXT* named = &data->Objects[i];
                    string name = named->ObjectName is null ? string.Empty : Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(named->ObjectName));
                    self.Objects.Add((named->ObjectType, (ulong)named->ObjectHandle, name));
                }
            }
        }
        catch (Exception)
        {
            // The record is lost; the test's assertion on Objects reports it.
        }

        _ = (severity, types);
        return false;
    }
}

using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Instances;
using BrutalEngine.Graphics.Vulkan.Memory;
using BrutalEngine.Graphics.Vulkan.Tests.Devices;
using BrutalEngine.Graphics.Vulkan.Tests.Memory;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Resources;

/// <summary>
/// One validated instance per test class, with a device, gate and allocator on every suitable adapter (on the dev host
/// the RTX and lavapipe): every instance a process creates reloads the NVIDIA driver, which runs out of static TLS after
/// enough of them. Created on first use, so a class's non-GPU rows never need a Vulkan driver. xUnit runs one class's
/// tests one at a time, so they can read the instance's validation counts before and after.
/// </summary>
public sealed class GpuResourceFixture : IDisposable
{
    private readonly List<Gpu> _gpus = [];
    private Logger? _logger;
    private VulkanInstance? _instance;

    /// <summary>Gets the validation sink.</summary>
    internal ValidationLog Sink { get; } = new();

    /// <summary>Gets the instance, creating everything on first use.</summary>
    internal VulkanInstance Instance
    {
        get
        {
            Start();
            return _instance!;
        }
    }

    /// <summary>Gets every suitable adapter's device, creating everything on first use.</summary>
    internal IReadOnlyList<Gpu> Gpus
    {
        get
        {
            Start();
            return _gpus;
        }
    }

    public void Dispose()
    {
        for (int i = _gpus.Count - 1; i >= 0; i--)
        {
            _gpus[i].Vma.Destroy();
            _gpus[i].Core.Dispose();
        }

        _instance?.Dispose();
        _logger?.Dispose();
    }

    private void Start()
    {
        if (_instance is not null)
        {
            return;
        }

        (Logger logger, LogCategory category) = ValidationLog.Create(Sink);
        _logger = logger;
        _instance = GpuDevices.CreateInstance(category, []);
        foreach (GpuDevices.Adapter adapter in GpuDevices.Suitable(_instance))
        {
            VulkanDeviceCore core = GpuAllocators.CreateDevice(_instance, adapter, withoutBudget: false);
            VulkanCallGate gate = new();
            Result<VmaHandle> vma = VmaAllocatorFactory.Create(core, gate);
            if (!vma.IsSuccess)
            {
                core.Dispose();
                Assert.Fail($"{adapter.Candidate.Name}: {vma}");
            }

            _gpus.Add(new Gpu(adapter.Candidate.Name, core, vma.Value, gate));
        }

        Assert.NotEmpty(_gpus);
    }

    /// <summary>A suitable adapter's device, its gate and its allocator.</summary>
    internal readonly record struct Gpu(string Name, VulkanDeviceCore Core, VmaHandle Vma, VulkanCallGate Gate);
}

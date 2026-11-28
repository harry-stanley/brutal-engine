using System;
using System.Linq;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Memory;
using BrutalEngine.Graphics.Vulkan.Resources;
using BrutalEngine.Graphics.Vulkan.Tests.Devices;
using BrutalEngine.Graphics.Vulkan.Tests.Memory;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;
using static BrutalEngine.Graphics.Vulkan.Tests.Resources.TextureFactoryScriptedTests;

namespace BrutalEngine.Graphics.Vulkan.Tests.Resources;

/// <summary>
/// The factory on real devices, every suitable adapter of one shared validated instance (<see cref="GpuResourceFixture"/>),
/// plus the portability row, which needs a hand-built restriction no local driver has. Rows only a script reaches are in
/// <see cref="TextureFactoryScriptedTests"/>.
/// </summary>
public sealed unsafe class TextureFactoryTests(GpuResourceFixture fixture) : IClassFixture<GpuResourceFixture>
{
    // Each usage flag with the format support it needs (graphics.md section 5).
    private static readonly (TextureUsage Usage, FormatSupport Needs)[] _usages =
    [
        (TextureUsage.Sampled, FormatSupport.Sampled),
        (TextureUsage.Storage, FormatSupport.Storage),
        (TextureUsage.ColorTarget, FormatSupport.ColorTarget),
        (TextureUsage.DepthStencilTarget, FormatSupport.DepthStencilTarget),
        (TextureUsage.CopySource, FormatSupport.CopySource),
        (TextureUsage.CopyDestination, FormatSupport.CopyDestination),
    ];

    public static TheoryData<TextureFormat> Formats => EveryFormat();

    // Every usage combination the device's format support allows, as a full mip chain with layers, and, where the
    // format multisamples, as a 4-sample target (4 is in every required sample-count limit): each created, named,
    // backed by a real allocation and destroyed, with no validation message and nothing left in VMA.
    [Theory]
    [Trait("Category", "Gpu")]
    [MemberData(nameof(Formats))]
    public void Create_SupportedCombinations_Succeed(TextureFormat format)
    {
        (long errors, long warnings) = (fixture.Instance.ValidationErrors, fixture.Instance.ValidationWarnings);
        foreach (GpuResourceFixture.Gpu gpu in fixture.Gpus)
        {
            FormatSupport support = gpu.Core.GetFormatSupport(format);
            TextureUsage allowed = _usages.Where(u => (support & u.Needs) != 0).Aggregate(TextureUsage.None, (all, u) => all | u.Usage);
            bool multisample = (support & FormatSupport.Multisample) != 0;
            TextureFactory factory = new(gpu.Core, gpu.Vma, gpu.Gate, default);
            int created = 0;
            for (int usage = 1; usage < 64; usage++)
            {
                if ((usage & ~(int)allowed) != 0)
                {
                    continue;
                }

                TextureDescription chain = new() { Format = format, Width = 64, Height = 48, MipLevels = 7, ArrayLayers = 3, Usage = (TextureUsage)usage, DebugName = $"{format} {usage}" };
                created += CreateAndDestroy(gpu, factory, chain);
                bool target = (usage & (int)(TextureUsage.ColorTarget | TextureUsage.DepthStencilTarget)) != 0;
                if (multisample && target && (usage & (int)TextureUsage.Storage) == 0)
                {
                    created += CreateAndDestroy(gpu, factory, chain with { MipLevels = 1, ArrayLayers = 1, SampleCount = 4 });
                }
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{gpu.Name} {format}: support {support}, {created} textures");
            Assert.True(allowed == TextureUsage.None || created > 0);
            Assert.Equal(0, factory.Table.Count);
            Assert.Equal(0u, AllocationCount(gpu.Vma));
        }

        Assert.Equal((errors, warnings), (fixture.Instance.ValidationErrors, fixture.Instance.ValidationWarnings));
    }

    // For every format, each usage flag the device's format support lacks, alone: refused by the real format query
    // with the driver's FORMAT_NOT_SUPPORTED, and VMA never called.
    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_UnsupportedCombination_IsUnsupportedWithoutVma()
    {
        long errors = fixture.Instance.ValidationErrors;
        foreach (GpuResourceFixture.Gpu gpu in fixture.Gpus)
        {
            RecordingTextures native = new() { Inner = new TextureFactory.NativeTextures(gpu.Core.Device, gpu.Vma) };
            TextureFactory factory = new(native, gpu.Core.Naming, gpu.Gate, gpu.Core.Portability, new ObjectTable<TextureFactory.Entry>(), default);
            int refused = 0;
            foreach (TextureFormat format in Formats.Select(row => row.Data))
            {
                FormatSupport support = gpu.Core.GetFormatSupport(format);
                foreach ((TextureUsage usage, FormatSupport needs) in _usages.Where(u => (support & u.Needs) == 0))
                {
                    Result<ulong> result = factory.Create(new TextureDescription { Format = format, Width = 16, Height = 16, Usage = usage, DebugName = "refused" });

                    Assert.Equal(new EngineError(ErrorKind.Unsupported, "vkGetPhysicalDeviceImageFormatProperties2 failed", (int)VkResult.ErrorFormatNotSupported), result.Error);
                    TestContext.Current.TestOutputHelper?.WriteLine($"{gpu.Name}: {format} lacks {needs}");
                    refused++;
                }
            }

            // Every local driver lacks some combination: no colour format is a depth target on any of them.
            Assert.True(refused > 0, gpu.Name);
            Assert.Equal((refused, 0, 0), (native.QueryCalls, native.CreateCalls, factory.Table.Count));
            Assert.False(gpu.Gate.IsLost);
        }

        Assert.Equal(errors, fixture.Instance.ValidationErrors);
    }

    // Restricted or not, and the sample count and layers asked for: only a restricted multisampled array is refused,
    // before any native call; the rest reach the query and VMA.
    [Theory]
    [InlineData(false, 4, 2, false)]
    [InlineData(false, 2, 6, false)]
    [InlineData(false, 4, 1, true)]
    [InlineData(false, 1, 6, true)]
    [InlineData(true, 4, 2, true)]
    public void Create_MultisampledArrayUnderPortability_IsUnsupported(bool multisampleArrayImage, int samples, int layers, bool created)
    {
        VulkanFaultInjector faults = new();
        faults.FailNth(VulkanCall.GetImageFormatProperties2, 2, VkResult.ErrorUnknown);
        RecordingTextures native = new();
        PortabilityRestrictions portability = PortabilityRestrictions.None with { MultisampleArrayImage = multisampleArrayImage };
        TextureFactory factory = Factory(native, new VulkanCallGate { Faults = faults }, portability: portability);

        Result<ulong> result = factory.Create(Description(TextureUsage.ColorTarget, "msaa array") with { SampleCount = samples, ArrayLayers = layers });

        int calls = created ? 1 : 0;
        Assert.Equal(created, result.IsSuccess);
        Assert.Equal((calls, calls, calls), (native.QueryCalls, native.CreateCalls, factory.Table.Count));
        if (!created)
        {
            Assert.Equal(new EngineError(ErrorKind.Unsupported, "The device's portability subset can't create a multisampled texture with several layers (multisampleArrayImage)"), result.Error);
            Assert.Empty(native.ObjectNames);

            // The refusal took no fault occurrence: the query's second call is still the one that fails.
            Assert.True(factory.Create(Description(TextureUsage.ColorTarget)).IsSuccess);
            Assert.Equal(ErrorKind.Native, factory.Create(Description(TextureUsage.ColorTarget)).Error.Kind);
        }
    }

    // Whether VMA's call or the query fails, its result, the kind and whether the budgets are logged and the gate marked lost: VMA's
    // results as for buffers, and the query's.
    [Theory]
    [Trait("Category", "Gpu")]
    [InlineData(true, VkResult.ErrorOutOfDeviceMemory, ErrorKind.OutOfMemory, true, false)]
    [InlineData(true, VkResult.ErrorOutOfHostMemory, ErrorKind.OutOfMemory, true, false)]
    [InlineData(true, VkResult.ErrorDeviceLost, ErrorKind.Unavailable, false, true)]
    [InlineData(true, VkResult.ErrorInitializationFailed, ErrorKind.Native, false, false)]
    [InlineData(false, VkResult.ErrorOutOfHostMemory, ErrorKind.OutOfMemory, false, false)]
    [InlineData(false, VkResult.ErrorDeviceLost, ErrorKind.Unavailable, false, true)]
    public void Create_InjectedResults_MapKinds(bool create, VkResult code, ErrorKind kind, bool budgets, bool lost)
    {
        ValidationLog lines = new();
        (Logger logger, _) = ValidationLog.Create(lines);
        using (logger)
        {
            LogCategory log = logger.CreateCategory("Graphics.Vulkan");
            foreach (GpuResourceFixture.Gpu gpu in fixture.Gpus)
            {
                // The heaps from the adapter's own query, not from VMA's copy, which the code under test reads.
                PhysicalDevice adapter = gpu.Core.Device.PhysicalDevice;
                VkPhysicalDeviceMemoryProperties memory = default;
                adapter.PInvoke.vkGetPhysicalDeviceMemoryProperties.Invoke(adapter.Handle, &memory);
                VulkanFaultInjector faults = new();
                faults.FailNth(create ? VulkanCall.CreateImage : VulkanCall.GetImageFormatProperties2, 1, code);
                VulkanCallGate gate = new() { Faults = faults };
                TextureFactory factory = new(gpu.Core, gpu.Vma, gate, log);
                int before = lines.Entries.Count;

                Result<ulong> result = factory.Create(new TextureDescription { Format = TextureFormat.Rgba8Unorm, Width = 32, Height = 32, Usage = TextureUsage.Sampled, DebugName = "injected" });

                string message = create ? "vmaCreateImage failed" : "vkGetPhysicalDeviceImageFormatProperties2 failed";
                Assert.Equal(new EngineError(kind, message, (int)code), result.Error);
                Assert.Equal(lost, gate.IsLost);
                var logged = lines.Entries.Skip(before).ToList();
                Assert.Equal(budgets ? memory.MemoryHeapCount : 0, logged.Count);
                Assert.All(logged, line => Assert.StartsWith("Out of GPU memory: heap ", line.Message, StringComparison.Ordinal));
                Assert.All(logged, line => Assert.Equal(LogLevel.Warning, line.Level));
                Assert.Equal(0, factory.Table.Count);
                Assert.Equal(0u, AllocationCount(gpu.Vma));
            }
        }
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Constructor_BadDeviceOrAllocator_Throws()
    {
        GpuResourceFixture.Gpu gpu = fixture.Gpus[0];
        Assert.Throws<ArgumentNullException>("gate", () => new TextureFactory(gpu.Core, gpu.Vma, null!, default));
        Assert.Throws<ArgumentException>("vma", () => new TextureFactory(gpu.Core, default, gpu.Gate, default));

        VulkanDeviceCore core = GpuAllocators.CreateDevice(fixture.Instance, GpuDevices.Suitable(fixture.Instance)[^1], withoutBudget: false);
        core.Dispose();
        Assert.Throws<ObjectDisposedException>(() => new TextureFactory(core, gpu.Vma, gpu.Gate, default));
    }

    // Creates a texture, checks it on the device and in VMA (a real allocation, named), then destroys it.
    private static int CreateAndDestroy(GpuResourceFixture.Gpu gpu, TextureFactory factory, in TextureDescription description)
    {
        Result<ulong> result = factory.Create(description);
        Assert.True(result.IsSuccess, $"{gpu.Name} {description}: {result}");
        Assert.True(factory.Table.TryGet(result.Value, out TextureFactory.Entry entry));
        Assert.Equal(description, entry.Description);
        gpu.Vma.GetAllocationInfo(entry.Allocation, out VmaAllocationInfo info);
        Assert.True(info.Size.Value > 0 && !info.DeviceMemory.IsNull(), $"{gpu.Name} {description}: no memory");
        Assert.Equal(description.DebugName, RecordingBuffers.Read(info.PName.Pointer));
        Assert.True(factory.Destroy(result.Value));
        return 1;
    }

    private static uint AllocationCount(VmaHandle vma)
    {
        vma.CalculateStatistics(out VmaTotalStatistics statistics);
        return statistics.Total.Statistics.AllocationCount;
    }
}

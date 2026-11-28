using System;
using System.Text;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Memory;
using BrutalEngine.Graphics.Vulkan.Resources;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Resources;

/// <summary>
/// The texture factory's rules over a scripted native seam: one query and one creation per texture on every host, so
/// the occurrence-numbered fault rows, the limit rows and the cleanup rows hold everywhere. <see cref="TextureFactoryTests"/>
/// has the real-driver rows.
/// </summary>
public sealed unsafe class TextureFactoryScriptedTests
{
    private const string QueryFailed = "vkGetPhysicalDeviceImageFormatProperties2 failed";
    private const string CreateFailed = "vmaCreateImage failed";

    // A native result, the kind section 6 gives it, and whether the gate is marked lost.
    public static TheoryData<VkResult, ErrorKind, bool> Failures => new()
    {
        { VkResult.ErrorOutOfDeviceMemory, ErrorKind.OutOfMemory, false },
        { VkResult.ErrorOutOfHostMemory, ErrorKind.OutOfMemory, false },
        { VkResult.ErrorDeviceLost, ErrorKind.Unavailable, true },
        { VkResult.ErrorFormatNotSupported, ErrorKind.Unsupported, false },
        { VkResult.ErrorUnknown, ErrorKind.Native, false },
    };

    [Fact]
    public void Create_Description_FillsTheQueryAndTheCreateInfo()
    {
        RecordingTextures native = new();
        TextureFactory factory = Factory(native);

        Result<ulong> result = factory.Create(Description(TextureUsage.Sampled | TextureUsage.CopyDestination) with { Width = 300, Height = 200, MipLevels = 9, ArrayLayers = 6 });

        Assert.True(result.IsSuccess, result.ToString());
        (int query, int create) = (native.QueryCalls, native.CreateCalls);
        Assert.Equal((1, 1), (query, create));

        // Literals from the Vulkan specification: R8G8B8A8_SRGB 43, IMAGE_TYPE_2D 1, TILING_OPTIMAL 0, SAMPLED 0x4 |
        // TRANSFER_DST 0x2, SHARING_MODE_EXCLUSIVE 0, LAYOUT_UNDEFINED 0. sType is the first field of each structure
        // (internal in BRUTAL): IMAGE_CREATE_INFO 14, PHYSICAL_DEVICE_IMAGE_FORMAT_INFO_2 1000059004.
        VkPhysicalDeviceImageFormatInfo2 asked = native.LastQuery;
        Assert.Equal(1000059004, *(int*)&asked);
        Assert.Equal((43, 1, 0, 0x6u, 0u), ((int)asked.Format, (int)asked.Type, (int)asked.Tiling, (uint)asked.Usage, (uint)asked.Flags));
        VkImageCreateInfo info = native.LastInfo;
        Assert.Equal(14, *(int*)&info);
        Assert.Equal((0u, 1, 43), ((uint)info.Flags, (int)info.ImageType, (int)info.Format));
        Assert.Equal((300, 200, 1), (info.Extent.Width, info.Extent.Height, info.Extent.Depth));
        Assert.Equal((9, 6, 1u, 0), (info.MipLevels, info.ArrayLayers, (uint)info.Samples, (int)info.Tiling));
        Assert.Equal((0x6u, 0, 0, 0), ((uint)info.Usage, (int)info.SharingMode, info.QueueFamilyIndexCount, (int)info.InitialLayout));
        Assert.True(info.Next == null);

        // Device-local placement, from MemoryPlacement (whose table MemoryPlacementTests pins).
        VmaAllocationCreateInfo expected = MemoryPlacement.For(BackendMemory.DeviceLocal);
        Assert.Equal((expected.Usage, expected.Flags, expected.RequiredFlags, expected.PreferredFlags), (native.LastPlacement.Usage, native.LastPlacement.Flags, native.LastPlacement.RequiredFlags, native.LastPlacement.PreferredFlags));
        Assert.True(factory.Table.TryGet(result.Value, out TextureFactory.Entry entry));
        Assert.Equal(new TextureFactory.Entry(native.LastCreated.Image, native.LastCreated.Allocation, Description(TextureUsage.Sampled | TextureUsage.CopyDestination) with { Width = 300, Height = 200, MipLevels = 9, ArrayLayers = 6 }), entry);
    }

    // Every format maps through FormatTable (whose literals FormatTableTests pins) into both structures.
    [Theory]
    [MemberData(nameof(EveryFormat))]
    public void Create_EveryFormat_IsAccepted(TextureFormat format)
    {
        RecordingTextures native = new();

        Assert.True(Factory(native).Create(Description(TextureUsage.Sampled) with { Format = format }).IsSuccess);

        Assert.Equal((FormatTable.ToVk(format), FormatTable.ToVk(format)), (native.LastQuery.Format, native.LastInfo.Format));
    }

    // VK_SAMPLE_COUNT_n_BIT is n (Brutal.Vulkan/Generated/Enums/VkSampleCountFlags.gen.cs:34-54).
    [Theory]
    [InlineData(1, 0x1u)]
    [InlineData(2, 0x2u)]
    [InlineData(4, 0x4u)]
    [InlineData(8, 0x8u)]
    [InlineData(16, 0x10u)]
    public void Create_EverySampleCount_IsAccepted(int samples, uint bit)
    {
        RecordingTextures native = new();

        Assert.True(Factory(native).Create(Description(TextureUsage.ColorTarget) with { SampleCount = samples }).IsSuccess);

        Assert.Equal(bit, (uint)native.LastInfo.Samples);
    }

    // One field at its format's limit (accepted) or one past it (refused); the scripted limits are 64x32, 4 mips,
    // 3 layers and 1 or 4 samples.
    [Theory]
    [InlineData(64, 32, 4, 3, 4, true)]
    [InlineData(65, 32, 1, 1, 1, false)]
    [InlineData(64, 33, 1, 1, 1, false)]
    [InlineData(64, 32, 5, 1, 1, false)]
    [InlineData(64, 32, 1, 4, 1, false)]
    [InlineData(64, 32, 1, 1, 2, false)]
    [InlineData(64, 32, 1, 1, 8, false)]
    [InlineData(1, 1, 1, 1, 1, true)]
    public void Create_FormatLimits_RefuseWhatExceedsThem(int width, int height, int mips, int layers, int samples, bool accepted)
    {
        RecordingTextures native = new()
        {
            Limits = new VkImageFormatProperties
            {
                MaxExtent = new VkExtent3D { Width = 64, Height = 32, Depth = 1 },
                MaxMipLevels = 4,
                MaxArrayLayers = 3,
                SampleCounts = VkSampleCountFlags._1Bit | VkSampleCountFlags._4Bit,
            },
        };
        TextureFactory factory = Factory(native);

        Result<ulong> result = factory.Create(new TextureDescription
        {
            Format = TextureFormat.Rgba8Unorm,
            Width = width,
            Height = height,
            MipLevels = mips,
            ArrayLayers = layers,
            SampleCount = samples,
            Usage = TextureUsage.Sampled,
        });

        Assert.Equal(1, native.QueryCalls);
        Assert.Equal(accepted ? 1 : 0, native.CreateCalls);
        Assert.Equal(accepted ? 1 : 0, factory.Table.Count);
        if (!accepted)
        {
            Assert.Equal(new EngineError(ErrorKind.Unsupported, "vkGetPhysicalDeviceImageFormatProperties2: the format can't have this size, mip level count, layer count or sample count on this device"), result.Error);
        }
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void Create_QueryFailure_ReturnsTheGatesErrorWithoutVma(VkResult code, ErrorKind kind, bool lost)
    {
        RecordingTextures native = new() { QueryResult = code };
        VulkanCallGate gate = new();
        TextureFactory factory = Factory(native, gate, log: default);

        Result<ulong> result = factory.Create(Description(TextureUsage.Storage, "refused"));

        Assert.Equal(new EngineError(kind, QueryFailed, (int)code), result.Error);
        Assert.Equal(lost, gate.IsLost);
        Assert.Equal((1, 0, 0), (native.QueryCalls, native.CreateCalls, factory.Table.Count));
        Assert.Empty(native.BudgetLogs);
        Assert.Empty(native.Destroyed);
        Assert.Empty(native.ObjectNames);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void Create_VmaFailure_ReturnsTheGatesError(VkResult code, ErrorKind kind, bool lost)
    {
        RecordingTextures native = new() { CreateResult = code };
        VulkanCallGate gate = new();
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        LogCategory log = logger.CreateCategory("Graphics.Vulkan");
        TextureFactory factory = Factory(native, gate, log: log);

        Result<ulong> result = factory.Create(Description(TextureUsage.Sampled, "failed"));

        Assert.Equal(new EngineError(kind, CreateFailed, (int)code), result.Error);
        Assert.Equal(lost, gate.IsLost);

        // The budgets are logged for out of memory only, to the factory's category.
        Assert.Equal(kind == ErrorKind.OutOfMemory ? [log] : [], native.BudgetLogs);
        Assert.Equal((1, 1, 0), (native.QueryCalls, native.CreateCalls, factory.Table.Count));
        Assert.Empty(native.Destroyed);
        Assert.Empty(native.ObjectNames);
        Assert.Empty(native.AllocationNames);
    }

    // Occurrence 1 makes no call of its own; occurrence 2 fails the second texture's call after the first succeeded.
    [Theory]
    [InlineData(false, 1, 0)]
    [InlineData(false, 2, 1)]
    [InlineData(true, 1, 1)]
    [InlineData(true, 2, 2)]
    public void Create_InjectedFault_SkipsTheCall(bool create, int occurrence, int queries)
    {
        VulkanFaultInjector faults = new();
        faults.FailNth(create ? VulkanCall.CreateImage : VulkanCall.GetImageFormatProperties2, occurrence, VkResult.ErrorOutOfDeviceMemory);
        RecordingTextures native = new();
        TextureFactory factory = Factory(native, new VulkanCallGate { Faults = faults });
        for (int i = 1; i < occurrence; i++)
        {
            Assert.True(factory.Create(Description(TextureUsage.Sampled)).IsSuccess);
        }

        Result<ulong> result = factory.Create(Description(TextureUsage.Sampled, "injected"));

        Assert.Equal(new EngineError(ErrorKind.OutOfMemory, create ? CreateFailed : QueryFailed, (int)VkResult.ErrorOutOfDeviceMemory), result.Error);
        Assert.Equal((queries, occurrence - 1, occurrence - 1), (native.QueryCalls, native.CreateCalls, factory.Table.Count));
        Assert.Equal(create ? 1 : 0, native.BudgetLogs.Count);
        Assert.Empty(native.ObjectNames);

        // The fault fired once: the next creation reaches VMA.
        Assert.True(factory.Create(Description(TextureUsage.Sampled)).IsSuccess);
        Assert.Equal(occurrence, native.CreateCalls);
    }

    // The format below, at and above the defined range, a usage bit past the last flag and the sign bit, and each
    // count at zero, negative or not a power of two up to 16.
    [Theory]
    [InlineData(-1, 1, 1, 1, 1, 1, 1, "format")]
    [InlineData(0, 1, 1, 1, 1, 1, 1, "format")]
    [InlineData(17, 1, 1, 1, 1, 1, 1, "format")]
    [InlineData(3, 1 << 6, 1, 1, 1, 1, 1, "usage")]
    [InlineData(3, int.MinValue, 1, 1, 1, 1, 1, "usage")]
    [InlineData(3, 1, 0, 1, 1, 1, 1, "description.Width")]
    [InlineData(3, 1, -1, 1, 1, 1, 1, "description.Width")]
    [InlineData(3, 1, 1, 0, 1, 1, 1, "description.Height")]
    [InlineData(3, 1, 1, -1, 1, 1, 1, "description.Height")]
    [InlineData(3, 1, 1, 1, 0, 1, 1, "description.MipLevels")]
    [InlineData(3, 1, 1, 1, -1, 1, 1, "description.MipLevels")]
    [InlineData(3, 1, 1, 1, 1, 0, 1, "description.ArrayLayers")]
    [InlineData(3, 1, 1, 1, 1, -1, 1, "description.ArrayLayers")]
    [InlineData(3, 1, 1, 1, 1, 1, 0, "description.SampleCount")]
    [InlineData(3, 1, 1, 1, 1, 1, -4, "description.SampleCount")]
    [InlineData(3, 1, 1, 1, 1, 1, 3, "description.SampleCount")]
    [InlineData(3, 1, 1, 1, 1, 1, 32, "description.SampleCount")]
    public void Create_InvalidArguments_ThrowBeforeAnyCall(int format, int usage, int width, int height, int mips, int layers, int samples, string parameter)
    {
        VulkanFaultInjector faults = new();
        faults.FailNth(VulkanCall.GetImageFormatProperties2, 1, VkResult.ErrorOutOfHostMemory);
        RecordingTextures native = new();
        TextureFactory factory = Factory(native, new VulkanCallGate { Faults = faults }, portability: PortabilityRestrictions.None with { MultisampleArrayImage = false });
        TextureDescription description = new()
        {
            Format = (TextureFormat)format,
            Usage = (TextureUsage)usage,
            Width = width,
            Height = height,
            MipLevels = mips,
            ArrayLayers = layers,
            SampleCount = samples,
        };

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => factory.Create(description));

        Assert.Equal(parameter, error.ParamName);
        Assert.Equal((0, 0, 0), (native.QueryCalls, native.CreateCalls, factory.Table.Count));

        // The check ran before the gate, so the fault is still pending for the next valid creation.
        Assert.Equal(new EngineError(ErrorKind.OutOfMemory, QueryFailed, (int)VkResult.ErrorOutOfHostMemory), factory.Create(Description(TextureUsage.Sampled)).Error);
    }

    [Fact]
    public void Create_TableExhausted_DestroysTheNewImageAndThrows()
    {
        RecordingTextures native = new();
        TextureFactory factory = Factory(native, table: new ObjectTable<TextureFactory.Entry>(maxSlots: 1));
        Assert.True(factory.Create(Description(TextureUsage.Sampled)).IsSuccess);

        Assert.Throws<InvalidOperationException>(() => factory.Create(Description(TextureUsage.Sampled, "second")));

        Assert.Equal((2, 2), (native.QueryCalls, native.CreateCalls));
        Assert.Equal([native.LastCreated], native.Destroyed);
        Assert.Equal(1, factory.Table.Count);
        Assert.Empty(native.ObjectNames);
        Assert.Empty(native.AllocationNames);
    }

    [Fact]
    public void Create_Name_AppliedOnceToTheImageAndTheAllocation()
    {
        RecordingTextures native = new();
        TextureFactory factory = Factory(native);

        ulong id = factory.Create(Description(TextureUsage.Sampled, "sprite atlas \u00E9")).Value;

        (VkImage image, VmaAllocation allocation) = native.LastCreated;
        Assert.Equal([(VkObjectType.Image, (ulong)image.VkHandle, "sprite atlas \u00E9")], native.ObjectNames);
        Assert.Equal([(allocation, "sprite atlas \u00E9")], native.AllocationNames);
        Assert.True(factory.Destroy(id));
        Assert.Single(native.ObjectNames);
        Assert.Single(native.AllocationNames);
    }

    [Theory]
    [InlineData(null, false, 0)]
    [InlineData("", true, 0)]
    [InlineData("vma only", false, 1)]
    public void Create_NoNameOrNoNaming_NamesOnlyWhatItCan(string? name, bool naming, int allocationNames)
    {
        RecordingTextures native = new();

        Assert.True(Factory(native, naming: naming).Create(Description(TextureUsage.Sampled, name)).IsSuccess);

        Assert.Empty(native.ObjectNames);
        Assert.Equal(allocationNames, native.AllocationNames.Count);
    }

    // ASCII bytes before one character, then the bytes kept of a name that continues with "zz".
    [Theory]
    [InlineData(254, "\u20AC", 254)] // a 3-byte character straddling byte 255: dropped
    [InlineData(252, "\u20AC", 255)] // a 3-byte character ending at byte 254: kept, exactly the limit
    public void Create_LongName_IsCutAtACharacterBoundary(int prefix, string character, int expected)
    {
        string name = new string('a', prefix) + character + "zz";
        string kept = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(name), 0, expected);
        RecordingTextures native = new();

        Assert.True(Factory(native).Create(Description(TextureUsage.Sampled, name)).IsSuccess);

        Assert.Equal(kept, Assert.Single(native.ObjectNames).Name);
        Assert.Equal(kept, Assert.Single(native.AllocationNames).Name);
    }

    [Fact]
    public void Destroy_LiveId_DestroysTheImageAndAllocationOnce()
    {
        RecordingTextures native = new();
        TextureFactory factory = Factory(native);
        ulong first = factory.Create(Description(TextureUsage.Sampled)).Value;
        (VkImage, VmaAllocation) created = native.LastCreated;
        ulong second = factory.Create(Description(TextureUsage.Sampled)).Value;

        Assert.True(factory.Destroy(first));
        Assert.False(factory.Destroy(first));

        Assert.Equal([created], native.Destroyed);
        Assert.False(factory.Table.TryGet(first, out _));
        Assert.True(factory.Table.TryGet(second, out _));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(5UL)]
    [InlineData((1UL << 32) | 9)]
    public void Destroy_NeverIssuedId_DestroysNothing(ulong id)
    {
        RecordingTextures native = new();
        TextureFactory factory = Factory(native);
        _ = factory.Create(Description(TextureUsage.Sampled));

        Assert.False(factory.Destroy(id));

        Assert.Empty(native.Destroyed);
        Assert.Equal(1, factory.Table.Count);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        RecordingTextures native = new();
        VulkanCallGate gate = new();
        DebugNames.Naming naming = new(true, gate, default);
        ObjectTable<TextureFactory.Entry> table = new();
        PortabilityRestrictions none = PortabilityRestrictions.None;

        Assert.Throws<ArgumentNullException>("native", () => new TextureFactory(null!, naming, gate, none, table, default));
        Assert.Throws<ArgumentNullException>("naming", () => new TextureFactory(native, null!, gate, none, table, default));
        Assert.Throws<ArgumentNullException>("gate", () => new TextureFactory(native, naming, null!, none, table, default));
        Assert.Throws<ArgumentNullException>("table", () => new TextureFactory(native, naming, gate, none, null!, default));
        Assert.Throws<ArgumentNullException>("core", () => new TextureFactory(null!, default, gate, default));
    }

    public static TheoryData<TextureFormat> EveryFormat()
    {
        TheoryData<TextureFormat> data = [];
        foreach (TextureFormat format in Enum.GetValues<TextureFormat>())
        {
            if (format != TextureFormat.Undefined)
            {
                data.Add(format);
            }
        }

        return data;
    }

    internal static TextureDescription Description(TextureUsage usage, string? name = null) =>
        new() { Format = TextureFormat.Rgba8UnormSrgb, Width = 64, Height = 64, Usage = usage, DebugName = name };

    internal static TextureFactory Factory(RecordingTextures native, VulkanCallGate? gate = null, ObjectTable<TextureFactory.Entry>? table = null, bool naming = true, PortabilityRestrictions? portability = null, LogCategory log = default)
    {
        gate ??= new VulkanCallGate();
        return new TextureFactory(native, new DebugNames.Naming(naming, gate, default), gate, portability ?? PortabilityRestrictions.None, table ?? new ObjectTable<TextureFactory.Entry>(), log);
    }
}

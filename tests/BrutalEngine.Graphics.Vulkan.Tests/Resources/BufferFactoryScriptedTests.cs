using System;
using System.Text;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Memory;
using BrutalEngine.Graphics.Vulkan.Resources;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Resources;

/// <summary>
/// The factory's rules over a scripted native seam: one call per creation on every host, whatever adapters it has, so
/// the occurrence-numbered fault rows and the mapping rule hold everywhere. <see cref="BufferFactoryTests"/> has the
/// real-driver rows.
/// </summary>
public sealed unsafe class BufferFactoryScriptedTests
{
    // VMA's result, the kind section 6 gives it, and whether the gate is marked lost.
    public static TheoryData<VkResult, ErrorKind, bool> Failures => new()
    {
        { VkResult.ErrorOutOfDeviceMemory, ErrorKind.OutOfMemory, false },
        { VkResult.ErrorOutOfHostMemory, ErrorKind.OutOfMemory, false },
        { VkResult.ErrorDeviceLost, ErrorKind.Unavailable, true },
        { VkResult.ErrorInitializationFailed, ErrorKind.Native, false },
        { VkResult.ErrorUnknown, ErrorKind.Native, false },
    };

    // Each memory kind and whether the contract maps it (graphics.md section 6: host kinds only).
    public static TheoryData<BackendMemory, bool> MappedRule => new()
    {
        { BackendMemory.DeviceLocal, false },
        { BackendMemory.DeviceLocalHostVisiblePreferred, false },
        { BackendMemory.HostUpload, true },
        { BackendMemory.HostReadback, true },
    };

    [Theory]
    [MemberData(nameof(MappedRule))]
    public void Create_EachMemory_ReturnsTheMappingOnlyForHostMemory(BackendMemory memory, bool mapped)
    {
        // The script reports a mapping for every kind, so a device kind returning it can't hide behind VMA's null.
        RecordingBuffers native = new() { MappedPointer = 0x7340 };
        BufferFactory factory = Factory(native);

        Result<BackendBuffer> result = factory.Create(Description(256, BufferUsage.CopyDestination), memory);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(mapped ? 0x7340 : 0, result.Value.MappedPointer);
        Assert.NotEqual(0UL, result.Value.Id);
        Assert.True(factory.Table.TryGet(result.Value.Id, out BufferFactory.Entry entry));
        Assert.Equal(new BufferFactory.Entry(native.LastCreated.Buffer, native.LastCreated.Allocation, memory, null), entry);
    }

    [Theory]
    [InlineData(BackendMemory.DeviceLocal)]
    [InlineData(BackendMemory.DeviceLocalHostVisiblePreferred)]
    [InlineData(BackendMemory.HostUpload)]
    [InlineData(BackendMemory.HostReadback)]
    public void Create_Description_FillsTheCreateInfoAndPlacement(BackendMemory memory)
    {
        RecordingBuffers native = new();
        BufferFactory factory = Factory(native);

        Result<BackendBuffer> result = factory.Create(Description(0x1_0000_0010, BufferUsage.Vertex | BufferUsage.CopyDestination), memory);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(1, native.CreateCalls);
        VkBufferCreateInfo info = native.LastInfo;
        // sType is internal in BRUTAL (set by the constructor); it is the first field (VkBufferCreateInfo.gen.cs:33-34).
        Assert.Equal(VkStructureType.BufferCreateInfo, *(VkStructureType*)&info);
        Assert.Equal(0x1_0000_0010UL, info.Size.Value);

        // VERTEX_BUFFER 0x80 | TRANSFER_DST 0x2 (Brutal.Vulkan/Generated/Enums/VkBufferUsageFlags.gen.cs:86, :116).
        Assert.Equal(0x82u, (uint)info.Usage);
        Assert.Equal(VkSharingMode.Exclusive, info.SharingMode);
        Assert.Equal(0u, (uint)info.Flags);
        Assert.Equal(0, info.QueueFamilyIndexCount);

        // The placement is the memory kind's, from MemoryPlacement (whose table MemoryPlacementTests pins).
        VmaAllocationCreateInfo expected = MemoryPlacement.For(memory);
        VmaAllocationCreateInfo placement = native.LastPlacement;
        Assert.Equal(
            (expected.Usage, expected.Flags, expected.RequiredFlags, expected.PreferredFlags),
            (placement.Usage, placement.Flags, placement.RequiredFlags, placement.PreferredFlags));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void Create_NativeFailure_ReturnsTheGatesError(VkResult code, ErrorKind kind, bool lost)
    {
        RecordingBuffers native = new() { Result = code };
        VulkanCallGate gate = new();
        BufferFactory factory = Factory(native, gate);

        Result<BackendBuffer> result = factory.Create(Description(64, BufferUsage.Uniform, "failed"), BackendMemory.HostUpload);

        Assert.Equal(new EngineError(kind, "vmaCreateBuffer failed", (int)code), result.Error);
        Assert.Equal(lost, gate.IsLost);
        Assert.Equal(1, native.CreateCalls);
        Assert.Equal(0, factory.Table.Count);
        Assert.Empty(native.Destroyed);
        Assert.Empty(native.ObjectNames);
        Assert.Empty(native.AllocationNames);
    }

    // Occurrence 1 makes no native call; occurrence 2 exactly one, the first creation's.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Create_InjectedFault_SkipsVma(int occurrence)
    {
        VulkanFaultInjector faults = new();
        faults.FailNth(VulkanCall.CreateBuffer, occurrence, VkResult.ErrorOutOfDeviceMemory);
        RecordingBuffers native = new();
        BufferFactory factory = Factory(native, new VulkanCallGate { Faults = faults });
        for (int i = 1; i < occurrence; i++)
        {
            Assert.True(factory.Create(Description(64, BufferUsage.Uniform), BackendMemory.DeviceLocal).IsSuccess);
        }

        Result<BackendBuffer> result = factory.Create(Description(64, BufferUsage.Uniform, "injected"), BackendMemory.DeviceLocal);

        Assert.Equal(new EngineError(ErrorKind.OutOfMemory, "vmaCreateBuffer failed", (int)VkResult.ErrorOutOfDeviceMemory), result.Error);
        Assert.Equal(occurrence - 1, native.CreateCalls);
        Assert.Equal(occurrence - 1, factory.Table.Count);
        Assert.Empty(native.ObjectNames);

        // The fault fired once: the next creation reaches VMA.
        Assert.True(factory.Create(Description(64, BufferUsage.Uniform), BackendMemory.DeviceLocal).IsSuccess);
        Assert.Equal(occurrence, native.CreateCalls);
    }

    [Theory]
    [InlineData(0L, BufferUsage.Vertex, BackendMemory.DeviceLocal, "Size")]
    [InlineData(-1L, BufferUsage.Vertex, BackendMemory.DeviceLocal, "Size")]
    [InlineData(64L, (BufferUsage)(1 << 7), BackendMemory.DeviceLocal, "usage")]
    [InlineData(64L, BufferUsage.Vertex, (BackendMemory)4, "memory")]
    [InlineData(64L, BufferUsage.Vertex, (BackendMemory)(-1), "memory")]
    public void Create_InvalidArguments_ThrowBeforeTheCall(long size, BufferUsage usage, BackendMemory memory, string parameter)
    {
        VulkanFaultInjector faults = new();
        faults.FailNth(VulkanCall.CreateBuffer, 1, VkResult.ErrorOutOfDeviceMemory);
        RecordingBuffers native = new();
        BufferFactory factory = Factory(native, new VulkanCallGate { Faults = faults });

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => factory.Create(Description(size, usage, "bad"), memory));

        Assert.Contains(parameter, error.ParamName, StringComparison.Ordinal);
        Assert.Equal(0, native.CreateCalls);
        Assert.Equal(0, factory.Table.Count);

        // The check ran before the gate, so the fault is still pending for the next valid creation.
        Assert.Equal(ErrorKind.OutOfMemory, factory.Create(Description(64, BufferUsage.Vertex), BackendMemory.DeviceLocal).Error.Kind);
    }

    [Fact]
    public void Create_TableExhausted_DestroysTheNewBufferAndThrows()
    {
        RecordingBuffers native = new();
        BufferFactory factory = Factory(native, table: new ObjectTable<BufferFactory.Entry>(maxSlots: 1));
        Assert.True(factory.Create(Description(64, BufferUsage.Index), BackendMemory.DeviceLocal).IsSuccess);

        Assert.Throws<InvalidOperationException>(() => factory.Create(Description(64, BufferUsage.Index, "second"), BackendMemory.HostUpload));

        Assert.Equal(2, native.CreateCalls);
        Assert.Equal([native.LastCreated], native.Destroyed);
        Assert.Equal(1, factory.Table.Count);
        Assert.Empty(native.ObjectNames);
        Assert.Empty(native.AllocationNames);
    }

    [Fact]
    public void Create_Name_AppliedOnceToTheBufferAndTheAllocation()
    {
        RecordingBuffers native = new();
        BufferFactory factory = Factory(native);

        Result<BackendBuffer> result = factory.Create(Description(64, BufferUsage.Storage, "sprite atlas \u00E9"), BackendMemory.DeviceLocal);

        Assert.True(result.IsSuccess, result.ToString());
        (VkBuffer buffer, VmaAllocation allocation) = native.LastCreated;
        Assert.Equal([(VkObjectType.Buffer, (ulong)buffer.VkHandle, "sprite atlas \u00E9")], native.ObjectNames);
        Assert.Equal([(allocation, "sprite atlas \u00E9")], native.AllocationNames);
        Assert.True(factory.Table.TryGet(result.Value.Id, out BufferFactory.Entry entry));
        Assert.Equal("sprite atlas \u00E9", entry.Name);

        Assert.True(factory.Destroy(result.Value.Id));
        Assert.Single(native.ObjectNames);
        Assert.Single(native.AllocationNames);
    }

    [Fact]
    public void Create_NamingDisabled_NamesOnlyTheAllocation()
    {
        RecordingBuffers native = new();
        BufferFactory factory = Factory(native, naming: false);

        Assert.True(factory.Create(Description(64, BufferUsage.Storage, "vma only"), BackendMemory.DeviceLocal).IsSuccess);

        Assert.Empty(native.ObjectNames);
        Assert.Equal([(native.LastCreated.Allocation, "vma only")], native.AllocationNames);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Create_NoName_NamesNothing(string? name)
    {
        RecordingBuffers native = new();
        BufferFactory factory = Factory(native);

        Assert.True(factory.Create(Description(64, BufferUsage.Storage, name), BackendMemory.DeviceLocal).IsSuccess);

        Assert.Empty(native.ObjectNames);
        Assert.Empty(native.AllocationNames);
    }

    // ASCII bytes before one character, then the bytes kept of a name that continues with "zz": at most 255 bytes, cut
    // before a character that would straddle byte 255.
    [Theory]
    [InlineData(254, "\u20AC", 254)]        // 3-byte character at 254-256: dropped
    [InlineData(252, "\u20AC", 255)]        // 3-byte character at 252-254: fits, "z" doesn't
    [InlineData(253, "\U0001F600", 253)]    // 4-byte character (a surrogate pair) at 253-256: dropped
    [InlineData(255, "z", 255)]             // ASCII past the end
    [InlineData(10, "\u00E9", 14)]          // short: everything
    public void Create_LongName_IsCutAtACharacterBoundary(int prefix, string character, int expected)
    {
        string name = new string('a', prefix) + character + "zz";
        string kept = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(name), 0, expected);
        RecordingBuffers native = new();
        BufferFactory factory = Factory(native);

        Assert.True(factory.Create(Description(64, BufferUsage.Storage, name), BackendMemory.DeviceLocal).IsSuccess);

        Assert.Equal(kept, Assert.Single(native.ObjectNames).Name);
        Assert.Equal(kept, Assert.Single(native.AllocationNames).Name);
        Assert.Equal(expected, Encoding.UTF8.GetByteCount(kept));
    }

    [Fact]
    public void Destroy_LiveId_DestroysTheBufferAndAllocationTogether()
    {
        RecordingBuffers native = new();
        BufferFactory factory = Factory(native);
        ulong first = factory.Create(Description(64, BufferUsage.Vertex), BackendMemory.DeviceLocal).Value.Id;
        (VkBuffer, VmaAllocation) created = native.LastCreated;
        ulong second = factory.Create(Description(64, BufferUsage.Vertex), BackendMemory.HostUpload).Value.Id;

        Assert.True(factory.Destroy(first));

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
        RecordingBuffers native = new();
        BufferFactory factory = Factory(native);
        _ = factory.Create(Description(64, BufferUsage.Vertex), BackendMemory.DeviceLocal);

        Assert.False(factory.Destroy(id));

        Assert.Empty(native.Destroyed);
        Assert.Equal(1, factory.Table.Count);
    }

    [Fact]
    public void Destroy_Twice_DestroysOnce()
    {
        RecordingBuffers native = new();
        BufferFactory factory = Factory(native);
        ulong id = factory.Create(Description(64, BufferUsage.Vertex), BackendMemory.DeviceLocal).Value.Id;

        Assert.True(factory.Destroy(id));
        Assert.False(factory.Destroy(id));

        Assert.Single(native.Destroyed);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        RecordingBuffers native = new();
        VulkanCallGate gate = new();
        DebugNames.Naming naming = new(true, gate, default);
        ObjectTable<BufferFactory.Entry> table = new();

        Assert.Throws<ArgumentNullException>("native", () => new BufferFactory(null!, naming, gate, table));
        Assert.Throws<ArgumentNullException>("naming", () => new BufferFactory(native, null!, gate, table));
        Assert.Throws<ArgumentNullException>("gate", () => new BufferFactory(native, naming, null!, table));
        Assert.Throws<ArgumentNullException>("table", () => new BufferFactory(native, naming, gate, null!));
        Assert.Throws<ArgumentNullException>("core", () => new BufferFactory(null!, default, gate));
    }

    private static BufferDescription Description(long size, BufferUsage usage, string? name = null) =>
        new() { Size = size, Usage = usage, DebugName = name };

    private static BufferFactory Factory(RecordingBuffers native, VulkanCallGate? gate = null, ObjectTable<BufferFactory.Entry>? table = null, bool naming = true)
    {
        gate ??= new VulkanCallGate();
        return new BufferFactory(native, new DebugNames.Naming(naming, gate, default), gate, table ?? new ObjectTable<BufferFactory.Entry>());
    }
}

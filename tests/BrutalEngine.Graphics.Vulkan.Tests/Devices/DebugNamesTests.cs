using System;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using Brutal.VulkanApi;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Tests.Validation;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Devices;

public sealed unsafe class DebugNamesTests
{
    private const VulkanCall Call = VulkanCall.SetDebugUtilsObjectName;

    // ASCII bytes before one character, then the bytes kept of a name that continues with "zz". The buffer keeps at
    // most 255 bytes, so the character either fits or straddles byte 255 and is dropped whole.
    public static TheoryData<int, string, int> Cuts => new()
    {
        { 254, "\u20AC", 254 },          // 3-byte character at 254-256
        { 253, "\u20AC", 253 },          // 3-byte character at 253-255
        { 252, "\u20AC", 255 },          // 3-byte character at 252-254: fits
        { 254, "\u00E9", 254 },          // 2-byte character at 254-255
        { 253, "\u00E9", 255 },          // 2-byte character at 253-254: fits
        { 253, "\U0001F600", 253 },      // 4-byte character at 253-256
        { 252, "\U0001F600", 252 },      // 4-byte character at 252-255
        { 251, "\U0001F600", 255 },      // 4-byte character at 251-254: fits
        { 255, "z", 255 },               // ASCII past the end
    };

    [Theory]
    [MemberData(nameof(Cuts))]
    public void Copy_LongMultibyteName_CutsAtBoundary(int prefix, string character, int expected)
    {
        byte[] name = Encoding.UTF8.GetBytes(new string('a', prefix) + character + "zz");
        Span<byte> buffer = stackalloc byte[DebugNames.BufferSize];
        buffer.Fill(0xFF);

        int length = DebugNames.Copy(name, buffer);

        Assert.Equal(expected, length);
        Assert.Equal(0, buffer[length]);
        Assert.True(buffer[..length].SequenceEqual(name.AsSpan(0, length)));
        Assert.True(Utf8.IsValid(buffer[..length]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(254)]
    [InlineData(255)]
    public void Copy_NameThatFits_CopiesAllOfIt(int size)
    {
        byte[] name = Encoding.UTF8.GetBytes(new string('n', size));
        Span<byte> buffer = stackalloc byte[DebugNames.BufferSize];
        buffer.Fill(0xFF);

        Assert.Equal(size, DebugNames.Copy(name, buffer));
        Assert.Equal(0, buffer[size]);
        Assert.True(buffer[..size].SequenceEqual(name));
    }

    [Fact]
    public void Copy_SmallBuffer_KeepsRoomForTheNul()
    {
        Span<byte> buffer = stackalloc byte[4];

        Assert.Equal(3, DebugNames.Copy("abcdef"u8, buffer));
        Assert.True(buffer.SequenceEqual("abc\0"u8));
        Assert.Throws<ArgumentException>("buffer", () => DebugNames.Copy("a"u8, []));
    }

    [Fact]
    public void Set_PassesTheTypeHandleAndNulTerminatedName()
    {
        FakeNamer namer = new();
        VulkanCallGate gate = new();

        DebugNames.Set(namer, new DebugNames.Naming(true, gate, default), VkObjectType.Buffer, 0x1234_5678_9ABC_DEF0, "sprite atlas"u8);

        Assert.Equal(1, namer.Calls);
        Assert.Equal(VkStructureType.DebugUtilsObjectNameInfoEXT, namer.Type);
        Assert.Equal((VkObjectType.Buffer, 0x1234_5678_9ABC_DEF0UL, "sprite atlas"), (namer.ObjectType, namer.Handle, namer.Name));
        Assert.False(gate.IsLost);
    }

    [Fact]
    public void Set_ExtensionOff_DoesNothing()
    {
        FakeNamer namer = new();
        VulkanFaultInjector faults = new();
        faults.FailNth(Call, 1, VkResult.ErrorDeviceLost);
        VulkanCallGate gate = new() { Faults = faults };

        DebugNames.Set(namer, new DebugNames.Naming(false, gate, default), VkObjectType.Image, 7, "unnamed"u8);

        // Neither the native call nor the gate ran: the armed fault is still waiting.
        Assert.Equal(0, namer.Calls);
        Assert.False(gate.IsLost);
        Assert.True(faults.TryTake(Call, out _));
    }

    [Fact]
    public void Set_Fails_LogsTheFirstFailureOnceAtDebug()
    {
        ValidationLog sink = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(sink);
        using (logger)
        {
            FakeNamer namer = new() { Result = VkResult.ErrorOutOfHostMemory };
            DebugNames.Naming naming = new(true, new VulkanCallGate(), category);

            DebugNames.Set(namer, naming, VkObjectType.Image, 7, "first"u8);
            DebugNames.Set(namer, naming, VkObjectType.Image, 7, "second"u8);

            Assert.Equal(2, namer.Calls);
            Assert.True(naming.FailureLogged);
            Assert.False(naming.Gate.IsLost);
        }

        (LogLevel level, string message) = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Debug, level);
        Assert.Contains("vkSetDebugUtilsObjectNameEXT failed", message, StringComparison.Ordinal);
        Assert.Contains("-1", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public void Set_InjectedFault_SkipsTheCallAndIsSwallowed(int occurrence, int nativeCalls)
    {
        // A scripted single namer, so the occurrence is this call's own: 1 fails the first call, 2 the second.
        FakeNamer namer = new();
        VulkanFaultInjector faults = new();
        faults.FailNth(Call, occurrence, VkResult.ErrorDeviceLost);
        DebugNames.Naming naming = new(true, new VulkanCallGate { Faults = faults }, default);

        for (int i = 0; i < occurrence; i++)
        {
            DebugNames.Set(namer, naming, VkObjectType.Image, 7, "name"u8);
        }

        Assert.Equal(nativeCalls, namer.Calls);
        Assert.True(naming.Gate.IsLost);
        Assert.True(naming.FailureLogged);
    }

    [Fact]
    public void Set_DeviceLost_MarksTheGateLost()
    {
        FakeNamer namer = new() { Result = VkResult.ErrorDeviceLost };
        DebugNames.Naming naming = new(true, new VulkanCallGate(), default);

        DebugNames.Set(namer, naming, VkObjectType.Image, 7, "name"u8);

        Assert.True(naming.Gate.IsLost);
    }

    [Fact]
    public void Set_DoesNotAllocate()
    {
        ValidationLog sink = new() { Record = false };
        (Logger logger, LogCategory category) = ValidationLog.Create(sink);
        using (logger)
        {
            byte[] longName = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("n\u20AC", 100)));
            CountingNamer succeeds = new(VkResult.Success);
            CountingNamer fails = new(VkResult.ErrorOutOfHostMemory);
            DebugNames.Naming on = new(true, new VulkanCallGate(), category);
            DebugNames.Naming off = new(false, new VulkanCallGate(), category);

            // The first failure logs; the window measures success, later failures, the extension off and both name lengths.
            DebugNames.Set(succeeds, on, VkObjectType.Image, 1, longName);
            DebugNames.Set(fails, on, VkObjectType.Image, 1, longName);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                DebugNames.Set(succeeds, on, VkObjectType.Image, (ulong)i, longName);
                DebugNames.Set(succeeds, on, VkObjectType.Buffer, (ulong)i, "short"u8);
                DebugNames.Set(fails, on, VkObjectType.Image, (ulong)i, longName);
                DebugNames.Set(succeeds, off, VkObjectType.Image, (ulong)i, longName);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(1, sink.Count);
        }
    }

    /// <summary>Records the one call it gets, reading the name while its pointer is valid.</summary>
    private sealed class FakeNamer : DebugNames.INamer
    {
        public VkResult Result { get; init; } = VkResult.Success;

        public int Calls { get; private set; }

        public VkStructureType Type { get; private set; }

        public VkObjectType ObjectType { get; private set; }

        public ulong Handle { get; private set; }

        public string? Name { get; private set; }

        public VkResult SetName(VkDebugUtilsObjectNameInfoEXT* info)
        {
            Calls++;
            Type = *(VkStructureType*)info;
            Assert.True(info->Next is null);
            ObjectType = info->ObjectType;
            Handle = (ulong)info->ObjectHandle;
            ReadOnlySpan<byte> name = new(info->ObjectName, DebugNames.BufferSize);
            Name = Encoding.UTF8.GetString(name[..name.IndexOf((byte)0)]);
            return Result;
        }
    }

    /// <summary>Returns a fixed result and allocates nothing.</summary>
    private readonly struct CountingNamer(VkResult result) : DebugNames.INamer
    {
        public VkResult SetName(VkDebugUtilsObjectNameInfoEXT* info) => info->ObjectName[0] != 0 ? result : VkResult.ErrorUnknown;
    }
}

using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using CsCheck;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Backend;

/// <summary>
/// The front end's handle-to-backend-id table (docs/design/graphics.md, "The backend interface"): one generation-checked
/// table per kind, interned UTF-8 labels, allocation-free lookups and growth that fails without changing anything. The
/// project turns background garbage collection off, which AllocationMeasurementTests guards, so the allocation count is
/// exact.
/// </summary>
public sealed class BackendObjectTableTests
{
    public static TheoryData<BackendObjectKind> Kinds() =>
    [
        BackendObjectKind.Buffer, BackendObjectKind.Texture, BackendObjectKind.TextureView, BackendObjectKind.Sampler,
        BackendObjectKind.ShaderModule, BackendObjectKind.BindGroupLayout, BackendObjectKind.PipelineLayout,
        BackendObjectKind.BindGroup, BackendObjectKind.GraphicsPipeline, BackendObjectKind.ComputePipeline,
    ];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void GetId_EachKind_ReturnsStoredId(BackendObjectKind kind)
    {
        var table = new BackendObjectTable();
        var handle = new Handle(3, 2);

        // The same handle in every kind with a different id each, so a lookup that reads another kind's table fails.
        for (int k = 0; k < BackendObjectTable.KindCount; k++)
        {
            table.Set((BackendObjectKind)k, handle, 1000UL + (ulong)k);
        }

        Assert.Equal(1000UL + (ulong)kind, GetId(table, kind, handle));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void GetId_NullStaleUnknown_Throws(BackendObjectKind kind)
    {
        var table = new BackendObjectTable();
        var removed = new Handle(1, 1);
        var reused = new Handle(1, 2);
        var live = new Handle(2, 1);
        table.Set(kind, removed, 10);
        table.Remove(kind, removed);
        table.Set(kind, reused, 11);
        table.Set(kind, live, 12);

        ArgumentException nullHandle = Assert.Throws<ArgumentException>(() => GetId(table, kind, Handle.Null));
        Assert.Equal("handle", nullHandle.ParamName);
        Assert.Equal(removed, Assert.Throws<StaleHandleException>(() => GetId(table, kind, removed)).Handle);
        Assert.Equal(new Handle(2, 2), Assert.Throws<StaleHandleException>(() => GetId(table, kind, new Handle(2, 2))).Handle);
        Assert.Equal(new Handle(0, 1), Assert.Throws<StaleHandleException>(() => GetId(table, kind, new Handle(0, 1))).Handle);
        Assert.Equal(new Handle(5000, 1), Assert.Throws<StaleHandleException>(() => GetId(table, kind, new Handle(5000, 1))).Handle);

        // Another kind's entries and another table's handles are unknown here.
        var other = (BackendObjectKind)(((int)kind + 1) % BackendObjectTable.KindCount);
        Assert.Throws<StaleHandleException>(() => GetId(table, other, live));
        var foreign = new BackendObjectTable();
        foreign.Set(kind, new Handle(4, 1), 13);
        Assert.Throws<StaleHandleException>(() => GetId(table, kind, new Handle(4, 1)));

        Assert.Equal((11UL, 12UL), (GetId(table, kind, reused), GetId(table, kind, live)));
    }

    [Fact]
    public void Remove_NotLive_Throws()
    {
        var table = new BackendObjectTable();
        var handle = new Handle(1, 1);
        table.Set(BackendObjectKind.Texture, handle, 7);
        table.Remove(BackendObjectKind.Texture, handle);

        Assert.Equal(handle, Assert.Throws<StaleHandleException>(() => table.Remove(BackendObjectKind.Texture, handle)).Handle);
        Assert.Throws<StaleHandleException>(() => table.Remove(BackendObjectKind.Texture, new Handle(9000, 1)));
        Assert.Throws<StaleHandleException>(() => table.Remove(BackendObjectKind.Buffer, new Handle(0, 1)));
        Assert.Equal("handle", Assert.Throws<ArgumentException>(() => table.Remove(BackendObjectKind.Texture, Handle.Null)).ParamName);
    }

    [Fact]
    public void Remove_OldGeneration_LeavesLiveEntry()
    {
        var table = new BackendObjectTable();
        table.Set(BackendObjectKind.Buffer, new Handle(1, 3), 7);

        Assert.Throws<StaleHandleException>(() => table.Remove(BackendObjectKind.Buffer, new Handle(1, 2)));
        Assert.Equal(7UL, table.GetId(new BufferHandle(new Handle(1, 3))));
    }

    public static TheoryData<string> SetMisuses() =>
        ["KindBelowRange", "KindAboveRange", "NullHandle", "ZeroId", "LiveSlot", "IndexBeyondAllocator"];

    [Theory]
    [MemberData(nameof(SetMisuses))]
    public void Set_Misuse_ThrowsAndLeavesTableUnchanged(string misuse)
    {
        var table = new BackendObjectTable();
        var live = new Handle(1, 1);
        table.Set(BackendObjectKind.Sampler, live, 5);

        (Action set, Type expected, string? parameter) = misuse switch
        {
            "KindBelowRange" => (() => table.Set((BackendObjectKind)(-1), new Handle(2, 1), 6), typeof(ArgumentOutOfRangeException), "kind"),
            "KindAboveRange" => (() => table.Set((BackendObjectKind)10, new Handle(2, 1), 6), typeof(ArgumentOutOfRangeException), "kind"),
            "NullHandle" => (() => table.Set(BackendObjectKind.Sampler, Handle.Null, 6), typeof(ArgumentException), "handle"),
            "ZeroId" => (() => table.Set(BackendObjectKind.Sampler, new Handle(2, 1), 0), typeof(ArgumentOutOfRangeException), "id"),
            "LiveSlot" => (() => table.Set(BackendObjectKind.Sampler, new Handle(1, 2), 6), typeof(InvalidOperationException), null),
            "IndexBeyondAllocator" => (
                () => table.Set(BackendObjectKind.Sampler, new Handle(HandleAllocator.MaxSlotLimit, 1), 6), typeof(ArgumentOutOfRangeException), "handle"),
            _ => ((Action, Type, string?))(() => { }, typeof(Exception), null),
        };

        Exception thrown = Assert.Throws(expected, set);
        if (parameter is not null)
        {
            Assert.Equal(parameter, ((ArgumentException)thrown).ParamName);
        }

        Assert.Equal(5UL, table.GetId(new SamplerHandle(live)));
        Assert.Throws<StaleHandleException>(() => table.GetId(new SamplerHandle(new Handle(2, 1))));
        Assert.Throws<StaleHandleException>(() => table.GetId(new SamplerHandle(new Handle(1, 2))));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Remove_KindOutOfRange_Throws(BackendObjectKind kind)
    {
        var table = new BackendObjectTable();
        table.Set(kind, new Handle(1, 1), 5);

        Assert.Equal("kind", Assert.Throws<ArgumentOutOfRangeException>(() => table.Remove((BackendObjectKind)(-1), new Handle(1, 1))).ParamName);
        Assert.Equal("kind", Assert.Throws<ArgumentOutOfRangeException>(() => table.Remove((BackendObjectKind)10, new Handle(1, 1))).ParamName);
        Assert.Equal(5UL, GetId(table, kind, new Handle(1, 1)));
    }

    [Fact]
    public void GetLabel_ReturnsUtf8()
    {
        var table = new BackendObjectTable();
        var label = new DebugLabel(new Handle(0, 1));
        var unpaired = new DebugLabel(new Handle(1, 1));
        var empty = new DebugLabel(new Handle(2, 1));

        // "Pass é✓𝄞": é is C3 A9, ✓ (U+2713) is E2 9C 93, 𝄞 (U+1D11E, a surrogate pair in UTF-16) is F0 9D 84 9E. A lone
        // high surrogate becomes U+FFFD, EF BF BD.
        table.SetLabel(label, "Pass \u00e9\u2713\U0001D11E");
        table.SetLabel(unpaired, "x\ud800");
        table.SetLabel(empty, "");

        Assert.Equal(
            new byte[] { 0x50, 0x61, 0x73, 0x73, 0x20, 0xC3, 0xA9, 0xE2, 0x9C, 0x93, 0xF0, 0x9D, 0x84, 0x9E },
            table.GetLabel(label).ToArray());
        Assert.Equal(new byte[] { 0x78, 0xEF, 0xBF, 0xBD }, table.GetLabel(unpaired).ToArray());
        Assert.True(table.GetLabel(empty).IsEmpty);
        Assert.True(table.GetLabel(default).IsEmpty);
    }

    [Fact]
    public void GetLabel_StaleOrUnknown_Throws()
    {
        var table = new BackendObjectTable();
        table.SetLabel(new DebugLabel(new Handle(1, 2)), "a");

        Assert.Equal(new Handle(1, 1), Assert.Throws<StaleHandleException>(() => table.GetLabel(new DebugLabel(new Handle(1, 1)))).Handle);
        Assert.Throws<StaleHandleException>(() => table.GetLabel(new DebugLabel(new Handle(0, 1))));
        Assert.Throws<StaleHandleException>(() => table.GetLabel(new DebugLabel(new Handle(700, 1))));
    }

    [Fact]
    public void SetLabel_Misuse_Throws()
    {
        var table = new BackendObjectTable();
        var label = new DebugLabel(new Handle(1, 1));
        table.SetLabel(label, "first");

        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => table.SetLabel(new DebugLabel(new Handle(2, 1)), null!)).ParamName);
        Assert.Equal("label", Assert.Throws<ArgumentException>(() => table.SetLabel(default, "x")).ParamName);
        Assert.Throws<InvalidOperationException>(() => table.SetLabel(new DebugLabel(new Handle(1, 2)), "second"));
        Assert.Equal("first"u8.ToArray(), table.GetLabel(label).ToArray());
    }

    [Fact]
    public void Clear_RemovesObjects_KeepsLabels()
    {
        var table = new BackendObjectTable();
        var handle = new Handle(1, 1);
        var label = new DebugLabel(new Handle(1, 1));
        for (int k = 0; k < BackendObjectTable.KindCount; k++)
        {
            table.Set((BackendObjectKind)k, handle, 5);
        }

        table.SetLabel(label, "frame");

        table.Clear();

        for (int k = 0; k < BackendObjectTable.KindCount; k++)
        {
            var kind = (BackendObjectKind)k;
            Assert.Throws<StaleHandleException>(() => GetId(table, kind, handle));
            table.Set(kind, new Handle(1, 2), 6);
            Assert.Equal(6UL, GetId(table, kind, new Handle(1, 2)));
        }

        Assert.Equal("frame"u8.ToArray(), table.GetLabel(label).ToArray());
    }

    [Fact]
    public void Lookups_DoNotAllocate()
    {
        var table = new BackendObjectTable();
        var handle = new Handle(40, 3);
        var label = new DebugLabel(new Handle(7, 1));
        for (int k = 0; k < BackendObjectTable.KindCount; k++)
        {
            table.Set((BackendObjectKind)k, handle, 100UL + (ulong)k);
        }

        table.SetLabel(label, "pass");
        ulong sum = Lookups(table, handle, label);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            sum += Lookups(table, handle, label);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(1001UL * (1045UL + 4), sum);
    }

    [Fact]
    public void Growth_Fails_LeavesTableUsable()
    {
        var table = new BackendObjectTable();
        for (uint i = 0; i < BackendObjectTable.InitialCapacity; i++)
        {
            table.Set(BackendObjectKind.Texture, new Handle(i, 1), 100 + i);
        }

        table.SetLabel(new DebugLabel(new Handle(0, 1)), "kept");

        // Fail the second of the two arrays a growth allocates, so a table that replaced the first one early would show it.
        int calls = 0;
        var grown = new List<BackendObjectKind?>();
        table.BeforeGrowth = kind =>
        {
            grown.Add(kind);
            if (++calls % 2 == 0)
            {
                throw new InsufficientMemoryException("Simulated: the table can't grow.");
            }
        };

        Assert.Throws<InsufficientMemoryException>(() => table.Set(BackendObjectKind.Texture, new Handle(BackendObjectTable.InitialCapacity, 1), 999));
        Assert.Throws<InsufficientMemoryException>(() => table.SetLabel(new DebugLabel(new Handle(BackendObjectTable.InitialCapacity, 1)), "lost"));
        Assert.Equal(new BackendObjectKind?[] { BackendObjectKind.Texture, BackendObjectKind.Texture, null, null }, grown);

        for (uint i = 0; i < BackendObjectTable.InitialCapacity; i++)
        {
            Assert.Equal(100 + i, table.GetId(new TextureHandle(new Handle(i, 1))));
        }

        Assert.Throws<StaleHandleException>(() => table.GetId(new TextureHandle(new Handle(BackendObjectTable.InitialCapacity, 1))));
        Assert.Throws<StaleHandleException>(() => table.GetLabel(new DebugLabel(new Handle(BackendObjectTable.InitialCapacity, 1))));
        Assert.Equal("kept"u8.ToArray(), table.GetLabel(new DebugLabel(new Handle(0, 1))).ToArray());

        table.BeforeGrowth = null;
        table.Set(BackendObjectKind.Texture, new Handle(BackendObjectTable.InitialCapacity, 1), 999);
        table.Remove(BackendObjectKind.Texture, new Handle(0, 1));
        Assert.Equal(999UL, table.GetId(new TextureHandle(new Handle(BackendObjectTable.InitialCapacity, 1))));
        Assert.Equal(115UL, table.GetId(new TextureHandle(new Handle(15, 1))));
    }

    [Fact]
    public void SetRemove_MatchReferenceModel()
    {
        // An operation: 0 set, 1 remove, 2 look up, 3 clear; then a kind, a slot index (over-sampling the first growth
        // boundary at 16 and slot 0), a generation and an id.
        Gen<(int Op, int Kind, uint Index, uint Generation, ulong Id)> operation = Gen.Select(
            Gen.Frequency((10, Gen.Int[0, 2]), (1, Gen.Const(3))),
            Gen.Int[0, BackendObjectTable.KindCount - 1],
            Gen.OneOf(Gen.UInt[0, 3], Gen.UInt[14, 18], Gen.UInt[0, 70]),
            Gen.UInt[1, 3],
            Gen.ULong[1, ulong.MaxValue]);

        operation.Array[1, 200].Sample(operations =>
        {
            var table = new BackendObjectTable();
            var model = new Dictionary<(int Kind, uint Index), (uint Generation, ulong Id)>();
            foreach ((int op, int k, uint index, uint generation, ulong id) in operations)
            {
                var kind = (BackendObjectKind)k;
                var handle = new Handle(index, generation);
                bool live = model.TryGetValue((k, index), out (uint Generation, ulong Id) entry);
                switch (op)
                {
                    case 0 when live:
                        Assert.Throws<InvalidOperationException>(() => table.Set(kind, handle, id));
                        break;
                    case 0:
                        table.Set(kind, handle, id);
                        model[(k, index)] = (generation, id);
                        break;
                    case 1 when live && entry.Generation == generation:
                        table.Remove(kind, handle);
                        model.Remove((k, index));
                        break;
                    case 1:
                        Assert.Throws<StaleHandleException>(() => table.Remove(kind, handle));
                        break;
                    case 2 when live && entry.Generation == generation:
                        Assert.Equal(entry.Id, GetId(table, kind, handle));
                        break;
                    case 2:
                        Assert.Throws<StaleHandleException>(() => GetId(table, kind, handle));
                        break;
                    default:
                        table.Clear();
                        model.Clear();
                        break;
                }
            }

            foreach (((int k, uint index), (uint generation, ulong id)) in model)
            {
                Assert.Equal(id, GetId(table, (BackendObjectKind)k, new Handle(index, generation)));
            }
        });
    }

    /// <summary>Every kind's lookup of <paramref name="handle"/> plus the label's length; allocates nothing.</summary>
    private static ulong Lookups(BackendObjectTable table, Handle handle, DebugLabel label)
    {
        ulong sum = 0;
        for (int k = 0; k < BackendObjectTable.KindCount; k++)
        {
            sum += GetId(table, (BackendObjectKind)k, handle);
        }

        return sum + (ulong)table.GetLabel(label).Length;
    }

    /// <summary>Looks <paramref name="handle"/> up through the public overload for <paramref name="kind"/>.</summary>
    private static ulong GetId(BackendObjectTable table, BackendObjectKind kind, Handle handle) => kind switch
    {
        BackendObjectKind.Buffer => table.GetId(new BufferHandle(handle)),
        BackendObjectKind.Texture => table.GetId(new TextureHandle(handle)),
        BackendObjectKind.TextureView => table.GetId(new TextureViewHandle(handle)),
        BackendObjectKind.Sampler => table.GetId(new SamplerHandle(handle)),
        BackendObjectKind.ShaderModule => table.GetId(new ShaderModuleHandle(handle)),
        BackendObjectKind.BindGroupLayout => table.GetId(new BindGroupLayoutHandle(handle)),
        BackendObjectKind.PipelineLayout => table.GetId(new PipelineLayoutHandle(handle)),
        BackendObjectKind.BindGroup => table.GetId(new BindGroupHandle(handle)),
        BackendObjectKind.GraphicsPipeline => table.GetId(new GraphicsPipelineHandle(handle)),
        BackendObjectKind.ComputePipeline => table.GetId(new ComputePipelineHandle(handle)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind."),
    };
}

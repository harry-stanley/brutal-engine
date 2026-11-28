using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BrutalEngine.Graphics.Vulkan.Resources;
using CsCheck;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Resources;

public sealed class ObjectTableTests
{
    // Ids that no table issues: zero, non-zero ids with generation 0 (Handle.FromRawValue throws for them), and ids with
    // a generation but an index past every slot.
    public static TheoryData<ulong> NeverIssued => new()
    {
        0UL,
        1UL,
        5UL,
        0xFFFF_FFFFUL,
        (1UL << 32) | 1000,
        (1UL << 32) | 0xFFFF_FFFF,
        ulong.MaxValue,
    };

    [Fact]
    public void Add_Get_Remove_Stale()
    {
        ObjectTable<Entry> table = new();

        ulong first = table.Add(new Entry(11, "first"));
        ulong second = table.Add(new Entry(22, "second"));

        Assert.NotEqual(0UL, first);
        Assert.NotEqual(0UL, second);
        Assert.NotEqual(first, second);
        Assert.Equal(2, table.Count);
        Assert.True(table.TryGet(first, out Entry got));
        Assert.Equal(new Entry(11, "first"), got);
        Assert.True(table.TryGet(second, out got));
        Assert.Equal(new Entry(22, "second"), got);

        Assert.True(table.TryRemove(first, out Entry removed));

        Assert.Equal(new Entry(11, "first"), removed);
        Assert.Equal(1, table.Count);
        Assert.False(table.TryGet(first, out got));
        Assert.Equal(default, got);
        Assert.False(table.TryRemove(first, out removed));
        Assert.Equal(default, removed);
        Assert.Equal(1, table.Count);

        // The other entry is untouched.
        Assert.True(table.TryGet(second, out got));
        Assert.Equal(new Entry(22, "second"), got);
    }

    [Fact]
    public void Add_ReusedSlot_GivesANewId_AndTheOldOneStaysStale()
    {
        ObjectTable<Entry> table = new();
        ulong old = table.Add(new Entry(1, "old"));
        Assert.True(table.TryRemove(old, out _));

        ulong reused = table.Add(new Entry(2, "new"));

        // Same slot (the low 32 bits), next generation: the old id doesn't reach the new entry.
        Assert.Equal((uint)old, (uint)reused);
        Assert.NotEqual(old, reused);
        Assert.False(table.TryGet(old, out _));
        Assert.False(table.TryRemove(old, out _));
        Assert.True(table.TryGet(reused, out Entry got));
        Assert.Equal(new Entry(2, "new"), got);
        Assert.Equal(1, table.Count);
    }

    [Theory]
    [MemberData(nameof(NeverIssued))]
    public void TryGet_TryRemove_NeverIssuedId_ReturnFalse(ulong id)
    {
        ObjectTable<Entry> table = new();
        ulong live = table.Add(new Entry(7, "live"));

        Assert.False(table.TryGet(id, out Entry got));
        Assert.Equal(default, got);
        Assert.False(table.TryRemove(id, out Entry removed));
        Assert.Equal(default, removed);

        Assert.Equal(1, table.Count);
        Assert.True(table.TryGet(live, out got));
        Assert.Equal(new Entry(7, "live"), got);
    }

    [Fact]
    public void TryGet_EmptyTable_ReturnsFalse()
    {
        ObjectTable<Entry> table = new();

        Assert.False(table.TryGet(1UL << 32, out _));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Enumerate_NewestFirst()
    {
        ObjectTable<Entry> table = new();
        ulong[] ids = new ulong[6];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = table.Add(new Entry(i, null));
        }

        Assert.Equal([ids[5], ids[4], ids[3], ids[2], ids[1], ids[0]], Ids(table));
        Assert.Equal([5, 4, 3, 2, 1, 0], Values(table));
    }

    [Fact]
    public void Enumerate_AfterRemovingNewestMiddleAndOldest_KeepsTheRestNewestFirst()
    {
        ObjectTable<Entry> table = new();
        ulong[] ids = new ulong[5];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = table.Add(new Entry(i, null));
        }

        Assert.True(table.TryRemove(ids[4], out _));
        Assert.Equal([ids[3], ids[2], ids[1], ids[0]], Ids(table));
        Assert.True(table.TryRemove(ids[2], out _));
        Assert.Equal([ids[3], ids[1], ids[0]], Ids(table));
        Assert.True(table.TryRemove(ids[0], out _));
        Assert.Equal([ids[3], ids[1]], Ids(table));

        // A slot freed in the middle is reused, but its new entry is the newest.
        ulong added = table.Add(new Entry(9, null));
        Assert.Equal([added, ids[3], ids[1]], Ids(table));
        Assert.Equal([9, 3, 1], Values(table));
    }

    [Fact]
    public void Enumerate_RemovingEverything_LeavesNothing()
    {
        ObjectTable<Entry> table = new();
        ulong a = table.Add(new Entry(1, null));
        ulong b = table.Add(new Entry(2, null));
        Assert.True(table.TryRemove(a, out _));
        Assert.True(table.TryRemove(b, out _));

        Assert.Empty(Ids(table));

        ulong c = table.Add(new Entry(3, null));
        Assert.Equal([c], Ids(table));
    }

    [Fact]
    public void Enumerate_EmptyTable_YieldsNothing()
    {
        Assert.Empty(Ids(new ObjectTable<Entry>()));
    }

    [Fact]
    public void Enumerate_TableChanged_NextStepThrows()
    {
        ObjectTable<Entry> table = new();
        ulong first = table.Add(new Entry(1, null));
        _ = table.Add(new Entry(2, null));

        ObjectTable<Entry>.Enumerator added = table.GetEnumerator();
        Assert.True(added.MoveNext());
        _ = table.Add(new Entry(3, null));
        Assert.Throws<InvalidOperationException>(() => added.MoveNext());

        ObjectTable<Entry>.Enumerator removed = table.GetEnumerator();
        Assert.True(removed.MoveNext());
        Assert.True(table.TryRemove(first, out _));
        Assert.Throws<InvalidOperationException>(() => removed.MoveNext());

        // A failed removal changes nothing, so it doesn't invalidate an enumerator.
        ObjectTable<Entry>.Enumerator unchanged = table.GetEnumerator();
        Assert.False(table.TryRemove(first, out _));
        Assert.True(unchanged.MoveNext());
        Assert.Equal(3, unchanged.Current.Entry.Value);
    }

    [Fact]
    public void Add_Growth_KeepsEveryEntryAndTheOrder()
    {
        ObjectTable<Entry> table = new();
        Assert.Equal(0, table.Capacity);
        List<int> capacities = [];
        ulong[] ids = new ulong[17];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = table.Add(new Entry(i * 3, null));
            if (capacities.Count == 0 || capacities[^1] != table.Capacity)
            {
                capacities.Add(table.Capacity);
            }
        }

        Assert.Equal([4, 8, 16, 32], capacities);
        Assert.Equal(17, table.Count);
        for (int i = 0; i < ids.Length; i++)
        {
            Assert.True(table.TryGet(ids[i], out Entry got));
            Assert.Equal(i * 3, got.Value);
        }

        List<int> expected = [];
        for (int i = ids.Length - 1; i >= 0; i--)
        {
            expected.Add(i * 3);
        }

        Assert.Equal(expected, Values(table));
    }

    [Fact]
    public void Add_FullTableAfterARemoval_ReusesTheSlotWithoutGrowing()
    {
        ObjectTable<Entry> table = new();
        ulong[] ids = new ulong[4];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = table.Add(new Entry(i, null));
        }

        Assert.Equal(4, table.Capacity);
        Assert.True(table.TryRemove(ids[1], out _));

        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong reused = table.Add(new Entry(8, null));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(4, table.Capacity);
        Assert.Equal((uint)ids[1], (uint)reused);
        Assert.Equal([8, 3, 2, 0], Values(table));
    }

    [Fact]
    public void Add_Exhausted_ThrowsAndChangesNothing()
    {
        ObjectTable<Entry> table = new(maxSlots: 3);
        ulong a = table.Add(new Entry(1, null));
        ulong b = table.Add(new Entry(2, null));
        ulong c = table.Add(new Entry(3, null));
        Assert.Equal(3, table.Capacity);

        Assert.Throws<InvalidOperationException>(() => table.Add(new Entry(4, null)));

        Assert.Equal(3, table.Count);
        Assert.Equal(3, table.Capacity);
        Assert.Equal([c, b, a], Ids(table));

        // A freed slot makes room again.
        Assert.True(table.TryRemove(b, out _));
        ulong d = table.Add(new Entry(5, null));
        Assert.Equal([d, c, a], Ids(table));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_MaxSlotsOutOfRange_Throws(int maxSlots)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObjectTable<Entry>(maxSlots));
    }

    [Fact]
    public void TryRemove_DropsTheTablesReference()
    {
        ObjectTable<Entry> table = new();
        (ulong id, WeakReference name) = AddUnreachable(table);

        Assert.True(RemoveInOwnFrame(table, id));

        for (int i = 0; i < 10 && name.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(name.IsAlive, "the removed entry's name is still reachable from the table");
        GC.KeepAlive(table);
    }

    [Fact]
    public void AddRemove_AfterGrowth_DoesNotAllocate()
    {
        const int Live = 64;
        ObjectTable<Entry> table = new();
        ulong[] ids = new ulong[Live];
        string name = "entry";

        // Grows the table to its working size and warms every path once.
        long checksum = Cycle(table, ids, name);

        long before = GC.GetAllocatedBytesForCurrentThread();
        checksum += Cycle(table, ids, name);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(64, table.Capacity);
        Assert.Equal(0, table.Count);
        Assert.Equal(2 * 4 * (Live * (Live - 1) / 2), checksum);
    }

    [Fact]
    public void Property_RandomAddsAndRemoves_MatchAModel()
    {
        // Each operation: 0-1 add, 2 remove a live entry picked by the next number, 3 remove an id that is stale.
        Gen.Select(Gen.Int[0, 3].Array[1, 300], Gen.Int.Array[300]).Sample((operations, picks) =>
        {
            ObjectTable<Entry> table = new();
            List<(ulong Id, int Value)> model = [];
            List<ulong> stale = [];
            HashSet<ulong> issued = [];
            for (int i = 0; i < operations.Length; i++)
            {
                int pick = picks[i] & int.MaxValue;
                if (operations[i] <= 1 || model.Count == 0)
                {
                    ulong id = table.Add(new Entry(i, null));
                    Assert.NotEqual(0UL, id);
                    Assert.True(issued.Add(id), "an id was issued twice");
                    model.Add((id, i));
                }
                else if (operations[i] == 2)
                {
                    (ulong id, int value) = model[pick % model.Count];
                    Assert.True(table.TryRemove(id, out Entry removed));
                    Assert.Equal(value, removed.Value);
                    _ = model.Remove((id, value));
                    stale.Add(id);
                }
                else if (stale.Count > 0)
                {
                    Assert.False(table.TryRemove(stale[pick % stale.Count], out _));
                }

                Assert.Equal(model.Count, table.Count);
            }

            List<ulong> newestFirst = [];
            for (int i = model.Count - 1; i >= 0; i--)
            {
                newestFirst.Add(model[i].Id);
                Assert.True(table.TryGet(model[i].Id, out Entry got));
                Assert.Equal(model[i].Value, got.Value);
            }

            Assert.Equal(newestFirst, Ids(table));
            foreach (ulong id in stale)
            {
                Assert.False(table.TryGet(id, out _));
            }
        });
    }

    // Adds an entry whose name only the table references, from a frame of its own so no local keeps it alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ulong Id, WeakReference Name) AddUnreachable(ObjectTable<Entry> table)
    {
        string name = new('n', 8);
        return (table.Add(new Entry(1, name)), new WeakReference(name));
    }

    // Removes in a frame of its own: the removed entry lands in a hidden local there, which would keep the name alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RemoveInOwnFrame(ObjectTable<Entry> table, ulong id) => table.TryRemove(id, out _);

    // Fills the table, then reads every entry, enumerates, misses with a stale id and empties it, folding what it reads
    // into a checksum so nothing is optimised away: each of the four passes adds the sum of 0..ids.Length-1.
    private static long Cycle(ObjectTable<Entry> table, ulong[] ids, string name)
    {
        long checksum = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = table.Add(new Entry(i, name));
        }

        for (int i = 0; i < ids.Length; i++)
        {
            checksum += table.TryGet(ids[i], out Entry entry) ? entry.Value : -1000;
        }

        int walked = 0;
        foreach ((ulong _, Entry entry) in table)
        {
            checksum += entry.Value;
            if (++walked > ids.Length)
            {
                return -1;
            }
        }

        for (int i = 0; i < ids.Length; i++)
        {
            checksum += table.TryRemove(ids[i], out Entry entry) ? entry.Value : -1000;
        }

        for (int i = 0; i < ids.Length; i++)
        {
            checksum += table.TryGet(ids[i], out _) || table.TryRemove(ids[i], out _) ? -1000 : i;
        }

        return checksum;
    }

    private static List<ulong> Ids(ObjectTable<Entry> table)
    {
        List<ulong> ids = [];
        foreach ((ulong id, Entry _) in table)
        {
            ids.Add(id);
            ThrowIfPastCount(table, ids.Count);
        }

        return ids;
    }

    private static List<int> Values(ObjectTable<Entry> table)
    {
        List<int> values = [];
        foreach ((ulong _, Entry entry) in table)
        {
            values.Add(entry.Value);
            ThrowIfPastCount(table, values.Count);
        }

        return values;
    }

    // Bounds the walk, so a broken link (a cycle through a removed slot) fails the test instead of hanging the run.
    private static void ThrowIfPastCount(ObjectTable<Entry> table, int yielded)
    {
        if (yielded > table.Count)
        {
            Assert.Fail($"the enumeration yielded {yielded} entries from a table of {table.Count}");
        }
    }

    private readonly record struct Entry(int Value, string? Name);
}

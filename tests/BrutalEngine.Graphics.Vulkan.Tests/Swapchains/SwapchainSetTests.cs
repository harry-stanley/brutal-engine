using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Swapchains;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Swapchains;

// The id rules of docs/design/graphics-vulkan.md section 6, "The resource tracker", for swapchains: Core handle raw
// values, never 0, stale ids detected.
public sealed class SwapchainSetTests
{
    [Fact]
    public void Add_FirstId_IsNotZeroAndFindsTheComponent()
    {
        SwapchainSet<object> set = new();
        object swapchain = new();

        ulong id = set.Add(swapchain);

        Assert.NotEqual(0UL, id);
        Assert.NotEqual(0U, (uint)(id >> 32)); // a live handle's generation is never 0
        Assert.True(set.TryGet(id, out object? found));
        Assert.Same(swapchain, found);
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void Add_Many_GivesDistinctIdsThatEachFindTheirOwn()
    {
        // More than any initial capacity, so the storage grows while earlier ids stay valid.
        SwapchainSet<object> set = new();
        List<(ulong Id, object Swapchain)> added = [];
        for (int i = 0; i < 40; i++)
        {
            object swapchain = new();
            added.Add((set.Add(swapchain), swapchain));
        }

        Assert.Equal(40, new HashSet<ulong>(added.ConvertAll(entry => entry.Id)).Count);
        Assert.DoesNotContain(0UL, added.ConvertAll(entry => entry.Id));
        foreach ((ulong id, object swapchain) in added)
        {
            Assert.True(set.TryGet(id, out object? found));
            Assert.Same(swapchain, found);
        }

        Assert.Equal(40, set.Count);
    }

    [Fact]
    public void Remove_LiveId_HandsBackTheComponentAndMakesTheIdStale()
    {
        SwapchainSet<object> set = new();
        object first = new();
        object second = new();
        ulong firstId = set.Add(first);
        ulong secondId = set.Add(second);

        Assert.True(set.Remove(firstId, out object? removed));

        Assert.Same(first, removed);
        Assert.False(set.TryGet(firstId, out object? stale));
        Assert.Null(stale);
        Assert.False(set.Remove(firstId, out object? again));
        Assert.Null(again);
        Assert.True(set.TryGet(secondId, out object? kept));
        Assert.Same(second, kept);
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void Add_AfterRemove_ReusesTheSlotWithANewIdAndTheOldStaysStale()
    {
        SwapchainSet<object> set = new();
        ulong oldId = set.Add(new object());
        Assert.True(set.Remove(oldId, out _));
        object replacement = new();

        ulong newId = set.Add(replacement);

        Assert.NotEqual(oldId, newId);
        Assert.Equal((uint)oldId, (uint)newId); // the same slot, so only the generation tells them apart
        Assert.False(set.TryGet(oldId, out _));
        Assert.True(set.TryGet(newId, out object? found));
        Assert.Same(replacement, found);
    }

    [Fact]
    public void Add_WhileOthersLive_NeverReusesALiveId()
    {
        SwapchainSet<object> set = new();
        ulong a = set.Add(new object());
        ulong b = set.Add(new object());
        Assert.True(set.Remove(a, out _));
        object bObject = set.TryGet(b, out object? found) ? found : throw new InvalidOperationException("b is live");

        ulong c = set.Add(new object());
        ulong d = set.Add(new object());

        Assert.Equal(4, new HashSet<ulong> { a, b, c, d }.Count);
        Assert.NotEqual(b, c);
        Assert.NotEqual(b, d);
        Assert.True(set.TryGet(b, out object? stillB));
        Assert.Same(bObject, stillB);
        Assert.Equal(3, set.Count);
    }

    [Theory]
    [InlineData(0UL)] // the null handle
    [InlineData(1UL)] // slot 1, generation 0: no handle has it
    [InlineData(0x0000_0001_0000_0000UL)] // slot 0, generation 1: never issued by an empty set
    [InlineData(0x0000_0001_0000_0005UL)] // a slot past the end
    [InlineData(ulong.MaxValue)]
    public void TryGet_IdNeverIssued_ReturnsFalse(ulong id)
    {
        SwapchainSet<object> empty = new();
        SwapchainSet<object> used = new();
        ulong live = used.Add(new object());

        Assert.False(empty.TryGet(id, out object? none));
        Assert.Null(none);
        Assert.False(empty.Remove(id, out _));
        if (id != live)
        {
            Assert.False(used.TryGet(id, out _));
            Assert.False(used.Remove(id, out _));
            Assert.Equal(1, used.Count);
        }
    }

    [Fact]
    public void TryGet_IdFromAnotherSet_ReturnsFalseOnceThatSlotIsReissued()
    {
        // Ids are per set: an id of another set reads as stale here unless the same raw value is live here too.
        SwapchainSet<object> mine = new();
        SwapchainSet<object> other = new();
        ulong otherId = other.Add(new object());
        ulong mineId = mine.Add(new object());
        Assert.True(mine.Remove(mineId, out _));

        Assert.False(mine.TryGet(otherId, out _));
    }

    [Fact]
    public void LookupRemoveAndReuse_AllocateNothing()
    {
        // The remarks' claims: TryGet and Remove allocate nothing, and Add allocates only when the set grows, so an Add
        // into a slot a Remove freed allocates nothing either. Hits, misses and a stale id are all measured.
        SwapchainSet<object> set = new();
        object first = new();
        object second = new();
        ulong kept = set.Add(first);
        ulong removed = set.Add(second);
        Assert.True(set.Remove(removed, out _));
        ulong reused = set.Add(second);
        Assert.True(set.Remove(reused, out _));
        int found = 0;

        long before = GC.GetAllocatedBytesForCurrentThread();
        found += set.TryGet(kept, out object? hit) && ReferenceEquals(hit, first) ? 1 : 0;
        found += set.TryGet(removed, out _) ? 10 : 0;
        found += set.TryGet(0, out _) ? 100 : 0;
        ulong again = set.Add(second);
        found += set.Remove(again, out object? back) && ReferenceEquals(back, second) ? 1000 : 0;
        found += set.Remove(again, out _) ? 10_000 : 0;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(1001, found);
    }

    [Fact]
    public void Add_Null_Throws()
    {
        SwapchainSet<object> set = new();

        Assert.Throws<ArgumentNullException>("swapchain", () => set.Add(null!));
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void Ids_AreCoreHandleRawValues()
    {
        SwapchainSet<object> set = new();

        ulong id = set.Add(new object());

        var handle = Handle.FromRawValue(id);
        Assert.Equal(id, handle.RawValue);
        Assert.False(handle.IsNull);
    }
}

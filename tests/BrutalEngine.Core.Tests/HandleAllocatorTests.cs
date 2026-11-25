using System;
using System.Collections.Generic;
using CsCheck;
using Xunit;

namespace BrutalEngine.Core.Tests;

public sealed class HandleAllocatorTests
{
    [Fact]
    public void New_IsEmpty()
    {
        HandleAllocator allocator = new();

        Assert.Equal(0, allocator.Count);
        Assert.Equal(0, allocator.Capacity);
        Assert.Equal(0, allocator.RetiredCount);
        Assert.Equal(HandleAllocator.MaxSlotLimit, allocator.MaxSlots);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(11, 10)]
    [InlineData(0, 0)]
    [InlineData(0, HandleAllocator.MaxSlotLimit + 1)]
    public void Constructor_InvalidArguments_Throw(int initialCapacity, int maxSlots)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HandleAllocator(initialCapacity, maxSlots));
    }

    [Fact]
    public void Constructor_ReservesInitialCapacity()
    {
        HandleAllocator allocator = new(initialCapacity: 16);

        Assert.Equal(16, allocator.Capacity);
        Assert.Equal(0, allocator.Count);
    }

    [Fact]
    public void Allocate_IssuesDistinctValidHandlesWithGenerationOne()
    {
        HandleAllocator allocator = new();

        Handle a = allocator.Allocate();
        Handle b = allocator.Allocate();

        Assert.NotEqual(a, b);
        Assert.False(a.IsNull);
        Assert.Equal(0u, a.Index);
        Assert.Equal(1u, b.Index);
        Assert.Equal(1u, a.Generation);
        Assert.True(allocator.IsValid(a));
        Assert.True(allocator.IsValid(b));
        Assert.Equal(2, allocator.Count);
    }

    [Fact]
    public void Free_MakesHandleStale()
    {
        HandleAllocator allocator = new();
        Handle handle = allocator.Allocate();

        allocator.Free(handle);

        Assert.False(allocator.IsValid(handle));
        Assert.Equal(0, allocator.Count);
    }

    [Fact]
    public void Allocate_AfterFree_ReusesSlotWithNewGeneration()
    {
        HandleAllocator allocator = new();
        Handle first = allocator.Allocate();
        allocator.Free(first);

        Handle second = allocator.Allocate();

        Assert.Equal(first.Index, second.Index);
        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.False(allocator.IsValid(first));
        Assert.True(allocator.IsValid(second));
    }

    [Fact]
    public void FreedSlots_AreReusedInFifoOrder()
    {
        HandleAllocator allocator = new();
        Handle a = allocator.Allocate();
        Handle b = allocator.Allocate();
        Handle c = allocator.Allocate();

        allocator.Free(b);
        allocator.Free(a);
        allocator.Free(c);

        Assert.Equal(b.Index, allocator.Allocate().Index);
        Assert.Equal(a.Index, allocator.Allocate().Index);
        Assert.Equal(c.Index, allocator.Allocate().Index);
    }

    [Fact]
    public void Free_StaleHandle_ThrowsStaleHandleException()
    {
        HandleAllocator allocator = new();
        Handle handle = allocator.Allocate();
        allocator.Free(handle);

        StaleHandleException exception = Assert.Throws<StaleHandleException>(() => allocator.Free(handle));

        Assert.Equal(handle, exception.Handle);
        Assert.Contains(handle.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Free_HandleFromOtherAllocator_ThrowsStaleHandleException()
    {
        HandleAllocator allocator = new();
        HandleAllocator other = new();
        _ = allocator.Allocate();
        Handle foreign = new(5, 1);

        Assert.Throws<StaleHandleException>(() => allocator.Free(foreign));
        Assert.Throws<StaleHandleException>(() => other.Free(foreign));
    }

    [Fact]
    public void Free_Null_ThrowsArgumentException()
    {
        HandleAllocator allocator = new();

        Assert.Throws<ArgumentException>(() => allocator.Free(Handle.Null));
    }

    [Fact]
    public void TryFree_InvalidHandles_ReturnFalseAndChangeNothing()
    {
        HandleAllocator allocator = new();
        Handle live = allocator.Allocate();

        Assert.False(allocator.TryFree(Handle.Null));
        Assert.False(allocator.TryFree(new Handle(live.Index, live.Generation + 1)));
        Assert.False(allocator.TryFree(new Handle(99, 1)));
        Assert.True(allocator.IsValid(live));
        Assert.Equal(1, allocator.Count);
    }

    [Fact]
    public void IsValid_RejectsNullWrongGenerationAndOutOfRange()
    {
        HandleAllocator allocator = new();
        Handle live = allocator.Allocate();

        Assert.False(allocator.IsValid(Handle.Null));
        Assert.False(allocator.IsValid(new Handle(live.Index, live.Generation + 1)));
        Assert.False(allocator.IsValid(new Handle(uint.MaxValue, 1)));
    }

    [Fact]
    public void MaxSlots_LimitsAllocation()
    {
        HandleAllocator allocator = new(maxSlots: 2);
        Handle a = allocator.Allocate();
        _ = allocator.Allocate();

        Assert.False(allocator.TryAllocate(out Handle failed));
        Assert.True(failed.IsNull);
        Assert.Throws<InvalidOperationException>(() => allocator.Allocate());

        allocator.Free(a);
        Assert.True(allocator.TryAllocate(out Handle reused));
        Assert.Equal(a.Index, reused.Index);
    }

    [Fact]
    public void Growth_NeverExceedsMaxSlots()
    {
        HandleAllocator allocator = new(maxSlots: 5);

        for (int i = 0; i < 5; i++)
        {
            _ = allocator.Allocate();
        }

        Assert.Equal(5, allocator.Capacity);
        Assert.False(allocator.TryAllocate(out _));
    }

    [Fact]
    public void GenerationExhaustion_RetiresSlotForever()
    {
        HandleAllocator allocator = new(maxSlots: 2);
        Handle first = allocator.Allocate();
        allocator.SetGenerationForTesting((int)first.Index, uint.MaxValue);
        Handle last = new(first.Index, uint.MaxValue);
        Assert.True(allocator.IsValid(last));

        allocator.Free(last);

        Assert.Equal(1, allocator.RetiredCount);
        Assert.False(allocator.IsValid(last));
        Handle next = allocator.Allocate();
        Assert.NotEqual(first.Index, next.Index);

        // Only one usable slot remains, and the retired one is never handed out again.
        Assert.False(allocator.TryAllocate(out _));
        allocator.Free(next);
        Assert.Equal(next.Index, allocator.Allocate().Index);
    }

    [Fact]
    public void Clear_InvalidatesAllHandlesAndQueuesSlotsInIndexOrder()
    {
        HandleAllocator allocator = new();
        Handle a = allocator.Allocate();
        Handle b = allocator.Allocate();
        Handle c = allocator.Allocate();
        allocator.Free(b);
        int capacity = allocator.Capacity;

        allocator.Clear();

        Assert.Equal(0, allocator.Count);
        Assert.Equal(capacity, allocator.Capacity);
        Assert.False(allocator.IsValid(a));
        Assert.False(allocator.IsValid(c));
        Assert.Equal(b.Index, allocator.Allocate().Index);
        Assert.Equal(a.Index, allocator.Allocate().Index);
        Assert.Equal(c.Index, allocator.Allocate().Index);
    }

    [Fact]
    public void EnsureCapacity_GrowsAndPreservesState()
    {
        HandleAllocator allocator = new();
        Handle a = allocator.Allocate();
        Handle b = allocator.Allocate();
        allocator.Free(a);

        allocator.EnsureCapacity(1000);

        Assert.True(allocator.Capacity >= 1000);
        Assert.True(allocator.IsValid(b));
        Assert.False(allocator.IsValid(a));
        Assert.Equal(a.Index, allocator.Allocate().Index);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void EnsureCapacity_OutOfRange_Throws(int capacity)
    {
        HandleAllocator allocator = new(maxSlots: 10);

        Assert.Throws<ArgumentOutOfRangeException>(() => allocator.EnsureCapacity(capacity));
    }

    [Fact]
    public void SteadyState_AllocateAndFree_DoNotAllocateManagedMemory()
    {
        HandleAllocator allocator = new(initialCapacity: 1024);
        var handles = new Handle[1024];

        // Warm up so any one-time JIT or tiering work happens before measuring.
        Cycle(allocator, handles);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            Cycle(allocator, handles);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);

        static void Cycle(HandleAllocator allocator, Handle[] handles)
        {
            for (int i = 0; i < handles.Length; i++)
            {
                handles[i] = allocator.Allocate();
            }

            for (int i = 0; i < handles.Length; i++)
            {
                allocator.Free(handles[i]);
            }
        }
    }

    /// <summary>
    /// Random operation sequences against a simple reference model. Properties checked after every step:
    /// live handles are exactly the model's live set, every issued handle is unique for all time,
    /// freed handles never become valid again, and Count matches.
    /// </summary>
    [Fact]
    public void RandomOperations_MatchReferenceModel()
    {
        Gen.Select(Gen.Int[1, 64], Gen.Int[0, 3].Array[1, 400], Gen.Int.Array[400])
            .Sample((maxSlots, operations, picks) =>
            {
                HandleAllocator allocator = new(maxSlots: maxSlots);
                List<Handle> live = [];
                List<Handle> dead = [];
                HashSet<Handle> everIssued = [];

                for (int step = 0; step < operations.Length; step++)
                {
                    int pick = picks[step] & int.MaxValue;
                    switch (operations[step])
                    {
                        case 0:
                        case 1:
                            bool allocated = allocator.TryAllocate(out Handle handle);
                            if (allocated != (live.Count < maxSlots) || !allocated && !handle.IsNull)
                            {
                                return false;
                            }

                            if (allocated)
                            {
                                if (handle.IsNull || !everIssued.Add(handle))
                                {
                                    return false;
                                }

                                live.Add(handle);
                            }

                            break;
                        case 2 when live.Count > 0:
                            Handle victim = live[pick % live.Count];
                            allocator.Free(victim);
                            live.Remove(victim);
                            dead.Add(victim);
                            break;
                        case 3 when dead.Count > 0:
                            if (allocator.TryFree(dead[pick % dead.Count]))
                            {
                                return false;
                            }

                            break;
                        default:
                            break;
                    }

                    if (allocator.Count != live.Count)
                    {
                        return false;
                    }

                    foreach (Handle h in live)
                    {
                        if (!allocator.IsValid(h))
                        {
                            return false;
                        }
                    }

                    foreach (Handle h in dead)
                    {
                        if (allocator.IsValid(h))
                        {
                            return false;
                        }
                    }
                }

                return true;
            });
    }
}

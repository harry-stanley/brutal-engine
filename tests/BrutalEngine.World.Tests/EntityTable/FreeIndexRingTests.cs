using System;
using System.Collections.Generic;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using CsCheck;
using Xunit;

namespace BrutalEngine.World.Tests.EntityTable;

public sealed class FreeIndexRingTests
{
    [Fact]
    public void EnqueueDequeue_IsFifo()
    {
        using FreeIndexRing ring = new(new HeapAllocator(), capacity: 4);
        ring.Enqueue(7);
        ring.Enqueue(3);
        ring.Enqueue(5);

        Assert.Equal(3, ring.Count);
        Assert.Equal(7, Dequeue(ring));
        Assert.Equal(3, Dequeue(ring));
        Assert.Equal(5, Dequeue(ring));
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void EnqueueDequeue_WrapsAround()
    {
        using FreeIndexRing ring = new(new HeapAllocator(), capacity: 3);
        for (int i = 0; i < 10; i++)
        {
            ring.Enqueue(i);
            ring.Enqueue(i + 100);
            Assert.Equal(i, Dequeue(ring));
            Assert.Equal(i + 100, Dequeue(ring));
        }
    }

    [Fact]
    public void TryDequeue_Empty_ReturnsFalse()
    {
        using FreeIndexRing ring = new(new HeapAllocator(), capacity: 2);

        Assert.False(ring.TryDequeue(out int index));
        Assert.Equal(0, index);
    }

    [Fact]
    public void Enqueue_Full_ThrowsAndKeepsContents()
    {
        using FreeIndexRing ring = new(new HeapAllocator(), capacity: 2);
        ring.Enqueue(1);
        ring.Enqueue(2);

        _ = Assert.Throws<InvalidOperationException>(() => ring.Enqueue(3));

        Assert.Equal(2, ring.Count);
        Assert.Equal(1, Dequeue(ring));
        Assert.Equal(2, Dequeue(ring));
    }

    [Fact]
    public void Grow_Wrapped_KeepsFifoOrderAndFreesOldBlock()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "Ring");
        using (FreeIndexRing ring = new(tracking, capacity: 4))
        {
            // Leave the head in the middle, so the queued indices wrap past the end of the block.
            for (int i = 0; i < 3; i++)
            {
                ring.Enqueue(-1);
                _ = Dequeue(ring);
            }

            ring.Enqueue(10);
            ring.Enqueue(11);
            ring.Enqueue(12);

            ring.Grow(9);

            Assert.Equal(9, ring.Capacity);
            Assert.Equal(1, tracking.LiveAllocationCount);
            for (int i = 0; i < 6; i++)
            {
                ring.Enqueue(20 + i);
            }

            int[] expected = [10, 11, 12, 20, 21, 22, 23, 24, 25];
            foreach (int index in expected)
            {
                Assert.Equal(index, Dequeue(ring));
            }
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(3)]
    public void Grow_NotLarger_Throws(int capacity)
    {
        using FreeIndexRing ring = new(new HeapAllocator(), capacity: 4);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => ring.Grow(capacity));
        Assert.Equal(4, ring.Capacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_CapacityBelowOne_Throws(int capacity)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new FreeIndexRing(new HeapAllocator(), capacity));
    }

    [Fact]
    public void Constructor_NullAllocator_Throws()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new FreeIndexRing(null!, 1));
    }

    [Fact]
    public void Dispose_FreesBlockOnce()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "Ring");
        FreeIndexRing ring = new(tracking, capacity: 8);
        Assert.Equal(1, tracking.LiveAllocationCount);

        ring.Dispose();
        tracking.ThrowIfLeaks();

        // A second Dispose frees nothing twice (the tracking allocator throws on a double free).
        ring.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => ring.Enqueue(1));
        _ = Assert.Throws<ObjectDisposedException>(() => ring.TryDequeue(out _));
        _ = Assert.Throws<ObjectDisposedException>(() => ring.Grow(16));
    }

    /// <summary>
    /// Random enqueue, dequeue and grow sequences against <see cref="Queue{T}"/>: after every step the counts agree,
    /// and every dequeued index is the one the model dequeues.
    /// </summary>
    [Fact]
    public void Operations_MatchQueueModel()
    {
        Gen.Select(Gen.Int[1, 8], Gen.Int[0, 3].Array[1, 300], Gen.Int.Array[300])
            .Sample((initialCapacity, operations, values) =>
            {
                TrackingAllocator tracking = new(new HeapAllocator(), "Ring");
                using (FreeIndexRing ring = new(tracking, initialCapacity))
                {
                    Queue<int> model = new();
                    int capacity = initialCapacity;
                    for (int step = 0; step < operations.Length; step++)
                    {
                        switch (operations[step])
                        {
                            case 0:
                            case 1:
                                if (model.Count == capacity)
                                {
                                    capacity += 1 + (values[step] & 7);
                                    ring.Grow(capacity);
                                }

                                ring.Enqueue(values[step]);
                                model.Enqueue(values[step]);
                                break;
                            case 2:
                                bool dequeued = ring.TryDequeue(out int index);
                                if (dequeued != model.TryDequeue(out int expected) || (dequeued && index != expected))
                                {
                                    return false;
                                }

                                break;
                            default:
                                capacity += 1 + (values[step] & 3);
                                ring.Grow(capacity);
                                break;
                        }

                        if (ring.Count != model.Count || ring.Capacity != capacity || tracking.LiveAllocationCount != 1)
                        {
                            return false;
                        }
                    }

                    while (model.TryDequeue(out int expected))
                    {
                        if (!ring.TryDequeue(out int index) || index != expected)
                        {
                            return false;
                        }
                    }
                }

                return tracking.LiveAllocationCount == 0;
            });
    }

    private static int Dequeue(FreeIndexRing ring)
    {
        Assert.True(ring.TryDequeue(out int index));
        return index;
    }
}

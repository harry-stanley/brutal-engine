using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Tests.Entities;
using BrutalEngine.World.Ticks;
using Xunit;

namespace BrutalEngine.World.Tests.Ticks;

/// <summary>
/// The world's list of tick consumers: dense slots with last-in, first-out reuse of released ones, all-or-nothing
/// growth through the world's allocator, misuse checks, the clamp over live slots, and disposal by the world.
/// </summary>
public sealed class TickConsumersTests
{
    public enum Operation
    {
        Get,
        Set,
        Release,
    }

    public enum BadSlot
    {
        Negative,
        Released,
        NeverRegistered,
        PastCapacity,
    }

    [Fact]
    public void Register_FirstSlots_AreDenseWithTheirTicks()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (TickConsumers consumers = new(tracking))
        {
            Assert.Equal(0, consumers.Capacity);
            Assert.Equal(0, tracking.LiveAllocationCount);

            Assert.Equal(0, consumers.Register(5));
            Assert.Equal(1, consumers.Register(9));
            Assert.Equal(2, consumers.Register(uint.MaxValue));
            Assert.Equal(3, consumers.Count);
            Assert.Equal(TickConsumers.InitialCapacity, consumers.Capacity);
            Assert.Equal(5u, consumers.Get(0));
            Assert.Equal(9u, consumers.Get(1));
            Assert.Equal(uint.MaxValue, consumers.Get(2));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Release_ThenRegister_ReusesLastReleasedFirst()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (TickConsumers consumers = new(tracking))
        {
            for (uint i = 0; i < 4; i++)
            {
                Assert.Equal((int)i, consumers.Register(i + 10));
            }

            consumers.Release(0);
            consumers.Release(2);
            Assert.Equal(2, consumers.Count);

            // Last in, first out, each with the tick it is registered with now; then the next unused index.
            Assert.Equal(2, consumers.Register(50));
            Assert.Equal(0, consumers.Register(51));
            Assert.Equal(4, consumers.Register(52));
            Assert.Equal(51u, consumers.Get(0));
            Assert.Equal(11u, consumers.Get(1));
            Assert.Equal(50u, consumers.Get(2));
            Assert.Equal(13u, consumers.Get(3));
            Assert.Equal(52u, consumers.Get(4));
            Assert.Equal(5, consumers.Count);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Register_PastCapacity_GrowsAndKeepsTicks()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (TickConsumers consumers = new(tracking))
        {
            const int Count = (TickConsumers.InitialCapacity * 2) + 1;
            for (int i = 0; i < Count; i++)
            {
                Assert.Equal(i, consumers.Register((uint)(i * 3) + 1));
            }

            Assert.Equal(TickConsumers.InitialCapacity * 4, consumers.Capacity);
            Assert.Equal(Count, consumers.Count);

            // Grown twice; each old array is freed.
            Assert.Equal(1, tracking.LiveAllocationCount);
            Assert.Equal(3, tracking.TotalAllocationCount);
            for (int i = 0; i < Count; i++)
            {
                Assert.Equal((uint)(i * 3) + 1, consumers.Get(i));
            }
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Register_FirstAllocationFails_ListUnchanged()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        FailingAllocator failing = new(tracking);
        using (TickConsumers consumers = new(failing))
        {
            failing.FailNext = true;
            Assert.Throws<InsufficientMemoryException>(() => consumers.Register(7));
            Assert.Equal(0, consumers.Count);
            Assert.Equal(0, consumers.Capacity);
            Assert.Throws<ArgumentOutOfRangeException>(() => consumers.Get(0));

            Assert.Equal(0, consumers.Register(8));
            Assert.Equal(8u, consumers.Get(0));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Register_GrowthFails_ListUnchanged()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        FailingAllocator failing = new(tracking);
        using (TickConsumers consumers = new(failing))
        {
            for (int i = 0; i < TickConsumers.InitialCapacity; i++)
            {
                consumers.Register((uint)i + 100);
            }

            failing.FailNext = true;
            Assert.Throws<InsufficientMemoryException>(() => consumers.Register(1));
            Assert.Equal(TickConsumers.InitialCapacity, consumers.Count);
            Assert.Equal(TickConsumers.InitialCapacity, consumers.Capacity);
            Assert.Equal(1, tracking.LiveAllocationCount);
            for (int i = 0; i < TickConsumers.InitialCapacity; i++)
            {
                Assert.Equal((uint)i + 100, consumers.Get(i));
            }

            // The next registration grows and takes the next index.
            Assert.Equal(TickConsumers.InitialCapacity, consumers.Register(2));
            Assert.Equal(2u, consumers.Get(TickConsumers.InitialCapacity));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Set_ChangesOnlyThatSlot()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (TickConsumers consumers = new(tracking))
        {
            consumers.Register(1);
            consumers.Register(2);
            consumers.Register(3);
            consumers.Set(1, 40);
            Assert.Equal(1u, consumers.Get(0));
            Assert.Equal(40u, consumers.Get(1));
            Assert.Equal(3u, consumers.Get(2));
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>Every operation with every kind of bad slot.</summary>
    public static TheoryData<Operation, BadSlot> MisuseRows()
    {
        TheoryData<Operation, BadSlot> rows = [];
        foreach (Operation operation in Enum.GetValues<Operation>())
        {
            foreach (BadSlot badSlot in Enum.GetValues<BadSlot>())
            {
                rows.Add(operation, badSlot);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(MisuseRows))]
    public void Misuse_BadSlot_ThrowsAndChangesNothing(Operation operation, BadSlot badSlot)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (TickConsumers consumers = new(tracking))
        {
            consumers.Register(11);
            consumers.Register(12);
            consumers.Register(13);
            consumers.Release(1);
            int slot = badSlot switch
            {
                BadSlot.Negative => -1,
                BadSlot.Released => 1,
                BadSlot.NeverRegistered => 3, // below the capacity, never handed out
                _ => TickConsumers.InitialCapacity,
            };

            for (int attempt = 0; attempt < 2; attempt++)
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => Call(consumers, operation, slot));
                Assert.Equal("slot", ex.ParamName);
                Assert.Equal(2, consumers.Count);
                Assert.Equal(11u, consumers.Get(0));
                Assert.Equal(13u, consumers.Get(2));
            }

            // The free list is intact: the released slot comes back first, then the next unused index.
            Assert.Equal(1, consumers.Register(20));
            Assert.Equal(3, consumers.Register(21));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Clamp_RaisesOnlyTicksOlderThanFloor()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (TickConsumers consumers = new(tracking))
        {
            // A floor just past the wrap, so two of the older ticks are numerically larger than it.
            const uint Floor = 3;
            uint[] before = [0xFFFF_FFFE, 0xC000_0004, 1, 2, Floor, Floor + 1, 0x4000_0002];
            uint[] after = [Floor, Floor, Floor, Floor, Floor, Floor + 1, 0x4000_0002];
            foreach (uint tick in before)
            {
                consumers.Register(tick);
            }

            consumers.Clamp(Floor);
            for (int i = 0; i < before.Length; i++)
            {
                Assert.Equal(after[i], consumers.Get(i));
            }

            // A slot released before the clamp and registered again after it holds its new tick.
            consumers.Release(1);
            consumers.Clamp(Floor + 10);
            Assert.Equal(1, consumers.Register(0xFFFF_0000));
            Assert.Equal(0xFFFF_0000u, consumers.Get(1));
            Assert.Equal(Floor + 10, consumers.Get(0));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Dispose_FreesArray_ThenMembersThrow()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        TickConsumers consumers = new(tracking);
        consumers.Register(1);
        consumers.Dispose();
        consumers.Dispose();

        tracking.ThrowIfLeaks();
        Assert.Equal(0, consumers.Count);
        Assert.Equal(0, consumers.Capacity);
        Assert.Throws<ObjectDisposedException>(() => consumers.Register(2));
        Assert.Throws<ObjectDisposedException>(() => consumers.Get(0));
        Assert.Throws<ObjectDisposedException>(() => consumers.Set(0, 3));
        Assert.Throws<ObjectDisposedException>(() => consumers.Release(0));
        Assert.Throws<ObjectDisposedException>(() => consumers.Clamp(4));
    }

    [Fact]
    public void Constructor_NullAllocator_Throws() =>
        Assert.Throws<ArgumentNullException>("allocator", () => new TickConsumers(null!));

    [Fact]
    public void WorldDispose_FreesConsumerArray()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        EcsWorld world = EntityTestSupport.NewWorld(tracking);
        int before = tracking.LiveAllocationCount;
        for (int i = 0; i < TickConsumers.InitialCapacity + 1; i++)
        {
            world.TickConsumers.Register(1);
        }

        // The list allocates from the world's allocator: one live array after growing once.
        Assert.Equal(before + 1, tracking.LiveAllocationCount);
        world.Dispose();

        tracking.ThrowIfLeaks();
        Assert.Throws<ObjectDisposedException>(() => world.TickConsumers.Register(1));
    }

    private static void Call(TickConsumers consumers, Operation operation, int slot)
    {
        switch (operation)
        {
            case Operation.Get:
                _ = consumers.Get(slot);
                break;
            case Operation.Set:
                consumers.Set(slot, 99);
                break;
            default:
                consumers.Release(slot);
                break;
        }
    }
}

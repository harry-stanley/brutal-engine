using System;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Tests.Entities;
using BrutalEngine.World.Ticks;
using Xunit;

namespace BrutalEngine.World.Tests.Ticks;

/// <summary>
/// The public change tick (docs/design/world.md, section 5): it starts at 1, advances by one per
/// <see cref="EcsWorld.AdvanceChangeTick"/>, wraps from <see cref="uint.MaxValue"/> to 0, and is what writes and new
/// rows are stamped with.
/// </summary>
public sealed class ChangeTickTests
{
    [Fact]
    public void Starts1_AdvancesByOne()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            // Observed on an untouched world: the tick it reports and the tick its first spawn stamps.
            Assert.Equal(1u, world.ChangeTick);
            Entity entity = world.Spawn(new Position(1, 2));
            Assert.Equal(1u, TickTestSupport.ChangedOf<Position>(world, entity));
            Assert.Equal(1u, TickTestSupport.AddedOf<Position>(world, entity));

            for (uint expected = 2; expected <= 6; expected++)
            {
                world.AdvanceChangeTick();
                Assert.Equal(expected, world.ChangeTick);
            }

            // A write after the advances stamps the advanced tick; the spawn's Added stamp stays.
            world.Write<Position>(entity).X = 3;
            Assert.Equal(6u, TickTestSupport.ChangedOf<Position>(world, entity));
            Assert.Equal(1u, TickTestSupport.AddedOf<Position>(world, entity));

            // And a spawn into a new chunk stamps it too.
            Entity second = world.Spawn(new Health(4));
            Assert.Equal(6u, TickTestSupport.AddedOf<Health>(world, second));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AdvanceChangeTick_FromMaxValue_WrapsToZero()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity entity = world.Spawn(new Position(1, 2));
            world.SetChangeTickForTesting(uint.MaxValue);

            world.AdvanceChangeTick();
            Assert.Equal(0u, world.ChangeTick);
            world.Write<Position>(entity);
            Assert.Equal(0u, TickTestSupport.ChangedOf<Position>(world, entity));

            world.AdvanceChangeTick();
            Assert.Equal(1u, world.ChangeTick);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void ChangeTick_WrongThread_ThrowsNamingMember()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Exception? failure = EntityTestSupport.RunOnOtherThread(() => _ = world.ChangeTick);
            WrongThreadException wrong = Assert.IsType<WrongThreadException>(failure);
            Assert.Contains("EcsWorld.ChangeTick", wrong.Message, StringComparison.Ordinal);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AdvanceChangeTick_WrongThread_ThrowsAndLeavesTick()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            // Just below a boundary, so an advance that slipped through would also clamp.
            Entity entity = world.Spawn(new Position(1, 2));
            world.SetChangeTickForTesting((2 * TickTestSupport.Interval) - 1);

            Exception? failure = EntityTestSupport.RunOnOtherThread(world.AdvanceChangeTick);
            WrongThreadException wrong = Assert.IsType<WrongThreadException>(failure);
            Assert.Contains("EcsWorld.AdvanceChangeTick", wrong.Message, StringComparison.Ordinal);

            Assert.Equal((2 * TickTestSupport.Interval) - 1, world.ChangeTick);
            Assert.Equal(1u, TickTestSupport.ChangedOf<Position>(world, entity));

            // A second call from the wrong thread fails the same way; the owner thread still advances.
            Assert.IsType<WrongThreadException>(EntityTestSupport.RunOnOtherThread(world.AdvanceChangeTick));
            world.AdvanceChangeTick();
            Assert.Equal(2 * TickTestSupport.Interval, world.ChangeTick);
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// The steady-state per-frame tick work allocates nothing: off-boundary advances with the tick read back, and a
    /// consumer registered into a released slot, set, read and released again, as queries and systems will.
    /// </summary>
    [Fact]
    public void SteadyAdvanceAndConsumerChurn_DoNotAllocate()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            const int Rounds = 1000;
            TickConsumers consumers = world.TickConsumers;

            // Warm up: the first registration grows the list; then one released slot to reuse, and every path once.
            int kept = consumers.Register(1);
            int freed = consumers.Register(1);
            consumers.Release(freed);
            long checksum = Round(world, consumers);

            long nativeAllocations = tracking.TotalAllocationCount;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Rounds; i++)
            {
                checksum += Round(world, consumers);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(nativeAllocations, tracking.TotalAllocationCount);

            // Every round ran: ticks 2 to Rounds + 2, each read twice (ChangeTick and the consumer's), plus the slots.
            long ticks = Rounds + 1;
            long expected = (ticks * (2 + Rounds + 2) / 2 * 2) + (ticks * freed);
            Assert.Equal(expected, checksum);
            Assert.Equal((uint)Rounds + 2, world.ChangeTick);
            Assert.Equal(1u, consumers.Get(kept));
            Assert.Equal(1, consumers.Count);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AfterDispose_ChangeTickMembersThrow()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        EcsWorld world = EntityTestSupport.NewWorld(tracking);
        world.Dispose();

        Assert.Throws<ObjectDisposedException>(() => world.ChangeTick);
        Assert.Throws<ObjectDisposedException>(world.AdvanceChangeTick);
        tracking.ThrowIfLeaks();
    }

    /// <summary>One steady frame's tick work; returns a checksum of what it read, so nothing is optimised away.</summary>
    private static long Round(EcsWorld world, TickConsumers consumers)
    {
        world.AdvanceChangeTick();
        uint tick = world.ChangeTick;
        // The released slot comes back each time; a different slot would change the checksum (and growth would
        // show as an allocator call).
        int slot = consumers.Register(tick);
        consumers.Set(slot, tick);
        long sum = (long)tick + consumers.Get(slot) + slot;
        consumers.Release(slot);
        return sum;
    }
}

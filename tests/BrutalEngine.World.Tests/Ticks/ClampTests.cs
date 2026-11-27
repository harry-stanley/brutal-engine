using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using BrutalEngine.World.Tests.Structural;
using BrutalEngine.World.Ticks;
using Xunit;

namespace BrutalEngine.World.Tests.Ticks;

/// <summary>
/// The clamp at every multiple of 2³⁰ (docs/design/world.md, section 5, "Wrap-safe comparison"): chunk ticks older
/// than <c>ChangeTick - 2³⁰ + 1</c> become that value, consumer ticks older than it become one less, newer ticks are
/// unchanged, a change pending against an idle consumer stays newer, and the pass allocates nothing. Boundaries are
/// reached through the test seam, which sets the tick without clamping.
/// </summary>
public sealed partial class ClampTests
{
    private const uint Interval = TickTestSupport.Interval;

    // The boundary most tests cross, 2³¹, and its floors. The first boundary, 2³⁰, has a stamp floor of 1, below
    // which a world that never wrapped holds nothing.
    private const uint Boundary = 2 * Interval;
    private const uint StampFloor = Interval + 1;
    private const uint ConsumerFloor = Interval;

    [Fact]
    public void AtBoundary_ClampsChunkAndConsumerTicks()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            StructuralSupport.RegisterInOrder(world);
            var fixture = Fixture.Create(world);
            TickConsumers consumers = world.TickConsumers;
            int[] slots =
            [
                consumers.Register(3),
                consumers.Register(ConsumerFloor - 1),
                consumers.Register(ConsumerFloor),
                consumers.Register(StampFloor),
                consumers.Register(Boundary - 1),
            ];

            world.SetChangeTickForTesting(Boundary - 1);
            world.AdvanceChangeTick();

            // Checked as soon as the advance returns: nothing else ran at the new tick.
            Assert.Equal(Boundary, world.ChangeTick);

            // The column-less chunks (the empty archetype's and the tag's) went through the clamp with nothing to raise.
            AssertNoColumns(world, fixture.Empty);
            AssertNoColumns(world, fixture.Tagged);

            // Single: stamped at 5, both ticks old.
            Assert.Equal(StampFloor, TickTestSupport.ChangedOf<Position>(world, fixture.Single));
            Assert.Equal(StampFloor, TickTestSupport.AddedOf<Position>(world, fixture.Single));

            // Pair: Health one tick below the floor (raised), Position at the floor (kept), Added old (raised).
            Assert.Equal(StampFloor, TickTestSupport.ChangedOf<Health>(world, fixture.Pair));
            Assert.Equal(StampFloor, TickTestSupport.ChangedOf<Position>(world, fixture.Pair));
            Assert.Equal(StampFloor, TickTestSupport.AddedOf<Health>(world, fixture.Pair));
            Assert.Equal(StampFloor, TickTestSupport.AddedOf<Position>(world, fixture.Pair));

            // Payload archetype, chunk 0: Payload written after the floor (kept), the rest old.
            Assert.Equal(Fixture.NewerTick, TickTestSupport.ChangedOf<Payload>(world, fixture.FirstChunk));
            Assert.Equal(StampFloor, TickTestSupport.ChangedOf<Position>(world, fixture.FirstChunk));
            Assert.Equal(StampFloor, TickTestSupport.AddedOf<Payload>(world, fixture.FirstChunk));

            // Payload archetype, chunk 1 (not the first chunk, of an archetype that isn't the first): Position written
            // at Boundary - 1 (kept), the rest old.
            Assert.Equal(1, StructuralSupport.Location(world, fixture.SecondChunk).Chunk);
            Assert.Equal(StampFloor, TickTestSupport.ChangedOf<Payload>(world, fixture.SecondChunk));
            Assert.Equal(Boundary - 1, TickTestSupport.ChangedOf<Position>(world, fixture.SecondChunk));
            Assert.Equal(StampFloor, TickTestSupport.AddedOf<Payload>(world, fixture.SecondChunk));
            Assert.Equal(StampFloor, TickTestSupport.AddedOf<Position>(world, fixture.SecondChunk));

            // Consumers older than the stamp floor go one below it; newer ones are unchanged.
            Assert.Equal(ConsumerFloor, consumers.Get(slots[0]));
            Assert.Equal(ConsumerFloor, consumers.Get(slots[1]));
            Assert.Equal(ConsumerFloor, consumers.Get(slots[2]));
            Assert.Equal(StampFloor, consumers.Get(slots[3]));
            Assert.Equal(Boundary - 1, consumers.Get(slots[4]));
            StructuralSupport.AssertDense(world);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void OffBoundary_Advances_ClampNothing()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            StructuralSupport.RegisterInOrder(world);
            _ = Fixture.Create(world);
            TickConsumers consumers = world.TickConsumers;
            int old = consumers.Register(3);
            int atFloor = consumers.Register(ConsumerFloor);

            // Two ticks short of the boundary: an advance that clamped here would raise the ticks stamped at 5.
            world.SetChangeTickForTesting(Boundary - 2);
            uint[] before = TickTestSupport.AllChunkTicks(world);
            world.AdvanceChangeTick();
            Assert.Equal(before, TickTestSupport.AllChunkTicks(world));
            Assert.Equal(3u, consumers.Get(old));

            // Across the boundary, then one more: the advance after a clamp leaves the clamped ticks where they are.
            world.AdvanceChangeTick();
            uint[] clamped = TickTestSupport.AllChunkTicks(world);
            Assert.NotEqual(before, clamped);
            world.AdvanceChangeTick();
            Assert.Equal(Boundary + 1, world.ChangeTick);
            Assert.Equal(clamped, TickTestSupport.AllChunkTicks(world));
            Assert.Equal(ConsumerFloor, consumers.Get(old));
            Assert.Equal(ConsumerFloor, consumers.Get(atFloor));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void PendingChangeAcrossClamp_StillNewer()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            StructuralSupport.RegisterInOrder(world);
            Entity pending = world.Spawn(new Position(1, 1));
            Entity seen = world.Spawn(new Health(1), new Position(2, 2));

            // The consumer last ran at 10; a write at 10 isn't newer, a write at 11 is pending against it.
            world.SetChangeTickForTesting(10);
            int consumer = world.TickConsumers.Register(world.ChangeTick);
            world.Write<Position>(seen);
            world.SetChangeTickForTesting(11);
            world.Write<Position>(pending);
            Assert.True(TickMath.IsNewer(TickTestSupport.ChangedOf<Position>(world, pending), world.TickConsumers.Get(consumer)));
            Assert.False(TickMath.IsNewer(TickTestSupport.ChangedOf<Position>(world, seen), world.TickConsumers.Get(consumer)));

            // Idle across the clamp at 2³¹.
            world.SetChangeTickForTesting(Boundary - 1);
            world.AdvanceChangeTick();

            // The pending change is still newer, because the consumer went strictly below the clamped stamp.
            uint since = world.TickConsumers.Get(consumer);
            Assert.True(TickMath.IsNewer(TickTestSupport.ChangedOf<Position>(world, pending), since));
            Assert.Equal(ConsumerFloor, since);
            Assert.Equal(StampFloor, TickTestSupport.ChangedOf<Position>(world, pending));

            // The documented worst case: the write the consumer had already seen is a false positive, once.
            Assert.True(TickMath.IsNewer(TickTestSupport.ChangedOf<Position>(world, seen), since));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void PendingChangeAtLastTickBeforeCrossing_StillNewer()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            StructuralSupport.RegisterInOrder(world);
            Entity entity = world.Spawn(new Position(1, 1));
            world.SetChangeTickForTesting(10);
            int consumer = world.TickConsumers.Register(world.ChangeTick);
            world.SetChangeTickForTesting(Boundary - 1);
            world.AdvanceChangeTick();
            Assert.Equal(ConsumerFloor, world.TickConsumers.Get(consumer));

            // The last tick before the next crossing: the clamped consumer is 2³¹ - 1 behind, the oldest valid age.
            const uint Next = 3 * Interval;
            world.SetChangeTickForTesting(Next - 1);
            world.Write<Position>(entity);
            Assert.True(TickMath.IsNewer(TickTestSupport.ChangedOf<Position>(world, entity), world.TickConsumers.Get(consumer)));

            world.AdvanceChangeTick();
            Assert.Equal(Boundary, world.TickConsumers.Get(consumer));
            Assert.Equal(Next - 1, TickTestSupport.ChangedOf<Position>(world, entity));
            Assert.True(TickMath.IsNewer(TickTestSupport.ChangedOf<Position>(world, entity), world.TickConsumers.Get(consumer)));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AtWrapBoundary_ClampsToFloorsBelowZero()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            StructuralSupport.RegisterInOrder(world);
            world.SetChangeTickForTesting(0x8000_0005);
            Entity old = world.Spawn(new Position(1, 1));
            Entity written = world.Spawn(new Health(1), new Position(2, 2));
            int consumer = world.TickConsumers.Register(0x8000_0003);
            world.SetChangeTickForTesting(0xC000_0002);
            world.Write<Health>(written);

            // uint.MaxValue to 0 is the boundary 2³²: the floors are 0xC0000001 and 0xC0000000.
            world.SetChangeTickForTesting(uint.MaxValue);
            world.AdvanceChangeTick();

            Assert.Equal(0u, world.ChangeTick);
            Assert.Equal(0xC000_0001u, TickTestSupport.ChangedOf<Position>(world, old));
            Assert.Equal(0xC000_0001u, TickTestSupport.AddedOf<Position>(world, old));
            Assert.Equal(0xC000_0002u, TickTestSupport.ChangedOf<Health>(world, written));
            Assert.Equal(0xC000_0001u, TickTestSupport.ChangedOf<Position>(world, written));
            Assert.Equal(0xC000_0000u, world.TickConsumers.Get(consumer));
            Assert.True(TickMath.IsNewer(TickTestSupport.ChangedOf<Health>(world, written), world.TickConsumers.Get(consumer)));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AfterWrap_ClampsTicksStampedBeforeZero()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            StructuralSupport.RegisterInOrder(world);
            world.SetChangeTickForTesting(0xFFFF_FFE0);
            Entity entity = world.Spawn(new Position(1, 1));
            Entity kept = world.Spawn(new Health(1));
            int consumer = world.TickConsumers.Register(world.ChangeTick);
            int later = world.TickConsumers.Register(0xFFFF_FFF8);
            world.SetChangeTickForTesting(0xFFFF_FFF0);
            world.Write<Position>(entity);

            // Over the wrap, the boundary at 2³⁰ (2³² + 2³⁰): the floors are 1 and 0, numerically below every tick
            // stamped before zero, which are still older.
            world.SetChangeTickForTesting(Interval - 2);
            world.Write<Health>(kept);
            world.AdvanceChangeTick();
            world.AdvanceChangeTick();

            Assert.Equal(Interval, world.ChangeTick);
            Assert.Equal(1u, TickTestSupport.ChangedOf<Position>(world, entity));
            Assert.Equal(1u, TickTestSupport.AddedOf<Position>(world, entity));
            Assert.Equal(Interval - 2, TickTestSupport.ChangedOf<Health>(world, kept));
            Assert.Equal(0u, world.TickConsumers.Get(consumer));
            Assert.Equal(0u, world.TickConsumers.Get(later));
            Assert.True(TickMath.IsNewer(TickTestSupport.ChangedOf<Position>(world, entity), world.TickConsumers.Get(consumer)));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Clamp_DoesNotAllocate()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            StructuralSupport.RegisterInOrder(world);
            var fixture = Fixture.Create(world);
            const int Consumers = TickConsumers.InitialCapacity + 3;
            for (int i = 0; i < Consumers; i++)
            {
                world.TickConsumers.Register((uint)i + 1);
            }

            // Warm up: one crossing at 2³¹ runs every clamp path once.
            world.SetChangeTickForTesting(Boundary - 1);
            world.AdvanceChangeTick();

            // Eight more crossings, the wrap at 2³² included. Each jump leaves every stored tick older than the next
            // floor, so every crossing clamps every chunk tick and every consumer.
            const int Crossings = 8;
            long nativeAllocations = tracking.TotalAllocationCount;
            uint boundary = Boundary;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Crossings; i++)
            {
                boundary = unchecked(boundary + Interval);
                world.SetChangeTickForTesting(boundary - 1);
                world.AdvanceChangeTick();
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(nativeAllocations, tracking.TotalAllocationCount);

            // The measured crossings did the work: everything sits at the last boundary's floors.
            Assert.Equal(boundary, world.ChangeTick);
            Assert.All(TickTestSupport.AllChunkTicks(world), tick => Assert.Equal(TickMath.StampFloor(boundary), tick));
            for (int i = 0; i < Consumers; i++)
            {
                Assert.Equal(TickMath.ConsumerFloor(boundary), world.TickConsumers.Get(i));
            }

            Assert.Equal(TickMath.StampFloor(boundary), TickTestSupport.ChangedOf<Position>(world, fixture.SecondChunk));
        }

        tracking.ThrowIfLeaks();
    }

    private static void AssertNoColumns(EcsWorld world, Entity entity)
    {
        Assert.True(world.IsAlive(entity));
        EntityRecord record = StructuralSupport.Location(world, entity);
        ChunkTable chunks = world.Archetypes[record.Archetype].Chunks;
        Assert.Equal(0, chunks.ColumnCount);
        Assert.Equal(1, chunks.ChunkCount);
        Assert.Equal(0, chunks.GetChanged(record.Chunk).Length);
        Assert.Equal(0, chunks.GetAdded(record.Chunk).Length);
    }

    /// <summary>
    /// Entities in three archetypes with ticks on both sides of the 2³¹ boundary's stamp floor, plus two in column-less
    /// archetypes (empty and tag-only) whose chunks have no tick arrays. The Payload archetype has two chunks, and its
    /// second chunk holds the newest stamp, so a clamp that skips archetypes or chunks shows.
    /// </summary>
    private sealed class Fixture
    {
        /// <summary>A stamp newer than the floor of the boundary at 2³¹.</summary>
        public const uint NewerTick = Interval + 9;

        private Fixture(Entity empty, Entity tagged, Entity single, Entity pair, Entity firstChunk, Entity secondChunk)
        {
            Empty = empty;
            Tagged = tagged;
            Single = single;
            Pair = pair;
            FirstChunk = firstChunk;
            SecondChunk = secondChunk;
        }

        /// <summary>In the empty archetype: a chunk with no columns, so no tick arrays.</summary>
        public Entity Empty { get; }

        /// <summary>A tag only: also a chunk with no columns.</summary>
        public Entity Tagged { get; }

        public Entity Single { get; }

        public Entity Pair { get; }

        public Entity FirstChunk { get; }

        public Entity SecondChunk { get; }

        public static Fixture Create(EcsWorld world)
        {
            world.SetChangeTickForTesting(5);
            Entity empty = world.Spawn();
            Entity tagged = world.Spawn(default(Marker));
            Entity single = world.Spawn(new Position(1, 1));
            Entity pair = world.Spawn(new Health(1), new Position(2, 2));

            world.SetChangeTickForTesting(7);
            Entity firstChunk = world.Spawn(new Payload(1), new Position(3, 3));
            Archetype payloads = StructuralSupport.ArchetypeOf(world, firstChunk);
            Entity last = firstChunk;
            while (payloads.Chunks.ChunkCount < 2)
            {
                last = world.Spawn(new Payload(2), new Position(4, 4));
            }

            // One below the floor and exactly at it.
            world.SetChangeTickForTesting(StampFloor - 1);
            world.Write<Health>(pair);
            world.SetChangeTickForTesting(StampFloor);
            world.Write<Position>(pair);

            world.SetChangeTickForTesting(NewerTick);
            world.Write<Payload>(firstChunk);
            world.SetChangeTickForTesting(Boundary - 1);
            world.Write<Position>(last);
            return new Fixture(empty, tagged, single, pair, firstChunk, last);
        }
    }
}

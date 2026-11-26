using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Structural;

/// <summary>The argument checks of the storage half of a move, <see cref="Archetype.CopySharedColumns"/>.</summary>
public sealed class ArchetypeMoveTests
{
    [Fact]
    public void CopySharedColumns_InvalidArguments_ThrowAndCopyNothing()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        using (EcsWorld otherWorld = EntityTestSupport.NewWorld(tracking))
        {
            Entity from = world.Spawn(new Health(1), new Payload(1));
            Entity to = world.Spawn(new Position(2, 2), new Health(2), new Payload(2));
            Entity foreign = otherWorld.Spawn(new Position(3, 3), new Health(3), new Payload(3));
            Archetype source = StructuralSupport.ArchetypeOf(world, from);
            Archetype target = StructuralSupport.ArchetypeOf(world, to);
            Archetype foreignTarget = StructuralSupport.ArchetypeOf(otherWorld, foreign);
            string before = StructuralSupport.Dump(world);

            _ = Assert.Throws<ArgumentNullException>(() => source.CopySharedColumns(0, 0, null!, 0, 0));
            Assert.Contains("another archetype", Assert.Throws<ArgumentException>(() => source.CopySharedColumns(0, 0, source, 0, 0)).Message, StringComparison.Ordinal);
            Assert.Contains("same set", Assert.Throws<ArgumentException>(() => source.CopySharedColumns(0, 0, foreignTarget, 0, 0)).Message, StringComparison.Ordinal);
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.CopySharedColumns(0, 1, target, 0, 0));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.CopySharedColumns(1, 0, target, 0, 0));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.CopySharedColumns(0, 0, target, 0, 1));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.CopySharedColumns(0, 0, target, -1, 0));

            Assert.Equal(before, StructuralSupport.Dump(world));
            Assert.Equal(3, otherWorld.Get<Health>(foreign).Value);

            // The valid call copies the shared columns and leaves the column only the target has.
            source.CopySharedColumns(0, 0, target, 0, 0);
            Assert.Equal(1, world.Get<Health>(to).Value);
            Assert.True(EntityComponents.Same(new Payload(1), world.Get<Payload>(to)));
            Assert.Equal(new Position(2, 2), world.Get<Position>(to));
        }

        tracking.ThrowIfLeaks();
    }
}

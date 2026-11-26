using System;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using Xunit;

namespace BrutalEngine.World.Tests.Entities;

/// <summary>
/// The failure table (docs/design/world.md, section 11) for the direct entity members: each misuse throws its documented
/// exception before anything changes.
/// </summary>
public sealed class EntityMisuseTests
{
    /// <summary>The member a row calls.</summary>
    public enum Member
    {
        /// <summary><see cref="EcsWorld.Spawn()"/>.</summary>
        Spawn,

        /// <summary><see cref="EcsWorld.EntityCount"/>.</summary>
        EntityCount,

        /// <summary><see cref="EcsWorld.Despawn"/>.</summary>
        Despawn,
    }

    /// <summary>The misuse a row makes.</summary>
    public enum Misuse
    {
        /// <summary>Passes <see cref="Entity.Null"/>.</summary>
        Null,

        /// <summary>Passes an entity that was despawned.</summary>
        Despawned,

        /// <summary>Passes an entity whose slot was retired at the last generation.</summary>
        Retired,

        /// <summary>Passes an entity whose index is outside the world's table, as one from another world can be.</summary>
        OutOfRange,

        /// <summary>Passes an entity reserved for a command buffer and not placed yet.</summary>
        Pending,

        /// <summary>Calls with an alive entity after the world is disposed.</summary>
        Disposed,

        /// <summary>Calls with an alive entity from a thread other than the owner.</summary>
        WrongThread,
    }

    [Theory]
    [InlineData(Member.Despawn, Misuse.Null, typeof(ArgumentException))]
    [InlineData(Member.Despawn, Misuse.Despawned, typeof(StaleHandleException))]
    [InlineData(Member.Despawn, Misuse.Retired, typeof(StaleHandleException))]
    [InlineData(Member.Despawn, Misuse.OutOfRange, typeof(StaleHandleException))]
    [InlineData(Member.Despawn, Misuse.Pending, typeof(InvalidOperationException))]
    [InlineData(Member.Despawn, Misuse.Disposed, typeof(ObjectDisposedException))]
    [InlineData(Member.Despawn, Misuse.WrongThread, typeof(WrongThreadException))]
    [InlineData(Member.Spawn, Misuse.Disposed, typeof(ObjectDisposedException))]
    [InlineData(Member.Spawn, Misuse.WrongThread, typeof(WrongThreadException))]
    [InlineData(Member.EntityCount, Misuse.Disposed, typeof(ObjectDisposedException))]
    [InlineData(Member.EntityCount, Misuse.WrongThread, typeof(WrongThreadException))]
    public void Table(Member member, Misuse misuse, Type expected)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        EcsWorld world = EntityTestSupport.NewWorld(tracking);
        try
        {
            Entity alive = world.Spawn();
            Entity other = world.Spawn();
            Entity despawned = world.Spawn();
            world.Despawn(despawned);
            Entity retiring = world.Spawn();
            world.Entities.SetGenerationForTesting((int)retiring.Index, uint.MaxValue);
            var retired = new Entity(retiring.Index, uint.MaxValue);
            world.Despawn(retired);
            Entity pending = world.Entities.Reserve();
            Entity target = misuse switch
            {
                Misuse.Null => Entity.Null,
                Misuse.Despawned => despawned,
                Misuse.Retired => retired,
                Misuse.OutOfRange => new Entity(4096, 1),
                Misuse.Pending => pending,
                _ => alive,
            };

            int slots = world.Entities.SlotCount;
            int rows = world.Archetypes.Empty.RowCount;
            Exception? failure;
            if (misuse == Misuse.WrongThread)
            {
                failure = EntityTestSupport.RunOnOtherThread(() => Call(world, member, target));
                WrongThreadException wrongThread = Assert.IsType<WrongThreadException>(failure);
                Assert.Equal($"EcsWorld.{member}", wrongThread.ApiName);
            }
            else
            {
                if (misuse == Misuse.Disposed)
                {
                    world.Dispose();
                }

                failure = Record.Exception(() => Call(world, member, target));
            }

            Assert.NotNull(failure);
            Assert.IsType(expected, failure);
            if (misuse != Misuse.Disposed)
            {
                // Thrown before any effect: the same entities, slots and rows as before the call.
                Assert.Equal(2, world.EntityCount);
                Assert.Equal(slots, world.Entities.SlotCount);
                Assert.Equal(rows, world.Archetypes.Empty.RowCount);
                Assert.True(world.IsAlive(alive));
                Assert.True(world.IsAlive(other));
                Assert.True(world.IsPending(pending));
                Assert.False(world.IsAlive(despawned));

                // Still usable.
                world.Despawn(alive);
                Assert.Equal(1, world.EntityCount);
            }
        }
        finally
        {
            world.Dispose();
        }

        tracking.ThrowIfLeaks();
    }

    private static void Call(EcsWorld world, Member member, Entity target)
    {
        switch (member)
        {
            case Member.Spawn:
                _ = world.Spawn();
                break;
            case Member.EntityCount:
                _ = world.EntityCount;
                break;
            case Member.Despawn:
                world.Despawn(target);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(member), member, "No such member.");
        }
    }
}

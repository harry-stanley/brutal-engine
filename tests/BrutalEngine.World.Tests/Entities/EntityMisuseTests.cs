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

        /// <summary><see cref="EcsWorld.Spawn{T1, T2}(in T1, in T2)"/>.</summary>
        SpawnWithComponents,

        /// <summary><see cref="EcsWorld.Has{T}"/>.</summary>
        Has,

        /// <summary><see cref="EcsWorld.Get{T}"/>.</summary>
        Get,

        /// <summary><see cref="EcsWorld.TryGet{T}"/>.</summary>
        TryGet,

        /// <summary><see cref="EcsWorld.Write{T}"/>.</summary>
        Write,
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
    [InlineData(Member.Has, Misuse.Null, typeof(ArgumentException))]
    [InlineData(Member.Has, Misuse.Despawned, typeof(StaleHandleException))]
    [InlineData(Member.Has, Misuse.Retired, typeof(StaleHandleException))]
    [InlineData(Member.Has, Misuse.OutOfRange, typeof(StaleHandleException))]
    [InlineData(Member.Has, Misuse.Pending, typeof(InvalidOperationException))]
    [InlineData(Member.Has, Misuse.Disposed, typeof(ObjectDisposedException))]
    [InlineData(Member.Has, Misuse.WrongThread, typeof(WrongThreadException))]
    [InlineData(Member.Get, Misuse.Null, typeof(ArgumentException))]
    [InlineData(Member.Get, Misuse.Despawned, typeof(StaleHandleException))]
    [InlineData(Member.Get, Misuse.Retired, typeof(StaleHandleException))]
    [InlineData(Member.Get, Misuse.OutOfRange, typeof(StaleHandleException))]
    [InlineData(Member.Get, Misuse.Pending, typeof(InvalidOperationException))]
    [InlineData(Member.Get, Misuse.Disposed, typeof(ObjectDisposedException))]
    [InlineData(Member.Get, Misuse.WrongThread, typeof(WrongThreadException))]
    [InlineData(Member.TryGet, Misuse.Null, typeof(ArgumentException))]
    [InlineData(Member.TryGet, Misuse.Despawned, typeof(StaleHandleException))]
    [InlineData(Member.TryGet, Misuse.Retired, typeof(StaleHandleException))]
    [InlineData(Member.TryGet, Misuse.OutOfRange, typeof(StaleHandleException))]
    [InlineData(Member.TryGet, Misuse.Pending, typeof(InvalidOperationException))]
    [InlineData(Member.TryGet, Misuse.Disposed, typeof(ObjectDisposedException))]
    [InlineData(Member.TryGet, Misuse.WrongThread, typeof(WrongThreadException))]
    [InlineData(Member.Write, Misuse.Null, typeof(ArgumentException))]
    [InlineData(Member.Write, Misuse.Despawned, typeof(StaleHandleException))]
    [InlineData(Member.Write, Misuse.Retired, typeof(StaleHandleException))]
    [InlineData(Member.Write, Misuse.OutOfRange, typeof(StaleHandleException))]
    [InlineData(Member.Write, Misuse.Pending, typeof(InvalidOperationException))]
    [InlineData(Member.Write, Misuse.Disposed, typeof(ObjectDisposedException))]
    [InlineData(Member.Write, Misuse.WrongThread, typeof(WrongThreadException))]
    [InlineData(Member.SpawnWithComponents, Misuse.Disposed, typeof(ObjectDisposedException))]
    [InlineData(Member.SpawnWithComponents, Misuse.WrongThread, typeof(WrongThreadException))]
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
            int types = world.Components.Count;
            int rows = world.Archetypes.Empty.RowCount;
            Exception? failure;
            if (misuse == Misuse.WrongThread)
            {
                failure = EntityTestSupport.RunOnOtherThread(() => Call(world, member, target));
                WrongThreadException wrongThread = Assert.IsType<WrongThreadException>(failure);
                Assert.Equal(member == Member.SpawnWithComponents ? "EcsWorld.Spawn" : $"EcsWorld.{member}", wrongThread.ApiName);
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
                Assert.Equal(types, world.Components.Count);
                Assert.Equal(1, world.ArchetypeCount);
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

    /// <summary>
    /// Data access that a component rule forbids: the entity lacks the type, the type isn't registered here, or it is a
    /// tag (registered and on the entity, registered and not on it, or not registered). Each throws
    /// <see cref="InvalidOperationException"/> naming the entity and the component, and registers nothing.
    /// </summary>
    [Theory]
    [InlineData(Member.Get, "missing")]
    [InlineData(Member.Get, "unregistered")]
    [InlineData(Member.Get, "tag")]
    [InlineData(Member.Get, "absent tag")]
    [InlineData(Member.Get, "unregistered tag")]
    [InlineData(Member.Write, "missing")]
    [InlineData(Member.Write, "unregistered")]
    [InlineData(Member.Write, "tag")]
    [InlineData(Member.Write, "absent tag")]
    [InlineData(Member.Write, "unregistered tag")]
    [InlineData(Member.TryGet, "tag")]
    [InlineData(Member.TryGet, "absent tag")]
    [InlineData(Member.TryGet, "unregistered tag")]
    public void ComponentTable(Member member, string rule)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity tagged = world.Spawn(new Position(1, 2), default(Marker));
            Entity plain = world.Spawn(new Health(3));
            world.SetChangeTickForTesting(4);
            int types = world.Components.Count;
            (Entity target, string component, Action call) = (member, rule) switch
            {
                (Member.Get, "missing") => (plain, "Position", () => _ = world.Get<Position>(plain)),
                (Member.Get, "unregistered") => (plain, "Unused", () => _ = world.Get<Unused>(plain)),
                (Member.Get, "tag") => (tagged, "Marker", () => _ = world.Get<Marker>(tagged)),
                (Member.Get, "absent tag") => (plain, "Marker", () => _ = world.Get<Marker>(plain)),
                (Member.Get, _) => (plain, "UnusedTag", () => _ = world.Get<UnusedTag>(plain)),
                (Member.Write, "missing") => (plain, "Position", () => _ = world.Write<Position>(plain)),
                (Member.Write, "unregistered") => (plain, "Unused", () => _ = world.Write<Unused>(plain)),
                (Member.Write, "tag") => (tagged, "Marker", () => _ = world.Write<Marker>(tagged)),
                (Member.Write, "absent tag") => (plain, "Marker", () => _ = world.Write<Marker>(plain)),
                (Member.Write, _) => (plain, "UnusedTag", () => _ = world.Write<UnusedTag>(plain)),
                (_, "tag") => (tagged, "Marker", () => _ = world.TryGet(tagged, out Marker _)),
                (_, "absent tag") => (plain, "Marker", () => _ = world.TryGet(plain, out Marker _)),
                _ => (plain, "UnusedTag", (Action)(() => _ = world.TryGet(plain, out UnusedTag _))),
            };

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(call);

            Assert.Contains(target.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.Contains($"Entities.{component}", exception.Message, StringComparison.Ordinal);

            // Which rule fired: a tag is rejected as a tag, not as a missing component.
            Assert.Equal(rule.EndsWith("tag", StringComparison.Ordinal), exception.Message.Contains("is a tag", StringComparison.Ordinal));
            Assert.Equal(types, world.Components.Count);
            Assert.Equal(new Position(1, 2), world.Get<Position>(tagged));

            // Nothing was stamped: every chunk's Changed ticks are still the spawn tick.
            for (int i = 0; i < world.ArchetypeCount; i++)
            {
                Storage.ChunkTable chunks = world.Archetypes[i].Chunks;
                for (int chunk = 0; chunk < chunks.ChunkCount; chunk++)
                {
                    Assert.All(chunks.GetChanged(chunk).ToArray(), tick => Assert.Equal(1u, tick));
                }
            }
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
            case Member.SpawnWithComponents:
                _ = world.Spawn(new Position(1, 2), new Health(3));
                break;
            case Member.Has:
                _ = world.Has<Position>(target);
                break;
            case Member.Get:
                _ = world.Get<Position>(target);
                break;
            case Member.TryGet:
                _ = world.TryGet(target, out Position _);
                break;
            case Member.Write:
                _ = world.Write<Position>(target);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(member), member, "No such member.");
        }
    }
}

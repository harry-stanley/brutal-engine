using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Tests.Entities;
using Xunit;
using static BrutalEngine.World.Tests.Structural.StructuralSupport;

namespace BrutalEngine.World.Tests.Structural;

/// <summary>
/// <see cref="EntityRef"/> forwards every member to the world (docs/design/world.md, section 2): each call through a
/// reference has the same result, the same exception and the same effect on storage as the direct call.
/// </summary>
public sealed class EntityRefTests
{
    /// <summary>The state of the entity or world when a row's member is called.</summary>
    public enum Condition
    {
        Alive, // The entity is alive.
        Stale, // The entity was despawned after the reference was made.
        Disposed, // The world was disposed after the reference was made.
        WrongThread, // The call is made from a thread other than the owner.
    }

    /// <summary>
    /// Every forwarded member and type case, with the direct call's outcome when the entity is alive. The entity has
    /// Health, Payload and the Marker tag, and lacks Position (registered) and Unused (never registered).
    /// </summary>
    private static readonly (string Member, string AliveOutcome)[] _members =
    [
        ("Has.Health", "True"),
        ("Has.Position", "False"),
        ("Has.Unused", "False"),
        ("Has.Marker", "True"),
        ("Get.Health", "5"),
        ("Get.Position", "System.InvalidOperationException"),
        ("Get.Marker", "System.InvalidOperationException"),
        ("Write.Health", "written"),
        ("Write.Position", "System.InvalidOperationException"),
        ("Write.Marker", "System.InvalidOperationException"),
        ("TryGet.Health", "True 5"),
        ("TryGet.Position", "False 0"),
        ("TryGet.Marker", "System.InvalidOperationException"),
        ("Add.Position", "done"),
        ("Add.Unused", "done"),
        ("Add.Health", "System.InvalidOperationException"),
        ("Add.Marker", "System.InvalidOperationException"),
        ("Set.Health", "done"),
        ("Set.Position", "done"),
        ("Set.Marker", "done"),
        ("Remove.Health", "True"),
        ("Remove.Position", "False"),
        ("Remove.Marker", "True"),
        ("Despawn", "done"),
        ("IsAlive", "True"),
    ];

    public static TheoryData<string, Condition> ForwardingRows()
    {
        TheoryData<string, Condition> rows = [];
        foreach ((string member, _) in _members)
        {
            foreach (Condition condition in Enum.GetValues<Condition>())
            {
                rows.Add(member, condition);
            }
        }

        return rows;
    }

    /// <summary>
    /// The same member is called directly on one world and through a reference on an identical one: the results or
    /// exceptions (type, message, handle) are the same, and so are the worlds' storage afterwards (rows, values,
    /// ticks, registrations). Each row's direct outcome is checked too, so it exercises the path it names.
    /// </summary>
    [Theory]
    [MemberData(nameof(ForwardingRows))]
    public void Forwards_EveryMember(string member, Condition condition)
    {
        (string direct, string? directWorld) = Run(member, condition, viaRef: false);
        (string forwarded, string? forwardedWorld) = Run(member, condition, viaRef: true);

        Assert.Equal(direct, forwarded);
        Assert.Equal(directWorld, forwardedWorld);
        string expected = condition switch
        {
            Condition.Alive => Array.Find(_members, row => row.Member == member).AliveOutcome,
            Condition.Stale when member == "IsAlive" => "False",
            Condition.Stale => typeof(StaleHandleException).FullName!,
            Condition.Disposed => typeof(ObjectDisposedException).FullName!,
            _ => typeof(WrongThreadException).FullName!,
        };
        Assert.StartsWith(expected, direct, StringComparison.Ordinal);
    }

    /// <summary>The reference made for an alive entity holds the world and the entity, and making it changes nothing.</summary>
    [Fact]
    public void Ref_AliveEntity_HoldsWorldAndEntity()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = world.Spawn(new Health(1));
            Entity entity = world.Spawn(new Health(2));
            world.SetChangeTickForTesting(9);
            string before = Dump(world);

            EntityRef handle = world.Ref(entity);

            Assert.Same(world, handle.World);
            Assert.Equal(entity, handle.Entity);
            Assert.Equal(before, Dump(world));
            Assert.Equal(2, handle.Get<Health>().Value);
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData(StructuralTests.Misuse.Null, typeof(ArgumentException))]
    [InlineData(StructuralTests.Misuse.Despawned, typeof(StaleHandleException))]
    [InlineData(StructuralTests.Misuse.OutOfRange, typeof(StaleHandleException))]
    [InlineData(StructuralTests.Misuse.Pending, typeof(InvalidOperationException))]
    [InlineData(StructuralTests.Misuse.Disposed, typeof(ObjectDisposedException))]
    [InlineData(StructuralTests.Misuse.WrongThread, typeof(WrongThreadException))]
    public void Ref_Misuse_Throws(StructuralTests.Misuse misuse, Type expected)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        EcsWorld world = EntityTestSupport.NewWorld(tracking);
        try
        {
            Entity alive = world.Spawn(new Health(1));
            Entity despawned = world.Spawn(new Health(2));
            world.Despawn(despawned);
            Entity pending = world.Entities.Reserve();
            Entity target = misuse switch
            {
                StructuralTests.Misuse.Null => Entity.Null,
                StructuralTests.Misuse.Despawned => despawned,
                StructuralTests.Misuse.OutOfRange => new Entity(4096, 1),
                StructuralTests.Misuse.Pending => pending,
                _ => alive,
            };

            Exception? failure;
            if (misuse == StructuralTests.Misuse.WrongThread)
            {
                failure = EntityTestSupport.RunOnOtherThread(() => _ = world.Ref(target));
                Assert.Equal("EcsWorld.Ref", Assert.IsType<WrongThreadException>(failure).ApiName);
            }
            else
            {
                if (misuse == StructuralTests.Misuse.Disposed)
                {
                    world.Dispose();
                }

                failure = Record.Exception(() => _ = world.Ref(target));
            }

            Assert.NotNull(failure);
            Assert.IsType(expected, failure);
            if (failure is StaleHandleException stale)
            {
                Assert.Equal(new Handle(target.Index, target.Generation), stale.Handle);
            }
        }
        finally
        {
            world.Dispose();
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>A default reference has no world: every member throws InvalidOperationException saying so.</summary>
    [Fact]
    public void Default_EveryMember_Throws()
    {
        EntityRef none = default;
        Action[] calls =
        [
            () => _ = none.World,
            () => _ = none.Has<Health>(),
            () => _ = none.Get<Health>(),
            () => _ = none.Write<Health>(),
            () => _ = none.TryGet(out Health _),
            () => none.Add(new Health(1)),
            () => none.Set(new Health(1)),
            () => _ = none.Remove<Health>(),
            none.Despawn,
            () => _ = none.IsAlive(),
        ];

        Assert.Equal(Entity.Null, none.Entity);
        foreach (Action call in calls)
        {
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(call);
            Assert.Contains("default EntityRef", failure.Message, StringComparison.Ordinal);
            Assert.Contains("EcsWorld.Ref", failure.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Making references and calling every member through them allocates no managed memory and makes no allocator
    /// call once the archetypes exist: reads, a write, an in-place set, add and remove moves of data and a tag between
    /// existing archetypes, a remove of an unregistered type, and a despawn followed by a spawn into an existing
    /// archetype. Measured on this thread, with background collection off in this test host
    /// (AllocationMeasurementTests).
    /// </summary>
    [Fact]
    public void RefAndForwarders_DoNotAllocate()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            // More than a chunk, so the moves and the despawns take rows out of the middle of chunks.
            const int Count = 150;
            var entities = new Entity[Count];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = world.Spawn(new Health(i), new Payload(i));
            }

            // Warm up: create the archetypes, cache the edges, grow the tables, fill the pool and JIT every path.
            long checksum = ForwardingChurn(world, entities);
            checksum += ForwardingChurn(world, entities);

            long nativeAllocations = tracking.TotalAllocationCount;
            int archetypes = world.ArchetypeCount;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 50; round++)
            {
                checksum += ForwardingChurn(world, entities);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(nativeAllocations, tracking.TotalAllocationCount);
            Assert.Equal(archetypes, world.ArchetypeCount);
            Assert.Equal(Count, world.EntityCount);
            Assert.NotEqual(0, checksum);
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// A read-only struct of exactly a world reference and an entity, so it holds a reference and can't be unmanaged
    /// (it can't be stored in a component).
    /// </summary>
    [Fact]
    public void Shape_ReadOnlyStructOfWorldAndEntity()
    {
        Type type = typeof(EntityRef);
        FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.True(type.IsValueType);
        Assert.True(type.IsDefined(typeof(IsReadOnlyAttribute), inherit: false));
        Assert.Equal(2, fields.Length);
        Assert.Single(fields, field => field.FieldType == typeof(EcsWorld));
        Assert.Single(fields, field => field.FieldType == typeof(Entity));
        Assert.True(RuntimeHelpers.IsReferenceOrContainsReferences<EntityRef>());
    }

    /// <summary>
    /// Calls every member through a fresh reference to each entity, in a cycle that ends where it started (every third
    /// entity is despawned and replaced by a new one with the same components), and folds each result into a checksum.
    /// </summary>
    private static long ForwardingChurn(EcsWorld world, Entity[] entities)
    {
        long checksum = 0;
        for (int i = 0; i < entities.Length; i++)
        {
            EntityRef handle = world.Ref(entities[i]);
            checksum += handle.Has<Health>() ? 1 : 0;
            checksum += handle.Get<Health>().Value;
            handle.Write<Health>() = new Health(i + 1);
            checksum += handle.TryGet(out Health health) ? health.Value : 0;
            handle.Set(new Health(i));
            handle.Add(new Position(i, -i));
            handle.Set(default(Marker));
            checksum += handle.Remove<Marker>() ? 1 : 0;
            checksum += handle.Remove<Position>() ? 1 : 0;
            checksum += handle.Remove<Unused>() ? 1 : 0;
            checksum += handle.IsAlive() ? 1 : 0;
            checksum += handle.Entity.Generation + (ReferenceEquals(handle.World, world) ? 1 : 0);
            if (i % 3 == 0)
            {
                handle.Despawn();
                checksum += handle.IsAlive() ? 0 : 1;
                entities[i] = world.Spawn(new Health(i), new Payload(i));
            }
        }

        return checksum;
    }

    /// <summary>
    /// Builds the row's world, calls the member directly or through a reference made while the entity was alive, and
    /// returns the outcome and a dump of the world afterwards (none once it is disposed).
    /// </summary>
    private static (string Outcome, string? World) Run(string member, Condition condition, bool viaRef)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        EcsWorld world = EntityTestSupport.NewWorld(tracking);
        string outcome;
        string? dump = null;
        try
        {
            // Position sorts first, so a move that adds or removes it shifts the shared columns; the entity is in the
            // middle of its chunk, so a move or despawn fills its row.
            RegisterInOrder(world);
            _ = world.Spawn(new Position(1, 1), new Health(1), new Payload(1));
            _ = world.Spawn(new Health(4), new Payload(4), default(Marker));
            Entity entity = world.Spawn(new Health(5), new Payload(5), default(Marker));
            _ = world.Spawn(new Health(6), new Payload(6), default(Marker));
            EntityRef handle = world.Ref(entity);
            world.SetChangeTickForTesting(9);
            if (condition == Condition.Stale)
            {
                world.Despawn(entity);
            }
            else if (condition == Condition.Disposed)
            {
                world.Dispose();
            }

            string? result = null;
            Exception? failure = condition == Condition.WrongThread
                ? EntityTestSupport.RunOnOtherThread(() => result = Invoke(member, world, entity, handle, viaRef))
                : Record.Exception(() => result = Invoke(member, world, entity, handle, viaRef));
            outcome = failure switch
            {
                null => result!,
                StaleHandleException stale => string.Create(CultureInfo.InvariantCulture, $"{stale.GetType().FullName}: {stale.Message} {stale.Handle}"),
                // Its message names the calling thread, which differs between the two runs: the API name is compared.
                WrongThreadException wrong => $"{wrong.GetType().FullName}: {wrong.ApiName}",
                _ => $"{failure.GetType().FullName}: {failure.Message}",
            };

            if (condition != Condition.Disposed)
            {
                dump = string.Create(CultureInfo.InvariantCulture, $"{Dump(world)}types={world.Components.Count}");
            }
        }
        finally
        {
            world.Dispose();
        }

        tracking.ThrowIfLeaks();
        return (outcome, dump);
    }

    /// <summary>Calls one member, directly or through the reference, and describes its result.</summary>
    private static string Invoke(string member, EcsWorld world, Entity entity, EntityRef handle, bool viaRef)
    {
        switch (member)
        {
            case "Has.Health":
                return (viaRef ? handle.Has<Health>() : world.Has<Health>(entity)).ToString();
            case "Has.Position":
                return (viaRef ? handle.Has<Position>() : world.Has<Position>(entity)).ToString();
            case "Has.Unused":
                return (viaRef ? handle.Has<Unused>() : world.Has<Unused>(entity)).ToString();
            case "Has.Marker":
                return (viaRef ? handle.Has<Marker>() : world.Has<Marker>(entity)).ToString();
            case "Get.Health":
                return (viaRef ? handle.Get<Health>() : world.Get<Health>(entity)).Value.ToString(CultureInfo.InvariantCulture);
            case "Get.Position":
                return (viaRef ? handle.Get<Position>() : world.Get<Position>(entity)).X.ToString(CultureInfo.InvariantCulture);
            case "Get.Marker":
                _ = viaRef ? handle.Get<Marker>() : world.Get<Marker>(entity);
                return "read";
            case "Write.Health":
                (viaRef ? ref handle.Write<Health>() : ref world.Write<Health>(entity)) = new Health(77);
                return "written";
            case "Write.Position":
                (viaRef ? ref handle.Write<Position>() : ref world.Write<Position>(entity)) = new Position(7, 7);
                return "written";
            case "Write.Marker":
                _ = viaRef ? handle.Write<Marker>() : world.Write<Marker>(entity);
                return "written";
            case "TryGet.Health":
                {
                    bool found = viaRef ? handle.TryGet(out Health health) : world.TryGet(entity, out health);
                    return string.Create(CultureInfo.InvariantCulture, $"{found} {health.Value}");
                }

            case "TryGet.Position":
                {
                    bool found = viaRef ? handle.TryGet(out Position position) : world.TryGet(entity, out position);
                    return string.Create(CultureInfo.InvariantCulture, $"{found} {position.X}");
                }

            case "TryGet.Marker":
                _ = viaRef ? handle.TryGet(out Marker _) : world.TryGet(entity, out Marker _);
                return "read";
            case "Add.Position":
                Do(viaRef, () => handle.Add(new Position(3, 3)), () => world.Add(entity, new Position(3, 3)));
                return "done";
            case "Add.Unused":
                Do(viaRef, () => handle.Add(new Unused(8)), () => world.Add(entity, new Unused(8)));
                return "done";
            case "Add.Health":
                Do(viaRef, () => handle.Add(new Health(8)), () => world.Add(entity, new Health(8)));
                return "done";
            case "Add.Marker":
                Do(viaRef, () => handle.Add(default(Marker)), () => world.Add(entity, default(Marker)));
                return "done";
            case "Set.Health":
                Do(viaRef, () => handle.Set(new Health(9)), () => world.Set(entity, new Health(9)));
                return "done";
            case "Set.Position":
                Do(viaRef, () => handle.Set(new Position(9, 9)), () => world.Set(entity, new Position(9, 9)));
                return "done";
            case "Set.Marker":
                Do(viaRef, () => handle.Set(default(Marker)), () => world.Set(entity, default(Marker)));
                return "done";
            case "Remove.Health":
                return (viaRef ? handle.Remove<Health>() : world.Remove<Health>(entity)).ToString();
            case "Remove.Position":
                return (viaRef ? handle.Remove<Position>() : world.Remove<Position>(entity)).ToString();
            case "Remove.Marker":
                return (viaRef ? handle.Remove<Marker>() : world.Remove<Marker>(entity)).ToString();
            case "Despawn":
                Do(viaRef, handle.Despawn, () => world.Despawn(entity));
                return "done";
            default:
                Assert.Equal("IsAlive", member);
                return (viaRef ? handle.IsAlive() : world.IsAlive(entity)).ToString();
        }
    }

    private static void Do(bool viaRef, Action forwarded, Action direct)
    {
        if (viaRef)
        {
            forwarded();
        }
        else
        {
            direct();
        }
    }
}

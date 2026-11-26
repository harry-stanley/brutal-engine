using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Threading;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Internal;
using Xunit;

namespace BrutalEngine.World.Tests.Components;

public sealed class ComponentRegistryTests
{
    private const int RacerCount = 8;
    private const int RacingThreads = 8;

    public static TheoryData<string> Members =>
    [
        "EcsWorld.Components",
        "ComponentRegistry.Count",
        "ComponentRegistry.Capacity",
        "ComponentRegistry.Register",
        "ComponentRegistry.TryGetId",
        "ComponentRegistry.TryGetId(StableComponentId)",
        "ComponentRegistry.GetComponentType",
    ];

    [Fact]
    public void Register_IdempotentAndDense()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            ComponentRegistry registry = world.Components;
            Assert.Equal(0, registry.Count);

            ComponentId position = registry.Register<Position>();
            ComponentId velocity = registry.Register<Velocity>();
            ComponentId frozen = registry.Register<Frozen>();

            Assert.Equal(0, position.Index);
            Assert.Equal(1, velocity.Index);
            Assert.Equal(2, frozen.Index);
            Assert.Equal(velocity, registry.Register<Velocity>());
            Assert.Equal(position, registry.Register<Position>());
            Assert.Equal(3, registry.Count);

            Assert.True(registry.TryGetId<Velocity>(out ComponentId byType));
            Assert.Equal(velocity, byType);
            Assert.True(registry.TryGetId(Velocity.Info.StableId, out ComponentId byStableId));
            Assert.Equal(velocity, byStableId);
            Assert.True(registry.TryGetId(Frozen.Info.StableId, out ComponentId frozenByStableId));
            Assert.Equal(frozen, frozenByStableId);

            Assert.Equal(new ComponentType(position, Position.Info, 8, 4), registry.GetComponentType(position));
            Assert.Equal(new ComponentType(velocity, Velocity.Info, 8, 4), registry.GetComponentType(velocity));
            Assert.Equal(new ComponentType(frozen, Frozen.Info, 1, 1), registry.GetComponentType(frozen));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void TryGetId_Unregistered_ReturnsFalseAndDefault()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            ComponentRegistry registry = world.Components;
            _ = registry.Register<Position>();

            // Another world registers Unregistered, so it has a process-wide ordinal, but it isn't registered here.
            using (EcsWorld other = NewWorld(tracking))
            {
                _ = other.Components.Register<Unregistered>();
            }

            Assert.False(registry.TryGetId<Unregistered>(out ComponentId byType));
            Assert.Equal(default, byType);
            Assert.False(registry.TryGetId(Unregistered.Info.StableId, out ComponentId byStableId));
            Assert.Equal(default, byStableId);
            Assert.Equal(1, registry.Count);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void TryGetId_NeverRegisteredAnywhere_ReturnsFalse()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            // No test registers NeverRegistered, so its ordinal is still unassigned (0).
            Assert.False(world.Components.TryGetId<NeverRegistered>(out ComponentId id));
            Assert.Equal(default, id);
            Assert.Equal(0, ComponentOrdinal<NeverRegistered>.Value);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void TwoRegistries_Independent()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld first = NewWorld(tracking))
        using (EcsWorld second = NewWorld(tracking))
        {
            _ = first.Components.Register<Position>();
            _ = first.Components.Register<Velocity>();
            _ = second.Components.Register<Velocity>();
            _ = second.Components.Register<Frozen>();
            _ = second.Components.Register<Position>();

            Assert.True(first.Components.TryGetId<Velocity>(out ComponentId firstVelocity));
            Assert.True(second.Components.TryGetId<Velocity>(out ComponentId secondVelocity));
            Assert.Equal(1, firstVelocity.Index);
            Assert.Equal(0, secondVelocity.Index);
            Assert.True(first.Components.TryGetId<Position>(out ComponentId firstPosition));
            Assert.True(second.Components.TryGetId<Position>(out ComponentId secondPosition));
            Assert.Equal(0, firstPosition.Index);
            Assert.Equal(2, secondPosition.Index);
            Assert.False(first.Components.TryGetId<Frozen>(out _));
            Assert.False(first.Components.TryGetId(Frozen.Info.StableId, out _));
            Assert.True(second.Components.TryGetId(Position.Info.StableId, out ComponentId secondPositionByStableId));
            Assert.Equal(secondPosition, secondPositionByStableId);
            Assert.Equal(2, first.Components.Count);
            Assert.Equal(3, second.Components.Count);
            Assert.Equal(Velocity.Info, second.Components.GetComponentType(secondVelocity).Info);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Register_SizeAndAlignment_ComeFromType()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            ComponentRegistry registry = world.Components;

            ComponentType wide = registry.GetComponentType(registry.Register<Wide>());
            ComponentType vector = registry.GetComponentType(registry.Register<Packed>());
            ComponentType mixed = registry.GetComponentType(registry.Register<Mixed>());

            Assert.Equal((16, 8), (wide.Size, wide.Alignment));
            Assert.Equal((32, 16), (vector.Size, vector.Alignment));
            Assert.Equal((12, 4), (mixed.Size, mixed.Alignment));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Capacity_IsMaxComponentTypes()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld defaults = NewWorld(tracking))
        using (EcsWorld wide = new(new WorldOptions { Allocator = tracking, InitialEntityCapacity = 4, MaxComponentTypes = 512 }))
        {
            Assert.Equal(new WorldOptions().MaxComponentTypes, defaults.Components.Capacity);
            Assert.Equal(512, wide.Components.Capacity);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Register_Full_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = NewWorld(tracking, maxComponentTypes: 64))
        {
            ComponentRegistry registry = world.Components;
            ComponentId velocity = registry.Register<Velocity>();

            // Fill the rest through the registry's internal seam: there aren't 64 hand-written component types.
            for (int i = 1; i < registry.Capacity; i++)
            {
                ComponentInfo info = InfoFor($"Tests.Filler{i}");
                _ = registry.Add(ComponentOrdinal.Reserve(), in info, 4, 4);
            }

            Assert.Equal(64, registry.Count);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => registry.Register<Position>());

            Assert.Contains(Position.Info.FullName, exception.Message, StringComparison.Ordinal);
            Assert.Contains("WorldOptions.MaxComponentTypes", exception.Message, StringComparison.Ordinal);
            Assert.Contains("64", exception.Message, StringComparison.Ordinal);

            // The failed registration changed nothing, and a registered type still registers idempotently.
            Assert.Equal(64, registry.Count);
            Assert.False(registry.TryGetId<Position>(out _));
            Assert.False(registry.TryGetId(Position.Info.StableId, out _));
            Assert.Equal(velocity, registry.Register<Velocity>());
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void GetComponentType_IndexAtLeastCount_ThrowsArgumentOutOfRange()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        using (EcsWorld other = NewWorld(tracking))
        {
            _ = world.Components.Register<Position>();
            _ = other.Components.Register<Position>();
            ComponentId foreign = other.Components.Register<Velocity>();

            // An id from another registry is detected only when its index is past this registry's Count.
            ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => world.Components.GetComponentType(foreign));

            Assert.Equal("id", exception.ParamName);
            Assert.Equal(1, exception.ActualValue);
            Assert.Throws<ArgumentOutOfRangeException>(() => world.Components.GetComponentType(new ComponentId(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.Components.GetComponentType(new ComponentId(int.MaxValue)));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void TryGetId_DoesNotAllocate()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            ComponentRegistry registry = world.Components;
            _ = registry.Register<Position>();
            ComponentId velocity = registry.Register<Velocity>();
            StableComponentId stableId = Velocity.Info.StableId;
            StableComponentId missingStableId = Unregistered.Info.StableId;

            // Warm up every path, so the measured window holds no first-call work.
            int found = Lookups(registry, stableId, missingStableId, velocity);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000; i++)
            {
                found += Lookups(registry, stableId, missingStableId, velocity);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(3 * 1_001, found);
            Assert.Equal(0, allocated);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void ConcurrentWorlds_ConsistentIds()
    {
        int[][] ordinals = new int[RacingThreads][];

        RunRacing((slot, waitForStart) =>
        {
            TrackingAllocator tracking = NewTracking();
            using (EcsWorld world = NewWorld(tracking))
            {
                // Each world exists before the start, so the threads race on registration alone. Even threads
                // register forwards and odd ones backwards: different dense ids per world, and first uses of Racer0
                // and Racer7 race.
                waitForStart();
                ordinals[slot] = RegisterRacers(world.Components, reverse: slot % 2 == 1);
            }

            tracking.ThrowIfLeaks();
        });

        // Every thread saw the same process-wide ordinal for each type, and no two types share one.
        int[] expected = ordinals[0];
        Assert.Equal(RacerCount, new HashSet<int>(expected).Count);
        Assert.DoesNotContain(0, expected);
        foreach (int[] seen in ordinals)
        {
            Assert.Equal(expected, seen);
        }
    }

    [Fact]
    public void ComponentOrdinal_ConcurrentReserve_HandsOutEachOrdinalOnce()
    {
        int[][] reserved = new int[RacingThreads][];

        RunRacing((slot, waitForStart) =>
        {
            int[] mine = new int[10_000];
            waitForStart();
            for (int i = 0; i < mine.Length; i++)
            {
                mine[i] = ComponentOrdinal.Reserve();
            }

            reserved[slot] = mine;
        });

        HashSet<int> all = [];
        foreach (int[] mine in reserved)
        {
            foreach (int ordinal in mine)
            {
                Assert.True(ordinal > 0, $"Ordinal {ordinal} isn't positive.");
                Assert.True(all.Add(ordinal), $"Ordinal {ordinal} was handed out twice.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void OtherThread_Throws(string apiName)
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            ComponentId position = world.Components.Register<Position>();
            Action call = Call(world, world.Components, apiName, position);

            (Exception? failure, int threadId) = RunOnOtherThread(call);

            WrongThreadException exception = Assert.IsType<WrongThreadException>(failure);
            Assert.Equal(apiName.Split('(')[0], exception.ApiName);
            Assert.Equal(Environment.CurrentManagedThreadId, exception.ExpectedThreadId);
            Assert.Equal(threadId, exception.ActualThreadId);

            // The rejected call changed nothing.
            Assert.Equal(1, world.Components.Count);
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void AfterDispose_Throws(string apiName)
    {
        TrackingAllocator tracking = NewTracking();
        EcsWorld world = NewWorld(tracking);
        ComponentRegistry registry = world.Components;
        ComponentId position = registry.Register<Position>();
        world.Dispose();

        ObjectDisposedException exception = Assert.Throws<ObjectDisposedException>(Call(world, registry, apiName, position));

        string expected = apiName.StartsWith("EcsWorld.", StringComparison.Ordinal) ? typeof(EcsWorld).FullName! : typeof(ComponentRegistry).FullName!;
        Assert.Equal(expected, exception.ObjectName);
        tracking.ThrowIfLeaks();
    }

    private static Action Call(EcsWorld world, ComponentRegistry registry, string apiName, ComponentId id) => apiName switch
    {
        "EcsWorld.Components" => () => _ = world.Components,
        "ComponentRegistry.Count" => () => _ = registry.Count,
        "ComponentRegistry.Capacity" => () => _ = registry.Capacity,
        "ComponentRegistry.Register" => () => _ = registry.Register<Velocity>(),
        "ComponentRegistry.TryGetId" => () => _ = registry.TryGetId<Position>(out _),
        "ComponentRegistry.TryGetId(StableComponentId)" => () => _ = registry.TryGetId(Position.Info.StableId, out _),
        "ComponentRegistry.GetComponentType" => () => _ = registry.GetComponentType(id),
        _ => throw new ArgumentOutOfRangeException(nameof(apiName), apiName, "Unknown member."),
    };

    private static int Lookups(ComponentRegistry registry, StableComponentId stableId, StableComponentId missingStableId, ComponentId id)
    {
        int found = 0;
        found += registry.TryGetId<Velocity>(out _) ? 1 : 0;
        found += registry.TryGetId<Unregistered>(out _) ? 1 : 0;
        found += registry.TryGetId<NeverRegistered>(out _) ? 1 : 0;
        found += registry.TryGetId(stableId, out _) ? 1 : 0;
        found += registry.TryGetId(missingStableId, out _) ? 1 : 0;
        found += registry.GetComponentType(id).Size == 8 ? 1 : 0;
        return found;
    }

    /// <summary>
    /// Registers every racer type, forwards or backwards, checks the dense ids and lookups, and returns each racer's
    /// process-wide ordinal (index = racer number).
    /// </summary>
    private static int[] RegisterRacers(ComponentRegistry registry, bool reverse)
    {
        int[] ordinals = new int[RacerCount];
        for (int i = 0; i < RacerCount; i++)
        {
            ComponentId id = RegisterRacer(registry, reverse ? RacerCount - 1 - i : i);
            Assert.Equal(i, id.Index);
        }

        for (int racer = 0; racer < RacerCount; racer++)
        {
            (bool found, ComponentId id, ComponentInfo info, int ordinal) = LookUpRacer(registry, racer);
            Assert.True(found, $"Racer{racer} isn't registered.");
            Assert.Equal(reverse ? RacerCount - 1 - racer : racer, id.Index);
            Assert.Equal(info, registry.GetComponentType(id).Info);
            Assert.True(registry.TryGetId(info.StableId, out ComponentId byStableId));
            Assert.Equal(id, byStableId);
            ordinals[racer] = ordinal;
        }

        Assert.Equal(RacerCount, registry.Count);
        return ordinals;
    }

    /// <summary>
    /// Runs <paramref name="body"/> on <see cref="RacingThreads"/> threads at once, then checks that none failed. Each
    /// body calls its start callback when it is ready; the callback returns on every thread at about the same moment.
    /// Exceptions, failed assertions included, are caught on their thread and rethrown here, so they can't take down
    /// the test host. Every wait is bounded, so a failure can't hang the run.
    /// </summary>
    private static void RunRacing(Action<int, Action> body)
    {
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        ConcurrentQueue<Exception> failures = new();
        int[] released = [0];
        using Barrier start = new(RacingThreads);
        void WaitForStart()
        {
            // A thread that never arrives fails the others instead of hanging them.
            if (!start.SignalAndWait(TimeSpan.FromSeconds(30), cancellation))
            {
                throw new TimeoutException("Not every thread reached the barrier.");
            }

            // The barrier wakes threads microseconds apart; spinning until every thread is past it lines them up far
            // closer, so first uses of a type really overlap.
            _ = Interlocked.Increment(ref released[0]);
            SpinUntil(released, RacingThreads, cancellation);
        }

        var threads = new Thread[RacingThreads];
        for (int t = 0; t < RacingThreads; t++)
        {
            int slot = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    body(slot, WaitForStart);
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            })
            {
                IsBackground = true,
            };
        }

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "A racing thread didn't finish.");
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// Spins until <c>counter[0]</c> reaches <paramref name="target"/>, yielding now and then so it also finishes on
    /// machines with fewer cores than threads. Bounded: it throws after 30 seconds or on cancellation.
    /// </summary>
    private static void SpinUntil(int[] counter, int target, CancellationToken cancellation)
    {
        long deadline = Stopwatch.GetTimestamp() + (30 * Stopwatch.Frequency);
        SpinWait spinner = default;
        while (Volatile.Read(ref counter[0]) < target)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() > deadline)
            {
                throw new TimeoutException("Not every thread got past the barrier.");
            }

            spinner.SpinOnce(sleep1Threshold: -1);
        }
    }

    private static ComponentId RegisterRacer(ComponentRegistry registry, int racer) => racer switch
    {
        0 => registry.Register<Racer0>(),
        1 => registry.Register<Racer1>(),
        2 => registry.Register<Racer2>(),
        3 => registry.Register<Racer3>(),
        4 => registry.Register<Racer4>(),
        5 => registry.Register<Racer5>(),
        6 => registry.Register<Racer6>(),
        _ => registry.Register<Racer7>(),
    };

    private static (bool Found, ComponentId Id, ComponentInfo Info, int Ordinal) LookUpRacer(ComponentRegistry registry, int racer) => racer switch
    {
        0 => (registry.TryGetId<Racer0>(out ComponentId id0), id0, Racer0.Info, ComponentOrdinal<Racer0>.Value),
        1 => (registry.TryGetId<Racer1>(out ComponentId id1), id1, Racer1.Info, ComponentOrdinal<Racer1>.Value),
        2 => (registry.TryGetId<Racer2>(out ComponentId id2), id2, Racer2.Info, ComponentOrdinal<Racer2>.Value),
        3 => (registry.TryGetId<Racer3>(out ComponentId id3), id3, Racer3.Info, ComponentOrdinal<Racer3>.Value),
        4 => (registry.TryGetId<Racer4>(out ComponentId id4), id4, Racer4.Info, ComponentOrdinal<Racer4>.Value),
        5 => (registry.TryGetId<Racer5>(out ComponentId id5), id5, Racer5.Info, ComponentOrdinal<Racer5>.Value),
        6 => (registry.TryGetId<Racer6>(out ComponentId id6), id6, Racer6.Info, ComponentOrdinal<Racer6>.Value),
        _ => (registry.TryGetId<Racer7>(out ComponentId id7), id7, Racer7.Info, ComponentOrdinal<Racer7>.Value),
    };

    /// <summary>
    /// Runs <paramref name="action"/> on a new thread and waits for it, bounded so a hang fails the test instead of
    /// the run. Any exception is caught there and returned, so it can't escape the thread and take down the test host.
    /// </summary>
    private static (Exception? Failure, int ThreadId) RunOnOtherThread(Action action)
    {
        Exception? failure = null;
        int threadId = 0;
        Thread thread = new(() =>
        {
            threadId = Environment.CurrentManagedThreadId;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The other thread didn't finish.");
        return (failure, threadId);
    }

    /// <summary>A valid info for a data component named <paramref name="fullName"/>, as the generator would emit it.</summary>
    private static ComponentInfo InfoFor(string fullName, bool isTag = false) => new()
    {
        StableId = StableComponentId.FromFullName(fullName),
        SchemaVersion = 1,
        FullName = fullName,
        IsTag = isTag,
    };

    /// <summary>A fresh tracking allocator per world group, so each leak check sees only its own blocks.</summary>
    private static TrackingAllocator NewTracking() => new(new HeapAllocator(), "ComponentRegistry");

    private static EcsWorld NewWorld(TrackingAllocator tracking, int maxComponentTypes = 256) =>
        new(new WorldOptions { Allocator = tracking, InitialEntityCapacity = 4, MaxComponentTypes = maxComponentTypes });

    // Fixture components, hand-writing the Info the generator will emit. Each Info is built once and stored. The
    // primary constructors only give the fields a writer; the registry needs nothing but the layout.
    private struct Position(float x, float y) : IComponent
    {
        public float X = x;
        public float Y = y;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Position).FullName!);
    }

    private struct Velocity(float x, float y) : IComponent
    {
        public float X = x;
        public float Y = y;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Velocity).FullName!);
    }

    private struct Frozen : IComponent
    {
        public static ComponentInfo Info { get; } = InfoFor(typeof(Frozen).FullName!, isTag: true);
    }

    private struct Wide(byte flag, long ticks) : IComponent
    {
        public byte Flag = flag;
        public long Ticks = ticks;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Wide).FullName!);
    }

    private struct Packed(Vector128<float> first, Vector128<float> second) : IComponent
    {
        public Vector128<float> First = first;
        public Vector128<float> Second = second;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Packed).FullName!);
    }

    private struct Mixed(short kind, int count, byte flags) : IComponent
    {
        public short Kind = kind;
        public int Count = count;
        public byte Flags = flags;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Mixed).FullName!);
    }

    private struct Unregistered(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Unregistered).FullName!);
    }

    private struct NeverRegistered(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(typeof(NeverRegistered).FullName!);
    }

    private struct Racer0(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Racer0).FullName!);
    }

    private struct Racer1(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Racer1).FullName!);
    }

    private struct Racer2(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Racer2).FullName!);
    }

    private struct Racer3(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Racer3).FullName!);
    }

    private struct Racer4(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Racer4).FullName!);
    }

    private struct Racer5(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Racer5).FullName!);
    }

    private struct Racer6(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Racer6).FullName!);
    }

    private struct Racer7(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Racer7).FullName!);
    }
}

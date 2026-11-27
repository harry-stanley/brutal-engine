using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Queries;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// The query builder (docs/design/world.md, sections 5 and 11): every term, the term rules, one build per builder,
/// registration of new types, the owner-thread and disposal checks, and a failed build.
/// </summary>
public sealed class QueryBuilderTests
{
    /// <summary>The terms in the order of <see cref="_pairTable"/>'s rows and columns.</summary>
    private static readonly string[] _termNames = ["Read", "Write", "With", "Without", "OptionalRead", "OptionalWrite", "Changed", "Added"];

    /// <summary>
    /// What the second term does after the first on the same type, written out from the term rules: <c>D</c> a
    /// duplicate, <c>X</c> both required and excluded, <c>A</c> a second access term, <c>O</c> a filter on an optional
    /// type, <c>.</c> allowed. Row: the first term; column: the second (order of <see cref="_termNames"/>).
    /// </summary>
    private static readonly string[] _pairTable =
    [
        "DAAXAA..", // Read
        "ADAXAA..", // Write
        "AADXAA..", // With
        "XXXDAAXX", // Without
        "AAAADAOO", // OptionalRead
        "AAAAADOO", // OptionalWrite
        "...XOOD.", // Changed
        "...XOO.D", // Added
    ];

    public static TheoryData<string, string, string> MisuseRows()
    {
        TheoryData<string, string, string> rows = [];
        for (int first = 0; first < _termNames.Length; first++)
        {
            for (int second = 0; second < _termNames.Length; second++)
            {
                char rule = _pairTable[first][second];
                if (rule != '.')
                {
                    rows.Add($"{_termNames[first]}:A", $"{_termNames[second]}:A", rule.ToString());
                }
            }
        }

        // Rules against the type's merged flags, and a term on another type before the conflict.
        rows.Add("Read:A Changed:A", "Without:A", "X");
        rows.Add("Changed:A Added:A", "OptionalRead:A", "O");
        rows.Add("Write:A Added:A", "Added:A", "D");
        rows.Add("Without:B Read:A", "Without:A", "X");
        rows.Add("With:B OptionalWrite:A", "Changed:A", "O");
        return rows;
    }

    public static TheoryData<string, string> AllowedPairs()
    {
        TheoryData<string, string> rows = [];
        for (int first = 0; first < _termNames.Length; first++)
        {
            for (int second = 0; second < _termNames.Length; second++)
            {
                if (_pairTable[first][second] == '.')
                {
                    rows.Add(_termNames[first], _termNames[second]);
                }
            }
        }

        return rows;
    }

    public static TheoryData<string, string> AllPairs()
    {
        TheoryData<string, string> rows = [];
        foreach (string first in _termNames)
        {
            foreach (string second in _termNames)
            {
                rows.Add(first, second);
            }
        }

        return rows;
    }

    public static TheoryData<string> Terms() => [.. _termNames];

    public static TheoryData<string> Members() => [.. _termNames, "Build"];

    [Theory]
    [MemberData(nameof(MisuseRows))]
    public void Misuse_Throws(string before, string term, string rule)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            QueryBuilder builder = world.Query();
            Apply(builder, before);
            int position = world.Components.Register<Position>().Index;
            int health = world.Components.Register<Health>().Index;

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Apply(builder, term));

            string name = term.Split(':')[0];
            Assert.StartsWith($"QueryBuilder.{name}<{Position.Info.FullName}>: ", exception.Message, StringComparison.Ordinal);
            string reason = rule switch
            {
                "D" => "the query already has this term for the type.",
                "X" => "the type would be both required and excluded.",
                "A" => "the type already has an access term",
                "O" => "a Changed or Added filter requires the type, so it can't also be optional.",
                _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
            };
            Assert.Contains(reason, exception.Message, StringComparison.Ordinal);

            // The builder is unchanged: it still builds, with exactly the earlier terms.
            using Query query = builder.Build();
            Assert.Equal(Expected(before, position, health), Terms(query));
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [MemberData(nameof(AllowedPairs))]
    public void AllowedPair_OnOneType_MergesFlags(string first, string second)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            using Query query = Apply(world.Query(), $"{first}:A {second}:A").Build();
            int position = world.Components.Register<Position>().Index;

            Assert.Equal([new QueryTerm(position, Flag(first) | Flag(second))], Terms(query));
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void AnyPair_OnTwoTypes_KeepsBothTermsInOrder(string first, string second)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            using Query query = Apply(world.Query(), $"{first}:B {second}:A").Build();
            int position = world.Components.Register<Position>().Index;
            int health = world.Components.Register<Health>().Index;

            Assert.Equal([new QueryTerm(health, Flag(first)), new QueryTerm(position, Flag(second))], Terms(query));
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [MemberData(nameof(Members))]
    public void BuiltBuilder_EveryMember_Throws(string member)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            QueryBuilder builder = world.Query().Read<Position>();
            using Query query = builder.Build();

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Call(builder, member, "B"));

            Assert.Equal(
                $"QueryBuilder.{member}: the builder has built its query already; start another with EcsWorld.Query().",
                exception.Message);

            // The rejected call changed nothing: the built query keeps its one term, and no query was added.
            Assert.Equal(1, query.Terms.Count);
            Assert.Equal(1, world.LiveQueryCount);
            Assert.False(world.Components.TryGetId<Health>(out _));
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [MemberData(nameof(Terms))]
    public void Term_UnregisteredType_RegistersIt(string term)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = world.Components.Register<Position>();
            Assert.False(world.Components.TryGetId<Health>(out _));

            QueryBuilder builder = Apply(world.Query(), $"{term}:B");

            Assert.True(world.Components.TryGetId<Health>(out ComponentId health));
            Assert.Equal(1, health.Index);
            using Query query = builder.Build();
            Assert.Equal([new QueryTerm(health.Index, Flag(term))], Terms(query));
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [MemberData(nameof(Terms))]
    public void Term_RegistrationFails_ThrowsAndLeavesBuilderUnchanged(string term)
    {
        TrackingAllocator tracking = QueryTestSupport.NewTracking();

        // Every id of this world is taken by a made-up component, so registering Position fails.
        using (EcsWorld world = QueryTestSupport.NewWorld(tracking, _ => false))
        {
            QueryBuilder builder = world.Query();

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Apply(builder, $"{term}:A"));

            Assert.Contains("MaxComponentTypes", exception.Message, StringComparison.Ordinal);
            using Query query = builder.Build();
            Assert.Equal(0, query.Terms.Count);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Build_NoTerms_MatchesEveryArchetype()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = world.Spawn();
            _ = world.Spawn(new Position(1, 2));
            _ = world.Spawn(new Health(3), new Marker());
            using Query query = world.Query().Build();

            Assert.Equal(0, query.Terms.Count);
            query.Cache.Refresh();
            Assert.Equal(world.ArchetypeCount, query.Cache.Count);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Build_TickListGrowthFails_ThrowsAndBuildsAgain()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        FailingAllocator failing = new(tracking);
        using (EcsWorld world = EntityTestSupport.NewWorld(failing))
        {
            QueryBuilder builder = world.Query().Read<Position>();
            int live = tracking.LiveAllocationCount;
            failing.FailNext = true;

            _ = Assert.Throws<InsufficientMemoryException>(() => builder.Build());

            // Nothing was registered or linked, and nothing native is held.
            Assert.Equal(0, world.TickConsumers.Count);
            Assert.Equal(0, world.LiveQueryCount);
            Assert.Equal(live, tracking.LiveAllocationCount);

            // The builder is still unbuilt: it builds now, with its terms.
            using Query query = builder.Build();
            Assert.Equal(1, query.Terms.Count);
            Assert.Equal(1, world.TickConsumers.Count);
            Assert.Equal(1, world.LiveQueryCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [MemberData(nameof(Members))]
    [InlineData("EcsWorld.Query")]
    public void Member_OtherThread_Throws(string member)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            QueryBuilder builder = world.Query();
            string apiName = member == "EcsWorld.Query" ? member : "QueryBuilder." + member;
            Action call = member == "EcsWorld.Query" ? () => world.Query() : () => Call(builder, member, "A");

            Exception? failure = EntityTestSupport.RunOnOtherThread(call);

            WrongThreadException exception = Assert.IsType<WrongThreadException>(failure);
            Assert.Equal(apiName, exception.ApiName);

            // Nothing changed: the type wasn't registered, and the builder still builds on the owner thread.
            Assert.False(world.Components.TryGetId<Position>(out _));
            Assert.Equal(0, world.LiveQueryCount);
            using Query query = builder.Build();
            Assert.Equal(0, query.Terms.Count);
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [MemberData(nameof(Members))]
    [InlineData("EcsWorld.Query")]
    public void Member_WorldDisposed_Throws(string member)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        EcsWorld world = EntityTestSupport.NewWorld(tracking);
        QueryBuilder builder = world.Query();
        world.Dispose();

        ObjectDisposedException exception = member == "EcsWorld.Query"
            ? Assert.Throws<ObjectDisposedException>(() => world.Query())
            : Assert.Throws<ObjectDisposedException>(() => Call(builder, member, "A"));

        Assert.Equal(typeof(EcsWorld).FullName, exception.ObjectName);
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Query_EachCall_ReturnsANewBuilder()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            QueryBuilder first = world.Query();
            QueryBuilder second = world.Query();
            Assert.NotSame(first, second);

            // Building one leaves the other usable.
            using Query built = first.Read<Position>().Build();
            using Query other = second.Write<Position>().Build();
            Assert.Equal(TermFlags.Write, other.Terms[0].Flags);
            Assert.Equal(2, world.LiveQueryCount);
        }

        tracking.ThrowIfLeaks();
    }

    // Applies space-separated "Term:Type" tokens, type A being Position and B Health.
    private static QueryBuilder Apply(QueryBuilder builder, string terms)
    {
        foreach (string token in terms.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = token.Split(':');
            _ = Call(builder, parts[0], parts[1]);
        }

        return builder;
    }

    private static object Call(QueryBuilder builder, string member, string type) => type switch
    {
        "A" => Call<Position>(builder, member),
        "B" => Call<Health>(builder, member),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown type."),
    };

    private static object Call<T>(QueryBuilder builder, string member)
        where T : unmanaged, IComponent => member switch
        {
            "Read" => builder.Read<T>(),
            "Write" => builder.Write<T>(),
            "With" => builder.With<T>(),
            "Without" => builder.Without<T>(),
            "OptionalRead" => builder.OptionalRead<T>(),
            "OptionalWrite" => builder.OptionalWrite<T>(),
            "Changed" => builder.Changed<T>(),
            "Added" => builder.Added<T>(),
            "Build" => builder.Build(),
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, "Unknown member."),
        };

    // The flag a term name stands for, written out separately from the builder.
    private static TermFlags Flag(string term) => term switch
    {
        "Read" => TermFlags.Read,
        "Write" => TermFlags.Write,
        "With" => TermFlags.With,
        "Without" => TermFlags.Without,
        "OptionalRead" => TermFlags.OptionalRead,
        "OptionalWrite" => TermFlags.OptionalWrite,
        "Changed" => TermFlags.Changed,
        "Added" => TermFlags.Added,
        _ => throw new ArgumentOutOfRangeException(nameof(term), term, "Unknown term."),
    };

    // The terms "Term:Type" tokens should build: one per type, in first-mention order, flags merged.
    private static QueryTerm[] Expected(string terms, int position, int health)
    {
        List<QueryTerm> expected = [];
        foreach (string token in terms.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = token.Split(':');
            int component = parts[1] == "A" ? position : health;
            int at = expected.FindIndex(t => t.Component == component);
            if (at < 0)
            {
                expected.Add(new QueryTerm(component, Flag(parts[0])));
            }
            else
            {
                expected[at] = new QueryTerm(component, expected[at].Flags | Flag(parts[0]));
            }
        }

        return [.. expected];
    }

    private static QueryTerm[] Terms(Query query)
    {
        var terms = new QueryTerm[query.Terms.Count];
        for (int i = 0; i < terms.Length; i++)
        {
            terms[i] = query.Terms[i];
        }

        return terms;
    }
}

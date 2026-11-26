using System;
using System.Globalization;
using Xunit;

namespace BrutalEngine.World.Tests;

public sealed class WorldOptionsTests
{
    [Fact]
    public void Defaults_MatchTheDesign()
    {
        WorldOptions options = new();

        Assert.Null(options.Allocator);
        Assert.Equal(64 * 1024, options.ChunkBytes);
        Assert.Equal(1024, options.InitialEntityCapacity);
        Assert.Equal(1 << 24, options.MaxEntities);
        Assert.Equal(256, options.MaxComponentTypes);
        Assert.Equal(64, options.MaxHierarchyDepth);
        Assert.Same(TimeProvider.System, options.Clock);
        Assert.Equal("World", options.Name);
    }

    [Theory]
    [InlineData(nameof(WorldOptions.ChunkBytes), "0")]
    [InlineData(nameof(WorldOptions.ChunkBytes), "-65536")]
    [InlineData(nameof(WorldOptions.ChunkBytes), "-2147483648")]
    [InlineData(nameof(WorldOptions.ChunkBytes), "8192")]
    [InlineData(nameof(WorldOptions.ChunkBytes), "16383")]
    [InlineData(nameof(WorldOptions.ChunkBytes), "49152")]
    [InlineData(nameof(WorldOptions.ChunkBytes), "1048577")]
    [InlineData(nameof(WorldOptions.ChunkBytes), "2097152")]
    [InlineData(nameof(WorldOptions.InitialEntityCapacity), "0")]
    [InlineData(nameof(WorldOptions.InitialEntityCapacity), "-1")]
    [InlineData(nameof(WorldOptions.InitialEntityCapacity), "16777217")]
    [InlineData(nameof(WorldOptions.MaxEntities), "0")]
    [InlineData(nameof(WorldOptions.MaxEntities), "-1")]
    [InlineData(nameof(WorldOptions.MaxComponentTypes), "0")]
    [InlineData(nameof(WorldOptions.MaxComponentTypes), "-64")]
    [InlineData(nameof(WorldOptions.MaxComponentTypes), "63")]
    [InlineData(nameof(WorldOptions.MaxComponentTypes), "100")]
    [InlineData(nameof(WorldOptions.MaxComponentTypes), "4160")]
    [InlineData(nameof(WorldOptions.MaxHierarchyDepth), "0")]
    [InlineData(nameof(WorldOptions.MaxHierarchyDepth), "-1")]
    [InlineData(nameof(WorldOptions.Clock), null)]
    [InlineData(nameof(WorldOptions.Name), null)]
    [InlineData(nameof(WorldOptions.Name), "")]
    public void Invalid_ThrowsNamingOption(string option, string? value)
    {
        WorldOptions options = WithInvalid(option, value);

        ArgumentException exception = Assert.ThrowsAny<ArgumentException>(() => new EcsWorld(options));

        Assert.Equal("options", exception.ParamName);
        Assert.StartsWith($"WorldOptions.{option} ", exception.Message, StringComparison.Ordinal);
        if (option is nameof(WorldOptions.Clock) or nameof(WorldOptions.Name))
        {
            Assert.IsType<ArgumentException>(exception);
        }
        else
        {
            ArgumentOutOfRangeException outOfRange = Assert.IsType<ArgumentOutOfRangeException>(exception);
            Assert.Equal(int.Parse(value!, CultureInfo.InvariantCulture), outOfRange.ActualValue);
        }
    }

    [Fact]
    public void InitialEntityCapacity_AboveMaxEntities_ThrowsNamingInitialEntityCapacity()
    {
        WorldOptions options = new() { MaxEntities = 10, InitialEntityCapacity = 11 };

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => new EcsWorld(options));

        Assert.Equal("options", exception.ParamName);
        Assert.StartsWith("WorldOptions.InitialEntityCapacity ", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(16 * 1024, 1, 1, 64, 1)]
    [InlineData(1024 * 1024, 1, int.MaxValue, 4096, int.MaxValue)]
    [InlineData(32 * 1024, 100, 100, 128, 2)]
    public void Valid_Constructs(int chunkBytes, int initialEntityCapacity, int maxEntities, int maxComponentTypes, int maxHierarchyDepth)
    {
        WorldOptions options = new()
        {
            ChunkBytes = chunkBytes,
            InitialEntityCapacity = initialEntityCapacity,
            MaxEntities = maxEntities,
            MaxComponentTypes = maxComponentTypes,
            MaxHierarchyDepth = maxHierarchyDepth,
            Name = " ",
        };

        using EcsWorld world = new(options);

        Assert.Equal(" ", world.Name);
    }

    private static WorldOptions WithInvalid(string option, string? value) => option switch
    {
        nameof(WorldOptions.ChunkBytes) => new() { ChunkBytes = ParseInt(value) },
        nameof(WorldOptions.InitialEntityCapacity) => new() { InitialEntityCapacity = ParseInt(value) },
        nameof(WorldOptions.MaxEntities) => new() { MaxEntities = ParseInt(value) },
        nameof(WorldOptions.MaxComponentTypes) => new() { MaxComponentTypes = ParseInt(value) },
        nameof(WorldOptions.MaxHierarchyDepth) => new() { MaxHierarchyDepth = ParseInt(value) },
        nameof(WorldOptions.Clock) => new() { Clock = null! },
        nameof(WorldOptions.Name) => new() { Name = value! },
        _ => throw new ArgumentOutOfRangeException(nameof(option), option, "Unknown option."),
    };

    private static int ParseInt(string? value) => int.Parse(value!, CultureInfo.InvariantCulture);
}

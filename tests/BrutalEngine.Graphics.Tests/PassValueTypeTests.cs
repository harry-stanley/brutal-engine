using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>
/// The pass and draw value types of docs/design/graphics.md section 9: their signatures and defaults as the note writes
/// them, their memory layout, and their text forms.
/// </summary>
public sealed class PassValueTypeTests
{
    // Stands for "no default" in the expected parameter lists: a parameter without a default value.
    private const string Required = "(required)";

    public static TheoryData<string> ValueTypes() =>
    [
        "Viewport", "ScissorRect", "IndexFormat", "DrawArguments", "DrawIndexedArguments", "LoadOp", "StoreOp", "ColorValue",
    ];

    [Theory]
    [MemberData(nameof(ValueTypes))]
    public void Signatures_MatchDesign(string type)
    {
        // Written from the note's section 9 code block, not from the code: a record's primary-constructor parameters in
        // order (name, type, default), or an enum's members in order with their values.
        (string Name, Type Type, object Default)[]? record = type switch
        {
            "Viewport" =>
            [
                ("X", typeof(float), Required), ("Y", typeof(float), Required), ("Width", typeof(float), Required),
                ("Height", typeof(float), Required), ("MinDepth", typeof(float), 0f), ("MaxDepth", typeof(float), 1f),
            ],
            "ScissorRect" =>
            [
                ("X", typeof(int), Required), ("Y", typeof(int), Required), ("Width", typeof(int), Required),
                ("Height", typeof(int), Required),
            ],
            "DrawArguments" =>
            [
                ("VertexCount", typeof(int), Required), ("InstanceCount", typeof(int), 1), ("FirstVertex", typeof(int), 0),
                ("FirstInstance", typeof(int), 0),
            ],
            "DrawIndexedArguments" =>
            [
                ("IndexCount", typeof(int), Required), ("InstanceCount", typeof(int), 1), ("FirstIndex", typeof(int), 0),
                ("BaseVertex", typeof(int), 0), ("FirstInstance", typeof(int), 0),
            ],
            "ColorValue" =>
            [
                ("R", typeof(float), Required), ("G", typeof(float), Required), ("B", typeof(float), Required),
                ("A", typeof(float), Required),
            ],
            _ => null,
        };
        (string Name, int Value)[]? enumeration = type switch
        {
            "IndexFormat" => [("Uint16", 0), ("Uint32", 1)],
            "LoadOp" => [("Load", 0), ("Clear", 1), ("DontCare", 2)],
            "StoreOp" => [("Store", 0), ("DontCare", 1)],
            _ => null,
        };
        if (record is null && enumeration is null)
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown type.");
        }

        // Looked up by name, so a missing type is a test failure rather than a build break.
        Type? clrType = typeof(GraphicsDeviceOptions).Assembly.GetType("BrutalEngine.Graphics." + type);
        Assert.NotNull(clrType);
        Assert.True(clrType.IsPublic);
        Assert.True(clrType.IsValueType);

        if (enumeration is not null)
        {
            Assert.True(clrType.IsEnum);
            Assert.False(clrType.IsDefined(typeof(FlagsAttribute), inherit: false));
            Assert.Equal(typeof(int), Enum.GetUnderlyingType(clrType));
            Assert.Equal(enumeration, Enum.GetValuesAsUnderlyingType(clrType).Cast<int>().Select(v => (Enum.GetName(clrType, v)!, v)));
            return;
        }

        Assert.True(clrType.IsDefined(typeof(IsReadOnlyAttribute), inherit: false));
        // A record struct implements IEquatable<T> and its == and != operators.
        Assert.Contains(typeof(IEquatable<>).MakeGenericType(clrType), clrType.GetInterfaces());
        Assert.NotNull(clrType.GetMethod("op_Equality", [clrType, clrType]));

        ConstructorInfo[] constructors = clrType.GetConstructors();
        ConstructorInfo primary = Assert.Single(constructors);
        Assert.Equal(
            record!.Select(p => (p.Name, p.Type, p.Default)),
            primary.GetParameters().Select(p => (p.Name!, p.ParameterType, p.HasDefaultValue ? p.DefaultValue! : Required)));

        // Every parameter is an init-only property of the same type, and those are the only instance properties.
        PropertyInfo[] properties = clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(record!.Select(p => p.Name).Order(StringComparer.Ordinal), properties.Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach ((string name, Type propertyType, _) in record!)
        {
            PropertyInfo property = clrType.GetProperty(name)!;
            Assert.Equal(propertyType, property.PropertyType);
            Assert.Contains(typeof(IsExternalInit), property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        }
    }

    [Fact]
    public void Defaults_MatchDesign()
    {
        Viewport viewport = new(1, 2, 3, 4);
        DrawArguments draw = new(3);
        DrawIndexedArguments drawIndexed = new(6);

        Assert.Equal((1f, 2f, 3f, 4f, 0f, 1f), (viewport.X, viewport.Y, viewport.Width, viewport.Height, viewport.MinDepth, viewport.MaxDepth));
        Assert.Equal((3, 1, 0, 0), (draw.VertexCount, draw.InstanceCount, draw.FirstVertex, draw.FirstInstance));
        Assert.Equal(
            (6, 1, 0, 0, 0),
            (drawIndexed.IndexCount, drawIndexed.InstanceCount, drawIndexed.FirstIndex, drawIndexed.BaseVertex, drawIndexed.FirstInstance));
        Assert.Equal(IndexFormat.Uint16, default);
        Assert.Equal(LoadOp.Load, default);
        Assert.Equal(StoreOp.Store, default);
        Assert.Equal(new ColorValue(0, 0, 0, 0), default);
        Assert.Equal(new ScissorRect(0, 0, 0, 0), default);
    }

    [Fact]
    public void Constructors_KeepEveryField()
    {
        Viewport viewport = new(1.5f, -2.5f, 640, 480, 0.25f, 0.75f);
        ScissorRect scissor = new(-1, 2, 30, 40);
        DrawArguments draw = new(3, 4, 5, 6);
        DrawIndexedArguments drawIndexed = new(7, 8, 9, -10, 11);
        ColorValue color = new(0.1f, 0.2f, 0.3f, 0.4f);

        Assert.Equal(
            (1.5f, -2.5f, 640f, 480f, 0.25f, 0.75f),
            (viewport.X, viewport.Y, viewport.Width, viewport.Height, viewport.MinDepth, viewport.MaxDepth));
        Assert.Equal((-1, 2, 30, 40), (scissor.X, scissor.Y, scissor.Width, scissor.Height));
        Assert.Equal((3, 4, 5, 6), (draw.VertexCount, draw.InstanceCount, draw.FirstVertex, draw.FirstInstance));
        Assert.Equal(
            (7, 8, 9, -10, 11),
            (drawIndexed.IndexCount, drawIndexed.InstanceCount, drawIndexed.FirstIndex, drawIndexed.BaseVertex, drawIndexed.FirstInstance));
        Assert.Equal((0.1f, 0.2f, 0.3f, 0.4f), (color.R, color.G, color.B, color.A));
    }

    [Fact]
    public void DrawArguments_Layouts_MatchIndirectDrawRecords()
    {
        // Section 9: the draw arguments' layout matches the GPU's indirect-draw records, so indirect draws can reuse it.
        // Vulkan's VkDrawIndirectCommand is { vertexCount, instanceCount, firstVertex, firstInstance } (16 bytes) and
        // VkDrawIndexedIndirectCommand { indexCount, instanceCount, firstIndex, vertexOffset, firstInstance } (20 bytes):
        // BRUTAL's bindings, Brutal.Vulkan/Generated/Structs/VkDrawIndirectCommand.gen.cs:15-24 and
        // VkDrawIndexedIndirectCommand.gen.cs:15-26.
        DrawArguments draw = new(1, 2, 3, 4);
        DrawIndexedArguments drawIndexed = new(5, 6, 7, -8, 9);

        Assert.Equal(16, Unsafe.SizeOf<DrawArguments>());
        Assert.Equal(20, Unsafe.SizeOf<DrawIndexedArguments>());
        Assert.Equal([1, 2, 3, 4], MemoryMarshal.Cast<DrawArguments, int>(new ReadOnlySpan<DrawArguments>(in draw)).ToArray());
        Assert.Equal(
            [5, 6, 7, -8, 9],
            MemoryMarshal.Cast<DrawIndexedArguments, int>(new ReadOnlySpan<DrawIndexedArguments>(in drawIndexed)).ToArray());
    }

    public static TheoryData<string> Records() => ["Viewport", "ScissorRect", "DrawArguments", "DrawIndexedArguments", "ColorValue"];

    [Theory]
    [MemberData(nameof(Records))]
    public void ToString_UnderAnotherCulture_IsInvariant(string type)
    {
        (object value, string expected) = type switch
        {
            "Viewport" => (
                (object)new Viewport(-1.5f, 2.25f, 640, 480, float.NaN, float.PositiveInfinity),
                "Viewport { X = -1.5, Y = 2.25, Width = 640, Height = 480, MinDepth = NaN, MaxDepth = Infinity }"),
            "ScissorRect" => (new ScissorRect(-1, -2, 30, 40), "ScissorRect { X = -1, Y = -2, Width = 30, Height = 40 }"),
            "DrawArguments" => (
                new DrawArguments(-3, 1000, -5, 6),
                "DrawArguments { VertexCount = -3, InstanceCount = 1000, FirstVertex = -5, FirstInstance = 6 }"),
            "DrawIndexedArguments" => (
                new DrawIndexedArguments(1000, 2, -3, -4, 5),
                "DrawIndexedArguments { IndexCount = 1000, InstanceCount = 2, FirstIndex = -3, BaseVertex = -4, FirstInstance = 5 }"),
            "ColorValue" => (
                new ColorValue(0.5f, -0.25f, float.NegativeInfinity, 1),
                "ColorValue { R = 0.5, G = -0.25, B = -Infinity, A = 1 }"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown record."),
        };

        // A clone of the invariant culture with every symbol a number format uses changed (no OS culture data needed):
        // any culture-sensitive formatting shows in the output.
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "~";
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = "'";
        culture.NumberFormat.NaNSymbol = "nan!";
        culture.NumberFormat.PositiveInfinitySymbol = "inf!";
        culture.NumberFormat.NegativeInfinitySymbol = "-inf!";
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            Assert.Equal("~1,5", (-1.5f).ToString(CultureInfo.CurrentCulture));
            Assert.Equal(expected, value.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}

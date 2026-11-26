using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The buffer description's equality and text form (docs/design/graphics.md section 5).</summary>
public sealed class BufferDescriptionTests
{
    private static readonly BufferDescription _description = new()
    {
        Size = -65536,
        Usage = BufferUsage.Vertex | BufferUsage.CopyDestination,
        Memory = MemoryIntent.Upload,
        DebugName = "sprites",
    };

    [Fact]
    public void ToString_UnderAnotherCulture_ListsEveryFieldInvariantly()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        // Swedish writes the minus sign as U+2212, so a culture-sensitive format of the (invalid) negative size shows.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            Assert.NotEqual("-1", (-1L).ToString(CultureInfo.CurrentCulture));
            Assert.Equal(
                "BufferDescription { Size = -65536, Usage = Vertex, CopyDestination, Memory = Upload, DebugName = sprites }",
                _description.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ToString_NoDebugName_LeavesItEmpty()
    {
        Assert.Equal(
            "BufferDescription { Size = 4, Usage = Index, Memory = GpuOnly, DebugName =  }",
            new BufferDescription { Size = 4, Usage = BufferUsage.Index }.ToString());
    }

    // One row per BufferDescription property; Equals_FieldRows_CoverEveryProperty fails until a new property adds its row.
    private static readonly string[] _fields =
    [
        nameof(BufferDescription.Size),
        nameof(BufferDescription.Usage),
        nameof(BufferDescription.Memory),
        nameof(BufferDescription.DebugName),
    ];

    public static TheoryData<string> Fields() => [.. _fields];

    [Theory]
    [MemberData(nameof(Fields))]
    public void Equals_ComparesEveryField(string field)
    {
        BufferDescription changed = field switch
        {
            nameof(BufferDescription.Size) => _description with { Size = 65536 },
            nameof(BufferDescription.Usage) => _description with { Usage = BufferUsage.Vertex },
            nameof(BufferDescription.Memory) => _description with { Memory = MemoryIntent.GpuOnly },
            // Ordinal: a name that differs only in case is a different name.
            nameof(BufferDescription.DebugName) => _description with { DebugName = "Sprites" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field."),
        };

        Assert.NotEqual(_description, changed);
        Assert.True(_description != changed);
    }

    [Fact]
    public void Equals_FieldRows_CoverEveryProperty()
    {
        Assert.Equal(
            typeof(BufferDescription).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order(StringComparer.Ordinal),
            _fields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Equals_SameFields_AreEqual()
    {
        // A separately built name, so equality can't be passing by reference.
        BufferDescription copy = _description with { DebugName = new string("sprites".AsSpan()) };

        Assert.Equal(_description, copy);
        Assert.True(_description == copy);
        Assert.Equal(_description.GetHashCode(), copy.GetHashCode());
    }
}

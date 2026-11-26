using System;
using System.Globalization;
using System.Reflection;
using BrutalEngine.Platform.Vfs;
using CsCheck;
using Xunit;

namespace BrutalEngine.Platform.Tests.Vfs;

public sealed class VfsEntryTests
{
    [Fact]
    public void Equality_AndToString_AreInvariant()
    {
        // Equality compares every property: flip each of them on its own.
        Gen.Select(Gen.Int[0, 1], Gen.Int[0, 1], Gen.Long[0, 1], Gen.Int[0, 7]).Sample((name, kind, length, flips) =>
        {
            VfsEntry left = Create(name, kind, length);
            VfsEntry right = Create(name ^ (flips & 1), kind ^ ((flips >> 1) & 1), length ^ ((flips >> 2) & 1));
            bool expected = left.Name == right.Name && left.Kind == right.Kind && left.Length == right.Length;

            Assert.Equal(expected, left == right);
            Assert.Equal(!expected, left != right);
            Assert.Equal(expected, left.Equals((object)right));
            if (expected)
            {
                Assert.Equal(left.GetHashCode(), right.GetHashCode());
            }
        });

        // Names compare ordinally: no case folding, no Unicode normalisation.
        Assert.NotEqual(new VfsEntry("a", VfsEntryKind.File, 1), new VfsEntry("A", VfsEntryKind.File, 1));
        Assert.NotEqual(new VfsEntry("\u00E9", VfsEntryKind.File, 1), new VfsEntry("e\u0301", VfsEntryKind.File, 1));

        // A clone of the invariant culture with every symbol a number format uses changed (no OS culture data
        // needed): any culture-sensitive formatting would show in the output.
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "~";
        culture.NumberFormat.NumberGroupSeparator = "'";
        culture.NumberFormat.NumberDecimalSeparator = ",";
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            Assert.Equal(
                "VfsEntry { Name = hero.png, Kind = File, Length = 9223372036854775807 }",
                new VfsEntry("hero.png", VfsEntryKind.File, long.MaxValue).ToString());
            Assert.Equal(
                "VfsEntry { Name = textures, Kind = Directory, Length = 0 }",
                new VfsEntry("textures", VfsEntryKind.Directory, 0).ToString());
            Assert.Equal("VfsEntry { Name = , Kind = None, Length = 0 }", default(VfsEntry).ToString());

            // The constructor keeps every number non-negative, and non-negative integers print the same in every
            // culture, so only a value written past the constructor shows whether ToString uses the invariant
            // culture. Write a negative length into the compiler's backing field through reflection to see it.
            object boxed = new VfsEntry("hero.png", VfsEntryKind.File, 1);
            FieldInfo length = typeof(VfsEntry).GetField("<Length>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("VfsEntry has no Length backing field.");
            length.SetValue(boxed, -1024L);
            Assert.Equal("~1024", (-1024L).ToString(CultureInfo.CurrentCulture));
            Assert.Equal("VfsEntry { Name = hero.png, Kind = File, Length = -1024 }", ((VfsEntry)boxed).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Directory_HasLengthZero()
    {
        VfsEntry directory = new("textures", VfsEntryKind.Directory, 0);
        Assert.Equal(0, directory.Length);

        foreach (long length in new[] { 1L, long.MaxValue, -1L, long.MinValue })
        {
            ArgumentException exception = Assert.ThrowsAny<ArgumentException>(() => new VfsEntry("textures", VfsEntryKind.Directory, length));
            Assert.Equal("Length", exception.ParamName);
        }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void Constructor_FileWithNonNegativeLength_StoresEveryProperty(long length)
    {
        VfsEntry entry = new("hero.png", VfsEntryKind.File, length);

        Assert.Equal("hero.png", entry.Name);
        Assert.Equal(VfsEntryKind.File, entry.Kind);
        Assert.Equal(length, entry.Length);
        (string name, VfsEntryKind kind, long deconstructed) = entry;
        Assert.Equal(("hero.png", VfsEntryKind.File, length), (name, kind, deconstructed));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Constructor_NegativeFileLength_Throws(long length)
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => new VfsEntry("hero.png", VfsEntryKind.File, length));

        Assert.Equal("Length", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullName_Throws()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => new VfsEntry(null!, VfsEntryKind.File, 0));

        Assert.Equal("Name", exception.ParamName);
    }

    [Fact]
    public void Constructor_EmptyName_Throws()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => new VfsEntry(string.Empty, VfsEntryKind.File, 0));

        Assert.Equal("Name", exception.ParamName);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(".hidden")]
    [InlineData("\U0001F600")]
    public void Constructor_UnusualButNonEmptyName_IsKept(string name)
    {
        Assert.Equal(name, new VfsEntry(name, VfsEntryKind.File, 0).Name);
    }

    [Theory]
    [InlineData(VfsEntryKind.None)]
    [InlineData((VfsEntryKind)(-1))]
    [InlineData((VfsEntryKind)3)]
    [InlineData((VfsEntryKind)int.MinValue)]
    [InlineData((VfsEntryKind)int.MaxValue)]
    public void Constructor_KindNotFileOrDirectory_Throws(VfsEntryKind kind)
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => new VfsEntry("hero.png", kind, 0));

        Assert.Equal("Kind", exception.ParamName);
    }

    [Fact]
    public void Default_HasEmptyNameNoneKindAndZeroLength()
    {
        VfsEntry entry = default;

        Assert.Equal(string.Empty, entry.Name);
        Assert.Equal(VfsEntryKind.None, entry.Kind);
        Assert.Equal(0, entry.Length);
        Assert.NotEqual(new VfsEntry("a", VfsEntryKind.Directory, 0), entry);
    }

    private static VfsEntry Create(int name, int kind, long length) =>
        kind == 0
            ? new VfsEntry(name == 0 ? "a" : "b", VfsEntryKind.File, length)
            : new VfsEntry(name == 0 ? "a" : "b", VfsEntryKind.Directory, 0);
}

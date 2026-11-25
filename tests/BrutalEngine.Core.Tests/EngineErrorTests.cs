using System;
using CsCheck;
using Xunit;

namespace BrutalEngine.Core.Tests;

public sealed class EngineErrorTests
{
    [Fact]
    public void Constructor_StoresKindMessageAndNativeCode()
    {
        EngineError error = new(ErrorKind.NotFound, "Asset 'hero.png' not found.", 2);

        Assert.Equal(ErrorKind.NotFound, error.Kind);
        Assert.Equal("Asset 'hero.png' not found.", error.Message);
        Assert.Equal(2, error.NativeCode);
    }

    [Fact]
    public void Constructor_NullMessage_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new EngineError(ErrorKind.Io, null!));
    }

    [Fact]
    public void Default_IsUnspecifiedWithEmptyMessage()
    {
        EngineError error = default;

        Assert.Equal(ErrorKind.Unspecified, error.Kind);
        Assert.Equal(string.Empty, error.Message);
        Assert.Equal(0, error.NativeCode);
    }

    [Fact]
    public void Native_SetsKindAndCode()
    {
        var error = EngineError.Native(-4, "vkCreateDevice failed.");

        Assert.Equal(ErrorKind.Native, error.Kind);
        Assert.Equal(-4, error.NativeCode);
        Assert.Equal("vkCreateDevice failed.", error.Message);
    }

    [Fact]
    public void Equality_ComparesAllFields()
    {
        EngineError a = new(ErrorKind.Io, "read failed", 5);

        Assert.True(a == new EngineError(ErrorKind.Io, "read failed", 5));
        Assert.True(a != new EngineError(ErrorKind.Timeout, "read failed", 5));
        Assert.True(a != new EngineError(ErrorKind.Io, "write failed", 5));
        Assert.True(a != new EngineError(ErrorKind.Io, "read failed", 6));
        Assert.True(a.Equals((object)new EngineError(ErrorKind.Io, "read failed", 5)));
        Assert.False(a.Equals("read failed"));
        Assert.True(default(EngineError) == new EngineError(ErrorKind.Unspecified, string.Empty));
    }

    [Fact]
    public void EqualErrors_HaveEqualHashCodes()
    {
        Gen.Select(Gen.Enum<ErrorKind>(), Gen.String, Gen.Int).Sample((kind, message, code) =>
        {
            EngineError a = new(kind, message, code);
            EngineError b = new(kind, new string(message.AsSpan()), code);
            return a == b && a.GetHashCode() == b.GetHashCode();
        });
    }

    [Fact]
    public void ToString_IncludesNativeCodeOnlyWhenPresent()
    {
        Assert.Equal("NotFound: missing", new EngineError(ErrorKind.NotFound, "missing").ToString());
        Assert.Equal("Native: boom (native code -3)", EngineError.Native(-3, "boom").ToString());
    }
}

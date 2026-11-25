using System;
using Xunit;

namespace BrutalEngine.Core.Tests;

public sealed class WrongThreadExceptionTests
{
    [Fact]
    public void StandardConstructors_LeaveApiNameEmptyAndIdsZero()
    {
        InvalidOperationException inner = new("inner");
        WrongThreadException[] exceptions =
        [
            new(),
            new("custom message"),
            new("custom message", inner),
        ];

        foreach (WrongThreadException exception in exceptions)
        {
            Assert.Equal(string.Empty, exception.ApiName);
            Assert.Equal(0, exception.ExpectedThreadId);
            Assert.Equal(0, exception.ActualThreadId);
        }

        Assert.False(string.IsNullOrWhiteSpace(exceptions[0].Message));
        Assert.Null(exceptions[0].InnerException);
        Assert.Equal("custom message", exceptions[1].Message);
        Assert.Null(exceptions[1].InnerException);
        Assert.Equal("custom message", exceptions[2].Message);
        Assert.Same(inner, exceptions[2].InnerException);
    }

    [Fact]
    public void Constructor_ApiNameAndIds_MessageNamesAll()
    {
        WrongThreadException exception = new("Window.SetTitle", 12345, 67890);

        Assert.Equal("Window.SetTitle", exception.ApiName);
        Assert.Equal(12345, exception.ExpectedThreadId);
        Assert.Equal(67890, exception.ActualThreadId);
        Assert.Contains("Window.SetTitle", exception.Message, StringComparison.Ordinal);
        Assert.Contains("12345", exception.Message, StringComparison.Ordinal);
        Assert.Contains("67890", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Constructor_NullApiName_ThrowsArgumentNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new WrongThreadException(null!, 1, 2));

        Assert.Equal("apiName", exception.ParamName);
    }

    [Fact]
    public void Constructor_EmptyApiName_IsAccepted()
    {
        WrongThreadException exception = new(string.Empty, 1, 2);

        Assert.Equal(string.Empty, exception.ApiName);
        Assert.Equal(1, exception.ExpectedThreadId);
        Assert.Equal(2, exception.ActualThreadId);
    }

    [Fact]
    public void WrongThreadException_IsInvalidOperationException()
    {
        Assert.IsAssignableFrom<InvalidOperationException>(new WrongThreadException("Api", 1, 2));
    }
}

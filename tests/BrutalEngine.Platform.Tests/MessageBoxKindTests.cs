using System;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class MessageBoxKindTests
{
    [Fact]
    public void Values_MatchDesign()
    {
        Assert.Equal(0, (int)MessageBoxKind.Information);
        Assert.Equal(1, (int)MessageBoxKind.Warning);
        Assert.Equal(2, (int)MessageBoxKind.Error);
        Assert.Equal(["Information", "Warning", "Error"], Enum.GetNames<MessageBoxKind>());
    }
}

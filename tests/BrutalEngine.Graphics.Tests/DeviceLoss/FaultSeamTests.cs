using System;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Internal;
using Xunit;

namespace BrutalEngine.Graphics.Tests.DeviceLoss;

/// <summary>The fault seam's arming rules and occurrence counting (docs/design/graphics.md section 12).</summary>
public sealed class FaultSeamTests
{
    private static readonly EngineError _loss = new(ErrorKind.Unavailable, "lost", -4);

    // The seam's operations by value: the enum is internal, so a public theory can't take it.
    public static TheoryData<int> Operations() => [.. Enum.GetValues<FaultSeam.Operation>().Select(operation => (int)operation)];

    [Fact]
    public void Unarmed_TakesNothing()
    {
        var seam = new FaultSeam();

        Assert.All(Enum.GetValues<FaultSeam.Operation>(), operation =>
        {
            Assert.False(seam.TryTake(operation, out EngineError loss));
            Assert.Equal(default, loss);
        });
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void Armed_TakesTheNthCallOfItsOperationOnly(int value)
    {
        var operation = (FaultSeam.Operation)value;
        var seam = new FaultSeam();
        seam.InjectLoss(operation, 3, _loss);
        FaultSeam.Operation other = operation == FaultSeam.Operation.WaitForFrame ? FaultSeam.Operation.CreateSwapchain : FaultSeam.Operation.WaitForFrame;

        // Calls of other operations never count.
        for (int i = 0; i < 5; i++)
        {
            Assert.False(seam.TryTake(other, out _));
        }

        Assert.False(seam.TryTake(operation, out _));
        Assert.False(seam.TryTake(operation, out _));
        Assert.True(seam.TryTake(operation, out EngineError taken));

        Assert.Equal(_loss, taken);
        // Taken once: the seam is disarmed.
        Assert.False(seam.TryTake(operation, out _));
    }

    [Fact]
    public void Arming_ReplacesTheArmedLoss()
    {
        var seam = new FaultSeam();
        seam.InjectLoss(FaultSeam.Operation.Execute, 1, _loss);
        var timeout = new EngineError(ErrorKind.Timeout, "hung", 2);

        seam.InjectLoss(FaultSeam.Operation.WaitIdle, 2, timeout);

        Assert.False(seam.TryTake(FaultSeam.Operation.Execute, out _));
        Assert.False(seam.TryTake(FaultSeam.Operation.WaitIdle, out _));
        Assert.True(seam.TryTake(FaultSeam.Operation.WaitIdle, out EngineError taken));
        Assert.Equal(timeout, taken);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(12)]
    [InlineData(int.MinValue)]
    public void InjectLoss_UndefinedOperation_Throws(int operation)
    {
        var seam = new FaultSeam();

        Assert.Throws<ArgumentOutOfRangeException>(() => seam.InjectLoss((FaultSeam.Operation)operation, 1, _loss));

        Assert.All(Enum.GetValues<FaultSeam.Operation>(), defined => Assert.False(seam.TryTake(defined, out _)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InjectLoss_OccurrenceBelowOne_Throws(int occurrence)
    {
        var seam = new FaultSeam();

        Assert.Throws<ArgumentOutOfRangeException>(() => seam.InjectLoss(FaultSeam.Operation.Execute, occurrence, _loss));

        Assert.False(seam.TryTake(FaultSeam.Operation.Execute, out _));
    }

    public static TheoryData<int, ErrorKind> Kinds()
    {
        TheoryData<int, ErrorKind> rows = [];
        foreach (FaultSeam.Operation operation in Enum.GetValues<FaultSeam.Operation>())
        {
            foreach (ErrorKind kind in Enum.GetValues<ErrorKind>())
            {
                rows.Add((int)operation, kind);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void InjectLoss_AcceptsOnlyALossTheCallCanReport(int value, ErrorKind kind)
    {
        var operation = (FaultSeam.Operation)value;
        var seam = new FaultSeam();
        var loss = new EngineError(kind, "loss", 3);
        bool status = operation is FaultSeam.Operation.Present or FaultSeam.Operation.AcquireImage;
        bool accepted = kind == ErrorKind.Unavailable || (kind == ErrorKind.Timeout && !status);

        if (accepted)
        {
            seam.InjectLoss(operation, 1, loss);
            Assert.True(seam.TryTake(operation, out EngineError taken));
            Assert.Equal(loss, taken);
        }
        else
        {
            ArgumentException thrown = Assert.Throws<ArgumentException>(() => seam.InjectLoss(operation, 1, loss));
            Assert.Equal("loss", thrown.ParamName);
            Assert.False(seam.TryTake(operation, out _));
        }
    }
}

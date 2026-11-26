using System;
using System.Runtime.InteropServices;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Instances;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Validation;

public sealed class ValidationSinkTests
{
    [Fact]
    public void Release_Twice_FreesOnce()
    {
        ValidationSink.HandleTracker tracker = new();
        ValidationSink sink = new(default, ReadOnlyMemory<ValidationAllowance>.Empty, tracker);
        nint userData = sink.UserData;

        Assert.NotEqual(0, userData);
        Assert.Same(sink, GCHandle.FromIntPtr(userData).Target);
        Assert.False(sink.IsReleased);
        Assert.Equal(1, tracker.Live);

        sink.Release();
        sink.Release();

        // A second free of the same handle would corrupt the runtime's handle table; the tracker would go negative.
        Assert.True(sink.IsReleased);
        Assert.Equal(0, sink.UserData);
        Assert.Equal(0, tracker.Live);
    }

    [Fact]
    public void Callback_ZeroOrReleasedUserData_ReturnsFalse()
    {
        ValidationLog log = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(log);
        using (logger)
        {
            ValidationSink sink = new(category, ReadOnlyMemory<ValidationAllowance>.Empty);
            nint userData = sink.UserData;
            sink.Release();

            Assert.False(FakeMessage.Send(0, FakeMessage.Error, FakeMessage.Validation));
            Assert.False(FakeMessage.Send(userData, FakeMessage.Error, FakeMessage.Validation));

            Assert.Equal(0, sink.Errors);
            Assert.Equal(0, sink.Warnings);
            Assert.Empty(log.Entries);
        }
    }

    [Fact]
    public void TwoSinks_CountSeparately()
    {
        ValidationLog log = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(log);
        using (logger)
        {
            ValidationSink first = new(category, ReadOnlyMemory<ValidationAllowance>.Empty);
            ValidationSink second = new(category, ReadOnlyMemory<ValidationAllowance>.Empty);
            try
            {
                _ = FakeMessage.Send(first.UserData, FakeMessage.Error, FakeMessage.Validation);
                _ = FakeMessage.Send(first.UserData, FakeMessage.Error, FakeMessage.Validation);
                _ = FakeMessage.Send(second.UserData, FakeMessage.Warning, FakeMessage.Validation);

                Assert.Equal((2, 0), (first.Errors, first.Warnings));
                Assert.Equal((0, 1), (second.Errors, second.Warnings));
                Assert.NotEqual(first.UserData, second.UserData);
            }
            finally
            {
                first.Release();
                second.Release();
            }
        }
    }

    [Fact]
    public void AllowList_CopiedAtConstruction()
    {
        ValidationLog log = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(log);
        using (logger)
        {
            ValidationAllowance[] allowList = [new(FakeMessage.Id, "Tolerated.")];
            ValidationSink sink = new(category, allowList);
            try
            {
                allowList[0] = new ValidationAllowance(FakeMessage.Id + 1, "Changed later.");

                _ = FakeMessage.Send(sink.UserData, FakeMessage.Error, FakeMessage.Validation);

                Assert.Equal(1, sink.Allowed);
                Assert.Equal(0, sink.Errors);
            }
            finally
            {
                sink.Release();
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Constructor_AllowanceWithoutReason_Throws(string? reason)
    {
        ValidationSink.HandleTracker tracker = new();
        ValidationAllowance[] allowList = [new(1, "Fine."), new(2, reason!)];

        ArgumentException exception = Assert.Throws<ArgumentException>("allowList", () => new ValidationSink(default, allowList, tracker));

        Assert.Contains("entry 1 (message id 2)", exception.Message, StringComparison.Ordinal);

        // Rejected before the handle exists, so nothing leaks.
        Assert.Equal(0, tracker.Live);
    }
}

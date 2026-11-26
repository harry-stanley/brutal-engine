using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Instances;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Validation;

// Not parallel with other tests: the runtime hands a freed handle's slot to the next GCHandle.Alloc on any thread, so a
// sink created by a parallel test could receive the released-handle message sent here.
[Collection(ValidationHandleTests.Name)]
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

    [Fact]
    [Trait("Category", "Gpu")]
    public void TwoInstances_CountSeparately()
    {
        ValidationLog log = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(log);
        using (logger)
        {
            using VulkanInstance first = GpuValidation.Create(category);
            using VulkanInstance second = GpuValidation.Create(category);
            Assert.Equal((0, 0), (first.ValidationErrors, second.ValidationErrors));

            GpuValidation.CreateMessengerWithoutSeverity(first);

            Assert.Equal(1, first.ValidationErrors);
            Assert.Equal(0, second.ValidationErrors);
            Assert.Equal(0, second.ValidationWarnings);
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("VUID-VkDebugUtilsMessengerCreateInfoEXT-messageSeverity-", StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void AllowList_SuppressesCount()
    {
        ValidationLog log = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(log);
        using (logger)
        {
            // The layer's id for the message, read from a first instance, so no layer version's hash is hard-coded.
            int id;
            using (VulkanInstance probe = GpuValidation.Create(category))
            {
                GpuValidation.CreateMessengerWithoutSeverity(probe);
                string message = Assert.Single(log.Entries, e => e.Level == LogLevel.Error).Message;
                int open = message.IndexOf(" (", StringComparison.Ordinal) + 2;
                id = int.Parse(message.AsSpan(open, message.IndexOf("): ", open, StringComparison.Ordinal) - open), CultureInfo.InvariantCulture);
            }

            using VulkanInstance allowed = GpuValidation.Create(category, new[] { new ValidationAllowance(id, "Deliberate in this test.") });
            GpuValidation.CreateMessengerWithoutSeverity(allowed);

            Assert.Equal(0, allowed.ValidationErrors);
            Assert.Equal(0, allowed.ValidationWarnings);
            Assert.Equal(1, allowed.ValidationAllowed);
            (LogLevel Level, string Message) entry = Assert.Single(log.Entries, e => e.Level == LogLevel.Debug);
            Assert.EndsWith(" Reason: Deliberate in this test.", entry.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public unsafe void Release_AfterInstance()
    {
        ValidationLog log = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(log);
        using (logger)
        {
            ValidationSink.HandleTracker tracker = new();
            VulkanInstance instance = GpuValidation.Create(category, handles: tracker);
            ValidationSink sink = instance.Sink!;
            Assert.Equal(1, tracker.Live);

            // A messenger left alive is reported by vkDestroyInstance (VUID-vkDestroyInstance-instance-00629), through the
            // chained messenger: counted only if the sink's handle is still valid while the instance is destroyed.
            VkDebugUtilsMessengerCreateInfoEXT info = ValidationCallback.CreateInfo(sink);
            VkDebugUtilsMessengerEXT leaked = default;
            Assert.Equal(VkResult.Success, instance.Brutal.PInvoke.vkCreateDebugUtilsMessengerEXT.Invoke(instance.Handle, &info, null, &leaked));
            Assert.Equal(0, instance.ValidationErrors);

            instance.Dispose();
            instance.Dispose();

            Assert.True(instance.ValidationErrors > 0, "vkDestroyInstance's leak report didn't reach the sink.");
            Assert.Contains(log.Entries, e => e.Message.Contains("VUID-vkDestroyInstance-instance-00629", StringComparison.Ordinal));
            Assert.True(sink.IsReleased);
            Assert.Equal(0, tracker.Live);
        }
    }
}

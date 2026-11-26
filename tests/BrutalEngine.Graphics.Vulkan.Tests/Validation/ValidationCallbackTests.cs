using System;
using System.Collections.Concurrent;
using System.Threading;
using Brutal.VulkanApi;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Vulkan.Instances;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Validation;

public sealed class ValidationCallbackTests
{
    private const VkDebugUtilsMessageSeverityFlagsEXT Info = VkDebugUtilsMessageSeverityFlagsEXT.InfoBitEXT;
    private const VkDebugUtilsMessageSeverityFlagsEXT Verbose = VkDebugUtilsMessageSeverityFlagsEXT.VerboseBitEXT;
    private const VkDebugUtilsMessageTypeFlagsEXT General = VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT;
    private const VkDebugUtilsMessageTypeFlagsEXT Performance = VkDebugUtilsMessageTypeFlagsEXT.PerformanceBitEXT;
    private const VkDebugUtilsMessageTypeFlagsEXT AddressBinding = VkDebugUtilsMessageTypeFlagsEXT.DeviceAddressBindingBitEXT;

    // Severity, types, then the expected errors, warnings, log level (None: not logged) and logged kind.
    public static TheoryData<VkDebugUtilsMessageSeverityFlagsEXT, VkDebugUtilsMessageTypeFlagsEXT, int, int, LogLevel, string> Messages => new()
    {
        { FakeMessage.Error, FakeMessage.Validation, 1, 0, LogLevel.Error, "validation" },
        { FakeMessage.Error, General, 1, 0, LogLevel.Error, "general" },
        { FakeMessage.Error, Performance, 1, 0, LogLevel.Error, "performance" },
        { FakeMessage.Warning, FakeMessage.Validation, 0, 1, LogLevel.Warning, "validation" },
        { FakeMessage.Warning, General, 0, 1, LogLevel.Warning, "general" },
        { FakeMessage.Warning, Performance, 0, 1, LogLevel.Warning, "performance" },
        { FakeMessage.Warning, General | Performance, 0, 1, LogLevel.Warning, "performance" },
        { FakeMessage.Error | FakeMessage.Warning, FakeMessage.Validation | General, 1, 0, LogLevel.Error, "validation" },
        { Info, FakeMessage.Validation, 0, 0, LogLevel.None, string.Empty },
        { Verbose, General, 0, 0, LogLevel.None, string.Empty },
        { FakeMessage.Error, AddressBinding, 0, 0, LogLevel.None, string.Empty },
    };

    [Fact]
    public void ThrowingLogger_CountsAndReturnsFalse()
    {
        ValidationLog log = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(log, new ThrowingTimeProvider());
        using (logger)
        {
            ValidationSink sink = new(category, ReadOnlyMemory<ValidationAllowance>.Empty);
            try
            {
                // The logger throws from inside the callback; the exception must not reach the native caller.
                bool result = FakeMessage.Send(sink.UserData, FakeMessage.Warning, FakeMessage.Validation);

                Assert.False(result);
                Assert.Equal(1, sink.Warnings);
                Assert.Equal(1, sink.Errors);
                Assert.Equal(0, log.Count);
            }
            finally
            {
                sink.Release();
            }
        }
    }

    [Fact]
    public void Message_DoesNotAllocate()
    {
        const int Iterations = 1000;
        ValidationLog log = new() { Record = false };
        (Logger logger, LogCategory category) = ValidationLog.Create(log);
        using (logger)
        {
            ValidationSink sink = new(category, new[] { new ValidationAllowance(7, "Tolerated in this test.") });
            try
            {
                nint userData = sink.UserData;
                SendThree(userData);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < Iterations; i++)
                {
                    SendThree(userData);
                }

                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.Equal(0, allocated);
                Assert.Equal((Iterations + 1) * 3, log.Count);
                Assert.Equal(Iterations + 1, sink.Errors);
                Assert.Equal(Iterations + 1, sink.Warnings);
                Assert.Equal(Iterations + 1, sink.Allowed);
            }
            finally
            {
                sink.Release();
            }
        }

        static void SendThree(nint userData)
        {
            _ = FakeMessage.Send(userData, FakeMessage.Error, FakeMessage.Validation);
            _ = FakeMessage.Send(userData, FakeMessage.Warning, Performance);
            _ = FakeMessage.Send(userData, FakeMessage.Error, FakeMessage.Validation, 7);
        }
    }

    [Theory]
    [MemberData(nameof(Messages))]
    public void SeverityAndType_CountIntoTheirCounters(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT types,
        int errors,
        int warnings,
        LogLevel level,
        string kind)
    {
        ValidationLog log = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(log);
        using (logger)
        {
            ValidationSink sink = new(category, ReadOnlyMemory<ValidationAllowance>.Empty);
            try
            {
                Assert.False(FakeMessage.Send(sink.UserData, severity, types));

                Assert.Equal(errors, sink.Errors);
                Assert.Equal(warnings, sink.Warnings);
                Assert.Equal(0, sink.Allowed);
                if (level == LogLevel.None)
                {
                    Assert.Empty(log.Entries);
                }
                else
                {
                    (LogLevel Level, string Message) entry = Assert.Single(log.Entries);
                    Assert.Equal(level, entry.Level);
                    Assert.Equal($"Vulkan {kind} message VUID-Test-name (4660): Test message text.", entry.Message);
                }
            }
            finally
            {
                sink.Release();
            }
        }
    }

    [Theory]
    [InlineData(VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT)]
    [InlineData(VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT)]
    public void AllowListed_LoggedAtDebugNotCounted(VkDebugUtilsMessageSeverityFlagsEXT severity)
    {
        ValidationLog log = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(log);
        using (logger)
        {
            ValidationAllowance[] allowList = [new(-5, "Another message."), new(FakeMessage.Id, "Known driver issue.")];
            ValidationSink sink = new(category, allowList);
            try
            {
                Assert.False(FakeMessage.Send(sink.UserData, severity, FakeMessage.Validation));

                Assert.Equal(0, sink.Errors);
                Assert.Equal(0, sink.Warnings);
                Assert.Equal(1, sink.Allowed);
                (LogLevel Level, string Message) entry = Assert.Single(log.Entries);
                Assert.Equal(LogLevel.Debug, entry.Level);
                Assert.Equal("Allowed Vulkan validation message VUID-Test-name (4660): Test message text. Reason: Known driver issue.", entry.Message);
            }
            finally
            {
                sink.Release();
            }
        }
    }

    [Fact]
    public void AllowList_OtherId_StillCounted()
    {
        ValidationLog log = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(log);
        using (logger)
        {
            ValidationSink sink = new(category, new[] { new ValidationAllowance(FakeMessage.Id + 1, "A different message.") });
            try
            {
                _ = FakeMessage.Send(sink.UserData, FakeMessage.Error, FakeMessage.Validation);

                Assert.Equal(1, sink.Errors);
                Assert.Equal(0, sink.Allowed);
                Assert.Equal(LogLevel.Error, Assert.Single(log.Entries).Level);
            }
            finally
            {
                sink.Release();
            }
        }
    }

    [Fact]
    public void Message_NullStrings_LoggedEmpty()
    {
        ValidationLog log = new();
        (Logger logger, LogCategory category) = ValidationLog.Create(log);
        using (logger)
        {
            ValidationSink sink = new(category, ReadOnlyMemory<ValidationAllowance>.Empty);
            try
            {
                // pMessageIdName may be NULL (the specification); a missing message must not crash either.
                _ = FakeMessage.Send(sink.UserData, FakeMessage.Error, FakeMessage.Validation, 3, default, default);

                Assert.Equal(1, sink.Errors);
                Assert.Equal("Vulkan validation message  (3): ", Assert.Single(log.Entries).Message);
            }
            finally
            {
                sink.Release();
            }
        }
    }

    [Fact]
    public void Callback_ConcurrentMessages_CountsEvery()
    {
        const int Threads = 4;
        const int PerThread = 2000;
        ValidationLog log = new() { Record = false };
        (Logger logger, LogCategory category) = ValidationLog.Create(log);
        using (logger)
        {
            ValidationSink sink = new(category, ReadOnlyMemory<ValidationAllowance>.Empty);
            try
            {
                nint userData = sink.UserData;
                CancellationToken token = TestContext.Current.CancellationToken;
                using Barrier start = new(Threads);
                ConcurrentQueue<Exception> failures = new();
                var threads = new Thread[Threads];
                for (int t = 0; t < Threads; t++)
                {
                    VkDebugUtilsMessageSeverityFlagsEXT severity = t % 2 == 0 ? FakeMessage.Error : FakeMessage.Warning;
                    threads[t] = new Thread(() =>
                    {
                        try
                        {
                            _ = start.SignalAndWait(TimeSpan.FromSeconds(30), token);
                            for (int i = 0; i < PerThread; i++)
                            {
                                _ = FakeMessage.Send(userData, severity, FakeMessage.Validation);
                            }
                        }
                        catch (Exception exception)
                        {
                            failures.Enqueue(exception);
                        }
                    });
                    threads[t].Start();
                }

                foreach (Thread thread in threads)
                {
                    Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
                }

                Assert.Empty(failures);
                Assert.Equal(Threads / 2 * PerThread, sink.Errors);
                Assert.Equal(Threads / 2 * PerThread, sink.Warnings);
            }
            finally
            {
                sink.Release();
            }
        }
    }

    [Fact]
    public unsafe void CreateInfo_SubscribesWarningsAndErrorsToTheSink()
    {
        ValidationSink sink = new(default, ReadOnlyMemory<ValidationAllowance>.Empty);
        try
        {
            VkDebugUtilsMessengerCreateInfoEXT info = ValidationCallback.CreateInfo(sink);

            // Literal flags from the Vulkan registry: WARNING 0x100 | ERROR 0x1000; GENERAL 1 | VALIDATION 2 | PERFORMANCE 4.
            Assert.Equal(0x1100u, (uint)info.MessageSeverity);
            Assert.Equal(0x7u, (uint)info.MessageType);
            Assert.True(info.Next is null);
            Assert.Equal(sink.UserData, (nint)info.UserData);

            // The pointer is this callback: calling it as the layer would counts into the sink.
            Assert.False(info.PfnUserCallback.Invoke(FakeMessage.Error, FakeMessage.Validation, null, info.UserData));
            Assert.Equal(1, sink.Errors);
        }
        finally
        {
            sink.Release();
        }
    }

    [Fact]
    public void CreateInfo_ReleasedOrNullSink_Throws()
    {
        ValidationSink sink = new(default, ReadOnlyMemory<ValidationAllowance>.Empty);
        sink.Release();

        _ = Assert.Throws<ObjectDisposedException>(() => ValidationCallback.CreateInfo(sink));
        _ = Assert.Throws<ArgumentNullException>("sink", () => ValidationCallback.CreateInfo(null!));
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Logger failure (test).");
    }
}

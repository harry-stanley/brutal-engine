using System;
using System.Text;
using Brutal.VulkanApi;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Logging.Sinks;
using Xunit;

namespace HelloWindow.Tests;

public sealed unsafe class ValidationMessengerTests
{
    private const VkDebugUtilsMessageTypeFlagsEXT Validation = VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT;

    [Fact]
    public void Callback_ErrorSeverity_CountsAnError()
    {
        using var messenger = new ValidationMessenger(default);

        Assert.False(Invoke(messenger.CreateInfo(), VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT, "vkCmdPipelineBarrier2: bad layout"u8));

        Assert.Equal(1, messenger.Errors);
        Assert.Equal(0, messenger.Warnings);
    }

    [Fact]
    public void Callback_WarningSeverity_CountsAWarning()
    {
        using var messenger = new ValidationMessenger(default);

        Assert.False(Invoke(messenger.CreateInfo(), VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT, "a warning"u8));

        Assert.Equal(0, messenger.Errors);
        Assert.Equal(1, messenger.Warnings);
    }

    [Fact]
    public void Callback_Error_LogsAnErrorWithTheMessage()
    {
        var sink = new MemoryLogSink(8, 4096);
        using (var logger = new Logger(new LoggerOptions { Sinks = [sink] }))
        {
            using var messenger = new ValidationMessenger(logger.CreateCategory("HelloWindow.Vulkan"));
            Invoke(messenger.CreateInfo(), VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT, "vkCmdPipelineBarrier2: bad layout"u8);
        }

        MemoryLogRecord record = Assert.Single(Records(sink));
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Equal("HelloWindow.Vulkan", record.Category);
        Assert.Equal("validation (ValidationBitEXT): vkCmdPipelineBarrier2: bad layout", Encoding.UTF8.GetString(record.Message.Span));
    }

    [Fact]
    public void Callback_Warning_LogsAWarning()
    {
        var sink = new MemoryLogSink(8, 4096);
        using (var logger = new Logger(new LoggerOptions { Sinks = [sink] }))
        {
            using var messenger = new ValidationMessenger(logger.CreateCategory("HelloWindow.Vulkan"));
            Invoke(messenger.CreateInfo(), VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT, "a warning"u8);
        }

        MemoryLogRecord record = Assert.Single(Records(sink));
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Equal("validation (ValidationBitEXT): a warning", Encoding.UTF8.GetString(record.Message.Span));
    }

    [Fact]
    public void Callback_TwoMessengers_CountsOnlyTheOneInItsUserData()
    {
        // The callback finds its messenger through pUserData, so the counts aren't shared (no static state).
        using var first = new ValidationMessenger(default);
        using var second = new ValidationMessenger(default);

        Invoke(first.CreateInfo(), VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT, "first"u8);
        Invoke(second.CreateInfo(), VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT, "second"u8);
        Invoke(second.CreateInfo(), VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT, "second again"u8);

        Assert.Equal((1, 0), (first.Errors, first.Warnings));
        Assert.Equal((0, 2), (second.Errors, second.Warnings));
    }

    [Fact]
    public void Callback_NullMessage_StillCounts()
    {
        using var messenger = new ValidationMessenger(default);
        VkDebugUtilsMessengerCreateInfoEXT info = messenger.CreateInfo();
        VkDebugUtilsMessengerCallbackDataEXT data = new();

        Assert.False(info.PfnUserCallback.Invoke(VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT, Validation, &data, info.UserData));

        Assert.Equal(1, messenger.Errors);
    }

    [Fact]
    public void CreateInfo_SetsUserDataAndSeverities()
    {
        using var messenger = new ValidationMessenger(default);

        VkDebugUtilsMessengerCreateInfoEXT info = messenger.CreateInfo();

        Assert.True(info.UserData != null);
        Assert.Equal(VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT | VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT, info.MessageSeverity);
    }

    [Fact]
    public void CreateInfo_AfterDispose_Throws()
    {
        var messenger = new ValidationMessenger(default);
        messenger.Dispose();
        messenger.Dispose();

        Assert.Throws<ObjectDisposedException>(() => messenger.CreateInfo());
    }

    private static MemoryLogRecord[] Records(MemoryLogSink sink)
    {
        var records = new MemoryLogRecord[sink.CapacityEntries];
        int count = sink.CopyTo(records, new byte[sink.CapacityBytes]);
        return records[..count];
    }

    private static bool Invoke(VkDebugUtilsMessengerCreateInfoEXT info, VkDebugUtilsMessageSeverityFlagsEXT severity, ReadOnlySpan<byte> message)
    {
        // The layer passes a NUL-terminated UTF-8 string.
        Span<byte> text = stackalloc byte[message.Length + 1];
        message.CopyTo(text);
        text[^1] = 0;
        fixed (byte* pointer = text)
        {
            VkDebugUtilsMessengerCallbackDataEXT data = new() { Message = pointer };
            return info.PfnUserCallback.Invoke(severity, Validation, &data, info.UserData);
        }
    }
}

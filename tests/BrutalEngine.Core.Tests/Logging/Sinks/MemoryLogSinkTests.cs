using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using System.Threading;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Logging.Sinks;
using CsCheck;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging.Sinks;

public sealed class MemoryLogSinkTests
{
    private static readonly byte[] _ellipsis = "\u2026"u8.ToArray();

    [Fact]
    public void Write_BeyondCapacity_KeepsNewestInOrder()
    {
        MemoryLogSink sink = new(capacityEntries: 3, capacityBytes: 1024);

        for (int i = 0; i < 8; i++)
        {
            Write(sink, $"m{i}");
        }

        Assert.Equal(["m5", "m6", "m7"], Messages(sink));
    }

    [Fact]
    public void Write_BeyondByteCapacity_KeepsNewestInOrder()
    {
        MemoryLogSink sink = new(capacityEntries: 100, capacityBytes: 64);
        string thirty = new('a', 30);

        // Two 30-byte messages fill the ring; later messages wrap around its end.
        Write(sink, thirty);
        Write(sink, "b" + thirty[1..]);
        Write(sink, "c" + thirty[1..]);
        Assert.Equal(["b" + thirty[1..], "c" + thirty[1..]], Messages(sink));

        Write(sink, "0123456789");
        Write(sink, "d" + thirty[1..]);
        Assert.Equal(["0123456789", "d" + thirty[1..]], Messages(sink));
    }

    [Fact]
    public void Write_BeyondCapacity_CountsDropped()
    {
        MemoryLogSink sink = new(capacityEntries: 4, capacityBytes: 64);

        for (int i = 0; i < 4; i++)
        {
            Write(sink, "1234567890");
        }

        Assert.Equal(0, sink.DroppedCount);

        Write(sink, "x");
        Assert.Equal(1, sink.DroppedCount);

        // 60 bytes: the three 10-byte entries go, the 1-byte "x" still fits beside it.
        Write(sink, new string('y', 60));
        Assert.Equal(4, sink.DroppedCount);
        Assert.Equal(["x", new string('y', 60)], Messages(sink));
    }

    [Theory]
    [InlineData("a", "\u00e9", 61)] // 'a' then 2-byte code points: byte 61 (64 - the ellipsis) starts one.
    [InlineData("", "\u00e9", 60)] // byte 61 is the 2nd byte of a code point: back up 1.
    [InlineData("ab", "\u20ac", 59)] // 3-byte code points: back up 2.
    [InlineData("xy", "\U0001F600", 58)] // 4-byte code points: back up 3.
    public void Write_OversizedMessage_StoresValidTruncatedUtf8(string prefix, string repeated, int expectedCut)
    {
        MemoryLogSink sink = new(capacityEntries: 4, capacityBytes: 64);
        byte[] message = Encoding.UTF8.GetBytes(prefix + string.Concat(Enumerable.Repeat(repeated, 40)));

        Write(sink, message);

        byte[] stored = Assert.Single(MessageBytes(sink));
        Assert.Equal(message.AsSpan(0, expectedCut).ToArray().Concat(_ellipsis), stored);
        Assert.True(Utf8.IsValid(stored));
        Assert.True(stored.Length <= 64);
        Assert.Equal(0, sink.DroppedCount);
    }

    [Fact]
    public void Write_MessageOfExactlyCapacity_IsStoredWhole()
    {
        MemoryLogSink sink = new(capacityEntries: 2, capacityBytes: 64);
        byte[] message = Encoding.UTF8.GetBytes(new string('z', 64));

        Write(sink, message);

        Assert.Equal(message, Assert.Single(MessageBytes(sink)));
    }

    [Fact]
    public void CopyTo_KeepsEveryField()
    {
        MemoryLogSink sink = new(capacityEntries: 2, capacityBytes: 64);
        DateTimeOffset timestamp = new(2026, 11, 25, 16, 0, 0, TimeSpan.FromHours(13));
        LogEntry entry = new()
        {
            Level = LogLevel.Critical,
            Category = "Render",
            Timestamp = timestamp,
            ThreadId = 12,
            Message = "device lost"u8,
            CallerFilePath = "/src/Device.cs",
            CallerLineNumber = 77,
        };
        sink.Write(entry);
        sink.Flush();

        var records = new MemoryLogRecord[2];
        byte[] buffer = new byte[64];
        Assert.Equal(1, sink.CopyTo(records, buffer));

        MemoryLogRecord record = records[0];
        Assert.Equal(LogLevel.Critical, record.Level);
        Assert.Equal("Render", record.Category);
        Assert.Equal(timestamp, record.Timestamp);
        Assert.Equal(12, record.ThreadId);
        Assert.Equal("device lost"u8.ToArray(), record.Message.ToArray());
        Assert.Equal("/src/Device.cs", record.CallerFilePath);
        Assert.Equal(77, record.CallerLineNumber);
        Assert.True(records[1].Message.IsEmpty);
        Assert.Equal(string.Empty, records[1].Category);
        Assert.Equal(string.Empty, records[1].CallerFilePath);
    }

    [Fact]
    public void CopyTo_SmallBuffers_CopiesNewestThatFitOldestFirst()
    {
        MemoryLogSink sink = new(capacityEntries: 8, capacityBytes: 256);
        foreach (string message in new[] { "aaaa", "bb", "cccccc", "d", "eee" })
        {
            Write(sink, message);
        }

        var records = new MemoryLogRecord[8];
        Assert.Equal(2, sink.CopyTo(records.AsSpan(0, 2), new byte[256]));
        Assert.Equal(["d", "eee"], records.Take(2).Select(r => Encoding.UTF8.GetString(r.Message.Span)));

        // 9 bytes: "eee" and "d" fit, "cccccc" doesn't. The copy stops there, though the older "bb" would fit.
        byte[] buffer = new byte[9];
        Assert.Equal(2, sink.CopyTo(records, buffer));
        Assert.Equal(["d", "eee"], records.Take(2).Select(r => Encoding.UTF8.GetString(r.Message.Span)));
        Assert.Equal("deee"u8.ToArray(), buffer.AsSpan(0, 4).ToArray());

        Assert.Equal(5, sink.CopyTo(records, new byte[16]));
        Assert.Equal(["aaaa", "bb", "cccccc", "d", "eee"], records.Take(5).Select(r => Encoding.UTF8.GetString(r.Message.Span)));

        Assert.Equal(0, sink.CopyTo(records, Memory<byte>.Empty));
        Assert.Equal(0, sink.CopyTo([], new byte[256]));
    }

    [Fact]
    public void CopyTo_Empty_ReturnsZero()
    {
        MemoryLogSink sink = new(capacityEntries: 1, capacityBytes: 64);

        Assert.Equal(0, sink.CopyTo(new MemoryLogRecord[1], new byte[64]));
        Assert.Equal(0, sink.DroppedCount);
    }

    [Theory]
    [InlineData(LogLevel.None)]
    [InlineData((LogLevel)9)]
    public void Write_LevelOutOfRange_ThrowsAndStoresNothing(LogLevel level)
    {
        MemoryLogSink sink = new(capacityEntries: 1, capacityBytes: 64);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            LogEntry entry = new() { Level = level, Message = "m"u8 };
            sink.Write(entry);
        });
        Assert.Empty(Messages(sink));
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(-1, 64)]
    [InlineData(1, 63)]
    [InlineData(1, 0)]
    public void Constructor_CapacityOutOfRange_ThrowsArgumentOutOfRange(int capacityEntries, int capacityBytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryLogSink(capacityEntries, capacityBytes));

    [Fact]
    public void Constructor_ValidCapacities_AreReported()
    {
        MemoryLogSink sink = new(capacityEntries: 1, capacityBytes: 64);

        Assert.Equal(1, sink.CapacityEntries);
        Assert.Equal(64, sink.CapacityBytes);
    }

    [Fact]
    public void RandomWrites_MatchReferenceModel()
    {
        // A message is valid UTF-8 from random scalars (1 to 4 bytes each); null means "copy out with small buffers".
        var operation = Gen.Frequency(
            (6, Gen.Int[0, 0x10FFFF].Where(Rune.IsValid).Array[0, 40].Select(s => (string?)string.Concat(s.Select(c => new Rune(c).ToString())))),
            (1, Gen.Const((string?)null)));

        Gen.Select(Gen.Int[1, 6], Gen.Int[64, 160], operation.Array[1, 60], Gen.Int[0, 7], Gen.Int[0, 200]).Sample((entries, bytes, operations, copyEntries, copyBytes) =>
        {
            MemoryLogSink sink = new(entries, bytes);
            List<byte[]> model = [];
            long dropped = 0;
            foreach (string? message in operations)
            {
                if (message is null)
                {
                    Assert.Equal(NewestThatFit(model, copyEntries, copyBytes), MessageBytes(sink, copyEntries, copyBytes));
                    continue;
                }

                Write(sink, message);
                model.Add(ModelTruncate(message, bytes));
                while (model.Count > entries || model.Sum(m => m.Length) > bytes)
                {
                    model.RemoveAt(0);
                    dropped++;
                }

                Assert.Equal(model, MessageBytes(sink));
                Assert.Equal(dropped, sink.DroppedCount);
            }
        });
    }

    [Fact]
    public void Write_FromManyThreads_NoTornEntries()
    {
        const int WriterCount = 8;
        const int EntriesPerWriter = 2000;
        MemoryLogSink sink = new(capacityEntries: 512, capacityBytes: 16 * 1024);
        using Barrier start = new(WriterCount + 1);

        // Exceptions (failed asserts included) are collected and checked on the test thread, so a broken sink fails
        // this test instead of crashing the test host.
        ConcurrentQueue<Exception> failures = new();
        var threads = new Thread[WriterCount + 1];
        for (int t = 0; t < WriterCount; t++)
        {
            int writer = t;
            threads[t] = new Thread(() =>
            {
                // Each writer's messages have their own length and fill byte, and the category names the writer.
                string category = $"Writer{writer}";
                byte[] message = new byte[8 + (writer * 9)];
                Capture(failures, () =>
                {
                    start.SignalAndWait(TestContext.Current.CancellationToken);
                    for (int i = 0; i < EntriesPerWriter; i++)
                    {
                        message.AsSpan().Fill((byte)('a' + ((writer + i) % 26)));
                        LogEntry entry = new() { Level = LogLevel.Information, Category = category, ThreadId = writer, Message = message, CallerLineNumber = i };
                        sink.Write(entry);
                    }
                });
            });
        }

        // A reader copies concurrently: every snapshot must contain only whole entries.
        threads[WriterCount] = new Thread(() =>
        {
            var records = new MemoryLogRecord[512];
            byte[] buffer = new byte[16 * 1024];
            Capture(failures, () =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                for (int i = 0; i < 200; i++)
                {
                    int count = sink.CopyTo(records, buffer);
                    AssertWhole(records.AsSpan(0, count));
                }
            });
        });

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        Assert.Empty(failures);
        var final = new MemoryLogRecord[512];
        int finalCount = sink.CopyTo(final, new byte[16 * 1024]);
        AssertWhole(final.AsSpan(0, finalCount));
        Assert.Equal(WriterCount * EntriesPerWriter, finalCount + sink.DroppedCount);

        // Per writer, the surviving entries are in the order that writer logged them.
        foreach (IGrouping<int, MemoryLogRecord> writer in final.Take(finalCount).GroupBy(r => r.ThreadId))
        {
            int[] lines = [.. writer.Select(r => r.CallerLineNumber)];
            Assert.Equal(lines.Order(), lines);
        }

        static void AssertWhole(ReadOnlySpan<MemoryLogRecord> records)
        {
            foreach (MemoryLogRecord record in records)
            {
                int writer = record.ThreadId;
                Assert.Equal($"Writer{writer}", record.Category);
                byte fill = (byte)('a' + ((writer + record.CallerLineNumber) % 26));
                Assert.Equal(8 + (writer * 9), record.Message.Length);
                Assert.True(record.Message.Span.IndexOfAnyExcept(fill) < 0, "A message mixes bytes from two entries.");
            }
        }
    }

    private static void Capture(ConcurrentQueue<Exception> failures, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Enqueue(exception);
        }
    }

    private static byte[] ModelTruncate(string message, int capacity)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length <= capacity)
        {
            return bytes;
        }

        // Independent of the sink: keep whole runes while they fit in front of the ellipsis.
        List<byte> kept = [];
        foreach (Rune rune in message.EnumerateRunes())
        {
            byte[] encoded = Encoding.UTF8.GetBytes(rune.ToString());
            if (kept.Count + encoded.Length > capacity - _ellipsis.Length)
            {
                break;
            }

            kept.AddRange(encoded);
        }

        return [.. kept, .. _ellipsis];
    }

    private static List<byte[]> NewestThatFit(List<byte[]> model, int maxEntries, int maxBytes)
    {
        List<byte[]> newest = [];
        int total = 0;
        for (int i = model.Count - 1; i >= 0 && newest.Count < maxEntries && total + model[i].Length <= maxBytes; i--)
        {
            total += model[i].Length;
            newest.Insert(0, model[i]);
        }

        return newest;
    }

    private static void Write(MemoryLogSink sink, string message) => Write(sink, Encoding.UTF8.GetBytes(message));

    private static void Write(MemoryLogSink sink, byte[] message)
    {
        LogEntry entry = new() { Level = LogLevel.Information, Category = "Core", Message = message };
        sink.Write(entry);
    }

    private static string[] Messages(MemoryLogSink sink) => [.. MessageBytes(sink).Select(Encoding.UTF8.GetString)];

    private static byte[][] MessageBytes(MemoryLogSink sink) => MessageBytes(sink, sink.CapacityEntries, sink.CapacityBytes);

    private static byte[][] MessageBytes(MemoryLogSink sink, int maxEntries, int maxBytes)
    {
        var records = new MemoryLogRecord[maxEntries];
        int count = sink.CopyTo(records, new byte[maxBytes]);
        return [.. records.Take(count).Select(r => r.Message.ToArray())];
    }
}

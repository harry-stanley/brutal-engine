using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Validation;
using CsCheck;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The copy rules of docs/design/graphics.md section 6: one row per rule, each with a passing and a failing case.</summary>
public sealed class CopyValidatorTests
{
    private const string DebugName = "probe";

    private static readonly BufferHandle _bufferA = new(new Handle(1, 1));
    private static readonly BufferHandle _bufferB = new(new Handle(2, 1));

    private static readonly BufferDescription _buffer = new()
    {
        Size = 1024,
        Usage = BufferUsage.CopySource | BufferUsage.CopyDestination,
        DebugName = DebugName,
    };

    // The largest buffer a description allows, so offsets near long.MaxValue lie inside it and only the range check
    // (Size - offset, never offset + size) stands between them and a wrapped sum.
    private static readonly BufferDescription _huge = _buffer with { Size = long.MaxValue };

    private static readonly BufferCopy _copy = new() { Source = _bufferA, Destination = _bufferB, DestinationOffset = 512, Size = 512 };

    public static TheoryData<string> Rules() =>
    [
        "BufferCopy.SourceUsage", "BufferCopy.DestinationUsage", "BufferCopy.SizeZero", "BufferCopy.SizeNegative", "BufferCopy.SizeMinValue",
        "BufferCopy.SizeAligned", "BufferCopy.SizePastEnd", "BufferCopy.SizeMaxValue", "BufferCopy.SourceOffsetNegative",
        "BufferCopy.SourceOffsetAtEnd", "BufferCopy.SourceOffsetPastEnd", "BufferCopy.SourceOffsetMaxValue", "BufferCopy.SourceOffsetAligned",
        "BufferCopy.SourceRangeInside", "BufferCopy.DestinationOffsetNegative", "BufferCopy.DestinationOffsetAtEnd",
        "BufferCopy.DestinationOffsetPastEnd", "BufferCopy.DestinationOffsetMaxValue", "BufferCopy.DestinationOffsetAligned",
        "BufferCopy.DestinationRangeInside", "BufferCopy.SelfOverlapForward", "BufferCopy.SelfOverlapBackward", "BufferCopy.SelfOverlapSameRange",
        "BufferCopy.SelfOverlapInside",
        "WriteBuffer.Usage", "WriteBuffer.DataEmpty", "WriteBuffer.DataNegative", "WriteBuffer.OffsetNegative", "WriteBuffer.OffsetMinValue",
        "WriteBuffer.OffsetAtEnd", "WriteBuffer.OffsetPastEnd", "WriteBuffer.OffsetMaxValue", "WriteBuffer.RangeInside", "WriteBuffer.Unaligned",
    ];

    [Theory]
    [MemberData(nameof(Rules))]
    public void Rule_Passes_And_Fails(string rule)
    {
        Type outOfRange = typeof(ArgumentOutOfRangeException);
        Type invalid = typeof(ArgumentException);
        BufferDescription noCopySource = _buffer with { Usage = BufferUsage.Vertex | BufferUsage.CopyDestination };
        BufferDescription noCopyDestination = _buffer with { Usage = BufferUsage.Vertex | BufferUsage.CopySource };
        BufferCopy self = _copy with { Destination = _bufferA };
        (Action pass, Action fail, Type exception, string param, string field) = rule switch
        {
            "BufferCopy.SourceUsage" => (Copy(_copy), Copy(_copy, noCopySource, _buffer), invalid, "copy", "BufferCopy.Source"),
            "BufferCopy.DestinationUsage" => (Copy(_copy), Copy(_copy, _buffer, noCopyDestination), invalid, "copy", "BufferCopy.Destination"),
            "BufferCopy.SizeZero" => (Copy(_copy with { Size = 4 }), Copy(_copy with { Size = 0 }), outOfRange, "copy", "BufferCopy.Size"),
            "BufferCopy.SizeNegative" => (Copy(_copy with { Size = 4 }), Copy(_copy with { Size = -4 }), outOfRange, "copy", "BufferCopy.Size"),
            "BufferCopy.SizeMinValue" => (Copy(_copy with { Size = 4 }), Copy(_copy with { Size = long.MinValue }), outOfRange, "copy", "BufferCopy.Size"),
            "BufferCopy.SizeAligned" => (Copy(_copy with { Size = 8 }), Copy(_copy with { Size = 6 }), invalid, "copy", "BufferCopy.Size"),
            // The whole buffer at once is the bound; 4 more bytes is one aligned step over.
            "BufferCopy.SizePastEnd" => (
                Copy(_copy with { DestinationOffset = 0, Size = 1024 }),
                Copy(_copy with { DestinationOffset = 0, Size = 1028 }),
                outOfRange, "copy", "BufferCopy.Size"),
            // long.MaxValue - 3 is the largest multiple of 4, so the range rule (not the alignment rule) rejects it.
            "BufferCopy.SizeMaxValue" => (Copy(_copy), Copy(_copy with { DestinationOffset = 0, Size = long.MaxValue - 3 }), outOfRange, "copy", "BufferCopy.Size"),
            "BufferCopy.SourceOffsetNegative" => (Copy(_copy with { SourceOffset = 0 }), Copy(_copy with { SourceOffset = -4 }), outOfRange, "copy", "BufferCopy.SourceOffset"),
            // An offset at the end is in range; the copy of 4 bytes from it isn't.
            "BufferCopy.SourceOffsetAtEnd" => (
                Copy(_copy with { SourceOffset = 1020, Size = 4 }),
                Copy(_copy with { SourceOffset = 1024, Size = 4 }),
                outOfRange, "copy", "BufferCopy.Size"),
            "BufferCopy.SourceOffsetPastEnd" => (Copy(_copy with { SourceOffset = 1020, Size = 4 }), Copy(_copy with { SourceOffset = 1028, Size = 4 }), outOfRange, "copy", "BufferCopy.SourceOffset"),
            "BufferCopy.SourceOffsetMaxValue" => (Copy(_copy), Copy(_copy with { SourceOffset = long.MaxValue }), outOfRange, "copy", "BufferCopy.SourceOffset"),
            "BufferCopy.SourceOffsetAligned" => (Copy(_copy with { SourceOffset = 4 }), Copy(_copy with { SourceOffset = 2 }), invalid, "copy", "BufferCopy.SourceOffset"),
            "BufferCopy.SourceRangeInside" => (Copy(_copy with { SourceOffset = 512 }), Copy(_copy with { SourceOffset = 516 }), outOfRange, "copy", "BufferCopy.Size"),
            "BufferCopy.DestinationOffsetNegative" => (
                Copy(_copy with { DestinationOffset = 0 }),
                Copy(_copy with { DestinationOffset = -4 }),
                outOfRange, "copy", "BufferCopy.DestinationOffset"),
            "BufferCopy.DestinationOffsetAtEnd" => (
                Copy(_copy with { DestinationOffset = 1020, Size = 4 }),
                Copy(_copy with { DestinationOffset = 1024, Size = 4 }),
                outOfRange, "copy", "BufferCopy.Size"),
            "BufferCopy.DestinationOffsetPastEnd" => (
                Copy(_copy with { DestinationOffset = 1020, Size = 4 }),
                Copy(_copy with { DestinationOffset = 1028, Size = 4 }),
                outOfRange, "copy", "BufferCopy.DestinationOffset"),
            "BufferCopy.DestinationOffsetMaxValue" => (Copy(_copy), Copy(_copy with { DestinationOffset = long.MaxValue }), outOfRange, "copy", "BufferCopy.DestinationOffset"),
            "BufferCopy.DestinationOffsetAligned" => (Copy(_copy with { DestinationOffset = 8 }), Copy(_copy with { DestinationOffset = 9 }), invalid, "copy", "BufferCopy.DestinationOffset"),
            "BufferCopy.DestinationRangeInside" => (Copy(_copy), Copy(_copy with { DestinationOffset = 516 }), outOfRange, "copy", "BufferCopy.Size"),
            // Adjacent ranges of one buffer are fine; 4 bytes of overlap, the smallest an aligned copy can make, aren't.
            "BufferCopy.SelfOverlapForward" => (
                Copy(self with { SourceOffset = 0, DestinationOffset = 256, Size = 256 }),
                Copy(self with { SourceOffset = 0, DestinationOffset = 252, Size = 256 }),
                invalid, "copy", "BufferCopy.DestinationOffset"),
            "BufferCopy.SelfOverlapBackward" => (
                Copy(self with { SourceOffset = 256, DestinationOffset = 0, Size = 256 }),
                Copy(self with { SourceOffset = 252, DestinationOffset = 0, Size = 256 }),
                invalid, "copy", "BufferCopy.DestinationOffset"),
            // The same offsets in two different buffers don't overlap; in one buffer they do.
            "BufferCopy.SelfOverlapSameRange" => (
                Copy(_copy with { SourceOffset = 4, DestinationOffset = 4, Size = 4 }),
                Copy(self with { SourceOffset = 4, DestinationOffset = 4, Size = 4 }),
                invalid, "copy", "BufferCopy.DestinationOffset"),
            "BufferCopy.SelfOverlapInside" => (
                Copy(self with { SourceOffset = 0, DestinationOffset = 512, Size = 512 }),
                Copy(self with { SourceOffset = 0, DestinationOffset = 4, Size = 8 }),
                invalid, "copy", "BufferCopy.DestinationOffset"),

            "WriteBuffer.Usage" => (Write(_buffer, 0, 4), Write(noCopyDestination, 0, 4), invalid, "buffer", "WriteBuffer.buffer"),
            "WriteBuffer.DataEmpty" => (Write(_buffer, 0, 1), Write(_buffer, 0, 0), outOfRange, "data", "WriteBuffer.data.Length"),
            "WriteBuffer.DataNegative" => (Write(_buffer, 0, 1), Write(_buffer, 0, int.MinValue), outOfRange, "data", "WriteBuffer.data.Length"),
            "WriteBuffer.OffsetNegative" => (Write(_buffer, 0, 1), Write(_buffer, -1, 1), outOfRange, "offset", "WriteBuffer.offset"),
            "WriteBuffer.OffsetMinValue" => (Write(_buffer, 0, 1), Write(_buffer, long.MinValue, 1), outOfRange, "offset", "WriteBuffer.offset"),
            "WriteBuffer.OffsetAtEnd" => (Write(_buffer, 1023, 1), Write(_buffer, 1024, 1), outOfRange, "data", "WriteBuffer.data.Length"),
            "WriteBuffer.OffsetPastEnd" => (Write(_buffer, 1023, 1), Write(_buffer, 1025, 1), outOfRange, "offset", "WriteBuffer.offset"),
            "WriteBuffer.OffsetMaxValue" => (Write(_buffer, 0, 1024), Write(_buffer, long.MaxValue, 1), outOfRange, "offset", "WriteBuffer.offset"),
            "WriteBuffer.RangeInside" => (Write(_buffer, 1, 1023), Write(_buffer, 1, 1024), outOfRange, "data", "WriteBuffer.data.Length"),
            // Writes need no alignment: an odd offset and length pass, and the same range fails only for its usage.
            "WriteBuffer.Unaligned" => (Write(_buffer, 3, 5), Write(noCopyDestination, 3, 5), invalid, "buffer", "WriteBuffer.buffer"),

            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };

        pass();
        AssertRejected(exception, param, field, fail);
    }

    public static TheoryData<string> HugeOffsets() =>
    [
        "SourceOffsetNearMax", "DestinationOffsetNearMax", "SizeNearMax", "SourceAndSizeNearMax", "WriteOffsetNearMax", "WriteOffsetAtMax",
    ];

    // Every row lies inside a long.MaxValue-byte buffer except for its last few bytes, so offset + size would wrap to a
    // negative number and pass a sum-based check.
    [Theory]
    [MemberData(nameof(HugeOffsets))]
    public void HugeOffsets_DoNotWrap(string row)
    {
        const long NearMax = long.MaxValue - 3; // 2^63 - 4: a multiple of 4.
        (Action validate, string param, string field) = row switch
        {
            "SourceOffsetNearMax" => (CopyHuge(_copy with { SourceOffset = NearMax, DestinationOffset = 0, Size = 8 }), "copy", "BufferCopy.Size"),
            "DestinationOffsetNearMax" => (CopyHuge(_copy with { DestinationOffset = NearMax, Size = 8 }), "copy", "BufferCopy.Size"),
            "SizeNearMax" => (CopyHuge(_copy with { SourceOffset = 4, DestinationOffset = 0, Size = NearMax }), "copy", "BufferCopy.Size"),
            "SourceAndSizeNearMax" => (CopyHuge(_copy with { SourceOffset = NearMax, DestinationOffset = 0, Size = NearMax }), "copy", "BufferCopy.Size"),
            "WriteOffsetNearMax" => (Write(_huge, NearMax, 8), "data", "WriteBuffer.data.Length"),
            "WriteOffsetAtMax" => (Write(_huge, long.MaxValue, 1), "data", "WriteBuffer.data.Length"),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown row."),
        };

        AssertRejected(typeof(ArgumentOutOfRangeException), param, field, validate);
    }

    [Fact]
    public void HugeOffsets_LastBytesOfHugeBuffer_Pass()
    {
        // The counterpart of HugeOffsets_DoNotWrap: ranges in the last bytes of the huge buffer, the write ending exactly
        // at long.MaxValue.
        CopyValidator.Validate(_copy with { SourceOffset = long.MaxValue - 7, DestinationOffset = 0, Size = 4 }, _huge, _huge);
        CopyValidator.ValidateWriteBuffer(_huge, long.MaxValue - 1, 1);
        CopyValidator.ValidateWriteBuffer(_huge, 0, int.MaxValue);
    }

    [Fact]
    public void RangeMessages_ReportTheComputedBound()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => CopyValidator.Validate(_copy with { SourceOffset = 1000 }, _buffer, _buffer));

        Assert.Contains("size minus SourceOffset (24), but is 512", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BufferRange_MatchesReference()
    {
        // Each part is drawn around the places the rules change, not uniformly: the buffer is small or long.MaxValue
        // bytes; the source starts at 0, at the end minus the size (ending exactly at the end), one aligned step past
        // that, or anywhere; the destination is adjacent to the source on either side, overlaps it by 4 bytes (the
        // smallest aligned overlap) or 1, starts with it, or ends at the end. An occasional 1-3 byte jitter reaches the
        // alignment rule, and plain values cover the rest. The arithmetic wraps freely: these are only inputs.
        var bufferSize = Gen.OneOf(Gen.Long[1, 256].Select(n => n * 4), Gen.Long[1, 300], Gen.Const(long.MaxValue));
        var length = Gen.OneOf(Gen.Long[1, 64].Select(n => n * 4), Gen.Long[-8, 8], Gen.Long);
        var jitter = Gen.Frequency((8, Gen.Const(0L)), (1, Gen.Long[-3, 3]));
        Gen<(long Size, long Source, long Destination, long Length, bool Same)> cases =
            Gen.Select(bufferSize, length, Gen.Int[0, 3], Gen.Int[0, 7], Gen.Long, jitter, jitter, Gen.Bool)
                .Select(c =>
                {
                    (long size, long len, int sourceShape, int destinationShape, long any, long j1, long j2, bool same) = c;
                    long src = unchecked(sourceShape switch
                    {
                        0 => 0,
                        1 => size - len,
                        2 => size - len + 4,
                        _ => any,
                    } + j1);
                    long dst = unchecked(destinationShape switch
                    {
                        0 => src + len,
                        1 => src - len,
                        2 => src + len - 4,
                        3 => src - len + 4,
                        4 => src + len - 1,
                        5 => src,
                        6 => size - len,
                        _ => any / 3,
                    } + j2);
                    return (size, src, dst, len, same);
                });

        cases.Sample(
            c =>
            {
                BufferDescription buffer = _buffer with { Size = c.Size };
                BufferCopy copy = _copy with { Destination = c.Same ? _bufferA : _bufferB, SourceOffset = c.Source, DestinationOffset = c.Destination, Size = c.Length };
                AssertMatches(ReferenceCopyIsValid(c.Size, c.Same, c.Source, c.Destination, c.Length), () => CopyValidator.Validate(copy, buffer, buffer));

                int dataLength = (int)Math.Clamp(c.Length, int.MinValue, int.MaxValue);
                AssertMatches(ReferenceWriteIsValid(c.Size, c.Source, dataLength), () => CopyValidator.ValidateWriteBuffer(buffer, c.Source, dataLength));
            },
            iter: 20_000);
    }

    [Fact]
    public void ValidCopy_DoesNotAllocate()
    {
        BufferCopy copy = _copy with { Destination = _bufferA, SourceOffset = 0, DestinationOffset = 512, Size = 512 };
        BufferDescription buffer = _buffer;
        ValidateAll(copy, buffer);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            ValidateAll(copy, buffer);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static void ValidateAll(in BufferCopy copy, in BufferDescription buffer)
    {
        CopyValidator.Validate(copy, buffer, buffer);
        CopyValidator.ValidateWriteBuffer(buffer, 3, 1021);
    }

    // The rules in exact integer arithmetic (Int128 holds every sum of two longs), written from the table rather than
    // from the validator's code.
    private static bool ReferenceCopyIsValid(long size, bool same, long src, long dst, long len)
    {
        bool Inside(long offset) => offset >= 0 && (Int128)offset + len <= size;
        bool aligned = len % 4 == 0 && src % 4 == 0 && dst % 4 == 0;
        bool overlap = same && (Int128)src < (Int128)dst + len && (Int128)dst < (Int128)src + len;
        return len > 0 && aligned && Inside(src) && Inside(dst) && !overlap;
    }

    private static bool ReferenceWriteIsValid(long size, long offset, int length) =>
        length > 0 && offset >= 0 && (Int128)offset + length <= size;

    private static void AssertMatches(bool valid, Action validate)
    {
        if (valid)
        {
            validate();
        }
        else
        {
            Assert.ThrowsAny<ArgumentException>(validate);
        }
    }

    private static Action Copy(BufferCopy copy) => () => CopyValidator.Validate(copy, _buffer, _buffer);

    private static Action Copy(BufferCopy copy, BufferDescription source, BufferDescription destination) =>
        () => CopyValidator.Validate(copy, source, destination);

    private static Action CopyHuge(BufferCopy copy) => () => CopyValidator.Validate(copy, _huge, _huge);

    private static Action Write(BufferDescription target, long offset, int length) => () => CopyValidator.ValidateWriteBuffer(target, offset, length);

    private static void AssertRejected(Type exceptionType, string paramName, string field, Action validate)
    {
        // Exact type: an ArgumentOutOfRangeException where an ArgumentException is documented fails, and vice versa.
        var exception = (ArgumentException)Assert.Throws(exceptionType, validate);

        Assert.Equal(paramName, exception.ParamName);
        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
        Assert.Contains("debug name 'probe'", exception.Message, StringComparison.Ordinal);
    }
}

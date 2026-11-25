using System;
using Xunit;

namespace BrutalEngine.Core.Tests;

public sealed class ResultTests
{
    private static readonly EngineError _sampleError = new(ErrorKind.Unavailable, "No audio device.");

    [Fact]
    public void Success_IsSuccess()
    {
        Result result = Result.Success;

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.False(result.TryGetError(out _));
        result.ThrowIfFailed();
    }

    [Fact]
    public void Failure_CarriesError()
    {
        var result = Result.Failure(_sampleError);

        Assert.True(result.IsFailure);
        Assert.Equal(_sampleError, result.Error);
        Assert.True(result.TryGetError(out EngineError error));
        Assert.Equal(_sampleError, error);
    }

    [Fact]
    public void ImplicitConversionFromError_IsFailure()
    {
        Result result = _sampleError;

        Assert.True(result.IsFailure);
        Assert.Equal(_sampleError, result.Error);
    }

    [Fact]
    public void Default_IsFailureWithUnspecifiedError()
    {
        Result result = default;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Unspecified, result.Error.Kind);
    }

    [Fact]
    public void Error_OnSuccess_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Result.Success.Error);
    }

    [Fact]
    public void ThrowIfFailed_OnFailure_ThrowsWithError()
    {
        ResultFailedException exception = Assert.Throws<ResultFailedException>(() => Result.Failure(_sampleError).ThrowIfFailed());

        Assert.Equal(_sampleError, exception.Error);
        Assert.Contains("No audio device.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_DescribesOutcome()
    {
        Assert.Equal("Success", Result.Success.ToString());
        Assert.Equal("Failure(Unavailable: No audio device.)", Result.Failure(_sampleError).ToString());
    }
}

public sealed class ResultOfTTests
{
    private static readonly EngineError _sampleError = new(ErrorKind.NotFound, "Font not found.");

    [Fact]
    public void Success_CarriesValue()
    {
        Result<int> result = new(42);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(42, result.Value);
        Assert.True(result.TryGetValue(out int value));
        Assert.Equal(42, value);
        Assert.False(result.TryGetError(out _));
        Assert.Equal(42, result.GetValueOrDefault(7));
    }

    [Fact]
    public void Failure_CarriesError()
    {
        Result<int> result = new(_sampleError);

        Assert.True(result.IsFailure);
        Assert.Equal(_sampleError, result.Error);
        Assert.False(result.TryGetValue(out _));
        Assert.True(result.TryGetError(out EngineError error));
        Assert.Equal(_sampleError, error);
        Assert.Equal(7, result.GetValueOrDefault(7));
    }

    [Fact]
    public void ImplicitConversions_CreateSuccessAndFailure()
    {
        static Result<string> Lookup(bool found) => found ? "value" : _sampleError;

        Assert.Equal("value", Lookup(true).Value);
        Assert.Equal(_sampleError, Lookup(false).Error);
    }

    [Fact]
    public void Default_IsFailureWithUnspecifiedError()
    {
        Result<int> result = default;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Unspecified, result.Error.Kind);
    }

    [Fact]
    public void Value_OnFailure_ThrowsWithError()
    {
        Result<int> result = new(_sampleError);

        ResultFailedException exception = Assert.Throws<ResultFailedException>(() => result.Value);
        Assert.Equal(_sampleError, exception.Error);
    }

    [Fact]
    public void Error_OnSuccess_Throws()
    {
        Result<int> result = new(1);

        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void ToResult_PreservesOutcome()
    {
        Assert.True(new Result<int>(1).ToResult().IsSuccess);
        Assert.Equal(_sampleError, new Result<int>(_sampleError).ToResult().Error);
    }

    [Fact]
    public void ToString_DescribesOutcome()
    {
        Assert.Equal("Success(5)", new Result<int>(5).ToString());
        Assert.Equal("Failure(NotFound: Font not found.)", new Result<int>(_sampleError).ToString());
    }

    [Fact]
    public void CreatingAndInspectingResults_DoesNotAllocate()
    {
        var error = new EngineError(ErrorKind.Timeout, "Timed out.");
        int sum = Consume(error);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            sum += Consume(error);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
        Assert.NotEqual(0, sum);

        static int Consume(EngineError error)
        {
            Result<int> ok = 3;
            Result<int> failed = error;
            Result plain = error;
            int total = ok.Value + failed.GetValueOrDefault(1);
            if (failed.TryGetError(out EngineError e) && plain.IsFailure && Result.Success.IsSuccess)
            {
                total += (int)e.Kind;
            }

            return total;
        }
    }
}

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using BrutalEngine.Core.Logging;
using Xunit;

namespace BrutalEngine.Core.Tests.Logging;

public sealed class MessageBufferCacheTests
{
    private const int BufferBytes = 4096;
    private const int SlotCount = 4;

    [Fact]
    public void Rent_AfterReturn_ReusesBuffer()
    {
        RecordingPool pool = new();
        MessageBufferCache cache = new(BufferBytes, SlotCount, pool);

        byte[] first = cache.Rent();
        cache.Return(first);
        byte[] second = cache.Rent();

        Assert.Same(first, second);
        Assert.Equal(BufferBytes, first.Length);
        Assert.Equal(0, pool.RentCount);
        Assert.Equal(0, pool.ReturnCount);
    }

    [Fact]
    public void Rent_WhileOthersHeld_ReturnsDistinctBuffers()
    {
        MessageBufferCache cache = new(BufferBytes, SlotCount, new RecordingPool());

        byte[][] held = RentAll(cache, SlotCount);

        Assert.Equal(SlotCount, new HashSet<byte[]>(held, ReferenceEqualityComparer.Instance).Count);
    }

    [Fact]
    public void Rent_AllSlotsTaken_FallsBackToSharedPool()
    {
        RecordingPool pool = new();
        MessageBufferCache cache = new(BufferBytes, SlotCount, pool);
        byte[][] cached = RentAll(cache, SlotCount);

        byte[] extra = cache.Rent();

        Assert.Equal(1, pool.RentCount);
        Assert.Same(pool.LastRented, extra);
        Assert.Equal(BufferBytes, pool.LastMinimumLength);
        foreach (byte[] buffer in cached)
        {
            Assert.NotSame(buffer, extra);
        }
    }

    [Fact]
    public void Return_FallbackBuffer_GoesToSharedPool()
    {
        RecordingPool pool = new();
        MessageBufferCache cache = new(BufferBytes, SlotCount, pool);
        byte[][] cached = RentAll(cache, SlotCount);
        byte[] extra = cache.Rent();

        cache.Return(extra);

        // The fallback buffer went back to the pool and took no slot: with every slot still held, the next rent
        // falls back again instead of handing out the returned buffer as a cached one.
        Assert.Equal(1, pool.ReturnCount);
        Assert.Same(extra, pool.LastReturned);
        _ = cache.Rent();
        Assert.Equal(2, pool.RentCount);
        Assert.NotNull(cached);
    }

    [Fact]
    public void Return_CachedBuffer_NeverGoesToSharedPool()
    {
        RecordingPool pool = new();
        MessageBufferCache cache = new(BufferBytes, SlotCount, pool);
        byte[][] cached = RentAll(cache, SlotCount);

        foreach (byte[] buffer in cached)
        {
            cache.Return(buffer);
        }

        // Every slot is free again and reuses its own buffer.
        byte[][] again = RentAll(cache, SlotCount);
        Assert.Equal(0, pool.ReturnCount);
        Assert.Equal(0, pool.RentCount);
        for (int i = 0; i < SlotCount; i++)
        {
            Assert.Same(cached[i], again[i]);
        }
    }

    [Fact]
    public void Return_CachedBufferTwice_Throws()
    {
        MessageBufferCache cache = new(BufferBytes, SlotCount, new RecordingPool());
        byte[] buffer = cache.Rent();
        cache.Return(buffer);

        _ = Assert.Throws<InvalidOperationException>(() => cache.Return(buffer));
    }

    [Fact]
    public void Return_Null_Throws()
    {
        MessageBufferCache cache = new(BufferBytes, SlotCount, new RecordingPool());

        _ = Assert.Throws<ArgumentNullException>(() => cache.Return(null!));
    }

    [Theory]
    [InlineData(0, SlotCount)]
    [InlineData(-1, SlotCount)]
    [InlineData(BufferBytes, 0)]
    [InlineData(BufferBytes, -1)]
    public void Constructor_NonPositiveSize_Throws(int bufferBytes, int slotCount)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new MessageBufferCache(bufferBytes, slotCount, new RecordingPool()));
    }

    [Fact]
    public void Constructor_NullFallback_Throws()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new MessageBufferCache(BufferBytes, SlotCount, null!));
    }

    [Fact]
    public void ConcurrentRentReturn_NeverSharesABuffer()
    {
        // More threads than slots, so cached and fallback buffers are rented and returned at the same time.
        const int Threads = 8;
        const int Iterations = 2000;
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingPool pool = new();
        MessageBufferCache cache = new(BufferBytes, SlotCount, pool);
        ConcurrentDictionary<byte[], int> holders = new(ReferenceEqualityComparer.Instance);
        ConcurrentDictionary<byte[], byte> seen = new(ReferenceEqualityComparer.Instance);
        ConcurrentQueue<Exception> failures = new();
        using Barrier start = new(Threads);

        var threads = new Thread[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int id = t + 1;
            threads[t] = new Thread(() =>
            {
                try
                {
                    // Every thread arrives before any rents, so the threads overlap from the first iteration.
                    start.SignalAndWait(token);
                    for (int i = 0; i < Iterations; i++)
                    {
                        byte[] buffer = cache.Rent();
                        _ = seen.TryAdd(buffer, 0);
                        if (!holders.TryAdd(buffer, id))
                        {
                            throw new InvalidOperationException($"Thread {id} rented a buffer that thread {holders.GetValueOrDefault(buffer)} holds.");
                        }

                        // Write and check a marker, so a second holder writing the same buffer is seen too.
                        buffer.AsSpan(0, 64).Fill((byte)id);
                        _ = Thread.Yield();
                        if (buffer.AsSpan(0, 64).ContainsAnyExcept((byte)id))
                        {
                            throw new InvalidOperationException($"Another thread wrote to the buffer thread {id} holds.");
                        }

                        _ = holders.TryRemove(buffer, out _);
                        cache.Return(buffer);
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            });
            threads[t].Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromMinutes(1)), "A worker thread didn't finish.");
        }

        Assert.Empty(failures);

        // Every fallback buffer went back to the pool, no cached buffer did, and the cache made at most one buffer
        // per slot.
        Assert.Equal(pool.RentCount, pool.ReturnCount);
        Assert.True(pool.RentedAll(pool.Returned), "A buffer returned to the pool didn't come from it.");
        int cachedCount = 0;
        foreach (byte[] buffer in seen.Keys)
        {
            cachedCount += pool.Rented(buffer) ? 0 : 1;
        }

        Assert.InRange(cachedCount, 1, SlotCount);
    }

    [Fact]
    public void RentReturn_SteadyState_DoesNotAllocate()
    {
        MessageBufferCache cache = new(BufferBytes, SlotCount, ArrayPool<byte>.Shared);
        byte[][] warm = RentAll(cache, SlotCount);
        foreach (byte[] buffer in warm)
        {
            cache.Return(buffer);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            byte[] first = cache.Rent();
            byte[] second = cache.Rent();
            cache.Return(first);
            cache.Return(second);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static byte[][] RentAll(MessageBufferCache cache, int count)
    {
        byte[][] buffers = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            buffers[i] = cache.Rent();
        }

        return buffers;
    }

    /// <summary>A fallback pool that allocates every rent and records what it rented and what came back.</summary>
    private sealed class RecordingPool : ArrayPool<byte>
    {
        private readonly ConcurrentDictionary<byte[], byte> _rented = new(ReferenceEqualityComparer.Instance);
        private readonly ConcurrentQueue<byte[]> _returned = new();
        private int _rentCount;
        private int _returnCount;
        private byte[]? _lastRented;
        private byte[]? _lastReturned;
        private int _lastMinimumLength;

        public int RentCount => Volatile.Read(ref _rentCount);

        public int ReturnCount => Volatile.Read(ref _returnCount);

        public byte[]? LastRented => _lastRented;

        public byte[]? LastReturned => _lastReturned;

        public int LastMinimumLength => _lastMinimumLength;

        public IEnumerable<byte[]> Returned => _returned;

        public bool Rented(byte[] buffer) => _rented.ContainsKey(buffer);

        public bool RentedAll(IEnumerable<byte[]> buffers)
        {
            foreach (byte[] buffer in buffers)
            {
                if (!Rented(buffer))
                {
                    return false;
                }
            }

            return true;
        }

        public override byte[] Rent(int minimumLength)
        {
            byte[] buffer = new byte[minimumLength];
            _ = _rented.TryAdd(buffer, 0);
            _lastRented = buffer;
            _lastMinimumLength = minimumLength;
            _ = Interlocked.Increment(ref _rentCount);
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            _returned.Enqueue(array);
            _lastReturned = array;
            _ = Interlocked.Increment(ref _returnCount);
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using BrutalEngine.Core;
using BrutalEngine.Platform.Vfs;
using Xunit;

namespace BrutalEngine.Platform.Tests.Vfs;

public sealed class VirtualFileSystemConcurrencyTests
{
    private const int Fillers = 8;
    private const int MountCycles = 10_000;
    private const int Readers = 4;

    [Fact]
    public void ConcurrentReadsDuringMount_NeverFail()
    {
        // "a" is mounted for the whole test, after eight filler mounts c0..c7. One thread keeps unmounting and
        // remounting the fillers and toggling "b", so "a" keeps changing position in the mount table while readers
        // read through it. Every read of a:// must succeed with a's bytes; a read of b:// or c<k>:// must either
        // succeed with that mount's own bytes or be NotFound, never another failure, an exception or another mount's
        // bytes. The threads start together through a barrier, and the readers keep reading until the mounter is done.
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        VirtualFileSystem vfs = new();
        var fillers = new InMemoryFileSystem[Fillers];
        for (int k = 0; k < Fillers; k++)
        {
            fillers[k] = Seeded((byte)(10 + k));
            vfs.Mount(Filler(k), fillers[k]);
        }

        InMemoryFileSystem a = Seeded(1);
        InMemoryFileSystem b = Seeded(2);
        vfs.Mount("a", a);
        var aPath = VfsPath.Parse("a://f");
        var bPath = VfsPath.Parse("b://f");
        var fillerPaths = new VfsPath[Fillers];
        for (int k = 0; k < Fillers; k++)
        {
            fillerPaths[k] = VfsPath.Parse(Filler(k) + "://f");
        }

        ConcurrentQueue<Exception> failures = new();
        using Barrier barrier = new(Readers + 1);
        int mounterDone = 0;
        int[] reads = new int[Readers];
        List<Thread> threads =
        [
            new(() => Guard(failures, () =>
            {
                barrier.SignalAndWait(cancellation);
                try
                {
                    for (int i = 0; i < MountCycles; i++)
                    {
                        int k = i % Fillers;
                        Check(vfs.Unmount(Filler(k)), "filler unmount");
                        vfs.Mount(Filler(k), fillers[k]);
                        if (i % 2 == 0)
                        {
                            vfs.Mount("b", b);
                        }
                        else
                        {
                            Check(vfs.Unmount("b"), "b unmount");
                        }
                    }
                }
                finally
                {
                    Volatile.Write(ref mounterDone, 1);
                }
            })),
        ];

        for (int r = 0; r < Readers; r++)
        {
            int reader = r;
            threads.Add(new Thread(() => Guard(failures, () =>
            {
                barrier.SignalAndWait(cancellation);
                int i = 0;
                do
                {
                    Result<byte[]> read = vfs.ReadAllBytes(aPath);
                    Check(read.IsSuccess && read.Value is [1], $"a:// read: {Describe(read)}");
                    Check(vfs.IsMounted("a"), "a not mounted");
                    Check(vfs.Exists(aPath) is { IsSuccess: true, Value: true }, "a:// missing");
                    CheckOptional(vfs.ReadAllBytes(bPath), 2, "b://");
                    int k = i % Fillers;
                    CheckOptional(vfs.ReadAllBytes(fillerPaths[k]), (byte)(10 + k), Filler(k));
                    i++;
                }
                while (Volatile.Read(ref mounterDone) == 0);

                reads[reader] = i;
            })));
        }

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "A worker thread did not finish.");
        }

        Assert.Empty(failures);
        Assert.All(reads, count => Assert.True(count > 0));

        // The mounter's last cycle unmounted "b" and left every filler mounted.
        Assert.False(vfs.IsMounted("b"));
        for (int k = 0; k < Fillers; k++)
        {
            Assert.True(vfs.IsMounted(Filler(k)));
        }
    }

    private static string Filler(int k) => "c" + (char)('0' + k);

    private static InMemoryFileSystem Seeded(byte id)
    {
        InMemoryFileSystem fileSystem = new();
        Assert.True(fileSystem.WriteAllBytes("f", [id]).IsSuccess);
        fileSystem.Freeze();
        return fileSystem;
    }

    private static void CheckOptional(Result<byte[]> read, byte id, string scheme)
    {
        bool ok = read.IsSuccess ? read.Value is [var only] && only == id : read.Error.Kind == ErrorKind.NotFound;
        Check(ok, $"{scheme} read: {Describe(read)}");
    }

    private static string Describe(Result<byte[]> read) =>
        read.IsSuccess ? "[" + string.Join(", ", read.Value) + "]" : read.Error.ToString();

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Guard(ConcurrentQueue<Exception> failures, Action body)
    {
        try
        {
            body();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failures.Enqueue(exception);
        }
    }
}

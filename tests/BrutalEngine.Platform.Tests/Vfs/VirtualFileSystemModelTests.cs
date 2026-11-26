using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BrutalEngine.Core;
using BrutalEngine.Platform.Vfs;
using CsCheck;
using Xunit;

namespace BrutalEngine.Platform.Tests.Vfs;

public sealed class VirtualFileSystemModelTests
{
    private const int Iterations = 3_000;

    // Schemes that are prefixes of each other, so routing by prefix or substring sends a call to the wrong mount.
    private static readonly string[] _schemes = ["a", "ab", "b", "ba"];

    // Few relative paths, so random steps keep hitting the same entries: the root, files and directories at
    // several depths, and names that are prefixes of each other.
    private static readonly string[] _paths = ["", "x", "y", "x/y", "x/z", "x/y/z", "y/x", "xy"];

    private static readonly byte[][] _contents = [[], [1], [1, 2], [3, 3, 3]];

    private enum Op
    {
        Mount,
        Unmount,
        IsMounted,
        Exists,
        GetEntryKind,
        ReadAllBytes,
        OpenRead,
        WriteAllBytes,
        Delete,
        CreateDirectory,
        List,
    }

    [Fact]
    public void OperationsMatchReferenceModel()
    {
        Gen<(Op Op, int Scheme, int Path, int Contents)> step = Gen.Select(
            Gen.Frequency(
                (1, OneOf(Op.Mount, Op.Mount, Op.Unmount, Op.IsMounted)),
                (4, OneOf(Op.WriteAllBytes, Op.CreateDirectory, Op.Delete)),
                (4, OneOf(Op.Exists, Op.GetEntryKind, Op.ReadAllBytes, Op.OpenRead, Op.List))),
            Gen.Int[0, _schemes.Length - 1],
            Gen.Int[0, _paths.Length - 1],
            Gen.Int[0, _contents.Length - 1]);

        // Every path of every scheme, parsed once: after each step the whole state is compared, not only what the
        // step touched.
        VfsPath[] everyPath = [.. _schemes.SelectMany(s => _paths.Select(p => VfsPath.Parse(s + "://" + p)))];

        // How often the interesting outcomes happened, so a generator change that stops reaching them fails.
        int filesRead = 0, nonEmptyListings = 0, ioFailures = 0, doubleMounts = 0, unmountedCalls = 0;

        // Sample runs on several threads. That is safe here: each sample builds its own virtual file system, file
        // systems and model, the static arrays and everyPath are only read, and the counters use Interlocked.
        step.Array[10, 80].Sample(steps =>
        {
            // One file system per scheme for the whole run, remounted after an unmount, so its contents persist
            // there and in the model alike.
            VirtualFileSystem vfs = new();
            Dictionary<string, InMemoryFileSystem> fileSystems = _schemes.ToDictionary(s => s, _ => new InMemoryFileSystem());
            Model model = new();

            // Every scheme from the start, so a scheme and its prefix are both mounted in most steps. Mounting is
            // drawn twice as often as unmounting, and each unmount and remount reorders the mount table.
            foreach (string scheme in _schemes)
            {
                vfs.Mount(scheme, fileSystems[scheme]);
                model.Mounted.Add(scheme);
            }

            foreach ((Op op, int schemeIndex, int pathIndex, int contentsIndex) in steps)
            {
                string scheme = _schemes[schemeIndex];
                var path = VfsPath.Parse(scheme + "://" + _paths[pathIndex]);
                string at = $"{op} {path}";
                switch (op)
                {
                    case Op.Mount:
                        if (model.Mounted.Contains(scheme))
                        {
                            Assert.Throws<InvalidOperationException>(() => vfs.Mount(scheme, fileSystems[scheme]));
                            Interlocked.Increment(ref doubleMounts);
                        }
                        else
                        {
                            vfs.Mount(scheme, fileSystems[scheme]);
                            model.Mounted.Add(scheme);
                        }

                        break;
                    case Op.Unmount:
                        Assert.Equal(model.Mounted.Remove(scheme), vfs.Unmount(scheme));
                        break;
                    case Op.IsMounted:
                        Assert.Equal(model.Mounted.Contains(scheme), vfs.IsMounted(scheme));
                        break;
                    case Op.Exists:
                        AssertSame(at, model.GetEntryKind(path).Select(kind => kind != VfsEntryKind.None), vfs.Exists(path));
                        break;
                    case Op.GetEntryKind:
                        AssertSame(at, model.GetEntryKind(path), vfs.GetEntryKind(path));
                        break;
                    case Op.ReadAllBytes:
                        Expected<byte[]> read = model.Read(path);
                        AssertSame(at, read, vfs.ReadAllBytes(path), Bytes);
                        Count(read.Ok && read.Value!.Length > 0, ref filesRead);
                        break;
                    case Op.OpenRead:
                        Result<Stream> stream = vfs.OpenRead(path);
                        try
                        {
                            Expected<byte[]> opened = model.Read(path);
                            AssertSame(at, opened, stream, ReadToEnd);
                            Count(opened.Ok && opened.Value!.Length > 0, ref filesRead);
                        }
                        finally
                        {
                            if (stream.IsSuccess)
                            {
                                stream.Value.Dispose();
                            }
                        }

                        break;
                    case Op.WriteAllBytes:
                        AssertSame(at, model.Write(path, _contents[contentsIndex]), vfs.WriteAllBytes(path, _contents[contentsIndex]));
                        break;
                    case Op.Delete:
                        Expected<bool> deleted = model.Delete(path);
                        AssertSame(at, deleted, vfs.Delete(path));
                        Count(!deleted.Ok && deleted.Kind == ErrorKind.Io, ref ioFailures);
                        break;
                    case Op.CreateDirectory:
                        AssertSame(at, model.CreateDirectory(path), vfs.CreateDirectory(path));
                        break;
                    case Op.List:
                        Expected<IReadOnlyList<VfsEntry>> listed = model.List(path);
                        AssertSame(at, listed, vfs.List(path), Entries);
                        Count(listed.Ok && listed.Value!.Count > 0, ref nonEmptyListings);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(steps), op, "Unknown operation.");
                }

                Count(op > Op.IsMounted && !model.Mounted.Contains(scheme), ref unmountedCalls);

                // After every step, the whole state matches the model, so a failure names the step that diverged:
                // each mounted scheme's root listing, the kind of every path, and the bytes of every file; on an
                // unmounted scheme each of these fails.
                foreach (string s in _schemes)
                {
                    var root = VfsPath.Parse(s + "://");
                    AssertSame($"{at}, then List {root}", model.List(root), vfs.List(root), Entries);
                }

                foreach (VfsPath p in everyPath)
                {
                    Expected<VfsEntryKind> kind = model.GetEntryKind(p);
                    AssertSame($"{at}, then GetEntryKind {p}", kind, vfs.GetEntryKind(p));
                    if (kind is { Ok: true, Value: VfsEntryKind.File })
                    {
                        AssertSame($"{at}, then ReadAllBytes {p}", model.Read(p), vfs.ReadAllBytes(p), Bytes);
                    }
                }
            }
        }, iter: Iterations);

        string counts = $"files read {filesRead}, non-empty listings {nonEmptyListings}, Io failures {ioFailures}, double mounts {doubleMounts}, unmounted calls {unmountedCalls}";
        Assert.True(
            Math.Min(Math.Min(Math.Min(filesRead, nonEmptyListings), Math.Min(ioFailures, doubleMounts)), unmountedCalls) > Iterations / 10,
            $"An outcome happened too rarely in {Iterations} runs: {counts}.");
    }

    private static void Count(bool happened, ref int counter)
    {
        if (happened)
        {
            Interlocked.Increment(ref counter);
        }
    }

    private static Gen<T> OneOf<T>(params T[] values) => Gen.Int[0, values.Length - 1].Select(i => values[i]);

    private static string Bytes(byte[] bytes) => string.Join(',', bytes);

    private static string ReadToEnd(Stream stream)
    {
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return Bytes(copy.ToArray());
    }

    private static string Entries(IReadOnlyList<VfsEntry> entries) =>
        string.Join(';', entries.Select(e => $"{e.Name}|{e.Kind}|{e.Length}"));

    private static void AssertSame<T>(string at, Expected<T> expected, Result<T> actual)
        where T : notnull =>
        AssertSame(at, expected, actual, value => value.ToString()!);

    private static void AssertSame<T, TActual>(string at, Expected<T> expected, Result<TActual> actual, Func<TActual, string> describe)
        where TActual : notnull
    {
        string actualText = actual.IsSuccess ? "ok " + describe(actual.Value) : "fail " + actual.Error.Kind;
        Assert.True(string.Equals(expected.Describe(), actualText, StringComparison.Ordinal), $"{at}: expected {expected.Describe()}, got {actualText}");
    }

    private static void AssertSame(string at, Expected<bool> expected, Result actual)
    {
        string actualText = actual.IsSuccess ? "ok True" : "fail " + actual.Error.Kind;
        Assert.True(string.Equals(expected.Describe(), actualText, StringComparison.Ordinal), $"{at}: expected {expected.Describe()}, got {actualText}");
    }

    /// <summary>What the model predicts: a value, or a failure of a kind.</summary>
    private readonly record struct Expected<T>(bool Ok, T? Value, ErrorKind Kind, Func<T, string>? Format = null)
    {
        public static Expected<T> Fail(ErrorKind kind) => new(false, default, kind);

        public Expected<TOut> Select<TOut>(Func<T, TOut> map) => Ok ? new(true, map(Value!), Kind) : Expected<TOut>.Fail(Kind);

        public string Describe() => Ok ? "ok " + (Format ?? (v => v!.ToString()!))(Value!) : "fail " + Kind;
    }

    /// <summary>
    /// The reference model, written from docs/design/platform.md section 5 without the code under test: per scheme,
    /// a flat map from relative path to contents (<see langword="null"/> for a directory), where "" is the root.
    /// </summary>
    private sealed class Model
    {
        private readonly Dictionary<string, Dictionary<string, byte[]?>> _trees =
            _schemes.ToDictionary(s => s, _ => new Dictionary<string, byte[]?>(StringComparer.Ordinal) { [""] = null });

        public HashSet<string> Mounted { get; } = new(StringComparer.Ordinal);

        public Expected<VfsEntryKind> GetEntryKind(VfsPath path)
        {
            if (Tree(path) is not { } tree)
            {
                return Expected<VfsEntryKind>.Fail(ErrorKind.NotFound);
            }

            VfsEntryKind kind = !tree.TryGetValue(path.RelativePath, out byte[]? contents) ? VfsEntryKind.None
                : contents is null ? VfsEntryKind.Directory
                : VfsEntryKind.File;
            return new(true, kind, default);
        }

        public Expected<byte[]> Read(VfsPath path) =>
            Tree(path) is { } tree && tree.TryGetValue(path.RelativePath, out byte[]? contents) && contents is not null
                ? new(true, contents, default, Bytes)
                : Expected<byte[]>.Fail(ErrorKind.NotFound);

        public Expected<bool> Write(VfsPath path, byte[] contents)
        {
            string p = path.RelativePath;
            if (Tree(path) is not { } tree || p.Length == 0 || AncestorIsFile(tree, p)
                || (tree.TryGetValue(p, out byte[]? existing) && existing is null))
            {
                return Expected<bool>.Fail(ErrorKind.NotFound);
            }

            foreach (string ancestor in Ancestors(p))
            {
                tree.TryAdd(ancestor, null);
            }

            tree[p] = contents;
            return new(true, true, default);
        }

        public Expected<bool> Delete(VfsPath path)
        {
            string p = path.RelativePath;
            if (Tree(path) is not { } tree)
            {
                return Expected<bool>.Fail(ErrorKind.NotFound);
            }

            if (p.Length == 0)
            {
                return Expected<bool>.Fail(ErrorKind.Unsupported);
            }

            if (!tree.ContainsKey(p))
            {
                return Expected<bool>.Fail(ErrorKind.NotFound);
            }

            if (tree.Keys.Any(k => k.StartsWith(p + "/", StringComparison.Ordinal)))
            {
                return Expected<bool>.Fail(ErrorKind.Io);
            }

            tree.Remove(p);
            return new(true, true, default);
        }

        public Expected<bool> CreateDirectory(VfsPath path)
        {
            string p = path.RelativePath;
            if (Tree(path) is not { } tree || AncestorIsFile(tree, p) || (tree.TryGetValue(p, out byte[]? existing) && existing is not null))
            {
                return Expected<bool>.Fail(ErrorKind.NotFound);
            }

            foreach (string ancestor in Ancestors(p))
            {
                tree.TryAdd(ancestor, null);
            }

            tree.TryAdd(p, null);
            return new(true, true, default);
        }

        public Expected<IReadOnlyList<VfsEntry>> List(VfsPath path)
        {
            string p = path.RelativePath;
            if (Tree(path) is not { } tree || !tree.TryGetValue(p, out byte[]? self) || self is not null)
            {
                return Expected<IReadOnlyList<VfsEntry>>.Fail(ErrorKind.NotFound);
            }

            List<string> children = [.. tree.Keys.Where(k => k.Length != 0 && Parent(k) == p)];
            children.Sort(string.CompareOrdinal);
            VfsEntry[] entries =
            [
                .. children.Select(k =>
                {
                    string name = p.Length == 0 ? k : k[(p.Length + 1)..];
                    return tree[k] is { } file ? new VfsEntry(name, VfsEntryKind.File, file.Length) : new VfsEntry(name, VfsEntryKind.Directory, 0);
                }),
            ];
            return new(true, entries, default, Entries);
        }

        private static string Parent(string path) => path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : "";

        private static IEnumerable<string> Ancestors(string path)
        {
            string[] segments = path.Split('/');
            for (int i = 1; i < segments.Length; i++)
            {
                yield return string.Join('/', segments[..i]);
            }
        }

        private static bool AncestorIsFile(Dictionary<string, byte[]?> tree, string path) =>
            Ancestors(path).Any(a => tree.TryGetValue(a, out byte[]? contents) && contents is not null);

        private Dictionary<string, byte[]?>? Tree(VfsPath path) => Mounted.Contains(path.Scheme) ? _trees[path.Scheme] : null;
    }
}

using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Brutal.Concurrency.Jobs;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace S5;

public struct Position { public float X, Y; }
public struct Velocity { public float X, Y; }
public struct Accel { public float X, Y; }
public struct Health { public int V; }
public struct T0 { public byte B; } public struct T1 { public byte B; } public struct T2 { public byte B; }
public struct T3 { public byte B; } public struct T4 { public byte B; }

public struct IntegrateFn : IForEach<Position, Velocity>
{
    public float Dt;
    public void Execute(ref Position p, ref Velocity v) { p.X += v.X * Dt; p.Y += v.Y * Dt; }
}

public static class Kernels
{
    public const float Dt = 1f / 60f;
    public static void Iter2Spans(World w, Query q)
    {
        foreach (var ch in w.Chunks(q))
        {
            var p = ch.Write<Position>(); var v = ch.Read<Velocity>();
            for (int i = 0; i < p.Length; i++) { p[i].X += v[i].X * Dt; p[i].Y += v[i].Y * Dt; }
        }
    }
    public static void Iter2ForEach(World w, Query q) { var f = new IntegrateFn { Dt = Dt }; w.ForEach<IntegrateFn, Position, Velocity>(q, ref f); }
    public static void Iter2Each(World w, Query q)
    {
        foreach (var e in w.Each<Position, Velocity>(q)) { e.A.X += e.B.X * Dt; e.A.Y += e.B.Y * Dt; }
    }
    /// what a source generator would emit: ref arithmetic, no bounds checks
    public static void Iter2Unchecked(World w, Query q)
    {
        foreach (var ch in w.Chunks(q))
        {
            var ps = ch.Write<Position>(); var vs = ch.Read<Velocity>();
            ref Position p = ref MemoryMarshal.GetReference(ps); ref Velocity v = ref MemoryMarshal.GetReference(vs);
            for (int i = 0; i < ps.Length; i++) { ref var pp = ref Unsafe.Add(ref p, i); ref readonly var vv = ref Unsafe.Add(ref v, i); pp.X += vv.X * Dt; pp.Y += vv.Y * Dt; }
        }
    }
    /// hand-SIMD over the float columns (Position/Velocity are 2 x float, layout-compatible)
    public static void Iter2Simd(World w, Query q)
    {
        var dt = Vector256.Create(Dt);
        foreach (var ch in w.Chunks(q))
        {
            var p = MemoryMarshal.Cast<Position, float>(ch.Write<Position>()); var v = MemoryMarshal.Cast<Velocity, float>(ch.Read<Velocity>());
            ref float pr = ref MemoryMarshal.GetReference(p); ref float vr = ref MemoryMarshal.GetReference(v);
            int i = 0, n = p.Length;
            for (; i <= n - 8; i += 8)
            {
                var pv = Vector256.LoadUnsafe(ref pr, (nuint)i); var vv = Vector256.LoadUnsafe(ref vr, (nuint)i);
                (pv + vv * dt).StoreUnsafe(ref pr, (nuint)i);
            }
            for (; i < n; i++) Unsafe.Add(ref pr, i) += Unsafe.Add(ref vr, i) * Dt;
        }
    }
    public static void Iter1(World w, Query q)
    {
        foreach (var ch in w.Chunks(q)) { var p = ch.Write<Position>(); for (int i = 0; i < p.Length; i++) p[i].X += 1f; }
    }
    public static void Iter3(World w, Query q)
    {
        foreach (var ch in w.Chunks(q))
        {
            var p = ch.Write<Position>(); var v = ch.Write<Velocity>(); var a = ch.Read<Accel>();
            for (int i = 0; i < p.Length; i++)
            {
                v[i].X += a[i].X * Dt; v[i].Y += a[i].Y * Dt;
                p[i].X += v[i].X * Dt; p[i].Y += v[i].Y * Dt;
            }
        }
    }
    public static World Make2(StorageMode m, int n)
    {
        var w = new World(m, n);
        for (int i = 0; i < n; i++) w.Spawn(new Position { X = i }, new Velocity { X = 1, Y = 2 });
        return w;
    }
    public static World Make3(StorageMode m, int n)
    {
        var w = new World(m, n);
        for (int i = 0; i < n; i++) w.Spawn(new Position { X = i }, new Velocity { X = 1 }, new Accel { Y = -9.8f });
        return w;
    }
    /// 1M Position+Velocity entities spread over 32 archetypes (5 tag bits)
    public static World MakeFragmented(StorageMode m, int n)
    {
        var w = new World(m, n);
        for (int i = 0; i < n; i++)
        {
            var e = w.Spawn(new Position { X = i }, new Velocity { X = 1 });
            int bits = i & 31;
            if ((bits & 1) != 0) w.Add(e, new T0()); if ((bits & 2) != 0) w.Add(e, new T1());
            if ((bits & 4) != 0) w.Add(e, new T2()); if ((bits & 8) != 0) w.Add(e, new T3());
            if ((bits & 16) != 0) w.Add(e, new T4());
        }
        return w;
    }
}

/// Parallel chunk iteration on BRUTAL JobScheduler. Jobs are allocated once and re-queued each frame.
public sealed unsafe class ParallelIntegrate : IDisposable
{
    public struct Slice { public int Arch, Chunk, Start, Count; }
    sealed class Job : IJob
    {
        public World W = null!; public Slice* Slices; public int Begin, End;
        public void Run()
        {
            int pid = TypeId<Position>.Id, vid = TypeId<Velocity>.Id;
            for (int s = Begin; s < End; s++)
            {
                var sl = Slices[s]; var a = W.GetArchetype(sl.Arch); byte* d = a.Chunks[sl.Chunk].Data;
                Position* p = (Position*)(d + a.ColOffset[a.ColumnOf[pid]]) + sl.Start;
                Velocity* v = (Velocity*)(d + a.ColOffset[a.ColumnOf[vid]]) + sl.Start;
                for (int i = 0; i < sl.Count; i++) { p[i].X += v[i].X * Kernels.Dt; p[i].Y += v[i].Y * Kernels.Dt; }
            }
        }
    }
    readonly JobScheduler _sched; readonly Job[] _jobs; Slice* _slices; int _sliceCap;
    public const int MaxSliceRows = 16384;
    public ParallelIntegrate(uint runners)
    {
        _sched = new JobScheduler(runners, "ecs");
        _jobs = new Job[runners]; for (int i = 0; i < runners; i++) _jobs[i] = new Job();
        _sliceCap = 1024; _slices = (Slice*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)(_sliceCap * sizeof(Slice)));
    }
    public void Run(World w, Query q)
    {
        int n = 0; int pid = TypeId<Position>.Id;
        foreach (var ch in w.Chunks(q))
        {
            _ = ch.Write<Position>(); // version bump on main thread
            int arch = ch.Archetype.Id; int ci = ch.ChunkIndex;
            for (int s = 0; s < ch.Count; s += MaxSliceRows)
            {
                if (n == _sliceCap) { _sliceCap *= 2; _slices = (Slice*)System.Runtime.InteropServices.NativeMemory.Realloc(_slices, (nuint)(_sliceCap * sizeof(Slice))); }
                _slices[n++] = new Slice { Arch = arch, Chunk = ci, Start = s, Count = Math.Min(MaxSliceRows, ch.Count - s) };
            }
        }
        int jobs = _jobs.Length;
        for (int j = 0; j < jobs; j++)
        {
            var job = _jobs[j]; job.W = w; job.Slices = _slices; job.Begin = n * j / jobs; job.End = n * (j + 1) / jobs;
            if (job.End > job.Begin) _sched.Queue(job, job.End - job.Begin);
        }
        _sched.ExecuteJobs(); _sched.Wait();
        _ = pid;
    }
    public void Dispose() => System.Runtime.InteropServices.NativeMemory.Free(_slices);
}


[MemoryDiagnoser]
public class IterationBench
{
    [Params(StorageMode.Chunk16K, StorageMode.Chunk64K, StorageMode.Table)] public StorageMode Mode;
    World _w2 = null!, _w3 = null!, _wf = null!; Query _q1 = null!, _q2 = null!, _q3 = null!, _qf = null!;
    [GlobalSetup] public void Setup()
    {
        _w2 = Kernels.Make2(Mode, 1_000_000); _w3 = Kernels.Make3(Mode, 1_000_000); _wf = Kernels.MakeFragmented(Mode, 1_000_000);
        _q1 = _w2.Query<Position>(); _q2 = _w2.Query<Position, Velocity>(); _q3 = _w3.Query<Position, Velocity, Accel>(); _qf = _wf.Query<Position, Velocity>();
    }
    [GlobalCleanup] public void Cleanup() { _w2.Dispose(); _w3.Dispose(); _wf.Dispose(); }
    [Benchmark(Description = "1M x Q<Pos> (1 comp)")] public void Q1() => Kernels.Iter1(_w2, _q1);
    [Benchmark(Baseline = true, Description = "1M x Q<Pos,Vel> chunk spans")] public void Q2Spans() => Kernels.Iter2Spans(_w2, _q2);
    [Benchmark(Description = "1M x Q<Pos,Vel> struct ForEach")] public void Q2ForEach() => Kernels.Iter2ForEach(_w2, _q2);
    [Benchmark(Description = "1M x Q<Pos,Vel> ref-pair foreach")] public void Q2Each() => Kernels.Iter2Each(_w2, _q2);
    [Benchmark(Description = "1M x Q<Pos,Vel> unchecked refs")] public void Q2Unchecked() => Kernels.Iter2Unchecked(_w2, _q2);
    [Benchmark(Description = "1M x Q<Pos,Vel> SIMD Vector256")] public void Q2Simd() => Kernels.Iter2Simd(_w2, _q2);
    [Benchmark(Description = "1M x Q<Pos,Vel,Acc> (3 comp)")] public void Q3() => Kernels.Iter3(_w3, _q3);
    [Benchmark(Description = "1M x Q<Pos,Vel> over 32 archetypes")] public void Q2Fragmented() => Kernels.Iter2Spans(_wf, _qf);
}

[MemoryDiagnoser]
public class ParallelBench
{
    [Params(StorageMode.Chunk16K, StorageMode.Table)] public StorageMode Mode;
    [Params(4u, 8u, 16u)] public uint Runners;
    World _w = null!; Query _q = null!; ParallelIntegrate _p = null!;
    [GlobalSetup] public void Setup() { _w = Kernels.Make2(Mode, 1_000_000); _q = _w.Query<Position, Velocity>(); _p = new ParallelIntegrate(Runners); }
    [GlobalCleanup] public void Cleanup() { _p.Dispose(); _w.Dispose(); }
    [Benchmark(Description = "1M x Q<Pos,Vel> parallel JobScheduler")] public void Par() => _p.Run(_w, _q);
}

[MemoryDiagnoser]
public class StructuralBench
{
    [Params(StorageMode.Chunk16K, StorageMode.Table)] public StorageMode Mode;
    const int N = 100_000;
    World _w = null!; CommandBuffer _cb = null!; Entity[] _ents = null!; int[] _perm = null!; Entity[] _existing = null!;
    [GlobalSetup] public void Setup()
    {
        _w = Kernels.Make2(Mode, 1_000_000); // background population in the same archetype
        _cb = new CommandBuffer(_w, 8 << 20);
        _ents = new Entity[N]; _perm = Enumerable.Range(0, N).OrderBy(_ => Random.Shared.Next()).ToArray();
        _existing = new Entity[N];
        for (int i = 0; i < N; i++) _existing[i] = _w.Spawn(new Position(), new Velocity());
    }
    [GlobalCleanup] public void Cleanup() { _cb.Dispose(); _w.Dispose(); }

    [Benchmark(Baseline = true, Description = "100k spawn+despawn via CB (in order)")]
    public void SpawnDespawnCB()
    {
        for (int i = 0; i < N; i++) _ents[i] = _cb.Spawn(new Position { X = i }, new Velocity { X = 1 });
        _cb.Playback();
        for (int i = 0; i < N; i++) _cb.Despawn(_ents[i]);
        _cb.Playback();
        _w.Tick();
    }
    [Benchmark(Description = "100k spawn+despawn via CB (shuffled)")]
    public void SpawnDespawnCBShuffled()
    {
        for (int i = 0; i < N; i++) _ents[i] = _cb.Spawn(new Position { X = i }, new Velocity { X = 1 });
        _cb.Playback();
        for (int i = 0; i < N; i++) _cb.Despawn(_ents[_perm[i]]);
        _cb.Playback();
        _w.Tick();
    }
    [Benchmark(Description = "100k spawn+despawn direct")]
    public void SpawnDespawnDirect()
    {
        for (int i = 0; i < N; i++) _ents[i] = _w.Spawn(new Position { X = i }, new Velocity { X = 1 });
        for (int i = 0; i < N; i++) _w.Despawn(_ents[i]);
        _w.Tick();
    }
    [Benchmark(Description = "100k add+remove <Health> via CB")]
    public void AddRemoveCB()
    {
        for (int i = 0; i < N; i++) _cb.Add(_existing[i], new Health { V = i });
        _cb.Playback();
        for (int i = 0; i < N; i++) _cb.Remove<Health>(_existing[i]);
        _cb.Playback();
        _w.Tick();
    }
    [Benchmark(Description = "100k add+remove <Health> direct")]
    public void AddRemoveDirect()
    {
        for (int i = 0; i < N; i++) _w.Add(_existing[i], new Health { V = i });
        for (int i = 0; i < N; i++) _w.Remove<Health>(_existing[i]);
        _w.Tick();
    }
}

/// Stopwatch + allocation-counter harness (independent of BDN): N frames, per-thread and process-wide bytes.
public static class AllocHarness
{
    public static void Run(StorageMode mode, int frames)
    {
        using var w2 = Kernels.Make2(mode, 1_000_000);
        using var w3 = Kernels.Make3(mode, 1_000_000);
        var q1 = w2.Query<Position>(); var q2 = w2.Query<Position, Velocity>(); var q3 = w3.Query<Position, Velocity, Accel>();
        using var ws = Kernels.Make2(mode, 1_000_000);
        using var cb = new CommandBuffer(ws, 8 << 20);
        var ents = new Entity[100_000]; var existing = new Entity[100_000];
        for (int i = 0; i < existing.Length; i++) existing[i] = ws.Spawn(new Position(), new Velocity());
        using var par = new ParallelIntegrate(8);
        var qs = ws.Query<Position, Velocity>();

        var names = new[] { "iter2 spans", "iter2 ForEach", "iter2 ref-pair", "iter2 unchecked", "iter2 SIMD", "iter1", "iter3", "parallel8 iter2", "CB spawn+despawn 100k", "CB add+remove 100k" };
        var acts = new Action[]
        {
            () => Kernels.Iter2Spans(w2, q2), () => Kernels.Iter2ForEach(w2, q2), () => Kernels.Iter2Each(w2, q2), () => Kernels.Iter2Unchecked(w2, q2), () => Kernels.Iter2Simd(w2, q2),
            () => Kernels.Iter1(w2, q1), () => Kernels.Iter3(w3, q3), () => par.Run(w2, q2),
            () => { for (int i = 0; i < ents.Length; i++) ents[i] = cb.Spawn(new Position { X = i }, new Velocity()); cb.Playback(); for (int i = 0; i < ents.Length; i++) cb.Despawn(ents[i]); cb.Playback(); ws.Tick(); },
            () => { for (int i = 0; i < existing.Length; i++) cb.Add(existing[i], new Health { V = i }); cb.Playback(); for (int i = 0; i < existing.Length; i++) cb.Remove<Health>(existing[i]); cb.Playback(); ws.Tick(); },
        };
        Console.WriteLine($"## {mode}: {frames} frames each (after 20 warm-up frames)");
        Console.WriteLine("| op | median ms | p99 ms | thread bytes | process bytes |");
        Console.WriteLine("|---|---|---|---|---|");
        var times = new double[frames];
        for (int k = 0; k < acts.Length; k++)
        {
            for (int i = 0; i < 20; i++) acts[k]();
            long t0 = GC.GetAllocatedBytesForCurrentThread(); long p0 = GC.GetTotalAllocatedBytes(true);
            for (int i = 0; i < frames; i++) { long s = Stopwatch.GetTimestamp(); acts[k](); times[i] = Stopwatch.GetElapsedTime(s).TotalMilliseconds; }
            long t1 = GC.GetAllocatedBytesForCurrentThread(); long p1 = GC.GetTotalAllocatedBytes(true);
            Array.Sort(times);
            Console.WriteLine($"| {names[k]} | {times[frames / 2]:F3} | {times[(int)(frames * 0.99)]:F3} | {t1 - t0} | {p1 - p0} |");
        }
        _ = qs;
    }
}

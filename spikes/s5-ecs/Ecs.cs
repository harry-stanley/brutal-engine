// S5 spike: throwaway archetype ECS prototype. NOT production code.
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Brutal;          // ByteSize (Brutal.Core); Mem returns Brutal.Pointers.Ptr (.Pointer)
using Brutal.Memory;   // Mem

namespace S5;

/// <summary>Generational entity id: 32-bit index + 32-bit generation (architecture 3.4).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Entity : IEquatable<Entity>
{
    public readonly uint Index;
    public readonly uint Gen; // 0 == null
    public Entity(uint index, uint gen) { Index = index; Gen = gen; }
    public ulong Raw => ((ulong)Gen << 32) | Index;
    public bool IsNull => Gen == 0;
    public bool Equals(Entity o) => Index == o.Index && Gen == o.Gen;
    public override bool Equals(object? o) => o is Entity e && Equals(e);
    public override int GetHashCode() => (int)(Index ^ (Gen * 0x9E3779B9));
    public override string ToString() => $"E{Index}v{Gen}";
}

public enum StorageMode { Chunk16K, Chunk64K, Table }

/// <summary>Prototype type registry. Real design: generator-emitted registration into an instance-owned registry.</summary>
public static class ComponentTypes
{
    public const int Max = 256;
    internal static int Count;
    internal static readonly int[] Sizes = new int[Max];
    internal static int Register(int size) { int id = Count++; Sizes[id] = size; return id; }
}
public static class TypeId<T> where T : unmanaged
{
    public static readonly int Id = ComponentTypes.Register(Unsafe.SizeOf<T>());
}

public struct ComponentMask : IEquatable<ComponentMask>
{
    ulong a, b, c, d;
    public void Set(int id) { ref ulong w = ref Word(id); w |= 1UL << (id & 63); }
    public void Clear(int id) { ref ulong w = ref Word(id); w &= ~(1UL << (id & 63)); }
    public readonly bool Has(int id) => ((id >> 6) switch { 0 => a, 1 => b, 2 => c, _ => d } & (1UL << (id & 63))) != 0;
    [UnscopedRef] ref ulong Word(int id) { switch (id >> 6) { case 0: return ref a; case 1: return ref b; case 2: return ref c; default: return ref d; } }
    public readonly bool ContainsAll(in ComponentMask o) => (a & o.a) == o.a && (b & o.b) == o.b && (c & o.c) == o.c && (d & o.d) == o.d;
    public readonly bool Intersects(in ComponentMask o) => ((a & o.a) | (b & o.b) | (c & o.c) | (d & o.d)) != 0;
    public readonly bool Equals(ComponentMask o) => a == o.a && b == o.b && c == o.c && d == o.d;
    public override readonly bool Equals(object? o) => o is ComponentMask m && Equals(m);
    public override readonly int GetHashCode() => HashCode.Combine(a, b, c, d);
    public static ComponentMask Of<T1>() where T1 : unmanaged { var m = new ComponentMask(); m.Set(TypeId<T1>.Id); return m; }
    public static ComponentMask Of<T1, T2>() where T1 : unmanaged where T2 : unmanaged { var m = Of<T1>(); m.Set(TypeId<T2>.Id); return m; }
    public static ComponentMask Of<T1, T2, T3>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged { var m = Of<T1, T2>(); m.Set(TypeId<T3>.Id); return m; }
}

public unsafe struct Chunk
{
    public byte* Data;   // [Entity[cap]][col0[cap]]..[colN[cap]][uint versions[N]]
    public int Count;
}

public sealed unsafe class Archetype
{
    public readonly int Id;
    public readonly ComponentMask Mask;
    public readonly int[] Types;     // sorted type ids
    public readonly int[] Sizes;
    public readonly int[] ColumnOf;  // typeId -> column, -1 if absent (prototype: dense 256 table)
    public readonly int ColumnCount;
    public int ChunkCapacity;
    public readonly int* ColOffset;  // native
    public int VersionOffset;
    public Chunk* Chunks; public int ChunkCount; int _chunkArrCap;
    public int EntityCount;
    public readonly int[] AddEdge, RemoveEdge; // graph edge cache, -1 = unknown
    readonly World _w;
    readonly bool _table;

    internal Archetype(World w, int id, ComponentMask mask)
    {
        _w = w; Id = id; Mask = mask; _table = w.Mode == StorageMode.Table;
        var types = new List<int>();
        for (int t = 0; t < ComponentTypes.Max; t++) if (mask.Has(t)) types.Add(t);
        Types = types.ToArray(); ColumnCount = Types.Length;
        Sizes = new int[ColumnCount];
        ColumnOf = new int[ComponentTypes.Max]; Array.Fill(ColumnOf, -1);
        for (int i = 0; i < ColumnCount; i++) { Sizes[i] = ComponentTypes.Sizes[Types[i]]; ColumnOf[Types[i]] = i; }
        ColOffset = (int*)Mem.Alloc(new ByteSize((nuint)(sizeof(int) * Math.Max(1, ColumnCount)))).Pointer;
        AddEdge = new int[ComponentTypes.Max]; Array.Fill(AddEdge, -1);
        RemoveEdge = new int[ComponentTypes.Max]; Array.Fill(RemoveEdge, -1);
        _chunkArrCap = 4;
        Chunks = (Chunk*)Mem.AllocZeroed(new ByteSize((nuint)(sizeof(Chunk) * _chunkArrCap))).Pointer;
        if (_table) { ChunkCapacity = 0; }
        else
        {
            int rowBytes = sizeof(Entity); foreach (var s in Sizes) rowBytes += s;
            int cap = w.ChunkBytes / rowBytes;
            while (cap > 1 && Layout(cap) > w.ChunkBytes) cap--;
            if (Layout(cap) > w.ChunkBytes) throw new NotSupportedException("row too big for chunk");
            ChunkCapacity = cap;
        }
    }

    static int Align16(int x) => (x + 15) & ~15;
    /// computes column offsets for a capacity, returns total bytes
    int Layout(int cap)
    {
        int off = Align16(sizeof(Entity) * cap);
        for (int c = 0; c < ColumnCount; c++) { ColOffset[c] = off; off = Align16(off + Sizes[c] * cap); }
        VersionOffset = off;
        return off + sizeof(uint) * ColumnCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* Column(int chunk, int col) => Chunks[chunk].Data + ColOffset[col];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint* Versions(int chunk) => (uint*)(Chunks[chunk].Data + VersionOffset);

    void PushChunk(byte* data)
    {
        if (ChunkCount == _chunkArrCap)
        {
            _chunkArrCap *= 2;
            Chunks = (Chunk*)Mem.Realloc(Chunks, new ByteSize((nuint)(sizeof(Chunk) * _chunkArrCap))).Pointer;
        }
        Chunks[ChunkCount++] = new Chunk { Data = data, Count = 0 };
    }

    void GrowTable()
    {
        int newCap = ChunkCapacity == 0 ? 64 : ChunkCapacity * 2;
        // capture old offsets
        int* oldOff = stackalloc int[Math.Max(1, ColumnCount)];
        for (int c = 0; c < ColumnCount; c++) oldOff[c] = ColOffset[c];
        int oldVer = VersionOffset; int oldCap = ChunkCapacity;
        int bytes = Layout(newCap);
        byte* nb = (byte*)Mem.Alloc(new ByteSize((nuint)bytes)).Pointer;
        if (ChunkCount == 0) { PushChunk(nb); Mem.Clear(nb + VersionOffset, new ByteSize((nuint)(4 * ColumnCount))); }
        else
        {
            ref var ch = ref Chunks[0];
            Buffer.MemoryCopy(ch.Data, nb, bytes, (long)sizeof(Entity) * ch.Count);
            for (int c = 0; c < ColumnCount; c++) Buffer.MemoryCopy(ch.Data + oldOff[c], nb + ColOffset[c], bytes, (long)Sizes[c] * ch.Count);
            Buffer.MemoryCopy(ch.Data + oldVer, nb + VersionOffset, 4 * ColumnCount, 4 * ColumnCount);
            Mem.Free(ch.Data);
            ch.Data = nb;
        }
        ChunkCapacity = newCap;
        _ = oldCap;
    }

    /// Appends a row for e. All chunks but the last are full (dense invariant).
    public void AllocRow(Entity e, out int chunk, out int row)
    {
        if (ChunkCount == 0 || Chunks[ChunkCount - 1].Count == ChunkCapacity)
        {
            if (_table) GrowTable();
            else { byte* d = _w.RentChunk(); PushChunk(d); Mem.Clear(d + VersionOffset, new ByteSize((nuint)(4 * ColumnCount))); }
        }
        chunk = ChunkCount - 1;
        ref var ch = ref Chunks[chunk];
        row = ch.Count++;
        ((Entity*)ch.Data)[row] = e;
        EntityCount++;
    }

    /// Swap-removes (chunk,row) with the archetype's last row. Returns the entity that moved into the hole (or null).
    public Entity RemoveRow(int chunk, int row)
    {
        int lc = ChunkCount - 1;
        ref var last = ref Chunks[lc];
        int lr = last.Count - 1;
        Entity moved = default;
        if (lc != chunk || lr != row)
        {
            ref var dst = ref Chunks[chunk];
            moved = ((Entity*)last.Data)[lr];
            ((Entity*)dst.Data)[row] = moved;
            for (int c = 0; c < ColumnCount; c++)
            {
                int s = Sizes[c];
                Unsafe.CopyBlockUnaligned(dst.Data + ColOffset[c] + row * s, last.Data + ColOffset[c] + lr * s, (uint)s);
            }
            if (lc != chunk) { uint* v = Versions(chunk); for (int c = 0; c < ColumnCount; c++) v[c] = _w.Version; }
        }
        last.Count--; EntityCount--;
        if (last.Count == 0 && !_table) { _w.ReturnChunk(last.Data); ChunkCount--; }
        return moved;
    }

    internal void Dispose()
    {
        for (int i = 0; i < ChunkCount; i++) { if (_table) Mem.Free(Chunks[i].Data); else _w.ReturnChunk(Chunks[i].Data); }
        ChunkCount = 0;
        Mem.Free(Chunks); Mem.Free(ColOffset);
    }
}

public struct EntityRecord { public int Archetype; public int Chunk; public int Row; public uint Gen; }

/// <summary>Cached query: archetype match list updated incrementally (archetypes are append-only).</summary>
public sealed class Query
{
    internal ComponentMask All, None;
    internal int[] Matches = new int[16];
    internal int MatchCount, Seen;
    internal Query(ComponentMask all, ComponentMask none) { All = all; None = none; }
}

public sealed unsafe class World : IDisposable
{
    public readonly int ChunkBytes;
    public readonly StorageMode Mode;
    public uint Version = 1;
    internal readonly List<Archetype> Archetypes = new();
    readonly Dictionary<ComponentMask, int> _archByMask = new();
    EntityRecord* _records; int _recordCount, _recordCap;
    uint* _free; int _freeCount, _freeCap;
    byte** _chunkPool; int _poolCount, _poolCap;
    public int AliveCount;

    public World(StorageMode mode, int initialEntityCapacity = 1024)
    {
        Mode = mode; ChunkBytes = mode == StorageMode.Chunk64K ? 64 * 1024 : 16 * 1024;
        _recordCap = initialEntityCapacity;
        _records = (EntityRecord*)Mem.Alloc(new ByteSize((nuint)(sizeof(EntityRecord) * _recordCap))).Pointer;
        _freeCap = 1024; _free = (uint*)Mem.Alloc(new ByteSize((nuint)(4 * _freeCap))).Pointer;
        _poolCap = 64; _chunkPool = (byte**)Mem.Alloc(new ByteSize((nuint)(sizeof(void*) * _poolCap))).Pointer;
        GetOrCreateArchetype(default); // empty archetype = 0
    }

    public void Tick() => Version++;

    // ---- chunk pool (16 KB blocks, 64-byte aligned; Mem has no aligned alloc so NativeMemory is used) ----
    internal byte* RentChunk()
    {
        if (_poolCount > 0) return _chunkPool[--_poolCount];
        return (byte*)NativeMemory.AlignedAlloc((nuint)ChunkBytes, 64);
    }
    internal void ReturnChunk(byte* p)
    {
        if (_poolCount == _poolCap) { _poolCap *= 2; _chunkPool = (byte**)Mem.Realloc(_chunkPool, new ByteSize((nuint)(sizeof(void*) * _poolCap))).Pointer; }
        _chunkPool[_poolCount++] = p;
    }

    public Archetype GetArchetype(int id) => Archetypes[id];
    public int ArchetypeCount => Archetypes.Count;

    public int GetOrCreateArchetype(in ComponentMask mask)
    {
        if (_archByMask.TryGetValue(mask, out int id)) return id;
        id = Archetypes.Count;
        Archetypes.Add(new Archetype(this, id, mask)); // allocates: cold path, only on new archetype
        _archByMask.Add(mask, id);
        return id;
    }

    // ---- entity ids ----
    internal Entity AllocEntity()
    {
        uint idx, gen;
        if (_freeCount > 0) { idx = _free[--_freeCount]; gen = _records[idx].Gen; }
        else
        {
            if (_recordCount == _recordCap) { _recordCap *= 2; _records = (EntityRecord*)Mem.Realloc(_records, new ByteSize((nuint)(sizeof(EntityRecord) * _recordCap))).Pointer; }
            idx = (uint)_recordCount++; gen = 1;
        }
        _records[idx] = new EntityRecord { Archetype = -1, Chunk = -1, Row = -1, Gen = gen };
        AliveCount++;
        return new Entity(idx, gen);
    }
    /// Reserve an id for deferred spawn (single-threaded here; production: atomic reservation cursor).
    public Entity ReserveEntity() => AllocEntity();

    public bool IsAlive(Entity e) => e.Index < (uint)_recordCount && _records[e.Index].Gen == e.Gen && e.Gen != 0;
    public ref EntityRecord Record(Entity e)
    {
        if (!IsAlive(e)) throw new InvalidOperationException($"stale entity {e}");
        return ref _records[e.Index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Place(Entity e, int arch)
    {
        var a = Archetypes[arch];
        a.AllocRow(e, out int c, out int r);
        ref var rec = ref _records[e.Index];
        rec.Archetype = arch; rec.Chunk = c; rec.Row = r;
        uint* v = a.Versions(c); for (int i = 0; i < a.ColumnCount; i++) v[i] = Version;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    T* ColPtr<T>(in EntityRecord rec) where T : unmanaged
    {
        var a = Archetypes[rec.Archetype];
        int col = a.ColumnOf[TypeId<T>.Id];
        if (col < 0) return null;
        return (T*)a.Column(rec.Chunk, col) + rec.Row;
    }

    // ---- direct structural API ----
    public Entity Spawn() { var e = AllocEntity(); Place(e, 0); return e; }
    int _lastMaskArch1 = -1, _lastMaskArch2 = -1; ComponentMask _lastMask1, _lastMask2;
    internal int ArchFor(in ComponentMask m, ref ComponentMask cacheMask, ref int cacheArch)
    {
        if (cacheArch >= 0 && cacheMask.Equals(m)) return cacheArch;
        cacheMask = m; return cacheArch = GetOrCreateArchetype(m);
    }
    public Entity Spawn<T1>(in T1 a) where T1 : unmanaged
    {
        var e = AllocEntity(); int arch = ArchFor(ComponentMask.Of<T1>(), ref _lastMask1, ref _lastMaskArch1);
        Place(e, arch); ref var rec = ref _records[e.Index];
        *ColPtr<T1>(rec) = a; return e;
    }
    public Entity Spawn<T1, T2>(in T1 a, in T2 b) where T1 : unmanaged where T2 : unmanaged
    {
        var e = AllocEntity(); int arch = ArchFor(ComponentMask.Of<T1, T2>(), ref _lastMask2, ref _lastMaskArch2);
        Place(e, arch); ref var rec = ref _records[e.Index];
        *ColPtr<T1>(rec) = a; *ColPtr<T2>(rec) = b; return e;
    }
    public Entity Spawn<T1, T2, T3>(in T1 a, in T2 b, in T3 c) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        var e = AllocEntity(); int arch = GetOrCreateArchetype(ComponentMask.Of<T1, T2, T3>());
        Place(e, arch); ref var rec = ref _records[e.Index];
        *ColPtr<T1>(rec) = a; *ColPtr<T2>(rec) = b; *ColPtr<T3>(rec) = c; return e;
    }

    public void Despawn(Entity e)
    {
        ref var rec = ref Record(e);
        if (rec.Archetype >= 0)
        {
            var moved = Archetypes[rec.Archetype].RemoveRow(rec.Chunk, rec.Row);
            if (!moved.IsNull) { ref var mr = ref _records[moved.Index]; mr.Chunk = rec.Chunk; mr.Row = rec.Row; }
        }
        rec.Gen++; if (rec.Gen == 0) rec.Gen = 1; // wrap: production should retire the slot instead
        rec.Archetype = -1;
        if (_freeCount == _freeCap) { _freeCap *= 2; _free = (uint*)Mem.Realloc(_free, new ByteSize((nuint)(4 * _freeCap))).Pointer; }
        _free[_freeCount++] = e.Index;
        AliveCount--;
    }

    void MoveTo(Entity e, ref EntityRecord rec, int dstId)
    {
        if (rec.Archetype < 0) { Place(e, dstId); return; }
        var src = Archetypes[rec.Archetype]; var dst = Archetypes[dstId];
        int sc = rec.Chunk, sr = rec.Row;
        dst.AllocRow(e, out int dc, out int dr);
        uint* dv = dst.Versions(dc);
        for (int i = 0; i < dst.ColumnCount; i++)
        {
            int scol = src.ColumnOf[dst.Types[i]];
            if (scol >= 0) { int s = dst.Sizes[i]; Unsafe.CopyBlockUnaligned(dst.Column(dc, i) + dr * s, src.Column(sc, scol) + sr * s, (uint)s); }
            dv[i] = Version;
        }
        var moved = src.RemoveRow(sc, sr);
        if (!moved.IsNull) { ref var mr = ref _records[moved.Index]; mr.Chunk = sc; mr.Row = sr; }
        rec.Archetype = dstId; rec.Chunk = dc; rec.Row = dr;
    }

    internal int AddEdge(int arch, int typeId)
    {
        var a = Archetypes[arch];
        int t = a.AddEdge[typeId];
        if (t < 0) { var m = a.Mask; m.Set(typeId); t = GetOrCreateArchetype(m); a.AddEdge[typeId] = t; Archetypes[t].RemoveEdge[typeId] = arch; }
        return t;
    }
    internal int RemoveEdge(int arch, int typeId)
    {
        var a = Archetypes[arch];
        int t = a.RemoveEdge[typeId];
        if (t < 0) { var m = a.Mask; m.Clear(typeId); t = GetOrCreateArchetype(m); a.RemoveEdge[typeId] = t; Archetypes[t].AddEdge[typeId] = arch; }
        return t;
    }

    public void Add<T>(Entity e, in T value) where T : unmanaged
    {
        fixed (T* p = &value) AddRaw(e, TypeId<T>.Id, (byte*)p);
    }
    internal void AddRaw(Entity e, int typeId, byte* data)
    {
        ref var rec = ref Record(e);
        int cur = rec.Archetype < 0 ? 0 : rec.Archetype;
        var ca = Archetypes[cur];
        if (!ca.Mask.Has(typeId)) MoveTo(e, ref rec, AddEdge(cur, typeId));
        var a = Archetypes[rec.Archetype]; int col = a.ColumnOf[typeId]; int s = a.Sizes[col];
        Unsafe.CopyBlockUnaligned(a.Column(rec.Chunk, col) + rec.Row * s, data, (uint)s);
        a.Versions(rec.Chunk)[col] = Version;
    }
    public void Remove<T>(Entity e) where T : unmanaged => RemoveRaw(e, TypeId<T>.Id);
    internal void RemoveRaw(Entity e, int typeId)
    {
        ref var rec = ref Record(e);
        if (rec.Archetype < 0 || !Archetypes[rec.Archetype].Mask.Has(typeId)) return;
        MoveTo(e, ref rec, RemoveEdge(rec.Archetype, typeId));
    }
    public bool Has<T>(Entity e) where T : unmanaged { ref var r = ref Record(e); return r.Archetype >= 0 && Archetypes[r.Archetype].Mask.Has(TypeId<T>.Id); }
    /// Write access: bumps the chunk/column change version.
    public ref T Get<T>(Entity e) where T : unmanaged
    {
        ref var rec = ref Record(e);
        T* p = rec.Archetype < 0 ? null : ColPtr<T>(rec);
        if (p == null) throw new InvalidOperationException($"{e} lacks component {typeof(T).Name}");
        var a = Archetypes[rec.Archetype]; a.Versions(rec.Chunk)[a.ColumnOf[TypeId<T>.Id]] = Version;
        return ref *p;
    }

    // ---- queries ----
    public Query CreateQuery(in ComponentMask all, in ComponentMask none = default) { var q = new Query(all, none); Refresh(q); return q; }
    public Query Query<T1>() where T1 : unmanaged => CreateQuery(ComponentMask.Of<T1>());
    public Query Query<T1, T2>() where T1 : unmanaged where T2 : unmanaged => CreateQuery(ComponentMask.Of<T1, T2>());
    public Query Query<T1, T2, T3>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged => CreateQuery(ComponentMask.Of<T1, T2, T3>());
    internal void Refresh(Query q)
    {
        for (int i = q.Seen; i < Archetypes.Count; i++)
        {
            var m = Archetypes[i].Mask;
            if (m.ContainsAll(q.All) && !m.Intersects(q.None))
            {
                if (q.MatchCount == q.Matches.Length) Array.Resize(ref q.Matches, q.MatchCount * 2);
                q.Matches[q.MatchCount++] = i;
            }
        }
        q.Seen = Archetypes.Count;
    }
    public ChunkEnumerable Chunks(Query q) { Refresh(q); return new ChunkEnumerable(this, q); }

    public void ForEach<TF, T1, T2>(Query q, ref TF f) where TF : struct, IForEach<T1, T2> where T1 : unmanaged where T2 : unmanaged
    {
        foreach (var ch in Chunks(q))
        {
            var s1 = ch.Write<T1>(); var s2 = ch.Write<T2>();
            for (int i = 0; i < s1.Length; i++) f.Execute(ref s1[i], ref s2[i]);
        }
    }
    public EachEnumerable<T1, T2> Each<T1, T2>(Query q) where T1 : unmanaged where T2 : unmanaged { Refresh(q); return new(this, q); }

    public void Dispose()
    {
        foreach (var a in Archetypes) a.Dispose();
        for (int i = 0; i < _poolCount; i++) NativeMemory.AlignedFree(_chunkPool[i]);
        Mem.Free(_chunkPool); Mem.Free(_records); Mem.Free(_free);
    }
}

public interface IForEach<T1, T2> { void Execute(ref T1 a, ref T2 b); }

public readonly ref struct ChunkEnumerable
{
    readonly World _w; readonly Query _q;
    public ChunkEnumerable(World w, Query q) { _w = w; _q = q; }
    public ChunkEnumerator GetEnumerator() => new(_w, _q);
}

public unsafe ref struct ChunkEnumerator
{
    readonly World _w; readonly Query _q;
    int _ai; int _ci; Archetype? _cur;
    public ChunkEnumerator(World w, Query q) { _w = w; _q = q; _ai = -1; _ci = 0; _cur = null; }
    public bool MoveNext()
    {
        while (true)
        {
            if (_cur != null && ++_ci < _cur.ChunkCount) { if (_cur.Chunks[_ci].Count > 0) return true; continue; }
            if (++_ai >= _q.MatchCount) return false;
            _cur = _w.Archetypes[_q.Matches[_ai]]; _ci = -1;
        }
    }
    public ChunkView Current => new(_cur!, _ci, _cur!.Chunks[_ci].Data, _cur.Chunks[_ci].Count, _w.Version);
}

/// <summary>One chunk's worth of SoA columns. Write&lt;T&gt; bumps the column's change version.</summary>
public readonly unsafe ref struct ChunkView
{
    readonly Archetype _a; readonly byte* _data; readonly int _count; readonly uint _version; readonly int _ci;
    public ChunkView(Archetype a, int chunkIndex, byte* data, int count, uint version) { _a = a; _ci = chunkIndex; _data = data; _count = count; _version = version; }
    public int ChunkIndex => _ci;
    public int Count => _count;
    public Archetype Archetype => _a;
    public ReadOnlySpan<Entity> Entities => new(_data, _count);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> Write<T>() where T : unmanaged
    {
        int col = _a.ColumnOf[TypeId<T>.Id];
        ((uint*)(_data + _a.VersionOffset))[col] = _version;
        return new Span<T>(_data + _a.ColOffset[col], _count);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> Read<T>() where T : unmanaged => new(_data + _a.ColOffset[_a.ColumnOf[TypeId<T>.Id]], _count);
    /// true if column T was written after `sinceVersion` (wrap-safe compare)
    public bool Changed<T>(uint sinceVersion) where T : unmanaged
        => (int)(((uint*)(_data + _a.VersionOffset))[_a.ColumnOf[TypeId<T>.Id]] - sinceVersion) > 0;
}

public ref struct RefPair<T1, T2>
{
    public ref T1 A; public ref T2 B;
    public RefPair(ref T1 a, ref T2 b) { A = ref a; B = ref b; }
}
public readonly ref struct EachEnumerable<T1, T2> where T1 : unmanaged where T2 : unmanaged
{
    readonly World _w; readonly Query _q;
    public EachEnumerable(World w, Query q) { _w = w; _q = q; }
    public EachEnumerator<T1, T2> GetEnumerator() => new(_w, _q);
}
public ref struct EachEnumerator<T1, T2> where T1 : unmanaged where T2 : unmanaged
{
    ChunkEnumerator _chunks; Span<T1> _s1; Span<T2> _s2; int _i;
    public EachEnumerator(World w, Query q) { _chunks = new ChunkEnumerator(w, q); _s1 = default; _s2 = default; _i = -1; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (++_i < _s1.Length) return true;
        while (_chunks.MoveNext()) { var c = _chunks.Current; _s1 = c.Write<T1>(); _s2 = c.Write<T2>(); _i = 0; if (_s1.Length > 0) return true; }
        return false;
    }
    public RefPair<T1, T2> Current { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => new(ref _s1[_i], ref _s2[_i]); }
}

/// <summary>Deferred structural changes in an unmanaged byte stream. Clear() keeps capacity (steady state: zero alloc).</summary>
public sealed unsafe class CommandBuffer : IDisposable
{
    enum Op : int { Spawn = 1, Despawn = 2, Add = 3, Remove = 4 }
    readonly World _w;
    byte* _buf; int _len, _cap;
    ComponentMask _m1, _m2; int _a1 = -1, _a2 = -1;
    public CommandBuffer(World w, int capacity = 4096) { _w = w; _cap = capacity; _buf = (byte*)Mem.Alloc(new ByteSize((nuint)_cap)).Pointer; }
    public int Length => _len;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    byte* Reserve(int n)
    {
        if (_len + n > _cap) { while (_len + n > _cap) _cap *= 2; _buf = (byte*)Mem.Realloc(_buf, new ByteSize((nuint)_cap)).Pointer; }
        byte* p = _buf + _len; _len += n; return p;
    }
    // Layout: [int op][int arch][Entity][int ncomp] then ncomp x ([int type][data(size)])
    public Entity Spawn()
    {
        var e = _w.ReserveEntity(); byte* p = Reserve(20);
        *(int*)p = (int)Op.Spawn; *(int*)(p + 4) = 0; *(Entity*)(p + 8) = e; *(int*)(p + 16) = 0; return e;
    }
    public Entity Spawn<T1, T2>(in T1 a, in T2 b) where T1 : unmanaged where T2 : unmanaged
    {
        var e = _w.ReserveEntity();
        int arch = _w.ArchFor(ComponentMask.Of<T1, T2>(), ref _m2, ref _a2);
        byte* p = Reserve(20 + 4 + sizeof(T1) + 4 + sizeof(T2));
        *(int*)p = (int)Op.Spawn; *(int*)(p + 4) = arch; *(Entity*)(p + 8) = e; *(int*)(p + 16) = 2; p += 20;
        *(int*)p = TypeId<T1>.Id; Unsafe.WriteUnaligned(p + 4, a); p += 4 + sizeof(T1);
        *(int*)p = TypeId<T2>.Id; Unsafe.WriteUnaligned(p + 4, b);
        return e;
    }
    public Entity Spawn<T1>(in T1 a) where T1 : unmanaged
    {
        var e = _w.ReserveEntity();
        int arch = _w.ArchFor(ComponentMask.Of<T1>(), ref _m1, ref _a1);
        byte* p = Reserve(20 + 4 + sizeof(T1));
        *(int*)p = (int)Op.Spawn; *(int*)(p + 4) = arch; *(Entity*)(p + 8) = e; *(int*)(p + 16) = 1; p += 20;
        *(int*)p = TypeId<T1>.Id; Unsafe.WriteUnaligned(p + 4, a);
        return e;
    }
    public void Despawn(Entity e) { byte* p = Reserve(12); *(int*)p = (int)Op.Despawn; *(Entity*)(p + 4) = e; }
    public void Add<T>(Entity e, in T v) where T : unmanaged
    {
        byte* p = Reserve(16 + sizeof(T)); *(int*)p = (int)Op.Add; *(Entity*)(p + 4) = e; *(int*)(p + 12) = TypeId<T>.Id; Unsafe.WriteUnaligned(p + 16, v);
    }
    public void Remove<T>(Entity e) where T : unmanaged
    {
        byte* p = Reserve(16); *(int*)p = (int)Op.Remove; *(Entity*)(p + 4) = e; *(int*)(p + 12) = TypeId<T>.Id;
    }

    /// Applies all commands in order at a sync point. Commands targeting dead entities are skipped.
    public void Playback()
    {
        byte* p = _buf, end = _buf + _len;
        while (p < end)
        {
            var op = (Op)(*(int*)p);
            switch (op)
            {
                case Op.Spawn:
                {
                    int arch = *(int*)(p + 4); var e = *(Entity*)(p + 8); int n = *(int*)(p + 16); p += 20;
                    bool alive = _w.IsAlive(e);
                    Archetype a = _w.Archetypes[arch];
                    int c = 0, r = 0;
                    if (alive)
                    {
                        a.AllocRow(e, out c, out r);
                        ref var rec = ref _w.Record(e); rec.Archetype = arch; rec.Chunk = c; rec.Row = r;
                        uint* v = a.Versions(c); for (int i = 0; i < a.ColumnCount; i++) v[i] = _w.Version;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        int t = *(int*)p; int s = ComponentTypes.Sizes[t];
                        if (alive) { int col = a.ColumnOf[t]; Unsafe.CopyBlockUnaligned(a.Column(c, col) + r * s, p + 4, (uint)s); }
                        p += 4 + s;
                    }
                    break;
                }
                case Op.Despawn:
                {
                    var e = *(Entity*)(p + 4); p += 12;
                    if (_w.IsAlive(e)) _w.Despawn(e);
                    break;
                }
                case Op.Add:
                {
                    var e = *(Entity*)(p + 4); int t = *(int*)(p + 12); int s = ComponentTypes.Sizes[t];
                    if (_w.IsAlive(e)) _w.AddRaw(e, t, p + 16);
                    p += 16 + s; break;
                }
                case Op.Remove:
                {
                    var e = *(Entity*)(p + 4); int t = *(int*)(p + 12); p += 16;
                    if (_w.IsAlive(e)) _w.RemoveRaw(e, t);
                    break;
                }
                default: throw new InvalidOperationException("corrupt command stream");
            }
        }
        _len = 0;
    }
    public void Dispose() { Mem.Free(_buf); }
}

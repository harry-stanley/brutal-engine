using S5;

namespace S5;

public struct CA { public int V; }
public struct CB { public long V; }
public struct CC { public int X, Y, Z; }
public struct CD { public byte V; }

/// Randomized differential test against a Dictionary reference model.
public static class RandomTest
{
    static void Check(bool c, string msg) { if (!c) throw new Exception("FAIL: " + msg); }

    public static void Run(StorageMode mode, int seed, int steps)
    {
        var rng = new Random(seed);
        using var w = new World(mode);
        using var cb = new CommandBuffer(w);
        // reference: entity -> 4 nullable component values (stored as long)
        var model = new Dictionary<Entity, long?[]>();
        var alive = new List<Entity>();
        var dead = new List<Entity>();
        long next = 1;

        void SetModel(Entity e, int t, long v) => model[e][t] = v;
        void AddComp(Entity e, int t, long v, bool deferred)
        {
            switch (t)
            {
                case 0: if (deferred) cb.Add(e, new CA { V = (int)v }); else w.Add(e, new CA { V = (int)v }); v = (int)v; break;
                case 1: if (deferred) cb.Add(e, new CB { V = v }); else w.Add(e, new CB { V = v }); break;
                case 2: if (deferred) cb.Add(e, new CC { X = (int)v, Y = -(int)v, Z = 7 }); else w.Add(e, new CC { X = (int)v, Y = -(int)v, Z = 7 }); v = (int)v; break;
                case 3: if (deferred) cb.Add(e, new CD { V = (byte)v }); else w.Add(e, new CD { V = (byte)v }); v = (byte)v; break;
            }
            SetModel(e, t, v);
        }
        void RemoveComp(Entity e, int t, bool deferred)
        {
            switch (t)
            {
                case 0: if (deferred) cb.Remove<CA>(e); else w.Remove<CA>(e); break;
                case 1: if (deferred) cb.Remove<CB>(e); else w.Remove<CB>(e); break;
                case 2: if (deferred) cb.Remove<CC>(e); else w.Remove<CC>(e); break;
                case 3: if (deferred) cb.Remove<CD>(e); else w.Remove<CD>(e); break;
            }
            model[e][t] = null;
        }

        for (int step = 0; step < steps; step++)
        {
            bool deferred = rng.Next(3) == 0;
            int batch = deferred ? rng.Next(1, 40) : 1;
            var pendingDespawn = new HashSet<Entity>();
            for (int b = 0; b < batch; b++)
            {
                int op = rng.Next(10);
                if (op < 3 || alive.Count == 0)
                {
                    Entity e;
                    int kind = rng.Next(3);
                    long v = next++;
                    if (kind == 0) { e = deferred ? cb.Spawn() : w.Spawn(); model[e] = new long?[4]; }
                    else if (kind == 1)
                    {
                        e = deferred ? cb.Spawn(new CA { V = (int)v }, new CB { V = v * 3 }) : w.Spawn(new CA { V = (int)v }, new CB { V = v * 3 });
                        model[e] = new long?[] { (int)v, v * 3, null, null };
                    }
                    else
                    {
                        e = deferred ? cb.Spawn(new CC { X = (int)v, Y = -(int)v, Z = 7 }) : w.Spawn(new CC { X = (int)v, Y = -(int)v, Z = 7 });
                        model[e] = new long?[] { null, null, (int)v, null };
                    }
                    alive.Add(e);
                }
                else
                {
                    int idx = rng.Next(alive.Count); var e = alive[idx];
                    if (pendingDespawn.Contains(e)) continue;
                    if (op < 5)
                    {
                        if (deferred) { cb.Despawn(e); pendingDespawn.Add(e); }
                        else w.Despawn(e);
                        model.Remove(e); alive[idx] = alive[^1]; alive.RemoveAt(alive.Count - 1); dead.Add(e);
                    }
                    else if (op < 8) AddComp(e, rng.Next(4), next++, deferred);
                    else if (op < 9) RemoveComp(e, rng.Next(4), deferred);
                    else if (!deferred && model[e][0].HasValue) { w.Get<CA>(e).V = 4242; model[e][0] = 4242; }
                }
            }
            if (deferred)
            {
                // deferred commands on a despawned entity must be skipped
                if (dead.Count > 0) cb.Add(dead[rng.Next(dead.Count)], new CD { V = 1 });
                cb.Playback();
            }
            if (step % 50 == 0 || step == steps - 1) Verify(w, model, dead);
            w.Tick();
        }
        int chunks = 0, maxc = 0; for (int i = 0; i < w.ArchetypeCount; i++) { chunks += w.GetArchetype(i).ChunkCount; maxc = Math.Max(maxc, w.GetArchetype(i).ChunkCount); }
        if (steps >= 30000) Console.WriteLine($"  [{mode}] alive={w.AliveCount} archetypes={w.ArchetypeCount} chunks={chunks} maxChunksPerArch={maxc} dead-handles-checked={dead.Count}");
    }

    static void Verify(World w, Dictionary<Entity, long?[]> model, List<Entity> dead)
    {
        Check(w.AliveCount == model.Count, $"alive {w.AliveCount} vs {model.Count}");
        foreach (var (e, comps) in model)
        {
            Check(w.IsAlive(e), $"{e} should be alive");
            Check(w.Has<CA>(e) == comps[0].HasValue, $"{e} CA presence");
            Check(w.Has<CB>(e) == comps[1].HasValue, $"{e} CB presence");
            Check(w.Has<CC>(e) == comps[2].HasValue, $"{e} CC presence");
            Check(w.Has<CD>(e) == comps[3].HasValue, $"{e} CD presence");
            if (comps[0].HasValue) Check(w.Get<CA>(e).V == comps[0], $"{e} CA value");
            if (comps[1].HasValue) Check(w.Get<CB>(e).V == comps[1], $"{e} CB value");
            if (comps[2].HasValue) { var c = w.Get<CC>(e); Check(c.X == comps[2] && c.Y == -comps[2] && c.Z == 7, $"{e} CC value"); }
            if (comps[3].HasValue) Check(w.Get<CD>(e).V == comps[3], $"{e} CD value");
        }
        foreach (var d in dead) Check(!w.IsAlive(d), $"{d} should be dead (stale handle)");
        // queries: counts per mask, and every row's entity record points back at it
        int[][] masks = { new[] { 0 }, new[] { 0, 1 }, new[] { 2 }, new[] { 0, 1, 2 }, new[] { 3 }, new[] { 1, 3 } };
        foreach (var m in masks)
        {
            var mask = new ComponentMask();
            foreach (var t in m) mask.Set(t switch { 0 => TypeId<CA>.Id, 1 => TypeId<CB>.Id, 2 => TypeId<CC>.Id, _ => TypeId<CD>.Id });
            var q = w.CreateQuery(mask);
            int n = 0;
            foreach (var ch in w.Chunks(q))
            {
                var ents = ch.Entities;
                for (int i = 0; i < ents.Length; i++)
                {
                    Check(model.ContainsKey(ents[i]), $"query yielded dead {ents[i]}");
                    ref var rec = ref w.Record(ents[i]);
                    Check(w.GetArchetype(rec.Archetype) == ch.Archetype && rec.Row == i, "record/location mismatch");
                }
                if (m.Contains(0)) { var s = ch.Read<CA>(); for (int i = 0; i < s.Length; i++) Check(model[ents[i]][0] == s[i].V, "CA via query"); }
                n += ch.Count;
            }
            int expect = model.Values.Count(c => m.All(t => c[t].HasValue));
            Check(n == expect, $"query [{string.Join(",", m)}] {n} vs {expect}");
        }
    }

    public static void ChangeDetection(StorageMode mode)
    {
        using var w = new World(mode);
        for (int i = 0; i < 5000; i++) w.Spawn(new CA { V = i }, new CB { V = i });
        var q = w.Query<CA, CB>();
        uint since = w.Version; w.Tick();
        int changed = 0; foreach (var ch in w.Chunks(q)) if (ch.Changed<CA>(since)) changed++;
        Check(changed == 0, "nothing changed yet");
        foreach (var ch in w.Chunks(q)) { _ = ch.Read<CA>(); }
        changed = 0; foreach (var ch in w.Chunks(q)) if (ch.Changed<CA>(since)) changed++;
        Check(changed == 0, "Read must not bump version");
        bool first = true;
        foreach (var ch in w.Chunks(q)) { if (first) { ch.Write<CA>()[0].V = -1; first = false; } }
        int chA = 0, chB = 0, total = 0;
        foreach (var ch in w.Chunks(q)) { total++; if (ch.Changed<CA>(since)) chA++; if (ch.Changed<CB>(since)) chB++; }
        Check(chA == 1 && chB == 0, $"expected exactly one changed CA chunk, got {chA}/{total}, CB {chB}");
        Console.WriteLine($"  change detection OK ({mode}): {chA}/{total} chunks flagged after one chunk write");
    }
}

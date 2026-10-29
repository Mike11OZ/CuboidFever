using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CuboidFever;

public class World
{
    public const int ChunksY = 8;
    public const int Height = ChunksY * Chunk.Size; // 128
    public const int SeaLevel = 64;

    // How many columns around the player stay loaded. Meshes exist out to LoadRadius - 1.
    public int LoadRadius = 10;
    private int UnloadRadius => LoadRadius + 2; // hysteresis, so chunks don't thrash at the boundary

    // Only the main thread ever touches these collections
    private readonly Dictionary<(int, int), Column> _columns = new();
    private readonly HashSet<(int, int)> _generating = new();
    private readonly HashSet<Chunk> _pending = new();   // need a (background) remesh
    private readonly HashSet<Chunk> _urgent = new();    // need an immediate remesh (player edits)
    private readonly HashSet<Chunk> _inFlight = new();  // a mesh job is running
    private readonly List<Chunk> _visible = new();

    private readonly byte _airId, _waterId;

    private bool _urgentMode;

    // Worker -> main thread hand-off
    private readonly ConcurrentQueue<Column> _generated = new();
    private readonly ConcurrentQueue<MeshData> _meshed = new();

    private readonly TerrainGenerator _gen;
    private readonly WorkerPool _pool;
     private readonly int _maxGenInFlight, _maxMeshInFlight;
    private (int x, int z) _lastCenter = (int.MinValue, 0);

    public readonly LightMap Light;

    public World(int seed)
    {
        // Cache block IDs from the registry.
        _airId = BlockRegistry.Instance.GetId("cuboidfever:air");
        _waterId = BlockRegistry.Instance.GetId("cuboidfever:water");

        _gen = new TerrainGenerator(seed);
        Light = new LightMap(this);
        _pool = new WorkerPool(Math.Clamp(Environment.ProcessorCount - 2, 2, 8));
        _maxGenInFlight = _pool.Count + 2;
        _maxMeshInFlight = _pool.Count * 3;
    }

    public int TotalChunks => _columns.Count * ChunksY;
    public int VisibleChunks => _visible.Count;
    public int PendingWork => _generating.Count + _inFlight.Count + _pending.Count;

    // =====================  LOOKUPS  =====================

    public Column GetColumn(int cx, int cz) => _columns.TryGetValue((cx, cz), out var c) ? c : null;

    public bool IsColumnLoaded(int wx, int wz) => _columns.ContainsKey((wx >> 4, wz >> 4));

    private Chunk GetChunkAt(int x, int y, int z)
    {
        if ((uint)y >= Height) return null;
        return GetColumn(x >> 4, z >> 4)?.Chunks[y >> 4];
    }

    public byte GetBlock(int x, int y, int z)
    {
        if ((uint)y >= Height) return _airId;

        var col = GetColumn(x >> 4, z >> 4);
        return col == null
            ? _airId
            : col.GetBlock(x & 15, y, z & 15);
    }

    // Solid = blocks you collide with.
    public bool IsSolid(int x, int y, int z)
    {
        byte b = GetBlock(x, y, z);
        return BlockRegistry.Instance.Info[b].Solid;
    }

    public void RemeshAll()
    {
        foreach (var col in _columns.Values)
        {
            if (!col.Active) continue;
            foreach (var chunk in col.Chunks)
                if (!chunk.IsEmpty) MarkDirty(chunk);
        }
    }

    // =====================  PER-FRAME UPDATE  =====================

    // Call once per frame, after any block edits
    public void Update(GraphicsDevice device, Vector3 camPos)
    {
        int ccx = (int)MathF.Floor(camPos.X / 16f);
        int ccz = (int)MathF.Floor(camPos.Z / 16f);

        RequestColumns(ccx, ccz);
        DrainGenerated(ccx, ccz);

        if ((ccx, ccz) != _lastCenter)
        {
            UnloadFar(ccx, ccz);
            _lastCenter = (ccx, ccz);
        }

        BuildUrgent(device);
        DispatchMeshJobs(device, camPos);
        DrainMeshed(device);
    }

    // ---- Generation ----

    private readonly List<(int d, int x, int z)> _cand = new();

    private void RequestColumns(int ccx, int ccz)
    {
        int slots = _maxGenInFlight - _generating.Count;
        if (slots <= 0) return;

        _cand.Clear();
        int R = LoadRadius;
        for (int dz = -R; dz <= R; dz++)
        for (int dx = -R; dx <= R; dx++)
        {
            int d = dx * dx + dz * dz;
            if (d > R * R) continue;
            var key = (ccx + dx, ccz + dz);
            if (_columns.ContainsKey(key) || _generating.Contains(key)) continue;
            _cand.Add((d, key.Item1, key.Item2));
        }
        if (_cand.Count == 0) return;

        _cand.Sort((a, b) => a.d.CompareTo(b.d)); // nearest first

        int count = Math.Min(slots, _cand.Count);
        for (int i = 0; i < count; i++)
        {
            var (_, x, z) = _cand[i];
            _generating.Add((x, z));
            _pool.Enqueue(() => GenerateJob(x, z));
        }
    }

    // Runs on a worker thread. Touches nothing shared: it builds a complete column and hands it over.
    private void GenerateJob(int cx, int cz)
    {
        var blocks = new byte[Chunk.Size, Height, Chunk.Size];
        _gen.GenerateColumn(cx, cz, blocks);

        var col = new Column(cx, cz);
        for (int cy = 0; cy < ChunksY; cy++)
        {
            var chunk = new Chunk(cx, cy, cz);
            chunk.CopyFromColumn(blocks);
            col.Chunks[cy] = chunk;
        }
        col.ComputeSkyLight();

        _generated.Enqueue(col);
    }

    private void DrainGenerated(int ccx, int ccz)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < 3.0 && _generated.TryDequeue(out var col))
        {
            _generating.Remove((col.CX, col.CZ));

            int dx = col.CX - ccx, dz = col.CZ - ccz;
            if (dx * dx + dz * dz > UnloadRadius * UnloadRadius) continue; // player moved away meanwhile
            if (_columns.ContainsKey((col.CX, col.CZ))) continue;

            _columns[(col.CX, col.CZ)] = col;
            Light.Stitch(col);

            for (int nz = -1; nz <= 1; nz++)
            for (int nx = -1; nx <= 1; nx++)
                TryActivate(GetColumn(col.CX + nx, col.CZ + nz));
        }
    }

    // A column becomes meshable once all 8 neighbors exist
    private void TryActivate(Column col)
    {
        if (col == null || col.Active) return;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            if ((dx != 0 || dz != 0) && !_columns.ContainsKey((col.CX + dx, col.CZ + dz))) return;

        col.Active = true;
        foreach (var chunk in col.Chunks)
            if (!chunk.IsEmpty) MarkDirty(chunk);
    }

    // ---- Unloading ----

    private readonly List<(int, int)> _toRemove = new();

    private void UnloadFar(int ccx, int ccz)
    {
        _toRemove.Clear();
        int r2 = UnloadRadius * UnloadRadius;

        foreach (var kv in _columns)
        {
            var c = kv.Value;
            int dx = c.CX - ccx, dz = c.CZ - ccz;
            if (dx * dx + dz * dz > r2 && !c.Modified) _toRemove.Add(kv.Key);
        }

        foreach (var key in _toRemove)
        {
            foreach (var chunk in _columns[key].Chunks)
            {
                chunk.Unloaded = true; // a still-running mesh job for it will be discarded
                chunk.DisposeMesh();
                _pending.Remove(chunk);
                _urgent.Remove(chunk);
            }
            _columns.Remove(key);
        }
        if (_toRemove.Count > 0) Light.InvalidateCache();
    }

    // ---- Meshing ----

    private Column[] Neighborhood(int cx, int cz)
    {
        var cols = new Column[9];
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            cols[(dz + 1) * 3 + (dx + 1)] = GetColumn(cx + dx, cz + dz);
        return cols;
    }

    // Player edits: mesh on the main thread right now, so the change shows this frame
    private void BuildUrgent(GraphicsDevice device)
    {
        if (_urgent.Count == 0) return;
        foreach (var c in _urgent)
        {
            if (c.Unloaded) continue;
            var m = ChunkMesher.Build(c, Neighborhood(c.CX, c.CZ), c.Version);
            c.ApplyMesh(device, m);
            c.AppliedVersion = m.Version;
        }
        _urgent.Clear();
    }

    private readonly List<(float d, Chunk c)> _ready = new();
    private readonly List<Chunk> _emptyDone = new();

    private void DispatchMeshJobs(GraphicsDevice device, Vector3 camPos)
    {
        if (_pending.Count == 0) return;

        _ready.Clear();
        _emptyDone.Clear();
        foreach (var c in _pending)
        {
            if (_inFlight.Contains(c)) continue; // wait for the running job to finish first
            if (c.IsEmpty) { _emptyDone.Add(c); continue; }

            var ctr = (c.Bounds.Min + c.Bounds.Max) * 0.5f;
            _ready.Add((Vector3.DistanceSquared(ctr, camPos), c));
        }

        // Empty chunks need no job, just their old mesh cleared
        foreach (var c in _emptyDone)
        {
            c.ApplyMesh(device, new MeshData { Chunk = c, Version = c.Version });
            c.AppliedVersion = c.Version;
            _pending.Remove(c);
        }

        int slots = _maxMeshInFlight - _inFlight.Count;
        if (slots <= 0 || _ready.Count == 0) return;

        _ready.Sort((a, b) => a.d.CompareTo(b.d)); // nearest first

        int count = Math.Min(slots, _ready.Count);
        for (int i = 0; i < count; i++)
        {
            var chunk = _ready[i].c;
            var cols = Neighborhood(chunk.CX, chunk.CZ);
            int version = chunk.Version;

            _pending.Remove(chunk);
            _inFlight.Add(chunk);
            _pool.Enqueue(() => _meshed.Enqueue(ChunkMesher.Build(chunk, cols, version)));
        }
    }

    // GPU uploads must happen on the main thread, so they're spread over frames
    private void DrainMeshed(GraphicsDevice device)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < 4.0 && _meshed.TryDequeue(out var m))
        {
            _inFlight.Remove(m.Chunk);
            if (m.Chunk.Unloaded) continue;

            // Skip results older than what's already on screen (e.g. after an urgent rebuild)
            if (m.Version > m.Chunk.AppliedVersion)
            {
                m.Chunk.ApplyMesh(device, m);
                m.Chunk.AppliedVersion = m.Version;
            }
        }
    }

    // =====================  EDITING  =====================

    private void MarkDirty(Chunk c)
    {
        c.Version++;
        if (_urgentMode)
        {
            _urgent.Add(c);
            _pending.Remove(c);
        }
        else if (GetColumn(c.CX, c.CZ)?.Active == true)
        {
            _pending.Add(c);
        }
    }

    private void MarkDirtyAt(int x, int y, int z)
    {
        var c = GetChunkAt(x, y, z);
        if (c != null) MarkDirty(c);
    }

    public void SetBlock(int x, int y, int z, byte id)
    {
        if ((uint)y >= Height) return;
        var col = GetColumn(x >> 4, z >> 4);
        if (col == null) return;

        int lx = x & 15, lz = z & 15;
        var chunk = col.Chunks[y >> 4];
        int ly = y & 15;
        if (chunk.GetBlock(lx, ly, lz) == id) return;

        chunk.SetBlock(lx, ly, lz, id);
        col.Modified = true;

        _urgentMode = true;
        MarkDirty(chunk);

        // A block on a chunk border changes which faces the neighbor needs
        if (lx == 0)  MarkDirtyAt(x - 1, y, z);
        if (lx == 15) MarkDirtyAt(x + 1, y, z);
        if (ly == 0)  MarkDirtyAt(x, y - 1, z);
        if (ly == 15) MarkDirtyAt(x, y + 1, z);
        if (lz == 0)  MarkDirtyAt(x, y, z - 1);
        if (lz == 15) MarkDirtyAt(x, y, z + 1);

        Light.OnBlockChanged(x, y, z);
        _urgentMode = false;
    }

    // A light change can alter faces in a neighboring chunk too
    public void MarkLightDirty(int x, int y, int z)
    {
        int lx = x & 15, ly = y & 15, lz = z & 15;
        MarkDirtyAt(x, y, z);
        if (lx == 0)  MarkDirtyAt(x - 1, y, z);
        if (lx == 15) MarkDirtyAt(x + 1, y, z);
        if (ly == 0)  MarkDirtyAt(x, y - 1, z);
        if (ly == 15) MarkDirtyAt(x, y + 1, z);
        if (lz == 0)  MarkDirtyAt(x, y, z - 1);
        if (lz == 15) MarkDirtyAt(x, y, z + 1);
    }

    // =====================  DRAWING  =====================

    public void UpdateVisibility(BoundingFrustum frustum, Vector3 camPos)
    {
        _visible.Clear();
        float maxDist = (LoadRadius + 1) * Chunk.Size;
        float maxSq = maxDist * maxDist;

        foreach (var col in _columns.Values)
        foreach (var chunk in col.Chunks)
        {
            if (!chunk.HasMesh) continue;

            // Horizontal distance only, so a high camera doesn't hide distant terrain
            float dx = (chunk.Bounds.Min.X + 8f) - camPos.X;
            float dz = (chunk.Bounds.Min.Z + 8f) - camPos.Z;
            if (dx * dx + dz * dz > maxSq) continue;

            if (frustum.Intersects(chunk.Bounds)) _visible.Add(chunk);
        }
        foreach (var chunk in _visible) chunk.UpdateWater(camPos);
    }

    public void DrawSolid(GraphicsDevice device)
    {
        foreach (var chunk in _visible) chunk.DrawSolid(device);
    }

    public void DrawWater(GraphicsDevice device)
    {
        foreach (var chunk in _visible) chunk.DrawWater(device);
    }

    // Voxel DDA (Amanatides & Woo). Water is skipped, so you can target the seabed through it.
    // 'hit' is the solid block; 'prev' is the empty block the ray passed through just before it.
    public bool Raycast(Vector3 origin, Vector3 dir, float maxDist,
                        out (int x, int y, int z) hit, out (int x, int y, int z) prev)
    {
        int x = (int)MathF.Floor(origin.X);
        int y = (int)MathF.Floor(origin.Y);
        int z = (int)MathF.Floor(origin.Z);
        hit = prev = (x, y, z);

        int stepX = dir.X > 0 ? 1 : -1, stepY = dir.Y > 0 ? 1 : -1, stepZ = dir.Z > 0 ? 1 : -1;

        float tDX = dir.X != 0 ? MathF.Abs(1f / dir.X) : float.PositiveInfinity;
        float tDY = dir.Y != 0 ? MathF.Abs(1f / dir.Y) : float.PositiveInfinity;
        float tDZ = dir.Z != 0 ? MathF.Abs(1f / dir.Z) : float.PositiveInfinity;

        float tMX = dir.X != 0 ? (stepX > 0 ? x + 1 - origin.X : origin.X - x) * tDX : float.PositiveInfinity;
        float tMY = dir.Y != 0 ? (stepY > 0 ? y + 1 - origin.Y : origin.Y - y) * tDY : float.PositiveInfinity;
        float tMZ = dir.Z != 0 ? (stepZ > 0 ? z + 1 - origin.Z : origin.Z - z) * tDZ : float.PositiveInfinity;

        float t = 0;
        while (t <= maxDist)
        {
            byte b = GetBlock(x, y, z);
            if (b != _airId && b != _waterId)
            {
                hit = (x, y, z);
                return true;
            }
            prev = (x, y, z);

            if (tMX < tMY && tMX < tMZ) { x += stepX; t = tMX; tMX += tDX; }
            else if (tMY < tMZ)         { y += stepY; t = tMY; tMY += tDY; }
            else                        { z += stepZ; t = tMZ; tMZ += tDZ; }
        }
        return false;
    }
}
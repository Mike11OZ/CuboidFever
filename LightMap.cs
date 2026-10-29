using System.Collections.Generic;

namespace CuboidFever;

// Channels: 0 = sky, 1 = red, 2 = green, 3 = blue. Each cell holds 0-15.
public class LightMap
{
    private readonly World _world;

    // One-entry cache: flood fills touch the same column thousands of times in a row
    private Column _cache;
    private int _cacheX = int.MinValue, _cacheZ;

    // Direction index 3 is -Y, which the sky-light rule below depends on
    private static readonly int[,] N =
    {
        { 1, 0, 0 }, { -1, 0, 0 }, { 0, 1, 0 }, { 0, -1, 0 }, { 0, 0, 1 }, { 0, 0, -1 }
    };

    public LightMap(World world) => _world = world;

    public void InvalidateCache() { _cache = null; _cacheX = int.MinValue; }

    private Column ColAt(int x, int z)
    {
        int cx = x >> 4, cz = z >> 4;
        if (_cache != null && cx == _cacheX && cz == _cacheZ) return _cache;
        var c = _world.GetColumn(cx, cz);
        if (c != null) { _cache = c; _cacheX = cx; _cacheZ = cz; }
        return c;
    }

    private bool Loaded(int x, int y, int z) => y >= 0 && y < World.Height && ColAt(x, z) != null;

    // Above the world is full sky; below it and in unloaded columns is dark
    public int Get(int ch, int x, int y, int z)
    {
        if (y < 0) return 0;
        if (y >= World.Height) return ch == 0 ? 15 : 0;
        var c = ColAt(x, z);
        if (c == null) return 0;
        return c.Light[ch][Column.LIdx(x & 15, y, z & 15)];
    }

    private void Set(int ch, int x, int y, int z, int v)
    {
        ColAt(x, z).Light[ch][Column.LIdx(x & 15, y, z & 15)] = (byte)v;
        _world.MarkLightDirty(x, y, z);
    }


    private bool Transparent(int x, int y, int z)
    {
        byte id = _world.GetBlock(x, y, z);
        return BlockRegistry.Instance.Info[_world.GetBlock(x, y, z)].Transparent;
    }

    private static int Emit(int ch, byte block)
    {
        var definition = BlockRegistry.Instance.Get(block);

        if (!definition.Emissive)
            return 0;

        var e = definition.Emission;

        return ch == 1 ? e.R
            : ch == 2 ? e.G
            : ch == 3 ? e.B
            : 0;
    }

    // Spreads light outward from every cell in the queue
    private void Propagate(int ch, Queue<(int x, int y, int z)> q)
    {
        while (q.Count > 0)
        {
            var (x, y, z) = q.Dequeue();
            int l = Get(ch, x, y, z);
            if (l <= 1) continue;

            for (int i = 0; i < 6; i++)
            {
                int nx = x + N[i, 0], ny = y + N[i, 1], nz = z + N[i, 2];
                if (!Loaded(nx, ny, nz) || !Transparent(nx, ny, nz)) continue;

                int nl = (ch == 0 && i == 3 && l == 15) ? 15 : l - 1;
                if (Get(ch, nx, ny, nz) < nl)
                {
                    Set(ch, nx, ny, nz, nl);
                    q.Enqueue((nx, ny, nz));
                }
            }
        }
    }

    // Removes this cell's light and everything that depended on it.
    // Cells lit by something else are added to 'add' so they can re-spread.
    private void Remove(int ch, int x, int y, int z, Queue<(int x, int y, int z)> add)
    {
        int old = Get(ch, x, y, z);
        if (old == 0) return;
        Set(ch, x, y, z, 0);

        var rem = new Queue<(int x, int y, int z, int l)>();
        rem.Enqueue((x, y, z, old));

        while (rem.Count > 0)
        {
            var (cx, cy, cz, l) = rem.Dequeue();
            for (int i = 0; i < 6; i++)
            {
                int nx = cx + N[i, 0], ny = cy + N[i, 1], nz = cz + N[i, 2];
                if (!Loaded(nx, ny, nz)) continue;
                int nl = Get(ch, nx, ny, nz);
                if (nl == 0) continue;

                bool dependent = nl < l || (ch == 0 && i == 3 && l == 15 && nl == 15);
                if (dependent)
                {
                    Set(ch, nx, ny, nz, 0);
                    rem.Enqueue((nx, ny, nz, nl));

                    int em = Emit(ch, _world.GetBlock(nx, ny, nz));
                    if (em > 0) { Set(ch, nx, ny, nz, em); add.Enqueue((nx, ny, nz)); }
                }
                else add.Enqueue((nx, ny, nz));
            }
        }
    }

    // Call after a block changes. Fixes up all four channels around that cell.
    public void OnBlockChanged(int x, int y, int z)
    {
        byte id = _world.GetBlock(x, y, z);
        bool clear = BlockRegistry.Instance.Get(id).Transparent;

        for (int ch = 0; ch < 4; ch++)
        {
            var q = new Queue<(int x, int y, int z)>();
            Remove(ch, x, y, z, q);

            if (clear)
            {
                if (ch == 0 && Get(0, x, y + 1, z) == 15)
                {
                    Set(0, x, y, z, 15);
                    q.Enqueue((x, y, z));
                }
                for (int i = 0; i < 6; i++)
                {
                    int nx = x + N[i, 0], ny = y + N[i, 1], nz = z + N[i, 2];
                    if (Loaded(nx, ny, nz) && Get(ch, nx, ny, nz) > 0) q.Enqueue((nx, ny, nz));
                }
            }

            int em = Emit(ch, id);
            if (em > 0) { Set(ch, x, y, z, em); q.Enqueue((x, y, z)); }

            Propagate(ch, q);
        }
    }

    // Blends a newly loaded column's light with the neighbors that already exist.
    // Wherever one side of a border is at least 2 brighter than the other, light spreads across.
    public void Stitch(Column col)
    {
        for (int side = 0; side < 4; side++)
        {
            int dx = side == 0 ? 1 : side == 1 ? -1 : 0;
            int dz = side == 2 ? 1 : side == 3 ? -1 : 0;
            var nb = _world.GetColumn(col.CX + dx, col.CZ + dz);
            if (nb == null) continue;

            for (int ch = 0; ch < 4; ch++)
            {
                var q = new Queue<(int x, int y, int z)>();
                var la = col.Light[ch];
                var lb = nb.Light[ch];

                for (int t = 0; t < Column.Size; t++)
                for (int y = 0; y < World.Height; y++)
                {
                    // a = border cell in this column, b = the cell facing it in the neighbor
                    int ax = side == 0 ? 15 : side == 1 ? 0 : t;
                    int az = side == 2 ? 15 : side == 3 ? 0 : t;
                    int bx = side == 0 ? 0 : side == 1 ? 15 : t;
                    int bz = side == 2 ? 0 : side == 3 ? 15 : t;

                    int va = la[Column.LIdx(ax, y, az)];
                    int vb = lb[Column.LIdx(bx, y, bz)];

                    if (va - 1 > vb)
                    {
                        if (nb.IsTransparent(bx, y, bz)) q.Enqueue((col.CX * 16 + ax, y, col.CZ * 16 + az));
                    }
                    else if (vb - 1 > va)
                    {
                        if (col.IsTransparent(ax, y, az)) q.Enqueue((nb.CX * 16 + bx, y, nb.CZ * 16 + bz));
                    }
                }
                Propagate(ch, q);
            }
        }
    }
}
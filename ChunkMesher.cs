using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CuboidFever;

// The output of a mesh job: plain arrays, no GPU objects, so workers can make it.
public sealed class MeshData
{
    public Chunk Chunk;
    public int Version;
    public VertexPositionColorTexture[] SolidV, WaterV;
    public int[] SolidI, WaterI;
}

public static class ChunkMesher
{
    private const int P = Chunk.Size + 2;   // padded size: the chunk plus a 1-block border
    private const int PP = P * P, PCount = P * P * P;

    // Index offsets to the neighbor in each face direction (+X, -X, +Y, -Y, +Z, -Z)
    private static readonly int[] Offsets = { 1, -1, PP, -PP, P, -P };

    private static readonly Vector3[][] FaceCorners =
    {
        new[] { new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(1,1,0), new Vector3(1,0,0) }, // +X
        new[] { new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,1,1), new Vector3(0,0,1) }, // -X
        new[] { new Vector3(0,1,1), new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1) }, // +Y
        new[] { new Vector3(0,0,0), new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,0,0) }, // -Y
        new[] { new Vector3(0,0,1), new Vector3(0,1,1), new Vector3(1,1,1), new Vector3(1,0,1) }, // +Z
        new[] { new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0), new Vector3(0,0,0) }, // -Z
    };

    private static readonly Vector2[] FaceUV = { new(0, 1), new(0, 0), new(1, 0), new(1, 1) };
    private static readonly Vector2[] BottomUV = { new(0, 0), new(0, 1), new(1, 1), new(1, 0) };
    private static readonly float[] FaceShade = { 0.8f, 0.8f, 1.0f, 0.5f, 0.65f, 0.65f };

    // For each padded coordinate 0..17: which column it falls in (-1, 0, +1) and its local coordinate
    private static readonly int[] ColOff = new int[P];
    private static readonly int[] LocalOf = new int[P];

    static ChunkMesher()
    {
        for (int p = 0; p < P; p++)
        {
            ColOff[p] = p == 0 ? -1 : p == P - 1 ? 1 : 0;
            LocalOf[p] = (p - 1) & 15;
        }
    }

    private sealed class Builder
    {
        public readonly List<VertexPositionColorTexture> V = new();
        public readonly List<int> I = new();

        public void Clear() { V.Clear(); I.Clear(); }

    
        public void AddFace(int f, Vector3 pos, Color color, TextureRegion region)
        {
            int start = V.Count;

            // A tiny inset helps prevent neighboring atlas textures bleeding
            // into this face. Scale U and V independently.
            float insetU = 0.05f / TextureAtlas.Instance.Width;
            float insetV = 0.05f / TextureAtlas.Instance.Height;

            float widthU = region.U1 - region.U0;
            float heightV = region.V1 - region.V0;

            Vector2[] uvs = f == 3 ? BottomUV : FaceUV;

            for (int c = 0; c < 4; c++)
            {
                var uv = new Vector2(
                    region.U0 + insetU + uvs[c].X * (widthU - 2f * insetU),
                    region.V0 + insetV + uvs[c].Y * (heightV - 2f * insetV));

                V.Add(new VertexPositionColorTexture(
                    FaceCorners[f][c] + pos,
                    color,
                    uv));
            }

            I.Add(start);     I.Add(start + 1); I.Add(start + 2);
            I.Add(start);     I.Add(start + 2); I.Add(start + 3);
        }
    }
    // Each thread keeps its own scratch buffers, so meshing allocates almost nothing
    private sealed class Buffers
    {
        public readonly byte[] Blocks = new byte[PCount];
        public readonly byte[][] Light = { new byte[PCount], new byte[PCount], new byte[PCount], new byte[PCount] };
        public readonly Builder Solid = new(), Water = new();
    }

    [ThreadStatic] private static Buffers _buf;


    private static bool ShouldDrawFace(byte self, byte neighbor)
    {
        var registry = BlockRegistry.Instance;
        var selfDef = registry.Get(self);
        var neighborDef = registry.Get(neighbor);

        string selfName = selfDef.Name;
        string neighborName = neighborDef.Name;

        bool selfWater = selfName == "cuboidfever:water";
        bool neighborWater = neighborName == "cuboidfever:water";
        bool selfGlass = selfName == "cuboidfever:glass";
        bool neighborGlass = neighborName == "cuboidfever:glass";
        bool selfLeaves = selfName == "cuboidfever:leaves";
        bool neighborLeaves = neighborName == "cuboidfever:leaves";

        // Air is always an exposed surface.
        if (neighborName == "cuboidfever:air")
            return true;

        // Water surfaces are only drawn against air.
        // The neighboring block renders its own visible face, if appropriate.
        if (selfWater)
            return false;

        // Suppress internal faces between matching transparent blocks.
        if ((selfGlass && neighborGlass) ||
            (selfLeaves && neighborLeaves))
            return false;

        // Glass faces remain visible against water and leaves,
        // but not against opaque terrain.
        if (selfGlass)
            return neighborWater || neighborLeaves;

        // Opaque terrain should still be visible through transparent materials.
        if (!selfDef.Transparent)
            return neighborDef.Transparent;

        // Other transparent-material boundaries.
        return neighborDef.Transparent;
    }

    // cols = the 3x3 columns around the chunk, index (dz + 1) * 3 + (dx + 1); center is 4
    public static MeshData Build(Chunk chunk, Column[] cols, int version)
    {
        var result = new MeshData { Chunk = chunk, Version = version };
        if (chunk.IsEmpty) return result;

        var b = _buf ??= new Buffers();
        Snapshot(chunk, cols, b);
        b.Solid.Clear();
        b.Water.Clear();

        int ox = chunk.CX * Chunk.Size, oy = chunk.CY * Chunk.Size, oz = chunk.CZ * Chunk.Size;

        for (int y = 0; y < Chunk.Size; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            int i = ((y + 1) * P + (z + 1)) * P + (x + 1);
            byte id = b.Blocks[i];
            var definition = BlockRegistry.Instance.Get(id);

            if (definition.Name == "cuboidfever:air")
                continue;

            Builder builder = definition.Name == "cuboidfever:water"
                ? b.Water
                : b.Solid;
            var pos = new Vector3(ox + x, oy + y, oz + z);

            for (int f = 0; f < 6; f++)
            {
                int n = i + Offsets[f];
                if (!ShouldDrawFace(id, b.Blocks[n])) continue;
                var region = BlockRegistry.Instance.GetTextureRegion(id, f);
                    builder.AddFace(f, pos, LightColor(id, f, n, b.Light), region);
            }
        }

        if (b.Solid.I.Count > 0) { result.SolidV = b.Solid.V.ToArray(); result.SolidI = b.Solid.I.ToArray(); }
        if (b.Water.I.Count > 0) { result.WaterV = b.Water.V.ToArray(); result.WaterI = b.Water.I.ToArray(); }
        return result;
    }

    // Copies blocks and light for the chunk and a 1-block border into flat arrays
    private static void Snapshot(Chunk c, Column[] cols, Buffers b)
    {
        int baseY = c.CY * Chunk.Size - 1;

        for (int py = 0; py < P; py++)
        {
            int wy = baseY + py;
            bool outside = wy < 0 || wy >= World.Height;

            for (int pz = 0; pz < P; pz++)
            {
                int rowStart = (ColOff[pz] + 1) * 3;
                int lz = LocalOf[pz];

                for (int px = 0; px < P; px++)
                {
                    int i = (py * P + pz) * P + px;

                    if (outside)
                    {
                        b.Blocks[i] = BlockRegistry.Instance.GetId("cuboidfever:air");
                        b.Light[0][i] = (byte)(wy < 0 ? 0 : 15);
                        b.Light[1][i] = b.Light[2][i] = b.Light[3][i] = 0;
                        continue;
                    }

                    var col = cols[rowStart + ColOff[px] + 1];
                    if (col == null) // shouldn't happen: columns are only meshed once surrounded
                    {
                        b.Blocks[i] = BlockRegistry.Instance.GetId("cuboidfever:stone");
                        b.Light[0][i] = b.Light[1][i] = b.Light[2][i] = b.Light[3][i] = 0;
                        continue;
                    }

                    int lx = LocalOf[px];
                    b.Blocks[i] = col.GetBlock(lx, wy, lz);
                    int li = Column.LIdx(lx, wy, lz);
                    b.Light[0][i] = col.Light[0][li];
                    b.Light[1][i] = col.Light[1][li];
                    b.Light[2][i] = col.Light[2][li];
                    b.Light[3][i] = col.Light[3][li];
                }
            }
        }
    }

    // Vertex color = light (sampled from the cell in front of the face) x face shade

    private static Color LightColor(byte id, int face, int n, byte[][] light)
    {
        var definition = BlockRegistry.Instance.Get(id);

        float a = definition.Name == "cuboidfever:water" ? 0.6f : 1f;
        float r, g, bl;

        if (definition.Emissive)
        {
            Color tint = definition.Tint?.ToXnaColor() ?? Color.White;
            r = tint.R / 255f;
            g = tint.G / 255f;
            bl = tint.B / 255f;
        }
        else
        {
            float shade = FaceShade[face];
            float sky = Chunk.Curve[light[0][n]];

            r = MathF.Max(sky, Chunk.Curve[light[1][n]]) * shade;
            g = MathF.Max(sky, Chunk.Curve[light[2][n]]) * shade;
            bl = MathF.Max(sky, Chunk.Curve[light[3][n]]) * shade;
        }

        return new Color(r * a, g * a, bl * a, a);
    }

}
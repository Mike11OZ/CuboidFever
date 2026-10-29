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
    public byte[] WaterF;
}

public static class ChunkMesher
{
    public const int MaxLeafDepth = 3;
    public static int LeafCullDepth = 3;

    // One extra cell because the adjacent leaf is already at distance 1.
    private const int Pad = MaxLeafDepth + 1;
    private const int P = Chunk.Size + 2 * Pad;
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

    public static bool AmbientOcclusion = true;

    // Brightness by occlusion level: 0 = fully boxed in ... 3 = open
    private static readonly float[] AoCurve = { 0.7f, 0.85f, 0.93f, 1.0f };
    private static readonly float[] Flat = { 1f, 1f, 1f, 1f };

    // For each face and vertex: index offsets (in the padded snapshot) of the two side cells
    // and the corner cell that sit on the open side of that vertex.
    private static readonly int[,,] AoIdx = BuildAo();

    private static int[,,] BuildAo()
    {
        int[] stride = { 1, PP, P };   // index step for the x, y and z axes
        var r = new int[6, 4, 3];

        for (int f = 0; f < 6; f++)
        {
            int na = f / 2;                          // axis the face points along
            int ns = f % 2 == 0 ? 1 : -1;            // and which way
            int t1 = (na + 1) % 3, t2 = (na + 2) % 3;
            int normalOff = ns * stride[na];

            for (int v = 0; v < 4; v++)
            {
                Vector3 c = FaceCorners[f][v];
                float[] comp = { c.X, c.Y, c.Z };
                int d1 = comp[t1] > 0.5f ? 1 : -1;   // which way this vertex leans on each tangent axis
                int d2 = comp[t2] > 0.5f ? 1 : -1;

                r[f, v, 0] = normalOff + d1 * stride[t1];
                r[f, v, 1] = normalOff + d2 * stride[t2];
                r[f, v, 2] = normalOff + d1 * stride[t1] + d2 * stride[t2];
            }
        }
        return r;
    }

    private static float AoFactor(byte[] blocks, BlockInfo[] info, int i, int f, int v)
    {
        bool s1 = info[blocks[i + AoIdx[f, v, 0]]].Occludes;
        bool s2 = info[blocks[i + AoIdx[f, v, 1]]].Occludes;
        bool c  = info[blocks[i + AoIdx[f, v, 2]]].Occludes;

        // Two sides closed means the corner is sealed whether or not the diagonal is solid
        int level = (s1 && s2) ? 0 : 3 - ((s1 ? 1 : 0) + (s2 ? 1 : 0) + (c ? 1 : 0));
        return AoCurve[level];
    }

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
            ColOff[p] = p < Pad ? -1 : p >= Pad + Chunk.Size ? 1 : 0;
            LocalOf[p] = (p - Pad) & 15;
        }
    }

    private sealed class Builder
    {
        public readonly List<byte> F = new();
        public readonly List<VertexPositionColorTexture> V = new();
        public readonly List<int> I = new();

        public void Clear() { V.Clear(); I.Clear(); F.Clear(); }

    
        public void AddFace(int f, Vector3 pos, Color color, TextureRegion region, float[] ao)
        {
            int start = V.Count;
            for (int c = 0; c < 4; c++) F.Add((byte)f);   // face index per vertex, for the water Fresnel

            float insetU = BlockRegistry.Instance.InsetU;
            float insetV = BlockRegistry.Instance.InsetV;
            float widthU = region.U1 - region.U0;
            float heightV = region.V1 - region.V0;
            Vector2[] uvs = f == 3 ? BottomUV : FaceUV;

            float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f, a = color.A / 255f;

            for (int c = 0; c < 4; c++)
            {
                var uv = new Vector2(
                    region.U0 + insetU + uvs[c].X * (widthU - 2f * insetU),
                    region.V0 + insetV + uvs[c].Y * (heightV - 2f * insetV));

                float k = ao[c];
                V.Add(new VertexPositionColorTexture(
                    FaceCorners[f][c] + pos, new Color(r * k, g * k, b * k, a), uv));
            }

            // Split the quad along the diagonal joining the darker pair of corners,
            // so the shading blends smoothly instead of creasing
            if (ao[0] + ao[2] > ao[1] + ao[3])
            {
                I.Add(start + 1); I.Add(start + 2); I.Add(start + 3);
                I.Add(start + 1); I.Add(start + 3); I.Add(start);
            }
            else
            {
                I.Add(start);     I.Add(start + 1); I.Add(start + 2);
                I.Add(start);     I.Add(start + 2); I.Add(start + 3);
            }
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


    private static bool ShouldDrawFace(in BlockInfo self, in BlockInfo nb)
    {
        if (nb.IsAir) return true;                         // air is always an exposed surface
        if (self.IsWater) return false;                    // water only shows against air
        if (self.IsGlass && nb.IsGlass) return false;      // no internal faces
        if (self.IsLeaves && nb.IsLeaves) return false;
        if (self.IsGlass) return nb.IsWater || nb.IsLeaves;
        return nb.Transparent;                             // opaque and other blocks show through see-through neighbors
    }

    // Called for a leaf face whose neighbor is also a leaf. 'neighborIndex' is that neighbor's
    // index in the padded snapshot and 'step' is the index offset for the face direction.
    private static bool LeafFaceVisible(
        byte[] blocks, BlockInfo[] info, int neighborIndex, int step)
    {
        int depth = Math.Clamp(LeafCullDepth, 0, MaxLeafDepth);

        if (depth == 0) return true;  // Draw all leaf faces
        if (depth == 1) return false; // Cull adjacent leaf faces

        int idx = neighborIndex;

        // Check two positions for depth 2, three for depth 3.
        for (int d = 2; d <= depth + 1; d++)
        {
            idx += step;
            ref readonly BlockInfo bi = ref info[blocks[idx]];

            if (bi.IsAir) return true;
            if (!bi.IsLeaves) return false;
        }

        return false;
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

        var registry = BlockRegistry.Instance;
        var info = registry.Info;
        var ao = new float[4];

        for (int y = 0; y < Chunk.Size; y++)
        for (int z = 0; z < Chunk.Size; z++)
        for (int x = 0; x < Chunk.Size; x++)
        {
            int i = ((y + Pad) * P + (z + Pad)) * P + (x + Pad);
            byte id = b.Blocks[i];
            ref readonly BlockInfo self = ref info[id];
            if (self.IsAir) continue;

            Builder builder = self.IsWater ? b.Water : b.Solid;
            var pos = new Vector3(ox + x, oy + y, oz + z);

            for (int f = 0; f < 6; f++)
            {
                int n = i + Offsets[f];
                byte nbId = b.Blocks[n];
                bool draw = self.IsLeaves && info[nbId].IsLeaves
                    ? LeafFaceVisible(b.Blocks, info, n, Offsets[f])
                    : ShouldDrawFace(in self, in info[nbId]);
                if (!draw) continue;
                float[] faceAo = Flat;
                if (AmbientOcclusion && !self.IsWater && !self.Emissive)
                {
                    for (int v = 0; v < 4; v++) ao[v] = AoFactor(b.Blocks, info, i, f, v);
                    faceAo = ao;
                }

                builder.AddFace(f, pos, LightColor(in self, f, n, b.Light), registry.FastRegion(id, f), faceAo);
            }
        }

        if (b.Solid.I.Count > 0) { result.SolidV = b.Solid.V.ToArray(); result.SolidI = b.Solid.I.ToArray(); }
        if (b.Water.I.Count > 0)
        {
            result.WaterV = b.Water.V.ToArray();
            result.WaterI = b.Water.I.ToArray();
            result.WaterF = b.Water.F.ToArray();
        }
        return result;
    }

    // Copies blocks and light for the chunk and a 1-block border into flat arrays
    private static void Snapshot(Chunk c, Column[] cols, Buffers b)
    {
        const byte air = Block.Air;
        byte stone = BlockRegistry.Instance.GetId("cuboidfever:stone");
        int baseY = c.CY * Chunk.Size - Pad;

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
                        b.Blocks[i] = air;
                        b.Light[0][i] = (byte)(wy < 0 ? 0 : 15);
                        b.Light[1][i] = b.Light[2][i] = b.Light[3][i] = 0;
                        continue;
                    }

                    var col = cols[rowStart + ColOff[px] + 1];
                    if (col == null) // shouldn't happen: columns are only meshed once surrounded
                    {
                        b.Blocks[i] = stone;
                        b.Light[0][i] = b.Light[1][i] = b.Light[2][i] = b.Light[3][i] = 0;
                        continue;
                    }

                    int lx = LocalOf[px];
                    b.Blocks[i] = col.GetBlock(lx, wy, lz);
                    int li = Column.LIdx(lx, wy, lz);
                    // Light is only read for cells next to the chunk, so skip the outer padding
                    bool lit = py >= Pad - 1 && py <= Pad + Chunk.Size
                            && pz >= Pad - 1 && pz <= Pad + Chunk.Size
                            && px >= Pad - 1 && px <= Pad + Chunk.Size;
                    if (lit)
                    {
                        b.Light[0][i] = col.Light[0][li];
                        b.Light[1][i] = col.Light[1][li];
                        b.Light[2][i] = col.Light[2][li];
                        b.Light[3][i] = col.Light[3][li];
                    }
                }
            }
        }
    }

    // Vertex color = light (sampled from the cell in front of the face) x face shade

    private static Color LightColor(in BlockInfo self, int face, int n, byte[][] light)
    {
        if (self.Emissive)
            return new Color(self.TintR, self.TintG, self.TintB, 1f);

        float shade = FaceShade[face];
        float sky = Chunk.Curve[light[0][n]];

        float r  = MathF.Max(sky, Chunk.Curve[light[1][n]]) * shade * self.TintR;
        float g  = MathF.Max(sky, Chunk.Curve[light[2][n]]) * shade * self.TintG;
        float bl = MathF.Max(sky, Chunk.Curve[light[3][n]]) * shade * self.TintB;

        return new Color(r, g, bl, 1f);
    }

}
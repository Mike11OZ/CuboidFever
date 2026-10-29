using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace CuboidFever;

public class ParticleSystem
{
    private readonly List<Particle> _particles = new();
    private readonly Random _rng = new();

    // Dynamic texture atlas data
    private Color[] _atlasPixels;
    private int _atlasWidth;
    private int _atlasHeight;
    private int _tileSize;

    // Particle rendering buffers
    private int _capacity;
    private VertexPositionColorTexture[] _vertices =
        Array.Empty<VertexPositionColorTexture>();
    private short[] _indices = Array.Empty<short>();

    private struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector2 Size;
        public Vector2 UvMin;
        public Vector2 UvMax;
        public Color Color;
        public float Life;
        public float MaxLife;
    }

    public void Init(TextureAtlas atlas)
    {
        _atlasWidth = atlas.Width;
        _atlasHeight = atlas.Height;
        _tileSize = atlas.TextureSize;
        _atlasPixels = atlas.Pixels;
    }

    public void SpawnBurst(
        World world,
        int bx,
        int by,
        int bz,
        byte blockId,
        int count = 12)
    {
        var region = BlockRegistry.Instance.GetTextureRegion(blockId, 2);
        var bounds = region.PixelBounds;

        Color color = LitTint(world, bx, by, bz, blockId);

        var center = new Vector3(
            bx + 0.5f,
            by + 0.5f,
            bz + 0.5f);

        int fragPx = Math.Max(1, _tileSize / 4);
        int fragmentsPerAxis = _tileSize / fragPx;

        for (int i = 0; i < count; i++)
        {
            Vector3 vel = new Vector3(
                (float)(_rng.NextDouble() - 0.5) * 4f,
                (float)(_rng.NextDouble() * 3f + 1f),
                (float)(_rng.NextDouble() - 0.5) * 4f);

            int fx = 0;
            int fy = 0;

            // Prefer opaque fragments.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                fx = _rng.Next(fragmentsPerAxis) * fragPx;
                fy = _rng.Next(fragmentsPerAxis) * fragPx;

                int sampleX = bounds.X + fx + fragPx / 2;
                int sampleY = bounds.Y + fy + fragPx / 2;

                if (_atlasPixels[sampleY * _atlasWidth + sampleX].A >= 128)
                    break;
            }

            float u0 = (bounds.X + fx) / (float)_atlasWidth;
            float v0 = (bounds.Y + fy) / (float)_atlasHeight;
            float du = fragPx / (float)_atlasWidth;
            float dv = fragPx / (float)_atlasHeight;

            _particles.Add(new Particle
            {
                Position = center + new Vector3(
                    (float)(_rng.NextDouble() - 0.5) * 0.6f,
                    (float)(_rng.NextDouble() - 0.5) * 0.6f,
                    (float)(_rng.NextDouble() - 0.5) * 0.6f),

                Velocity = vel,
                Size = new Vector2(0.12f, 0.12f),
                UvMin = new Vector2(u0, v0),
                UvMax = new Vector2(u0 + du, v0 + dv),
                Color = color,
                Life = 0f,
                MaxLife = 0.35f + (float)_rng.NextDouble() * 0.3f
            });
        }
    }

    // Block tint multiplied by the light level at the cell.
    private static Color LitTint(
        World world,
        int x,
        int y,
        int z,
        byte id)
    {
        var L = world.Light;

        float sky = Chunk.Curve[L.Get(0, x, y, z)];
        float r = MathF.Max(sky, Chunk.Curve[L.Get(1, x, y, z)]);
        float g = MathF.Max(sky, Chunk.Curve[L.Get(2, x, y, z)]);
        float b = MathF.Max(sky, Chunk.Curve[L.Get(3, x, y, z)]);

        Color t = BlockRegistry.Instance.GetTint(id);

        return new Color(
            t.R / 255f * r,
            t.G / 255f * g,
            t.B / 255f * b,
            1f);
    }

    public void Update(float dt, World world)
    {
        const float gravity = 18f;

        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];

            p.Life += dt;

            if (p.Life >= p.MaxLife)
            {
                _particles.RemoveAt(i);
                continue;
            }

            p.Velocity.Y -= gravity * dt;

            // Move one axis at a time.
            Vector3 pos = p.Position;

            float nx = pos.X + p.Velocity.X * dt;

            if (Solid(world, nx, pos.Y, pos.Z))
                p.Velocity.X *= -0.3f;
            else
                pos.X = nx;

            float ny = pos.Y + p.Velocity.Y * dt;

            if (Solid(world, pos.X, ny, pos.Z))
            {
                if (p.Velocity.Y < 0)
                {
                    // Ground friction.
                    p.Velocity.X *= 0.6f;
                    p.Velocity.Z *= 0.6f;
                }

                p.Velocity.Y *= -0.3f;
            }
            else
            {
                pos.Y = ny;
            }

            float nz = pos.Z + p.Velocity.Z * dt;

            if (Solid(world, pos.X, pos.Y, nz))
                p.Velocity.Z *= -0.3f;
            else
                pos.Z = nz;

            p.Position = pos;
            _particles[i] = p;
        }
    }

    private static bool Solid(World w, float x, float y, float z) =>
        w.IsSolid(
            (int)MathF.Floor(x),
            (int)MathF.Floor(y),
            (int)MathF.Floor(z));

    private void EnsureCapacity(int quads)
    {
        if (quads <= _capacity)
            return;

        _capacity = Math.Max(Math.Max(quads, _capacity * 2), 64);

        _vertices =
            new VertexPositionColorTexture[_capacity * 4];

        _indices = new short[_capacity * 6];

        for (int q = 0; q < _capacity; q++)
        {
            int v = q * 4;
            int i = q * 6;

            _indices[i]     = (short)v;
            _indices[i + 1] = (short)(v + 1);
            _indices[i + 2] = (short)(v + 2);
            _indices[i + 3] = (short)v;
            _indices[i + 4] = (short)(v + 2);
            _indices[i + 5] = (short)(v + 3);
        }
    }

    public void Draw(
        GraphicsDevice device,
        BasicEffect effect,
        BoundingFrustum frustum,
        Vector3 cameraRight,
        Vector3 cameraUp)
    {
        if (_particles.Count == 0)
            return;

        EnsureCapacity(_particles.Count);

        int quads = 0;

        for (int i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];

            if (frustum.Contains(p.Position) == ContainmentType.Disjoint)
                continue;

            Vector3 halfW = cameraRight * (p.Size.X * 0.5f);
            Vector3 halfH = cameraUp * (p.Size.Y * 0.5f);

            // Fade particles near the end of their lifetime.
            Color c = p.Color * (1f - p.Life / p.MaxLife);

            int v = quads * 4;

            _vertices[v] = new VertexPositionColorTexture(
                p.Position - halfW - halfH,
                c,
                new Vector2(p.UvMin.X, p.UvMax.Y));

            _vertices[v + 1] = new VertexPositionColorTexture(
                p.Position - halfW + halfH,
                c,
                new Vector2(p.UvMin.X, p.UvMin.Y));

            _vertices[v + 2] = new VertexPositionColorTexture(
                p.Position + halfW + halfH,
                c,
                new Vector2(p.UvMax.X, p.UvMin.Y));

            _vertices[v + 3] = new VertexPositionColorTexture(
                p.Position + halfW - halfH,
                c,
                new Vector2(p.UvMax.X, p.UvMax.Y));

            quads++;
        }

        if (quads == 0)
            return;

        device.SamplerStates[0] = SamplerState.PointClamp;
        device.BlendState = BlendState.AlphaBlend;
        device.DepthStencilState = DepthStencilState.DepthRead;

        foreach (var pass in effect.CurrentTechnique.Passes)
        {
            pass.Apply();

            device.DrawUserIndexedPrimitives(
                PrimitiveType.TriangleList,
                _vertices,
                0,
                quads * 4,
                _indices,
                0,
                quads * 2);
        }
    }
}
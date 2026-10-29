
using System;

namespace CuboidFever;

public class TerrainGenerator
{
    // Block IDs loaded from the data-driven block registry.
    private readonly byte _airId;
    private readonly byte _stoneId;
    private readonly byte _waterId;
    private readonly byte _grassId;
    private readonly byte _dirtId;
    private readonly byte _sandId;
    private readonly byte _bedrockId;
    private readonly byte _logId;
    private readonly byte _leavesId;

    // ---- Tunables (tweak these while you experiment) ----
    private const double BaseHeight = 0.40;
    private const double DepthInfluence = 2.0;
    private const double Roughness = 1.0;

    private const double NoiseScale = 684.412;
    private const int GX = 5, GY = 17, GZ = 5;

    private readonly int _seed;

    private readonly OctaveNoise _low, _high, _select, _sandNoise,
        _stoneDepth, _scale2D, _depth2D;

    public TerrainGenerator(int seed)
    {
        _seed = seed;

        // BlockRegistry must be initialized before creating the world.
        var blocks = BlockRegistry.Instance;

        _airId = blocks.GetId("cuboidfever:air");
        _stoneId = blocks.GetId("cuboidfever:stone");
        _waterId = blocks.GetId("cuboidfever:water");
        _grassId = blocks.GetId("cuboidfever:grass");
        _dirtId = blocks.GetId("cuboidfever:dirt");
        _sandId = blocks.GetId("cuboidfever:sand");
        _bedrockId = blocks.GetId("cuboidfever:bedrock");
        _logId = blocks.GetId("cuboidfever:log");
        _leavesId = blocks.GetId("cuboidfever:leaves");

        var r = new Random(seed);

        _low = new OctaveNoise(r, 16);
        _high = new OctaveNoise(r, 16);
        _select = new OctaveNoise(r, 8);
        _sandNoise = new OctaveNoise(r, 4);
        _stoneDepth = new OctaveNoise(r, 4);
        _scale2D = new OctaveNoise(r, 10);
        _depth2D = new OctaveNoise(r, 16);
    }

    public void GenerateColumn(int cx, int cz, byte[,,] col)
    {
        var density = BuildDensityLattice(cx, cz);

        FillFromDensity(density, col);
        SurfacePass(cx, cz, col);
        PlaceTrees(cx, cz, col);
    }

    private void PlaceTrees(int cx, int cz, byte[,,] col)
    {
        var rng = new Random(
            unchecked(_seed ^ 0x5EED ^
            (cx * 73856093) ^ (cz * 19349663)));

        for (int x = 2; x < 14; x++)
        for (int z = 2; z < 14; z++)
        {
            if (rng.Next(50) != 0)
                continue;

            int y = World.Height - 1;

            while (y > 0 && col[x, y, z] == _airId)
                y--;

            // Water and sand fail this check, preventing seabed trees.
            if (col[x, y, z] != _grassId)
                continue;

            int h = 4 + rng.Next(3);

            if (y + h + 2 >= World.Height)
                continue;

            // Tree trunk.
            for (int i = 1; i <= h; i++)
                PutBlock(col, x, y + i, z, _logId);

            // Tree leaves.
            int top = y + h;

            for (int ly = top - 2; ly <= top + 1; ly++)
            {
                int r = ly >= top ? 1 : 2;

                for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    bool corner =
                        Math.Abs(dx) == r &&
                        Math.Abs(dz) == r;

                    if (corner &&
                        (ly >= top || rng.Next(2) == 0))
                        continue;

                    PutBlock(
                        col,
                        x + dx,
                        ly,
                        z + dz,
                        _leavesId);
                }
            }
        }
    }

    // Places a block only if the target position contains air.
    private void PutBlock(
        byte[,,] col,
        int x,
        int y,
        int z,
        byte id)
    {
        if (x < 0 || x >= 16 ||
            z < 0 || z >= 16 ||
            y < 0 || y >= World.Height)
            return;

        if (col[x, y, z] != _airId)
            return;

        col[x, y, z] = id;
    }

    private double[,,] BuildDensityLattice(int cx, int cz)
    {
        var d = new double[GX, GY, GZ];

        for (int i = 0; i < GX; i++)
        for (int k = 0; k < GZ; k++)
        {
            // Global lattice coordinates ensure neighboring chunks
            // share identical edge samples.
            double cellX = cx * 4 + i;
            double cellZ = cz * 4 + k;

            // 2D noise: overall height and roughness.
            double scaleN = _scale2D.Sample(
                cellX * 1.121, 10, cellZ * 1.121);

            double depthN = ShapeDepth(
                _depth2D.Sample(
                    cellX * 200, 10, cellZ * 200) / 8000.0);

            double variation =
                Math.Clamp(
                    (scaleN + 256) / 512 * Roughness,
                    0, 1) * 0.9 + 0.1;

            double heightOffset =
                BaseHeight + depthN * DepthInfluence;

            double centerCell =
                GY / 2.0 + heightOffset * (GY / 16.0) * 4.0;

            for (int j = 0; j < GY; j++)
            {
                double lo = _low.Sample(
                    cellX * NoiseScale,
                    j * NoiseScale,
                    cellZ * NoiseScale) / 512;

                double hi = _high.Sample(
                    cellX * NoiseScale,
                    j * NoiseScale,
                    cellZ * NoiseScale) / 512;

                double sel = (
                    _select.Sample(
                        cellX * NoiseScale / 80,
                        j * NoiseScale / 160,
                        cellZ * NoiseScale / 80) / 10 + 1) / 2;

                double v = sel < 0
                    ? lo
                    : sel > 1
                        ? hi
                        : lo + (hi - lo) * sel;

                // Density falls as altitude increases.
                // Below the center, the gradient is four times stronger.
                double grad = (j - centerCell) * 12.0 / variation;

                if (grad < 0)
                    grad *= 4;

                v -= grad;

                // Force air at the very top of the world.
                if (j > GY - 4)
                {
                    double t = (j - (GY - 4)) / 3.0;
                    v = v * (1 - t) + -10 * t;
                }

                d[i, j, k] = v;
            }
        }

        return d;
    }

    private static double ShapeDepth(double d)
    {
        if (d < 0)
            d = -d * 0.3;

        d = d * 3 - 2;

        if (d < 0)
        {
            d /= 2;

            if (d < -1)
                d = -1;

            d /= 1.4;
            d /= 2;
        }
        else
        {
            if (d > 1)
                d = 1;

            d /= 8;
        }

        return d;
    }

    private void FillFromDensity(double[,,] g, byte[,,] col)
    {
        for (int i = 0; i < 4; i++)
        for (int k = 0; k < 4; k++)
        for (int j = 0; j < 16; j++)
        {
            for (int dy = 0; dy < 8; dy++)
            {
                double fy = dy / 8.0;

                double a = Lerp(
                    fy, g[i, j, k], g[i, j + 1, k]);

                double b = Lerp(
                    fy, g[i + 1, j, k], g[i + 1, j + 1, k]);

                double c = Lerp(
                    fy, g[i, j, k + 1], g[i, j + 1, k + 1]);

                double e = Lerp(
                    fy, g[i + 1, j, k + 1],
                    g[i + 1, j + 1, k + 1]);

                int y = j * 8 + dy;

                for (int dx = 0; dx < 4; dx++)
                {
                    double fx = dx / 4.0;

                    double ab = Lerp(fx, a, b);
                    double ce = Lerp(fx, c, e);

                    for (int dz = 0; dz < 4; dz++)
                    {
                        double v = Lerp(dz / 4.0, ab, ce);

                        byte block = v > 0
                            ? _stoneId
                            : y < World.SeaLevel
                                ? _waterId
                                : _airId;

                        col[
                            i * 4 + dx,
                            y,
                            k * 4 + dz] = block;
                    }
                }
            }
        }
    }

    private static double Lerp(
        double t,
        double a,
        double b)
        => a + t * (b - a);

    // Converts the top of the stone into grass, dirt, or sand
    // and adds bedrock.
    private void SurfacePass(int cx, int cz, byte[,,] col)
    {
        var rng = new Random(
            unchecked(_seed * 31 +
            cx * 341873128 +
            cz * 132897987));

        const double d = 0.03125;

        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
        {
            int wx = cx * 16 + x;
            int wz = cz * 16 + z;

            bool isSand =
                _sandNoise.Sample(wx * d, wz * d, 0) +
                rng.NextDouble() * 0.2 > 0;

            int stoneDepth = (int)(
                _stoneDepth.Sample(
                    wx * d * 2,
                    wz * d * 2,
                    d * 2) / 3.0 +
                3.0 +
                rng.NextDouble() * 0.25);

            int run = -1;
            byte filler = _dirtId;

            for (int y = World.Height - 1; y >= 0; y--)
            {
                if (y < 5 && y <= rng.Next(5))
                {
                    col[x, y, z] = _bedrockId;
                    continue;
                }

                byte b = col[x, y, z];

                if (b == _airId)
                {
                    run = -1;
                    continue;
                }

                // Leave water unchanged.
                if (b != _stoneId)
                    continue;

                // First stone below air or water is the surface.
                if (run == -1)
                {
                    byte top = _grassId;
                    filler = _dirtId;

                    if (stoneDepth <= 0)
                    {
                        top = _airId;
                        filler = _stoneId;
                    }
                    else if (
                        y >= World.SeaLevel - 4 &&
                        y <= World.SeaLevel + 1 &&
                        isSand)
                    {
                        top = _sandId;
                        filler = _sandId;
                    }

                    if (y < World.SeaLevel && top == _airId)
                        top = _waterId;

                    run = stoneDepth;

                    // Above the waterline: top block.
                    // Underwater: filler, so no underwater grass.
                    col[x, y, z] =
                        y >= World.SeaLevel - 1
                            ? top
                            : filler;
                }
                else if (run > 0)
                {
                    run--;
                    col[x, y, z] = filler;
                }
            }
        }
    }
}

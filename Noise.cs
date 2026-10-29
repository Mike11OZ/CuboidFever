using System;

namespace CuboidFever;

// Ken Perlin's "improved noise". Output is roughly -1..1.
public class PerlinNoise
{
    private readonly int[] _p = new int[512];
    private readonly double _xo, _yo, _zo; // random offset per instance, so octaves don't all share an origin

    public PerlinNoise(Random rng)
    {
        _xo = rng.NextDouble() * 256;
        _yo = rng.NextDouble() * 256;
        _zo = rng.NextDouble() * 256;

        var perm = new int[256];
        for (int i = 0; i < 256; i++) perm[i] = i;
        for (int i = 255; i > 0; i--) // Fisher-Yates shuffle
        {
            int j = rng.Next(i + 1);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }
        for (int i = 0; i < 512; i++) _p[i] = perm[i & 255];
    }

    public double Noise(double x, double y, double z)
    {
        x += _xo; y += _yo; z += _zo;
        double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
        int X = (int)fx & 255, Y = (int)fy & 255, Z = (int)fz & 255;
        x -= fx; y -= fy; z -= fz;
        double u = Fade(x), v = Fade(y), w = Fade(z);

        int A = _p[X] + Y, AA = _p[A] + Z, AB = _p[A + 1] + Z;
        int B = _p[X + 1] + Y, BA = _p[B] + Z, BB = _p[B + 1] + Z;

        return Lerp(w,
            Lerp(v, Lerp(u, Grad(_p[AA], x, y, z),         Grad(_p[BA], x - 1, y, z)),
                    Lerp(u, Grad(_p[AB], x, y - 1, z),     Grad(_p[BB], x - 1, y - 1, z))),
            Lerp(v, Lerp(u, Grad(_p[AA + 1], x, y, z - 1), Grad(_p[BA + 1], x - 1, y, z - 1)),
                    Lerp(u, Grad(_p[AB + 1], x, y - 1, z - 1), Grad(_p[BB + 1], x - 1, y - 1, z - 1))));
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    private static double Lerp(double t, double a, double b) => a + t * (b - a);

    private static double Grad(int hash, double x, double y, double z)
    {
        int h = hash & 15;
        double u = h < 8 ? x : y;
        double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
}

// Beta-style octave noise: each octave has HALF the frequency and DOUBLE the
// amplitude of the previous one, so the big slow octaves dominate. (This is the
// opposite of the usual fBm convention, and it's what gives Beta its huge landforms.)
public class OctaveNoise
{
    private readonly PerlinNoise[] _octaves;

    public OctaveNoise(Random rng, int octaves)
    {
        _octaves = new PerlinNoise[octaves];
        for (int i = 0; i < octaves; i++) _octaves[i] = new PerlinNoise(rng);
    }

    public double Sample(double x, double y, double z)
    {
        double sum = 0, f = 1;
        foreach (var o in _octaves)
        {
            sum += o.Noise(x * f, y * f, z * f) / f;
            f /= 2;
        }
        return sum;
    }
}
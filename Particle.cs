using Microsoft.Xna.Framework;

namespace CuboidFever;

public struct Particle
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
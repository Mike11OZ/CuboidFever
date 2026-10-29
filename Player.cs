using System;
using Microsoft.Xna.Framework;

namespace CuboidFever;

public class Player
{
    private const float Width = 0.6f, Height = 1.8f, EyeHeight = 1.62f;
    private const float WalkSpeed = 4.0f, FlySpeed = 24f;
    private const float Gravity = 28f, JumpSpeed = 8.5f, MaxFall = 50f;
    private const float Eps = 0.001f;

    public Vector3 Position;   // center of the feet
    public Vector3 Velocity;
    public bool OnGround;
    public bool Flying;        // fly mode ignores collision

    public Vector3 Eye => Position + new Vector3(0, EyeHeight, 0);

    // 'wish' is the desired movement direction (not necessarily normalized);
    // 'vertical' is -1/0/+1 for fly mode down/none/up.
    public void Update(World world, float dt, Vector3 wish, bool jump, float vertical)
    {
        dt = MathF.Min(dt, 0.05f); // avoid huge steps after a hitch

        if (Flying)
        {
            if (wish != Vector3.Zero) wish.Normalize();
            Position += (wish * FlySpeed + new Vector3(0, vertical * FlySpeed, 0)) * dt;
            Velocity = Vector3.Zero;
            OnGround = false;
            return;
        }

        if (wish != Vector3.Zero) wish.Normalize();
        Velocity.X = wish.X * WalkSpeed;
        Velocity.Z = wish.Z * WalkSpeed;

        Velocity.Y = MathF.Max(Velocity.Y - Gravity * dt, -MaxFall);
        if (jump && OnGround) Velocity.Y = JumpSpeed;

        OnGround = false;
        Move(world, 0, Velocity.X * dt);
        Move(world, 1, Velocity.Y * dt);
        Move(world, 2, Velocity.Z * dt);
    }

    // True if the player's box overlaps the block at (x, y, z). Used to stop you placing blocks inside yourself.
    public bool Overlaps(int x, int y, int z)
    {
        float hw = Width / 2;
        return Position.X + hw > x && Position.X - hw < x + 1
            && Position.Y + Height > y && Position.Y < y + 1
            && Position.Z + hw > z && Position.Z - hw < z + 1;
    }

    private bool Collides(World world)
    {
        float hw = Width / 2;
        int x0 = (int)MathF.Floor(Position.X - hw), x1 = (int)MathF.Floor(Position.X + hw);
        int y0 = (int)MathF.Floor(Position.Y),      y1 = (int)MathF.Floor(Position.Y + Height);
        int z0 = (int)MathF.Floor(Position.Z - hw), z1 = (int)MathF.Floor(Position.Z + hw);

        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
            if (world.IsSolid(x, y, z)) return true;
        return false;
    }

    private float Get(int a) => a == 0 ? Position.X : a == 1 ? Position.Y : Position.Z;
    private void Set(int a, float v)
    {
        if (a == 0) Position.X = v; else if (a == 1) Position.Y = v; else Position.Z = v;
    }

    // Moves along one axis in small sub-steps (so a fast fall can't tunnel through a floor),
    // snapping to the surface of whatever block it hits.
    private void Move(World world, int axis, float delta)
    {
        if (delta == 0) return;
        int steps = (int)MathF.Ceiling(MathF.Abs(delta) / 0.4f);
        float d = delta / steps;
        float hw = Width / 2;
        float upper = axis == 1 ? Height : hw; // distance from position to the + edge
        float lower = axis == 1 ? 0 : hw;      // distance from position to the - edge

        for (int i = 0; i < steps; i++)
        {
            Set(axis, Get(axis) + d);
            if (!Collides(world)) continue;

            if (d > 0)
            {
                int block = (int)MathF.Floor(Get(axis) + upper);
                Set(axis, block - upper - Eps);
            }
            else
            {
                int block = (int)MathF.Floor(Get(axis) - lower);
                Set(axis, block + 1 + lower + Eps);
                if (axis == 1) OnGround = true;
            }

            if (axis == 0) Velocity.X = 0;
            else if (axis == 1) Velocity.Y = 0;
            else Velocity.Z = 0;
            return;
        }
    }
}
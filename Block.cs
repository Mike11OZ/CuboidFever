
using Microsoft.Xna.Framework;

namespace CuboidFever;

public static class Block
{
    public const byte Air = 0, Grass = 1, Stone = 2, Dirt = 3,
        Sand = 4, Water = 5, Bedrock = 6, Planks = 7,
        SmoothStone = 8, Log = 9, Leaves = 10,
        RedLamp = 11, GreenLamp = 12, BlueLamp = 13,
        WhiteLamp = 14, Glass = 15;

    // Light passes through these blocks.
    public static bool IsLightTransparent(byte id) =>
        id == Air || id == Water || id == Leaves || id == Glass;

    public static bool IsEmissive(byte id) =>
        id >= RedLamp && id <= WhiteLamp;

    // Emitted light per channel, 0-15.
    public static (int r, int g, int b) Emission(byte id) => id switch
    {
        RedLamp   => (15, 0, 0),
        GreenLamp => (0, 15, 0),
        BlueLamp  => (0, 0, 15),
        WhiteLamp => (15, 15, 15),
        _         => (0, 0, 0)
    };

    // Color multiplied into the texture (white = no tint).
    public static Color Tint(byte id) => id switch
    {
        RedLamp   => new Color(255, 70, 70),
        GreenLamp => new Color(70, 255, 70),
        BlueLamp  => new Color(90, 90, 255),
        _         => Color.White
    };

    // Tile indices in the atlas (row-major, 8 tiles per row).
    private const int TDirt = 0, TGrassTop = 1, TGrassSide = 2,
        TBedrock = 3, TStone = 4, TSand = 5, TWater = 6,
        TGlass = 7, TPlanks = 8, TSmooth = 9, TLog = 10,
        TLeaves = 11;

    // Face: 0=+X, 1=-X, 2=+Y (top), 3=-Y (bottom), 4=+Z, 5=-Z.
    public static int Tile(byte id, int face)
    {
        switch (id)
        {
            case Grass:
                return face == 2 ? TGrassTop
                    : face == 3 ? TDirt
                    : TGrassSide;

            case Stone:      return TStone;
            case Dirt:       return TDirt;
            case Sand:       return TSand;
            case Water:      return TWater;
            case Bedrock:    return TBedrock;
            case Planks:     return TPlanks;
            case SmoothStone:return TSmooth;
            case Log:        return TLog;
            case Leaves:     return TLeaves;
            case Glass:      return TGlass;

            case RedLamp:
            case GreenLamp:
            case BlueLamp:
            case WhiteLamp:
                return TSmooth;

            default:
                return 0;
        }
    }
}

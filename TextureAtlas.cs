
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CuboidFever;

public sealed class TextureRegion
{
    public int Index { get; }
    public Rectangle PixelBounds { get; }
    public float U0 { get; }
    public float V0 { get; }
    public float U1 { get; }
    public float V1 { get; }

    public TextureRegion(int index, Rectangle bounds, int atlasWidth, int atlasHeight)
    {
        Index = index;
        PixelBounds = bounds;
        U0 = bounds.X / (float)atlasWidth;
        V0 = bounds.Y / (float)atlasHeight;
        U1 = bounds.Right / (float)atlasWidth;
        V1 = bounds.Bottom / (float)atlasHeight;
    }
}

public sealed class TextureAtlas
{
    private sealed class AtlasConfig
    {
        public int TextureSize { get; set; } = 16;
        public int InitialColumns { get; set; } = 2;
        public int InitialRows { get; set; } = 2;
        public int MaxAtlasSize { get; set; } = 4096;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static TextureAtlas Instance { get; private set; }

    public Texture2D Texture { get; }
    public Color[] Pixels { get; }
    public int TextureSize { get; }
    public int Columns { get; }
    public int Rows { get; }
    public int Width => Texture.Width;
    public int Height => Texture.Height;

    private readonly Dictionary<string, TextureRegion> _regions =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize(
        GraphicsDevice device,
        string textureDirectory,
        string configPath)
    {
        Instance = new TextureAtlas(device, textureDirectory, configPath);
    }

    private TextureAtlas(
        GraphicsDevice device,
        string textureDirectory,
        string configPath)
    {
        Console.WriteLine($"Current directory: {Environment.CurrentDirectory}");
        Console.WriteLine($"Base directory: {AppContext.BaseDirectory}");
        Console.WriteLine($"Config path: {configPath}");
        Console.WriteLine($"Full config path: {Path.GetFullPath(configPath)}");
        Console.WriteLine($"Config exists: {File.Exists(configPath)}");

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "Missing texture atlas configuration.",
                configPath);
        }
        if (!Directory.Exists(textureDirectory))
            throw new DirectoryNotFoundException(textureDirectory);

        var config = JsonSerializer.Deserialize<AtlasConfig>(
            File.ReadAllText(configPath), JsonOptions)
            ?? throw new InvalidDataException("Invalid texture_atlas.json.");

        if (config.TextureSize <= 0 ||
            config.InitialColumns <= 0 ||
            config.InitialRows <= 0 ||
            config.MaxAtlasSize <= 0)
        {
            throw new InvalidDataException(
                "Atlas dimensions and textureSize must be positive.");
        }

        TextureSize = config.TextureSize;

        var files = Directory.GetFiles(
                textureDirectory, "*.png", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
            throw new InvalidDataException(
                $"No PNG textures found in '{textureDirectory}'.");

        // Begin at the configured grid size. Grow width, then height,
        // alternating until every texture has a slot.
        int columns = config.InitialColumns;
        int rows = config.InitialRows;
        bool growColumns = true;

        while ((long)columns * rows < files.Length)
        {
            if (growColumns)
                columns = checked(columns * 2);
            else
                rows = checked(rows * 2);

            growColumns = !growColumns;
        }

        int atlasWidth = checked(columns * TextureSize);
        int atlasHeight = checked(rows * TextureSize);

        if (atlasWidth > config.MaxAtlasSize ||
            atlasHeight > config.MaxAtlasSize)
        {
            throw new InvalidDataException(
                $"Generated atlas {atlasWidth}x{atlasHeight} exceeds " +
                $"maxAtlasSize ({config.MaxAtlasSize}).");
        }

        Columns = columns;
        Rows = rows;

        Pixels = new Color[checked(atlasWidth * atlasHeight)];

        for (int index = 0; index < files.Length; index++)
        {
            string file = files[index];

            using var stream = File.OpenRead(file);
            using var source = Texture2D.FromStream(device, stream);

            if (source.Width != TextureSize || source.Height != TextureSize)
            {
                throw new InvalidDataException(
                    $"Texture '{file}' is {source.Width}x{source.Height}; " +
                    $"expected {TextureSize}x{TextureSize}. " +
                    "Resize the image or change textureSize in texture_atlas.json.");
            }

            var sourcePixels = new Color[TextureSize * TextureSize];
            source.GetData(sourcePixels);

            int tileX = index % Columns;
            int tileY = index / Columns;
            int pixelX = tileX * TextureSize;
            int pixelY = tileY * TextureSize;

            for (int y = 0; y < TextureSize; y++)
            {
                Array.Copy(
                    sourcePixels,
                    y * TextureSize,
                    Pixels,
                    (pixelY + y) * atlasWidth + pixelX,
                    TextureSize);
            }

            // A file at blocks/stone.png becomes cuboidfever:stone.
            string relative = Path.GetRelativePath(textureDirectory, file);
            string resourceName = "cuboidfever:" +
                Path.ChangeExtension(relative, null)
                    .Replace('\\', '/')
                    .Replace('\\', '/');

            var bounds = new Rectangle(
                pixelX, pixelY, TextureSize, TextureSize);

            if (!_regions.TryAdd(
                resourceName,
                new TextureRegion(index, bounds, atlasWidth, atlasHeight)))
            {
                throw new InvalidDataException(
                    $"Duplicate texture resource name: {resourceName}");
            }
        }

        Console.WriteLine($"Loaded {_regions.Count} textures.");

        foreach (var name in _regions.Keys.OrderBy(n => n))
        {
            Console.WriteLine($"  {name}");
        }

        Texture = new Texture2D(device, atlasWidth, atlasHeight);
        Texture.SetData(Pixels);
    }

    public TextureRegion GetRegion(string resourceName)
    {
        if (_regions.TryGetValue(resourceName, out var region))
            return region;

        throw new KeyNotFoundException(
            $"Unknown texture resource '{resourceName}'. " +
            "Check the PNG filename and the block JSON texture reference.");
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace CuboidFever;

public sealed class BlockRegistry
{
    public static BlockRegistry Instance { get; private set; }

    private readonly Dictionary<string, byte> _ids =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<byte, BlockDefinition> _definitions = new();

    private readonly Dictionary<string, HashSet<byte>> _tags =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    
    public TextureRegion GetTextureRegion(byte id, int face)
    {
        var definition = Get(id);
        var textures = definition.Textures;

        string[] faceKeys = face switch
        {
            0 => new[] { "east", "side", "all" },
            1 => new[] { "west", "side", "all" },
            2 => new[] { "top", "all" },
            3 => new[] { "bottom", "all" },
            4 => new[] { "south", "side", "all" },
            5 => new[] { "north", "side", "all" },
            _ => throw new ArgumentOutOfRangeException(nameof(face))
        };

        string textureName = null;

        foreach (string key in faceKeys)
        {
            if (textures != null &&
                textures.TryGetValue(key, out string value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                textureName = value;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(textureName))
            textureName = definition.Texture;

        // Preserve the old fallback behavior: atlas tile zero was dirt.
        if (string.IsNullOrWhiteSpace(textureName))
            textureName = "cuboidfever:dirt";

        return TextureAtlas.Instance.GetRegion(textureName);
    }

    // Keep this compatibility method if other code still uses tile indices.
    public int GetTile(byte id, int face) =>
        GetTextureRegion(id, face).Index;

    public byte[] HotbarIds { get; private set; } = Array.Empty<byte>();

    public static void Initialize(string dataRoot)
    {
        Instance = new BlockRegistry(dataRoot);
    }

    private BlockRegistry(string dataRoot)
    {
        string blockDir = Path.Combine(dataRoot, "block");
        string tagDir = Path.Combine(dataRoot, "tags");

        if (!Directory.Exists(blockDir))
            throw new DirectoryNotFoundException(blockDir);

        var definitions = Directory.GetFiles(blockDir, "*.json")
            .Select(file => JsonSerializer.Deserialize<BlockDefinition>(
                File.ReadAllText(file), JsonOptions)
                ?? throw new InvalidDataException($"Invalid block JSON: {file}"))
            .ToList();

        // Air must always be runtime ID 0.
        var air = definitions.SingleOrDefault(d =>
            d.Name.Equals("cuboidfever:air",
                StringComparison.OrdinalIgnoreCase));

        if (air == null)
            throw new InvalidDataException(
                "Missing block definition: cuboidfever:air");

        definitions.Remove(air);
        definitions = definitions
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        definitions.Insert(0, air);

        if (definitions.Count > 256)
            throw new InvalidDataException("Maximum of 256 block IDs exceeded.");

        for (int i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];

            if (string.IsNullOrWhiteSpace(definition.Name))
                throw new InvalidDataException("A block has no name.");

            byte id = (byte)i;

            if (!_ids.TryAdd(definition.Name, id))
                throw new InvalidDataException(
                    $"Duplicate block name: {definition.Name}");

            _definitions.Add(id, definition);
        }

        // Read tag files after all block IDs have been assigned.
        if (Directory.Exists(tagDir))
        {
            foreach (string file in Directory.GetFiles(tagDir, "*.json"))
            {
                string tagName = Path.GetFileNameWithoutExtension(file);
                var tagFile = JsonSerializer.Deserialize<TagFile>(
                    File.ReadAllText(file), JsonOptions)
                    ?? throw new InvalidDataException($"Invalid tag JSON: {file}");

                var members = new HashSet<byte>();

                foreach (string name in tagFile.Values ?? new())
                {
                    members.Add(GetId(name));
                }

                _tags.Add($"cuboidfever:{tagName}", members);
            }
        }

        // Load hotbar entries by name, not by numeric ID.
        string hotbarPath = Path.Combine(dataRoot, "hotbar.json");
        var hotbar = JsonSerializer.Deserialize<HotbarConfig>(
            File.ReadAllText(hotbarPath), JsonOptions)
            ?? throw new InvalidDataException("Invalid hotbar.json");

        HotbarIds = (hotbar.Slots ?? new List<string>())
            .Select(GetId)
            .ToArray();
    }

    public Color GetTint(byte id)
    {
        return Get(id).Tint?.ToXnaColor() ?? Color.White;
    }
    public byte GetId(string name)
    {
        if (_ids.TryGetValue(name, out byte id))
            return id;

        throw new KeyNotFoundException($"Unknown block: {name}");
    }

    public string GetName(byte id) => Get(id).Name;

    public BlockDefinition Get(byte id)
    {
        if (_definitions.TryGetValue(id, out var definition))
            return definition;

        throw new KeyNotFoundException($"Unknown block ID: {id}");
    }

    public bool HasTag(byte id, string tag)
    {
        return _tags.TryGetValue(tag, out var members)
            && members.Contains(id);
    }
}
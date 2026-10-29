using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace CuboidFever;

public class BlockDefinition
{
    public string Name { get; set; } = "";
    public bool Solid { get; set; } = true;
    public bool Transparent { get; set; }
    public bool Replaceable { get; set; }
    public bool Emissive { get; set; }
    public string RenderLayer { get; set; } = "opaque";

    public EmissionData Emission { get; set; } = new();
    public ColorData Tint { get; set; } = new ColorData(255, 255, 255);

    public string Texture { get; set; }
    public Dictionary<string, string> Textures { get; set; } = new();
}

public class EmissionData
{
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
}

[JsonConverter(typeof(ColorDataConverter))]
public class ColorData
{
    public int R { get; set; } = 255;
    public int G { get; set; } = 255;
    public int B { get; set; } = 255;

    public ColorData() { }
    public ColorData(int r, int g, int b) => (R, G, B) = (r, g, b);
    
    public Color ToXnaColor() => new Color(R, G, B);
}

public class ColorDataConverter : JsonConverter<ColorData>
{
    public override ColorData Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            int r = 255, g = 255, b = 255;
            if (reader.Read() && reader.TokenType == JsonTokenType.Number) r = reader.GetInt32();
            if (reader.Read() && reader.TokenType == JsonTokenType.Number) g = reader.GetInt32();
            if (reader.Read() && reader.TokenType == JsonTokenType.Number) b = reader.GetInt32();
            while (reader.TokenType != JsonTokenType.EndArray)
            {
                reader.Read();
            }
            return new ColorData(r, g, b);
        }
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            int r = 255, g = 255, b = 255;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    string propName = reader.GetString();
                    reader.Read();
                    if (propName.Equals("r", StringComparison.OrdinalIgnoreCase)) r = reader.GetInt32();
                    else if (propName.Equals("g", StringComparison.OrdinalIgnoreCase)) g = reader.GetInt32();
                    else if (propName.Equals("b", StringComparison.OrdinalIgnoreCase)) b = reader.GetInt32();
                }
            }
            return new ColorData(r, g, b);
        }
        return new ColorData();
    }

    public override void Write(Utf8JsonWriter writer, ColorData value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("r", value.R);
        writer.WriteNumber("g", value.G);
        writer.WriteNumber("b", value.B);
        writer.WriteEndObject();
    }
}

public class TagFile
{
    public List<string> Values { get; set; }
}

public class HotbarConfig
{
    public List<string> Slots { get; set; }
}
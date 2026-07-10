using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenClipTool.Config;

/// <summary>Durée d'un clip : un nombre de secondes, ou le buffer complet.</summary>
[JsonConverter(typeof(ClipDurationConverter))]
public readonly record struct ClipDuration
{
    public bool IsFull { get; init; }
    public int Seconds { get; init; }

    public static ClipDuration Full => new() { IsFull = true };
    public static ClipDuration FromSeconds(int seconds) => new() { Seconds = seconds };

    public override string ToString() => IsFull ? "full" : Seconds.ToString();
}

/// <summary>Sérialise <c>duration_seconds</c> comme nombre JSON, ou la chaîne "full".</summary>
public sealed class ClipDurationConverter : JsonConverter<ClipDuration>
{
    public override ClipDuration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return ClipDuration.FromSeconds(reader.GetInt32());
            case JsonTokenType.String:
                var s = reader.GetString()!.Trim();
                if (s.Equals("full", StringComparison.OrdinalIgnoreCase))
                    return ClipDuration.Full;
                if (int.TryParse(s, out var v))
                    return ClipDuration.FromSeconds(v);
                throw new JsonException($"duration_seconds invalide : \"{s}\" (attendu : nombre ou \"full\")");
            default:
                throw new JsonException("duration_seconds invalide (attendu : nombre ou \"full\")");
        }
    }

    public override void Write(Utf8JsonWriter writer, ClipDuration value, JsonSerializerOptions options)
    {
        if (value.IsFull) writer.WriteStringValue("full");
        else writer.WriteNumberValue(value.Seconds);
    }
}

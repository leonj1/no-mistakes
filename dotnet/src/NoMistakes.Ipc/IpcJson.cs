using System.Text.Json;
using System.Text.Json.Serialization;
using NoMistakes.Core;

namespace NoMistakes.Ipc;

/// <summary>
/// Shared serializer settings for the IPC wire format. Mirrors Go's
/// encoding/json behavior for the internal/ipc protocol: snake_case field
/// names come from explicit <see cref="JsonPropertyNameAttribute"/> tags on
/// the message types, and nullable fields are omitted when null (Go's
/// pointer-typed omitempty fields).
/// </summary>
public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Serializes a value into a detached JSON element (Go json.RawMessage).</summary>
    public static JsonElement ToElement(object? value) =>
        JsonSerializer.SerializeToElement(value, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? Deserialize<T>(JsonElement element) => element.Deserialize<T>(Options);
}

/// <summary>
/// Normalizes the retired "babysit" step name to "ci" whenever a step name is
/// read from the wire. Mirrors Go's types.StepName.UnmarshalJSON.
/// </summary>
public sealed class StepNameJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        StepName.Normalize(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>Element-wise <see cref="StepNameJsonConverter"/> for step-name lists.</summary>
public sealed class StepNameListJsonConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? new List<string>();
        for (var i = 0; i < raw.Count; i++)
        {
            raw[i] = StepName.Normalize(raw[i]);
        }
        return raw;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImportAssetToOptiDam.Infrastructure.Json;

/// <summary>
/// Reads a JSON object into a list of KVPs preserving the property order from the
/// document. Required for AWS pre-signed POST uploads, where the meta fields must
/// be appended to the multipart body in the same order they were received — and
/// <see cref="Dictionary{TKey, TValue}"/> does not guarantee insertion order across
/// runtimes / configurations.
/// </summary>
public sealed class OrderedStringMapConverter
    : JsonConverter<IReadOnlyList<KeyValuePair<string, string>>>
{
    public override IReadOnlyList<KeyValuePair<string, string>> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                $"Expected StartObject for ordered string map, got {reader.TokenType}.");
        }

        var entries = new List<KeyValuePair<string, string>>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.EndObject:
                    return entries;

                case JsonTokenType.PropertyName:
                    var name = reader.GetString()
                        ?? throw new JsonException("Property name was null.");

                    if (!reader.Read())
                    {
                        throw new JsonException("Unexpected end of stream after property name.");
                    }

                    var value = reader.TokenType switch
                    {
                        JsonTokenType.String => reader.GetString() ?? string.Empty,
                        JsonTokenType.Null   => string.Empty,
                        // Numbers and bools are normalized to their string form. We use
                        // GetDouble().ToString(invariant) rather than GetRawText() so values
                        // like "1.0e2" come through as "100" — GCS POST policies are
                        // signed against decimal strings, not their JSON literal form.
                        // Caveat: GetDouble loses precision above 2^53. Acceptable here
                        // because GCS policy meta fields are strings in practice.
                        JsonTokenType.Number => reader.GetDouble()
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                        JsonTokenType.True   => "true",
                        JsonTokenType.False  => "false",
                        _ => throw new JsonException(
                            $"Unsupported value type {reader.TokenType} for property '{name}'."),
                    };

                    entries.Add(new KeyValuePair<string, string>(name, value));
                    break;

                default:
                    throw new JsonException($"Unexpected token {reader.TokenType}.");
            }
        }

        throw new JsonException("Unterminated JSON object.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyList<KeyValuePair<string, string>> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, val) in value)
        {
            writer.WriteString(name, val);
        }
        writer.WriteEndObject();
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Odx.Client.Json;

/// <summary>
/// Opt-in <see cref="System.Text.Json"/> converter for <see cref="Many2One"/>. Reads
/// Odoo's <c>[id, name]</c> array or <c>false</c>/<c>null</c> (unset); writes the bare
/// integer id (or <c>false</c> when unset), matching Odoo's write semantics — NOT the
/// <c>[id, name]</c> tuple (memory note <c>wire-helpers-placement</c>). Plug it into
/// your own <see cref="JsonSerializerOptions"/>; it is never applied implicitly.
/// </summary>
public sealed class Many2OneConverter : JsonConverter<Many2One>
{
    public override Many2One Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.False:
            case JsonTokenType.Null:
                return Many2One.Unset;

            case JsonTokenType.Number:
                // A bare id (Odoo occasionally returns this, e.g. from read with a plain field).
                return new Many2One(reader.GetInt64());

            case JsonTokenType.StartArray:
            {
                reader.Read();
                long id = reader.TokenType == JsonTokenType.Number ? reader.GetInt64() : 0;

                string? name = null;
                if (reader.TokenType != JsonTokenType.EndArray)
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        name = reader.GetString();
                }

                // Consume any trailing elements + the closing bracket.
                while (reader.TokenType != JsonTokenType.EndArray)
                    reader.Read();

                return new Many2One(id, name);
            }

            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for Many2One.");
        }
    }

    public override void Write(Utf8JsonWriter writer, Many2One value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteNumberValue(value.Id);
        else
            writer.WriteBooleanValue(false);
    }
}

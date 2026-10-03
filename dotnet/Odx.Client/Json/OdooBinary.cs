using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraKernel.OdxClient.Json;

/// <summary>
/// An Odoo binary field value. Odoo 20+ reads binary fields as
/// <c>{"content": "&lt;base64&gt;", "filename": ..., "size": n}</c> (over v1 and v2 alike);
/// Odoo 19 and older return a bare base64 string. An empty field is still <c>false</c>.
/// This is a wire-protocol primitive, not a domain model — see <see cref="OdooBinaryConverter"/>.
/// </summary>
public sealed class OdooBinary
{
    /// <summary>The file content, base64-encoded (as Odoo sends it).</summary>
    public required string Content { get; init; }

    /// <summary>The file name, when Odoo provides one.</summary>
    public string? Filename { get; init; }

    /// <summary>The size in bytes, when Odoo provides it (Odoo 20+).</summary>
    public long? Size { get; init; }

    /// <summary>Decode <see cref="Content"/> to bytes.</summary>
    public byte[] GetBytes() => Convert.FromBase64String(Content);
}

/// <summary>
/// Opt-in converter for <see cref="OdooBinary"/>. Reads the Odoo 20+ object, a bare base64
/// string (Odoo 19 and older), or <c>false</c>/<c>null</c> (empty field → <see langword="null"/>).
/// Writes a bare base64 string, or <c>{"content", "filename"}</c> when a file name is set —
/// the two shapes Odoo accepts on write. Plug it into your own <see cref="JsonSerializerOptions"/>
/// or source-generation context; it is never applied implicitly.
/// </summary>
public sealed class OdooBinaryConverter : JsonConverter<OdooBinary?>
{
    public override bool HandleNull => true;

    public override OdooBinary? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.False:
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return new OdooBinary { Content = reader.GetString()! };

            case JsonTokenType.StartObject:
            {
                string? content = null;
                string? filename = null;
                long? size = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.ValueTextEquals("content"u8))
                    {
                        reader.Read();
                        content = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    }
                    else if (reader.ValueTextEquals("filename"u8))
                    {
                        reader.Read();
                        filename = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    }
                    else if (reader.ValueTextEquals("size"u8))
                    {
                        reader.Read();
                        size = reader.TokenType == JsonTokenType.Number ? reader.GetInt64() : null;
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }
                return content is null ? null : new OdooBinary { Content = content, Filename = filename, Size = size };
            }

            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for an Odoo binary field.");
        }
    }

    public override void Write(Utf8JsonWriter writer, OdooBinary? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteBooleanValue(false); // Odoo clears a binary field with false
            return;
        }
        if (value.Filename is null)
        {
            writer.WriteStringValue(value.Content);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("content"u8, value.Content);
        writer.WriteString("filename"u8, value.Filename);
        writer.WriteEndObject();
    }
}

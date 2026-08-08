using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraKernel.OdxClient.Json;

/// <summary>
/// Opt-in converter that reads Odoo's <c>false</c> (an unset scalar) as
/// <see langword="null"/>. Odoo returns <c>false</c> for empty char/text/date fields;
/// apply this to a <c>string?</c> property (via attribute or
/// <see cref="JsonSerializerOptions"/>) to get <see langword="null"/> instead of a
/// deserialization error. Write side emits the string, or JSON <c>null</c> when null.
/// </summary>
public sealed class OdooFalseAsNullStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.False or JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for Odoo false-as-null string."),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}

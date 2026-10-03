using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraKernel.OdxClient;

/// <summary>
/// A JSON value passed to a v2 call (a domain, <c>vals</c>, a <c>context</c>, <c>kwargs</c>):
/// either a <see cref="JsonNode"/> (convenient: <c>new JsonObject { ["name"] = "Acme" }</c>)
/// or pre-serialized UTF-8 bytes (fastest: spliced into the request verbatim, never
/// re-parsed into a tree). Both convert implicitly, so one parameter takes either.
/// <c>default</c> means "not set": the argument is left out of the request.
/// </summary>
/// <remarks>
/// Byte input must stay valid until the call's task completes. Keys inside the value
/// are Odoo names and are sent exactly as given — never case-converted.
/// </remarks>
public readonly struct OdxJson
{
    private readonly JsonNode? _node;
    private readonly ReadOnlyMemory<byte> _utf8;

    /// <summary>Wrap a <see cref="JsonNode"/>. A <see langword="null"/> node means "not set".</summary>
    public OdxJson(JsonNode? node)
    {
        _node = node;
        _utf8 = default;
    }

    /// <summary>Wrap UTF-8 JSON bytes. Empty means "not set".</summary>
    public OdxJson(ReadOnlyMemory<byte> utf8)
    {
        _node = null;
        _utf8 = utf8;
    }

    /// <summary>True when no value was given (the argument is omitted from the request).</summary>
    public bool IsEmpty => _node is null && _utf8.IsEmpty;

    /// <summary>Wrap JSON text, e.g. <c>OdxJson.Parse("""[["is_company","=",true]]""")</c>.</summary>
    public static OdxJson Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return new OdxJson(Encoding.UTF8.GetBytes(json));
    }

    public static implicit operator OdxJson(JsonNode? node) => new(node);
    public static implicit operator OdxJson(byte[]? utf8) => new(utf8 is null ? default : utf8.AsMemory());
    public static implicit operator OdxJson(ReadOnlyMemory<byte> utf8) => new(utf8);

    /// <summary>The kind of the top-level token (Object / Array / …), validating nothing else.</summary>
    internal JsonValueKind Kind
    {
        get
        {
            if (_node is not null)
                return _node.GetValueKind();
            if (_utf8.IsEmpty)
                return JsonValueKind.Undefined;
            var reader = new Utf8JsonReader(_utf8.Span);
            if (!reader.Read())
                return JsonValueKind.Undefined;
            return reader.TokenType switch
            {
                JsonTokenType.StartObject => JsonValueKind.Object,
                JsonTokenType.StartArray => JsonValueKind.Array,
                JsonTokenType.String => JsonValueKind.String,
                JsonTokenType.Number => JsonValueKind.Number,
                JsonTokenType.True => JsonValueKind.True,
                JsonTokenType.False => JsonValueKind.False,
                JsonTokenType.Null => JsonValueKind.Null,
                _ => JsonValueKind.Undefined,
            };
        }
    }

    /// <summary>Write the value. Bytes are validated by the writer, never re-parsed into a tree.</summary>
    internal void WriteTo(Utf8JsonWriter w)
    {
        if (_node is not null)
            _node.WriteTo(w);
        else
            w.WriteRawValue(_utf8.Span, skipInputValidation: false);
    }

    /// <summary>
    /// Split a JSON object into its top-level properties without copying values: byte
    /// input yields slices of the original buffer, node input the child nodes.
    /// </summary>
    internal List<(string Name, OdxJson Value)> GetProperties(string paramName)
    {
        var props = new List<(string, OdxJson)>();
        if (_node is not null)
        {
            if (_node is not JsonObject obj)
                throw new ArgumentException("Expected a JSON object.", paramName);
            foreach (var (name, value) in obj)
                props.Add((name, value is null ? new OdxJson(JsonNullUtf8) : new OdxJson(value)));
            return props;
        }

        if (_utf8.IsEmpty)
            return props;

        var reader = new Utf8JsonReader(_utf8.Span);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new ArgumentException("Expected a JSON object.", paramName);

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString()!;
            reader.Read();
            int start = (int)reader.TokenStartIndex;
            reader.Skip(); // validates the value; lands on its last token
            int end = (int)reader.BytesConsumed;
            props.Add((name, new OdxJson(_utf8[start..end])));
        }
        return props;
    }

    /// <summary>Serialize to a standalone byte array (used to snapshot session-level values).</summary>
    internal byte[] ToUtf8Array()
    {
        if (_node is null)
            return _utf8.ToArray();
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
            _node.WriteTo(w);
        return buffer.WrittenSpan.ToArray();
    }

    private static readonly byte[] JsonNullUtf8 = "null"u8.ToArray();
}

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraKernel.OdxClient.Interop;

namespace TerraKernel.OdxClient;

/// <summary>
/// Parses odxproxy responses off the caller's thread. Reflection-free
/// (<see cref="Utf8JsonReader"/> + a caller-supplied <see cref="JsonTypeInfo{T}"/>),
/// so it is AOT/trim-safe (spec constraint #7). Handles the HTTP-200-with-error trap:
/// a 2xx status can still carry an Odoo <c>error</c> object (IMPLEMENTATION-PLAN.md §3.2).
/// </summary>
internal static class Envelope
{
    /// <summary>
    /// Read a JSON-RPC envelope: throw the mapped exception if it carries an
    /// <c>error</c>, otherwise deserialize <c>result</c> into <typeparamref name="T"/>.
    /// </summary>
    public static T? ReadResult<T>(ReadOnlySpan<byte> json, OdxStatus status, ushort http, JsonTypeInfo<T> resultType)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw MapError(status, http, null, "malformed response envelope (expected a JSON object)", null);

        bool hasResult = false;
        T? result = default;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Skip();
                continue;
            }

            if (reader.ValueTextEquals("error"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    (long code, string? message, string? data) = ReadError(ref reader);
                    throw MapError(status, http, code, message, data);
                }
                // error: null / false -> not actually an error
            }
            else if (reader.ValueTextEquals("result"u8))
            {
                reader.Read();
                result = reader.TokenType == JsonTokenType.Null
                    ? default
                    : JsonSerializer.Deserialize(ref reader, resultType);
                hasResult = true;
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (!hasResult)
            throw MapError(status, http, null, "response had neither a result nor an error", null);

        return result;
    }

    /// <summary>Deserialize a flat (non-envelope) body — <c>/_/license</c>, <c>/_/about</c>.</summary>
    public static T? ReadFlat<T>(ReadOnlySpan<byte> json, OdxStatus status, ushort http, JsonTypeInfo<T> type)
    {
        if (status != OdxStatus.Ok)
            throw MapError(status, http, null, null, null);
        var reader = new Utf8JsonReader(json);
        return JsonSerializer.Deserialize(ref reader, type);
    }

    private static (long code, string? message, string? data) ReadError(ref Utf8JsonReader reader)
    {
        long code = 0;
        string? message = null;
        string? data = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Skip();
                continue;
            }

            if (reader.ValueTextEquals("code"u8))
            {
                reader.Read();
                code = reader.TokenType == JsonTokenType.Number ? reader.GetInt64() : 0;
            }
            else if (reader.ValueTextEquals("message"u8))
            {
                reader.Read();
                message = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else if (reader.ValueTextEquals("data"u8))
            {
                reader.Read();
                using var doc = JsonDocument.ParseValue(ref reader);
                data = doc.RootElement.GetRawText();
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return (code, message, data);
    }

    /// <summary>Map a status + optional JSON-RPC error into the typed exception.</summary>
    public static OdxException MapError(OdxStatus status, ushort http, long? rpcCode, string? message, string? data)
    {
        _ = http; // reserved for richer diagnostics later
        return status switch
        {
            // HTTP 2xx + an error body => Odoo-side logic error.
            OdxStatus.Ok => new OdxOdooException(rpcCode ?? 0, message, data),

            OdxStatus.Unauthorized => new OdxAuthException(status, message, rpcCode, data),
            OdxStatus.BadRequest => new OdxBadRequestException(status, message, rpcCode, data),
            OdxStatus.Forbidden => new OdxLicenseException(status, message, rpcCode, data),
            OdxStatus.UpstreamTimeout => new OdxUpstreamTimeoutException(status, message, rpcCode, data),
            OdxStatus.UpstreamConnect => new OdxUpstreamConnectException(status, message, rpcCode, data),
            OdxStatus.ProxyInternal => new OdxProxyInternalException(status, message, rpcCode, data),

            OdxStatus.LocalTimeout or OdxStatus.ConnectError or OdxStatus.TransportError
                => new OdxTransportException(status, message ?? status.ToString()),

            _ => new OdxServerException(status, message, rpcCode, data),
        };
    }
}

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
    public static T? ReadResult<T>(ReadOnlySpan<byte> json, OdxStatus status, ushort http, JsonTypeInfo<T> resultType, bool brain = false)
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
                    throw brain
                        ? MapBrainError(status, http, code, message, data)
                        : MapError(status, http, code, message, data);
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

    /// <summary>
    /// Read a <c>/brain/v1/*</c> response (SYSTEM_ARCHITECTURE.md §7.2). Non-envelope answers are
    /// handled before any JSON parse: a bare 404 (proxy older than 0.10.0) is "not installed", and
    /// the proxy's plain-text 400/422 (malformed body) is a request error carrying the text.
    /// </summary>
    public static T? ReadBrainResult<T>(ReadOnlySpan<byte> body, OdxStatus status, ushort http, JsonTypeInfo<T> resultType)
    {
        if (http == 404)
            throw new OdxBrainNotInstalledException(status, "ODXBrain is not supported by this proxy (HTTP 404; proxy older than 0.10.0)", null, null);
        if (http is 400 or 422)
            throw new OdxBrainRequestException(status, PlainText(body, http), null, null);
        if (body.Length == 0)
            throw http == 501
                ? new OdxBrainNotInstalledException(status, "ODXBrain is not installed on this proxy (HTTP 501)", null, null)
                : MapError(status, http, null, null, null);
        if (!LooksLikeJsonObject(body))
            throw http == 501
                ? new OdxBrainNotInstalledException(status, PlainText(body, http), null, null)
                : MapError(status, http, null, PlainText(body, http), null);
        return ReadResult(body, status, http, resultType, brain: true);
    }

    /// <summary>
    /// Map a <c>/brain/*</c> error. Proxy codes (negative, and <c>0</c> on 403) keep their proxy
    /// meaning; on HTTP 200 the code is <b>ODXBrain's</b> HTTP status, never Odoo's.
    /// </summary>
    public static OdxException MapBrainError(OdxStatus status, ushort http, long? rpcCode, string? message, string? data)
    {
        if (rpcCode == -32008 || http == 501)
            return new OdxBrainNotInstalledException(status, message, rpcCode, data);
        if (rpcCode == -32011)
            return new OdxBrainConfigException(status, message, rpcCode, data);
        if (rpcCode == -32009)
            return new OdxBrainUnavailableException(status, message, rpcCode, data, isRetryable: true);
        if (rpcCode == -32010)
            return new OdxBrainTimeoutException(status, message, rpcCode, data);
        return status switch
        {
            OdxStatus.Ok => rpcCode switch
            {
                400 => new OdxBrainRequestException(status, message, rpcCode, data),
                503 => new OdxBrainUnavailableException(status, message, rpcCode, data, isRetryable: false),
                _ => new OdxBrainException(status, message, rpcCode, data),
            },
            // An unexpected 502/504 on a brain route is still the brain hop.
            OdxStatus.UpstreamConnect => new OdxBrainUnavailableException(status, message, rpcCode, data, isRetryable: true),
            OdxStatus.UpstreamTimeout => new OdxBrainTimeoutException(status, message, rpcCode, data),
            _ => MapError(status, http, rpcCode, message, data), // 401 auth, 403 license, transport, ...
        };
    }

    private static bool LooksLikeJsonObject(ReadOnlySpan<byte> body)
    {
        foreach (byte b in body)
        {
            if (b is 0x20 or 0x09 or 0x0D or 0x0A) // JSON whitespace
                continue;
            return b == (byte)'{';
        }
        return false;
    }

    private static string PlainText(ReadOnlySpan<byte> body, ushort http)
    {
        const int Max = 512;
        string text = System.Text.Encoding.UTF8.GetString(body.Length > Max ? body[..Max] : body).Trim();
        return text.Length > 0 ? $"{text} (HTTP {http})" : $"HTTP {http}";
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
            // HTTP 2xx + an error body => Odoo-side logic error (code 0 included: Odoo 19+
            // /jsonrpc errors carry code 0; only a 403 is a license error). -32006 is the
            // one proxy code that arrives on a 200.
            OdxStatus.Ok => rpcCode == -32006
                ? new OdxJson2UnavailableException(status, message, rpcCode, data)
                : MapOdooError(rpcCode ?? 0, message, data),

            OdxStatus.Unauthorized => new OdxAuthException(status, message, rpcCode, data),
            OdxStatus.BadRequest => rpcCode == -32007
                ? new OdxInvalidRequestException(status, message, rpcCode, data)
                : new OdxBadRequestException(status, message, rpcCode, data),
            OdxStatus.Forbidden => new OdxLicenseException(status, message, rpcCode, data),
            OdxStatus.UpstreamTimeout => new OdxUpstreamTimeoutException(status, message, rpcCode, data),
            OdxStatus.UpstreamConnect => new OdxUpstreamConnectException(status, message, rpcCode, data),
            OdxStatus.ProxyInternal => new OdxProxyInternalException(status, message, rpcCode, data),

            OdxStatus.LocalTimeout or OdxStatus.ConnectError or OdxStatus.TransportError
                => new OdxTransportException(status, message ?? status.ToString()),

            _ => new OdxServerException(status, message, rpcCode, data),
        };
    }

    // Codes 100-599 on a 200 are Odoo HTTP statuses (always on v2); anything else is
    // Odoo's own JSON-RPC code. All map to OdxOdooException or a subclass.
    private static OdxOdooException MapOdooError(long code, string? message, string? data) => code switch
    {
        401 => new OdxOdooAuthException(code, message, data),
        403 => new OdxOdooAccessException(code, message, data),
        404 => new OdxOdooNotFoundException(code, message, data),
        409 => new OdxOdooConflictException(code, message, data),
        422 => new OdxOdooValidationException(code, message, data),
        >= 500 and <= 599 => new OdxOdooServerException(code, message, data),
        _ => new OdxOdooException(code, message, data),
    };
}

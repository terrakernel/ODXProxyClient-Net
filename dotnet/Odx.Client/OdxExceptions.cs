using System.Text.Json;
using TerraKernel.OdxClient.Interop;

namespace TerraKernel.OdxClient;

/// <summary>Proxy authentication failed (HTTP 401 / <c>-32000</c>).</summary>
public sealed class OdxAuthException : OdxException
{
    public OdxAuthException(OdxStatus status, string? message, long? rpcCode, string? rpcData)
        : base(status, message, rpcCode, rpcData) { }
}

/// <summary>
/// The proxy rejected the request (HTTP 400): v1 invalid action / missing <c>fn_name</c>
/// (<c>-32001</c>/<c>-32002</c>); v2's <c>-32007</c> is the <see cref="OdxInvalidRequestException"/> subclass.
/// </summary>
public class OdxBadRequestException : OdxException
{
    public OdxBadRequestException(OdxStatus status, string? message, long? rpcCode, string? rpcData)
        : base(status, message, rpcCode, rpcData) { }
}

/// <summary>License invalid (HTTP 403 / code <c>0</c>).</summary>
public sealed class OdxLicenseException : OdxException
{
    public OdxLicenseException(OdxStatus status, string? message, long? rpcCode, string? rpcData)
        : base(status, message, rpcCode, rpcData) { }
}

/// <summary>Upstream Odoo timed out (HTTP 504 / <c>-32003</c>).</summary>
public sealed class OdxUpstreamTimeoutException : OdxException
{
    public OdxUpstreamTimeoutException(OdxStatus status, string? message, long? rpcCode, string? rpcData)
        : base(status, message, rpcCode, rpcData) { }
}

/// <summary>Proxy could not reach Odoo (HTTP 502 / <c>-32004</c>).</summary>
public sealed class OdxUpstreamConnectException : OdxException
{
    public OdxUpstreamConnectException(OdxStatus status, string? message, long? rpcCode, string? rpcData)
        : base(status, message, rpcCode, rpcData) { }
}

/// <summary>Proxy internal error (HTTP 500 / <c>-32005</c>).</summary>
public sealed class OdxProxyInternalException : OdxException
{
    public OdxProxyInternalException(OdxStatus status, string? message, long? rpcCode, string? rpcData)
        : base(status, message, rpcCode, rpcData) { }
}

/// <summary>Any other non-2xx server error.</summary>
public sealed class OdxServerException : OdxException
{
    public OdxServerException(OdxStatus status, string? message, long? rpcCode, string? rpcData)
        : base(status, message, rpcCode, rpcData) { }
}

/// <summary>
/// A transport-level failure before any HTTP response arrived (local timeout,
/// connection failure, or other reqwest error). No body is available.
/// </summary>
public sealed class OdxTransportException : OdxException
{
    public OdxTransportException(OdxStatus status, string? message)
        : base(status, message) { }
}

/// <summary>
/// An Odoo-side logic error: the proxy returned HTTP 200 with an <c>error</c> object
/// in the body (IMPLEMENTATION-PLAN.md §3.2). <see cref="OdooCode"/> is Odoo's own
/// error code, distinct from the proxy's <c>-3200x</c> codes. The raw <c>error.data</c>
/// is always kept in <see cref="OdxException.RpcData"/>.
/// </summary>
/// <remarks>
/// When the code is an Odoo HTTP status (always on v2; on v1 only when Odoo itself
/// answered non-2xx) a subclass is thrown: <see cref="OdxOdooAuthException"/> (401),
/// <see cref="OdxOdooAccessException"/> (403), <see cref="OdxOdooNotFoundException"/> (404),
/// <see cref="OdxOdooConflictException"/> (409), <see cref="OdxOdooValidationException"/>
/// (422), <see cref="OdxOdooServerException"/> (5xx). <c>catch (OdxOdooException)</c> still
/// catches all of them. Code <c>0</c> on HTTP 200 (every Odoo 19+ <c>/jsonrpc</c> error) is
/// an Odoo error, never a license error — that is HTTP 403 only.
/// </remarks>
public class OdxOdooException : OdxException
{
    private string? _odooErrorName;
    private bool _odooErrorNameRead;

    public long OdooCode => RpcCode ?? 0;

    /// <summary>
    /// Odoo's exception class from <c>error.data.name</c> (e.g.
    /// <c>odoo.exceptions.ValidationError</c>), when present. Branch on this for finer
    /// handling; never parse <c>data.debug</c>.
    /// </summary>
    public string? OdooErrorName
    {
        get
        {
            if (!_odooErrorNameRead)
            {
                _odooErrorName = ReadDataName(RpcData);
                _odooErrorNameRead = true;
            }
            return _odooErrorName;
        }
    }

    public OdxOdooException(long code, string? message, string? data)
        : base(OdxStatus.Ok, message, code, data) { }

    private static string? ReadDataName(string? data)
    {
        if (string.IsNullOrEmpty(data))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("name"u8, out JsonElement name)
                && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

// ---- v2 (JSON-2) additions ----

/// <summary>
/// v2: the proxy found no JSON-2 on that Odoo (HTTP 200 / <c>-32006</c>). Either the server
/// is Odoo 18 or older (use v1), or the database is not selectable on that host
/// (<c>dbfilter</c>: <c>url</c> must be the database's own hostname). Not retryable.
/// </summary>
public sealed class OdxJson2UnavailableException : OdxException
{
    public OdxJson2UnavailableException(OdxStatus status, string? message, long? rpcCode, string? rpcData)
        : base(status, message, rpcCode, rpcData) { }
}

/// <summary>
/// v2: the proxy rejected the request before contacting Odoo (HTTP 400 / <c>-32007</c>):
/// <c>model_id</c>/<c>method</c> is not an Odoo identifier, or <c>db</c>/<c>api_key</c> is
/// not a valid HTTP header value. Not retryable.
/// </summary>
public sealed class OdxInvalidRequestException : OdxBadRequestException
{
    public OdxInvalidRequestException(OdxStatus status, string? message, long? rpcCode, string? rpcData)
        : base(status, message, rpcCode, rpcData) { }
}

/// <summary>
/// Odoo rejected the Odoo API key (code <c>401</c> on HTTP 200): invalid, expired (non-admin
/// keys expire on a schedule), wrong scope (must be <c>rpc</c> on Odoo 20+), or a password
/// was used. Distinct from <see cref="OdxAuthException"/>, which is the <b>proxy</b> key.
/// </summary>
public sealed class OdxOdooAuthException : OdxOdooException
{
    public OdxOdooAuthException(long code, string? message, string? data) : base(code, message, data) { }
}

/// <summary>Access rights, or a private (<c>_</c>-prefixed) method (code <c>403</c> on HTTP 200).</summary>
public sealed class OdxOdooAccessException : OdxOdooException
{
    public OdxOdooAccessException(long code, string? message, string? data) : base(code, message, data) { }
}

/// <summary>Unknown model/method, or the record does not exist (code <c>404</c> on HTTP 200).</summary>
public sealed class OdxOdooNotFoundException : OdxOdooException
{
    public OdxOdooNotFoundException(long code, string? message, string? data) : base(code, message, data) { }
}

/// <summary>Odoo lock conflict (code <c>409</c> on HTTP 200). Safe to retry with backoff.</summary>
public sealed class OdxOdooConflictException : OdxOdooException
{
    public OdxOdooConflictException(long code, string? message, string? data) : base(code, message, data) { }
}

/// <summary>
/// Validation/user error, or bad arguments — an unknown kwarg name, or <c>ids</c> sent to an
/// <c>@api.model</c> method (code <c>422</c> on HTTP 200).
/// </summary>
public sealed class OdxOdooValidationException : OdxOdooException
{
    public OdxOdooValidationException(long code, string? message, string? data) : base(code, message, data) { }
}

/// <summary>Odoo server error (code <c>500</c>–<c>599</c> on HTTP 200).</summary>
public sealed class OdxOdooServerException : OdxOdooException
{
    public OdxOdooServerException(long code, string? message, string? data) : base(code, message, data) { }
}

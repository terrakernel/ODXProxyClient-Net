using TerraKernel.OdxClient.Interop;

namespace TerraKernel.OdxClient;

/// <summary>Proxy authentication failed (HTTP 401 / <c>-32000</c>).</summary>
public sealed class OdxAuthException : OdxException
{
    public OdxAuthException(OdxStatus status, string? message, long? rpcCode, string? rpcData)
        : base(status, message, rpcCode, rpcData) { }
}

/// <summary>Invalid action or missing <c>fn_name</c> (HTTP 400 / <c>-32001</c>/<c>-32002</c>).</summary>
public sealed class OdxBadRequestException : OdxException
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
/// error code, distinct from the proxy's <c>-3200x</c> codes.
/// </summary>
public sealed class OdxOdooException : OdxException
{
    public long OdooCode => RpcCode ?? 0;

    public OdxOdooException(long code, string? message, string? data)
        : base(OdxStatus.Ok, message, code, data) { }
}

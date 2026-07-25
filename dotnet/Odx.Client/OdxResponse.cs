using Odx.Client.Interop;

namespace Odx.Client;

/// <summary>
/// A completed RPC response. <see cref="Body"/> is the raw response bytes (the
/// JSON-RPC envelope for the <c>execute</c>/<c>version</c> endpoints, or the flat
/// body for <c>license</c>/<c>about</c>/<c>metrics</c>). The bytes are copied off
/// the tokio worker thread, in the awaiting continuation, so no network or buffer
/// work ever touches the caller's thread.
/// </summary>
/// <remarks>
/// <see cref="Status"/> == <see cref="OdxStatus.Ok"/> means HTTP 2xx — it does NOT
/// by itself mean the RPC succeeded, because the proxy returns Odoo logic errors on
/// HTTP 200 with an <c>error</c> object in <see cref="Body"/> (IMPLEMENTATION-PLAN.md
/// §3.2). The typed <c>...Async&lt;T&gt;</c> layer will surface those as exceptions.
/// </remarks>
public sealed class OdxResponse
{
    public OdxStatus Status { get; }
    public ushort HttpStatus { get; }
    public byte[] Body { get; }

    internal OdxResponse(OdxStatus status, ushort httpStatus, byte[] body)
    {
        Status = status;
        HttpStatus = httpStatus;
        Body = body;
    }
}

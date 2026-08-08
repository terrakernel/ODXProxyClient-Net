namespace TerraKernel.OdxClient.Interop;

/// <summary>
/// Transport/proxy outcome category reported across the FFI boundary. Mirrors the
/// Rust <c>OdxStatus</c> (repr(i32)) — values MUST stay in sync with
/// <c>src/status.rs</c>. This is deliberately coarse: it is derived without parsing
/// the JSON body. The fine-grained JSON-RPC <c>error.code</c> inside the body is a
/// separate axis, read on the .NET side. See IMPLEMENTATION-PLAN.md §3.
/// </summary>
public enum OdxStatus
{
    Ok = 0,

    // submit-time (returned directly by the entry points, never via callback)
    InvalidHandle = 1,
    InvalidArgument = 2,
    InvalidConfig = 3,
    RuntimeUnavailable = 4,

    // transport (no usable HTTP response was received)
    LocalTimeout = 10,
    ConnectError = 11,
    TransportError = 12,
    Cancelled = 13,

    // proxy / HTTP categories (mapped from the proxy's HTTP status)
    Unauthorized = 20,    // 401 -> proxy error.code -32000
    BadRequest = 21,      // 400 -> -32001 invalid action / -32002 missing fn_name
    Forbidden = 22,       // 403 -> license invalid, error.code 0
    UpstreamTimeout = 23, // 504 -> -32003
    UpstreamConnect = 24, // 502 -> -32004
    ProxyInternal = 25,   // 500 -> -32005
    ServerError = 26,     // any other non-2xx
}

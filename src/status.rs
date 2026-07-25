//! `OdxStatus` — the transport/proxy outcome category reported across the FFI
//! boundary. This is deliberately coarse and is derived WITHOUT parsing the JSON
//! body: from the reqwest outcome or the proxy's HTTP status only. The
//! fine-grained JSON-RPC `error.code` inside the body is a separate axis read on
//! the .NET side. See IMPLEMENTATION-PLAN.md §3.

/// Repr matches the C ABI (`int32_t`). Values are stable — the .NET mirror and
/// any generated header depend on them.
#[repr(i32)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum OdxStatus {
    Ok = 0,

    // submit-time (returned directly by the odx_* functions, never via callback)
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
    Unauthorized = 20,   // 401 -> proxy error.code -32000
    BadRequest = 21,     // 400 -> -32001 invalid action / -32002 missing fn_name
    Forbidden = 22,      // 403 -> license invalid, error.code 0
    UpstreamTimeout = 23, // 504 -> -32003
    UpstreamConnect = 24, // 502 -> -32004
    ProxyInternal = 25,  // 500 -> -32005
    ServerError = 26,    // any other non-2xx
}

impl OdxStatus {
    /// Map an HTTP status to a category. NOTE: `Ok` (2xx) does not imply success —
    /// the proxy returns Odoo *logic* errors on HTTP 200 with an `error` object in
    /// the body, which the .NET layer detects (IMPLEMENTATION-PLAN.md §3.2).
    pub fn from_http(status: u16) -> Self {
        match status {
            200..=299 => OdxStatus::Ok,
            400 => OdxStatus::BadRequest,
            401 => OdxStatus::Unauthorized,
            403 => OdxStatus::Forbidden,
            500 => OdxStatus::ProxyInternal,
            502 => OdxStatus::UpstreamConnect,
            504 => OdxStatus::UpstreamTimeout,
            _ => OdxStatus::ServerError,
        }
    }

    /// Classify a transport-level failure (no HTTP response arrived).
    pub fn from_reqwest_error(e: &reqwest::Error) -> Self {
        if e.is_timeout() {
            OdxStatus::LocalTimeout
        } else if e.is_connect() {
            OdxStatus::ConnectError
        } else {
            OdxStatus::TransportError
        }
    }
}

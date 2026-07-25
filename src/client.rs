//! Client handle: owns one `reqwest::Client` (a keep-alive connection pool to the
//! proxy) plus the proxy base URL, API key, and default timeout. See
//! IMPLEMENTATION-PLAN.md §4.1.

use std::time::Duration;

use reqwest::header::HeaderValue;

use crate::status::OdxStatus;

/// Blittable construction config passed from .NET. All pointer/len pairs are
/// borrowed only for the duration of `odx_client_create`.
#[repr(C)]
pub struct OdxClientConfig {
    pub base_url_ptr: *const u8,
    pub base_url_len: usize,
    pub api_key_ptr: *const u8,
    pub api_key_len: usize,
    /// Sent as `x-request-timeout`; 0 = omit the header.
    pub default_timeout_secs: u32,
    /// reqwest connect timeout; 0 = reqwest default.
    pub connect_timeout_ms: u32,
    /// 0 = reqwest default.
    pub pool_max_idle_per_host: u32,
}

/// The opaque client the .NET side holds as `OdxClient*`.
pub struct ClientInner {
    pub http: reqwest::Client,
    pub base_url: String, // trimmed, no trailing slash
    pub api_key: HeaderValue,
    pub default_timeout_secs: u32,
}

/// Create a client. On success writes an owned handle to `*out_client`; free it
/// with `odx_client_free`. On failure returns a non-Ok status and sets the
/// thread-local last error (retrieve with `odx_last_error`).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odx_client_create(
    cfg: *const OdxClientConfig,
    out_client: *mut *mut ClientInner,
) -> OdxStatus {
    crate::ffi_guard(OdxStatus::ProxyInternal, || {
        if cfg.is_null() || out_client.is_null() {
            return OdxStatus::InvalidArgument;
        }
        let cfg = unsafe { &*cfg };

        let base_url = match unsafe { crate::str_opt(cfg.base_url_ptr, cfg.base_url_len) } {
            Some(s) if !s.is_empty() => s.trim_end_matches('/').to_owned(),
            _ => {
                crate::set_last_error("odx_client_create: invalid or empty base_url");
                return OdxStatus::InvalidConfig;
            }
        };

        let api_key_bytes = match unsafe { crate::slice_opt(cfg.api_key_ptr, cfg.api_key_len) } {
            Some(b) if !b.is_empty() => b,
            _ => {
                crate::set_last_error("odx_client_create: invalid or empty api_key");
                return OdxStatus::InvalidConfig;
            }
        };
        let mut api_key = match HeaderValue::from_bytes(api_key_bytes) {
            Ok(v) => v,
            Err(_) => {
                crate::set_last_error("odx_client_create: api_key is not a valid HTTP header value");
                return OdxStatus::InvalidConfig;
            }
        };
        api_key.set_sensitive(true);

        // Ensure the global runtime exists before we hand back a client.
        if crate::runtime().is_none() {
            crate::set_last_error("odx_client_create: failed to initialize tokio runtime");
            return OdxStatus::RuntimeUnavailable;
        }

        let mut builder = reqwest::Client::builder().use_rustls_tls();
        if cfg.connect_timeout_ms > 0 {
            builder = builder.connect_timeout(Duration::from_millis(cfg.connect_timeout_ms as u64));
        }
        if cfg.pool_max_idle_per_host > 0 {
            builder = builder.pool_max_idle_per_host(cfg.pool_max_idle_per_host as usize);
        }
        let http = match builder.build() {
            Ok(c) => c,
            Err(e) => {
                crate::set_last_error(&format!("odx_client_create: reqwest build failed: {e}"));
                return OdxStatus::InvalidConfig;
            }
        };

        let inner = Box::new(ClientInner {
            http,
            base_url,
            api_key,
            default_timeout_secs: cfg.default_timeout_secs,
        });
        unsafe {
            *out_client = Box::into_raw(inner);
        }
        OdxStatus::Ok
    })
}

/// Free a client handle. Null-safe. Drops the connection pool.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odx_client_free(client: *mut ClientInner) {
    crate::ffi_guard_unit(|| {
        if !client.is_null() {
            drop(unsafe { Box::from_raw(client) });
        }
    });
}

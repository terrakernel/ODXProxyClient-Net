//! The RPC entry points. `odx_execute` (POST `/api/odoo/execute`) is the
//! workhorse; `odx_get_version` (POST `/api/odoo/version`) and the GET endpoints
//! (`/_/license`, `/_/about`, `/_/metrics`) all reuse the same non-blocking
//! plumbing: submit → immediate request handle → completion callback from a tokio
//! worker → zero-copy response handoff, with `oneshot` cancellation and the
//! exactly-one-callback guarantee. See IMPLEMENTATION-PLAN.md §2.2, §4, §7.2.

use std::ffi::c_void;
use std::ptr;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use bytes::Bytes;
use reqwest::Method;
use reqwest::header::{CONTENT_TYPE, HeaderName, HeaderValue};
use tokio::sync::oneshot;

use crate::client::ClientInner;
use crate::status::OdxStatus;

static X_API_KEY: HeaderName = HeaderName::from_static("x-api-key");
static X_REQUEST_TIMEOUT: HeaderName = HeaderName::from_static("x-request-timeout");
static APPLICATION_JSON: HeaderValue = HeaderValue::from_static("application/json");

/// Added to the client-side timeout so the proxy's own 504 (UpstreamTimeout) can
/// arrive before our local timeout (LocalTimeout) fires.
const TIMEOUT_GRACE_SECS: u64 = 5;

/// In-flight request handle. Shared (`Arc`) between the .NET side (one ref via
/// `Arc::into_raw`, released by `odx_request_free`) and the spawned task (the
/// other ref, dropped on completion). The .NET-held ref keeps this alive so
/// `odx_cancel` is safe any time until `odx_request_free`.
pub struct OdxRequest {
    /// Taken (once) either by `odx_cancel` to signal, or dropped with the struct.
    cancel_tx: Mutex<Option<oneshot::Sender<()>>>,
    /// Set by the task when it has chosen a branch; lets `odx_cancel` short-circuit.
    done: AtomicBool,
}

/// Owns a response body buffer handed to the callback. Free via `odx_buffer_free`.
/// Holds the reqwest `Bytes` directly (no copy) — the data pointer points into its
/// heap buffer.
pub struct OdxBuffer {
    bytes: Bytes,
}

/// Completion callback. Must be non-null. Invoked exactly once per submitted
/// request, from a tokio worker thread. `data_ptr`/`owner` are null on transport
/// failure or cancel. The callee takes ownership of `owner` and must free it with
/// `odx_buffer_free`.
pub type OdxCallback = unsafe extern "C" fn(
    user_data: *mut c_void,
    status: OdxStatus,
    http_status: u16,
    data_ptr: *const u8,
    data_len: usize,
    owner: *mut OdxBuffer,
);

/// Wraps the caller's opaque cookie so it can be moved into the spawned task.
struct SendPtr(*mut c_void);
// SAFETY: the pointer is an opaque cookie the caller keeps valid until its
// callback fires; we never dereference it on the Rust side.
unsafe impl Send for SendPtr {}

enum Outcome {
    Response { http_status: u16, body: Bytes },
    Transport(OdxStatus),
}

// ---- public entry points ----

/// POST `/api/odoo/execute`. `body_ptr`/`body_len` is the full JSON request body;
/// it is copied into Rust before returning. `timeout_secs` overrides the client
/// default for this call (0 = use the client default). On success writes an owned
/// request handle to `*out_request` (free with `odx_request_free`).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odx_execute(
    client: *mut ClientInner,
    body_ptr: *const u8,
    body_len: usize,
    timeout_secs: u32,
    callback: OdxCallback,
    user_data: *mut c_void,
    out_request: *mut *mut OdxRequest,
) -> OdxStatus {
    crate::ffi_guard(OdxStatus::ProxyInternal, || unsafe {
        submit_with_body(
            client,
            body_ptr,
            body_len,
            timeout_secs,
            callback,
            user_data,
            out_request,
            "/api/odoo/execute",
        )
    })
}

/// POST `/api/odoo/version`. Body is `{id, url}` (proxy `x-api-key` only, no Odoo
/// creds). Same shape as `odx_execute`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odx_get_version(
    client: *mut ClientInner,
    body_ptr: *const u8,
    body_len: usize,
    timeout_secs: u32,
    callback: OdxCallback,
    user_data: *mut c_void,
    out_request: *mut *mut OdxRequest,
) -> OdxStatus {
    crate::ffi_guard(OdxStatus::ProxyInternal, || unsafe {
        submit_with_body(
            client,
            body_ptr,
            body_len,
            timeout_secs,
            callback,
            user_data,
            out_request,
            "/api/odoo/version",
        )
    })
}

/// GET `/_/license`. Flat body `{licensee, valid_until, is_valid}` (NOT a JSON-RPC
/// envelope). Uses the client's default timeout.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odx_get_license(
    client: *mut ClientInner,
    callback: OdxCallback,
    user_data: *mut c_void,
    out_request: *mut *mut OdxRequest,
) -> OdxStatus {
    crate::ffi_guard(OdxStatus::ProxyInternal, || unsafe {
        submit_get(client, callback, user_data, out_request, "/_/license")
    })
}

/// GET `/_/about`. Body `{build, version}`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odx_get_about(
    client: *mut ClientInner,
    callback: OdxCallback,
    user_data: *mut c_void,
    out_request: *mut *mut OdxRequest,
) -> OdxStatus {
    crate::ffi_guard(OdxStatus::ProxyInternal, || unsafe {
        submit_get(client, callback, user_data, out_request, "/_/about")
    })
}

/// GET `/_/metrics`. Prometheus text (not JSON) — still delivered as raw bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odx_get_metrics(
    client: *mut ClientInner,
    callback: OdxCallback,
    user_data: *mut c_void,
    out_request: *mut *mut OdxRequest,
) -> OdxStatus {
    crate::ffi_guard(OdxStatus::ProxyInternal, || unsafe {
        submit_get(client, callback, user_data, out_request, "/_/metrics")
    })
}

/// Request cancellation of an in-flight call. Safe to call any time before
/// `odx_request_free`; a no-op if the request already completed. The callback
/// still fires exactly once, with status `Cancelled`. Null-safe.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odx_cancel(request: *mut OdxRequest) {
    crate::ffi_guard_unit(|| {
        if request.is_null() {
            return;
        }
        // Borrow without touching the refcount; the .NET-held ref keeps it alive.
        let state = unsafe { &*(request as *const OdxRequest) };
        if state.done.load(Ordering::Acquire) {
            return;
        }
        if let Ok(mut guard) = state.cancel_tx.lock() {
            if let Some(tx) = guard.take() {
                let _ = tx.send(());
            }
        }
    });
}

/// Release a request handle. Call exactly once. Ref-counted internally, so it is
/// safe regardless of whether the in-flight task has completed. Null-safe.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odx_request_free(request: *mut OdxRequest) {
    crate::ffi_guard_unit(|| {
        if !request.is_null() {
            drop(unsafe { Arc::from_raw(request as *const OdxRequest) });
        }
    });
}

/// Free a response buffer handed to the callback. Null-safe; call exactly once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odx_buffer_free(owner: *mut OdxBuffer) {
    crate::ffi_guard_unit(|| {
        if !owner.is_null() {
            drop(unsafe { Box::from_raw(owner) });
        }
    });
}

// ---- shared internals ----

/// Validate + submit a POST with a JSON body (execute / version).
///
/// # Safety
/// `client`/`out_request` must be valid or null; `body_ptr`/`body_len` describe a
/// borrowed buffer valid for the duration of the call.
unsafe fn submit_with_body(
    client: *mut ClientInner,
    body_ptr: *const u8,
    body_len: usize,
    timeout_secs: u32,
    callback: OdxCallback,
    user_data: *mut c_void,
    out_request: *mut *mut OdxRequest,
    path: &str,
) -> OdxStatus {
    if !out_request.is_null() {
        unsafe { *out_request = ptr::null_mut() };
    }
    if client.is_null() {
        return OdxStatus::InvalidHandle;
    }
    if body_ptr.is_null() || body_len == 0 {
        return OdxStatus::InvalidArgument;
    }
    // Copy the request body: the caller may recycle its buffer as soon as this
    // returns, but the spawned task outlives the call (IMPLEMENTATION-PLAN.md §4.4).
    let body = unsafe { std::slice::from_raw_parts(body_ptr, body_len) }.to_vec();
    let inner = unsafe { &*client };
    spawn_request(
        inner,
        callback,
        user_data,
        out_request,
        Method::POST,
        path,
        Some(body),
        timeout_secs,
    )
}

/// Validate + submit a bodyless GET (license / about / metrics).
///
/// # Safety
/// `client`/`out_request` must be valid or null.
unsafe fn submit_get(
    client: *mut ClientInner,
    callback: OdxCallback,
    user_data: *mut c_void,
    out_request: *mut *mut OdxRequest,
    path: &str,
) -> OdxStatus {
    if !out_request.is_null() {
        unsafe { *out_request = ptr::null_mut() };
    }
    if client.is_null() {
        return OdxStatus::InvalidHandle;
    }
    let inner = unsafe { &*client };
    // timeout_secs = 0 → spawn_request falls back to the client default.
    spawn_request(
        inner,
        callback,
        user_data,
        out_request,
        Method::GET,
        path,
        None,
        0,
    )
}

/// Spawn a validated request onto the runtime and write its handle to
/// `out_request` (or drop the extra ref if the caller passed null). Shared by all
/// endpoints.
fn spawn_request(
    inner: &ClientInner,
    callback: OdxCallback,
    user_data: *mut c_void,
    out_request: *mut *mut OdxRequest,
    method: Method,
    path: &str,
    body: Option<Vec<u8>>,
    timeout_secs: u32,
) -> OdxStatus {
    let rt = match crate::runtime() {
        Some(rt) => rt,
        None => return OdxStatus::RuntimeUnavailable,
    };

    let url = format!("{}{}", inner.base_url, path);
    let http = inner.http.clone(); // cheap Arc clone
    let api_key = inner.api_key.clone();
    let eff_timeout = if timeout_secs > 0 {
        timeout_secs
    } else {
        inner.default_timeout_secs
    };
    let ud = SendPtr(user_data);

    // Request handle: one Arc ref for the task, one handed to the caller.
    let (cancel_tx, mut cancel_rx) = oneshot::channel::<()>();
    let state = Arc::new(OdxRequest {
        cancel_tx: Mutex::new(Some(cancel_tx)),
        done: AtomicBool::new(false),
    });
    let task_state = Arc::clone(&state);

    rt.spawn(async move {
        let ud = ud;
        let task_state = task_state;
        tokio::select! {
            biased;
            _ = &mut cancel_rx => {
                task_state.done.store(true, Ordering::Release);
                let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                    deliver_cancelled(callback, ud.0);
                }));
            }
            outcome = do_request(&http, &api_key, method, &url, body, eff_timeout) => {
                task_state.done.store(true, Ordering::Release);
                let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                    deliver(callback, ud.0, outcome);
                }));
            }
        }
        // task_state's Arc ref drops here.
    });

    if out_request.is_null() {
        // Caller opted out of the handle; drop our extra ref so it isn't leaked.
        // (The task holds the other ref and frees the state on completion.)
        drop(state);
    } else {
        unsafe { *out_request = Arc::into_raw(state) as *mut OdxRequest };
    }
    OdxStatus::Ok
}

async fn do_request(
    http: &reqwest::Client,
    api_key: &HeaderValue,
    method: Method,
    url: &str,
    body: Option<Vec<u8>>,
    timeout_secs: u32,
) -> Outcome {
    let mut req = http.request(method, url).header(&X_API_KEY, api_key);
    if let Some(body) = body {
        req = req.header(CONTENT_TYPE, APPLICATION_JSON.clone()).body(body);
    }
    if timeout_secs > 0 {
        req = req
            .header(&X_REQUEST_TIMEOUT, timeout_secs.to_string())
            .timeout(Duration::from_secs(timeout_secs as u64 + TIMEOUT_GRACE_SECS));
    }
    match req.send().await {
        Ok(resp) => {
            let http_status = resp.status().as_u16();
            match resp.bytes().await {
                Ok(body) => Outcome::Response { http_status, body },
                Err(e) => Outcome::Transport(OdxStatus::from_reqwest_error(&e)),
            }
        }
        Err(e) => Outcome::Transport(OdxStatus::from_reqwest_error(&e)),
    }
}

fn deliver(callback: OdxCallback, user_data: *mut c_void, outcome: Outcome) {
    match outcome {
        Outcome::Response { http_status, body } => {
            let status = OdxStatus::from_http(http_status);
            // Hand the buffer over without copying. Ownership transfers to the
            // callee, which frees it via odx_buffer_free.
            let buffer = Box::new(OdxBuffer { bytes: body });
            let data_ptr = buffer.bytes.as_ptr();
            let data_len = buffer.bytes.len();
            let owner = Box::into_raw(buffer);
            unsafe {
                callback(user_data, status, http_status, data_ptr, data_len, owner);
            }
        }
        Outcome::Transport(status) => unsafe {
            callback(user_data, status, 0, ptr::null(), 0, ptr::null_mut());
        },
    }
}

fn deliver_cancelled(callback: OdxCallback, user_data: *mut c_void) {
    unsafe {
        callback(user_data, OdxStatus::Cancelled, 0, ptr::null(), 0, ptr::null_mut());
    }
}

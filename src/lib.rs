//! odxclient — Rust C-ABI core for the odxproxy .NET client.
//!
//! Phase 1: client lifecycle (`odx_client_create`/`odx_client_free`), the
//! non-blocking `odx_execute` workhorse with a completion callback, a process-
//! global tokio runtime, and zero-copy response handoff. Cancellation, the
//! request handle, and the remaining endpoints land in later phases (see
//! IMPLEMENTATION-PLAN.md §11).
//!
//! Safety model: every `extern "C"` entry point is wrapped in `catch_unwind` so a
//! panic can never unwind across the FFI boundary (spec non-negotiable). The core
//! does no JSON (de)serialization — it is a pure opaque-byte pipe.

mod call;
mod client;
mod status;

use std::cell::RefCell;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr;
use std::sync::OnceLock;

use tokio::runtime::{Builder, Runtime};

pub use call::{
    OdxBuffer, OdxCallback, OdxRequest, odx_buffer_free, odx_cancel, odx_execute, odx_get_about,
    odx_get_license, odx_get_metrics, odx_get_version, odx_request_free,
};
pub use client::{ClientInner, OdxClientConfig, odx_client_create, odx_client_free};
pub use status::OdxStatus;

// ---- global tokio runtime (IMPLEMENTATION-PLAN.md §5) ----

static RUNTIME: OnceLock<Runtime> = OnceLock::new();
const DEFAULT_WORKER_THREADS: usize = 2; // I/O-bound; a small pool is plenty.

/// The process-global runtime, lazily created with defaults on first use.
pub(crate) fn runtime() -> Option<&'static Runtime> {
    if let Some(rt) = RUNTIME.get() {
        return Some(rt);
    }
    let rt = Builder::new_multi_thread()
        .worker_threads(DEFAULT_WORKER_THREADS)
        .enable_all()
        .build()
        .ok()?;
    // If another thread won the race, our `rt` is dropped and theirs is used.
    let _ = RUNTIME.set(rt);
    RUNTIME.get()
}

/// Configure the global runtime before the first client is created.
/// `worker_threads == 0` uses the default. Returns `InvalidArgument` if a runtime
/// already exists.
#[unsafe(no_mangle)]
pub extern "C" fn odx_runtime_init(worker_threads: u32) -> OdxStatus {
    ffi_guard(OdxStatus::RuntimeUnavailable, || {
        if RUNTIME.get().is_some() {
            set_last_error("odx_runtime_init: runtime already initialized");
            return OdxStatus::InvalidArgument;
        }
        let workers = if worker_threads == 0 {
            DEFAULT_WORKER_THREADS
        } else {
            worker_threads as usize
        };
        match Builder::new_multi_thread()
            .worker_threads(workers)
            .enable_all()
            .build()
        {
            Ok(rt) => {
                if RUNTIME.set(rt).is_err() {
                    OdxStatus::InvalidArgument
                } else {
                    OdxStatus::Ok
                }
            }
            Err(e) => {
                set_last_error(&format!("odx_runtime_init: {e}"));
                OdxStatus::RuntimeUnavailable
            }
        }
    })
}

// ---- panic guards (IMPLEMENTATION-PLAN.md §6) ----

pub(crate) fn ffi_guard<F: FnOnce() -> OdxStatus>(on_panic: OdxStatus, f: F) -> OdxStatus {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(status) => status,
        Err(_) => {
            set_last_error("panic caught at FFI boundary");
            on_panic
        }
    }
}

pub(crate) fn ffi_guard_unit<F: FnOnce()>(f: F) {
    let _ = catch_unwind(AssertUnwindSafe(f));
}

// ---- thread-local last error (sync construction diagnostics) ----

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

pub(crate) fn set_last_error(msg: &str) {
    LAST_ERROR.with(|e| {
        let mut e = e.borrow_mut();
        e.clear();
        e.push_str(msg);
    });
}

/// Copies this thread's last error message (UTF-8) into `buf`, returning the full
/// length in bytes (may exceed `cap`, indicating truncation). Only meaningful
/// right after a function returned a non-Ok submit-time status on this thread.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn odx_last_error(buf: *mut u8, cap: usize) -> usize {
    LAST_ERROR.with(|e| {
        let msg = e.borrow();
        let bytes = msg.as_bytes();
        let n = bytes.len();
        if !buf.is_null() && cap > 0 {
            let to_copy = n.min(cap);
            unsafe {
                ptr::copy_nonoverlapping(bytes.as_ptr(), buf, to_copy);
            }
        }
        n
    })
}

// ---- borrowed-slice helpers ----

/// Borrow a `ptr`/`len` pair as a slice. `len == 0` yields an empty slice; a null
/// pointer with non-zero len yields `None`.
pub(crate) unsafe fn slice_opt<'a>(ptr: *const u8, len: usize) -> Option<&'a [u8]> {
    if len == 0 {
        return Some(&[]);
    }
    if ptr.is_null() {
        return None;
    }
    Some(unsafe { std::slice::from_raw_parts(ptr, len) })
}

/// Like `slice_opt`, but also validates UTF-8.
pub(crate) unsafe fn str_opt<'a>(ptr: *const u8, len: usize) -> Option<&'a str> {
    let bytes = unsafe { slice_opt(ptr, len) }?;
    std::str::from_utf8(bytes).ok()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::c_void;
    use std::io::{Read, Write};
    use std::net::TcpListener;
    use std::sync::{Arc, Condvar, Mutex};
    use std::thread::ThreadId;
    use std::time::Duration;

    struct CbResult {
        status: OdxStatus,
        http: u16,
        body: Vec<u8>,
        thread_id: ThreadId,
    }

    type Signal = (Mutex<Option<CbResult>>, Condvar);

    unsafe extern "C" fn capture_cb(
        user_data: *mut c_void,
        status: OdxStatus,
        http_status: u16,
        data_ptr: *const u8,
        data_len: usize,
        owner: *mut OdxBuffer,
    ) {
        let body = if data_ptr.is_null() {
            Vec::new()
        } else {
            unsafe { std::slice::from_raw_parts(data_ptr, data_len) }.to_vec()
        };
        // We took ownership of the buffer; free it (mirrors what the .NET layer does).
        unsafe { odx_buffer_free(owner) };

        let signal = unsafe { &*(user_data as *const Signal) };
        *signal.0.lock().unwrap() = Some(CbResult {
            status,
            http: http_status,
            body,
            thread_id: std::thread::current().id(),
        });
        signal.1.notify_one();
    }

    /// Drives the full async path against a one-shot local HTTP server: verifies
    /// the callback fires with the right status/body AND on a different thread than
    /// the submitter (the off-thread hard invariant).
    #[test]
    fn execute_delivers_response_off_thread() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let body = br#"{"jsonrpc":"2.0","id":"t","result":[]}"#.to_vec();
        let server_body = body.clone();
        let server = std::thread::spawn(move || {
            let (mut sock, _) = listener.accept().unwrap();
            let mut buf = [0u8; 4096];
            let _ = sock.read(&mut buf);
            let head = format!(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                server_body.len()
            );
            sock.write_all(head.as_bytes()).unwrap();
            sock.write_all(&server_body).unwrap();
        });

        let base = format!("http://{addr}");
        let api_key = "test-key";
        let cfg = OdxClientConfig {
            base_url_ptr: base.as_ptr(),
            base_url_len: base.len(),
            api_key_ptr: api_key.as_ptr(),
            api_key_len: api_key.len(),
            default_timeout_secs: 5,
            connect_timeout_ms: 2000,
            pool_max_idle_per_host: 0,
        };
        let mut client: *mut ClientInner = std::ptr::null_mut();
        assert_eq!(unsafe { odx_client_create(&cfg, &mut client) }, OdxStatus::Ok);
        assert!(!client.is_null());

        let signal: Arc<Signal> = Arc::new((Mutex::new(None), Condvar::new()));
        let req = br#"{"id":"t","action":"search_read","model_id":"res.partner","keyword":{},"params":[],"odoo_instance":{"url":"x","user_id":1,"db":"d","api_key":"k"}}"#;
        let submit_tid = std::thread::current().id();
        let mut handle: *mut OdxRequest = std::ptr::null_mut();
        let st = unsafe {
            odx_execute(
                client,
                req.as_ptr(),
                req.len(),
                0,
                capture_cb,
                Arc::as_ptr(&signal) as *mut c_void,
                &mut handle,
            )
        };
        assert_eq!(st, OdxStatus::Ok);
        assert!(!handle.is_null(), "Phase 2 produces a request handle");

        // Wait for the callback (Condvar releases the lock while parked, so the
        // callback thread can acquire it).
        let (lock, cvar) = &*signal;
        let mut guard = lock.lock().unwrap();
        while guard.is_none() {
            let (g, timeout) = cvar.wait_timeout(guard, Duration::from_secs(10)).unwrap();
            guard = g;
            if timeout.timed_out() {
                break;
            }
        }
        let result = guard.take().expect("callback fired within 10s");
        drop(guard);

        assert_eq!(result.status, OdxStatus::Ok);
        assert_eq!(result.http, 200);
        assert_eq!(result.body, body);
        assert_ne!(
            result.thread_id, submit_tid,
            "callback must be delivered off the submitting thread"
        );

        // Cancelling after completion is a no-op; then release the handle.
        unsafe { odx_cancel(handle) };
        unsafe { odx_request_free(handle) };

        server.join().unwrap();
        unsafe { odx_client_free(client) };
    }

    /// Cancels an in-flight call whose (slow) server has not yet responded, and
    /// asserts exactly one callback fires with status `Cancelled`.
    #[test]
    fn cancel_in_flight_delivers_cancelled_once() {
        // Server accepts + reads the request, then stalls ~2s without responding.
        // Detached (not joined): cancel may win *before* reqwest connects, in which
        // case `accept()` never returns — the test must not depend on it.
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let _server = std::thread::spawn(move || {
            if let Ok((mut sock, _)) = listener.accept() {
                let mut buf = [0u8; 4096];
                let _ = sock.read(&mut buf);
                std::thread::sleep(Duration::from_secs(2)); // stall past the cancel
            }
        });

        let base = format!("http://{addr}");
        let api_key = "k";
        let cfg = OdxClientConfig {
            base_url_ptr: base.as_ptr(),
            base_url_len: base.len(),
            api_key_ptr: api_key.as_ptr(),
            api_key_len: api_key.len(),
            default_timeout_secs: 30, // long, so a timeout can't pre-empt the cancel
            connect_timeout_ms: 2000,
            pool_max_idle_per_host: 0,
        };
        let mut client: *mut ClientInner = std::ptr::null_mut();
        assert_eq!(unsafe { odx_client_create(&cfg, &mut client) }, OdxStatus::Ok);

        let signal: Arc<Signal> = Arc::new((Mutex::new(None), Condvar::new()));
        let req = br#"{"id":"c","action":"search","model_id":"res.partner","keyword":{},"params":[],"odoo_instance":{"url":"x","user_id":1,"db":"d","api_key":"k"}}"#;
        let mut handle: *mut OdxRequest = std::ptr::null_mut();
        let st = unsafe {
            odx_execute(
                client,
                req.as_ptr(),
                req.len(),
                0,
                capture_cb,
                Arc::as_ptr(&signal) as *mut c_void,
                &mut handle,
            )
        };
        assert_eq!(st, OdxStatus::Ok);
        assert!(!handle.is_null());

        // Cancel while the server is still stalling.
        unsafe { odx_cancel(handle) };

        let (lock, cvar) = &*signal;
        let mut guard = lock.lock().unwrap();
        while guard.is_none() {
            let (g, timeout) = cvar.wait_timeout(guard, Duration::from_secs(5)).unwrap();
            guard = g;
            if timeout.timed_out() {
                break;
            }
        }
        let result = guard.take().expect("callback fired after cancel");
        drop(guard);
        assert_eq!(result.status, OdxStatus::Cancelled);
        assert_eq!(result.http, 0);
        assert!(result.body.is_empty());

        unsafe { odx_request_free(handle) };
        unsafe { odx_client_free(client) };
        // `_server` is detached; it is reaped at process exit.
    }

    #[test]
    fn create_rejects_empty_base_url() {
        let api_key = "k";
        let cfg = OdxClientConfig {
            base_url_ptr: std::ptr::null(),
            base_url_len: 0,
            api_key_ptr: api_key.as_ptr(),
            api_key_len: api_key.len(),
            default_timeout_secs: 0,
            connect_timeout_ms: 0,
            pool_max_idle_per_host: 0,
        };
        let mut client: *mut ClientInner = std::ptr::null_mut();
        assert_eq!(
            unsafe { odx_client_create(&cfg, &mut client) },
            OdxStatus::InvalidConfig
        );
        assert!(client.is_null());
    }

    #[test]
    fn execute_null_client_is_rejected() {
        let req = br#"{}"#;
        let mut handle: *mut OdxRequest = std::ptr::null_mut();
        let st = unsafe {
            odx_execute(
                std::ptr::null_mut(),
                req.as_ptr(),
                req.len(),
                0,
                capture_cb,
                std::ptr::null_mut(),
                &mut handle,
            )
        };
        assert_eq!(st, OdxStatus::InvalidHandle);
    }

    /// One-shot HTTP server: records the request's first line and replies 200 with
    /// `resp_body`. Returns (addr, request-line-cell, join-handle).
    fn oneshot_server(
        resp_body: Vec<u8>,
    ) -> (
        std::net::SocketAddr,
        Arc<Mutex<Option<String>>>,
        std::thread::JoinHandle<()>,
    ) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let line: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));
        let line_srv = Arc::clone(&line);
        let handle = std::thread::spawn(move || {
            if let Ok((mut sock, _)) = listener.accept() {
                let mut buf = [0u8; 8192];
                let n = sock.read(&mut buf).unwrap_or(0);
                let text = String::from_utf8_lossy(&buf[..n]);
                *line_srv.lock().unwrap() = Some(text.lines().next().unwrap_or("").to_string());
                let head = format!(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                    resp_body.len()
                );
                let _ = sock.write_all(head.as_bytes());
                let _ = sock.write_all(&resp_body);
            }
        });
        (addr, line, handle)
    }

    fn client_for(addr: std::net::SocketAddr) -> *mut ClientInner {
        let base = format!("http://{addr}");
        let api_key = "k";
        let cfg = OdxClientConfig {
            base_url_ptr: base.as_ptr(),
            base_url_len: base.len(),
            api_key_ptr: api_key.as_ptr(),
            api_key_len: api_key.len(),
            default_timeout_secs: 5,
            connect_timeout_ms: 2000,
            pool_max_idle_per_host: 0,
        };
        let mut client: *mut ClientInner = std::ptr::null_mut();
        assert_eq!(unsafe { odx_client_create(&cfg, &mut client) }, OdxStatus::Ok);
        client
    }

    fn wait_result(signal: &Arc<Signal>) -> CbResult {
        let (lock, cvar) = &**signal;
        let mut guard = lock.lock().unwrap();
        while guard.is_none() {
            let (g, timeout) = cvar.wait_timeout(guard, Duration::from_secs(10)).unwrap();
            guard = g;
            if timeout.timed_out() {
                break;
            }
        }
        guard.take().expect("callback fired within 10s")
    }

    #[test]
    fn get_about_routes_get_to_correct_path() {
        let about = br#"{"build":"b1","version":"0.1.0"}"#.to_vec();
        let (addr, req_line, server) = oneshot_server(about.clone());
        let client = client_for(addr);

        let signal: Arc<Signal> = Arc::new((Mutex::new(None), Condvar::new()));
        let mut handle: *mut OdxRequest = std::ptr::null_mut();
        let st = unsafe {
            odx_get_about(
                client,
                capture_cb,
                Arc::as_ptr(&signal) as *mut c_void,
                &mut handle,
            )
        };
        assert_eq!(st, OdxStatus::Ok);
        assert!(!handle.is_null());

        let result = wait_result(&signal);
        assert_eq!(result.status, OdxStatus::Ok);
        assert_eq!(result.http, 200);
        assert_eq!(result.body, about);

        let line = req_line.lock().unwrap().clone().unwrap_or_default();
        assert!(line.starts_with("GET /_/about "), "unexpected request line: {line}");

        unsafe { odx_request_free(handle) };
        server.join().unwrap();
        unsafe { odx_client_free(client) };
    }

    #[test]
    fn get_version_routes_post_to_correct_path() {
        let resp = br#"{"jsonrpc":"2.0","id":"v","result":{"server_version":"17.0"}}"#.to_vec();
        let (addr, req_line, server) = oneshot_server(resp.clone());
        let client = client_for(addr);

        let signal: Arc<Signal> = Arc::new((Mutex::new(None), Condvar::new()));
        let body = br#"{"id":"v","url":"https://odoo.example"}"#;
        let mut handle: *mut OdxRequest = std::ptr::null_mut();
        let st = unsafe {
            odx_get_version(
                client,
                body.as_ptr(),
                body.len(),
                0,
                capture_cb,
                Arc::as_ptr(&signal) as *mut c_void,
                &mut handle,
            )
        };
        assert_eq!(st, OdxStatus::Ok);
        assert!(!handle.is_null());

        let result = wait_result(&signal);
        assert_eq!(result.status, OdxStatus::Ok);
        assert_eq!(result.http, 200);
        assert_eq!(result.body, resp);

        let line = req_line.lock().unwrap().clone().unwrap_or_default();
        assert!(
            line.starts_with("POST /api/odoo/version "),
            "unexpected request line: {line}"
        );

        unsafe { odx_request_free(handle) };
        server.join().unwrap();
        unsafe { odx_client_free(client) };
    }
}

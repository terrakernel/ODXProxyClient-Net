# odxproxy .NET Client — Implementation Plan

Status: **design locked, pre-code.** This resolves the "Open / not yet decided" items in
[CLAUDE.md](CLAUDE.md): the FFI function list, handle/session lifecycle, the callback→`Task`
bridge, the error-code enum, and the Windows build config. Grounded against
`theodxproxy` source (`src/main.rs`, `src/models.rs`, `src/errors.rs`) and the two Rust
siblings — not guessed.

The architecture, async model, safety model, and perf constraints are already settled (see
CLAUDE.md + memory notes). This document turns them into concrete signatures and a build.

---

## 0. Key realization that shapes everything

**The Rust core needs no serde.** With the zero-copy design below:

- **Requests** are a fully-formed JSON body built on the .NET side (`Utf8JsonWriter`) and handed
  to Rust as opaque bytes. Rust attaches headers and POSTs them verbatim — it never parses the
  request.
- **Responses** come back from `reqwest` as an already-contiguous body buffer. Rust hands the
  bytes to .NET *without parsing*. The proxy already encodes the error **category** in the HTTP
  status (`401/400/403/504/502/500/200`), which `reqwest` exposes for free — so Rust classifies
  the outcome without touching the JSON.

Consequence: the Rust dependency set is essentially just `reqwest` + `tokio`. No `serde_json`
in the core. This is the leanest possible core (constraint #1) and keeps the boundary a pure
byte pipe (constraint #3, raw passthrough). The only JSON parsing anywhere is on the .NET side,
off the UI thread (the hard invariant in [`ffi-async-model`](.claude/memory/ffi-async-model.md)).

---

## 1. Architecture at a glance

```
 .NET consumer (WinUI POS, or any .NET app)
   │  await client.SearchReadAsync<Partner>(...)
   ▼
 TerraKernel.OdxClient         (typed convenience layer — C#)
   • builds request body with Utf8JsonWriter (off UI thread)
   • TaskCompletionSource<T> (RunContinuationsAsynchronously)
   • parses response T off-thread (Utf8JsonReader + source-gen)
   • Many2One / false-as-null converters (opt-in, TerraKernel.OdxClient.Json)
   │
   ▼  [LibraryImport] flat C ABI, opaque handles, function-pointer callback
 TerraKernel.OdxClient.Interop (thin P/Invoke — C#, no logic)
   │
   ▼  odxclient.dll  (C ABI)
 odxclient crate               (Rust cdylib — ALL protocol/transport/retry logic)
   • one process-global tokio runtime + per-client reqwest::Client (keep-alive pool)
   • submit → return request handle → fire caller callback on completion
   • cancel-in-flight; panics caught at the boundary
   │
   ▼  plain HTTP (stateless, creds re-sent per call)
 odxproxy  (Rust/Axum)  →  Odoo JSON-RPC
```

Two-layer .NET binding is already resolved (CLAUDE.md "Open / not yet decided" → resolved):
raw-bytes interop + typed convenience. Only the signatures below were open; this plan sets them.

---

## 2. FFI surface (C ABI)

All functions are `extern "C"`, `#[no_mangle]`, and **every body is wrapped in
`catch_unwind`** (§6.3). Opaque handles are raw pointers; all strings/bytes are `ptr + len`
pairs (never null-terminated assumptions). Nothing uses COM or complex marshaling
(constraint #7, AOT-friendly).

### 2.1 Types

```c
// Transport/proxy outcome category — determined WITHOUT parsing the JSON body.
// The JSON-RPC error.code inside the body is a separate axis, read on the .NET side (§3).
typedef int32_t OdxStatus;   // see §3.1 for the enumerants

// Opaque handles
typedef struct OdxClient        OdxClient;         // owns reqwest::Client + config
typedef struct OdxRequest       OdxRequest;        // in-flight call; used to cancel
typedef struct OdxBuffer        OdxBuffer;         // owns a response body buffer (Rust-allocated)

// Client construction config (blittable, repr(C))
typedef struct {
    const uint8_t* base_url_ptr;   uintptr_t base_url_len;   // e.g. "https://proxy.host:3000"
    const uint8_t* api_key_ptr;    uintptr_t api_key_len;    // proxy x-api-key
    uint32_t default_timeout_secs; // sent as x-request-timeout; 0 = omit the header
    uint32_t connect_timeout_ms;   // reqwest connect timeout; 0 = reqwest default
    uint32_t pool_max_idle_per_host; // 0 = reqwest default
} OdxClientConfig;

// Completion callback. Invoked exactly ONCE per submitted request, from a tokio
// worker thread. Must return quickly and must not throw across the boundary.
typedef void (*OdxCallback)(
    void*          user_data,   // opaque cookie the caller passed at submit time
    OdxStatus      status,      // transport/proxy category (§3.1)
    uint16_t       http_status, // raw HTTP status, or 0 if no response was received
    const uint8_t* data_ptr,    // response body bytes; NULL on transport error / cancel
    uintptr_t      data_len,
    OdxBuffer*     owner         // pass to odx_buffer_free when done; NULL if data_ptr is NULL
);
```

### 2.2 Functions

```c
// ---- runtime (optional; lazily created on first client if not called) ----
// Configure the global tokio runtime BEFORE the first client is created.
// worker_threads = 0 → default (small, I/O-bound: capped at ~2). Returns InvalidArgument
// if a runtime already exists.
OdxStatus odx_runtime_init(uint32_t worker_threads);

// ---- client lifecycle ----
OdxStatus odx_client_create(const OdxClientConfig* cfg, OdxClient** out_client);
void      odx_client_free(OdxClient* client);   // null-safe; drops the connection pool

// ---- calls (all non-blocking: submit → out_request → callback later) ----
// body_ptr/body_len: full JSON request body, built on .NET. Copied into Rust before return
// (the .NET buffer may be recycled immediately). timeout_secs overrides the client default
// for this call (0 = use client default).
OdxStatus odx_execute    (OdxClient* c, const uint8_t* body_ptr, uintptr_t body_len,
                          uint32_t timeout_secs, OdxCallback cb, void* user_data,
                          OdxRequest** out_request);   // POST /api/odoo/execute
OdxStatus odx_get_version(OdxClient* c, const uint8_t* body_ptr, uintptr_t body_len,
                          uint32_t timeout_secs, OdxCallback cb, void* user_data,
                          OdxRequest** out_request);   // POST /api/odoo/version {id,url}
OdxStatus odx_get_license(OdxClient* c, OdxCallback cb, void* user_data,
                          OdxRequest** out_request);   // GET /_/license  (flat body, not envelope)
OdxStatus odx_get_about  (OdxClient* c, OdxCallback cb, void* user_data,
                          OdxRequest** out_request);   // GET /_/about
OdxStatus odx_get_metrics(OdxClient* c, OdxCallback cb, void* user_data,
                          OdxRequest** out_request);   // GET /_/metrics (Prometheus text)

// ---- cancellation & cleanup ----
// Safe to call anytime; no-op if the request already completed. The callback still fires
// exactly once, with status = Cancelled.
void odx_cancel(OdxRequest* request);
// Release the request handle. Call exactly once (typically at the end of the callback, or
// right after submit if you never intend to cancel). Ref-counted internally — safe w.r.t. the
// in-flight task.
void odx_request_free(OdxRequest* request);
// Free a response body buffer handed to the callback. Call exactly once after you finish
// reading data_ptr.
void odx_buffer_free(OdxBuffer* owner);

// ---- diagnostics (sync construction errors) ----
// Copies the last thread-local error message (UTF-8) into buf; returns the full length
// (may exceed cap → truncated). Only meaningful right after a function returned a non-Ok
// submit-time status on this thread.
uintptr_t odx_last_error(uint8_t* buf, uintptr_t cap);
```

**Why explicit per-endpoint functions** instead of one generic `odx_request(endpoint_enum,…)`:
there are only five endpoints, GET vs POST and which headers attach differ per endpoint, and a
self-documenting ABI is worth more than saving four function stubs. (Noted as a reversible
choice — collapsing to one dispatcher later is trivial.)

**Why `id` isn't an FFI concern**: the proxy echoes the request `id`, but correlation on our
side is done via `user_data`, not the wire `id`. `.NET` puts whatever `id` it likes inside the
body it builds.

---

## 3. Error model — three distinct axes

Do not conflate these. Each lives at a different layer.

1. **`OdxStatus`** (Rust → callback): coarse transport/proxy category, derived from the reqwest
   outcome + HTTP status. **No JSON parsing.** This is all Rust knows.
2. **JSON-RPC `error.code`** (inside the response body): the fine-grained proxy/Odoo code
   (`-32000…-32005`, `0`, or an Odoo-side code on HTTP 200). Read on the **.NET side** with a
   cheap `Utf8JsonReader` scan, only when needed to build an exception.
3. **`OdxException` hierarchy** (.NET, typed): the public, catchable taxonomy the consumer sees.
   Ported *critically* from the Swift sibling's `OdxErrors.swift` (best taxonomy — see
   [`wire-helpers-placement`](.claude/memory/wire-helpers-placement.md)).

### 3.1 `OdxStatus` enum (Rust, `#[repr(i32)]`)

Kept clean (no HTTP numbers overloaded in); the raw HTTP status rides alongside in the callback.

```rust
#[repr(i32)]
pub enum OdxStatus {
    Ok = 0,               // HTTP 2xx. Body may STILL carry an Odoo logic error (§3.2) — .NET checks.

    // submit-time (returned directly by odx_* submit fns, never via callback)
    InvalidHandle = 1,    // null / already-freed client or request pointer
    InvalidArgument = 2,  // null body ptr, zero-length where required, etc.
    InvalidConfig = 3,    // bad base_url / api_key at create time
    RuntimeUnavailable = 4,

    // transport (no usable HTTP response)
    LocalTimeout = 10,    // reqwest client-side timeout (our timeout elapsed)
    ConnectError = 11,    // DNS / TCP / TLS failure reaching the proxy
    TransportError = 12,  // other reqwest send/recv failure
    Cancelled = 13,       // odx_cancel fired before completion

    // proxy / HTTP categories (mapped from the proxy's HTTP status)
    Unauthorized = 20,    // 401  → proxy error.code -32000
    BadRequest = 21,      // 400  → -32001 invalid action / -32002 missing fn_name
    Forbidden = 22,       // 403  → license invalid, error.code 0
    UpstreamTimeout = 23, // 504  → -32003
    UpstreamConnect = 24, // 502  → -32004
    ProxyInternal = 25,   // 500  → -32005
    ServerError = 26,     // any other non-2xx
}
```

Mapping source of truth (`theodxproxy/src/main.rs` `handle_rpc_error_wing`, `auth_guard`,
`proxy_handler`): `-32000` auth/401 · `-32001` invalid action/400 · `-32002` missing fn_name/400
· `-32003` upstream timeout/504 · `-32004` upstream connect/502 · `-32005` proxy internal/500 ·
`0` license invalid/403. **Odoo logic errors are returned on HTTP 200** with Odoo's own code in
`error.code` — hence §3.2.

### 3.2 The HTTP-200-with-error trap (must-handle on .NET)

The proxy returns Odoo-side logic errors as **`200 OK`** with an `error` object in the envelope
(`_ => StatusCode::OK` arm in `handle_rpc_error_wing`). Therefore `OdxStatus::Ok` / `http 200`
does **not** imply success. The .NET convenience layer, on `Ok`, does a cheap top-level
`Utf8JsonReader` check: if the envelope has an `error` member → throw `OdxOdooException(code,
message, data)`; else deserialize `result` into `T`. This check is O(envelope prefix), off the
UI thread.

### 3.3 `.NET` exception taxonomy (initial; reconcile against Swift `OdxErrors.swift`)

```
OdxException                      (base; carries OdxStatus, optional rpc code/message/data)
├─ OdxAuthException               Unauthorized  (-32000)
├─ OdxBadRequestException         BadRequest    (-32001 / -32002; message distinguishes)
├─ OdxLicenseException            Forbidden     (0)
├─ OdxUpstreamTimeoutException    UpstreamTimeout (-32003)
├─ OdxUpstreamConnectException    UpstreamConnect (-32004)
├─ OdxProxyInternalException      ProxyInternal (-32005)
├─ OdxServerException             ServerError / other non-2xx
├─ OdxOdooException               HTTP 200 + error body (Odoo-side code/message/data)
└─ OdxTransportException          LocalTimeout / ConnectError / TransportError
   (Cancelled surfaces as OperationCanceledException, not an OdxException)
```

---

## 4. Handle & lifecycle model

### 4.1 Client handle
`OdxClient*` = `Box::into_raw(Box::new(ClientInner { http, base_url, api_key: HeaderValue,
default_timeout, rt: &'static Runtime }))`. `odx_client_free` = `Box::from_raw` + drop (null-safe,
inside `catch_unwind`). One `reqwest::Client` per handle = one keep-alive connection pool to the
proxy (CLAUDE.md "Connection pooling" decision). On .NET it's wrapped in a `SafeHandle` so the
native client is released even if `Dispose` is missed.

### 4.2 Request handle & the exactly-one-callback guarantee
`OdxRequest*` wraps an `Arc<RequestState>` holding a **cancellation `oneshot::Sender`** (or its
trigger) and an `AtomicBool done`. Two `Arc` refs exist: one returned to .NET, one held by the
spawned task.

- The task does `tokio::select! { res = do_http(...) => …, _ = cancel_rx => fire Cancelled }`.
  Whichever branch wins, the task fires the callback **once**, sets `done`, drops its ref.
  (We deliberately do *not* use `AbortHandle::abort()` — that would drop the future with no
  callback and break the one-callback guarantee.)
- `odx_cancel` borrows the state, and if `!done`, triggers the cancel signal. Safe anytime;
  a no-op after completion.
- `odx_request_free` = `Arc::from_raw` + drop (decrement). Actual free happens when both refs
  are gone → no use-after-free, no double-free regardless of call ordering.

### 4.3 Response buffer ownership (zero-copy handoff)
`reqwest` has already read the body into one contiguous `Bytes`. We `Box` it and pass
`data_ptr = bytes.as_ptr()`, `owner = Box<Bytes>` raw pointer. **Ownership transfers to .NET**;
Rust does not copy. The .NET continuation reads directly from unmanaged memory via
`new ReadOnlySpan<byte>(data_ptr, len)` → `Utf8JsonReader`, then `odx_buffer_free` in a `finally`.
This beats copying (search_read responses can be large — constraint #1). Tradeoff stated: .NET
must hold + free an unmanaged buffer across the async hop; the convenience layer owns that
`try/finally`, so consumers never see it.

### 4.4 Request buffer
The request body **is copied** into Rust inside `odx_execute` before it returns, because the
.NET side recycles its `ArrayPool` buffer immediately. One `memcpy` per request — necessary and
negligible next to the round-trip (tradeoff stated per constraint #1; pinning instead was
rejected as more fragile and worse for GC on large batch writes).

---

## 5. Async model (Rust side)

- **One process-global `tokio` multi-thread runtime**, lazily created on first `odx_client_create`
  (or eagerly via `odx_runtime_init`). Worker count small (I/O-bound; default cap ~2) — perf
  footprint over parallelism we don't need.
- Submit path: build the `reqwest::RequestBuilder` (headers: `content-type: application/json`,
  `x-api-key`, optional `x-request-timeout`), `rt.spawn(task)`, return the request handle
  **immediately**. Non-blocking, matches both siblings (Swift `async`, Kotlin `CompletableFuture`)
  — CLAUDE.md async decision.
- Completion: task maps the outcome → `OdxStatus`, wraps the body buffer, invokes the callback
  on its tokio worker thread, then returns (freeing the worker). The callback is designed to be
  trivial (§7.2) so the worker isn't held.

---

## 6. Safety (non-negotiables)

### 6.1 No unwinding across FFI
Every `extern "C"` fn body → `std::panic::catch_unwind(AssertUnwindSafe(|| { … }))`; on panic
return `OdxStatus::ProxyInternal` (or `RuntimeUnavailable` for submit) and stash a message via
`odx_last_error`. A tiny `ffi_guard!` macro standardizes this.

### 6.2 Panic isolation in tasks
The spawned task body is also wrapped in `catch_unwind`; a panic becomes a `TransportError`
callback (with the message) rather than unwinding into the tokio worker (mirrors the
panic-isolated `tokio::spawn` pattern in `trustedtimeclient-rs`).

### 6.3 `panic = "unwind"` stays
Do **not** set `panic = "abort"` — `catch_unwind` needs unwind. (Called out because release
profiles often flip this.)

---

## 7. .NET binding (two layers)

### 7.1 Interop layer — `TerraKernel.OdxClient.Interop` (thin, no logic)
`[LibraryImport("odxclient")]` `partial` methods (source-generated, AOT-friendly, reflection-free
— constraint #7) mirroring §2.2. Blittable `OdxClientConfig`. The callback is a single static
`[UnmanagedCallersOnly]` method exposed as a `delegate* unmanaged<…>` — **no per-call delegate
allocation, no marshaling**. `OdxStatus` enum mirror. `SafeHandle` subclasses for `OdxClient*`.

### 7.2 The callback → `Task` bridge (the crux)

```csharp
// Per-call context, pinned across the boundary via a GCHandle (normal, not pinned-object).
sealed class CallContext {
    public TaskCompletionSource<PooledResponse> Tcs;   // RunContinuationsAsynchronously
    public CancellationTokenRegistration CtReg;
}

// Submit (simplified). Runs the body build off the UI thread if needed, then hands bytes to Rust.
Task<PooledResponse> SubmitAsync(ReadOnlyMemory<byte> body, uint timeoutSecs, CancellationToken ct)
{
    var tcs = new TaskCompletionSource<PooledResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
    var ctx = new CallContext { Tcs = tcs };
    var gch = GCHandle.Alloc(ctx);                      // freed inside the callback
    OdxRequestHandle req;
    OdxStatus st;
    unsafe {
        fixed (byte* p = body.Span)
            st = NativeMethods.odx_execute(_client, p, (nuint)body.Length, timeoutSecs,
                                           &OnComplete, (void*)GCHandle.ToIntPtr(gch), out req);
    }
    if (st != OdxStatus.Ok) { gch.Free(); throw OdxException.FromSubmit(st); }
    ctx.CtReg = ct.Register(static r => NativeMethods.odx_cancel((OdxRequestHandle)r), req);
    // req is freed inside OnComplete after we detach cancellation.
    return tcs.Task;
}

[UnmanagedCallersOnly]
static unsafe void OnComplete(void* userData, OdxStatus status, ushort http,
                              byte* data, nuint len, OdxBuffer* owner)
{
    // MUST be trivial + non-throwing. NO deserialize here (would run on the tokio thread).
    var gch = GCHandle.FromIntPtr((IntPtr)userData);
    var ctx = (CallContext)gch.Target!;
    try {
        ctx.CtReg.Dispose();
        // Wrap the unmanaged buffer WITHOUT copying; parse happens in the continuation.
        var resp = new PooledResponse(status, http, data, len, owner);
        if (status == OdxStatus.Cancelled) ctx.Tcs.TrySetCanceled();
        else ctx.Tcs.TrySetResult(resp);   // RunContinuationsAsynchronously → hops off this thread
    } catch { /* last-resort: never let an exception cross into native */ }
    finally { gch.Free(); }
    // Note: odx_request_free + odx_buffer_free happen on the .NET side once the continuation
    // has consumed the buffer (PooledResponse.Dispose), so the tokio worker returns now.
}
```

Then in the public `...Async<T>`:

```csharp
using var resp = await SubmitAsync(body, timeoutSecs, ct).ConfigureAwait(false); // continuation off UI
return DeserializeOrThrow<T>(resp);   // Utf8JsonReader over unmanaged span; off-thread
```

**Guarantees delivered (matches [`api-safety-naive-consumers`](.claude/memory/api-safety-naive-consumers.md)
+ [`ffi-async-model`](.claude/memory/ffi-async-model.md)):**
- `RunContinuationsAsynchronously` ⇒ the Rust/tokio thread never runs the .NET deserialize inline;
  the worker frees instantly.
- `ConfigureAwait(false)` on every internal await ⇒ the parse runs on the thread pool, never the
  captured UI `SynchronizationContext`. There is **no** code path that drags a parse onto the UI
  thread, whatever the caller does.
- Body build uses a "hop only if currently on a UI `SynchronizationContext`" helper — no wasted
  bounce for small requests (perf #1), but large batch writes never serialize on the UI thread.

### 7.3 Public surface (`TerraKernel.OdxClient`)
- Primary: `...Async<T>` — `SearchReadAsync<T>`, `SearchAsync`, `ReadAsync<T>`, `SearchCountAsync`,
  `FieldsGetAsync`, `CreateAsync`, `WriteAsync`, `UnlinkAsync`, `CallMethodAsync<T>`,
  `GetVersionAsync`, `GetLicenseAsync`, `GetAboutAsync`. Each takes a `CancellationToken`.
- **No synchronous/blocking API** (kills the `.Result` footgun by construction).
- Optional callback overloads (`onSuccess`/`onError`) that capture `SynchronizationContext.Current`
  and post results back to it — framework-agnostic (NOT WinUI `DispatcherQueue`).
- Client-side pagination reset (zero `fields/order/limit/offset` for actions where they're
  meaningless) lives here, never in Rust (per `ffi-async-model`).

### 7.4 Wire helpers — `TerraKernel.OdxClient.Json` (opt-in, segregated)
`Many2OneConverter` and an opt-in false-as-null scalar converter as `System.Text.Json`
`JsonConverter`s the app plugs into its own `JsonSerializerOptions`; a source-gen `JsonSerializerContext`
for AOT. **Encode side emits the bare int id, not `[id,name]`** (Odoo write semantics — the
critical-port note in [`wire-helpers-placement`](.claude/memory/wire-helpers-placement.md)).
Never in the Rust core.

---

## 8. Serialization strategy (both sides)

- **.NET request**: `Utf8JsonWriter` straight into an `ArrayPool<byte>` buffer — no intermediate
  `string`, reflection-free, AOT-friendly. Build `{id, action, model_id, keyword, params, fn_name?,
  odoo_instance{url,user_id,db,api_key}}` (exact shape from `theodxproxy/src/models.rs`).
- **.NET response**: `Utf8JsonReader` over the unmanaged response span; `result` deserialized with
  a source-gen context into `T`. Cheap top-level `error`-presence scan first (§3.2).
- **Rust**: no (de)serialization — opaque byte pipe. (Mirrors the proxy's own `Box<RawValue>`
  passthrough.)
- MessagePack/flatbuffers: **not now.** Round-trip + Odoo processing dominate parse cost; revisit
  only with real numbers from §10 (CLAUDE.md serialization decision).

---

## 9. Windows build config

### 9.1 `Cargo.toml`
```toml
[package]
name = "odxclient"
edition = "2024"

[lib]
crate-type = ["cdylib"]        # → odxclient.dll + odxclient.dll.lib (MSVC import lib)

[dependencies]
tokio   = { version = "1", features = ["rt-multi-thread", "sync", "time"] }
reqwest = { version = "0.12", default-features = false,
            features = ["rustls-tls", "http2", "gzip", "brotli", "deflate"] }
# no serde / serde_json in the core (see §0)

[profile.release]
opt-level      = 3            # perf > size here
lto            = "fat"
codegen-units  = 1
strip          = "symbols"
panic          = "unwind"     # REQUIRED: catch_unwind at the FFI boundary (§6.3)
```
TLS: `rustls` (matches `trustedtimeclient-rs`, no system OpenSSL/schannel C dep, portable). If a
future requirement forces Windows-native cert stores, swap to `native-tls`/schannel — isolated
change.

### 9.2 Build
```
rustup target add x86_64-pc-windows-msvc      # once
cargo build --release --target x86_64-pc-windows-msvc
# → target/x86_64-pc-windows-msvc/release/odxclient.dll  (+ odxclient.dll.lib)
```
Native build on the dev box, MSVC ABI, no cross-compilation (constraints #5/#6).

### 9.3 C header
`cbindgen` with a checked-in `cbindgen.toml`, run **manually / in an `xtask`** (not in `build.rs`,
to keep every incremental build fast) to emit `include/odxclient.h`. The .NET consumer uses
`LibraryImport` and does **not** need the `.h`; it exists for C/C++ consumers and as ABI
documentation. (Low-maintenance per deliverable #2.)

### 9.4 .NET packaging
`TerraKernel.OdxClient` targets modern .NET (AOT-compatible, no .NET Framework). `odxclient.dll` ships as a
native runtime asset under `runtimes/win-x64/native/` so the layout is nupkg-ready (deliverable #4).

---

## 10. Verification & benchmarks

- **Off-UI-thread test (required by `ffi-async-model`)**: install a custom `SynchronizationContext`,
  call `SearchReadAsync<T>`, assert the deserialization ran on a *different* thread than the caller.
- **One-callback / cancel test**: assert the callback fires exactly once under (a) success,
  (b) `odx_cancel` before completion, (c) cancel *after* completion (no-op) — and no double free.
- **No-deadlock-on-`.Result` test**: a naive `.Result` call must complete (freeze-not-deadlock),
  proving `ConfigureAwait(false)` coverage.
- **Overhead bench (deliverable #5)**: pure-Rust direct call to the proxy vs. the same call through
  the FFI + .NET parse; report per-round-trip delta. This is also the gate for revisiting JSON vs
  MessagePack and blocking-vs-callback (CLAUDE.md deliverable #5).

---

## 11. Phased implementation order

1. **Rust core, happy path**: crate + `odx_client_create/free`, `odx_execute` (POST, headers),
   global runtime, callback on completion, `OdxStatus` from HTTP status, zero-copy buffer handoff.
   No cancel yet.
2. **Rust lifecycle hardening**: request handle + `oneshot` cancel + exactly-one-callback,
   `odx_request_free`/`odx_buffer_free`, `catch_unwind` guards, `odx_last_error`.
3. **Remaining endpoints**: `odx_get_version/license/about/metrics`.
4. **.NET interop layer**: `LibraryImport` decls, `[UnmanagedCallersOnly]` callback, `SafeHandle`.
5. **.NET convenience layer**: callback→`Task` bridge (§7.2), `...Async<T>` surface, off-UI-thread
   guarantees, typed exceptions, `CancellationToken` wiring.
6. **Wire helpers** (`TerraKernel.OdxClient.Json`) + source-gen context.
7. **Tests (§10) + overhead bench + README** (the loud DO/DON'T threading block — CLAUDE.md
   README must-haves).
8. **cbindgen `.h` + nupkg-ready packaging layout.**

---

## 12. Still-open decisions (with recommendation)

| Decision | Recommendation | Revisit trigger |
|---|---|---|
| One generic `odx_request` vs per-endpoint fns | **Per-endpoint** (self-documenting; only 5) | if endpoints proliferate |
| `oneshot` cancel vs `tokio_util::CancellationToken` | **`oneshot`** (avoid the extra dep, footprint) | if cancel semantics get richer |
| Runtime worker threads | **Lazy, cap ~2** (I/O-bound) | bench in §10 |
| Response handoff: transfer vs copy | **Transfer (zero-copy)** | if UAF risk shows up in practice |
| JSON vs MessagePack | **JSON** | only with §10 numbers |
| `.NET` exception taxonomy | **Port Swift `OdxErrors.swift`** verbatim-critically once accessible | on first read of the Swift source |

---

*Grounded against `theodxproxy` @ `D:\Projects\@terrakernel\theodxproxy` (`main.rs`, `models.rs`,
`errors.rs`) and the Rust siblings `trustedtimeclient-rs` / `odoo-rpc-client-rs`. Architecture,
async, safety, and perf decisions per CLAUDE.md + memory notes `performance-philosophy`,
`ffi-async-model`, `api-safety-naive-consumers`, `wire-helpers-placement`.*

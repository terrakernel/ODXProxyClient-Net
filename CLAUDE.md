# CLAUDE.md

Guidance for Claude Code working in this repo.

## Project

Reusable native **.NET client library for odxproxy** (a Rust/Axum proxy fronting Odoo's JSON-RPC). Core is written in **Rust**, compiled to a C-ABI `cdylib`, consumed from .NET via P/Invoke. First consumer is a WinUI 3 POS app, but the library stays standalone and general-purpose.

> **Status: v1.0.0 published on NuGet (2026-08-08); v1.1.0 adds the v2 (JSON-2) API (unreleased).** Rust core: 14 C symbols (`odx_execute_v2` / `odx_get_version_v2` added for `/v2/odoo/*`), 7 Rust tests. .NET: interop (`[LibraryImport]` x 14) → `OdxClient` (callback→`Task` bridge, raw + typed v1 calls, `OdxRequestBuilder`, `OdxAction`) → **v2**: `OdxClient.ForInstanceV2(...)` → `OdxSessionV2` (`SearchAsync`/`SearchReadAsync<T>`/`SearchCountAsync`/`ReadAsync<T>`/`FieldsGetAsync<T>`/`CreateAsync`/`CreateOneAsync`/`WriteAsync`/`UnlinkAsync`/`CallMethodAsync<T>`), `OdxJson` (JsonNode or raw UTF-8 args), session context merge, `GetVersionV2Async` + `SupportsV2Async` (cached per URL), Odoo-status exception subclasses + `-32006`/`-32007` types, `OdooBinary` helper. Tests: `Odx.Client.SmokeTest` (v1, mock, 10), `Odx.Client.V2Test` (v2 wire snapshots + error mapping, mock), `Odx.Client.V2LiveTest` (live, env-var keys), plus the v1 live harnesses (`RealTest`/`ActionsTest`/`ErrorTest`). The proxy repo's `SDK_V2_ROLLOUT.md` tracks the v2 rollout. See [CHANGELOG.md](CHANGELOG.md) and [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md).

## Authoritative spec

**[odxproxy-dotnet-client-prompt.md](odxproxy-dotnet-client-prompt.md) is the spec** — read it first. Non-negotiables, compressed:

1. **Performance is priority #1** — smallest footprint / fewest cycles wins over ergonomics; state the tradeoff when perf hurts readability.
2. **No CLR in client logic** — all connection/serialization/retry/protocol logic lives in Rust, never in the .NET GC/JIT.
3. **Raw Odoo semantics only — no typed model/ORM layer.** Thin passthrough matching odxproxy's surface. Domain modeling belongs in the consuming app.
4. **JSON as the FFI marshaling format** (serde_json ↔ System.Text.Json) unless a lower-overhead option proves out — flag it if so.
5. **Windows 11 only, MSVC ABI** (`x86_64-pc-windows-msvc`). No Win10, no legacy fallbacks.
6. **Built natively on Windows** (MSVC + rustup MSVC target). No cross-compilation.
7. **Consumer uses Native AOT** — keep the C ABI flat, reflection-free, opaque handles, no COM/complex marshaling.

Panics must never unwind across the FFI boundary — catch and convert to error codes.

> **Performance north star (why the constraints are strict, not preferences):** the maintainer holds Windows apps to a high bar — too many feel sluggish, "worse than JVM" (and JVM overhead is at least excusable, since the JVM doesn't own the OS). This library must feel *genuinely native and snappy*, never like a fat managed app. On every tradeoff, favor the leaner option; treat constraint #1 as a hard requirement. See memory `performance-philosophy`.

## Related repos (siblings under `D:\Projects\@terrakernel\`)

- **`theodxproxy/`** — the proxy itself. Source of truth for the wire protocol; inspect it rather than guessing. Key file: `src/main.rs` (routes, auth, error mapping).
- **`odoo-rpc-client-rs/`** — the proxy's Odoo JSON-RPC client dep. Shows `execute_kw` shape and error types; a good reference for our own reqwest usage.
- **[github.com/terrakernel/ODXProxyClient-Swift](https://github.com/terrakernel/ODXProxyClient-Swift)** — sibling client, same author. Mature design to mirror; **best error taxonomy** (typed per-code cases in `OdxErrors.swift`). Port *critically*, don't copy verbatim.
- **[github.com/terrakernel/ODXProxyClient-Java](https://github.com/terrakernel/ODXProxyClient-Java)** — Kotlin/JVM sibling. Good references for **non-blocking async** (returns `CompletableFuture`, I/O off caller thread) and **stream/zero-copy serialization** (`OdxProxyClient.kt`). Its error model is thinner than Swift's — prefer Swift's.

## odxproxy wire protocol (resolved from `theodxproxy` source)

Plain HTTP, stateless, single request/response — no WebSocket/SSE/session. Odoo credentials are sent on **every** call.

- **`POST /api/odoo/execute`** — the workhorse. Auth header `x-api-key`; optional `x-request-timeout` (seconds). Body:
  ```json
  { "id": "...", "action": "...", "model_id": "res.partner",
    "keyword": {}, "params": [], "fn_name": "...only for call_method...",
    "odoo_instance": { "url": "...", "user_id": 2, "db": "...", "api_key": "..." } }
  ```
  Allowed `action` values: `search_count`, `search`, `read`, `fields_get`, `search_read`, `create`, `write`, `unlink`, `call_method` (uses `fn_name`).
- **`POST /api/odoo/version`** — body `{id, url}`, `x-api-key` only (no Odoo creds).
- **`GET /_/license`** — flat object `{licensee, valid_until, is_valid}`, **not** a JSON-RPC envelope (the one exception).
- **`GET /_/about`** — `{build, version}`. **`GET /_/metrics`** — Prometheus.
- **v2 (JSON-2, Odoo 19+):** `POST /v2/odoo/execute` body `{id, model_id, method, kwargs: {…named args…}, odoo_instance: {url, db, api_key}}` (no `user_id`, no action allowlist, no positional args; `ids` in `kwargs` for record methods only). `POST /v2/odoo/version` body `{id, url}` → `{version_info, version}`. `/v1/odoo/*` are aliases of `/api/odoo/*`. The proxy repo's `SYSTEM_ARCHITECTURE.md` §4.6/§4.7/§7.1 is the v2 spec.

Response envelope: `{jsonrpc, id, result?, error?: {code, message, data?}}`.

Error codes: `-32000` auth (401) · `-32001` invalid action (400) · `-32002` missing fn_name (400) · `-32003` upstream timeout (504) · `-32004` upstream connect (502) · `-32005` proxy internal (500) · `0` license invalid (403). Any other code on **200** = Odoo-side logic error; any other on non-200 = generic server error. v2 adds `-32006` (200: no JSON-2 / db not selectable) and `-32007` (400: invalid identifier or header value), and on 200 `error.code` is Odoo's HTTP status (401/403/404/409/422/5xx). Code `0` on 200 is an Odoo error (Odoo 19+ `/jsonrpc`), never a license error.

## Resolved design decisions

- **Connection pooling:** reuse one `reqwest::Client` (HTTP keep-alive). odxproxy is stateless w.r.t. Odoo auth (creds re-sent per call) — there is no Odoo session to pool.
- **Async model:** callback/non-blocking over polling or a blocking flat call — **both siblings make non-blocking async the public contract** (Swift `async/await`, Kotlin `CompletableFuture`). Rust owns a persistent tokio runtime + client at load time; submit → get handle → fire caller callback on completion; expose a cancel-in-flight handle. Still benchmark blocking vs callback (deliverable #5) before finalizing.
- **Threading invariant (hard rule):** all requests + JSON (de)serialization run **off the main/UI thread by default** — the library owns this, not the caller. The expensive path (response bytes → typed `T`) is on the **.NET side** (Rust does raw passthrough). Guarantee it via `TaskCompletionSource<T>` with `RunContinuationsAsynchronously` (Rust's tokio-thread callback never runs .NET work inline) + `ConfigureAwait(false)` on every internal await (deserialize on thread pool, not the UI `SynchronizationContext`). Request serialization also off-UI (batch writes can be large). Verify with a test asserting deserialization runs off an installed `SynchronizationContext`. See memory `ffi-async-model`.
- **Safe-by-default for non-expert consumers:** many consumers won't understand threading. Serialization is decoupled from the call pattern — no calling mistake can drag a JSON parse onto the UI thread (work is done off-thread before the result is handed back). Primary surface = Task-based `...Async` methods (`await` = iOS-like ergonomics, compiler nudges via CS4014). **Ship NO synchronous blocking API** (removes the `.Result` footgun). `ConfigureAwait(false)` everywhere turns a naive sync-over-async deadlock into at worst a brief freeze. Optional callback overload (`onSuccess`/`onError`, marshaled via `SynchronizationContext` — NOT WinUI `DispatcherQueue`, stay framework-agnostic) as the belt-and-suspenders for async-averse devs. Can't stop a determined `.Result`; make it non-catastrophic. See memory `api-safety-naive-consumers`.
- **Serialization:** keep JSON. Round-trip + Odoo processing dominate over parse cost; revisit MessagePack/flatbuffers only with real numbers. Prefer stream/zero-copy + reflection-free: `Utf8JsonReader`/`Utf8JsonWriter` + source generators on .NET (also AOT-friendly), `serde_json::RawValue` passthrough on Rust.
- **Wire-quirk helpers** (Many2One, false-as-null): ship on the .NET side only, opt-in, segregated from the interop layer; never in the Rust core. Rationale + how-to in the memory note `wire-helpers-placement`.

## README / consumer docs (must-haves)

The threading & serialization safety model **must be stated loudly and early in the README** — non-expert consumers will otherwise misuse it and blame the library. Non-negotiable README content:

- A prominent "**Do this / Don't do this**" block near the top: **DO** `var x = await OdxClient.SearchReadAsync<T>(...)`; **DON'T** `.Result` / `.Wait()` (blocks the UI thread — freezes the app; there is deliberately no synchronous API).
- State plainly that all network + JSON work runs off the UI thread automatically — the consumer never needs `Task.Run`, never needs to know threading.
- Show the copy-paste-safe WinUI example (async event handler / `await`), and the callback overload for the async-averse.
- Call out that there is **no blocking/sync API by design**, so nobody goes looking for one.

Model the tone on the Swift sibling's README "gotchas" callout and the Java sibling's "What an LLM reading consumer code should enforce" table. See memory `api-safety-naive-consumers`.

## Open / not yet decided

- Final FFI function list, handle lifecycle, struct layouts, error-code enum values.
- cbindgen vs hand-maintained `.h`.
- Binding shape is **resolved**: two-layer — raw-bytes interop + a typed convenience layer (`...Async<T>` + opt-in converters). Only the exact function signatures remain to design.

**Next concrete step when resuming — shippable-v1 polish (the code is feature-complete):** (1) README with the loud threading DO/DON'T block (`await` yes, `.Result`/`.Wait()` no; runs off the UI thread automatically; no sync API by design; copy-paste WinUI example + callback overload) — model on the Swift README gotchas + Java enforcement table (see the "README / consumer docs" section below); (2) overhead benchmark (FFI+.NET vs pure-Rust round-trip, deliverable #5); (3) cbindgen `.h` (checked-in `cbindgen.toml`, run manually — .NET uses LibraryImport, so the header is for C/C++ consumers only); (4) nupkg layout with the DLL under `runtimes/win-x64/native/`. Optional: a callback-overload surface (`onSuccess`/`onError` via `SynchronizationContext`) for async-averse devs, per memory `api-safety-naive-consumers`.

## Build / test

Rust core (`odxclient` cdylib). `.cargo/config.toml` pins the target, so `--target` is optional.

```
cargo build --release      # → target/x86_64-pc-windows-msvc/release/odxclient.dll (+ .dll.lib)
cargo test                 # in-crate smoke tests (async round-trip against a local server, off-thread delivery)
```

If a server-based test ever hangs, isolate it with `cargo test -- --test-threads=1`.

.NET binding (`dotnet/Odx.Client/`, net10.0). The library builds without the native DLL (LibraryImport generates stubs at compile time). The smoke test copies `odxclient.dll` from the Rust **release** output next to its exe, so `cargo build --release` must have run first.

```
dotnet build dotnet/Odx.Client/Odx.Client.csproj -c Release
dotnet run --project dotnet/Odx.Client.SmokeTest/Odx.Client.SmokeTest.csproj -c Release   # end-to-end vs a local mock server
```

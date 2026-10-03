# ODXProxyClient-Net

A fast, native **.NET client for [odxproxy](https://github.com/terrakernel/theodxproxy)** — a
Rust/Axum proxy that fronts Odoo's JSON-RPC API (v1) and Odoo 19+'s JSON-2 API (v2).

The performance-critical core (connection handling, the HTTP round-trip, cancellation) is
written in **Rust** and compiled to a C-ABI `cdylib` (`odxclient.dll`). The .NET layer is a thin,
AOT-friendly binding: no connection logic runs in the CLR, and all network + JSON work happens off
your UI thread automatically.

This is a **raw passthrough** to odxproxy's surface (`search`, `search_read`, `create`, `write`,
`unlink`, `fields_get`, `call_method`, …). It deliberately ships **no** typed domain models
(`Order`, `Product`, …) — Odoo's schema is dynamic, so domain modelling belongs in your app.

---

## ⚠️ Threading — read this first

**Every call runs the network request and JSON (de)serialization off your UI thread, for you.**
You never call `Task.Run`, and you never need to know anything about threads. Just `await`.

✅ **DO** — `await` the call. The result comes back ready to use on your UI thread:

```csharp
Partner[]? partners = await client.ExecuteAsync<Partner[]>(
    OdxAction.SearchRead, "res.partner", odoo, AppJson.Default.PartnerArray);
```

❌ **DON'T** — block on it. This freezes your UI:

```csharp
var p = client.ExecuteAsync<Partner[]>(...).Result;          // ❌ freezes the app
client.ExecuteAsync<Partner[]>(...).Wait();                  // ❌ freezes the app
client.ExecuteAsync<Partner[]>(...).GetAwaiter().GetResult();// ❌ freezes the app
```

There is **deliberately no synchronous/blocking API** — there is nothing to `.Result` that would be
"the sync way", so nobody goes looking for one. Internally every `await` uses
`ConfigureAwait(false)`, so if you *do* block anyway you get at worst a brief freeze — never a
permanent deadlock — but don't: **`await` is the way.**

### Rules the compiler can't enforce (but you should)

| If your code… | …do this instead |
| --- | --- |
| calls `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` | `await` the call |
| wraps a call in `Task.Run(...)` | just `await` it — it's already off-thread |
| looks for a synchronous `Execute(...)` | there isn't one by design → `await ExecuteAsync(...)` |
| lets exceptions escape | catch `OdxException`; catch `OperationCanceledException` for cancels |

---

## Requirements

- **Windows 11, x64 only** (`x86_64-pc-windows-msvc`). No Windows 10, no other architectures.
- **.NET 10** (or a compatible modern .NET; the binding is Native-AOT compatible).
- The native **`odxclient.dll`** next to your application (see below).

## Building the native DLL

Built natively on Windows with the MSVC toolchain (Visual Studio Build Tools "Desktop development
with C++") plus `rustup target add x86_64-pc-windows-msvc`:

```bash
cargo build --release
# → target/x86_64-pc-windows-msvc/release/odxclient.dll  (+ odxclient.dll.lib)
```

Copy `odxclient.dll` into your app's output directory (or, once packaged, it ships under
`runtimes/win-x64/native/`). P/Invoke resolves it from the application base directory.

## Building the .NET binding

```bash
dotnet build dotnet/Odx.Client/Odx.Client.csproj -c Release
```

---

## Quick start

```csharp
using System.Text.Json.Serialization;
using TerraKernel.OdxClient;

// 1) Declare the shapes you deserialize. Source-generated => reflection-free & AOT-safe.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Partner[]))]
internal partial class AppJson : JsonSerializerContext;

public sealed record Partner(long Id, string Name);

// 2) One client per proxy — reuse it (it owns the connection pool). IDisposable.
using var client = OdxClient.Create(
    baseUrl: "https://proxy.example:3000",
    apiKey:  "<proxy x-api-key>");

// 3) One OdooInstance per Odoo backend — reuse it. odxproxy is stateless w.r.t. Odoo
//    auth, so these credentials are re-sent on every call.
var odoo = new OdooInstance
{
    Url    = "https://odoo.example",
    UserId = 2,
    Db     = "mydb",
    ApiKey = "<odoo api key>",
};

// 4) Call. `action` is a compile-checked OdxAction; `params`/`keyword` are raw Odoo JSON
//    (this client is a thin passthrough).
Partner[]? partners = await client.ExecuteAsync(
    action:      OdxAction.SearchRead,
    modelId:     "res.partner",
    instance:    odoo,
    resultType:  AppJson.Default.PartnerArray,
    paramsJson:  """[[["is_company","=",true]]]"""u8.ToArray(),
    keywordJson: """{"fields":["id","name"],"limit":80}"""u8.ToArray());
```

### Copy-paste-safe WinUI event handler

```csharp
private async void OnRefreshClick(object sender, RoutedEventArgs e)
{
    try
    {
        Partner[]? partners = await _client.ExecuteAsync<Partner[]>(
            OdxAction.SearchRead, "res.partner", _odoo, AppJson.Default.PartnerArray);
        MyListView.ItemsSource = partners;   // back on the UI thread, ready to bind
    }
    catch (OdxException ex)
    {
        await ShowError(ex.Message);
    }
}
```

No `Task.Run`, no dispatcher marshalling — the network + parse already happened off-thread, and the
`await` resumes you on the UI thread.

---

## API surface

`OdxClient` exposes each odxproxy endpoint in three flavours. All are `async`, all take an optional
`CancellationToken`.

| Endpoint | Method | HTTP |
| --- | --- | --- |
| Odoo RPC | `ExecuteAsync` | POST `/api/odoo/execute` |
| Odoo version | `GetVersionAsync` | POST `/api/odoo/version` |
| Odoo RPC, v2 (JSON-2) | [`OdxSessionV2`](#v2-api-odoo-19-json-2) / `ExecuteV2Async` (raw) | POST `/v2/odoo/execute` |
| Odoo version, v2 | `GetVersionV2Async` | POST `/v2/odoo/version` |
| License | `GetLicenseAsync` | GET `/_/license` |
| Build/version | `GetAboutAsync` | GET `/_/about` |
| Prometheus | `GetMetricsAsync` | GET `/_/metrics` |

**Three call styles for `execute`/`version`:**

```csharp
// (a) Structured + typed — recommended. `action` is a compile-checked OdxAction; builds
//     the request envelope for you and deserializes `result` into T.
Partner[]? a = await client.ExecuteAsync<Partner[]>(
    OdxAction.SearchRead, "res.partner", odoo, AppJson.Default.PartnerArray, paramsJson, keywordJson);

// (b) Raw body + typed — you supply the full request JSON, we deserialize the result.
byte[] body = OdxRequestBuilder.BuildExecute(OdxAction.Search, "res.partner", odoo, paramsJson);
long[]? b = await client.ExecuteAsync<long[]>(body, AppJson.Default.Int64Array);

// (c) Raw body + raw response — you own both sides (advanced).
OdxResponse c = await client.ExecuteAsync(body);
// c.Status (OdxStatus), c.HttpStatus (ushort), c.Body (byte[] — the JSON-RPC envelope)
```

The GET endpoints have a typed overload too (`GetAboutAsync<T>(AppJson.Default.AboutInfo)`) and a
raw overload returning `OdxResponse`.

### Actions — use the `OdxAction` enum

The `execute` action is a **closed set** the proxy defines. Pass an `OdxAction` rather than a raw
string so a typo is a **compile error**, not a runtime `-32001 invalid action` round-trip. The enum
value maps to the exact wire string for you (as a UTF-8 constant — no allocation, no reflection):

| `OdxAction` | wire string | | `OdxAction` | wire string |
| --- | --- | --- | --- | --- |
| `SearchCount` | `search_count` | | `Create` | `create` |
| `Search` | `search` | | `Write` | `write` |
| `Read` | `read` | | `Unlink` | `unlink` |
| `FieldsGet` | `fields_get` | | `CallMethod` | `call_method` |
| `SearchRead` | `search_read` | | | |

`OdxAction.CallMethod` requires an `fnName` — the client throws `ArgumentException` up front if you
omit it (the proxy would otherwise return `-32002`):

```csharp
var res = await client.ExecuteAsync<long>(
    OdxAction.CallMethod, "res.partner", odoo, AppJson.Default.Int64,
    paramsJson: """[[42]]"""u8.ToArray(), fnName: "action_archive");
```

> **Escape hatch:** every structured method and `OdxRequestBuilder.BuildExecute` also has a raw
> `string` action overload, in case the proxy adds an action before this enum does. Prefer the
> enum; reach for the string only when you must.

> **Why `JsonTypeInfo<T>`?** The typed methods require a source-generated `JsonTypeInfo<T>` (from
> your `JsonSerializerContext`) so the whole path stays reflection-free and AOT-safe. This is also
> the fastest System.Text.Json path.

### Request bodies

`params` and `keyword` are **raw Odoo JSON** — a JSON array and a JSON object respectively — passed
as `ReadOnlyMemory<byte>`. The client splices them in verbatim (no re-serialization). You know the
Odoo argument shapes; this library does not model them. `OdxRequestBuilder.BuildExecute` /
`BuildVersion` assemble the envelope with `Utf8JsonWriter` if you want the bytes directly.

For large batch bodies the structured overloads serialize on the thread pool when you're on a UI
`SynchronizationContext` (and inline otherwise — small requests pay no hop).

---

## v2 API (Odoo 19+, JSON-2)

ODXProxy 0.9.0 added `/v2` endpoints that reach Odoo over its **JSON-2** API instead of the legacy
`/jsonrpc`. This client exposes them through a separate session type, `OdxSessionV2`, on the same
`OdxClient`:

| Odoo version | v1 (`ExecuteAsync`) | v2 (`ForInstanceV2`) |
| --- | --- | --- |
| 18 and older | ✅ | ❌ (`OdxJson2UnavailableException`) |
| 19 – 21 | ✅ | ✅ |
| 22+ | ❌ (Odoo removes `/jsonrpc`) | ✅ |

v1 is not deprecated and its API is unchanged.

```csharp
using System.Text.Json.Nodes;

// One session per Odoo instance. No user id: JSON-2 derives the user from the key, which must be
// an Odoo API key (not a password; scope `rpc` on Odoo 20+). The context is merged into every call.
OdxSessionV2 erp = client.ForInstanceV2(
    url: "https://erp.example.com", db: "prod", apiKey: "<odoo api key>",
    context: new JsonObject { ["lang"] = "en_US", ["tz"] = "Asia/Jakarta", ["allowed_company_ids"] = new JsonArray(1) });

Partner[]? partners = await erp.SearchReadAsync("res.partner", AppJson.Default.PartnerArray,
    domain: OdxJson.Parse("""[["is_company","=",true]]"""), fields: ["name", "email"], limit: 100);

long[] ids = await erp.CreateAsync("res.partner", OdxJson.Parse("""[{"name":"Acme"},{"name":"Globex"}]""")); // [41, 42]
long id    = await erp.CreateOneAsync("res.partner", new JsonObject { ["name"] = "Initech" });             // 43
await erp.WriteAsync("res.partner", ids, new JsonObject { ["comment"] = "via v2" });
await erp.UnlinkAsync("res.partner", ids);

await erp.CallMethodAsync("account.move", "action_post", AppJson.Default.JsonElement, ids: [invoiceId]);
await erp.CallMethodAsync("res.partner", "name_search", AppJson.Default.JsonElement,
    kwargs: new JsonObject { ["name"] = "Acm", ["limit"] = 5 });
```

Already have a v1 `OdooInstance`? `client.ForInstanceV2(odoo)` reuses it (its `UserId` is ignored).

### Methods and the `kwargs` they send

Method names match v1's `OdxAction` values. Every method builds the JSON-2 `kwargs` object using
**Odoo's Python parameter names exactly** (never case-converted): Odoo checks each key against the
method signature and rejects an unknown one with a 422.

| Method | Odoo method | `kwargs` | Returns |
| --- | --- | --- | --- |
| `SearchAsync(model, domain, offset?, limit?, order?)` | `search` | `domain`, `offset`, `limit`, `order` | `long[]` |
| `SearchReadAsync<T>(model, type, domain?, fields?, offset?, limit?, order?)` | `search_read` | same keys | `T` |
| `SearchCountAsync(model, domain, limit?)` | `search_count` | `domain`, `limit` | `long` |
| `ReadAsync<T>(model, ids, type, fields?, load?)` | `read` | `ids`, `fields`, `load` | `T` |
| `FieldsGetAsync<T>(model, type, allfields?, attributes?)` | `fields_get` | `allfields`, `attributes` | `T` |
| `CreateAsync(model, vals)` | `create` | `vals_list` (**always an array**) | `long[]` (**always**) |
| `CreateOneAsync(model, vals)` | `create` | `vals_list: [vals]` | `long` |
| `WriteAsync(model, ids, vals)` | `write` | `ids`, `vals` | `bool` |
| `UnlinkAsync(model, ids)` | `unlink` | `ids` | `bool` |
| `CallMethodAsync<T>(model, method, type, ids?, kwargs?)` | *method* | `ids` (only if given) + `kwargs` | `T` |
| `GetVersionAsync()` / `client.GetVersionV2Async(url)` | – (`POST /v2/odoo/version`) | – | `OdxVersionInfoV2` |

Every method also takes an optional per-call `context` (merged over the session's; the call's keys
win), `timeoutSecs` and `CancellationToken`. For `CallMethodAsync`, put `context` inside `kwargs`.

- **Unset arguments are omitted**, not sent as `null`, so Odoo's own defaults apply. An unset
  `domain` is `[]` for `search`/`search_count` and omitted for `search_read`.
- **`ids` only goes to record methods.** The typed methods never send it to `@api.model` methods
  (`search`, `search_read`, `search_count`, `fields_get`, `create`). With `CallMethodAsync`, pass
  `ids` only for record methods.
- **No positional arguments.** `CallMethodAsync` takes named arguments only, as a JSON object; the
  names are the Odoo method's Python parameters (see its definition, or `/doc/<model>` in Odoo).
- **`create` always returns an array of ids**, even for one record. Use `CreateOneAsync` for one id.

**JSON arguments** (`domain`, `vals`, `context`, `kwargs`) are `OdxJson`, which accepts either a
`JsonNode` (`new JsonObject { … }`, `new JsonArray(…)`) or pre-serialized UTF-8 bytes
(`"""…"""u8.ToArray()`, `OdxJson.Parse(text)`). Bytes are spliced into the request verbatim — the
fastest path. Either way the keys inside are Odoo field names and are sent exactly as given.

### Choosing v1 or v2

Prefer choosing explicitly. To pick automatically, `await client.SupportsV2Async(url)` (or
`session.IsSupportedAsync()`) probes `/v2/odoo/version` once and caches the answer per URL. Don't
probe before every call. A `-32006` from a server you know runs 19+ is a database/`dbfilter`
problem, not a version problem.

### What's different on v2

- **Database selection follows `dbfilter`.** JSON-2 picks the database by header. On hosts that pick
  the database from the hostname, `url` must be that database's own hostname, or calls fail with
  `OdxJson2UnavailableException`. (v1 is not affected.)
- **Odoo API keys expire.** Keys of non-admin users expire on a schedule; that surfaces as
  `OdxOdooAuthException` — show it to the end user, it is not a proxy misconfiguration.
- **Binary fields** read on Odoo 20+ (v1 and v2 alike) are `{content, filename, size}` objects, not
  bare base64 — see `OdooBinary` below. Empty values are still `false`; datetimes are UTC strings.

---

## Error handling

Failures throw a typed `OdxException`. Cancellation throws `OperationCanceledException`.

| Exception | When |
| --- | --- |
| `OdxAuthException` | proxy auth failed (401 / `-32000`) |
| `OdxBadRequestException` | invalid action / missing `fn_name` (400 / `-32001`,`-32002`) |
| `OdxLicenseException` | proxy license invalid (403 / code `0`) |
| `OdxUpstreamTimeoutException` | Odoo timed out (504 / `-32003`) |
| `OdxUpstreamConnectException` | proxy couldn't reach Odoo (502 / `-32004`) |
| `OdxProxyInternalException` | proxy internal error (500 / `-32005`) |
| `OdxServerException` | any other non-2xx |
| `OdxTransportException` | couldn't reach the proxy (DNS/TCP/TLS, local timeout) |
| **`OdxOdooException`** | **HTTP 200 but Odoo returned a logic error** (see below) |
| `OdxJson2UnavailableException` | v2: no JSON-2 on that Odoo (200 / `-32006`): Odoo ≤ 18 (use v1), or the db isn't selectable on that host (`dbfilter`) |
| `OdxInvalidRequestException` | v2: invalid `model_id`/`method`, or `db`/`api_key` not header-safe (400 / `-32007`); a subclass of `OdxBadRequestException` |

> **The HTTP-200 trap:** odxproxy returns Odoo-side *logic* errors (access errors, validation
> errors, …) as **HTTP 200** with an `error` object in the body. The typed methods detect this and
> throw `OdxOdooException` (with `OdooCode` and raw `RpcData`) — you never have to check for it
> yourself. The base `OdxException` carries `Status`, `RpcCode`, and `RpcData`.

```csharp
try
{
    var ids = await client.ExecuteAsync<long[]>(OdxAction.Unlink, "res.partner", odoo,
        AppJson.Default.Int64Array, paramsJson: """[[999999]]"""u8.ToArray());
}
catch (OdxOdooException ex)      { /* Odoo said no: ex.OdooCode, ex.Message, ex.RpcData */ }
catch (OdxAuthException)         { /* bad proxy key */ }
catch (OperationCanceledException) { /* cancelled */ }
```

`OdxOdooException` has subclasses keyed by Odoo's HTTP status, which v2 always reports as the error
code (v1 only when Odoo itself answered non-2xx). `catch (OdxOdooException)` still catches them all:

| Exception | Code (on HTTP 200) | Meaning |
| --- | --- | --- |
| `OdxOdooAuthException` | `401` | **Odoo** rejected the Odoo API key: invalid, expired, wrong scope, or a password. Not the proxy key (`OdxAuthException`). |
| `OdxOdooAccessException` | `403` | access rights, or a private (`_`-prefixed) method |
| `OdxOdooNotFoundException` | `404` | unknown model/method, or the record doesn't exist |
| `OdxOdooConflictException` | `409` | Odoo lock conflict |
| `OdxOdooValidationException` | `422` | validation/user error, or bad arguments (unknown kwarg, `ids` on an `@api.model` method) |
| `OdxOdooServerException` | `5xx` | Odoo server error |

Every Odoo error keeps the raw `error.data` in `RpcData`; `OdooErrorName` exposes `data.name`
(e.g. `odoo.exceptions.ValidationError`) for finer branching. Never parse `data.debug`.

> **Code `0` is not a license error.** Odoo 19+ returns every `/jsonrpc` error with code `0` on
> HTTP 200; that is an `OdxOdooException`. Only an HTTP 403 is `OdxLicenseException`.

**Retries.** The client never retries on its own. If you add a retry loop, retry only these:
`OdxUpstreamConnectException` (`-32004`) and `OdxOdooConflictException` (`409`), with backoff; and
`OdxUpstreamTimeoutException` (`-32003`) **only for idempotent calls** (reads), because the upstream
call may still have run. Nothing else is retryable.

## Cancellation

Pass a `CancellationToken`; cancelling aborts the in-flight request and surfaces
`OperationCanceledException`. Handy when a view is dismissed mid-request.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var res = await client.ExecuteAsync<Partner[]>(
    OdxAction.SearchRead, "res.partner", odoo, AppJson.Default.PartnerArray, cancellationToken: cts.Token);
```

## Odoo wire helpers (opt-in)

Namespace `TerraKernel.OdxClient.Json` ships `System.Text.Json` converters for Odoo's wire quirks. They are
**opt-in** — add them to your own `JsonSerializerOptions`; they are never applied implicitly and
never live in the Rust core.

- **`Many2One` + `Many2OneConverter`** — reads Odoo's `[id, name]` (or `false` when unset); **writes
  the bare integer id** (or `false`), matching Odoo's write semantics.
- **`OdooFalseAsNullStringConverter`** — reads Odoo's `false` (an unset scalar) as `null`.
- **`OdooBinary` + `OdooBinaryConverter`** — an Odoo binary field. Reads the Odoo 20+
  `{content, filename, size}` object, a bare base64 string (Odoo ≤ 19), or `false` (→ `null`);
  writes a bare base64 string, or `{content, filename}` when a file name is set.

```csharp
var opts = new JsonSerializerOptions();
opts.Converters.Add(new Many2OneConverter());
Many2One partner = JsonSerializer.Deserialize<Many2One>("""[7,"Acme"]""", opts); // {Id=7, Name="Acme"}
```

---

## Build & test

```bash
# Rust core
cargo build --release          # → odxclient.dll (+ .dll.lib)
cargo test                     # in-crate tests (async round-trip, off-thread delivery, cancel, routing)

# .NET binding + end-to-end smoke test (loads the real DLL against a local mock server)
dotnet build dotnet/Odx.Client/Odx.Client.csproj -c Release
dotnet run --project dotnet/Odx.Client.SmokeTest/Odx.Client.SmokeTest.csproj -c Release

# v2 offline tests: a wire-JSON snapshot per method + error mapping per code (mock server)
dotnet run --project dotnet/Odx.Client.V2Test/Odx.Client.V2Test.csproj -c Release

# v2 live integration test (real proxy + Odoo 19+). Keys come from env vars only;
# see the header of dotnet/Odx.Client.V2LiveTest/Program.cs.
dotnet run --project dotnet/Odx.Client.V2LiveTest/Odx.Client.V2LiveTest.csproj -c Release
```

## Status

Published on NuGet as `TerraKernel.OdxClient`; changes are listed in [`CHANGELOG.md`](CHANGELOG.md).
The binding overhead is measured in [`BENCHMARK.md`](BENCHMARK.md), and C/C++ consumers can use the
generated header [`include/odxclient.h`](include/odxclient.h). See [`IMPLEMENTATION-PLAN.md`](IMPLEMENTATION-PLAN.md) for the design and the
[spec](odxproxy-dotnet-client-prompt.md) for the non-negotiable constraints.

## License

[MIT](LICENSE) © 2026 TerraKernel

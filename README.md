# ODXProxyClient-Net

A fast, native **.NET client for [odxproxy](https://github.com/terrakernel/theodxproxy)** — a
Rust/Axum proxy that fronts Odoo's JSON-RPC API.

The performance-critical core (connection handling, the HTTP round-trip, retries, cancellation) is
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
    "search_read", "res.partner", odoo, AppJson.Default.PartnerArray);
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
using Odx.Client;

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

// 4) Call. `params`/`keyword` are raw Odoo JSON — this client is a thin passthrough.
Partner[]? partners = await client.ExecuteAsync(
    action:      "search_read",
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
            "search_read", "res.partner", _odoo, AppJson.Default.PartnerArray);
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
| License | `GetLicenseAsync` | GET `/_/license` |
| Build/version | `GetAboutAsync` | GET `/_/about` |
| Prometheus | `GetMetricsAsync` | GET `/_/metrics` |

**Three call styles for `execute`/`version`:**

```csharp
// (a) Structured + typed — recommended. Builds the request envelope for you and
//     deserializes `result` into T.
Partner[]? a = await client.ExecuteAsync<Partner[]>(
    "search_read", "res.partner", odoo, AppJson.Default.PartnerArray, paramsJson, keywordJson);

// (b) Raw body + typed — you supply the full request JSON, we deserialize the result.
byte[] body = OdxRequestBuilder.BuildExecute("search", "res.partner", odoo, paramsJson);
long[]? b = await client.ExecuteAsync<long[]>(body, AppJson.Default.Int64Array);

// (c) Raw body + raw response — you own both sides (advanced).
OdxResponse c = await client.ExecuteAsync(body);
// c.Status (OdxStatus), c.HttpStatus (ushort), c.Body (byte[] — the JSON-RPC envelope)
```

The GET endpoints have a typed overload too (`GetAboutAsync<T>(AppJson.Default.AboutInfo)`) and a
raw overload returning `OdxResponse`.

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

> **The HTTP-200 trap:** odxproxy returns Odoo-side *logic* errors (access errors, validation
> errors, …) as **HTTP 200** with an `error` object in the body. The typed methods detect this and
> throw `OdxOdooException` (with `OdooCode` and raw `RpcData`) — you never have to check for it
> yourself. The base `OdxException` carries `Status`, `RpcCode`, and `RpcData`.

```csharp
try
{
    var ids = await client.ExecuteAsync<long[]>("unlink", "res.partner", odoo,
        AppJson.Default.Int64Array, paramsJson: """[[999999]]"""u8.ToArray());
}
catch (OdxOdooException ex)      { /* Odoo said no: ex.OdooCode, ex.Message, ex.RpcData */ }
catch (OdxAuthException)         { /* bad proxy key */ }
catch (OperationCanceledException) { /* cancelled */ }
```

## Cancellation

Pass a `CancellationToken`; cancelling aborts the in-flight request and surfaces
`OperationCanceledException`. Handy when a view is dismissed mid-request.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var res = await client.ExecuteAsync<Partner[]>(
    "search_read", "res.partner", odoo, AppJson.Default.PartnerArray, cancellationToken: cts.Token);
```

## Odoo wire helpers (opt-in)

Namespace `Odx.Client.Json` ships `System.Text.Json` converters for Odoo's wire quirks. They are
**opt-in** — add them to your own `JsonSerializerOptions`; they are never applied implicitly and
never live in the Rust core.

- **`Many2One` + `Many2OneConverter`** — reads Odoo's `[id, name]` (or `false` when unset); **writes
  the bare integer id** (or `false`), matching Odoo's write semantics.
- **`OdooFalseAsNullStringConverter`** — reads Odoo's `false` (an unset scalar) as `null`.

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
```

## Status

The Rust core and the .NET binding are feature-complete and covered by an end-to-end smoke test.
Still on the roadmap: an overhead benchmark, a generated C header (`cbindgen`) for C/C++ consumers,
and NuGet packaging. See [`IMPLEMENTATION-PLAN.md`](IMPLEMENTATION-PLAN.md) for the design and the
[spec](odxproxy-dotnet-client-prompt.md) for the non-negotiable constraints.

## License

Copyright © TerraKernel. License TBD — add a `LICENSE` file before distributing.

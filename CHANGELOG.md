# Changelog

All notable changes to `TerraKernel.OdxClient`. Versions follow [SemVer](https://semver.org/).

## 1.1.0 — unreleased

Adds ODXProxy's **v2 API** (`/v2/odoo/*`, Odoo's JSON-2, Odoo 19+). Additive: the v1 API is
unchanged, and existing code compiles and behaves as before.

### Added

- **`OdxSessionV2`**, created with `OdxClient.ForInstanceV2(url, db, apiKey, context?)` (or from a
  v1 `OdooInstance`; its `UserId` is ignored). It has no user id and a session-level default
  `context` that is merged into every call; a call's own keys win.
- v2 methods with v1's names: `SearchAsync`, `SearchReadAsync<T>`, `SearchCountAsync`, `ReadAsync<T>`,
  `FieldsGetAsync<T>`, `CreateAsync` (always sends `vals_list` as an array and returns `long[]`),
  `CreateOneAsync`, `WriteAsync`, `UnlinkAsync`, and a generic named-only `CallMethodAsync<T>`.
  `kwargs` keys are Odoo's Python parameter names, sent as-is; unset arguments are omitted; `ids`
  never goes to `@api.model` methods.
- `OdxJson`: a JSON argument that takes either a `JsonNode` or pre-serialized UTF-8 bytes (spliced
  verbatim).
- `OdxClient.GetVersionV2Async(url)` → `OdxVersionInfoV2` (`{version_info, version}`), and
  `OdxClient.SupportsV2Async(url)` (also `OdxSessionV2.IsSupportedAsync()`), a v1/v2 picker cached
  per URL.
- Raw v2 layer: `OdxClient.ExecuteV2Async(body)` / `ExecuteV2Async<T>(body, type)` /
  `GetVersionV2Async(body)` and `OdxRequestBuilder.BuildExecuteV2(...)`.
- Error types:
  - `OdxJson2UnavailableException` (`-32006`);
  - `OdxInvalidRequestException` (`-32007`, a subclass of `OdxBadRequestException`);
  - `OdxOdooException` subclasses keyed by Odoo's HTTP status: `OdxOdooAuthException` (401, distinct
    from the proxy's `OdxAuthException`), `OdxOdooAccessException` (403), `OdxOdooNotFoundException`
    (404), `OdxOdooConflictException` (409), `OdxOdooValidationException` (422) and
    `OdxOdooServerException` (5xx);
  - `OdxOdooException.OdooErrorName` (Odoo's `error.data.name`).
- `TerraKernel.OdxClient.Json.OdooBinary` + `OdooBinaryConverter` for Odoo 20 binary fields
  (`{content, filename, size}`, or a bare base64 string on Odoo ≤ 19).
- Native core: `odx_execute_v2` and `odx_get_version_v2` exports (also in `include/odxclient.h`).

### Changed

- `OdxOdooException` and `OdxBadRequestException` are no longer `sealed`, so the new types can
  derive from them. `catch (OdxOdooException)` and `catch (OdxBadRequestException)` still catch
  everything they caught before.
- An Odoo error whose code is an Odoo HTTP status (`401`/`403`/`404`/`409`/`422`/`5xx`) now throws
  the matching `OdxOdooException` subclass instead of `OdxOdooException` itself. On v1 this only
  happens when Odoo itself answered non-2xx.

### Checked: the "code 0" bug

Odoo 19+ returns every `/jsonrpc` error with JSON-RPC code `0` on HTTP 200. Other SDKs
misclassified that as a license error. This SDK does not: it classifies by HTTP status, so code
`0` on HTTP 200 is an `OdxOdooException`, and only HTTP 403 is `OdxLicenseException`. A test now
pins this.

## 1.0.0 — 2026-08-08

First release: Rust C-ABI core (`odxclient.dll`) and the .NET binding for ODXProxy's v1 API
(`/api/odoo/execute`, `/api/odoo/version`, `/_/license`, `/_/about`, `/_/metrics`).

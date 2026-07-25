# Project: odxproxy .NET Client Library (Rust core, C ABI)

## Context

Build a reusable, high-performance native client library that lets any .NET
project talk to **odxproxy** (a Rust/Axum proxy for Odoo's RPC protocol).
This library will initially back a WinUI 3 point-of-sale application on
Windows 11, but must remain a standalone, general-purpose library usable by
any future .NET project.

## Non-negotiable constraints

1. **Performance is priority #1.** Smallest memory footprint and shortest
   CPU cycle count wins over convenience, ergonomics, or idiomatic-ness on
   *every* tradeoff decision. State the tradeoff explicitly if a
   performance-optimal choice hurts readability/maintainability.
2. **No CLR/managed runtime in the client logic.** The library core must be
   written in **Rust**, compiled to a C-ABI-compatible dynamic library
   (`crate-type = ["cdylib"]`). All connection handling, serialization,
   retries, and odxproxy protocol logic live here — none of it should ever
   run inside the .NET GC/JIT.
3. **Raw Odoo semantics only — no typed model/ORM abstraction layer.**
   Expose thin pass-through calls matching odxproxy's own surface
   (`search_read`, `fields_get`, `create`, `write`, `unlink`, etc.). Do NOT
   build typed domain models (Order, Product, etc.) into this library —
   Odoo's dynamic schema (custom fields, Studio changes, module-added
   columns) makes a typed layer a maintenance trap. Domain modeling belongs
   in the consuming application, not this library.
4. **JSON as the FFI marshaling format** for arbitrary/dynamic field data
   crossing the boundary (serde_json on the Rust side, System.Text.Json on
   the .NET side), unless you find a lower-overhead alternative that still
   handles Odoo's dynamic field dictionaries cleanly — flag it if so.
5. **Target platform: Windows 11 only**, MSVC ABI
   (`x86_64-pc-windows-msvc`). No Windows 10 compatibility, no legacy
   fallback code paths.
6. **Developed and compiled natively on Windows.** MSVC toolchain (Visual
   Studio Build Tools, "Desktop development with C++" workload) plus
   `rustup target add x86_64-pc-windows-msvc` is available directly on the
   dev machine. No cross-compilation tooling needed — build, test, and
   iterate all happen on the same Windows box.
7. **Consumer will use Native AOT** on the .NET side eventually — keep the
   exposed C ABI simple and reflection-free (flat functions, opaque
   handles/pointers, no COM, no complex marshaling attributes required)
   so it stays AOT-friendly on the consuming side.

## Deliverables

1. Rust crate (`cdylib`) exposing a flat `extern "C"` API surface for:
   - Connection/session lifecycle (connect, disconnect, handle
     creation/teardown)
   - Raw RPC calls mirroring odxproxy's protocol (search_read, fields_get,
     create, write, unlink, and whatever else odxproxy currently exposes —
     inspect the odxproxy source/API before finalizing the surface)
   - Error reporting across the FFI boundary (error codes + optional
     message buffer, not exceptions/panics — panics must never unwind
     across the FFI boundary; catch and convert to error codes)
   - Async handling strategy: decide and justify polling vs callback-based
     async at the C ABI boundary, since C ABIs have no native async story
2. A C header (`.h`) describing the exposed API, generated via `cbindgen`
   or hand-maintained — your call, optimize for low maintenance overhead.
3. Build scripts/config for compiling natively on Windows to
   `x86_64-pc-windows-msvc`, producing `.dll` + `.dll.lib`.
4. A minimal C# P/Invoke binding layer (thin — just the interop
   declarations, no business logic) demonstrating the library is
   consumable from .NET, structured so it could later be packaged as a
   standalone nupkg.
5. Benchmarks or at least a clear note on expected call overhead per RPC
   round-trip through the FFI boundary vs. calling odxproxy directly from
   pure Rust, so performance regressions are visible early.

## Explicitly out of scope

- Any typed/ORM-style domain model layer
- WinUI 3 / XAML / UI code of any kind
- Web technology of any kind (no Tauri, no webview, no JS)
- Windows 10 support or legacy API fallbacks
- .NET Framework (target modern .NET only, AOT-compatible)

## Open questions to resolve during development (ask before assuming)

- Exact current odxproxy RPC surface/protocol (inspect the odxproxy repo
  directly rather than guessing at its API shape)
- Sync-blocking vs callback vs polling async model at the FFI boundary
- Connection pooling/reuse strategy for the Rust client against odxproxy
- Whether JSON is truly the lowest-overhead viable serialization format
  for this boundary, or whether something like MessagePack/flatbuffers is
  worth it given the performance priority

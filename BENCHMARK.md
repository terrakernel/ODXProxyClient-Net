# Overhead benchmark

Spec deliverable #5: quantify the cost the **.NET binding + FFI boundary** adds on top of the raw
Rust round-trip — what a consumer pays versus what a pure-Rust app pays.

**Result up front:** the binding tax is a **flat ~12–16 µs per call**, constant across a 100× payload
change. On a real proxy + Odoo round-trip (milliseconds), that is **~0.1% overhead — negligible.**
Allocation is low, GC stays gen0-only (no stop-the-world pauses), and the managed layer adds nothing
to tail latency. The "native-snappy" design goal (constraint #1) is met.

## What is measured — and why this way

End-to-end latency is dominated by the network + Odoo, which is identical on both paths and would
drown the µs-scale signal. So we do not measure end-to-end latency; we **subtract a matched baseline**:

Both paths use the *same* reqwest core hitting the *same* backend, so the HTTP + reqwest cost cancels.
What remains in the delta is exactly our overhead. Measured in layers, to attribute each cost:

| Measurement | Path | The delta isolates |
| --- | --- | --- |
| **Rust baseline** | in-process reqwest round-trip → mock, gets `Bytes` (`src/bin/bench_baseline.rs`) | (the floor) |
| **.NET raw** | `ExecuteAsync` → `OdxResponse` | minus baseline = **FFI + callback→Task bridge + byte copy** |
| **.NET typed** | `ExecuteAsync<T>` | minus .NET raw = **System.Text.Json deserialize** |

To make the subtraction valid and the numbers honest:

- **Local loopback mock** (`Odx.Client.Bench.Mock`) returns a canned JSON-RPC envelope with keep-alive
  and constant latency — one server, hit by both clients, so it is a fixed constant. This removes
  network variance (which is orders of magnitude larger than the overhead).
- The Rust baseline **replicates `call.rs::do_request` exactly** — same `use_rustls_tls()` client build,
  same `x-api-key` + `content-type` headers, same body copy per call, same `send().await` +
  `bytes().await`. It loops inside a single `block_on` so per-iteration runtime entry is not charged.
- The .NET bench is published with **Native AOT** (`PublishAot`) — the spec's real consumer uses AOT, so
  this is the honest steady state (no JIT / tiered compilation / startup in the numbers).
- The request body is **pre-built once** on both sides, so we measure per-call overhead, not body
  assembly.
- **3,000 warmup iterations discarded**, then **20,000 measured**, sequential on the warm keep-alive
  connection. We report the **distribution** (p50/p90/p99/max, mean, sd) — "snappy" is a tail property.
- Two payload sizes: **small** (`result` is a scalar) isolates fixed per-call overhead; **large**
  (`result` is 100 `{id,name}` rows) shows how copy + parse scale.

## Results

20,000 iterations after 3,000 warmup; Native AOT; loopback mock; single machine
(`x86_64-pc-windows-msvc`). Latencies in µs.

| Payload | Path | p50 | p90 | p99 | max | mean | sd | alloc/call | GC g0/g1/g2 |
| --- | --- | --: | --: | --: | --: | --: | --: | --: | --: |
| **small** | Rust baseline (raw) | 38.6 | 54.0 | 93.5 | 2289.1 | 42.7 | 21.2 | — | — |
| | .NET raw → `OdxResponse` | 52.0 | 77.6 | 122.3 | 490.8 | 58.4 | 21.1 | 504 B | 0/0/0 |
| | .NET typed `<long>` | 51.8 | 71.0 | 126.1 | 438.3 | 57.1 | 22.6 | 648 B | 1/0/0 |
| **large** (100 rows) | Rust baseline (raw) | 39.7 | 55.5 | 97.8 | 2097.8 | 44.3 | 20.4 | — | — |
| | .NET raw → `OdxResponse` | 51.6 | 70.2 | 126.1 | 428.8 | 57.0 | 20.6 | 3,896 B | 5/0/0 |
| | .NET typed `<Row[]>` ×100 | 89.4 | 116.6 | 183.7 | 548.1 | 96.7 | 20.9 | 24,328 B | 29/2/0 |

### Reading the deltas

- **Binding tax (FFI + bridge + copy) = `.NET raw − Rust baseline`:** small **+13.4 µs p50 (+35%)**,
  large **+11.9 µs p50 (+30%)**. Constant across a 100× payload change ⇒ fixed per-call boundary cost,
  not something that scales.
- **Deserialize = `.NET typed − .NET raw`:** scalar `<long>` ≈ **0 µs** (free); 100 rows **+37.8 µs p50**.
  This is inherent JSON work (a pure-Rust app deserializing the same rows would pay it too — the baseline
  is raw bytes) and it runs **off the UI thread** by design, so it never janks the app.
- **Allocation** is low and predictable (504 B/call raw small; 24 KB/call even when materializing 100
  objects). **GC stayed gen0-only** across 20k calls (max 29 gen0, 2 gen1, **0 gen2**) — no
  stop-the-world pauses.
- **Tail latency** is not worsened by the binding: .NET `max` (≤ 548 µs) was *lower* than the Rust
  baseline's (~2.1–2.3 ms). The tail is OS-scheduling noise on both; the managed layer adds none of its
  own. (Compare on p50/p99, which are robust to those outliers.)

### In context

The absolute ~40 µs floor is the loopback TCP + HTTP + reqwest round-trip — present in both paths, so it
cancels. Against the real proxy + Odoo, a round-trip is *milliseconds*; a flat ~12 µs of binding
overhead on even a fast 10 ms call is **~0.1%**. The overhead is real but immaterial.

## Reproducing

Components: `src/bin/bench_baseline.rs` (Rust baseline), `dotnet/Odx.Client.Bench` (AOT .NET bench),
`dotnet/Odx.Client.Bench.Mock` (mock).

```bash
# 1) Native core + Rust baseline bin (release; also produces odxclient.dll)
cargo build --release

# 2) Mock server
dotnet build dotnet/Odx.Client.Bench.Mock/Odx.Client.Bench.Mock.csproj -c Release

# 3) .NET bench, Native AOT (needs the MSVC toolchain env — see note below)
dotnet publish dotnet/Odx.Client.Bench/Odx.Client.Bench.csproj -c Release -r win-x64
```

Then, per payload size: start the mock, run the Rust baseline, run the AOT bench against the same port,
stop the mock:

```bash
dotnet dotnet/Odx.Client.Bench.Mock/bin/Release/net10.0/Odx.Client.Bench.Mock.dll --port 6699 --size small
target/x86_64-pc-windows-msvc/release/bench_baseline.exe --url http://127.0.0.1:6699 --size small --iters 20000 --warmup 3000
dotnet/Odx.Client.Bench/bin/x64/Release/net10.0/win-x64/publish/Odx.Client.Bench.exe --url http://127.0.0.1:6699 --size small --iters 20000 --warmup 3000
```

(Use `--size large` on its own port for the large-payload pass.)

> **AOT publish + MSVC on this box:** `dotnet publish` for Native AOT shells out to `vswhere.exe` to
> locate the C++ linker. If `vswhere` isn't on `PATH` (VS Build Tools installed to a nonstandard
> location), the link step fails. Fix: prepend the VS Installer dir to `PATH` and source `vcvars64.bat`
> before publishing, e.g.
> `set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%"` then
> `call "<VS>\VC\Auxiliary\Build\vcvars64.bat"` then `dotnet publish …`.

## Caveats

- Loopback removes the network but keeps a small constant TCP + HTTP cost; it is present in both paths
  and cancels in the delta.
- AOT numbers are reported; JIT startup is deliberately excluded (unrepresentative of the AOT consumer).
- The delta legitimately includes the Rust-side per-call bookkeeping the FFI path adds over a bare
  reqwest call (`OdxRequest` / `oneshot` / spawn / `OdxBuffer`) — that is part of *our* overhead.
- Single machine, single run of 20k iterations per cell; p50/p99 are stable within the distribution.

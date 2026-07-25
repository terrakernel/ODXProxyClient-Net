//! Pure-Rust baseline for the overhead benchmark: the same reqwest round-trip the
//! FFI core performs (`do_request` in call.rs) — same client build, same headers,
//! same `send().await` + `bytes().await` — but with NO FFI boundary, no callback
//! bridge, no .NET. Loops inside a single `block_on` so per-iteration runtime entry
//! isn't charged. The `.NET path − this baseline` delta is the binding's overhead.
//!
//! Usage: bench_baseline --url http://127.0.0.1:6699 --iters 20000 --warmup 2000 --size small

use std::time::Instant;

use reqwest::header::{CONTENT_TYPE, HeaderName, HeaderValue};

fn main() {
    let mut url = "http://127.0.0.1:6699".to_string();
    let mut iters = 20_000usize;
    let mut warmup = 2_000usize;
    let mut size = "small".to_string();

    let a: Vec<String> = std::env::args().collect();
    let mut i = 1;
    while i + 1 < a.len() {
        match a[i].as_str() {
            "--url" => url = a[i + 1].clone(),
            "--iters" => iters = a[i + 1].parse().expect("iters"),
            "--warmup" => warmup = a[i + 1].parse().expect("warmup"),
            "--size" => size = a[i + 1].clone(),
            _ => {}
        }
        i += 2;
    }

    // Mirror the core: multi-thread tokio runtime + a rustls reqwest client.
    let rt = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .expect("runtime");
    let client = reqwest::Client::builder()
        .use_rustls_tls()
        .build()
        .expect("client");

    let x_api_key = HeaderName::from_static("x-api-key");
    let api_key = HeaderValue::from_static("bench-key");
    let app_json = HeaderValue::from_static("application/json");
    let full_url = format!("{url}/api/odoo/execute");
    // A representative execute body (the mock ignores it; matching the .NET request shape).
    let body: Vec<u8> = br#"{"id":"1","action":"search_count","model_id":"res.partner","keyword":{},"params":[[]],"odoo_instance":{"url":"http://x","user_id":1,"db":"d","api_key":"k"}}"#.to_vec();

    let mut samples = rt.block_on(async {
        for _ in 0..warmup {
            let _ = do_one(&client, &x_api_key, &api_key, &app_json, &full_url, &body).await;
        }
        let mut samples = Vec::with_capacity(iters);
        for _ in 0..iters {
            let t = Instant::now();
            let n = do_one(&client, &x_api_key, &api_key, &app_json, &full_url, &body).await;
            let e = t.elapsed();
            std::hint::black_box(n);
            samples.push(e.as_nanos() as u64);
        }
        samples
    });

    report(&format!("RUST baseline (raw round-trip) [{size}]"), &mut samples);
}

async fn do_one(
    client: &reqwest::Client,
    x_api_key: &HeaderName,
    api_key: &HeaderValue,
    app_json: &HeaderValue,
    url: &str,
    body: &[u8],
) -> usize {
    // Same header set + body copy as call.rs::do_request (the core to_vec()s the body
    // once per call; cloning here mirrors that).
    let resp = client
        .post(url)
        .header(x_api_key.clone(), api_key.clone())
        .header(CONTENT_TYPE, app_json.clone())
        .body(body.to_vec())
        .send()
        .await
        .expect("send");
    let bytes = resp.bytes().await.expect("bytes");
    bytes.len()
}

fn report(label: &str, s: &mut [u64]) {
    s.sort_unstable();
    let n = s.len();
    let pct = |p: f64| -> f64 {
        let idx = ((p / 100.0) * n as f64) as usize;
        s[idx.min(n - 1)] as f64 / 1000.0
    };
    let sum: u128 = s.iter().map(|&x| x as u128).sum();
    let mean = sum as f64 / n as f64;
    let var = s.iter().map(|&x| (x as f64 - mean).powi(2)).sum::<f64>() / n as f64;
    let sd = var.sqrt();
    println!("{label}  n={n}");
    println!(
        "  p50={:.2}us  p90={:.2}us  p99={:.2}us  max={:.2}us  mean={:.2}us  sd={:.2}us",
        pct(50.0),
        pct(90.0),
        pct(99.0),
        *s.last().unwrap() as f64 / 1000.0,
        mean / 1000.0,
        sd / 1000.0
    );
}

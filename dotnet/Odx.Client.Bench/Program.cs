using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Odx.Client;
using Odx.Client.Interop;

// Native-AOT overhead bench. Args: --url <base> --iters N --warmup W --size small|large
string url = "http://127.0.0.1:6699";
int iters = 20_000, warmup = 2_000;
string size = "small";
for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--url": url = args[i + 1]; break;
        case "--iters": iters = int.Parse(args[i + 1]); break;
        case "--warmup": warmup = int.Parse(args[i + 1]); break;
        case "--size": size = args[i + 1]; break;
    }
}

using var client = OdxClient.Create(url, "bench-key", defaultTimeoutSecs: 30);
var odoo = new OdooInstance { Url = "http://x", UserId = 1, Db = "d", ApiKey = "k" };

// Pre-build the request body ONCE (the mock ignores it) so we measure per-call
// overhead, not body assembly — matching the Rust baseline's reused body.
var action = size == "large" ? OdxAction.SearchRead : OdxAction.SearchCount;
ReadOnlyMemory<byte> body = OdxRequestBuilder.BuildExecute(action, "res.partner", odoo).AsMemory();

Console.WriteLine($"NET AOT bench  url={url}  size={size}  warmup={warmup}  iters={iters}\n");

// Raw path: FFI + callback bridge + byte copy, no deserialize.
await Measure("NET raw  (ExecuteAsync -> OdxResponse)", warmup, iters, async () =>
{
    OdxResponse r = await client.ExecuteAsync(body);
    if (r.Status != OdxStatus.Ok) throw new Exception($"status={r.Status}");
});

// Typed path: raw path + System.Text.Json deserialize into T.
if (size == "large")
{
    JsonTypeInfo<Row[]> ti = BenchJson.Default.RowArray;
    await Measure("NET typed (ExecuteAsync<Row[]>, 100 rows)", warmup, iters, async () =>
    {
        Row[]? rows = await client.ExecuteAsync<Row[]>(body, ti);
        if (rows is not { Length: 100 }) throw new Exception($"rows={rows?.Length}");
    });
}
else
{
    JsonTypeInfo<long> ti = BenchJson.Default.Int64;
    await Measure("NET typed (ExecuteAsync<long>)", warmup, iters, async () =>
    {
        long v = await client.ExecuteAsync<long>(body, ti);
        if (v != 2861) throw new Exception($"value={v}");
    });
}

return 0;

static async Task Measure(string label, int warmup, int iters, Func<Task> call)
{
    for (int i = 0; i < warmup; i++) await call();

    var ticks = new long[iters];
    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
    long alloc0 = GC.GetTotalAllocatedBytes(precise: true);

    for (int i = 0; i < iters; i++)
    {
        long s = Stopwatch.GetTimestamp();
        await call();
        ticks[i] = Stopwatch.GetTimestamp() - s;
    }

    long alloc1 = GC.GetTotalAllocatedBytes(precise: true);
    int d0 = GC.CollectionCount(0) - g0, d1 = GC.CollectionCount(1) - g1, d2 = GC.CollectionCount(2) - g2;

    double nsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
    Array.Sort(ticks);
    double Us(double p) => ticks[Math.Min((int)(p / 100.0 * iters), iters - 1)] * nsPerTick / 1000.0;
    double mean = 0;
    for (int i = 0; i < iters; i++) mean += ticks[i];
    mean = mean / iters * nsPerTick / 1000.0;
    double var = 0;
    for (int i = 0; i < iters; i++) { double u = ticks[i] * nsPerTick / 1000.0 - mean; var += u * u; }
    double sd = Math.Sqrt(var / iters);

    Console.WriteLine(label + $"  n={iters}");
    Console.WriteLine($"  p50={Us(50):F2}us  p90={Us(90):F2}us  p99={Us(99):F2}us  max={ticks[^1] * nsPerTick / 1000.0:F2}us  mean={mean:F2}us  sd={sd:F2}us");
    Console.WriteLine($"  alloc/call={(alloc1 - alloc0) / (double)iters:F0} B   GC gen0/1/2 = {d0}/{d1}/{d2}\n");
}

internal sealed record Row(long Id, string Name);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(Row[]))]
internal partial class BenchJson : JsonSerializerContext;

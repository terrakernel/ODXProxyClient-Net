using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraKernel.OdxClient;
using TerraKernel.OdxClient.Interop;

// Same gitignored config as the other real-instance tests.
string configPath = args.Length > 0 ? args[0] : "dotnet/Odx.Client.RealTest/realtest.local.json";
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {configPath}");
    return 2;
}

RealConfig cfg;
try
{
    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    cfg = JsonSerializer.Deserialize<RealConfig>(File.ReadAllText(configPath), opts)
          ?? throw new InvalidOperationException("config deserialized to null");
}
catch (Exception e)
{
    Console.Error.WriteLine($"Failed to read {configPath}: {e.Message}");
    return 2;
}

if (HasPlaceholder(cfg.ProxyUrl) || HasPlaceholder(cfg.ProxyApiKey) ||
    HasPlaceholder(cfg.Odoo.Url) || HasPlaceholder(cfg.Odoo.ApiKey) || HasPlaceholder(cfg.Odoo.Db))
{
    Console.Error.WriteLine($"{configPath} still has REPLACE_ME placeholders — fill in real values first.");
    return 2;
}

const string model = "res.partner";
Console.WriteLine($"Proxy: {cfg.ProxyUrl}   Odoo: {cfg.Odoo.Url}");
Console.WriteLine("Deliberately triggering each error path; asserting the typed exception.\n");

int failures = 0;

// The good client + a valid Odoo instance, used where only ONE thing is deliberately wrong.
using var client = OdxClient.Create(cfg.ProxyUrl, cfg.ProxyApiKey, defaultTimeoutSecs: 20);
var odoo = new OdooInstance { Url = cfg.Odoo.Url, Db = cfg.Odoo.Db, UserId = cfg.Odoo.UserId, ApiKey = cfg.Odoo.ApiKey };

// 1) Unreachable PROXY -> transport failure (no HTTP response at all).
await Expect<OdxTransportException>("unreachable proxy  ->  OdxTransportException", async () =>
{
    using var bad = OdxClient.Create("http://127.0.0.1:1", cfg.ProxyApiKey, defaultTimeoutSecs: 5);
    await bad.ExecuteAsync(OdxAction.SearchCount, model, odoo, ErrJson.Default.Int64, paramsJson: U8("[[]]"));
});

// 2) Wrong PROXY api key -> 401 / -32000.
await Expect<OdxAuthException>("bad proxy api key  ->  OdxAuthException (-32000)", async () =>
{
    using var bad = OdxClient.Create(cfg.ProxyUrl, "definitely-not-the-key", defaultTimeoutSecs: 15);
    await bad.ExecuteAsync(OdxAction.SearchCount, model, odoo, ErrJson.Default.Int64, paramsJson: U8("[[]]"));
});

// 3) Invalid action -> 400 / -32001. (Uses the raw string builder to bypass the enum.)
await Expect<OdxBadRequestException>("invalid action  ->  OdxBadRequestException (-32001)", async () =>
{
    byte[] body = OdxRequestBuilder.BuildExecute("no_such_action", model, odoo, paramsJson: U8("[[]]"));
    await client.ExecuteAsync<long>(body, ErrJson.Default.Int64);
});

// 4) call_method with no fn_name, PROXY-side -> 400 / -32002. (String builder skips the guard.)
await Expect<OdxBadRequestException>("call_method w/o fn_name (proxy)  ->  OdxBadRequestException (-32002)", async () =>
{
    byte[] body = OdxRequestBuilder.BuildExecute("call_method", model, odoo, paramsJson: U8("[[1]]"));
    await client.ExecuteAsync<long>(body, ErrJson.Default.Int64);
});

// 5) call_method with no fn_name, CLIENT-side -> ArgumentException, NO round trip.
//    (This is the win the OdxAction enum buys: caught before the network.)
ExpectSync<ArgumentException>("call_method w/o fn_name (enum)  ->  ArgumentException, no round trip", () =>
{
    _ = OdxRequestBuilder.BuildExecute(OdxAction.CallMethod, model, odoo, paramsJson: U8("[[1]]"));
});

// 6) Unreachable Odoo (proxy can't connect upstream) -> 502 / -32004.
await Expect<OdxUpstreamConnectException>("unreachable Odoo host  ->  OdxUpstreamConnectException (-32004)", async () =>
{
    var badOdoo = new OdooInstance { Url = "http://127.0.0.1:1", Db = odoo.Db, UserId = odoo.UserId, ApiKey = odoo.ApiKey };
    await client.ExecuteAsync(OdxAction.SearchCount, model, badOdoo, ErrJson.Default.Int64, paramsJson: U8("[[]]"));
});

// 7) Odoo-side logic error (bogus method) -> HTTP 200 WITH an error body -> OdxOdooException.
await Expect<OdxOdooException>("bogus Odoo method  ->  OdxOdooException (HTTP-200 trap)", async () =>
{
    await client.ExecuteAsync(OdxAction.CallMethod, model, odoo, ErrJson.Default.JsonElement,
        paramsJson: U8("[[]]"), fnName: "odx_no_such_method_xyz");
});

Console.WriteLine("Not triggered here (need a specific server state, not reproducible read-only):");
Console.WriteLine("  -32003 OdxUpstreamTimeout (slow Odoo op) · -32005 OdxProxyInternal · code 0 OdxLicense");
Console.WriteLine("  (invalid license) · OdxServerException (other non-2xx). OperationCanceledException is");
Console.WriteLine("  covered by the mock SmokeTest cancellation case.\n");

if (failures == 0)
{
    Console.WriteLine("ALL ERROR SCENARIOS SCOPED CORRECTLY");
    return 0;
}
Console.WriteLine($"{failures} scenario(s) did NOT map as expected");
return 1;

async Task Expect<TEx>(string name, Func<Task> body) where TEx : Exception
{
    Console.WriteLine($"[{name}]");
    try
    {
        await body();
        failures++;
        Console.WriteLine($"    FAIL: expected {typeof(TEx).Name}, but the call SUCCEEDED\n");
    }
    catch (TEx ex)
    {
        Console.WriteLine($"    PASS: {ex.GetType().Name}{Detail(ex)}\n");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"    FAIL: expected {typeof(TEx).Name}, got {ex.GetType().Name}: {ex.Message}\n");
    }
}

void ExpectSync<TEx>(string name, Action body) where TEx : Exception
{
    Console.WriteLine($"[{name}]");
    try
    {
        body();
        failures++;
        Console.WriteLine($"    FAIL: expected {typeof(TEx).Name}, but nothing was thrown\n");
    }
    catch (TEx)
    {
        Console.WriteLine($"    PASS: {typeof(TEx).Name} (thrown before any network call)\n");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"    FAIL: expected {typeof(TEx).Name}, got {ex.GetType().Name}: {ex.Message}\n");
    }
}

static string Detail(Exception ex) => ex is OdxException oe
    ? $"  (status={oe.Status}" + (oe.RpcCode is { } c ? $", rpcCode={c}" : "") + ")"
    : "";

static bool HasPlaceholder(string? s) => s is null || s.Contains("REPLACE", StringComparison.OrdinalIgnoreCase);

static byte[] U8(string s) => Encoding.UTF8.GetBytes(s);

internal sealed record RealConfig(string ProxyUrl, string ProxyApiKey, OdooCfg Odoo, string? Model = null);
internal sealed record OdooCfg(string Url, string Db, long UserId, string ApiKey);

[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(JsonElement))]
internal partial class ErrJson : JsonSerializerContext;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Odx.Client;
using Odx.Client.Interop;

// Config path: first CLI arg, else realtest.local.json in the working directory.
string configPath = args.Length > 0 ? args[0] : "realtest.local.json";
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {configPath}");
    Console.Error.WriteLine("Usage: dotnet run --project dotnet/Odx.Client.RealTest -- <config.json>");
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

string model = string.IsNullOrWhiteSpace(cfg.Model) ? "res.partner" : cfg.Model;

Console.WriteLine($"Proxy: {cfg.ProxyUrl}");
Console.WriteLine($"Odoo:  {cfg.Odoo.Url}  db={cfg.Odoo.Db}  user_id={cfg.Odoo.UserId}");
Console.WriteLine();

using var client = OdxClient.Create(cfg.ProxyUrl, cfg.ProxyApiKey, defaultTimeoutSecs: 20);
var odoo = new OdooInstance
{
    Url = cfg.Odoo.Url,
    Db = cfg.Odoo.Db,
    UserId = cfg.Odoo.UserId,
    ApiKey = cfg.Odoo.ApiKey,
};

int failures = 0;

// /_/about and /_/license don't touch Odoo — they check the proxy itself.
await Step("GET /_/about", async () =>
{
    OdxResponse r = await client.GetAboutAsync();
    Console.WriteLine($"    http={r.HttpStatus} status={r.Status}  {Text(r.Body)}");
    Need(r.Status == OdxStatus.Ok);
});

await Step("GET /_/license", async () =>
{
    OdxResponse r = await client.GetLicenseAsync();
    Console.WriteLine($"    http={r.HttpStatus} status={r.Status}  {Text(r.Body)}");
    Need(r.Status == OdxStatus.Ok);
});

// version needs the proxy key + the Odoo URL, but not the Odoo credentials.
await Step("POST /api/odoo/version", async () =>
{
    OdxResponse r = await client.GetVersionAsync(OdxRequestBuilder.BuildVersion(cfg.Odoo.Url));
    Console.WriteLine($"    http={r.HttpStatus} status={r.Status}  {Text(r.Body)}");
    Need(r.Status == OdxStatus.Ok);
});

// The real end-to-end check: full Odoo auth + an actual RPC. Read-only (search_count,
// empty domain). Uses the typed path so an Odoo/proxy error surfaces as a typed exception.
await Step($"POST /api/odoo/execute  search_count({model})", async () =>
{
    long count = await client.ExecuteAsync<long>(
        "search_count", model, odoo, RealJson.Default.Int64,
        paramsJson: "[[]]"u8.ToArray());
    Console.WriteLine($"    {model} count = {count}");
});

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("ALL PASSED (real instance)");
    return 0;
}
Console.WriteLine($"{failures} step(s) FAILED");
return 1;

async Task Step(string name, Func<Task> body)
{
    Console.WriteLine($"[{name}]");
    try
    {
        await body();
        Console.WriteLine("    OK");
    }
    catch (OdxException ex)
    {
        failures++;
        Console.WriteLine($"    FAIL: {ex.GetType().Name} status={ex.Status} code={ex.RpcCode}: {ex.Message}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"    FAIL: {ex.GetType().Name}: {ex.Message}");
    }
    Console.WriteLine();
}

void Need(bool ok)
{
    if (!ok)
        throw new InvalidOperationException("unexpected non-Ok transport status");
}

static bool HasPlaceholder(string? s) => s is null || s.Contains("REPLACE", StringComparison.OrdinalIgnoreCase);

static string Text(byte[] body)
{
    string s = Encoding.UTF8.GetString(body);
    return s.Length > 600 ? s[..600] + "…" : s;
}

internal sealed record RealConfig(string ProxyUrl, string ProxyApiKey, OdooCfg Odoo, string? Model = null);
internal sealed record OdooCfg(string Url, string Db, long UserId, string ApiKey);

[JsonSerializable(typeof(long))]
internal partial class RealJson : JsonSerializerContext;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraKernel.OdxClient;
using TerraKernel.OdxClient.Interop;

// Config path: first CLI arg, else the RealTest config (same gitignored file).
string configPath = args.Length > 0 ? args[0] : "dotnet/Odx.Client.RealTest/realtest.local.json";
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {configPath}");
    Console.Error.WriteLine("Usage: dotnet run --project dotnet/Odx.Client.ActionsTest -- <config.json>");
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

// res.partner is the known-safe test model (only `name` is required for create).
const string model = "res.partner";

Console.WriteLine($"Proxy: {cfg.ProxyUrl}");
Console.WriteLine($"Odoo:  {cfg.Odoo.Url}  db={cfg.Odoo.Db}  user_id={cfg.Odoo.UserId}");
Console.WriteLine($"Model: {model}  (mutations use a throwaway record this test creates)");
Console.WriteLine();

using var client = OdxClient.Create(cfg.ProxyUrl, cfg.ProxyApiKey, defaultTimeoutSecs: 30);
var odoo = new OdooInstance
{
    Url = cfg.Odoo.Url,
    Db = cfg.Odoo.Db,
    UserId = cfg.Odoo.UserId,
    ApiKey = cfg.Odoo.ApiKey,
};

int failures = 0;
long[] someIds = [];
long newId = 0;
bool unlinked = false;
string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

try
{
    // ---- read-only actions (against existing data) ----

    await Step("fields_get", async () =>
    {
        JsonElement? r = await client.ExecuteAsync(OdxAction.FieldsGet, model, odoo, ActJson.Default.JsonElement,
            keywordJson: U8("""{"attributes":["type"]}"""));
        int n = r is { ValueKind: JsonValueKind.Object } el ? CountProps(el) : -1;
        bool hasName = r is { } e2 && e2.ValueKind == JsonValueKind.Object && e2.TryGetProperty("name", out _);
        Console.WriteLine($"    {n} fields; has 'name' field = {hasName}");
        Need(n > 0 && hasName);
    });

    await Step("search (limit 5)", async () =>
    {
        someIds = await client.ExecuteAsync(OdxAction.Search, model, odoo, ActJson.Default.Int64Array,
            paramsJson: U8("[[]]"), keywordJson: U8("""{"limit":5}""")) ?? [];
        Console.WriteLine($"    ids = [{string.Join(",", someIds)}]");
        Need(someIds.Length > 0);
    });

    await Step("search_count", async () =>
    {
        long c = await client.ExecuteAsync(OdxAction.SearchCount, model, odoo, ActJson.Default.Int64,
            paramsJson: U8("[[]]"));
        Console.WriteLine($"    count = {c}");
        Need(c > 0);
    });

    await Step("search_read (fields id,name; limit 3)", async () =>
    {
        JsonElement? r = await client.ExecuteAsync(OdxAction.SearchRead, model, odoo, ActJson.Default.JsonElement,
            paramsJson: U8("[[]]"), keywordJson: U8("""{"fields":["id","name"],"limit":3}"""));
        int n = r is { ValueKind: JsonValueKind.Array } el ? el.GetArrayLength() : -1;
        Console.WriteLine($"    {n} rows; first = {(n > 0 ? r!.Value[0].ToString() : "-")}");
        Need(n > 0);
    });

    await Step("read (names of the searched ids)", async () =>
    {
        string idsJson = "[" + string.Join(",", Slice(someIds, 3)) + "]";
        JsonElement? r = await client.ExecuteAsync(OdxAction.Read, model, odoo, ActJson.Default.JsonElement,
            paramsJson: U8($"[{idsJson}]"), keywordJson: U8("""{"fields":["name"]}"""));
        int n = r is { ValueKind: JsonValueKind.Array } el ? el.GetArrayLength() : -1;
        Console.WriteLine($"    {n} records read");
        Need(n > 0);
    });

    await Step("call_method: default_get(['name','email'])", async () =>
    {
        JsonElement? r = await client.ExecuteAsync(OdxAction.CallMethod, model, odoo, ActJson.Default.JsonElement,
            paramsJson: U8("""[["name","email"]]"""), fnName: "default_get");
        Console.WriteLine($"    defaults = {r}");
        Need(r is { ValueKind: JsonValueKind.Object });
    });

    // ---- mutating actions (on a throwaway record) ----

    await Step("create (new throwaway partner)", async () =>
    {
        string vals = "[{\"name\":\"ODX ActionsTest " + stamp + "\"}]";
        JsonElement? r = await client.ExecuteAsync(OdxAction.Create, model, odoo, ActJson.Default.JsonElement,
            paramsJson: U8(vals));
        newId = r is { ValueKind: JsonValueKind.Array } arr ? arr[0].GetInt64()
              : r is { } one ? one.GetInt64() : 0;
        Console.WriteLine($"    created id = {newId}");
        Need(newId > 0);
    });

    if (newId > 0)
    {
        await Step("write (rename the created partner)", async () =>
        {
            string vals = "[[" + newId + "],{\"name\":\"ODX ActionsTest " + stamp + " (updated)\"}]";
            bool ok = await client.ExecuteAsync(OdxAction.Write, model, odoo, ActJson.Default.Boolean,
                paramsJson: U8(vals));
            Console.WriteLine($"    write returned {ok}");
            Need(ok);
        });

        await Step("read-back (verify the write landed)", async () =>
        {
            JsonElement? r = await client.ExecuteAsync(OdxAction.Read, model, odoo, ActJson.Default.JsonElement,
                paramsJson: U8("[[" + newId + "]]"), keywordJson: U8("""{"fields":["name"]}"""));
            string name = r is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > 0
                && arr[0].TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            Console.WriteLine($"    name now = \"{name}\"");
            Need(name.Contains("(updated)"));
        });

        await Step("unlink (delete the created partner)", async () =>
        {
            bool ok = await client.ExecuteAsync(OdxAction.Unlink, model, odoo, ActJson.Default.Boolean,
                paramsJson: U8("[[" + newId + "]]"));
            unlinked = ok;
            Console.WriteLine($"    unlink returned {ok}");
            Need(ok);
        });
    }
}
finally
{
    // Backstop: if we created a record but didn't confirm its unlink, try once more so we
    // never leave test junk in the instance.
    if (newId > 0 && !unlinked)
    {
        try
        {
            await client.ExecuteAsync(OdxAction.Unlink, model, odoo, ActJson.Default.Boolean,
                paramsJson: U8("[[" + newId + "]]"));
            Console.WriteLine($"[cleanup] unlinked leftover id {newId}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[cleanup] FAILED to unlink id {newId}: {e.Message} — remove it manually");
        }
    }
}

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("ALL ACTIONS PASSED (real instance)");
    return 0;
}
Console.WriteLine($"{failures} action(s) FAILED");
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
        throw new InvalidOperationException("assertion failed");
}

static long[] Slice(long[] a, int n) => a.Length <= n ? a : a[..n];

static int CountProps(JsonElement obj)
{
    int n = 0;
    foreach (var _ in obj.EnumerateObject()) n++;
    return n;
}

static bool HasPlaceholder(string? s) => s is null || s.Contains("REPLACE", StringComparison.OrdinalIgnoreCase);

static byte[] U8(string s) => Encoding.UTF8.GetBytes(s);

internal sealed record RealConfig(string ProxyUrl, string ProxyApiKey, OdooCfg Odoo, string? Model = null);
internal sealed record OdooCfg(string Url, string Db, long UserId, string ApiKey);

[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(JsonElement))]
internal partial class ActJson : JsonSerializerContext;

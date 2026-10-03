using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TerraKernel.OdxClient;

// Live v2 integration test: real odxclient.dll -> ODXProxy (0.9.0+) -> Odoo 19+.
//
// Environment (keys are never read from or written to a file):
//   ODX_PROXY_URL       proxy base URL            (default http://127.0.0.1:3000)
//   ODX_PROXY_API_KEY   proxy x-api-key           (required)
//   ODX_ODOO_URL        Odoo base URL             (default https://skm.odoo20.teka.it.com)
//   ODX_ODOO_DB         Odoo database             (default ENT_SKM_test)
//   ODX_ODOO_API_KEY    Odoo user API key         (required; scope rpc on Odoo 20+)
//   ODX_ODOO_USER_ID    Odoo uid                  (optional; enables the v1 checks)
//   ODX_LIVE_WRITES=1   also run a self-cleaning create/write/unlink on res.partner
//
// Run:  dotnet run --project dotnet/Odx.Client.V2LiveTest -c Release

string proxyUrl = Env("ODX_PROXY_URL") ?? "http://127.0.0.1:3000";
string? proxyKey = Env("ODX_PROXY_API_KEY");
string odooUrl = Env("ODX_ODOO_URL") ?? "https://skm.odoo20.teka.it.com";
string db = Env("ODX_ODOO_DB") ?? "ENT_SKM_test";
string? odooKey = Env("ODX_ODOO_API_KEY");
string? userId = Env("ODX_ODOO_USER_ID");
bool writes = Env("ODX_LIVE_WRITES") == "1";

if (proxyKey is null || odooKey is null)
{
    Console.Error.WriteLine("Set ODX_PROXY_API_KEY and ODX_ODOO_API_KEY (see the header of Program.cs).");
    return 2;
}

Console.WriteLine($"proxy {proxyUrl} -> odoo {odooUrl} db {db} (writes: {(writes ? "on" : "off")})");
int failures = 0, passed = 0;
using var client = OdxClient.Create(proxyUrl, proxyKey, defaultTimeoutSecs: 30);
var session = client.ForInstanceV2(odooUrl, db, odooKey, new JsonObject { ["lang"] = "en_US" });
var j = LiveJson.Default;

Console.WriteLine("v2 reads:");
await Run("version v2 is 19+, SupportsV2Async true", async () =>
{
    OdxVersionInfoV2 v = await client.GetVersionV2Async(odooUrl);
    Console.WriteLine($"        {v.Version}");
    Assert(v.Major >= 19, $"major {v.Major}");
    Assert(await client.SupportsV2Async(odooUrl), "SupportsV2Async");
});
long[] someIds = [];
await Run("search_count / search / read agree", async () =>
{
    long count = await session.SearchCountAsync("res.partner", default);
    someIds = await session.SearchAsync("res.partner", default, limit: 3, order: "id asc");
    Partner[]? read = await session.ReadAsync("res.partner", someIds, j.PartnerArray, fields: ["name"]);
    Console.WriteLine($"        count={count} ids=[{string.Join(",", someIds)}]");
    Assert(count >= someIds.Length && someIds.Length > 0, "no partners");
    Assert(read is not null && read.Length == someIds.Length, "read length");
});
await Run("search_read with domain/fields/limit/context", async () =>
{
    Partner[]? rows = await session.SearchReadAsync("res.partner", j.PartnerArray,
        domain: OdxJson.Parse("""[["id","!=",0]]"""), fields: ["name"], limit: 2,
        context: new JsonObject { ["active_test"] = false });
    Assert(rows is { Length: > 0 and <= 2 }, $"rows {rows?.Length}");
});
await Run("fields_get with allfields/attributes", async () =>
{
    JsonElement f = await session.FieldsGetAsync("res.partner", j.JsonElement, allfields: ["name"], attributes: ["type"]);
    Assert(f.GetProperty("name").GetProperty("type").GetString() == "char", f.ToString());
});
await Run("call_method name_search (named args only)", async () =>
{
    JsonElement r = await session.CallMethodAsync("res.partner", "name_search", j.JsonElement,
        kwargs: new JsonObject { ["name"] = "", ["limit"] = 2 });
    Assert(r.ValueKind == JsonValueKind.Array, r.ToString());
});

Console.WriteLine("v2 errors:");
await Expect<OdxOdooValidationException>("ids on an @api.model method -> 422", () =>
    session.CallMethodAsync("res.partner", "search_count", j.JsonElement, ids: [1], kwargs: OdxJson.Parse("""{"domain":[]}""")));
await Expect<OdxOdooValidationException>("unknown kwarg -> 422", () =>
    session.CallMethodAsync("res.partner", "search_count", j.JsonElement, kwargs: OdxJson.Parse("""{"domian":[]}""")));
await Expect<OdxOdooNotFoundException>("unknown model -> 404", () =>
    session.SearchCountAsync("x.no.such.model", default));
await Expect<OdxOdooAccessException>("private method -> 403", () =>
    session.CallMethodAsync("res.partner", "_compute_display_name", j.JsonElement, ids: [someIds.FirstOrDefault(1)]));
await Expect<OdxOdooAuthException>("bad Odoo API key -> Odoo 401", () =>
    client.ForInstanceV2(odooUrl, db, "not-a-real-key").SearchCountAsync("res.partner", default));
await Expect<OdxJson2UnavailableException>("wrong database -> -32006", () =>
    client.ForInstanceV2(odooUrl, "no_such_db_odx", odooKey).SearchCountAsync("res.partner", default));
await Expect<OdxInvalidRequestException>("path-traversal model -> -32007", () =>
    session.SearchCountAsync("../web", default));
await Run("bad proxy key -> proxy 401 (OdxAuthException)", async () =>
{
    using var bad = OdxClient.Create(proxyUrl, "wrong-proxy-key");
    try
    {
        await bad.ForInstanceV2(odooUrl, db, odooKey).SearchCountAsync("res.partner", default);
        Assert(false, "no exception");
    }
    catch (OdxException e)
    {
        Assert(e.GetType() == typeof(OdxAuthException), e.GetType().Name);
    }
});

if (writes)
{
    Console.WriteLine("v2 writes (self-cleaning):");
    var created = new List<long>();
    try
    {
        await Run("create (list) / create_one / write / read back / unlink", async () =>
        {
            long[] two = await session.CreateAsync("res.partner",
                OdxJson.Parse("""[{"name":"odx-dotnet-v2 A"},{"name":"odx-dotnet-v2 B"}]"""));
            created.AddRange(two);
            Assert(two.Length == 2, $"create list -> {two.Length}");
            long one = await session.CreateOneAsync("res.partner", new JsonObject { ["name"] = "odx-dotnet-v2 C" });
            created.Add(one);
            Assert(await session.WriteAsync("res.partner", two, new JsonObject { ["comment"] = "via .NET v2" }), "write");
            Partner[]? back = await session.ReadAsync("res.partner", two, j.PartnerArray, fields: ["name", "comment"]);
            Assert(back is { Length: 2 } && back.All(p => p.Comment?.Contains("via .NET v2") == true), "read back");
            Assert(await session.UnlinkAsync("res.partner", created), "unlink");
            created.Clear();
        });
    }
    finally
    {
        if (created.Count > 0)
            try { await session.UnlinkAsync("res.partner", created); } catch { /* best effort */ }
    }
}

if (userId is not null)
{
    Console.WriteLine("v1 on the same instance:");
    var v1 = new OdooInstance { Url = odooUrl, UserId = long.Parse(userId), Db = db, ApiKey = odooKey };
    await Run("v1 search_count", async () =>
    {
        long n = await client.ExecuteAsync(OdxAction.SearchCount, "res.partner", v1, j.Int64, paramsJson: "[[]]"u8.ToArray());
        Assert(n > 0, $"count {n}");
    });
    await Run("v1 Odoo error (code 0 on Odoo 19+) is OdxOdooException, not license", async () =>
    {
        try
        {
            await client.ExecuteAsync(OdxAction.CallMethod, "res.partner", v1, j.JsonElement, fnName: "no_such_method_odx");
            Assert(false, "no exception");
        }
        catch (OdxException e)
        {
            Console.WriteLine($"        {e.GetType().Name} code={e.RpcCode}");
            Assert(e is OdxOdooException && e is not OdxLicenseException, e.GetType().Name);
        }
    });
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? $"ALL PASSED ({passed})" : $"{failures} FAILED, {passed} passed");
return failures == 0 ? 0 : 1;

async Task Run(string name, Func<Task> test)
{
    try
    {
        await test();
        passed++;
        Console.WriteLine($"  PASS  {name}");
    }
    catch (Exception e)
    {
        failures++;
        Console.WriteLine($"  FAIL  {name}: {e.GetType().Name}: {e.Message}");
    }
}

Task Expect<TEx>(string name, Func<Task> call) where TEx : OdxException => Run(name, async () =>
{
    try
    {
        await call();
    }
    catch (OdxException e)
    {
        Console.WriteLine($"        {e.GetType().Name} code={e.RpcCode} name={(e as OdxOdooException)?.OdooErrorName}");
        Assert(e.GetType() == typeof(TEx), $"expected {typeof(TEx).Name}");
        return;
    }
    Assert(false, "no exception");
});

static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

internal sealed class Partner
{
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("comment")] public JsonElement CommentRaw { get; init; }
    [JsonIgnore] public string? Comment => CommentRaw.ValueKind == JsonValueKind.String ? CommentRaw.GetString() : null;
}

[JsonSerializable(typeof(Partner[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(long))]
internal partial class LiveJson : JsonSerializerContext;

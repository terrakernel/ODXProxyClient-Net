using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TerraKernel.OdxClient;

// ODXBrain tests (SYSTEM_ARCHITECTURE.md §4.8–§4.10, §7.2). The offline section drives the real
// odxclient.dll against a one-shot mock server that captures the exact bytes on the wire. The
// live section runs only when ODX_PROXY_URL / ODX_PROXY_API_KEY (and optionally
// ODX_PROXY_NOBRAIN_URL, ODX_PROXY_NOSOPS_URL, ODOO_URL, ODOO_DB, ODOO_API_KEY) are set.
// Credentials are read from the environment only; never put them in this file.

int failures = 0;
int skipped = 0;
using var offline = OdxClient.Create("http://127.0.0.1:1", "proxy-key");

Console.WriteLine("wire snapshots:");
await Snap("resolve: query only (no facts, no k: nothing else is sent, never null)",
    "/brain/v1/resolve", Fixtures.ModelOk, c => c.Brain.ResolveModelAsync("which invoice is not paid yet"),
    """{"query":"which invoice is not paid yet"}""");
await Snap("resolve: facts + k + extra passthrough",
    "/brain/v1/resolve", Fixtures.ModelOk,
    c => c.Brain.ResolveModelAsync("unpaid invoices", new OdxBrainFacts("19.0", OdxBrainFacts.Enterprise), k: 5,
        extra: new JsonObject { ["lang"] = "id", ["future_flag"] = true }),
    """{"query":"unpaid invoices","odoo_version":"19.0","edition":"enterprise","k":5,"lang":"id","future_flag":true}""");
await Snap("resolve: one fact set, the other omitted",
    "/brain/v1/resolve", Fixtures.ModelOk, c => c.Brain.ResolveModelAsync("q", new OdxBrainFacts(Edition: "community")),
    """{"query":"q","edition":"community"}""");
await Snap("sops/resolve: every argument",
    "/brain/v1/sops/resolve", Fixtures.SopOk,
    c => c.Brain.ResolveSopAsync("record a vendor bill", new OdxBrainFacts("saas~16.3", "enterprise"),
        installedModules: ["account", "purchase"], company: "acme", k: 10, extra: OdxJson.Parse("""{"x":[1,2]}""")),
    """{"query":"record a vendor bill","odoo_version":"saas~16.3","edition":"enterprise","installed_modules":["account","purchase"],"company":"acme","k":10,"x":[1,2]}""");
await Snap("sops/resolve: query only",
    "/brain/v1/sops/resolve", Fixtures.SopOk, c => c.Brain.ResolveSopAsync("q"), """{"query":"q"}""");
await RunAsync("info: GET /brain/v1/info, null id parses", Info);
Run("extra may not repeat a named argument", () =>
{
    foreach (string key in new[] { "id", "query", "odoo_version", "edition", "k", "company", "installed_modules" })
        ExpectArg(() => offline.Brain.ResolveModelAsync("q", extra: new JsonObject { [key] = "x" }));
});
Run("extra must be a JSON object", () => ExpectArg(() => offline.Brain.ResolveModelAsync("q", extra: OdxJson.Parse("[1]"))));

Console.WriteLine("results are not altered:");
await RunAsync("model result: every candidate in order, conditional fields, unknown fields, raw", ModelResultIntact);
await RunAsync("sop result: match none, excluded, facts_missing, sops.root, unknown fields", SopResultIntact);

Console.WriteLine("availability + facts:");
await Available("features.odxbrain true -> true", """{"jsonrpc":"2.0","id":"about","result":{"build":"b","features":{"odxbrain":true},"version":"0.10.1"}}""", true);
await Available("features.odxbrain false -> false", """{"jsonrpc":"2.0","id":"about","result":{"build":"b","features":{"odxbrain":false},"version":"0.10.1"}}""", false);
await Available("no features (proxy < 0.10.0) -> false", """{"jsonrpc":"2.0","id":"about","result":{"build":"b","version":"0.9.0"}}""", false);
await Available("features without odxbrain -> false", """{"jsonrpc":"2.0","id":"about","result":{"features":{}}}""", false);
await RunAsync("v1 facts: server_serie + '+e' anywhere in server_version, cached per URL", FactsV1);
await RunAsync("v1 facts: community build", FactsV1Community);
await RunAsync("v2 facts: version_info[0].version_info[1], pre-release '+e' kept", FactsV2);

Console.WriteLine("error mapping (§7.2 table):");
await Err("501 -32008 -> NotInstalled", 501, Rpc(-32008, """{"type":"odxbrain_not_installed"}"""), typeof(OdxBrainNotInstalledException), -32008, retry: false, errorType: "odxbrain_not_installed");
await Err("bare 404, plain text (proxy < 0.10.0) -> NotInstalled", 404, "Not Found", typeof(OdxBrainNotInstalledException), null, retry: false, contentType: "text/plain");
await Err("bare 404, empty body -> NotInstalled", 404, "", typeof(OdxBrainNotInstalledException), null, retry: false, contentType: "text/plain");
await Err("401 -32000 (id null) -> OdxAuthException", 401, """{"jsonrpc":"2.0","id":null,"error":{"code":-32000,"message":"Authentication failed"}}""", typeof(OdxAuthException), -32000);
await Err("403 0 -> OdxLicenseException", 403, Rpc(0), typeof(OdxLicenseException), 0);
await Err("502 -32009 -> Unavailable (retry once)", 502, Rpc(-32009), typeof(OdxBrainUnavailableException), -32009, retry: true);
await Err("504 -32010 -> Timeout (retry once)", 504, Rpc(-32010), typeof(OdxBrainTimeoutException), -32010, retry: true);
await Err("502 -32011 -> ConfigError (not an auth error)", 502, Rpc(-32011), typeof(OdxBrainConfigException), -32011, retry: false);
await Err("200 + 400 -> RequestError, data.type kept", 200, Rpc(400, """{"message":"'query' is empty","type":"invalid_request_error"}"""), typeof(OdxBrainRequestException), 400, retry: false, errorType: "invalid_request_error");
await Err("200 + 400 unsupported_version -> RequestError", 200, Rpc(400, """{"type":"unsupported_version"}"""), typeof(OdxBrainRequestException), 400, errorType: "unsupported_version");
await Err("200 + 503 sops_unavailable -> Unavailable (no retry)", 200, Rpc(503, """{"type":"sops_unavailable"}"""), typeof(OdxBrainUnavailableException), 503, retry: false, errorType: "sops_unavailable");
await Err("200 + 500 -> OdxBrainException", 200, Rpc(500, """{"type":"internal_error"}"""), typeof(OdxBrainException), 500, retry: false, errorType: "internal_error");
await Err("200 + unknown code -> OdxBrainException (never an Odoo error)", 200, Rpc(418), typeof(OdxBrainException), 418);
await Err("400 plain text (bad JSON) -> RequestError, not parsed", 400, "Failed to parse the request body as JSON: key must be a string at line 1 column 2", typeof(OdxBrainRequestException), null, contentType: "text/plain; charset=utf-8", messageContains: "Failed to parse");
await Err("422 plain text (wrong shape) -> RequestError, not parsed", 422, "Failed to deserialize the JSON body into the target type: missing field `id`", typeof(OdxBrainRequestException), null, contentType: "text/plain; charset=utf-8", messageContains: "missing field");
Run("-32011 type is not an OdxAuthException", () => Assert(!typeof(OdxBrainConfigException).IsAssignableTo(typeof(OdxAuthException)), "must not be auth"));
Run("brain errors are not Odoo errors", () => Assert(!typeof(OdxBrainException).IsAssignableTo(typeof(OdxOdooException)), "must not be Odoo"));
await RunAsync("the same errors on /brain/v1/sops/resolve and /brain/v1/info", ErrorsOnOtherRoutes);

Console.WriteLine("threading:");
Run("sync-over-async under a UI SynchronizationContext does not deadlock", SyncOverAsyncNoDeadlock);

Console.WriteLine("live (env vars):");
await Live();

Console.WriteLine();
Console.WriteLine(failures == 0 ? $"ALL PASSED ({skipped} live skipped)" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

// ---- offline tests ----

async Task Snap<T>(string name, string path, string response, Func<OdxClient, Task<T>> call, string expected)
{
    await RunAsync(name, async () =>
    {
        var server = MockServer.Start(200, response);
        using var client = Client(server.Port);
        await call(client);
        string req = await server.Request;
        Assert(req.StartsWith($"POST {path} ", StringComparison.Ordinal), $"wrong route: {req.Split('\n')[0]}");
        Assert(Regex.IsMatch(req, "(?im)^x-api-key: proxy-key\r$"), "proxy key header missing");
        string body = Body(req);
        Assert(Regex.IsMatch(body, "^\\{\"id\":\"[^\"]+\","), "id must be first and auto-generated");
        Assert(!body.Contains("null"), "nothing may be sent as null");
        Assert(!body.Contains("odoo_instance"), "odoo_instance must not be attached");
        string normalized = Regex.Replace(body, "^\\{\"id\":\"[^\"]+\",", "{");
        Assert(normalized == expected, $"\n  expected: {expected}\n  actual:   {normalized}");
    });
}

async Task Info()
{
    var server = MockServer.Start(200, """{"jsonrpc":"2.0","id":null,"result":{"service":"odxbrain","corpus":{"odoo":{"min_version":"14.0","max_version":"19.0"}}}}""");
    using var client = Client(server.Port);
    JsonElement info = await client.Brain.GetInfoAsync();
    string req = await server.Request;
    Assert(req.StartsWith("GET /brain/v1/info ", StringComparison.Ordinal), $"wrong route: {req.Split('\n')[0]}");
    Assert(info.GetProperty("service").GetString() == "odxbrain", "service");
    Assert(info.GetProperty("corpus").GetProperty("odoo").GetProperty("max_version").GetString() == "19.0", "nested");
}

async Task ModelResultIntact()
{
    const string response = """
        {"jsonrpc":"2.0","id":"x","result":{
          "candidates":[{"model":"account.move","score":0.513},
                        {"model":"account.payment","score":0.02,"provisional":true,"why":"new"},
                        {"model":"account.payment.term","score":0.001}],
          "excluded":[{"model":"account.invoice","reason":"removed in 13.0","successor":"account.move"}],
          "filtered":false,"version_unaudited":"19.0",
          "model":"bge-m3","dim":1024,"corpus_version":"2026.09.4","usage":{"prompt_tokens":10},
          "brand_new":{"a":1}}}
        """;
    var server = MockServer.Start(200, response);
    using var client = Client(server.Port);
    OdxBrainModelResult r = await client.Brain.ResolveModelAsync("q");
    Assert(r.Candidates.Select(c => c.Model).SequenceEqual(["account.move", "account.payment", "account.payment.term"]), "every candidate, in order (low scores kept)");
    Assert(r.Candidates[0].Score == 0.513 && !r.Candidates[0].Provisional, "candidate 0");
    Assert(r.Candidates[1].Provisional, "provisional");
    Assert(r.Candidates[1].AdditionalFields?["why"].GetString() == "new", "unknown candidate field");
    Assert(r.Excluded is [{ Model: "account.invoice", Reason: "removed in 13.0", Successor: "account.move" }], "excluded");
    Assert(r.Filtered == false && r.VersionUnaudited == "19.0", "filtered / version_unaudited");
    Assert(r.Model == "bge-m3" && r.Dim == 1024 && r.CorpusVersion == "2026.09.4", "model / dim / corpus_version");
    Assert(r.Usage?.GetProperty("prompt_tokens").GetInt32() == 10, "usage");
    Assert(r.AdditionalFields?["brand_new"].GetProperty("a").GetInt32() == 1, "unknown top-level field");
    Assert(r.Raw.GetProperty("brand_new").GetProperty("a").GetInt32() == 1 && r.Raw.GetProperty("candidates").GetArrayLength() == 3, "raw");

    var server2 = MockServer.Start(200, Fixtures.ModelOk);
    using var client2 = Client(server2.Port);
    OdxBrainModelResult plain = await client2.Brain.ResolveModelAsync("q");
    Assert(plain.Excluded.Length == 0 && plain.Filtered is null && plain.VersionUnaudited is null, "absent conditional fields");
}

async Task SopResultIntact()
{
    const string response = """
        {"jsonrpc":"2.0","id":"x","result":{
          "candidates":[{"id":"customer-invoice","score":0.36,"title":"Invoice a customer","version":"8a92178bbf88b44d"},
                        {"id":"legacy-quotation","score":0.35,"title":"Send a quotation","version":"2102018c3ac2d62d","lang":"en"}],
          "dim":1024,"excluded":[{"id":"vendor-bill","reason":"needs module purchase"}],
          "facts_missing":["odoo_version","company"],"match":"none","model":"bge-m3",
          "sops":{"generation":1,"root":"9660e95aec011de84e0af821e6859198ddea6fe35934a206e4e12231e9ebcdd7","label":"odx-it"},
          "usage":{"prompt_tokens":6},"next":true}}
        """;
    var server = MockServer.Start(200, response);
    using var client = Client(server.Port);
    OdxBrainSopResult r = await client.Brain.ResolveSopAsync("bake a cake");
    Assert(r.IsNoMatch && r.Match == "none", "match none");
    Assert(r.Candidates.Length == 2 && r.Candidates[0].Id == "customer-invoice" && r.Candidates[0].Version == "8a92178bbf88b44d", "candidates kept even with no match");
    Assert(r.Candidates[1].AdditionalFields?["lang"].GetString() == "en", "unknown candidate field");
    Assert(r.Excluded is [{ Id: "vendor-bill", Reason: "needs module purchase" }], "excluded");
    Assert(r.FactsMissing.SequenceEqual(["odoo_version", "company"]), "facts_missing");
    Assert(r.Sops is { Generation: 1, Root.Length: 64 } && r.Sops.AdditionalFields?["label"].GetString() == "odx-it", "sops");
    Assert(r.Model == "bge-m3" && r.Dim == 1024, "model / dim");
    Assert(r.AdditionalFields?["next"].GetBoolean() == true && r.Raw.GetProperty("next").GetBoolean(), "unknown field + raw");

    var server2 = MockServer.Start(200, Fixtures.SopOk);
    using var client2 = Client(server2.Port);
    OdxBrainSopResult plain = await client2.Brain.ResolveSopAsync("q");
    Assert(!plain.IsNoMatch && plain.Match is null && plain.Excluded.Length == 0 && plain.FactsMissing.Length == 0, "absent fields");
}

async Task Available(string name, string about, bool expected)
{
    await RunAsync(name, async () =>
    {
        var server = MockServer.Start(200, about);
        using var client = Client(server.Port);
        bool ok = await client.Brain.IsAvailableAsync();
        Assert((await server.Request).StartsWith("GET /_/about ", StringComparison.Ordinal), "route");
        Assert(ok == expected, $"got {ok}");
    });
}

async Task FactsV1()
{
    var server = MockServer.Start(200, """{"jsonrpc":"2.0","id":"1","result":{"server_version":"20.0+e-20260925","server_version_info":[20,0,0,"final",0,"e"],"server_serie":"20.0","protocol_version":1}}""");
    using var client = Client(server.Port);
    OdxBrainFacts f = await client.Brain.GetFactsAsync("https://erp.example.com");
    Assert((await server.Request).StartsWith("POST /api/odoo/version ", StringComparison.Ordinal), "route");
    Assert(f == new OdxBrainFacts("20.0", "enterprise"), $"got {f}");
    // The one-shot server is gone: a second round trip would fail.
    Assert(await client.Brain.GetFactsAsync("https://erp.example.com") == f, "cached");
}

async Task FactsV1Community()
{
    var server = MockServer.Start(200, """{"jsonrpc":"2.0","id":"1","result":{"server_version":"17.0","server_serie":"17.0"}}""");
    using var client = Client(server.Port);
    Assert(await client.Brain.GetFactsAsync("https://ce.example.com") == new OdxBrainFacts("17.0", "community"), "community");
}

async Task FactsV2()
{
    var server = MockServer.Start(200, """{"jsonrpc":"2.0","id":"1","result":{"version_info":[20,0,0,"alpha",1,""],"version":"20.0alpha1+e"}}""");
    using var client = Client(server.Port);
    OdxBrainFacts f = await client.ForInstanceV2("https://erp.example.com", "prod", "k").GetBrainFactsAsync();
    Assert((await server.Request).StartsWith("POST /v2/odoo/version ", StringComparison.Ordinal), "route");
    Assert(f == new OdxBrainFacts("20.0", "enterprise"), $"got {f} (version_info[5] must not decide edition)");
}

async Task Err(string name, int http, string body, Type expected, long? code, bool? retry = null,
    string? errorType = null, string contentType = "application/json", string? messageContains = null)
{
    await RunAsync(name, async () =>
    {
        var server = MockServer.Start(http, body, contentType);
        using var client = Client(server.Port);
        try
        {
            await client.Brain.ResolveModelAsync("q");
            Assert(false, "expected an exception (never an empty result)");
        }
        catch (OdxException e)
        {
            Assert(e.GetType() == expected, $"got {e.GetType().Name}: {e.Message}");
            Assert(e.RpcCode == code, $"RpcCode {e.RpcCode}");
            if (retry is bool rt)
                Assert(e is OdxBrainException b && b.IsRetryable == rt, $"IsRetryable should be {rt}");
            if (errorType is not null)
                Assert(e is OdxBrainException b2 && b2.ErrorType == errorType, $"ErrorType {(e as OdxBrainException)?.ErrorType}");
            if (messageContains is not null)
                Assert(e.Message.Contains(messageContains, StringComparison.Ordinal), $"message: {e.Message}");
        }
    });
}

async Task ErrorsOnOtherRoutes()
{
    var s1 = MockServer.Start(501, Rpc(-32008, """{"type":"odxbrain_not_installed"}"""));
    using (var c = Client(s1.Port))
        await ExpectType<OdxBrainNotInstalledException>(() => c.Brain.ResolveSopAsync("q"));
    var s2 = MockServer.Start(404, "", "text/plain");
    using (var c = Client(s2.Port))
        await ExpectType<OdxBrainNotInstalledException>(() => c.Brain.GetInfoAsync());
    var s3 = MockServer.Start(200, Rpc(503, """{"type":"sops_unavailable"}"""));
    using (var c = Client(s3.Port))
        await ExpectType<OdxBrainUnavailableException>(() => c.Brain.ResolveSopAsync("q"));
}

void SyncOverAsyncNoDeadlock()
{
    var server = MockServer.Start(200, Fixtures.ModelOk);
    using var client = Client(server.Port);
    var ctx = new BlockingSyncContext();
    var prev = SynchronizationContext.Current;
    try
    {
        SynchronizationContext.SetSynchronizationContext(ctx);
        OdxBrainModelResult r = client.Brain.ResolveModelAsync("q").GetAwaiter().GetResult();
        Assert(r.Candidates.Length == 1, "result");
    }
    finally
    {
        SynchronizationContext.SetSynchronizationContext(prev);
    }
    Assert(ctx.Posts == 0, $"{ctx.Posts} continuation(s) were posted to the UI context");
}

// ---- live tests (skipped unless the env vars are set) ----

async Task Live()
{
    string? url = Env("ODX_PROXY_URL"), key = Env("ODX_PROXY_API_KEY");
    if (url is null || key is null)
    {
        Console.WriteLine("  SKIP  ODX_PROXY_URL / ODX_PROXY_API_KEY not set");
        skipped++;
        return;
    }

    using var client = OdxClient.Create(url, key);
    OdxBrainFacts facts = default;

    await RunAsync("available() on the ODXBrain proxy", async () => Assert(await client.Brain.IsAvailableAsync(), "expected true"));
    await RunAsync("info()", async () =>
    {
        JsonElement info = await client.Brain.GetInfoAsync();
        Assert(info.GetProperty("service").GetString() == "odxbrain", "service");
        Console.WriteLine($"          corpus {info.GetProperty("corpus").GetProperty("version")}, sops root {info.GetProperty("sops").GetProperty("root").GetString()?[..12]}…");
    });

    string? odooUrl = Env("ODOO_URL"), db = Env("ODOO_DB"), odooKey = Env("ODOO_API_KEY");
    if (odooUrl is not null && db is not null && odooKey is not null)
    {
        await RunAsync("brain facts from v2 session == from v1 version", async () =>
        {
            facts = await client.ForInstanceV2(odooUrl, db, odooKey).GetBrainFactsAsync();
            using var fresh = OdxClient.Create(url, key); // separate cache
            OdxBrainFacts v1 = await fresh.Brain.GetFactsAsync(odooUrl);
            Console.WriteLine($"          v2 {facts}, v1 {v1}");
            Assert(facts.OdooVersion is not null && facts.Edition is not null, "facts");
            Assert(facts == v1, "v1 and v2 disagree");
        });
    }
    else
    {
        Console.WriteLine("  SKIP  brain facts (ODOO_URL / ODOO_DB / ODOO_API_KEY not set)");
        skipped++;
    }

    await RunAsync("resolve_model with facts", async () =>
    {
        OdxBrainModelResult r = await client.Brain.ResolveModelAsync("which invoice is not paid yet", facts, k: 5);
        Console.WriteLine($"          {string.Join(", ", r.Candidates.Select(c => $"{c.Model} {c.Score:0.000}"))}; unaudited={r.VersionUnaudited ?? "-"}");
        Assert(r.Candidates.Length == 5, $"k=5 gave {r.Candidates.Length}");
        Assert(r.Candidates.Any(c => c.Model == "account.move"), "account.move missing");
        Assert(r.Model is not null && r.Dim > 0 && r.CorpusVersion is not null, "model/dim/corpus_version");
    });
    await RunAsync("resolve_model without facts -> filtered false", async () =>
    {
        OdxBrainModelResult r = await client.Brain.ResolveModelAsync("customer addresses");
        Assert(r.Filtered == false, $"filtered {r.Filtered}");
        Assert(r.Candidates.Length > 0 && r.Candidates[0].Model == "res.partner", $"top {r.Candidates.FirstOrDefault()?.Model}");
    });
    await RunAsync("resolve_sop with modules", async () =>
    {
        OdxBrainSopResult r = await client.Brain.ResolveSopAsync("record a vendor bill from a purchase order", facts, installedModules: ["account", "purchase"]);
        Console.WriteLine($"          {string.Join(", ", r.Candidates.Select(c => $"{c.Id}@{c.Version[..6]} {c.Score:0.00}"))}; match={r.Match ?? "-"}; missing=[{string.Join(",", r.FactsMissing)}]");
        Assert(!r.IsNoMatch && r.Candidates[0].Id == "vendor-bill-from-po", $"top {r.Candidates.FirstOrDefault()?.Id}");
        Assert(r.Sops?.Root?.Length == 64, "sops.root");
    });
    await RunAsync("resolve_sop for an unrelated task -> match none", async () =>
    {
        OdxBrainSopResult r = await client.Brain.ResolveSopAsync("bake a chocolate cake");
        Assert(r.IsNoMatch, $"match {r.Match}");
        Assert(r.Candidates.Length > 0, "candidates still returned");
    });
    await RunAsync("empty query -> OdxBrainRequestException invalid_request_error", async () =>
    {
        var e = await ExpectType<OdxBrainRequestException>(() => client.Brain.ResolveModelAsync(""));
        Assert(e.ErrorType == "invalid_request_error" && e.RpcCode == 400, $"{e.ErrorType} {e.RpcCode}");
    });
    await RunAsync("version below 14.0 -> unsupported_version", async () =>
    {
        var e = await ExpectType<OdxBrainRequestException>(() => client.Brain.ResolveModelAsync("q", new OdxBrainFacts("12.0")));
        Assert(e.ErrorType == "unsupported_version", $"{e.ErrorType}");
    });
    await RunAsync("wrong proxy key -> OdxAuthException", async () =>
    {
        using var bad = OdxClient.Create(url, "wrong-key");
        await ExpectType<OdxAuthException>(() => bad.Brain.ResolveModelAsync("q"));
    });

    if (Env("ODX_PROXY_NOBRAIN_URL") is { } noBrain)
    {
        using var c = OdxClient.Create(noBrain, key);
        await RunAsync("no-ODXBrain proxy: available() false", async () => Assert(!await c.Brain.IsAvailableAsync(), "expected false"));
        await RunAsync("no-ODXBrain proxy: 501 -> NotInstalled on all three routes", async () =>
        {
            var e = await ExpectType<OdxBrainNotInstalledException>(() => c.Brain.ResolveModelAsync("q"));
            Assert(e.RpcCode == -32008 && !e.IsRetryable && e.ErrorType == "odxbrain_not_installed", $"{e.RpcCode} {e.ErrorType}");
            await ExpectType<OdxBrainNotInstalledException>(() => c.Brain.ResolveSopAsync("q"));
            await ExpectType<OdxBrainNotInstalledException>(() => c.Brain.GetInfoAsync());
        });
    }
    else
    {
        Console.WriteLine("  SKIP  ODX_PROXY_NOBRAIN_URL not set");
        skipped++;
    }

    if (Env("ODX_PROXY_NOSOPS_URL") is { } noSops)
    {
        using var c = OdxClient.Create(noSops, key);
        await RunAsync("no-SOP proxy: sops -> Unavailable sops_unavailable (no retry); models still work", async () =>
        {
            var e = await ExpectType<OdxBrainUnavailableException>(() => c.Brain.ResolveSopAsync("record a vendor bill"));
            Assert(e.RpcCode == 503 && e.ErrorType == "sops_unavailable" && !e.IsRetryable, $"{e.RpcCode} {e.ErrorType} {e.IsRetryable}");
            Assert((await c.Brain.ResolveModelAsync("customer addresses")).Candidates.Length > 0, "models");
        });
    }
    else
    {
        Console.WriteLine("  SKIP  ODX_PROXY_NOSOPS_URL not set");
        skipped++;
    }
}

// ---- plumbing ----


static string Rpc(long code, string? data = null)
    => """{"jsonrpc":"2.0","id":"1","error":{"code":""" + code + ""","message":"m" """ + (data is null ? "" : ""","data":""" + data) + "}}";

static OdxClient Client(int port) => OdxClient.Create($"http://127.0.0.1:{port}", "proxy-key", defaultTimeoutSecs: 5);

static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

static string Body(string request)
{
    int sep = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
    return sep >= 0 ? request[(sep + 4)..] : request;
}

static void ExpectArg(Func<Task> call)
{
    try
    {
        call().GetAwaiter().GetResult();
    }
    catch (ArgumentException)
    {
        return;
    }
    throw new Exception("expected ArgumentException");
}

static async Task<TEx> ExpectType<TEx>(Func<Task> call) where TEx : Exception
{
    try
    {
        await call();
    }
    catch (Exception e)
    {
        Assert(e.GetType() == typeof(TEx), $"expected {typeof(TEx).Name}, got {e.GetType().Name}: {e.Message}");
        return (TEx)e;
    }
    throw new Exception($"expected {typeof(TEx).Name}");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"  PASS  {name}");
    }
    catch (Exception e)
    {
        failures++;
        Console.WriteLine($"  FAIL  {name}: {e.GetType().Name}: {e.Message}"); if (Env("BRAIN_TEST_TRACE") is not null) Console.WriteLine(e.StackTrace);
    }
}

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"  PASS  {name}");
    }
    catch (Exception e)
    {
        failures++;
        Console.WriteLine($"  FAIL  {name}: {e.GetType().Name}: {e.Message}"); if (Env("BRAIN_TEST_TRACE") is not null) Console.WriteLine(e.StackTrace);
    }
}

/// <summary>One-shot HTTP/1.1 server: reads one full request (honoring Content-Length), replies, closes.</summary>
sealed class MockServer
{
    public required int Port { get; init; }
    public required Task<string> Request { get; init; }

    public static MockServer Start(int status, string body, string contentType = "application/json")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using TcpClient sock = listener.AcceptTcpClient();
                using NetworkStream stream = sock.GetStream();
                var buf = new List<byte>();
                var chunk = new byte[8192];
                int headerEnd = -1, contentLength = 0;
                while (true)
                {
                    int n = stream.Read(chunk, 0, chunk.Length);
                    if (n <= 0) break;
                    buf.AddRange(chunk.AsSpan(0, n));
                    string text = Encoding.UTF8.GetString(buf.ToArray());
                    if (headerEnd < 0 && (headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                    {
                        var m = Regex.Match(text[..headerEnd], "(?im)^content-length:\\s*(\\d+)");
                        contentLength = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                    }
                    if (headerEnd >= 0 && buf.Count >= Encoding.UTF8.GetByteCount(text[..headerEnd]) + 4 + contentLength)
                        break;
                }
                tcs.TrySetResult(Encoding.UTF8.GetString(buf.ToArray()));

                byte[] payload = Encoding.UTF8.GetBytes(body);
                byte[] head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} X\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                stream.Write(head);
                stream.Write(payload);
                stream.Flush();
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
            finally
            {
                listener.Stop();
            }
        })
        { IsBackground = true };
        thread.Start();
        return new MockServer { Port = port, Request = tcs.Task };
    }
}

// A UI-like context that records posts but never runs them (its thread is blocked).
sealed class BlockingSyncContext : SynchronizationContext
{
    public int Posts;
    public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posts);
    public override void Send(SendOrPostCallback d, object? state)
    {
        Interlocked.Increment(ref Posts);
        d(state);
    }
}

static class Fixtures
{
    public const string ModelOk = """{"jsonrpc":"2.0","id":"1","result":{"candidates":[{"model":"res.partner","score":0.5}],"model":"bge-m3","dim":1024,"corpus_version":"c"}}""";
    public const string SopOk = """{"jsonrpc":"2.0","id":"1","result":{"candidates":[{"id":"s","version":"v","title":"t","score":0.8}],"model":"bge-m3","dim":1024,"sops":{"generation":1,"root":"r"}}}""";
}

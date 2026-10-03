using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TerraKernel.OdxClient;
using TerraKernel.OdxClient.Interop;
using TerraKernel.OdxClient.Json;

// Offline v2 tests. Every request goes through the real odxclient.dll to a one-shot
// mock server that captures the exact bytes on the wire, so a snapshot checks the
// path + the body the proxy would receive (SYSTEM_ARCHITECTURE.md §7.1).

int failures = 0;
using var OfflineClient = OdxClient.Create("http://127.0.0.1:1", "proxy-key");
const string Instance = "\"odoo_instance\":{\"url\":\"https://erp.example.com\",\"db\":\"prod\",\"api_key\":\"secret\"}";
const string Ok = """{"jsonrpc":"2.0","id":"1","result":true}""";

Console.WriteLine("wire snapshots (one per v2 method):");
await Snap("search", """{"jsonrpc":"2.0","id":"1","result":[7,8]}""",
    s => s.SearchAsync("res.partner", OdxJson.Parse("""[["is_company","=",true]]"""), offset: 10, limit: 5, order: "name asc"),
    """{"model_id":"res.partner","method":"search","kwargs":{"domain":[["is_company","=",true]],"offset":10,"limit":5,"order":"name asc","context":{"lang":"en_US","allowed_company_ids":[1]}},""" + Instance + "}");
await Snap("search (unset domain is sent as [])", """{"jsonrpc":"2.0","id":"1","result":[]}""",
    s => s.SearchAsync("res.partner", default),
    """{"model_id":"res.partner","method":"search","kwargs":{"domain":[],"context":{"lang":"en_US","allowed_company_ids":[1]}},""" + Instance + "}");
await Snap("search_read", """{"jsonrpc":"2.0","id":"1","result":[{"id":1,"name":"A"}]}""",
    s => s.SearchReadAsync("res.partner", TestJson.Default.PartnerArray,
        domain: new JsonArray(new JsonArray("is_company", "=", true)), fields: ["name", "email"], limit: 100,
        context: new JsonObject { ["lang"] = "id_ID", ["tz"] = "Asia/Jakarta" }),
    """{"model_id":"res.partner","method":"search_read","kwargs":{"domain":[["is_company","=",true]],"fields":["name","email"],"limit":100,"context":{"lang":"id_ID","allowed_company_ids":[1],"tz":"Asia/Jakarta"}},""" + Instance + "}");
await Snap("search_read (nothing set: no keys, no context)", """{"jsonrpc":"2.0","id":"1","result":[]}""",
    s => s.SearchReadAsync("res.partner", TestJson.Default.PartnerArray),
    """{"model_id":"res.partner","method":"search_read","kwargs":{},""" + Instance + "}", noContext: true);
await Snap("search_count", """{"jsonrpc":"2.0","id":"1","result":42}""",
    s => s.SearchCountAsync("res.partner", OdxJson.Parse("""[["active","=",true]]"""), limit: 1000),
    """{"model_id":"res.partner","method":"search_count","kwargs":{"domain":[["active","=",true]],"limit":1000,"context":{"lang":"en_US","allowed_company_ids":[1]}},""" + Instance + "}");
await Snap("read", """{"jsonrpc":"2.0","id":"1","result":[{"id":3,"name":"C"}]}""",
    s => s.ReadAsync("res.partner", [3, 4], TestJson.Default.PartnerArray, fields: ["name"], load: "_classic_read"),
    """{"model_id":"res.partner","method":"read","kwargs":{"ids":[3,4],"fields":["name"],"load":"_classic_read","context":{"lang":"en_US","allowed_company_ids":[1]}},""" + Instance + "}");
await Snap("fields_get", """{"jsonrpc":"2.0","id":"1","result":{"name":{"type":"char"}}}""",
    s => s.FieldsGetAsync("res.partner", TestJson.Default.JsonElement, allfields: ["name"], attributes: ["type", "string"]),
    """{"model_id":"res.partner","method":"fields_get","kwargs":{"allfields":["name"],"attributes":["type","string"],"context":{"lang":"en_US","allowed_company_ids":[1]}},""" + Instance + "}");
await Snap("create (one dict is wrapped into vals_list)", """{"jsonrpc":"2.0","id":"1","result":[41]}""",
    s => s.CreateAsync("res.partner", new JsonObject { ["name"] = "Acme" }),
    """{"model_id":"res.partner","method":"create","kwargs":{"vals_list":[{"name":"Acme"}],"context":{"lang":"en_US","allowed_company_ids":[1]}},""" + Instance + "}");
await Snap("create (a list is sent as-is)", """{"jsonrpc":"2.0","id":"1","result":[41,42]}""",
    s => s.CreateAsync("res.partner", OdxJson.Parse("""[{"name":"Acme"},{"name":"Globex"}]""")),
    """{"model_id":"res.partner","method":"create","kwargs":{"vals_list":[{"name":"Acme"},{"name":"Globex"}],"context":{"lang":"en_US","allowed_company_ids":[1]}},""" + Instance + "}");
await Snap("create_one", """{"jsonrpc":"2.0","id":"1","result":[43]}""",
    s => s.CreateOneAsync("res.partner", OdxJson.Parse("""{"name":"Initech"}""")),
    """{"model_id":"res.partner","method":"create","kwargs":{"vals_list":[{"name":"Initech"}],"context":{"lang":"en_US","allowed_company_ids":[1]}},""" + Instance + "}");
await Snap("write", Ok,
    s => s.WriteAsync("res.partner", [41, 42], new JsonObject { ["comment"] = "via v2" }),
    """{"model_id":"res.partner","method":"write","kwargs":{"ids":[41,42],"vals":{"comment":"via v2"},"context":{"lang":"en_US","allowed_company_ids":[1]}},""" + Instance + "}");
await Snap("unlink", Ok,
    s => s.UnlinkAsync("res.partner", [41]),
    """{"model_id":"res.partner","method":"unlink","kwargs":{"ids":[41],"context":{"lang":"en_US","allowed_company_ids":[1]}},""" + Instance + "}");
await Snap("call_method (ids + kwargs, kwargs.context merged)", Ok,
    s => s.CallMethodAsync("account.move", "action_post", TestJson.Default.JsonElement, ids: [7],
        kwargs: OdxJson.Parse("""{"context":{"lang":"fr_FR"},"force":true}""")),
    """{"model_id":"account.move","method":"action_post","kwargs":{"ids":[7],"force":true,"context":{"lang":"fr_FR","allowed_company_ids":[1]}},""" + Instance + "}");
await Snap("call_method (no ids: named args only)", """{"jsonrpc":"2.0","id":"1","result":[[1,"Acme"]]}""",
    s => s.CallMethodAsync("res.partner", "name_search", TestJson.Default.JsonElement,
        kwargs: new JsonObject { ["name"] = "Acm", ["limit"] = 5 }),
    """{"model_id":"res.partner","method":"name_search","kwargs":{"name":"Acm","limit":5,"context":{"lang":"en_US","allowed_company_ids":[1]}},""" + Instance + "}");
await RunAsync("version v2: POST /v2/odoo/version {id,url}, parses {version_info, version}", VersionV2);
Run("OdxRequestBuilder.BuildExecuteV2 (raw) snapshot", RawBuilderSnapshot);
await RunAsync("JsonNode and UTF-8 byte arguments produce identical bytes", NodeAndBytesMatch);
await RunAsync("v1 /api/odoo/execute still sends user_id (unchanged)", V1Unchanged);

Console.WriteLine("error mapping (SYSTEM_ARCHITECTURE.md §7.1 table):");
await Err(401, -32000, typeof(OdxAuthException));
await Err(403, 0, typeof(OdxLicenseException));
await Err(504, -32003, typeof(OdxUpstreamTimeoutException));
await Err(502, -32004, typeof(OdxUpstreamConnectException));
await Err(500, -32005, typeof(OdxProxyInternalException));
await Err(200, -32006, typeof(OdxJson2UnavailableException));
await Err(400, -32007, typeof(OdxInvalidRequestException));
await Err(200, 401, typeof(OdxOdooAuthException));
await Err(200, 403, typeof(OdxOdooAccessException));
await Err(200, 404, typeof(OdxOdooNotFoundException));
await Err(200, 409, typeof(OdxOdooConflictException));
await Err(200, 422, typeof(OdxOdooValidationException));
await Err(200, 500, typeof(OdxOdooServerException));
await Err(200, 503, typeof(OdxOdooServerException));
await Err(400, -32001, typeof(OdxBadRequestException)); // v1 codes keep their v1 type
await Err(200, 0, typeof(OdxOdooException));            // the "code 0" case: Odoo, not license
await Err(200, 200, typeof(OdxOdooException));          // Odoo <=18 /jsonrpc error code
await RunAsync("Odoo error keeps raw error.data + exposes data.name", OdooErrorData);
await RunAsync("proxy 401 and Odoo 401 are different types", TwoAuthErrorsDiffer);

Console.WriteLine("v1/v2 picker:");
await RunAsync("SupportsV2Async: 20.x -> true, cached per URL (no 2nd round trip)", SupportsV2Cached);
await RunAsync("SupportsV2Async: -32006 -> false", SupportsV2Json2Unavailable);
await RunAsync("SupportsV2Async: 18.x -> false", SupportsV2Old);

Console.WriteLine("arguments + helpers:");
Run("domain must be an array", () => ExpectArg(() => Offline().SearchAsync("res.partner", OdxJson.Parse("""{"a":1}"""))));
Run("kwargs must be an object", () => ExpectArg(() => Offline().CallMethodAsync("res.partner", "x", TestJson.Default.JsonElement, kwargs: OdxJson.Parse("[1]"))));
Run("ids given twice (argument + kwargs) is rejected", () => ExpectArg(() => Offline().CallMethodAsync("res.partner", "x", TestJson.Default.JsonElement, ids: [1], kwargs: OdxJson.Parse("""{"ids":[2]}"""))));
Run("create_one rejects a list", () => ExpectArg(() => Offline().CreateOneAsync("res.partner", OdxJson.Parse("[{}]"))));
Run("session context must be an object", () => ExpectArg(() => Task.FromResult(OfflineClient.ForInstanceV2("u", "d", "k", OdxJson.Parse("[]")))));
Run("OdooBinary reads Odoo 20 object / bare base64 / false, writes base64", OdooBinaryRoundTrip);
RunSync("sync-over-async under a UI SynchronizationContext does not deadlock", SyncOverAsyncNoDeadlock);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

// ---- snapshot / error helpers ----

async Task Snap<T>(string name, string response, Func<OdxSessionV2, Task<T>> call, string expected, bool noContext = false)
{
    await RunAsync(name, async () =>
    {
        var server = MockServer.Start(200, response);
        using var client = Client(server.Port);
        await call(Session(client, noContext));
        string req = await server.Request;
        Assert(req.StartsWith("POST /v2/odoo/execute ", StringComparison.Ordinal), $"wrong route: {req.Split('\n')[0]}");
        AssertBody(req, expected);
    });
}

async Task Err(int http, long code, Type expected)
{
    await RunAsync($"HTTP {http} / code {code} -> {expected.Name}", async () =>
    {
        string body = """{"jsonrpc":"2.0","id":"1","error":{"code":""" + code + ""","message":"m","data":{"name":"odoo.exceptions.X"}}}""";
        var server = MockServer.Start(http, body);
        using var client = Client(server.Port);
        try
        {
            await Session(client).SearchCountAsync("res.partner", default);
            Assert(false, "expected an exception");
        }
        catch (OdxException e)
        {
            Assert(e.GetType() == expected, $"got {e.GetType().Name}");
            Assert(e.RpcCode == code, $"RpcCode {e.RpcCode}");
            Assert(e.RpcData == """{"name":"odoo.exceptions.X"}""", $"RpcData {e.RpcData}");
        }
    });
}

async Task OdooErrorData()
{
    const string data = """{"name":"odoo.exceptions.ValidationError","message":"bad","arguments":["bad"],"context":{},"debug":""}""";
    var server = MockServer.Start(200, """{"jsonrpc":"2.0","id":"1","error":{"code":422,"message":"bad","data":""" + data + "}}");
    using var client = Client(server.Port);
    try
    {
        await Session(client).WriteAsync("res.partner", [1], new JsonObject { ["name"] = "" });
        Assert(false, "expected an exception");
    }
    catch (OdxOdooValidationException e)
    {
        Assert(e.RpcData == data, "raw data lost");
        Assert(e.OdooErrorName == "odoo.exceptions.ValidationError", $"name {e.OdooErrorName}");
        Assert(e.OdooCode == 422, "OdooCode");
        Assert(e is OdxOdooException, "must stay an OdxOdooException");
    }
}

async Task TwoAuthErrorsDiffer()
{
    var proxy = MockServer.Start(401, """{"jsonrpc":"2.0","id":null,"error":{"code":-32000,"message":"Unauthorized"}}""");
    using (var c = Client(proxy.Port))
        await ExpectType<OdxAuthException>(() => Session(c).UnlinkAsync("res.partner", [1]));
    var odoo = MockServer.Start(200, """{"jsonrpc":"2.0","id":"1","error":{"code":401,"message":"Invalid apikey"}}""");
    using (var c = Client(odoo.Port))
        await ExpectType<OdxOdooAuthException>(() => Session(c).UnlinkAsync("res.partner", [1]));
    Assert(!typeof(OdxOdooAuthException).IsAssignableTo(typeof(OdxAuthException)), "Odoo 401 must not be an OdxAuthException");
}

async Task VersionV2()
{
    var server = MockServer.Start(200, """{"jsonrpc":"2.0","id":"1","result":{"version_info":[20,0,0,"final",0,"e"],"version":"20.0+e"}}""");
    using var client = Client(server.Port);
    OdxVersionInfoV2 v = await client.GetVersionV2Async("https://erp.example.com");
    string req = await server.Request;
    Assert(req.StartsWith("POST /v2/odoo/version ", StringComparison.Ordinal), $"wrong route: {req.Split('\n')[0]}");
    AssertBody(req, """{"url":"https://erp.example.com"}""");
    Assert(v.Major == 20 && v.Version == "20.0+e" && v.VersionInfo.Length == 6, $"parsed {v.Major} {v.Version}");
}

void RawBuilderSnapshot()
{
    byte[] body = OdxRequestBuilder.BuildExecuteV2("res.partner", "search_count", "https://erp.example.com", "prod", "secret",
        """{"domain":[]}"""u8, id: "abc");
    string json = Encoding.UTF8.GetString(body);
    const string expected = """{"id":"abc","model_id":"res.partner","method":"search_count","kwargs":{"domain":[]},""" + Instance + "}";
    Assert(json == expected, $"got {json}");
    Assert(!json.Contains("user_id"), "v2 must not send user_id");
    ExpectArg(() => Task.FromResult(OdxRequestBuilder.BuildExecuteV2("m", "x", "u", "d", "k", "[]"u8)));
}

async Task NodeAndBytesMatch()
{
    async Task<string> Capture(Func<OdxSessionV2, Task> call)
    {
        var server = MockServer.Start(200, Ok);
        using var client = Client(server.Port);
        await call(Session(client));
        return Regex.Replace(Body(await server.Request), "^\\{\"id\":\"[^\"]+\",", "{");
    }
    string a = await Capture(s => s.WriteAsync("res.partner", [1], new JsonObject { ["name"] = "Acme", ["active"] = true },
        context: new JsonObject { ["tz"] = "UTC" }));
    string b = await Capture(s => s.WriteAsync("res.partner", [1], """{"name":"Acme","active":true}"""u8.ToArray(),
        context: """{"tz":"UTC"}"""u8.ToArray()));
    Assert(a == b, $"\n  node:  {a}\n  bytes: {b}");
}

async Task V1Unchanged()
{
    var server = MockServer.Start(200, """{"jsonrpc":"2.0","id":"1","result":3}""");
    using var client = Client(server.Port);
    var inst = new OdooInstance { Url = "https://erp.example.com", UserId = 2, Db = "prod", ApiKey = "secret" };
    await client.ExecuteAsync(OdxAction.SearchCount, "res.partner", inst, TestJson.Default.Int64, paramsJson: "[[]]"u8.ToArray());
    string req = await server.Request;
    Assert(req.StartsWith("POST /api/odoo/execute ", StringComparison.Ordinal), "v1 route changed");
    Assert(Body(req).Contains("\"user_id\":2"), "v1 user_id missing");
}

async Task SupportsV2Cached()
{
    var server = MockServer.Start(200, """{"jsonrpc":"2.0","id":"1","result":{"version_info":[20,0,0,"final",0,"e"],"version":"20.0+e"}}""");
    using var client = Client(server.Port);
    Assert(await client.SupportsV2Async("https://a.example"), "expected true");
    await server.Request;
    // The one-shot server is gone: a second probe would fail with a transport error.
    Assert(await client.SupportsV2Async("https://a.example"), "expected cached true");
    Assert(await client.ForInstanceV2("https://a.example", "d", "k").IsSupportedAsync(), "session probe should hit the cache");
}

async Task SupportsV2Json2Unavailable()
{
    var server = MockServer.Start(200, """{"jsonrpc":"2.0","id":"1","error":{"code":-32006,"message":"JSON-2 not available"}}""");
    using var client = Client(server.Port);
    Assert(!await client.SupportsV2Async("https://old.example"), "expected false");
}

async Task SupportsV2Old()
{
    var server = MockServer.Start(200, """{"jsonrpc":"2.0","id":"1","result":{"version_info":[18,0,0,"final",0,""],"version":"18.0"}}""");
    using var client = Client(server.Port);
    Assert(!await client.SupportsV2Async("https://old.example"), "expected false");
}

void OdooBinaryRoundTrip()
{
    var opts = new JsonSerializerOptions();
    opts.Converters.Add(new OdooBinaryConverter());

    var b20 = JsonSerializer.Deserialize<OdooBinary?>("""{"content":"aGk=","filename":"a.txt","size":2}""", opts);
    Assert(b20 is { Content: "aGk=", Filename: "a.txt", Size: 2 }, "Odoo 20 object");
    Assert(b20!.GetBytes().AsSpan().SequenceEqual("hi"u8), "decode");

    var b19 = JsonSerializer.Deserialize<OdooBinary?>("\"aGk=\"", opts);
    Assert(b19 is { Content: "aGk=", Filename: null, Size: null }, "bare base64");

    Assert(JsonSerializer.Deserialize<OdooBinary?>("false", opts) is null, "false -> null");
    Assert(JsonSerializer.Serialize<OdooBinary?>(b19, opts) == "\"aGk=\"", "write bare");
    Assert(JsonSerializer.Serialize<OdooBinary?>(new OdooBinary { Content = "aGk=", Filename = "a.txt" }, opts)
        == """{"content":"aGk=","filename":"a.txt"}""", "write with filename");
    Assert(JsonSerializer.Serialize<OdooBinary?>(null, opts) == "false", "write null -> false");
}

void SyncOverAsyncNoDeadlock()
{
    var server = MockServer.Start(200, """{"jsonrpc":"2.0","id":"1","result":5}""");
    using var client = Client(server.Port);
    var session = Session(client);
    var ctx = new BlockingSyncContext();
    var prev = SynchronizationContext.Current;
    try
    {
        SynchronizationContext.SetSynchronizationContext(ctx);
        // Naive .Result on a "UI thread": must still finish (build + parse are off this context).
        long n = session.SearchCountAsync("res.partner", default).GetAwaiter().GetResult();
        Assert(n == 5, $"count {n}");
    }
    finally
    {
        SynchronizationContext.SetSynchronizationContext(prev);
    }
    Assert(ctx.Posts == 0, $"{ctx.Posts} continuation(s) were posted to the UI context");
}

// ---- plumbing ----

OdxClient Client(int port) => OdxClient.Create($"http://127.0.0.1:{port}", "proxy-key", defaultTimeoutSecs: 5);

// For argument-validation tests: nothing is ever sent (validation throws first).
OdxSessionV2 Offline() => Session(OfflineClient);

OdxSessionV2 Session(OdxClient client, bool noContext = false) => client.ForInstanceV2(
    "https://erp.example.com", "prod", "secret",
    noContext ? default : new JsonObject { ["lang"] = "en_US", ["allowed_company_ids"] = new JsonArray(1) });

static string Body(string request)
{
    int sep = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
    return sep >= 0 ? request[(sep + 4)..] : request;
}

static void AssertBody(string request, string expectedWithoutId)
{
    string body = Body(request);
    // Ids are auto-generated; snapshot everything else byte-for-byte.
    string normalized = Regex.Replace(body, "^\\{\"id\":\"[^\"]+\",", "{");
    Assert(normalized == expectedWithoutId, $"\n  expected: {expectedWithoutId}\n  actual:   {normalized}");
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

static async Task ExpectType<TEx>(Func<Task> call) where TEx : Exception
{
    try
    {
        await call();
    }
    catch (Exception e)
    {
        Assert(e.GetType() == typeof(TEx), $"expected {typeof(TEx).Name}, got {e.GetType().Name}");
        return;
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
        Console.WriteLine($"  FAIL  {name}: {e.GetType().Name}: {e.Message}");
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
        Console.WriteLine($"  FAIL  {name}: {e.GetType().Name}: {e.Message}");
    }
}

void RunSync(string name, Action test) => Run(name, test);

/// <summary>One-shot HTTP/1.1 server: reads one full request (honoring Content-Length), replies, closes.</summary>
sealed class MockServer
{
    public required int Port { get; init; }
    public required Task<string> Request { get; init; }

    public static MockServer Start(int status, string body)
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
                    $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
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

internal record Partner(long Id, string? Name);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Partner[]))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(long))]
internal partial class TestJson : JsonSerializerContext;

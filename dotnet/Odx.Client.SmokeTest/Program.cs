using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TerraKernel.OdxClient;
using TerraKernel.OdxClient.Interop;
using TerraKernel.OdxClient.Json;

int failures = 0;

await RunAsync("ExecuteAsync round-trips (real DLL, off-thread)", ExecuteRoundTrip);
RunSync("Sync-over-async under SynchronizationContext does not deadlock", SyncOverAsyncNoDeadlock);
await RunAsync("Cancellation delivers OperationCanceledException", CancelInFlight);
await RunAsync("GetAboutAsync routes GET /_/about", GetAboutRoute);
await RunAsync("ExecuteAsync<T> deserializes result off-thread", TypedExecute);
await RunAsync("HTTP 200 with error body throws OdxOdooException", OdooErrorOn200);
await RunAsync("GetAboutAsync<T> deserializes flat body", TypedAbout);
RunSync("Many2One converter round-trips (read [id,name]/false, write bare id)", Many2OneRoundTrip);
await RunAsync("ExecuteAsync (structured builder) writes a correct envelope", StructuredExecute);
RunSync("OdxAction values serialize to their exact wire strings", OdxActionWireStrings);

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("ALL PASSED");
    return 0;
}
Console.WriteLine($"{failures} FAILED");
return 1;

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

void RunSync(string name, Action test)
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

async Task ExecuteRoundTrip()
{
    byte[] body = U8("""{"jsonrpc":"2.0","id":"t","result":[]}""");
    var (port, server) = StartOneShotServer(body);
    using var client = OdxClient.Create($"http://127.0.0.1:{port}", "test-key", defaultTimeoutSecs: 5);

    byte[] req = U8("""{"id":"t","action":"search_read","model_id":"res.partner","keyword":{},"params":[],"odoo_instance":{"url":"x","user_id":1,"db":"d","api_key":"k"}}""");
    OdxResponse resp = await client.ExecuteAsync(req);
    server.Join();

    Assert(resp.Status == OdxStatus.Ok, $"status was {resp.Status}");
    Assert(resp.HttpStatus == 200, $"http was {resp.HttpStatus}");
    Assert(resp.Body.AsSpan().SequenceEqual(body), "body mismatch");
}

async Task GetAboutRoute()
{
    byte[] body = U8("""{"build":"b1","version":"0.1.0"}""");
    var (port, server) = StartOneShotServer(body);
    using var client = OdxClient.Create($"http://127.0.0.1:{port}", "k", defaultTimeoutSecs: 5);

    OdxResponse resp = await client.GetAboutAsync();
    server.Join();

    Assert(resp.Status == OdxStatus.Ok, $"status was {resp.Status}");
    Assert(resp.Body.AsSpan().SequenceEqual(body), "body mismatch");
}

async Task CancelInFlight()
{
    // Server stalls without responding, so the cancel wins.
    var (port, _) = StartOneShotServer(Array.Empty<byte>(), delayMs: 3000, respond: false);
    using var client = OdxClient.Create($"http://127.0.0.1:{port}", "k", defaultTimeoutSecs: 30);

    byte[] req = U8("""{"id":"c","action":"search","model_id":"res.partner","keyword":{},"params":[],"odoo_instance":{"url":"x","user_id":1,"db":"d","api_key":"k"}}""");
    using var cts = new CancellationTokenSource();
    Task<OdxResponse> task = client.ExecuteAsync(req, cancellationToken: cts.Token);
    cts.Cancel();

    try
    {
        await task;
        Assert(false, "expected OperationCanceledException");
    }
    catch (OperationCanceledException)
    {
        // expected
    }
}

// Proves ConfigureAwait(false) coverage: under a UI-like SynchronizationContext that
// never pumps (its thread is blocked in GetResult), a sync-over-async call must still
// complete — and no continuation may be posted back to that context.
void SyncOverAsyncNoDeadlock()
{
    byte[] body = U8("""{"jsonrpc":"2.0","id":"t","result":[]}""");
    var (port, server) = StartOneShotServer(body);

    Exception? error = null;
    OdxResponse? result = null;
    int posts = 0;

    var worker = new Thread(() =>
    {
        var prev = SynchronizationContext.Current;
        var sc = new BlockingSyncContext();
        SynchronizationContext.SetSynchronizationContext(sc);
        try
        {
            using var client = OdxClient.Create($"http://127.0.0.1:{port}", "k", defaultTimeoutSecs: 5);
            byte[] req = U8("""{"id":"t","action":"search_read","model_id":"res.partner","keyword":{},"params":[],"odoo_instance":{"url":"x","user_id":1,"db":"d","api_key":"k"}}""");
            result = client.ExecuteAsync(req).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            error = e;
        }
        finally
        {
            posts = sc.Posts;
            SynchronizationContext.SetSynchronizationContext(prev);
        }
    })
    { IsBackground = true };

    worker.Start();
    bool finished = worker.Join(TimeSpan.FromSeconds(15));
    server.Join();

    Assert(finished, "deadlocked under SynchronizationContext (missing ConfigureAwait(false)?)");
    Assert(error is null, $"threw {error?.GetType().Name}: {error?.Message}");
    Assert(result is { Status: OdxStatus.Ok }, "result was not Ok");
    Assert(posts == 0, $"a continuation was posted to the UI SynchronizationContext {posts} time(s) — ConfigureAwait(false) missing");
}

async Task TypedExecute()
{
    byte[] respBody = U8("""{"jsonrpc":"2.0","id":"t","result":[1,2,3]}""");
    var (port, server) = StartOneShotServer(respBody);
    using var client = OdxClient.Create($"http://127.0.0.1:{port}", "k", defaultTimeoutSecs: 5);

    byte[] req = U8("""{"id":"t","action":"search","model_id":"res.partner","keyword":{},"params":[],"odoo_instance":{"url":"x","user_id":1,"db":"d","api_key":"k"}}""");
    long[]? ids = await client.ExecuteAsync(req, Int64ArrayInfo());
    server.Join();

    Assert(ids is { Length: 3 } && ids[0] == 1 && ids[1] == 2 && ids[2] == 3, "ids mismatch");
}

async Task OdooErrorOn200()
{
    // HTTP 200 but the envelope carries an Odoo logic error.
    byte[] respBody = U8("""{"jsonrpc":"2.0","id":"t","error":{"code":2,"message":"Access Denied","data":{"name":"AccessError"}}}""");
    var (port, server) = StartOneShotServer(respBody);
    using var client = OdxClient.Create($"http://127.0.0.1:{port}", "k", defaultTimeoutSecs: 5);

    byte[] req = U8("""{"id":"t","action":"search","model_id":"res.partner","keyword":{},"params":[],"odoo_instance":{"url":"x","user_id":1,"db":"d","api_key":"k"}}""");
    try
    {
        _ = await client.ExecuteAsync(req, Int64ArrayInfo());
        server.Join();
        Assert(false, "expected OdxOdooException");
    }
    catch (OdxOdooException ex)
    {
        server.Join();
        Assert(ex.OdooCode == 2, $"OdooCode was {ex.OdooCode}");
        Assert(ex.Message.Contains("Access Denied"), $"message was '{ex.Message}'");
        Assert(ex.RpcData is not null && ex.RpcData.Contains("AccessError"), "error.data not captured");
    }
}

async Task TypedAbout()
{
    byte[] respBody = U8("""{"build":"b1","version":"0.1.0"}""");
    var (port, server) = StartOneShotServer(respBody);
    using var client = OdxClient.Create($"http://127.0.0.1:{port}", "k", defaultTimeoutSecs: 5);

    AboutInfo? about = await client.GetAboutAsync(SmokeJsonContext.Default.AboutInfo);
    server.Join();

    Assert(about is not null && about.Build == "b1" && about.Version == "0.1.0", "about mismatch");
}

void Many2OneRoundTrip()
{
    var opts = new JsonSerializerOptions();
    opts.Converters.Add(new Many2OneConverter());

    var m = JsonSerializer.Deserialize<Many2One>("""[5,"Acme"]""", opts);
    Assert(m is { HasValue: true, Id: 5, Name: "Acme" }, "read [id,name] failed");

    var unset = JsonSerializer.Deserialize<Many2One>("false", opts);
    Assert(!unset.HasValue, "read false-as-unset failed");

    Assert(JsonSerializer.Serialize(m, opts) == "5", "write should emit the bare id");
    Assert(JsonSerializer.Serialize(Many2One.Unset, opts) == "false", "write unset should emit false");
}

async Task StructuredExecute()
{
    byte[] respBody = U8("""{"jsonrpc":"2.0","id":"1","result":[7,8]}""");
    var reqBox = new StrongBox<string?>();
    var (port, server) = StartOneShotServer(respBody, capture: reqBox);
    using var client = OdxClient.Create($"http://127.0.0.1:{port}", "k", defaultTimeoutSecs: 5);

    var instance = new OdooInstance { Url = "https://odoo.example", UserId = 2, Db = "mydb", ApiKey = "secret" };
    long[]? ids = await client.ExecuteAsync(
        "search_read", "res.partner", instance, Int64ArrayInfo(),
        paramsJson: U8("""[[["is_company","=",true]]]"""),
        keywordJson: U8("""{"fields":["name"],"limit":80}"""));
    server.Join();

    Assert(ids is { Length: 2 } && ids[0] == 7 && ids[1] == 8, "ids mismatch");

    string req = reqBox.Value ?? "";
    int sep = req.IndexOf("\r\n\r\n", StringComparison.Ordinal);
    string body = sep >= 0 ? req[(sep + 4)..] : req;
    Assert(body.Contains("\"action\":\"search_read\"", StringComparison.Ordinal), "action missing");
    Assert(body.Contains("\"model_id\":\"res.partner\"", StringComparison.Ordinal), "model_id missing");
    Assert(body.Contains("\"user_id\":2", StringComparison.Ordinal), "odoo_instance.user_id missing");
    Assert(body.Contains("\"api_key\":\"secret\"", StringComparison.Ordinal), "odoo_instance.api_key missing");
    Assert(body.Contains("is_company", StringComparison.Ordinal), "params fragment not spliced");
    Assert(body.Contains("\"limit\":80", StringComparison.Ordinal), "keyword fragment not spliced");
}

void OdxActionWireStrings()
{
    var instance = new OdooInstance { Url = "u", UserId = 1, Db = "d", ApiKey = "k" };
    (OdxAction Action, string Wire)[] cases =
    [
        (OdxAction.SearchCount, "search_count"),
        (OdxAction.Search,      "search"),
        (OdxAction.Read,        "read"),
        (OdxAction.FieldsGet,   "fields_get"),
        (OdxAction.SearchRead,  "search_read"),
        (OdxAction.Create,      "create"),
        (OdxAction.Write,       "write"),
        (OdxAction.Unlink,      "unlink"),
        (OdxAction.CallMethod,  "call_method"),
    ];

    foreach (var (action, wire) in cases)
    {
        string? fn = action == OdxAction.CallMethod ? "my_method" : null;
        byte[] body = OdxRequestBuilder.BuildExecute(action, "res.partner", instance, fnName: fn);
        string json = Encoding.UTF8.GetString(body);
        Assert(json.Contains($"\"action\":\"{wire}\"", StringComparison.Ordinal), $"{action} => expected \"{wire}\", got: {json}");
    }

    // If a new OdxAction value is added without a wire mapping + a case above, this trips.
    Assert(Enum.GetValues<OdxAction>().Length == cases.Length, "an OdxAction value has no wire-string test case");

    // call_method without fnName must fail fast client-side (the proxy would return -32002).
    try
    {
        OdxRequestBuilder.BuildExecute(OdxAction.CallMethod, "res.partner", instance);
        Assert(false, "call_method without fnName should throw ArgumentException");
    }
    catch (ArgumentException)
    {
        // expected
    }
}

static JsonTypeInfo<long[]> Int64ArrayInfo() =>
    (JsonTypeInfo<long[]>)SmokeJsonContext.Default.GetTypeInfo(typeof(long[]))!;

static byte[] U8(string s) => Encoding.UTF8.GetBytes(s);

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

// Starts a one-shot HTTP/1.1 server on a random loopback port; handles a single
// connection on its own thread. Returns (port, serverThread).
static (int Port, Thread Server) StartOneShotServer(byte[] responseBody, int delayMs = 0, bool respond = true, StrongBox<string?>? capture = null)
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;

    var thread = new Thread(() =>
    {
        try
        {
            using TcpClient sock = listener.AcceptTcpClient();
            using NetworkStream stream = sock.GetStream();
            var buf = new byte[8192];
            int n = stream.Read(buf, 0, buf.Length);
            if (capture is not null)
                capture.Value = Encoding.UTF8.GetString(buf, 0, Math.Max(n, 0));
            if (delayMs > 0)
                Thread.Sleep(delayMs);
            if (respond)
            {
                byte[] head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {responseBody.Length}\r\nConnection: close\r\n\r\n");
                stream.Write(head);
                stream.Write(responseBody);
                stream.Flush();
            }
        }
        catch
        {
            // client cancelled / connection reset — fine for a one-shot test server.
        }
        finally
        {
            listener.Stop();
        }
    })
    { IsBackground = true };

    thread.Start();
    return (port, thread);
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

internal record AboutInfo(string Build, string Version);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(AboutInfo))]
internal partial class SmokeJsonContext : JsonSerializerContext;

using System.Net;
using System.Net.Sockets;
using System.Text;

// Constant-latency loopback HTTP mock. Args: --port <n> --size small|large
// Returns a fixed JSON-RPC envelope for every request, with HTTP/1.1 keep-alive.

int port = 6699;
string size = "small";
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
    else if (args[i] == "--size") size = args[i + 1];
}

byte[] body = size == "large" ? LargeEnvelope() : SmallEnvelope();
byte[] response = BuildResponse(body);

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Console.WriteLine($"MOCK LISTENING 127.0.0.1:{port} size={size} bodyBytes={body.Length}");

while (true)
{
    TcpClient client = listener.AcceptTcpClient();
    var t = new Thread(() => Serve(client, response)) { IsBackground = true };
    t.Start();
}

static void Serve(TcpClient client, byte[] response)
{
    try
    {
        client.NoDelay = true;
        using NetworkStream ns = client.GetStream();
        var acc = new List<byte>(2048);
        var tmp = new byte[4096];

        while (true)
        {
            // Frame one full HTTP request (headers + Content-Length body) before replying.
            int headerEnd;
            while ((headerEnd = IndexOfCrlfCrlf(acc)) < 0)
            {
                int n = ns.Read(tmp, 0, tmp.Length);
                if (n <= 0) return;
                acc.AddRange(new ReadOnlySpan<byte>(tmp, 0, n));
            }

            int contentLength = ParseContentLength(acc, headerEnd);
            int total = headerEnd + 4 + contentLength;
            while (acc.Count < total)
            {
                int n = ns.Read(tmp, 0, tmp.Length);
                if (n <= 0) return;
                acc.AddRange(new ReadOnlySpan<byte>(tmp, 0, n));
            }

            ns.Write(response, 0, response.Length);
            acc.RemoveRange(0, total); // keep any bytes belonging to the next request
        }
    }
    catch
    {
        // client closed / reset — normal at end of a bench run.
    }
}

static int IndexOfCrlfCrlf(List<byte> b)
{
    for (int i = 0; i + 3 < b.Count; i++)
        if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10)
            return i;
    return -1;
}

static int ParseContentLength(List<byte> b, int headerEnd)
{
    string headers = Encoding.ASCII.GetString(b.GetRange(0, headerEnd).ToArray());
    foreach (string line in headers.Split("\r\n"))
    {
        int c = line.IndexOf(':');
        if (c > 0 && line.AsSpan(0, c).Trim().Equals("content-length", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(line.AsSpan(c + 1).Trim(), out int v) ? v : 0;
    }
    return 0;
}

static byte[] BuildResponse(byte[] body)
{
    string head = "HTTP/1.1 200 OK\r\n" +
                  "Content-Type: application/json\r\n" +
                  $"Content-Length: {body.Length}\r\n" +
                  "Connection: keep-alive\r\n\r\n";
    byte[] h = Encoding.ASCII.GetBytes(head);
    var buf = new byte[h.Length + body.Length];
    Buffer.BlockCopy(h, 0, buf, 0, h.Length);
    Buffer.BlockCopy(body, 0, buf, h.Length, body.Length);
    return buf;
}

static byte[] SmallEnvelope() =>
    Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":"1","result":2861}""");

static byte[] LargeEnvelope()
{
    var sb = new StringBuilder("""{"jsonrpc":"2.0","id":"1","result":[""");
    for (int i = 1; i <= 100; i++)
    {
        if (i > 1) sb.Append(',');
        sb.Append("{\"id\":").Append(i).Append(",\"name\":\"Partner ").Append(i.ToString("D6")).Append("\"}");
    }
    sb.Append("]}");
    return Encoding.UTF8.GetBytes(sb.ToString());
}

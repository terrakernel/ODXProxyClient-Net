using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraKernel.OdxClient;

/// <summary>
/// ODXBrain, the optional retrieval sidecar behind odxproxy (<c>/brain/v1/*</c>): ask which
/// Odoo model holds some data, or which SOP covers a task, and get ranked candidates back.
/// Reach it as <see cref="OdxClient.Brain"/>. It needs no Odoo credentials (proxy
/// <c>x-api-key</c> only), so it lives on the client, not on an instance session.
/// </summary>
/// <remarks>
/// <para>Many proxies never run ODXBrain. "Not installed" is a normal state:
/// <see cref="IsAvailableAsync"/> tells you up front, and a call on such a proxy throws
/// <see cref="OdxBrainNotInstalledException"/> (never retry it).</para>
/// <para>Unset arguments are <b>omitted</b> from the request, never sent as <c>null</c>, and
/// no fact is ever guessed. Results keep every candidate in order, with unknown fields intact.</para>
/// <para>Threading is the same as the rest of the client: the response is parsed on the
/// thread pool, never on a captured UI <see cref="SynchronizationContext"/>.</para>
/// </remarks>
public sealed class OdxBrain
{
    private readonly OdxClient _client;
    private readonly ConcurrentDictionary<string, OdxBrainFacts> _facts = new(StringComparer.Ordinal);

    internal OdxBrain(OdxClient client) => _client = client;

    /// <summary>
    /// <c>POST /brain/v1/resolve</c>: which Odoo model holds this? Returns every candidate,
    /// best first. Follow up with <c>fields_get</c> on the model you pick.
    /// </summary>
    /// <param name="query">The question, in any supported language.</param>
    /// <param name="facts">Odoo version/edition to filter on; unset facts are omitted (filter nothing).</param>
    /// <param name="k">How many candidates (ODXBrain's default is 3). Omitted when null.</param>
    /// <param name="extra">A JSON object of fields this SDK doesn't know yet, forwarded unchanged.</param>
    /// <param name="timeoutSecs">Per-call <c>x-request-timeout</c> (0 = client default).</param>
    /// <param name="cancellationToken">Cancels the in-flight request.</param>
    public async Task<OdxBrainModelResult> ResolveModelAsync(
        string query,
        OdxBrainFacts facts = default,
        int? k = null,
        OdxJson extra = default,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
    {
        byte[] body = BuildResolve(query, facts, null, null, k, extra);
        OdxResponse resp = await ResolveModelAsync(body, timeoutSecs, cancellationToken).ConfigureAwait(false);
        return Parse(resp, OdxBrainJsonContext.Default.OdxBrainModelResult, static (r, raw) => r.Raw = raw);
    }

    /// <summary>
    /// <c>POST /brain/v1/sops/resolve</c>: which SOP covers this task? If the result's
    /// <see cref="OdxBrainSopResult.IsNoMatch"/> is true, stop and ask; don't run the nearest SOP.
    /// </summary>
    /// <param name="query">The task description.</param>
    /// <param name="facts">Odoo version/edition to filter on; unset facts are omitted.</param>
    /// <param name="installedModules">Installed module names (<c>ir.module.module</c>, state <c>installed</c>). Omitted when null.</param>
    /// <param name="company">The key the SOPs were published under. Omitted when null.</param>
    /// <param name="k">How many candidates (capped at 10 upstream). Omitted when null.</param>
    /// <param name="extra">A JSON object of fields this SDK doesn't know yet, forwarded unchanged.</param>
    /// <param name="timeoutSecs">Per-call <c>x-request-timeout</c> (0 = client default).</param>
    /// <param name="cancellationToken">Cancels the in-flight request.</param>
    public async Task<OdxBrainSopResult> ResolveSopAsync(
        string query,
        OdxBrainFacts facts = default,
        IReadOnlyList<string>? installedModules = null,
        string? company = null,
        int? k = null,
        OdxJson extra = default,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
    {
        byte[] body = BuildResolve(query, facts, installedModules, company, k, extra);
        OdxResponse resp = await ResolveSopAsync(body, timeoutSecs, cancellationToken).ConfigureAwait(false);
        return Parse(resp, OdxBrainJsonContext.Default.OdxBrainSopResult, static (r, raw) => r.Raw = raw);
    }

    /// <summary>
    /// <c>GET /brain/v1/info</c>: ODXBrain's pre-flight (embedding model, corpus and SOP set
    /// loaded, Odoo version range), returned as the raw <c>result</c> object.
    /// </summary>
    public async Task<JsonElement> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        OdxResponse resp = await _client.SubmitGet(cancellationToken, OdxClient.Endpoint.BrainInfo).ConfigureAwait(false);
        return Envelope.ReadBrainResult(resp.Body, resp.Status, resp.HttpStatus, OdxBrainJsonContext.Default.JsonElement);
    }

    /// <summary>
    /// Whether this proxy is configured to forward to ODXBrain (<c>GET /_/about</c>,
    /// <c>result.features.odxbrain</c>). A missing <c>features</c> (proxy older than 0.10.0) is
    /// <see langword="false"/>. <see langword="true"/> means "configured", not "up right now".
    /// Not cached. Network and proxy errors are thrown.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        OdxResponse resp = await _client.SubmitGet(cancellationToken, OdxClient.Endpoint.About).ConfigureAwait(false);
        if (resp.Status != Interop.OdxStatus.Ok)
            throw Envelope.MapError(resp.Status, resp.HttpStatus, null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(resp.Body);
            JsonElement root = doc.RootElement;
            // Envelope ({result:{features}}); tolerate a flat body too.
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result"u8, out JsonElement result))
                root = result;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("features"u8, out JsonElement features)
                && features.ValueKind == JsonValueKind.Object
                && features.TryGetProperty("odxbrain"u8, out JsonElement flag)
                && flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException e)
        {
            throw new OdxServerException(resp.Status, "malformed /_/about body: " + e.Message, null, null);
        }
    }

    /// <summary>
    /// The ODXBrain facts for the Odoo at <paramref name="odooUrl"/>, from v1's
    /// <c>/api/odoo/version</c> (works on every Odoo version): <c>odoo_version</c> =
    /// <c>server_serie</c>, <c>edition</c> = <c>enterprise</c> iff <c>server_version</c> contains
    /// <c>"+e"</c>. Cached per URL for the life of this client (shared with
    /// <see cref="OdxSessionV2.GetBrainFactsAsync"/>). A fact Odoo doesn't report stays null.
    /// </summary>
    public async Task<OdxBrainFacts> GetFactsAsync(string odooUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(odooUrl);
        if (_facts.TryGetValue(odooUrl, out OdxBrainFacts cached))
            return cached;

        JsonElement v = await _client.GetVersionAsync(odooUrl, OdxBrainJsonContext.Default.JsonElement, 0, cancellationToken).ConfigureAwait(false);
        string? serie = GetString(v, "server_serie"u8);
        string? serverVersion = GetString(v, "server_version"u8);
        var facts = new OdxBrainFacts(
            string.IsNullOrEmpty(serie) ? null : serie,
            serverVersion is null ? null : EditionOf(serverVersion));
        return _facts[odooUrl] = facts;
    }

    /// <summary>v2 flavour of <see cref="GetFactsAsync"/>, used by <see cref="OdxSessionV2.GetBrainFactsAsync"/>.</summary>
    internal async Task<OdxBrainFacts> GetFactsV2Async(string odooUrl, CancellationToken cancellationToken)
    {
        if (_facts.TryGetValue(odooUrl, out OdxBrainFacts cached))
            return cached;

        OdxVersionInfoV2 v = await _client.GetVersionV2Async(odooUrl, 0, cancellationToken).ConfigureAwait(false);
        string? odooVersion = v.VersionInfo.Length >= 2
            ? $"{ElementText(v.VersionInfo[0])}.{ElementText(v.VersionInfo[1])}"
            : null;
        var facts = new OdxBrainFacts(odooVersion, string.IsNullOrEmpty(v.Version) ? null : EditionOf(v.Version));
        return _facts[odooUrl] = facts;
    }

    // ---- raw-body overloads (request built by the caller; response returned unparsed) ----

    /// <summary>POST <c>/brain/v1/resolve</c> with a full JSON body (must include <c>id</c>); raw response.</summary>
    public Task<OdxResponse> ResolveModelAsync(ReadOnlyMemory<byte> body, uint timeoutSecs = 0, CancellationToken cancellationToken = default)
        => _client.SubmitWithBody(body, timeoutSecs, cancellationToken, OdxClient.Endpoint.BrainResolve);

    /// <summary>POST <c>/brain/v1/sops/resolve</c> with a full JSON body (must include <c>id</c>); raw response.</summary>
    public Task<OdxResponse> ResolveSopAsync(ReadOnlyMemory<byte> body, uint timeoutSecs = 0, CancellationToken cancellationToken = default)
        => _client.SubmitWithBody(body, timeoutSecs, cancellationToken, OdxClient.Endpoint.BrainSopsResolve);

    // ---- internals ----

    // Don't cut at '+': packaged builds report "20.0+e-20260925", pre-releases "20.0alpha1+e".
    // Never use version_info[5] for edition.
    internal static string EditionOf(string version)
        => version.Contains("+e", StringComparison.Ordinal) ? OdxBrainFacts.Enterprise : OdxBrainFacts.Community;

    private static T Parse<T>(OdxResponse resp, JsonTypeInfo<T> type, Action<T, JsonElement> setRaw) where T : class
    {
        JsonElement raw = Envelope.ReadBrainResult(resp.Body, resp.Status, resp.HttpStatus, OdxBrainJsonContext.Default.JsonElement);
        if (raw.ValueKind != JsonValueKind.Object)
            throw new OdxBrainException(resp.Status, $"ODXBrain result is {raw.ValueKind}, expected an object", null, null);
        T result = raw.Deserialize(type)!;
        setRaw(result, raw);
        return result;
    }

    private static readonly string[] Reserved = ["id", "query", "odoo_version", "edition", "installed_modules", "company", "k"];

    internal static byte[] BuildResolve(
        string query, OdxBrainFacts facts, IReadOnlyList<string>? installedModules, string? company, int? k, OdxJson extra)
    {
        ArgumentNullException.ThrowIfNull(query); // empty is ODXBrain's 400 to report, not ours
        List<(string Name, OdxJson Value)> extras = extra.GetProperties(nameof(extra));
        foreach (var (name, _) in extras)
        {
            if (Array.IndexOf(Reserved, name) >= 0)
                throw new ArgumentException($"'{name}' has its own parameter; don't pass it in extra.", nameof(extra));
        }

        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id"u8, OdxRequestBuilder.NextId());
            w.WriteString("query"u8, query);
            if (facts.OdooVersion is not null) w.WriteString("odoo_version"u8, facts.OdooVersion);
            if (facts.Edition is not null) w.WriteString("edition"u8, facts.Edition);
            if (installedModules is not null)
            {
                w.WriteStartArray("installed_modules"u8);
                foreach (string m in installedModules)
                    w.WriteStringValue(m);
                w.WriteEndArray();
            }
            if (company is not null) w.WriteString("company"u8, company);
            if (k is int kv) w.WriteNumber("k"u8, kv);
            foreach (var (name, value) in extras)
            {
                w.WritePropertyName(name);
                value.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static string? GetString(JsonElement obj, ReadOnlySpan<byte> name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string ElementText(JsonElement e)
        => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText();
}

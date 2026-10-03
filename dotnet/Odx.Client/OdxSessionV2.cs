using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraKernel.OdxClient;

/// <summary>
/// A v2 (JSON-2, Odoo 19+) session bound to one Odoo instance: <c>POST /v2/odoo/execute</c>.
/// Create it with <see cref="OdxClient.ForInstanceV2(string, string, string, OdxJson)"/>.
/// Immutable and thread-safe; it does not own the <see cref="OdxClient"/>.
/// </summary>
/// <remarks>
/// <para>v2 arguments are <b>named only</b>. Every method sends its <c>kwargs</c> keys exactly as
/// Odoo's Python parameter names (<c>domain</c>, <c>fields</c>, <c>vals_list</c>, <c>allfields</c>,
/// <c>ids</c>, …); Odoo rejects an unknown or misspelled name with a 422
/// (<see cref="OdxOdooValidationException"/>). Arguments left <see langword="null"/>/<c>default</c>
/// are omitted, so Odoo's own defaults apply. <c>ids</c> is sent only to record methods.</para>
/// <para>The session-level default <c>context</c> is merged into every call; a call's own
/// <c>context</c> keys win.</para>
/// <para>Threading is the same as v1: request building runs off a captured UI
/// <see cref="SynchronizationContext"/>, and the response is parsed on the thread pool.</para>
/// </remarks>
public sealed class OdxSessionV2
{
    private readonly OdxClient _client;
    private readonly byte[] _instanceJson;
    private readonly (string Name, byte[] Value)[] _defaultContext;

    internal OdxSessionV2(OdxClient client, string url, string db, string apiKey, OdxJson context)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(db);
        ArgumentException.ThrowIfNullOrEmpty(apiKey);

        _client = client;
        Url = url;
        Db = db;
        _instanceJson = OdxRequestBuilder.BuildInstanceV2(url, db, apiKey);
        // Snapshot the default context once, so later mutation of the caller's
        // JsonObject cannot change in-flight or future calls.
        _defaultContext = context.GetProperties(nameof(context))
            .Select(p => (p.Name, p.Value.ToUtf8Array()))
            .ToArray();
    }

    /// <summary>The Odoo base URL this session targets.</summary>
    public string Url { get; }

    /// <summary>The Odoo database (sent upstream as <c>X-Odoo-Database</c>).</summary>
    public string Db { get; }

    // ---- data methods (same names as v1's OdxAction values) ----

    /// <summary>
    /// <c>search(domain, offset, limit, order)</c> → the matching ids. An unset
    /// <paramref name="domain"/> is sent as <c>[]</c> (every record).
    /// </summary>
    public Task<long[]> SearchAsync(
        string model,
        OdxJson domain,
        int? offset = null,
        int? limit = null,
        string? order = null,
        OdxJson context = default,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
    {
        RequireArrayOrEmpty(domain, nameof(domain));
        return SendNonNull(model, "search", w =>
        {
            w.WritePropertyName("domain"u8);
            WriteOrEmptyArray(w, domain);
            WriteOpt(w, "offset"u8, offset);
            WriteOpt(w, "limit"u8, limit);
            WriteOpt(w, "order"u8, order);
        }, context, OdxInternalJsonContext.Default.Int64Array, timeoutSecs, cancellationToken);
    }

    /// <summary>
    /// <c>search_read(domain, fields, offset, limit, order)</c> → records, deserialized into
    /// <typeparamref name="T"/> (e.g. <c>Partner[]</c>). An unset <paramref name="domain"/> is
    /// omitted (every record).
    /// </summary>
    public Task<T?> SearchReadAsync<T>(
        string model,
        JsonTypeInfo<T> resultType,
        OdxJson domain = default,
        IReadOnlyList<string>? fields = null,
        int? offset = null,
        int? limit = null,
        string? order = null,
        OdxJson context = default,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
    {
        RequireArrayOrEmpty(domain, nameof(domain));
        return Send(model, "search_read", w =>
        {
            WriteOpt(w, "domain"u8, domain);
            WriteOpt(w, "fields"u8, fields);
            WriteOpt(w, "offset"u8, offset);
            WriteOpt(w, "limit"u8, limit);
            WriteOpt(w, "order"u8, order);
        }, context, resultType, timeoutSecs, cancellationToken);
    }

    /// <summary>
    /// <c>search_count(domain, limit)</c> → the number of matching records. An unset
    /// <paramref name="domain"/> is sent as <c>[]</c>.
    /// </summary>
    public async Task<long> SearchCountAsync(
        string model,
        OdxJson domain,
        int? limit = null,
        OdxJson context = default,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
    {
        RequireArrayOrEmpty(domain, nameof(domain));
        return await Send(model, "search_count", w =>
        {
            w.WritePropertyName("domain"u8);
            WriteOrEmptyArray(w, domain);
            WriteOpt(w, "limit"u8, limit);
        }, context, OdxInternalJsonContext.Default.Int64, timeoutSecs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>read(fields, load)</c> on <paramref name="ids"/> → records, deserialized into <typeparamref name="T"/>.</summary>
    public Task<T?> ReadAsync<T>(
        string model,
        IReadOnlyList<long> ids,
        JsonTypeInfo<T> resultType,
        IReadOnlyList<string>? fields = null,
        string? load = null,
        OdxJson context = default,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return Send(model, "read", w =>
        {
            WriteIds(w, ids);
            WriteOpt(w, "fields"u8, fields);
            WriteOpt(w, "load"u8, load);
        }, context, resultType, timeoutSecs, cancellationToken);
    }

    /// <summary><c>fields_get(allfields, attributes)</c> → field descriptions keyed by name.</summary>
    public Task<T?> FieldsGetAsync<T>(
        string model,
        JsonTypeInfo<T> resultType,
        IReadOnlyList<string>? allfields = null,
        IReadOnlyList<string>? attributes = null,
        OdxJson context = default,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
        => Send(model, "fields_get", w =>
        {
            WriteOpt(w, "allfields"u8, allfields);
            WriteOpt(w, "attributes"u8, attributes);
        }, context, resultType, timeoutSecs, cancellationToken);

    /// <summary>
    /// <c>create(vals_list)</c> → <b>always an array of ids</b>. <paramref name="vals"/> is one
    /// record (a JSON object) or several (a JSON array of objects); <c>vals_list</c> is always
    /// sent as an array. For a single id use <see cref="CreateOneAsync"/>.
    /// </summary>
    public Task<long[]> CreateAsync(
        string model,
        OdxJson vals,
        OdxJson context = default,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
    {
        JsonValueKind kind = vals.Kind;
        if (kind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new ArgumentException("vals must be a JSON object or an array of objects.", nameof(vals));
        return SendNonNull(model, "create", w =>
        {
            w.WritePropertyName("vals_list"u8);
            if (kind == JsonValueKind.Array)
            {
                vals.WriteTo(w);
            }
            else
            {
                w.WriteStartArray();
                vals.WriteTo(w);
                w.WriteEndArray();
            }
        }, context, OdxInternalJsonContext.Default.Int64Array, timeoutSecs, cancellationToken);
    }

    /// <summary>Create one record (<paramref name="vals"/> is a JSON object) and return its id.</summary>
    public async Task<long> CreateOneAsync(
        string model,
        OdxJson vals,
        OdxJson context = default,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
    {
        if (vals.Kind != JsonValueKind.Object)
            throw new ArgumentException("vals must be a JSON object (one record).", nameof(vals));
        long[] ids = await CreateAsync(model, vals, context, timeoutSecs, cancellationToken).ConfigureAwait(false);
        return ids.Length > 0
            ? ids[0]
            : throw new OdxException(Interop.OdxStatus.Ok, "create returned no ids");
    }

    /// <summary><c>write(vals)</c> on <paramref name="ids"/>. Returns Odoo's result (<see langword="true"/>).</summary>
    public async Task<bool> WriteAsync(
        string model,
        IReadOnlyList<long> ids,
        OdxJson vals,
        OdxJson context = default,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (vals.Kind != JsonValueKind.Object)
            throw new ArgumentException("vals must be a JSON object.", nameof(vals));
        return await Send(model, "write", w =>
        {
            WriteIds(w, ids);
            w.WritePropertyName("vals"u8);
            vals.WriteTo(w);
        }, context, OdxInternalJsonContext.Default.Boolean, timeoutSecs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>unlink()</c> on <paramref name="ids"/>. Returns Odoo's result (<see langword="true"/>).</summary>
    public async Task<bool> UnlinkAsync(
        string model,
        IReadOnlyList<long> ids,
        OdxJson context = default,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return await Send(model, "unlink", w => WriteIds(w, ids),
            context, OdxInternalJsonContext.Default.Boolean, timeoutSecs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Call any public model method. v2 has no positional arguments: <paramref name="kwargs"/>
    /// (a JSON object) holds every argument under its Odoo Python parameter name — see the
    /// method's definition or <c>/doc/&lt;model&gt;</c> in Odoo. <paramref name="ids"/> is sent as
    /// <c>ids</c> only when given; pass it only for record methods, never <c>@api.model</c>
    /// ones. A <c>context</c> inside <paramref name="kwargs"/> is merged over the session default.
    /// A returned recordset arrives as its list of ids.
    /// </summary>
    public Task<T?> CallMethodAsync<T>(
        string model,
        string method,
        JsonTypeInfo<T> resultType,
        IReadOnlyList<long>? ids = null,
        OdxJson kwargs = default,
        uint timeoutSecs = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        OdxJson callContext = default;
        var extra = new List<(string Name, OdxJson Value)>();
        foreach (var p in kwargs.GetProperties(nameof(kwargs)))
        {
            if (p.Name == "context")
            {
                if (p.Value.Kind != JsonValueKind.Object)
                    throw new ArgumentException("kwargs.context must be a JSON object.", nameof(kwargs));
                callContext = p.Value;
            }
            else if (p.Name == "ids" && ids is not null)
            {
                throw new ArgumentException("Pass ids either as the ids argument or inside kwargs, not both.", nameof(kwargs));
            }
            else
            {
                extra.Add(p);
            }
        }

        return Send(model, method, w =>
        {
            if (ids is not null)
                WriteIds(w, ids);
            foreach (var (name, value) in extra)
            {
                w.WritePropertyName(name);
                value.WriteTo(w);
            }
        }, callContext, resultType, timeoutSecs, cancellationToken);
    }

    // ---- version / support probe (same as the OdxClient methods, for this session's URL) ----

    /// <summary><c>POST /v2/odoo/version</c> for this session's Odoo URL.</summary>
    public Task<OdxVersionInfoV2> GetVersionAsync(uint timeoutSecs = 0, CancellationToken cancellationToken = default)
        => _client.GetVersionV2Async(Url, timeoutSecs, cancellationToken);

    /// <summary>Whether this session's Odoo is reachable over v2 (Odoo 19+). Cached per URL.</summary>
    public Task<bool> IsSupportedAsync(CancellationToken cancellationToken = default)
        => _client.SupportsV2Async(Url, cancellationToken);

    // ---- request assembly ----

    private async Task<T> SendNonNull<T>(string model, string method, Action<Utf8JsonWriter> writeArgs, OdxJson callContext,
        JsonTypeInfo<T> resultType, uint timeoutSecs, CancellationToken ct)
        where T : class
        => await Send(model, method, writeArgs, callContext, resultType, timeoutSecs, ct).ConfigureAwait(false)
           ?? throw new OdxException(Interop.OdxStatus.Ok, $"{method} returned null");

    private Task<T?> Send<T>(string model, string method, Action<Utf8JsonWriter> writeArgs, OdxJson callContext,
        JsonTypeInfo<T> resultType, uint timeoutSecs, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(resultType);
        if (!callContext.IsEmpty && callContext.Kind != JsonValueKind.Object)
            throw new ArgumentException("context must be a JSON object.", "context");

        // Same rule as v1's structured calls: build off a captured UI context, inline otherwise.
        return SynchronizationContext.Current is null
            ? Submit(model, method, writeArgs, callContext, resultType, timeoutSecs, ct)
            : Task.Run(() => Submit(model, method, writeArgs, callContext, resultType, timeoutSecs, ct), ct);
    }

    private Task<T?> Submit<T>(string model, string method, Action<Utf8JsonWriter> writeArgs, OdxJson callContext,
        JsonTypeInfo<T> resultType, uint timeoutSecs, CancellationToken ct)
    {
        byte[] body = Build(model, method, writeArgs, callContext);
        return _client.ExecuteV2Async(body.AsMemory(), resultType, timeoutSecs, ct);
    }

    internal byte[] Build(string model, string method, Action<Utf8JsonWriter> writeArgs, OdxJson callContext, string? id = null)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id"u8, id ?? OdxRequestBuilder.NextId());
            w.WriteString("model_id"u8, model);
            w.WriteString("method"u8, method);

            w.WritePropertyName("kwargs"u8);
            w.WriteStartObject();
            writeArgs(w);
            WriteMergedContext(w, callContext);
            w.WriteEndObject();

            w.WritePropertyName("odoo_instance"u8);
            w.WriteRawValue(_instanceJson, skipInputValidation: true);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Shallow merge, matching the JS SDK's <c>{...default, ...call}</c>: default keys first
    /// (overridden in place by the call's value), then the call's remaining keys. Omitted
    /// entirely when both are empty.
    /// </summary>
    private void WriteMergedContext(Utf8JsonWriter w, OdxJson callContext)
    {
        List<(string Name, OdxJson Value)> call = callContext.GetProperties("context");
        if (_defaultContext.Length == 0 && call.Count == 0)
            return;

        w.WritePropertyName("context"u8);
        w.WriteStartObject();
        foreach (var (name, value) in _defaultContext)
        {
            int i = call.FindIndex(p => p.Name == name);
            w.WritePropertyName(name);
            if (i >= 0)
            {
                call[i].Value.WriteTo(w);
                call.RemoveAt(i);
            }
            else
            {
                w.WriteRawValue(value, skipInputValidation: true);
            }
        }
        foreach (var (name, value) in call)
        {
            w.WritePropertyName(name);
            value.WriteTo(w);
        }
        w.WriteEndObject();
    }

    // ---- kwargs writers: a null/default argument is omitted, never sent as null ----

    private static void WriteIds(Utf8JsonWriter w, IReadOnlyList<long> ids)
    {
        w.WritePropertyName("ids"u8);
        w.WriteStartArray();
        for (int i = 0; i < ids.Count; i++)
            w.WriteNumberValue(ids[i]);
        w.WriteEndArray();
    }

    private static void WriteOpt(Utf8JsonWriter w, ReadOnlySpan<byte> name, int? value)
    {
        if (value is { } v)
            w.WriteNumber(name, v);
    }

    private static void WriteOpt(Utf8JsonWriter w, ReadOnlySpan<byte> name, string? value)
    {
        if (value is not null)
            w.WriteString(name, value);
    }

    private static void WriteOpt(Utf8JsonWriter w, ReadOnlySpan<byte> name, IReadOnlyList<string>? values)
    {
        if (values is null)
            return;
        w.WritePropertyName(name);
        w.WriteStartArray();
        for (int i = 0; i < values.Count; i++)
            w.WriteStringValue(values[i]);
        w.WriteEndArray();
    }

    private static void WriteOpt(Utf8JsonWriter w, ReadOnlySpan<byte> name, OdxJson value)
    {
        if (value.IsEmpty)
            return;
        w.WritePropertyName(name);
        value.WriteTo(w);
    }

    private static void WriteOrEmptyArray(Utf8JsonWriter w, OdxJson value)
    {
        if (value.IsEmpty)
            w.WriteRawValue("[]"u8, skipInputValidation: true);
        else
            value.WriteTo(w);
    }

    private static void RequireArrayOrEmpty(OdxJson value, string paramName)
    {
        if (!value.IsEmpty && value.Kind != JsonValueKind.Array)
            throw new ArgumentException($"{paramName} must be a JSON array (an Odoo domain).", paramName);
    }
}

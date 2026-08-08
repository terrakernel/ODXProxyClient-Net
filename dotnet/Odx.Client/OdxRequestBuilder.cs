using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TerraKernel.OdxClient;

/// <summary>
/// Builds odxproxy request bodies with <see cref="Utf8JsonWriter"/> — reflection-free
/// and AOT-safe. <c>params</c>/<c>keyword</c> are spliced in as raw JSON via
/// <see cref="Utf8JsonWriter.WriteRawValue(System.ReadOnlySpan{byte},bool)"/>, so the
/// caller's already-serialized fragments are never re-parsed. This stays at the wire
/// protocol level — it names no Odoo model or field (spec constraint #3). The
/// structured <c>OdxClient</c> overloads use this and additionally run it off the UI
/// thread when needed; call these directly only if you want the raw bytes.
/// </summary>
public static class OdxRequestBuilder
{
    private static long _idSeq;

    /// <summary>
    /// Build a POST <c>/api/odoo/execute</c> body from an <see cref="OdxAction"/> — the
    /// preferred overload: the action is validated at compile time and its wire string is
    /// spliced in as a UTF-8 constant (no transcode, no allocation).
    /// <paramref name="paramsJson"/> must be a JSON array and <paramref name="keywordJson"/>
    /// a JSON object; empty spans become <c>[]</c>/<c>{}</c>. <paramref name="fnName"/> is
    /// required for <see cref="OdxAction.CallMethod"/> (the proxy returns <c>-32002</c>
    /// without it) and ignored otherwise.
    /// </summary>
    public static byte[] BuildExecute(
        OdxAction action,
        string modelId,
        OdooInstance instance,
        ReadOnlySpan<byte> paramsJson = default,
        ReadOnlySpan<byte> keywordJson = default,
        string? fnName = null,
        string? id = null)
    {
        // Fail fast, locally, instead of a -32002 proxy round-trip — a concrete win the
        // typed action buys us (the string overload can't know it's call_method).
        if (action == OdxAction.CallMethod && string.IsNullOrEmpty(fnName))
            throw new ArgumentException(
                "OdxAction.CallMethod requires a non-empty fnName.", nameof(fnName));

        return BuildExecuteCore(ActionUtf8(action), modelId, instance, paramsJson, keywordJson, fnName, id);
    }

    /// <summary>
    /// Build a POST <c>/api/odoo/execute</c> body from a raw action string — the escape
    /// hatch for actions this client's <see cref="OdxAction"/> enum does not (yet) cover.
    /// Prefer the <see cref="OdxAction"/> overload. <paramref name="paramsJson"/> must be a
    /// JSON array and <paramref name="keywordJson"/> a JSON object; empty spans become
    /// <c>[]</c>/<c>{}</c>. <paramref name="fnName"/> is written only when non-empty
    /// (required by the proxy for <c>action == "call_method"</c>).
    /// </summary>
    public static byte[] BuildExecute(
        string action,
        string modelId,
        OdooInstance instance,
        ReadOnlySpan<byte> paramsJson = default,
        ReadOnlySpan<byte> keywordJson = default,
        string? fnName = null,
        string? id = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(action);
        // Escape-hatch path: transcode the arbitrary action once for the shared core.
        return BuildExecuteCore(Encoding.UTF8.GetBytes(action), modelId, instance, paramsJson, keywordJson, fnName, id);
    }

    private static byte[] BuildExecuteCore(
        ReadOnlySpan<byte> actionUtf8,
        string modelId,
        OdooInstance instance,
        ReadOnlySpan<byte> paramsJson,
        ReadOnlySpan<byte> keywordJson,
        string? fnName,
        string? id)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(instance);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id"u8, id ?? NextId());
            w.WriteString("action"u8, actionUtf8);
            w.WriteString("model_id"u8, modelId);

            w.WritePropertyName("keyword"u8);
            WriteRawOrDefault(w, keywordJson, "{}"u8);

            w.WritePropertyName("params"u8);
            WriteRawOrDefault(w, paramsJson, "[]"u8);

            if (!string.IsNullOrEmpty(fnName))
                w.WriteString("fn_name"u8, fnName);

            w.WritePropertyName("odoo_instance"u8);
            WriteInstance(w, instance);

            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    // Enum -> exact wire string, as a UTF-8 literal spliced straight into the buffer.
    // Deliberately a switch (never Enum.ToString(): that reflects, allocates, and is
    // AOT-hostile). The `_ => throw` guards against an enum value added without a mapping.
    private static ReadOnlySpan<byte> ActionUtf8(OdxAction action) => action switch
    {
        OdxAction.SearchCount => "search_count"u8,
        OdxAction.Search      => "search"u8,
        OdxAction.Read        => "read"u8,
        OdxAction.FieldsGet   => "fields_get"u8,
        OdxAction.SearchRead  => "search_read"u8,
        OdxAction.Create      => "create"u8,
        OdxAction.Write       => "write"u8,
        OdxAction.Unlink      => "unlink"u8,
        OdxAction.CallMethod  => "call_method"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown OdxAction."),
    };

    /// <summary>Build a POST <c>/api/odoo/version</c> body <c>{id, url}</c>.</summary>
    public static byte[] BuildVersion(string odooUrl, string? id = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(odooUrl);

        var buffer = new ArrayBufferWriter<byte>(96);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id"u8, id ?? NextId());
            w.WriteString("url"u8, odooUrl);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteRawOrDefault(Utf8JsonWriter w, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> fallback)
    {
        if (raw.IsEmpty)
            w.WriteRawValue(fallback, skipInputValidation: true);   // our own constants
        else
            w.WriteRawValue(raw, skipInputValidation: false);       // validate caller input
    }

    private static void WriteInstance(Utf8JsonWriter w, OdooInstance i)
    {
        w.WriteStartObject();
        w.WriteString("url"u8, i.Url);
        w.WriteNumber("user_id"u8, i.UserId);
        w.WriteString("db"u8, i.Db);
        w.WriteString("api_key"u8, i.ApiKey);
        w.WriteEndObject();
    }

    // The proxy echoes id but we don't correlate on it (the callback cookie does that);
    // a cheap monotonic value avoids a per-call Guid allocation.
    private static string NextId() =>
        Interlocked.Increment(ref _idSeq).ToString(CultureInfo.InvariantCulture);
}

using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Odx.Client;

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
    /// Build a POST <c>/api/odoo/execute</c> body. <paramref name="paramsJson"/> must be
    /// a JSON array and <paramref name="keywordJson"/> a JSON object; empty spans become
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
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(instance);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id"u8, id ?? NextId());
            w.WriteString("action"u8, action);
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

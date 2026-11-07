using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraKernel.OdxClient;

/// <summary>
/// The Odoo facts ODXBrain filters on: <c>odoo_version</c> (Odoo's serie, e.g. <c>"19.0"</c>,
/// <c>"saas~16.3"</c>) and <c>edition</c> (<c>"community"</c> / <c>"enterprise"</c>). A
/// <see langword="null"/> fact is <b>omitted</b> from the request, never sent as <c>null</c>:
/// a missing fact filters nothing, while a wrong one silently removes correct answers, so
/// never guess. Get real facts from <see cref="OdxBrain.GetFactsAsync"/> or
/// <see cref="OdxSessionV2.GetBrainFactsAsync"/>.
/// </summary>
public readonly record struct OdxBrainFacts(string? OdooVersion = null, string? Edition = null)
{
    /// <summary>The <c>edition</c> value for Odoo Community.</summary>
    public const string Community = "community";

    /// <summary>The <c>edition</c> value for Odoo Enterprise.</summary>
    public const string Enterprise = "enterprise";
}

/// <summary>
/// <c>POST /brain/v1/resolve</c> result: ranked Odoo model names for a question. Every
/// candidate is returned, in ODXBrain's order; nothing is filtered on <c>score</c> (ordering
/// is the contract, the scale is not). Follow up with <c>fields_get</c> on the chosen model.
/// </summary>
/// <remarks>
/// Unknown fields are kept in <see cref="AdditionalFields"/> on every object, and the whole
/// <c>result</c> is in <see cref="Raw"/>. The conditional fields (<see cref="Excluded"/>,
/// <see cref="Filtered"/>, <see cref="VersionUnaudited"/>,
/// <see cref="OdxBrainModelCandidate.Provisional"/>) are information, not errors.
/// </remarks>
public sealed class OdxBrainModelResult
{
    /// <summary>Every candidate, best first. Never thresholded.</summary>
    [JsonPropertyName("candidates")]
    [JsonInclude]
    public OdxBrainModelCandidate[] Candidates { get; internal set; } = [];

    /// <summary>Models that would have ranked but don't exist on that Odoo version or edition. Empty when absent.</summary>
    [JsonPropertyName("excluded")]
    [JsonInclude]
    public OdxBrainExcludedModel[] Excluded { get; internal set; } = [];

    /// <summary><see langword="false"/> when neither <c>odoo_version</c> nor <c>edition</c> was sent; <see langword="null"/> when absent.</summary>
    [JsonPropertyName("filtered")]
    [JsonInclude]
    public bool? Filtered { get; internal set; }

    /// <summary>
    /// Set when the <c>odoo_version</c> sent is newer than ODXBrain's audited range. The value is
    /// the <b>newest audited version</b> (e.g. <c>"19.0"</c> for a 20.0 instance), not the one sent.
    /// </summary>
    [JsonPropertyName("version_unaudited")]
    [JsonInclude]
    public string? VersionUnaudited { get; internal set; }

    /// <summary>The embedding model id (e.g. <c>"bge-m3"</c>). Detects a model swap.</summary>
    [JsonPropertyName("model")]
    [JsonInclude]
    public string? Model { get; internal set; }

    /// <summary>The embedding dimension.</summary>
    [JsonPropertyName("dim")]
    [JsonInclude]
    public int? Dim { get; internal set; }

    /// <summary>The model corpus version (e.g. <c>"2026.09.4"</c>). Detects a corpus swap.</summary>
    [JsonPropertyName("corpus_version")]
    [JsonInclude]
    public string? CorpusVersion { get; internal set; }

    /// <summary>Token usage, as ODXBrain reports it (e.g. <c>{"prompt_tokens": 10}</c>).</summary>
    [JsonPropertyName("usage")]
    [JsonInclude]
    public JsonElement? Usage { get; internal set; }

    /// <summary>Fields this SDK does not know yet, unchanged.</summary>
    [JsonExtensionData]
    [JsonInclude]
    public Dictionary<string, JsonElement>? AdditionalFields { get; internal set; }

    /// <summary>The whole <c>result</c> object exactly as received.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; internal set; }
}

/// <summary>One ranked Odoo model.</summary>
public sealed class OdxBrainModelCandidate
{
    /// <summary>The Odoo model name, e.g. <c>account.move</c>.</summary>
    [JsonPropertyName("model")]
    [JsonInclude]
    public string Model { get; internal set; } = "";

    /// <summary>The similarity score. Compare only within one result; don't threshold it.</summary>
    [JsonPropertyName("score")]
    [JsonInclude]
    public double Score { get; internal set; }

    /// <summary>The model's availability on that version/edition rests on inference, not audited source.</summary>
    [JsonPropertyName("provisional")]
    [JsonInclude]
    public bool Provisional { get; internal set; }

    /// <summary>Fields this SDK does not know yet, unchanged.</summary>
    [JsonExtensionData]
    [JsonInclude]
    public Dictionary<string, JsonElement>? AdditionalFields { get; internal set; }
}

/// <summary>A model left out because it does not exist on the Odoo version or edition sent.</summary>
public sealed class OdxBrainExcludedModel
{
    [JsonPropertyName("model")]
    [JsonInclude]
    public string Model { get; internal set; } = "";

    [JsonPropertyName("reason")]
    [JsonInclude]
    public string? Reason { get; internal set; }

    /// <summary>The model that replaced it on that version, when there is one.</summary>
    [JsonPropertyName("successor")]
    [JsonInclude]
    public string? Successor { get; internal set; }

    /// <summary>Fields this SDK does not know yet, unchanged.</summary>
    [JsonExtensionData]
    [JsonInclude]
    public Dictionary<string, JsonElement>? AdditionalFields { get; internal set; }
}

/// <summary>
/// <c>POST /brain/v1/sops/resolve</c> result: ranked SOP ids for a task. If
/// <see cref="IsNoMatch"/>, <b>stop and ask the user</b>; don't run the nearest SOP. Check the
/// chosen candidate's <see cref="OdxBrainSopCandidate.Title"/> against the task before acting:
/// embeddings match the topic, not the operation.
/// </summary>
/// <remarks>Unknown fields are kept in <see cref="AdditionalFields"/>; the whole <c>result</c> is in <see cref="Raw"/>.</remarks>
public sealed class OdxBrainSopResult
{
    /// <summary>Every candidate, best first. Never thresholded.</summary>
    [JsonPropertyName("candidates")]
    [JsonInclude]
    public OdxBrainSopCandidate[] Candidates { get; internal set; } = [];

    /// <summary><c>"none"</c> when no SOP is a confident match; <see langword="null"/> when absent.</summary>
    [JsonPropertyName("match")]
    [JsonInclude]
    public string? Match { get; internal set; }

    /// <summary>True when <see cref="Match"/> is <c>"none"</c>: no confident match, so ask instead of acting.</summary>
    [JsonIgnore]
    public bool IsNoMatch => Match == "none";

    /// <summary>SOPs filtered out on this instance (e.g. a required module isn't installed). Empty when absent.</summary>
    [JsonPropertyName("excluded")]
    [JsonInclude]
    public OdxBrainExcludedSop[] Excluded { get; internal set; } = [];

    /// <summary>Facts the caller didn't send (e.g. <c>company</c>, <c>installed_modules</c>). Empty when absent.</summary>
    [JsonPropertyName("facts_missing")]
    [JsonInclude]
    public string[] FactsMissing { get; internal set; } = [];

    /// <summary>The embedding model id.</summary>
    [JsonPropertyName("model")]
    [JsonInclude]
    public string? Model { get; internal set; }

    /// <summary>The embedding dimension.</summary>
    [JsonPropertyName("dim")]
    [JsonInclude]
    public int? Dim { get; internal set; }

    /// <summary>The SOP set that answered (<c>generation</c>, <c>root</c>): ties an action to the approved set.</summary>
    [JsonPropertyName("sops")]
    [JsonInclude]
    public OdxBrainSopSet? Sops { get; internal set; }

    /// <summary>Token usage, as ODXBrain reports it.</summary>
    [JsonPropertyName("usage")]
    [JsonInclude]
    public JsonElement? Usage { get; internal set; }

    /// <summary>Fields this SDK does not know yet, unchanged.</summary>
    [JsonExtensionData]
    [JsonInclude]
    public Dictionary<string, JsonElement>? AdditionalFields { get; internal set; }

    /// <summary>The whole <c>result</c> object exactly as received.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; internal set; }
}

/// <summary>One ranked SOP. Fetch its body from the doc manager (<c>GET /v1/sops/{id}/{version}</c>), not the proxy.</summary>
public sealed class OdxBrainSopCandidate
{
    [JsonPropertyName("id")]
    [JsonInclude]
    public string Id { get; internal set; } = "";

    /// <summary>Opaque version (in practice a content hash). Compare it; never parse it.</summary>
    [JsonPropertyName("version")]
    [JsonInclude]
    public string Version { get; internal set; } = "";

    [JsonPropertyName("title")]
    [JsonInclude]
    public string? Title { get; internal set; }

    /// <summary>The similarity score. Compare only within one result; don't threshold it.</summary>
    [JsonPropertyName("score")]
    [JsonInclude]
    public double Score { get; internal set; }

    /// <summary>Fields this SDK does not know yet, unchanged.</summary>
    [JsonExtensionData]
    [JsonInclude]
    public Dictionary<string, JsonElement>? AdditionalFields { get; internal set; }
}

/// <summary>An SOP filtered out on this instance.</summary>
public sealed class OdxBrainExcludedSop
{
    [JsonPropertyName("id")]
    [JsonInclude]
    public string Id { get; internal set; } = "";

    [JsonPropertyName("reason")]
    [JsonInclude]
    public string? Reason { get; internal set; }

    /// <summary>Fields this SDK does not know yet, unchanged.</summary>
    [JsonExtensionData]
    [JsonInclude]
    public Dictionary<string, JsonElement>? AdditionalFields { get; internal set; }
}

/// <summary>The SOP set an answer came from.</summary>
public sealed class OdxBrainSopSet
{
    [JsonPropertyName("generation")]
    [JsonInclude]
    public long Generation { get; internal set; }

    /// <summary>The set's root hash (64 hex chars).</summary>
    [JsonPropertyName("root")]
    [JsonInclude]
    public string? Root { get; internal set; }

    /// <summary>Fields this SDK does not know yet, unchanged.</summary>
    [JsonExtensionData]
    [JsonInclude]
    public Dictionary<string, JsonElement>? AdditionalFields { get; internal set; }
}

/// <summary>Source-generated (reflection-free, AOT-safe) metadata for the ODXBrain result types.</summary>
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(OdxBrainModelResult))]
[JsonSerializable(typeof(OdxBrainSopResult))]
internal partial class OdxBrainJsonContext : JsonSerializerContext;

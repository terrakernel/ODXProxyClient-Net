using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraKernel.OdxClient;

/// <summary>
/// The result of <c>POST /v2/odoo/version</c> — Odoo's <c>GET /json/version</c>, e.g.
/// <c>{"version_info": [20, 0, 0, "final", 0, "e"], "version": "20.0+e"}</c>. This is a
/// different shape from the v1 version result.
/// </summary>
public sealed class OdxVersionInfoV2
{
    /// <summary>Odoo's version tuple, e.g. <c>[20, 0, 0, "final", 0, "e"]</c> (mixed numbers and strings).</summary>
    [JsonPropertyName("version_info")]
    public JsonElement[] VersionInfo { get; init; } = [];

    /// <summary>The version string, e.g. <c>"20.0+e"</c>.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    /// <summary>The major version (<c>version_info[0]</c>), or 0 if absent.</summary>
    [JsonIgnore]
    public int Major =>
        VersionInfo.Length > 0 && VersionInfo[0].ValueKind == JsonValueKind.Number && VersionInfo[0].TryGetInt32(out int m)
            ? m
            : 0;
}

/// <summary>
/// Source-generated metadata for the result types the library itself deserializes
/// (reflection-free, AOT-safe). No naming policy: wire names come from
/// <see cref="JsonPropertyNameAttribute"/> only.
/// </summary>
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(OdxVersionInfoV2))]
internal partial class OdxInternalJsonContext : JsonSerializerContext;

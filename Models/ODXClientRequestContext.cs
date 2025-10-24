using System.Text.Json.Serialization;

namespace ODXProxy.Client.Models;

/// <summary>
/// Represents the context for an Odoo request, including user permissions and settings.
/// </summary>
public record ODXClientRequestContext
{
    [JsonPropertyName("allowed_company_ids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? AllowedCompanyIds { get; init; }

    [JsonPropertyName("default_company_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DefaultCompanyId { get; init; }

    [JsonPropertyName("tz")]
    public string Timezone { get; init; } = "UTC";
}
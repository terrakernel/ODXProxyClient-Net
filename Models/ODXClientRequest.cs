using System.Text.Json.Serialization;

namespace ODXProxy.Client.Models;

/// <summary>
/// Represents the complete request payload sent to the ODX Gateway.
/// </summary>
public record ODXClientRequest
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("model_id")]
    public required string ModelId { get; init; }

    [JsonPropertyName("keyword")]
    public required ODXClientKeywordRequest Keyword { get; init; }
    
    [JsonPropertyName("fn_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FunctionName { get; init; }

    [JsonPropertyName("params")]
    public required object[] Params { get; init; }
    
    [JsonPropertyName("odoo_instance")]
    public required ODXInstanceInfo OdooInstance { get; init; }
}
using System.Text.Json.Serialization;

namespace ODXProxy.Client.Models;

/// <summary>
/// Represents keyword arguments for an Odoo request.
/// </summary>
public record ODXClientKeywordRequest
{
    [JsonPropertyName("fields")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Fields { get; init; }

    [JsonPropertyName("order")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Order { get; init; }

    [JsonPropertyName("limit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Limit { get; init; }

    [JsonPropertyName("offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Offset { get; init; }

    [JsonPropertyName("context")]
    public ODXClientRequestContext Context { get; init; } = new();
}
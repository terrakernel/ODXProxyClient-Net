using System.Text.Json.Serialization;

namespace ODXProxy.Client.Models;

/// <summary>
/// Represents the connection information for a specific Odoo instance.
/// </summary>
public record ODXInstanceInfo(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("user_id")] int UserId,
    [property: JsonPropertyName("db")] string Db,
    [property: JsonPropertyName("api_key")] string ApiKey
);

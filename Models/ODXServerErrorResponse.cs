using System.Text.Json;
using System.Text.Json.Serialization;

namespace ODXProxy.Client.Models;

/// <summary>
/// Represents a detailed server error response from the ODX Gateway.
/// </summary>
public record ODXServerErrorResponse(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] JsonElement? Data
);
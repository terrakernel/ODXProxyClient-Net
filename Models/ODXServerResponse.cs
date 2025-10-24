using System.Text.Json.Serialization;

namespace ODXProxy.Client.Models;

/// <summary>
/// Represents the generic JSON-RPC response from the ODX Gateway.
/// </summary>
public record ODXServerResponse<T>(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("result")] T? Result,
    [property: JsonPropertyName("error")] ODXServerErrorResponse? Error
);
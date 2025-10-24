using System.Text.Json.Serialization;

namespace ODXProxy.Client.Models;

/// <summary>
/// Configuration options for initializing the ODX Proxy Client.
/// </summary>
public record ODXProxyClientInfo(
	[property: JsonPropertyName("instance")] ODXInstanceInfo Instance,
	[property: JsonPropertyName("odx_api_key")] string OdxApiKey,
	[property: JsonPropertyName("gateway_url")] string? GatewayUrl = null
);

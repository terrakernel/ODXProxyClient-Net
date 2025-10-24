using System.Net.Http.Json;
using System.Text.Json;
using ODXProxy.Client.Exceptions;
using ODXProxy.Client.Models;

namespace ODXProxy.Client;

/// <summary>
/// The core HTTP client for communicating with the ODX Proxy Gateway.
/// This class follows the singleton pattern.
/// </summary>
public sealed class OdxProxyClient
{
    private static readonly Lazy<OdxProxyClient> LazyInstance = new(() => new OdxProxyClient());
    private static ODXProxyClientInfo? _options;

    private readonly HttpClient _httpClient;

    public static OdxProxyClient Instance => LazyInstance.Value;
    
    public ODXInstanceInfo OdooInstance => _options?.Instance 
        ?? throw new InvalidOperationException("Client has not been initialized. Call OdxProxyClient.Init() first.");

    private OdxProxyClient()
    {
        if (_options == null)
        {
            throw new InvalidOperationException("Client has not been initialized. Call OdxProxyClient.Init() first.");
        }

        string gatewayUrl = _options.GatewayUrl ?? "https://gateway.odxproxy.io";
        if (gatewayUrl.EndsWith('/'))
        {
            gatewayUrl = gatewayUrl[..^1];
        }

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(gatewayUrl),
            Timeout = TimeSpan.FromMilliseconds(45000)
        };
        
        _httpClient.DefaultRequestHeaders.Accept.Add(new("application/json"));
        _httpClient.DefaultRequestHeaders.Add("x-api-key", _options.OdxApiKey);
    }
    
    /// <summary>
    /// Initializes the singleton client instance with the specified options.
    /// This must be called once before accessing the client instance.
    /// </summary>
    /// <param name="options">The configuration options for the client.</param>
    public static void Init(ODXProxyClientInfo options)
    {
        if (_options != null)
        {
            throw new InvalidOperationException("OdxProxyClient has already been initialized.");
        }
        _options = options;
    }

    /// <summary>
    /// Sends a POST request to the ODX Gateway.
    /// </summary>
    /// <typeparam name="T">The expected type of the 'result' property in the response.</typeparam>
    /// <param name="request">The request payload.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The deserialized server response.</returns>
    /// <exception cref="OdxProxyException">Thrown when the API returns an error or the request fails.</exception>
    public async Task<ODXServerResponse<T>> PostRequestAsync<T>(ODXClientRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/api/odoo/execute", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var serverError = JsonSerializer.Deserialize<ODXServerErrorResponse>(errorContent);
                throw new OdxProxyException(
                    serverError?.Message ?? response.ReasonPhrase ?? "An unknown error occurred.",
                    (int)response.StatusCode,
                    serverError
                );
            }

            var result = await response.Content.ReadFromJsonAsync<ODXServerResponse<T>>(cancellationToken: cancellationToken);
            return result ?? throw new OdxProxyException("Failed to deserialize server response.", (int)response.StatusCode);
        }
        catch (TaskCanceledException ex) when (ex.CancellationToken != cancellationToken) // Catches timeout
        {
            throw new OdxProxyException($"Request Timeout: exceeded client limit of {_httpClient.Timeout.TotalMilliseconds}ms", 408, innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new OdxProxyException("A network error occurred.", 500, innerException: ex);
        }
    }
}
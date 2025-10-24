using ODXProxy.Client.Models;

namespace ODXProxy.Client;

/// <summary>
/// Provides high-level methods to interact with an Odoo instance via the ODX Proxy Gateway.
/// </summary>
public static class OdxApiClient
{
    private static OdxProxyClient Client => OdxProxyClient.Instance;

    /// <summary>
    /// Initializes the underlying ODX Proxy Client. Must be called once before any other methods.
    /// </summary>
    public static void Init(ODXProxyClientInfo options) => OdxProxyClient.Init(options);

    private static ODXClientKeywordRequest GetCleanKeyword(ODXClientKeywordRequest keyword) =>
        keyword with { Order = null, Limit = null, Offset = null, Fields = null };

    /// <summary>
    /// Performs a search, returning only record IDs.
    /// </summary>
    public static Task<ODXServerResponse<int[]>> SearchAsync(string model, object[] domain, ODXClientKeywordRequest keyword, string? id = null, CancellationToken ct = default)
    {
        var request = new ODXClientRequest
        {
            Id = id ?? Ulid.NewUlid().ToString(),
            Action = "search",
            ModelId = model,
            Keyword = GetCleanKeyword(keyword),
            Params = domain,
            OdooInstance = Client.OdooInstance
        };
        return Client.PostRequestAsync<int[]>(request, ct);
    }
    
    /// <summary>
    /// Performs a search and reads the data of the found records.
    /// </summary>
    public static Task<ODXServerResponse<T[]>> SearchReadAsync<T>(string model, object[] domain, ODXClientKeywordRequest keyword, string? id = null, CancellationToken ct = default)
    {
        var request = new ODXClientRequest
        {
            Id = id ?? Ulid.NewUlid().ToString(),
            Action = "search_read",
            ModelId = model,
            Keyword = keyword,
            Params = domain,
            OdooInstance = Client.OdooInstance
        };
        return Client.PostRequestAsync<T[]>(request, ct);
    }

    /// <summary>
    /// Counts the number of records matching the search domain.
    /// </summary>
    public static Task<ODXServerResponse<int>> SearchCountAsync(string model, object[] domain, ODXClientKeywordRequest keyword, string? id = null, CancellationToken ct = default)
    {
        var request = new ODXClientRequest
        {
            Id = id ?? Ulid.NewUlid().ToString(),
            Action = "search_count",
            ModelId = model,
            Keyword = GetCleanKeyword(keyword),
            Params = domain,
            OdooInstance = Client.OdooInstance
        };
        return Client.PostRequestAsync<int>(request, ct);
    }

    /// <summary>
    /// Reads the data for a specific set of record IDs.
    /// </summary>
    public static Task<ODXServerResponse<T[]>> ReadAsync<T>(string model, int[] ids, ODXClientKeywordRequest keyword, string? id = null, CancellationToken ct = default)
    {
        var request = new ODXClientRequest
        {
            Id = id ?? Ulid.NewUlid().ToString(),
            Action = "read",
            ModelId = model,
            Keyword = GetCleanKeyword(keyword),
            Params = new object[] { ids },
            OdooInstance = Client.OdooInstance
        };
        return Client.PostRequestAsync<T[]>(request, ct);
    }
    
    /// <summary>
    /// Retrieves metadata for a model's fields.
    /// </summary>
    public static Task<ODXServerResponse<T>> FieldsGetAsync<T>(string model, ODXClientKeywordRequest keyword, string? id = null, CancellationToken ct = default)
    {
        var request = new ODXClientRequest
        {
            Id = id ?? Ulid.NewUlid().ToString(),
            Action = "fields_get",
            ModelId = model,
            Keyword = GetCleanKeyword(keyword),
            Params = Array.Empty<object>(),
            OdooInstance = Client.OdooInstance
        };
        return Client.PostRequestAsync<T>(request, ct);
    }

    /// <summary>
    /// Creates a new record.
    /// </summary>
    /// <returns>The ID of the newly created record.</returns>
    public static Task<ODXServerResponse<int>> CreateAsync(string model, object values, ODXClientKeywordRequest keyword, string? id = null, CancellationToken ct = default)
    {
        var request = new ODXClientRequest
        {
            Id = id ?? Ulid.NewUlid().ToString(),
            Action = "create",
            ModelId = model,
            Keyword = GetCleanKeyword(keyword),
            Params = new object[] { values },
            OdooInstance = Client.OdooInstance
        };
        return Client.PostRequestAsync<int>(request, ct);
    }

    /// <summary>
    /// Updates existing records.
    /// </summary>
    public static Task<ODXServerResponse<bool>> WriteAsync(string model, int[] ids, object values, ODXClientKeywordRequest keyword, string? id = null, CancellationToken ct = default)
    {
        var request = new ODXClientRequest
        {
            Id = id ?? Ulid.NewUlid().ToString(),
            Action = "write",
            ModelId = model,
            Keyword = GetCleanKeyword(keyword),
            Params = new object[] { ids, values },
            OdooInstance = Client.OdooInstance
        };
        return Client.PostRequestAsync<bool>(request, ct);
    }

    /// <summary>
    /// Deletes (unlinks) records by their IDs.
    /// </summary>
    public static Task<ODXServerResponse<bool>> RemoveAsync(string model, int[] ids, ODXClientKeywordRequest keyword, string? id = null, CancellationToken ct = default)
    {
        var request = new ODXClientRequest
        {
            Id = id ?? Ulid.NewUlid().ToString(),
            Action = "unlink",
            ModelId = model,
            Keyword = GetCleanKeyword(keyword),
            Params = new object[] { ids },
            OdooInstance = Client.OdooInstance
        };
        return Client.PostRequestAsync<bool>(request, ct);
    }

    /// <summary>
    /// Calls an arbitrary method on a model.
    /// </summary>
    public static Task<ODXServerResponse<T>> CallMethodAsync<T>(string model, string methodName, object[] parameters, ODXClientKeywordRequest keyword, string? id = null, CancellationToken ct = default)
    {
        var request = new ODXClientRequest
        {
            Id = id ?? Ulid.NewUlid().ToString(),
            Action = "call_method",
            ModelId = model,
            FunctionName = methodName,
            Keyword = GetCleanKeyword(keyword),
            Params = parameters,
            OdooInstance = Client.OdooInstance
        };
        return Client.PostRequestAsync<T>(request, ct);
    }
}
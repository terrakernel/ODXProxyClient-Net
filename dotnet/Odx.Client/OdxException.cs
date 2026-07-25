using Odx.Client.Interop;

namespace Odx.Client;

/// <summary>
/// Base exception for odxclient failures. Carries the coarse transport/proxy
/// <see cref="OdxStatus"/> plus, when the failure came with a JSON-RPC error body,
/// the proxy/Odoo <see cref="RpcCode"/> and raw <see cref="RpcData"/>. See the typed
/// subclasses and IMPLEMENTATION-PLAN.md §3.3.
/// </summary>
public class OdxException : Exception
{
    /// <summary>Coarse transport/proxy category.</summary>
    public OdxStatus Status { get; }

    /// <summary>The JSON-RPC <c>error.code</c> from the response body, if present.</summary>
    public long? RpcCode { get; }

    /// <summary>The JSON-RPC <c>error.data</c> as raw JSON, if present.</summary>
    public string? RpcData { get; }

    public OdxException(OdxStatus status, string? message = null, long? rpcCode = null, string? rpcData = null)
        : base(BuildMessage(status, message, rpcCode))
    {
        Status = status;
        RpcCode = rpcCode;
        RpcData = rpcData;
    }

    private static string BuildMessage(OdxStatus status, string? message, long? rpcCode)
    {
        if (!string.IsNullOrEmpty(message))
            return rpcCode is { } c ? $"{message} (status={status}, code={c})" : $"{message} (status={status})";
        return rpcCode is { } cc ? $"odxclient error: {status} (code={cc})" : $"odxclient error: {status}";
    }
}

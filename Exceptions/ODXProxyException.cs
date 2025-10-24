using ODXProxy.Client.Models;

namespace ODXProxy.Client.Exceptions;

/// <summary>
/// Represents errors that occur during an ODX Proxy API request.
/// </summary>
public class OdxProxyException : Exception
{
    /// <summary>
    /// The HTTP status code of the error response.
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// The detailed error data returned from the server, if any.
    /// </summary>
    public ODXServerErrorResponse? ServerError { get; }

    public OdxProxyException(string message, int statusCode, ODXServerErrorResponse? serverError = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ServerError = serverError;
    }
}
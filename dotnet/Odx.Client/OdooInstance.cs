namespace Odx.Client;

/// <summary>
/// The Odoo connection details odxproxy needs on every call (it is stateless w.r.t.
/// Odoo auth — credentials are re-sent per request). Create one per Odoo instance and
/// reuse it across calls. This is a wire-protocol value, not a domain model.
/// </summary>
public sealed class OdooInstance
{
    public required string Url { get; init; }
    public required long UserId { get; init; }
    public required string Db { get; init; }
    public required string ApiKey { get; init; }
}

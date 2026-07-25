namespace Odx.Client;

/// <summary>
/// The odxproxy <c>execute</c> actions — the closed set the proxy accepts on
/// <c>POST /api/odoo/execute</c>. Passing this enum instead of a raw action string turns a
/// typo from a runtime <c>-32001 invalid action</c> round-trip into a compile error.
/// </summary>
/// <remarks>
/// This is a wire-protocol primitive (the same category as the client's error-code mapping),
/// <b>not</b> a domain model: it names no Odoo model or field, so it does not cross the
/// "no typed model/ORM layer" line (spec constraint #3). Each value maps to the exact
/// snake_case string the proxy expects (see <c>OdxRequestBuilder</c>). The raw
/// <c>string</c> overloads remain as an escape hatch if the proxy ever adds an action.
/// </remarks>
public enum OdxAction
{
    /// <summary><c>search_count</c> — count records matching a domain.</summary>
    SearchCount,

    /// <summary><c>search</c> — return matching record ids.</summary>
    Search,

    /// <summary><c>read</c> — read fields for given ids.</summary>
    Read,

    /// <summary><c>fields_get</c> — model field metadata.</summary>
    FieldsGet,

    /// <summary><c>search_read</c> — search + read in one call.</summary>
    SearchRead,

    /// <summary><c>create</c> — create records.</summary>
    Create,

    /// <summary><c>write</c> — update records.</summary>
    Write,

    /// <summary><c>unlink</c> — delete records.</summary>
    Unlink,

    /// <summary>
    /// <c>call_method</c> — invoke an arbitrary model method. Requires <c>fnName</c>
    /// (the proxy returns <c>-32002</c> without it).
    /// </summary>
    CallMethod,
}

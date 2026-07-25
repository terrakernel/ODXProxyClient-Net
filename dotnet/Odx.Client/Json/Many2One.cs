namespace Odx.Client.Json;

/// <summary>
/// An Odoo many2one field value. On the wire Odoo reads it as <c>[id, name]</c> or
/// <c>false</c> (unset); it is WRITTEN as the bare integer id (or <c>false</c> to
/// clear). This is a wire-protocol primitive, not a domain model — see
/// <see cref="Many2OneConverter"/> and the memory note <c>wire-helpers-placement</c>.
/// </summary>
public readonly struct Many2One
{
    /// <summary>False for an unset many2one (Odoo's <c>false</c>).</summary>
    public bool HasValue { get; }

    public long Id { get; }

    /// <summary>The display name from <c>[id, name]</c>, if the read side provided one.</summary>
    public string? Name { get; }

    public Many2One(long id, string? name = null)
    {
        HasValue = true;
        Id = id;
        Name = name;
    }

    private Many2One(bool hasValue)
    {
        HasValue = hasValue;
        Id = 0;
        Name = null;
    }

    /// <summary>An unset many2one (serializes to <c>false</c>).</summary>
    public static Many2One Unset => new(false);

    public override string ToString() =>
        HasValue ? (Name is null ? Id.ToString() : $"{Id} ({Name})") : "(unset)";
}

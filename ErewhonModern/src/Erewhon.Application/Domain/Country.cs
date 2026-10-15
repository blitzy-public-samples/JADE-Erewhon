using Erewhon.Application.Common;

namespace Erewhon.Application.Domain;

/// <summary>
/// A country owned by a company: its name and its regions.
/// </summary>
public class Country : Location
{
    /// <summary>The row id of the owning company.</summary>
    public long CompanyId { get; set; }

    /// <summary>The owning company; deleting the company deletes the country.</summary>
    public Company? Company { get; set; }

    /// <summary>The regions of the country; deleting the country deletes them.</summary>
    public ICollection<Region> Regions { get; set; } = new List<Region>();

    /// <summary>The class number of <c>Country</c>, <see cref="ObjectIdentity.Country"/>.</summary>
    public override int JadeClassNumber => ObjectIdentity.Country;

    /// <summary>
    /// Returns the developer description <c>&lt;oid&gt; Country: &lt;name&gt;</c>,
    /// e.g. <c>5780.3 Country: France</c> for the country with id 3.
    /// </summary>
    /// <returns>The debug string.</returns>
    public string GetDebugString() => ObjectIdentity.Of(this) + " Country: " + Name;
}

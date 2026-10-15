using Erewhon.Application.Common;
using Erewhon.Application.Messages;

namespace Erewhon.Application.Domain;

/// <summary>
/// A region of a country: its name, its country and the items listed in it.
/// </summary>
public class Region : Location
{
    /// <summary>The row id of the owning country.</summary>
    public long CountryId { get; set; }

    /// <summary>The owning country; deleting the country deletes the region.</summary>
    public Country? Country { get; set; }

    /// <summary>The items listed in the region; deleting the region clears their region.</summary>
    public ICollection<Item> Items { get; set; } = new List<Item>();

    /// <summary>The class number of <c>Region</c>, <see cref="ObjectIdentity.Region"/>.</summary>
    public override int JadeClassNumber => ObjectIdentity.Region;

    /// <summary>
    /// Returns the country name, a space and the region name,
    /// e.g. <c>New Zealand Christchurch</c>.
    /// </summary>
    /// <returns>The country and region names.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Country"/> is not loaded.</exception>
    public string GetCountryAndRegionNames() => RequireCountry().Name + " " + Name;

    /// <summary>
    /// Returns the developer description <c>&lt;oid&gt; Region: &lt;name&gt; of &lt;country name&gt;</c>,
    /// e.g. <c>5781.5 Region: Christchurch of New Zealand</c> for the region with id 5.
    /// </summary>
    /// <returns>The debug string.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Country"/> is not loaded.</exception>
    public string GetDebugString() =>
        ObjectIdentity.Of(this) + " Region: " + Name + " " + MessageCatalog.Get(MessageKeys.Of) + " " + RequireCountry().Name;

    private Country RequireCountry() =>
        Country ?? throw new InvalidOperationException("Region.Country is not loaded.");
}

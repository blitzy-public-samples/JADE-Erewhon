namespace Erewhon.StandIn.Model;

/// <summary>
/// JADE <c>Location</c>: the abstract base of <see cref="Country"/> and <see cref="Region"/>,
/// carrying the name both are keyed by.
/// </summary>
public abstract class Location : PersistentModel
{
    /// <summary>Creates a location with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    protected Location(long oid)
        : base(oid)
    {
    }

    /// <summary>
    /// JADE <c>name: String[81]</c>, set by <c>LocationTA</c> already trimmed; the length limit is
    /// not enforced. It is the key of <see cref="CountriesByNameDict"/> and
    /// <see cref="RegionsByNameDict"/>, so whoever changes it re-keys the location there.
    /// </summary>
    public string Name { get; internal set; } = "";
}

/// <summary>JADE <c>Country</c>: a location of the company that holds its regions by name.</summary>
/// <remarks>
/// Membership of the country in <see cref="Company.AllCountries"/> and of its regions in
/// <see cref="AllRegions"/> is maintained by <c>CountryTA</c> and <c>RegionTA</c> in
/// <c>Validation.cs</c>, the counterpart of the JADE automatic inverses; the country only exposes
/// the dictionary. Not thread-safe.
/// </remarks>
public sealed class Country : Location
{
    /// <summary>Creates a country with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    internal Country(long oid)
        : base(oid)
    {
    }

    /// <summary>JADE <c>allRegions</c>, keyed by name.</summary>
    public RegionsByNameDict AllRegions { get; } = new();

    /// <summary>
    /// JADE <c>myCompany</c>, set once when <c>CountryTA</c> creates the country (JADE
    /// <c>onCreate</c> assigns <c>app.myCompany</c>).
    /// </summary>
    public Company? MyCompany { get; internal set; }

    /// <summary>JADE <c>hasRegionWithThisName</c>: true when a region has exactly this name.</summary>
    public bool HasRegionWithThisName(string name) => AllRegions.GetAtKey(name) != null;

    /// <summary>
    /// JADE <c>hasAnotherRegionWithThisName</c>: true when a region other than
    /// <paramref name="region"/> has exactly this name.
    /// </summary>
    public bool HasAnotherRegionWithThisName(string name, Region? region)
    {
        Region? found = AllRegions.GetAtKey(name);
        return found != null && !ReferenceEquals(found, region);
    }
}

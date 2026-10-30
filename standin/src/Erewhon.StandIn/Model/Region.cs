namespace Erewhon.StandIn.Model;

/// <summary>
/// JADE <c>Region subclassOf Location</c>: a region of a <see cref="Country"/>, which seed items
/// are located in (<c>Company.getRegion(country, region)</c>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MyCountry"/> is set once, when <c>RegionTA</c> creates the region (JADE
/// <c>onCreate</c> and <c>setCommonProperties</c> assign <c>pRegionTA.myCountry</c>). The region's
/// membership of <see cref="Country.AllRegions"/> and the items' membership of
/// <see cref="AllItems"/> are maintained by <c>RegionTA</c> and <c>ItemTA</c> in
/// <c>Validation.cs</c>, the counterpart of the JADE automatic inverses
/// (<c>allRegions of Country automatic parentOf myCountry of Region manual</c> and
/// <c>myRegion of Item manual peerOf allItems of Region automatic</c>); the region only exposes
/// the dictionary.
/// </para>
/// <para>
/// The DocumentorPackage and debugging methods (<c>documentSelfRTF</c>, <c>getDebugString</c>,
/// <c>getTAClass</c>) serve the JADE IDE only and are not ported. Not thread-safe.
/// </para>
/// </remarks>
public sealed class Region : Location
{
    /// <summary>Creates a region with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    internal Region(long oid)
        : base(oid)
    {
    }

    /// <summary>
    /// JADE <c>myCountry</c>: the country whose <see cref="Country.AllRegions"/> holds this region.
    /// </summary>
    public Country? MyCountry { get; internal set; }

    /// <summary>
    /// JADE <c>allItems</c>: the retail and tender items located in this region, keyed by
    /// <c>myItemCategory.prefix</c>, <c>codeNumber</c>.
    /// </summary>
    public ItemsByPrefixByNumberDict AllItems { get; } = new();

    /// <summary>
    /// JADE <c>getCountryAndRegionNames</c>: the country name, a space, then the region name, for
    /// example <c>UK London</c>.
    /// </summary>
    /// <remarks>
    /// A region without a country, which <c>RegionTA</c> never creates, contributes the JADE null
    /// string for the country name rather than raising.
    /// </remarks>
    public string GetCountryAndRegionNames() => (MyCountry?.Name ?? "") + " " + Name;
}

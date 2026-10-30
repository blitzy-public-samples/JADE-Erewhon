namespace Erewhon.StandIn.Model;

/// <summary>
/// JADE <c>ItemCategory</c>: a category of sale items (seed <c>DEST</c> Holidays, <c>ANQU</c>
/// Antiques, <c>PROP</c> Properties) with its commission rates and items.
/// </summary>
/// <remarks>
/// <para>
/// Every attribute holds the value <c>ItemCategoryTA</c> assigned (JADE
/// <c>setCommonProperties</c>), already trimmed, upper-cased and validated there; this class
/// neither trims nor validates. The empty string is the JADE null string. The declared JADE
/// lengths are documented but not enforced.
/// </para>
/// <para>
/// <see cref="Name"/> keys <see cref="Company.AllItemCategoriesByName"/> and every
/// <see cref="CommissionRatesByCategoryDict"/>; <see cref="Prefix"/> keys
/// <see cref="Company.AllItemCategoriesByPrefix"/> and every prefix-keyed item and sale
/// dictionary. Both are set once, when the category is created, so no dictionary needs re-keying.
/// </para>
/// <para>
/// Dictionary membership is maintained by the transaction agents in <c>Validation.cs</c>, the
/// counterpart of the JADE automatic inverses. Not thread-safe.
/// </para>
/// </remarks>
public sealed class ItemCategory : PersistentModel
{
    /// <summary>Creates a category with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    internal ItemCategory(long oid)
        : base(oid)
    {
    }

    /// <summary>JADE <c>name: String[41]</c>, for example <c>Holidays</c>.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>JADE <c>description: String</c>.</summary>
    public string Description { get; internal set; } = "";

    /// <summary>
    /// JADE <c>prefix: String[5]</c>, for example <c>DEST</c>; upper-cased by
    /// <c>ItemCategoryTA.doPreValidate</c> before it is assigned.
    /// </summary>
    public string Prefix { get; internal set; } = "";

    /// <summary>
    /// JADE <c>myCompany</c>, set when the category is created (JADE <c>onCreate</c> assigns
    /// <c>app.myCompany</c>).
    /// </summary>
    public Company? MyCompany { get; internal set; }

    /// <summary>
    /// JADE <c>allCommissionRates</c>, keyed by percentage ascending: the first entry is the
    /// category's default (lowest) rate, which <c>Agent.getCommissionRateForCategory</c> falls back
    /// to when the agent has no rate of its own for this category.
    /// </summary>
    public CommissionRatesByPercentDict AllCommissionRates { get; } = new();

    /// <summary>
    /// JADE <c>allItems</c>: the category's retail and tender items, keyed by code number.
    /// </summary>
    public ItemsByNumberDict AllItems { get; } = new();

    // JADE getNextCodeNumber (per category, never called) is not ported; see Company.GetNextCodeNumber.
}

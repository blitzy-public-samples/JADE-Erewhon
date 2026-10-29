namespace Erewhon.StandIn.Model;

/// <summary>
/// JADE <c>Agent</c>: a seller who lists retail and tender items and earns a commission on their
/// sales at the rate held for each item category.
/// </summary>
/// <remarks>
/// <para>
/// Dictionary membership is maintained by <c>AgentTA</c> and the item and sale transaction agents
/// in <c>Validation.cs</c>, the counterpart of the JADE automatic inverses. As in JADE
/// <c>onCreate</c>, the agent's <see cref="Name"/> is set before it joins
/// <see cref="Company.AllAgents"/>, and <see cref="MyCompany"/> is set after it.
/// </para>
/// <para>
/// <see cref="Name"/> keys <see cref="Company.AllAgents"/> and the <see cref="CommissionRate.AllAgents"/>
/// of every rate in <see cref="AllCommissionRates"/>, so whoever changes it re-keys the agent in
/// each of them.
/// </para>
/// <para>
/// JADE <c>getAllSales</c> (the <c>ISearchable</c> GUI search), <c>getDebugString</c>,
/// <c>getNameString</c>, <c>documentSelfRTF</c> and <c>getTAClass</c> are not ported: no REST,
/// SOAP or seed path reads them. Not thread-safe.
/// </para>
/// </remarks>
public sealed class Agent : PersistentModel
{
    /// <summary>Creates an agent with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    internal Agent(long oid)
        : base(oid)
    {
    }

    /// <summary>
    /// JADE <c>name: String[81]</c>, already trimmed of blanks by <c>AgentTA</c>; the key of
    /// <see cref="Company.AllAgents"/> and of every <see cref="CommissionRate.AllAgents"/>. The
    /// length limit is not enforced.
    /// </summary>
    public string Name { get; internal set; } = "";

    /// <summary>JADE <c>myAddress</c>, set when <c>AgentTA</c> creates the agent's address.</summary>
    public Address? MyAddress { get; internal set; }

    /// <summary>
    /// JADE <c>myCompany</c>, set when <c>AgentTA</c> creates the agent (JADE <c>onCreate</c>
    /// assigns <c>app.myCompany</c>).
    /// </summary>
    public Company? MyCompany { get; internal set; }

    /// <summary>
    /// JADE <c>allCommissionRates</c>, keyed by the rate's category name: at most one rate per
    /// category, the one the agent earns on that category's sales. It is the manual peer of
    /// <see cref="CommissionRate.AllAgents"/>; change it only through
    /// <see cref="AddCommissionRate"/> and <see cref="RemoveCommissionRate"/>, which keep both sides
    /// in step.
    /// </summary>
    public CommissionRatesByCategoryDict AllCommissionRates { get; } = new();

    /// <summary>
    /// JADE <c>allItems</c>: every retail and tender item the agent lists, keyed by category, then
    /// category prefix and code number.
    /// </summary>
    public ItemsByCategoryPrefixDict AllItems { get; } = new();

    /// <summary>
    /// JADE <c>allSoldItems</c> (<c>where Item::isSold</c>): the agent's items that have a sale,
    /// keyed as <see cref="AllItems"/>. An item joins only once its sale is set.
    /// </summary>
    public ItemsByCategoryPrefixDict AllSoldItems { get; } = new();

    /// <summary>
    /// JADE <c>getCommissionRateForCategory</c>: the rate the agent earns on items of the category
    /// with exactly this name.
    /// </summary>
    /// <remarks>
    /// The agent's own rate for the category wins. Otherwise the category's default rate applies,
    /// which is the first of its rates in percentage order, the lowest (2% in the seed). JADE raises
    /// a null-reference exception for an agent without a company; every agent <c>AgentTA</c> creates
    /// has one, so skipping the default for a missing company applies only to an agent still being
    /// built.
    /// </remarks>
    /// <param name="categoryName">The category name, compared ordinally.</param>
    /// <returns>
    /// The rate, or null when the agent has no rate for the category and the category does not
    /// exist or has no rates.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="categoryName"/> is null.</exception>
    public CommissionRate? GetCommissionRateForCategory(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);

        CommissionRate? rate = AllCommissionRates.GetAtKey(categoryName);
        if (rate != null)
        {
            return rate;
        }

        ItemCategory? category = MyCompany?.AllItemCategoriesByName.GetAtKey(categoryName);
        return category?.AllCommissionRates.First();
    }

    /// <summary>
    /// JADE <c>getNameAndAddress</c>: the name, a comma and a space, then the formatted address.
    /// </summary>
    /// <remarks>
    /// JADE raises a null-reference exception for an agent without an address; every agent
    /// <c>AgentTA</c> creates has one, so the empty address text applies only to an agent still
    /// being built.
    /// </remarks>
    public string GetNameAndAddress() => Name + ", " + (MyAddress?.GetFormattedAddress() ?? "");

    /// <summary>
    /// JADE <c>onModify_AddCommissionRate</c> (<c>AgentTA</c> modification 1004): makes
    /// <paramref name="rate"/> the agent's rate for its category, replacing any other rate the
    /// agent holds for that category, and keeps <see cref="CommissionRate.AllAgents"/> of both
    /// rates in step.
    /// </summary>
    /// <param name="rate">The rate to use; it must have a category.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rate"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="rate"/> has no category.</exception>
    internal void AddCommissionRate(CommissionRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);

        CommissionRate? existing = AllCommissionRates.GetAtKey(CategoryNameOf(rate));
        if (ReferenceEquals(existing, rate))
        {
            // The agent already uses the supplied rate.
            return;
        }

        if (existing != null)
        {
            AllCommissionRates.Remove(existing);
            existing.AllAgents.Remove(this);
        }

        AllCommissionRates.Add(rate);

        // CommissionRate.allAgents is the automatic inverse, which never raises for an agent it
        // already holds.
        if (!rate.AllAgents.Includes(this))
        {
            rate.AllAgents.Add(this);
        }
    }

    /// <summary>
    /// JADE <c>onModify_RemoveCommissionRate</c> (<c>AgentTA</c> modification 1005): removes the
    /// rate the agent holds for <paramref name="rate"/>'s category, if any, from both sides. As in
    /// JADE, the rate removed is looked up by category name, so it need not be
    /// <paramref name="rate"/> itself.
    /// </summary>
    /// <param name="rate">A rate of the category whose agent rate is removed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rate"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="rate"/> has no category.</exception>
    internal void RemoveCommissionRate(CommissionRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);

        CommissionRate? existing = AllCommissionRates.GetAtKey(CategoryNameOf(rate));
        if (existing == null)
        {
            return;
        }

        AllCommissionRates.Remove(existing);
        existing.AllAgents.Remove(this);
    }

    // JADE reads pAgentTA.myCommissionRate.myItemCategory.name, raising a null-reference exception
    // for a rate without a category; CommissionRateTA never creates one.
    private static string CategoryNameOf(CommissionRate rate) =>
        rate.MyItemCategory?.Name
        ?? throw new InvalidOperationException(
            $"Commission rate (oid {rate.Oid}) has no item category, so it cannot be an agent's rate.");
}

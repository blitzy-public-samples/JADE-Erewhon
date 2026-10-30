namespace Erewhon.StandIn.Model;

/// <summary>
/// JADE <c>CommissionRate</c>: one commission percentage of an item category, together with the
/// agents who earn it on that category's sales.
/// </summary>
/// <remarks>
/// <para>
/// The seed gives every category the rates 2, 3.5, 5, 8 and 12 (<c>CategoryRates.txt</c>) and
/// assigns each agent one rate per category (<c>AgentRates.txt</c>). A sale's agent commission is
/// the sale price times <see cref="Percentage"/> divided by 100, using the rate
/// <c>Agent.getCommissionRateForCategory</c> selects.
/// </para>
/// <para>
/// Both attributes hold the values <c>CommissionRateTA</c> assigned (JADE
/// <c>setCommonProperties</c>), already rounded and validated there; this class neither rounds
/// nor validates.
/// </para>
/// <para>
/// <see cref="Percentage"/> keys <see cref="ItemCategory.AllCommissionRates"/> of
/// <see cref="MyItemCategory"/>, and the category's name keys every agent's
/// <see cref="CommissionRatesByCategoryDict"/>, so whoever changes either re-keys the rate there.
/// </para>
/// <para>
/// Dictionary membership is maintained by the transaction agents in <c>Validation.cs</c>, the
/// counterpart of the JADE automatic inverses: <c>CommissionRateTA</c> (JADE
/// <c>addOrRemoveAgents</c>) issues the <c>AgentTA</c> add- and remove-commission-rate
/// modifications, which keep <see cref="AllAgents"/> in step with each agent's rates. The rate
/// only exposes the dictionary.
/// </para>
/// <para>
/// JADE <c>getDebugString</c> and <c>getTAClass</c> are not ported: no REST, SOAP or seed path
/// reads them. Not thread-safe.
/// </para>
/// </remarks>
public sealed class CommissionRate : PersistentModel
{
    /// <summary>Creates a rate with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    internal CommissionRate(long oid)
        : base(oid)
    {
    }

    /// <summary>
    /// JADE <c>percentage: Decimal[5,2]</c>, assigned already rounded to two decimal places. It is
    /// compared by value, so <c>3.5</c> and <c>3.50</c> are the same rate.
    /// </summary>
    public decimal Percentage { get; internal set; }

    /// <summary>
    /// JADE <c>myItemCategory</c>, the category whose
    /// <see cref="ItemCategory.AllCommissionRates"/> holds this rate.
    /// </summary>
    public ItemCategory? MyItemCategory { get; internal set; }

    /// <summary>
    /// JADE <c>allAgents</c>, keyed by name: the agents who use this rate for
    /// <see cref="MyItemCategory"/>. It is the automatic peer of each <see cref="Agent"/>'s
    /// <c>allCommissionRates</c>.
    /// </summary>
    public AgentsByNameDict AllAgents { get; } = new();
}

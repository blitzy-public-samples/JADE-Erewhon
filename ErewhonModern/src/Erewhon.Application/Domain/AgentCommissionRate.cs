namespace Erewhon.Application.Domain;

/// <summary>
/// The link between an agent and a commission rate the agent earns; an agent holds at most one rate per item category.
/// </summary>
public class AgentCommissionRate
{
    /// <summary>The row id of the agent.</summary>
    public long AgentId { get; set; }

    /// <summary>The agent; deleting the agent deletes the link.</summary>
    public Agent? Agent { get; set; }

    /// <summary>The row id of the commission rate.</summary>
    public long CommissionRateId { get; set; }

    /// <summary>The commission rate; deleting the rate deletes the link.</summary>
    public CommissionRate? CommissionRate { get; set; }

    /// <summary>The row id of the commission rate's item category, equal to <see cref="Domain.CommissionRate.ItemCategoryId"/> of <see cref="CommissionRate"/>.</summary>
    public long ItemCategoryId { get; set; }
}

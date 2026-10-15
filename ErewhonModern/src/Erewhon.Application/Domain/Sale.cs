using Erewhon.Application.Common;

namespace Erewhon.Application.Domain;

/// <summary>
/// The sale of one item to a client of a company, and the commission the item's agent earns on it.
/// </summary>
public abstract class Sale : PersistentModel
{
    /// <summary>The row id of the owning company.</summary>
    public long CompanyId { get; set; }

    /// <summary>The owning company; deleting the company deletes the sale.</summary>
    public Company? Company { get; set; }

    /// <summary>The row id of the client who bought the item.</summary>
    public long ClientId { get; set; }

    /// <summary>The client who bought the item; deleting the client deletes the sale.</summary>
    public Client? Client { get; set; }

    /// <summary>The row id of the sold item; an item has at most one sale.</summary>
    public long ItemId { get; set; }

    /// <summary>The sold item; deleting the item deletes the sale.</summary>
    public Item? Item { get; set; }

    /// <summary>The commission earned by the item's agent on the sale, stored with precision 12 and 2 decimal places.</summary>
    public decimal AgentCommission { get; set; }

    /// <summary>
    /// Sets <see cref="AgentCommission"/> to <paramref name="salePrice"/> times the percentage of the rate returned by
    /// <see cref="Agent.GetCommissionRateForCategory"/> for the item's category, divided by 100 and rounded to 2 decimal
    /// places with ties away from zero, e.g. 0.81 for a sale price of 23 and a rate of 3.5; sets it to 0 when the item
    /// has no agent or no rate applies.
    /// </summary>
    /// <param name="salePrice">The amount the commission is earned on: the retail sale price or the winning tender offer.</param>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Item"/> is not loaded, or the item has an agent and its <see cref="Domain.Item.ItemCategory"/> is not loaded.
    /// </exception>
    public void CalculateAgentCommission(decimal salePrice)
    {
        AgentCommission = 0;
        var item = Item ?? throw new InvalidOperationException("Sale.Item is not loaded.");
        var agent = item.Agent;
        if (agent is not null)
        {
            var category = item.ItemCategory
                ?? throw new InvalidOperationException("Sale.Item.ItemCategory is not loaded.");
            var rate = agent.GetCommissionRateForCategory(category.Name);
            if (rate is not null)
            {
                AgentCommission = DecimalRules.Round2(salePrice * (rate.Percentage / 100m));
            }
        }
    }

    /// <summary>Returns the date of the sale; <see langword="null"/> on the base class.</summary>
    /// <returns>The sale date, or <see langword="null"/>.</returns>
    public virtual DateOnly? GetDate() => null;

    /// <summary>Returns the developer description of the sale, beginning with its OID text and its class name.</summary>
    /// <returns>The debug string.</returns>
    public abstract string GetDebugString();
}

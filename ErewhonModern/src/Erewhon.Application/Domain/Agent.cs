using Erewhon.Application.Common;

namespace Erewhon.Application.Domain;

/// <summary>
/// A sales agent of a company: its name and address, the commission rates it earns and the items it sells.
/// </summary>
public class Agent : PersistentModel
{
    /// <summary>The row id of the owning company.</summary>
    public long CompanyId { get; set; }

    /// <summary>The owning company; deleting the company deletes the agent.</summary>
    public Company? Company { get; set; }

    /// <summary>Agent name, unique within its company; at most 81 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The address of the agent.</summary>
    public Address Address { get; set; } = new();

    /// <summary>The links to the commission rates the agent earns, at most one per item category; deleting the agent deletes them.</summary>
    public ICollection<AgentCommissionRate> AgentCommissionRates { get; set; } = new List<AgentCommissionRate>();

    /// <summary>The items the agent sells; deleting the agent clears their agent.</summary>
    public ICollection<Item> Items { get; set; } = new List<Item>();

    /// <summary>The class number of <c>Agent</c>, <see cref="ObjectIdentity.Agent"/>.</summary>
    public override int JadeClassNumber => ObjectIdentity.Agent;

    /// <summary>Returns <see cref="Name"/>.</summary>
    /// <returns>The agent name.</returns>
    public string GetNameString() => Name;

    /// <summary>
    /// Returns the name, a comma and a space, and the formatted address,
    /// e.g. <c>John Smith, 314 Nohwere Lane, Christchurch, New Zealand</c>.
    /// </summary>
    /// <returns>The name and address.</returns>
    public string GetNameAndAddress() => Name + ", " + Address.GetFormattedAddress();

    /// <summary>
    /// Returns the developer description <c>&lt;oid&gt; Agent</c>, e.g. <c>5774.3 Agent</c> for the agent with id 3.
    /// </summary>
    /// <returns>The debug string.</returns>
    public string GetDebugString() => ObjectIdentity.Of(this) + " Agent";

    /// <summary>
    /// Returns the sold items of <see cref="Items"/>, ordered by category row id, category prefix (ordinal) and code number.
    /// </summary>
    /// <returns>The sold items; empty when the agent has none.</returns>
    public IReadOnlyList<Item> GetSoldItems() =>
        Items
            .Where(item => item.IsSold)
            .OrderBy(item => item.ItemCategoryId)
            .ThenBy(item => item.ItemCategory?.Prefix, StringComparer.Ordinal)
            .ThenBy(item => item.CodeNumber)
            .ToList();

    /// <summary>
    /// Returns the sale of each item in <see cref="GetSoldItems"/>, each sale once, ordered by class number and then row id.
    /// </summary>
    /// <returns>The sales; empty when the agent has sold nothing.</returns>
    public IReadOnlyList<Sale> GetAllSales() =>
        GetSoldItems()
            .Select(item => item.GetSale())
            .OfType<Sale>()
            .Distinct<Sale>(ReferenceEqualityComparer.Instance)
            .OrderBy(sale => sale.JadeClassNumber)
            .ThenBy(sale => sale.Id)
            .ToList();

    /// <summary>
    /// Returns the agent's rate for the item category named <paramref name="categoryName"/>; otherwise the
    /// lowest-percentage rate of that category in <see cref="Company"/>; otherwise <see langword="null"/>.
    /// Category names compare by ordinal comparison.
    /// </summary>
    /// <param name="categoryName">The name of the item category.</param>
    /// <returns>
    /// The commission rate, or <see langword="null"/> when the company has no category of that name, the category has
    /// no rates, or <see cref="Company"/> is not loaded and the agent has no rate for the category.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// A link in <see cref="AgentCommissionRates"/> has its <see cref="AgentCommissionRate.CommissionRate"/>, or that
    /// rate its <see cref="CommissionRate.ItemCategory"/>, not loaded.
    /// </exception>
    public CommissionRate? GetCommissionRateForCategory(string categoryName)
    {
        foreach (var link in AgentCommissionRates)
        {
            var rate = link.CommissionRate
                ?? throw new InvalidOperationException("AgentCommissionRate.CommissionRate is not loaded.");
            var rateCategory = rate.ItemCategory
                ?? throw new InvalidOperationException("CommissionRate.ItemCategory is not loaded.");
            if (string.Equals(rateCategory.Name, categoryName, StringComparison.Ordinal))
            {
                return rate;
            }
        }

        var category = Company?.ItemCategories
            .FirstOrDefault(candidate => string.Equals(candidate.Name, categoryName, StringComparison.Ordinal));

        return category?.CommissionRates
            .OrderBy(rate => rate.Percentage)
            .FirstOrDefault();
    }
}

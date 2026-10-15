using Erewhon.Application.Common;

namespace Erewhon.Application.Domain;

/// <summary>
/// A commission percentage offered on an item category, and the agents who earn it.
/// </summary>
public class CommissionRate : PersistentModel
{
    /// <summary>The row id of the owning item category.</summary>
    public long ItemCategoryId { get; set; }

    /// <summary>The owning item category; deleting the category deletes the rate.</summary>
    public ItemCategory? ItemCategory { get; set; }

    /// <summary>The commission percentage, stored with precision 5 and 2 decimal places.</summary>
    public decimal Percentage { get; set; }

    /// <summary>The links to the agents using this rate; deleting the rate deletes them.</summary>
    public ICollection<AgentCommissionRate> AgentCommissionRates { get; set; } = new List<AgentCommissionRate>();

    /// <summary>The class number of <c>CommissionRate</c>, <see cref="ObjectIdentity.CommissionRate"/>.</summary>
    public override int JadeClassNumber => ObjectIdentity.CommissionRate;

    /// <summary>
    /// Returns the developer description <c>&lt;oid&gt; CommissionRate: &lt;percentage&gt;%</c>, with the percentage
    /// printed to 2 decimal places, e.g. <c>5777.2 CommissionRate: 11.00%</c> for the rate with id 2 and percentage 11.
    /// </summary>
    /// <returns>The debug string.</returns>
    public string GetDebugString() =>
        ObjectIdentity.Of(this) + " CommissionRate: " + JadeFormat.DecimalString(Percentage) + "%";
}

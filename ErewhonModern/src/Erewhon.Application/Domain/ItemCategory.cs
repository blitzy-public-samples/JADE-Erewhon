using Erewhon.Application.Common;

namespace Erewhon.Application.Domain;

/// <summary>
/// A category of sale items owned by a company: its name, its code prefix, its description,
/// its commission rates and its items.
/// </summary>
public class ItemCategory : PersistentModel
{
    /// <summary>The row id of the owning company.</summary>
    public long CompanyId { get; set; }

    /// <summary>The owning company; deleting the company deletes the category.</summary>
    public Company? Company { get; set; }

    /// <summary>Category name; at most 41 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Code prefix of the category's items, stored trimmed and in upper case; at most 5 characters.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Category description; unbounded.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The commission rates of the category; deleting the category deletes them.</summary>
    public ICollection<CommissionRate> CommissionRates { get; set; } = new List<CommissionRate>();

    /// <summary>The items in the category; deleting the category deletes them.</summary>
    public ICollection<Item> Items { get; set; } = new List<Item>();

    /// <summary>The class number of <c>ItemCategory</c>, <see cref="ObjectIdentity.ItemCategory"/>.</summary>
    public override int JadeClassNumber => ObjectIdentity.ItemCategory;

    /// <summary>
    /// Returns the developer description <c>&lt;oid&gt; ItemCategory: &lt;name&gt;, &lt;description&gt;</c>,
    /// e.g. <c>5788.4 ItemCategory: Category Item, Some Category Description</c> for the category with id 4.
    /// </summary>
    /// <returns>The debug string.</returns>
    public string GetDebugString() =>
        ObjectIdentity.Of(this) + " ItemCategory: " + Name + ", " + Description;
}

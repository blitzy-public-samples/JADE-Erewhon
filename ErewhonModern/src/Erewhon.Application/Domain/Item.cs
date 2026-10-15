using System.Globalization;
using Erewhon.Application.Common;

namespace Erewhon.Application.Domain;

/// <summary>
/// An item offered for sale by a company: its code, name, description, listed date, photo and price,
/// its agent, category and region, and the sale that sold it.
/// </summary>
public abstract class Item : PersistentModel
{
    /// <summary>The row id of the owning company.</summary>
    public long CompanyId { get; set; }

    /// <summary>The owning company; deleting the company deletes the item.</summary>
    public Company? Company { get; set; }

    /// <summary>The code number of the item, unique within its company.</summary>
    public int CodeNumber { get; set; }

    /// <summary>Item name; at most 31 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Item description; unbounded.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The date the item was listed.</summary>
    public DateOnly ListedDate { get; set; }

    /// <summary>The picture of the item; <see langword="null"/> when the item has none.</summary>
    public byte[]? Photo { get; set; }

    /// <summary>The price of the item to 2 decimal places; for a tender item, its reserve price.</summary>
    public decimal Price { get; set; }

    /// <summary>The row id of the agent selling the item; <see langword="null"/> when it has no agent.</summary>
    public long? AgentId { get; set; }

    /// <summary>The agent selling the item; deleting the agent clears it.</summary>
    public Agent? Agent { get; set; }

    /// <summary>The row id of the item's category.</summary>
    public long ItemCategoryId { get; set; }

    /// <summary>The category of the item; deleting the category deletes the item.</summary>
    public ItemCategory? ItemCategory { get; set; }

    /// <summary>The row id of the region the item is listed in; <see langword="null"/> when it has no region.</summary>
    public long? RegionId { get; set; }

    /// <summary>The region the item is listed in; deleting the region clears it.</summary>
    public Region? Region { get; set; }

    /// <summary>The sale of the item; <see langword="null"/> while the item is unsold. Deleting the item deletes the sale.</summary>
    public Sale? Sale { get; set; }

    /// <summary>True when <see cref="Sale"/> is set.</summary>
    public bool IsSold => Sale is not null;

    /// <summary>
    /// Returns the category prefix followed by the code number, e.g. <c>CATI7</c> for prefix <c>CATI</c> and code number 7,
    /// or <see langword="null"/> when the item has no category.
    /// </summary>
    /// <returns>The item code, or <see langword="null"/>.</returns>
    public string? GetCode() =>
        ItemCategory is null ? null : ItemCategory.Prefix + CodeNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>Returns <see cref="CodeNumber"/>.</summary>
    /// <returns>The code number.</returns>
    public int GetCodeNumber() => CodeNumber;

    /// <summary>
    /// Returns the prefix of the item's category, or <see langword="null"/> when the item has no category.
    /// </summary>
    /// <returns>The category prefix, or <see langword="null"/>.</returns>
    public string? GetCodePrefix() => ItemCategory?.Prefix;

    /// <summary>Returns <see cref="Name"/>.</summary>
    /// <returns>The item name.</returns>
    public string GetName() => Name;

    /// <summary>Returns the price of the item.</summary>
    /// <returns>The price.</returns>
    public abstract decimal GetPrice();

    /// <summary>Returns <see cref="Sale"/>: the sale of the item, or <see langword="null"/> while it is unsold.</summary>
    /// <returns>The sale, or <see langword="null"/>.</returns>
    public Sale? GetSale() => Sale;

    /// <summary>Returns the display name of the item's type.</summary>
    /// <returns>The type text.</returns>
    public abstract string GetTypeString();

    /// <summary>Returns whether <paramref name="offer"/> is an acceptable amount for a cart line on the item.</summary>
    /// <param name="offer">The amount offered for the item.</param>
    /// <returns><see langword="true"/> when the offer is acceptable; otherwise <see langword="false"/>.</returns>
    public abstract bool CheckOffer(decimal offer);

    /// <summary>Returns the developer description of the item.</summary>
    /// <returns>The debug string.</returns>
    public abstract string GetDebugString();

    /// <summary>
    /// Returns <c>&lt;oid&gt; &lt;className&gt;: &lt;code&gt;, &lt;name&gt;, $&lt;price&gt;</c>,
    /// e.g. <c>5786.9 RetailItem: CATI2, Proxy Retail, $5.50</c>; the code part is empty when the item has no category.
    /// </summary>
    /// <param name="className">The class name printed after the OID text, e.g. <c>RetailItem</c>.</param>
    /// <returns>The debug string.</returns>
    protected string BuildItemDebugString(string className) =>
        ObjectIdentity.Of(this) + " " + className + ": " + GetCode() + ", " + Name + ", $" + JadeFormat.DecimalString(Price);
}

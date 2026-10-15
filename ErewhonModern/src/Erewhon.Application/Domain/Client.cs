using Erewhon.Application.Common;

namespace Erewhon.Application.Domain;

/// <summary>
/// A client of a company: its name and address, the retail and tender sales made to it and the tenders it has placed.
/// </summary>
public class Client : PersistentModel
{
    /// <summary>The row id of the owning company.</summary>
    public long CompanyId { get; set; }

    /// <summary>The owning company; deleting the company deletes the client.</summary>
    public Company? Company { get; set; }

    /// <summary>Client name, unique within its company; at most 81 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The address of the client.</summary>
    public Address Address { get; set; } = new();

    /// <summary>The retail and tender sales made to the client; deleting the client deletes them.</summary>
    public ICollection<Sale> Sales { get; set; } = new List<Sale>();

    /// <summary>The tenders the client has placed; deleting the client deletes them.</summary>
    public ICollection<Tender> Tenders { get; set; } = new List<Tender>();

    /// <summary>The class number of <c>Client</c>, <see cref="ObjectIdentity.Client"/>.</summary>
    public override int JadeClassNumber => ObjectIdentity.Client;

    /// <summary>
    /// Returns the retail sales of <see cref="Sales"/>, ordered by <see cref="RetailSale.DateSold"/>, then the item's
    /// category prefix (ordinal), then the item's code number. A sale whose item or category is not loaded sorts first
    /// among sales with the same earlier keys.
    /// </summary>
    /// <returns>The retail sales; empty when the client has none.</returns>
    public IReadOnlyList<RetailSale> GetRetailSales() =>
        Sales
            .OfType<RetailSale>()
            .OrderBy(sale => sale.DateSold)
            .ThenBy(sale => sale.Item?.ItemCategory?.Prefix, StringComparer.Ordinal)
            .ThenBy(sale => sale.Item?.CodeNumber)
            .ToList();

    /// <summary>
    /// Returns the tender sales of <see cref="Sales"/>, ordered by the <see cref="Domain.Tender.DateTendered"/> of the
    /// winning tender, then the item's category prefix (ordinal), then the item's code number. A sale whose tender, item
    /// or category is not loaded sorts first among sales with the same earlier keys.
    /// </summary>
    /// <returns>The tender sales; empty when the client has none.</returns>
    public IReadOnlyList<TenderSale> GetTenderSales() =>
        Sales
            .OfType<TenderSale>()
            .OrderBy(sale => sale.Tender?.DateTendered)
            .ThenBy(sale => sale.Item?.ItemCategory?.Prefix, StringComparer.Ordinal)
            .ThenBy(sale => sale.Item?.CodeNumber)
            .ToList();

    /// <summary>
    /// Returns the sales of <see cref="GetRetailSales"/> and <see cref="GetTenderSales"/>, each sale once, ordered by
    /// class number and then row id, so retail sales precede tender sales.
    /// </summary>
    /// <returns>The sales; empty when the client has none.</returns>
    public IReadOnlyList<Sale> GetAllSales() =>
        GetRetailSales()
            .Concat<Sale>(GetTenderSales())
            .Distinct<Sale>(ReferenceEqualityComparer.Instance)
            .OrderBy(sale => sale.JadeClassNumber)
            .ThenBy(sale => sale.Id)
            .ToList();

    /// <summary>
    /// Returns the name, a comma and a space, and the formatted address,
    /// e.g. <c>Clive Entworth, 314 Nohwere Lane, Christchurch, New Zealand</c>.
    /// </summary>
    /// <returns>The name and address.</returns>
    public string GetNameAndAddress() => Name + ", " + Address.GetFormattedAddress();

    /// <summary>
    /// Returns the developer description <c>&lt;oid&gt; Client</c>, e.g. <c>5776.2 Client</c> for the client with id 2.
    /// </summary>
    /// <returns>The debug string.</returns>
    public string GetDebugString() => ObjectIdentity.Of(this) + " Client";
}

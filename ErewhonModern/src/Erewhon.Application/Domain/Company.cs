using Erewhon.Application.Common;

namespace Erewhon.Application.Domain;

/// <summary>
/// A company: its name and address, and the agents, clients, countries, item categories, items, sales and tenders it owns.
/// Deleting the company deletes everything it owns.
/// </summary>
public class Company : PersistentModel
{
    /// <summary>Company name; at most 81 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The address of the company.</summary>
    public Address Address { get; set; } = new();

    /// <summary>The agents of the company, unique by name; deleting the company deletes them.</summary>
    public ICollection<Agent> Agents { get; set; } = new List<Agent>();

    /// <summary>The clients of the company, unique by name; deleting the company deletes them.</summary>
    public ICollection<Client> Clients { get; set; } = new List<Client>();

    /// <summary>The countries of the company, unique by name; deleting the company deletes them.</summary>
    public ICollection<Country> Countries { get; set; } = new List<Country>();

    /// <summary>
    /// The item categories of the company, unique by name and by prefix compared case-insensitively;
    /// deleting the company deletes them.
    /// </summary>
    public ICollection<ItemCategory> ItemCategories { get; set; } = new List<ItemCategory>();

    /// <summary>
    /// The retail and tender items of the company, unique by code number; deleting the company deletes them.
    /// </summary>
    public ICollection<Item> Items { get; set; } = new List<Item>();

    /// <summary>
    /// The retail and tender sales of the company, at most one per item; deleting the company deletes them.
    /// </summary>
    public ICollection<Sale> Sales { get; set; } = new List<Sale>();

    /// <summary>
    /// The tenders placed on the company's tender items, unique by time tendered and offer;
    /// deleting the company deletes them.
    /// </summary>
    public ICollection<Tender> Tenders { get; set; } = new List<Tender>();

    /// <summary>The class number of <c>Company</c>, <see cref="ObjectIdentity.Company"/>.</summary>
    public override int JadeClassNumber => ObjectIdentity.Company;

    /// <summary>
    /// Returns the developer description <c>&lt;oid&gt; Company</c>, e.g. <c>5778.1 Company</c> for the company with id 1.
    /// </summary>
    /// <returns>The debug string.</returns>
    public string GetDebugString() => ObjectIdentity.Of(this) + " Company";

    /// <summary>
    /// Returns the name, a comma and a space, and the formatted address,
    /// e.g. <c>Jade Software Corporation, 314 Nohwere Lane, Christchurch, New Zealand</c>.
    /// </summary>
    /// <returns>The name and address.</returns>
    public string GetNameAndAddress() => Name + ", " + Address.GetFormattedAddress();

    /// <summary>
    /// Returns the sales of <see cref="Sales"/>, each sale once, ordered by class number and then row id,
    /// so retail sales precede tender sales.
    /// </summary>
    /// <returns>The sales; empty when the company has none.</returns>
    public IReadOnlyList<Sale> GetAllSales() =>
        Sales
            .Distinct<Sale>(ReferenceEqualityComparer.Instance)
            .OrderBy(sale => sale.JadeClassNumber)
            .ThenBy(sale => sale.Id)
            .ToList();
}

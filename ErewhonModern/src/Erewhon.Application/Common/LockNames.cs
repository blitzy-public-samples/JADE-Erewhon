using System.Globalization;

namespace Erewhon.Application.Common;

/// <summary>
/// Names of the advisory locks taken on owned collections, in the form
/// <c>&lt;Owner&gt;.&lt;collection&gt;:&lt;ownerId&gt;</c>.
/// </summary>
public static class LockNames
{
    private const string Company = "Company";
    private const string ItemCategory = "ItemCategory";
    private const string Agent = "Agent";
    private const string Region = "Region";
    private const string Country = "Country";
    private const string Client = "Client";
    private const string TenderItem = "TenderItem";

    private const string AllAgents = "allAgents";
    private const string AllClients = "allClients";
    private const string AllCountries = "allCountries";
    private const string AllItemCategoriesByName = "allItemCategoriesByName";
    private const string AllItems = "allItems";
    private const string AllSalesByItem = "allSalesByItem";
    private const string AllTenderItemsBySaleDate = "allTenderItemsBySaleDate";
    private const string AllCommissionRates = "allCommissionRates";
    private const string AllSoldItems = "allSoldItems";
    private const string AllRegions = "allRegions";
    private const string AllRetailSales = "allRetailSales";
    private const string AllTenderSales = "allTenderSales";
    private const string AllTenders = "allTenders";
    private const string AllTendersByOfferTime = "allTendersByOfferTime";
    private const string AllTendersByTimeOffer = "allTendersByTimeOffer";

    /// <summary>
    /// Builds a lock name <c>&lt;owner&gt;.&lt;collection&gt;:&lt;ownerId&gt;</c>, formatting the id with the invariant culture.
    /// </summary>
    /// <param name="owner">Class name of the collection owner, for example <c>Company</c>.</param>
    /// <param name="collection">Property name of the collection, for example <c>allItems</c>.</param>
    /// <param name="ownerId">Row id of the collection owner.</param>
    /// <returns>The lock name.</returns>
    public static string Format(string owner, string collection, long ownerId) =>
        string.Create(CultureInfo.InvariantCulture, $"{owner}.{collection}:{ownerId}");

    /// <summary>Lock name for a company's agent collection: <c>Company.allAgents:&lt;ownerId&gt;</c>.</summary>
    /// <param name="ownerId">Row id of the company.</param>
    public static string CompanyAllAgents(long ownerId) => Format(Company, AllAgents, ownerId);

    /// <summary>Lock name for a company's client collection: <c>Company.allClients:&lt;ownerId&gt;</c>.</summary>
    /// <param name="ownerId">Row id of the company.</param>
    public static string CompanyAllClients(long ownerId) => Format(Company, AllClients, ownerId);

    /// <summary>Lock name for a company's country collection: <c>Company.allCountries:&lt;ownerId&gt;</c>.</summary>
    /// <param name="ownerId">Row id of the company.</param>
    public static string CompanyAllCountries(long ownerId) => Format(Company, AllCountries, ownerId);

    /// <summary>
    /// Lock name for a company's item categories keyed by name: <c>Company.allItemCategoriesByName:&lt;ownerId&gt;</c>.
    /// </summary>
    /// <param name="ownerId">Row id of the company.</param>
    public static string CompanyAllItemCategoriesByName(long ownerId) =>
        Format(Company, AllItemCategoriesByName, ownerId);

    /// <summary>
    /// Lock name for a company's item collection, also taken for item-code allocation: <c>Company.allItems:&lt;ownerId&gt;</c>.
    /// </summary>
    /// <param name="ownerId">Row id of the company.</param>
    public static string CompanyAllItems(long ownerId) => Format(Company, AllItems, ownerId);

    /// <summary>Lock name for a company's sales keyed by item: <c>Company.allSalesByItem:&lt;ownerId&gt;</c>.</summary>
    /// <param name="ownerId">Row id of the company.</param>
    public static string CompanyAllSalesByItem(long ownerId) => Format(Company, AllSalesByItem, ownerId);

    /// <summary>
    /// Lock name for a company's tender items keyed by sale and closure date: <c>Company.allTenderItemsBySaleDate:&lt;ownerId&gt;</c>.
    /// </summary>
    /// <param name="ownerId">Row id of the company.</param>
    public static string CompanyAllTenderItemsBySaleDate(long ownerId) =>
        Format(Company, AllTenderItemsBySaleDate, ownerId);

    /// <summary>
    /// Lock name for an item category's commission rates: <c>ItemCategory.allCommissionRates:&lt;ownerId&gt;</c>.
    /// </summary>
    /// <param name="ownerId">Row id of the item category.</param>
    public static string ItemCategoryAllCommissionRates(long ownerId) =>
        Format(ItemCategory, AllCommissionRates, ownerId);

    /// <summary>Lock name for an item category's items: <c>ItemCategory.allItems:&lt;ownerId&gt;</c>.</summary>
    /// <param name="ownerId">Row id of the item category.</param>
    public static string ItemCategoryAllItems(long ownerId) => Format(ItemCategory, AllItems, ownerId);

    /// <summary>Lock name for an agent's items: <c>Agent.allItems:&lt;ownerId&gt;</c>.</summary>
    /// <param name="ownerId">Row id of the agent.</param>
    public static string AgentAllItems(long ownerId) => Format(Agent, AllItems, ownerId);

    /// <summary>Lock name for an agent's sold items: <c>Agent.allSoldItems:&lt;ownerId&gt;</c>.</summary>
    /// <param name="ownerId">Row id of the agent.</param>
    public static string AgentAllSoldItems(long ownerId) => Format(Agent, AllSoldItems, ownerId);

    /// <summary>Lock name for a region's items: <c>Region.allItems:&lt;ownerId&gt;</c>.</summary>
    /// <param name="ownerId">Row id of the region.</param>
    public static string RegionAllItems(long ownerId) => Format(Region, AllItems, ownerId);

    /// <summary>Lock name for a country's regions: <c>Country.allRegions:&lt;ownerId&gt;</c>.</summary>
    /// <param name="ownerId">Row id of the country.</param>
    public static string CountryAllRegions(long ownerId) => Format(Country, AllRegions, ownerId);

    /// <summary>Lock name for a client's retail sales: <c>Client.allRetailSales:&lt;ownerId&gt;</c>.</summary>
    /// <param name="ownerId">Row id of the client.</param>
    public static string ClientAllRetailSales(long ownerId) => Format(Client, AllRetailSales, ownerId);

    /// <summary>Lock name for a client's tender sales: <c>Client.allTenderSales:&lt;ownerId&gt;</c>.</summary>
    /// <param name="ownerId">Row id of the client.</param>
    public static string ClientAllTenderSales(long ownerId) => Format(Client, AllTenderSales, ownerId);

    /// <summary>Lock name for a client's tenders: <c>Client.allTenders:&lt;ownerId&gt;</c>.</summary>
    /// <param name="ownerId">Row id of the client.</param>
    public static string ClientAllTenders(long ownerId) => Format(Client, AllTenders, ownerId);

    /// <summary>
    /// Lock name for a tender item's tenders ordered by offer then time: <c>TenderItem.allTendersByOfferTime:&lt;ownerId&gt;</c>.
    /// </summary>
    /// <param name="ownerId">Row id of the tender item.</param>
    public static string TenderItemAllTendersByOfferTime(long ownerId) =>
        Format(TenderItem, AllTendersByOfferTime, ownerId);

    /// <summary>
    /// Lock name for a tender item's tenders ordered by time then offer: <c>TenderItem.allTendersByTimeOffer:&lt;ownerId&gt;</c>.
    /// </summary>
    /// <param name="ownerId">Row id of the tender item.</param>
    public static string TenderItemAllTendersByTimeOffer(long ownerId) =>
        Format(TenderItem, AllTendersByTimeOffer, ownerId);
}

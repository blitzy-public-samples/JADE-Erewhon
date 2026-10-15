namespace Erewhon.Persistence.Seed;

/// <summary>
/// File names of the seed data files read from the seed data directory.
/// </summary>
public static class SeedFileNames
{
    /// <summary>File name of the agents seed data file.</summary>
    public const string AgentDataFile = "Agents.txt";

    /// <summary>File name of the agent commission rates seed data file.</summary>
    public const string AgentRatesDataFile = "AgentRates.txt";

    /// <summary>File name of the item categories seed data file.</summary>
    public const string CategoriesDataFile = "Categories.txt";

    /// <summary>File name of the category commission rates seed data file.</summary>
    public const string CategoryRatesDataFile = "CategoryRates.txt";

    /// <summary>File name of the clients seed data file.</summary>
    public const string ClientsDataFile = "Clients.txt";

    /// <summary>File name of the countries and regions seed data file.</summary>
    public const string LocationsDataFile = "Locations.txt";

    /// <summary>File name of the retail items seed data file.</summary>
    public const string RetailItemsDataFile = "RetailItems.txt";

    /// <summary>File name of the retail sales seed data file.</summary>
    public const string RetailSalesDataFile = "RetailSales.txt";

    /// <summary>File name of the tender items seed data file.</summary>
    public const string TenderItemsDataFile = "TenderItems.txt";

    /// <summary>File name of the tenders seed data file.</summary>
    public const string TendersDataFile = "Tenders.txt";

    /// <summary>
    /// The seed data file names in the order in which the loader reads them.
    /// </summary>
    public static IReadOnlyList<string> LoadOrder { get; } = Array.AsReadOnly(new[]
    {
        LocationsDataFile,
        AgentDataFile,
        ClientsDataFile,
        CategoriesDataFile,
        RetailItemsDataFile,
        TenderItemsDataFile,
        CategoryRatesDataFile,
        AgentRatesDataFile,
        TendersDataFile,
        RetailSalesDataFile,
    });
}

using System.Diagnostics.CodeAnalysis;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.FixtureHost;

/// <summary>
/// The named fixtures the bridge builds. Each fixture creates one company with
/// <see cref="TestCompanyBuilder.CreateCompanyAsync"/>, binds it with <see cref="TestCompanyBuilder.Bind"/>, then
/// creates its objects in a fixed order through the builder, setting the builder's <c>My*</c> property of each object
/// as it is created.
/// </summary>
public static class FixtureCatalog
{
    /// <summary>The fixture that ports <c>TestViewApp.setUp</c>.</summary>
    public const string TestViewApp = "TestViewApp";

    /// <summary>The fixture that ports <c>TestViewSale.setUp</c>.</summary>
    public const string TestViewSale = "TestViewSale";

    /// <summary>
    /// The fixture that ports <c>TestViewSaleItem.setUp</c>, as inherited by <c>TestViewRetailSaleItem</c> and
    /// <c>TestViewTenderSaleItem</c>.
    /// </summary>
    public const string TestViewSaleItem = "TestViewSaleItem";

    /// <summary>The fixture of the admin form-guard tests, composed from the <c>JadeTestCase</c> helpers.</summary>
    public const string AdminGuards = "AdminGuards";

    private static readonly FixtureDefinition[] Definitions =
    [
        new(TestViewApp, SessionRole.Client, BuildTestViewAppAsync),
        new(TestViewSale, SessionRole.Administrator, BuildTestViewSaleAsync),
        new(TestViewSaleItem, SessionRole.Client, BuildTestViewSaleItemAsync),
        new(AdminGuards, SessionRole.Agent, BuildAdminGuardsAsync),
    ];

    /// <summary>
    /// The fixture names: <see cref="TestViewApp"/>, <see cref="TestViewSale"/>, <see cref="TestViewSaleItem"/> and
    /// <see cref="AdminGuards"/>, in that order.
    /// </summary>
    public static IReadOnlyList<string> Names { get; } =
        Array.AsReadOnly(Array.ConvertAll(Definitions, definition => definition.Name));

    /// <summary>Returns the fixture whose name equals <paramref name="name"/>, compared ordinally.</summary>
    /// <param name="name">The fixture name.</param>
    /// <param name="definition">The fixture, or <see langword="null"/> when no fixture has that name.</param>
    /// <returns><see langword="true"/> when a fixture has that name; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public static bool TryGet(string name, [NotNullWhen(true)] out FixtureDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (FixtureDefinition candidate in Definitions)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                definition = candidate;
                return true;
            }
        }

        definition = null;
        return false;
    }

    /// <summary>
    /// Creates, in this order: the company and its binding; the agent "John Smith"; the client "Clive Entworth"; the
    /// country "France" and its region "Paris"; the item category "Category 1" ("CAT1", "Item Category 1"); a retail
    /// item and a tender item of that category, region and agent. Ports <c>TestViewApp.setUp</c>.
    /// </summary>
    private static async Task<FixtureBuildResult> BuildTestViewAppAsync(
        TestCompanyBuilder builder,
        IServiceProvider services,
        CancellationToken ct)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        Company company = await CreateAndBindCompanyAsync(builder, ids, ct);

        Agent agent = Add(ids, Keys.Agent, await builder.CreateAgentAsync(ct));
        builder.MyAgent = agent;

        builder.MyClient = Add(ids, Keys.Client, await builder.CreateClientAsync(ct));

        Region region = await CreateCountryAndRegionAsync(builder, ids, ct);

        ItemCategory itemCategory = Add(
            ids, Keys.ItemCategory, await builder.CreateItemCategoryAsync("Category 1", "CAT1", "Item Category 1", ct));
        builder.MyItemCategory = itemCategory;

        builder.MyRetailItem = Add(
            ids, Keys.RetailItem, await builder.CreateRetailItemAsync(region, itemCategory, agent, ct));
        builder.MyTenderItem = Add(
            ids, Keys.TenderItem, await builder.CreateTenderItemAsync(region, itemCategory, agent, ct));

        return Result(builder, company, ids);
    }

    /// <summary>
    /// Creates, in this order: the company and its binding; the client "Clive Entworth"; the agent "John Smith"; the
    /// country "France" and its region "Paris"; the item category "Category Item" ("CATI", "Some Category
    /// Description"); a tender item; a tender of 500 by the client on it; a retail item; a retail sale of the retail
    /// item to the client; a tender sale of the tender item to the client through the tender. Ports
    /// <c>TestViewSale.setUp</c>.
    /// </summary>
    private static async Task<FixtureBuildResult> BuildTestViewSaleAsync(
        TestCompanyBuilder builder,
        IServiceProvider services,
        CancellationToken ct)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        Company company = await CreateAndBindCompanyAsync(builder, ids, ct);

        (Client client, TenderItem tenderItem, Tender tender, RetailItem retailItem) =
            await CreateSaleItemObjectsAsync(builder, ids, ct);

        builder.MyRetailSale = Add(ids, Keys.RetailSale, await builder.CreateRetailSaleAsync(retailItem, client, ct));
        builder.MyTenderSale = Add(
            ids, Keys.TenderSale, await builder.CreateTenderSaleAsync(tenderItem, tender, client, ct));

        return Result(builder, company, ids);
    }

    /// <summary>
    /// Creates, in this order: the company and its binding; the client "Clive Entworth"; the agent "John Smith"; the
    /// country "France" and its region "Paris"; the item category "Category Item" ("CATI", "Some Category
    /// Description"); a tender item; a tender of 500 by the client on it; a retail item. Ports
    /// <c>TestViewSaleItem.setUp</c>.
    /// </summary>
    private static async Task<FixtureBuildResult> BuildTestViewSaleItemAsync(
        TestCompanyBuilder builder,
        IServiceProvider services,
        CancellationToken ct)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        Company company = await CreateAndBindCompanyAsync(builder, ids, ct);

        await CreateSaleItemObjectsAsync(builder, ids, ct);

        return Result(builder, company, ids);
    }

    /// <summary>
    /// Creates, in this order: the company and its binding; the agent "John Smith"; the agent "Jane Doe" at 123
    /// Another Pl, Auckland, Aotearoa (phone "555 7654321", fax "FAX2", e-mail "support@Erewhon.nz", web site
    /// "https://www.jadeworld.com/contact-us/"); the client "Clive Entworth"; the country "France" and its region
    /// "Paris"; the item category "Abstractions" ("ABST", "Items that do not model real world observables"); a
    /// commission rate of 25.00 on it held by "John Smith"; two retail items of "John Smith" and two of "Jane Doe";
    /// three tender items of "John Smith"; tenders by the client of 500.00 on the first tender item, 510.00 on the
    /// second, and 550.00 and 600.00 on the third; then moves the second tender item's closure date to the day before
    /// the server date through <see cref="ItemService"/>; then retail sales to the client of the second retail item of
    /// each agent, and a tender sale of the third tender item to the client through the 600.00 tender.
    /// </summary>
    private static async Task<FixtureBuildResult> BuildAdminGuardsAsync(
        TestCompanyBuilder builder,
        IServiceProvider services,
        CancellationToken ct)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        Company company = await CreateAndBindCompanyAsync(builder, ids, ct);

        Agent agent = Add(ids, Keys.Agent, await builder.CreateAgentAsync(ct));
        builder.MyAgent = agent;

        Address otherAgentAddress = builder.CreateTestAddress(
            "123 Another Pl",
            "Auckland",
            "Aotearoa",
            "555 7654321",
            "FAX2",
            "support@Erewhon.nz",
            "https://www.jadeworld.com/contact-us/");
        Agent otherAgent = Add(
            ids, Keys.OtherAgent, await builder.CreateTestAgentAsync("Jane Doe", otherAgentAddress, ct));

        Client client = Add(ids, Keys.Client, await builder.CreateClientAsync(ct));
        builder.MyClient = client;

        Region region = await CreateCountryAndRegionAsync(builder, ids, ct);

        ItemCategory itemCategory = Add(
            ids,
            Keys.ItemCategory,
            await builder.CreateItemCategoryAsync(
                "Abstractions", "ABST", "Items that do not model real world observables", ct));
        builder.MyItemCategory = itemCategory;

        builder.MyCommissionRate = Add(
            ids, Keys.CommissionRate, await builder.CreateCommissionRateAsync(itemCategory, 25.00m, agent, ct));

        builder.MyRetailItem = Add(
            ids, Keys.RetailItem, await builder.CreateRetailItemAsync(region, itemCategory, agent, ct));
        RetailItem soldRetailItem = Add(
            ids, Keys.SoldRetailItem, await builder.CreateRetailItemAsync(region, itemCategory, agent, ct));
        Add(ids, Keys.OtherRetailItem, await builder.CreateRetailItemAsync(region, itemCategory, otherAgent, ct));
        RetailItem otherSoldRetailItem = Add(
            ids, Keys.OtherSoldRetailItem, await builder.CreateRetailItemAsync(region, itemCategory, otherAgent, ct));

        TenderItem tenderItem = Add(
            ids, Keys.TenderItem, await builder.CreateTenderItemAsync(region, itemCategory, agent, ct));
        builder.MyTenderItem = tenderItem;
        TenderItem closedTenderItem = Add(
            ids, Keys.ClosedTenderItem, await builder.CreateTenderItemAsync(region, itemCategory, agent, ct));
        TenderItem soldTenderItem = Add(
            ids, Keys.SoldTenderItem, await builder.CreateTenderItemAsync(region, itemCategory, agent, ct));

        Add(ids, Keys.OpenTender, await builder.CreateTenderAsync(client, tenderItem, 500.00m, ct));
        Add(ids, Keys.ClosedTender, await builder.CreateTenderAsync(client, closedTenderItem, 510.00m, ct));
        Add(ids, Keys.OtherTender, await builder.CreateTenderAsync(client, soldTenderItem, 550.00m, ct));
        Tender tender = Add(ids, Keys.Tender, await builder.CreateTenderAsync(client, soldTenderItem, 600.00m, ct));
        builder.MyTender = tender;

        await CloseTenderItemAsync(services, closedTenderItem, ct);

        builder.MyRetailSale = Add(
            ids, Keys.RetailSale, await builder.CreateRetailSaleAsync(soldRetailItem, client, ct));
        Add(ids, Keys.OtherRetailSale, await builder.CreateRetailSaleAsync(otherSoldRetailItem, client, ct));
        builder.MyTenderSale = Add(
            ids, Keys.TenderSale, await builder.CreateTenderSaleAsync(soldTenderItem, tender, client, ct));

        return Result(builder, company, ids);
    }

    /// <summary>
    /// Creates the company "Jade Software Corporation", sets <see cref="TestCompanyBuilder.MyCompany"/>, records its
    /// id under <see cref="Keys.Company"/> and binds it. Ports <c>createCompany</c> and <c>app.initializeUnitTest</c>.
    /// </summary>
    private static async Task<Company> CreateAndBindCompanyAsync(
        TestCompanyBuilder builder,
        Dictionary<string, long> ids,
        CancellationToken ct)
    {
        Company company = await builder.CreateCompanyAsync(ct);
        builder.MyCompany = company;
        ids.Add(Keys.Company, company.Id);
        builder.Bind(company.Id);
        return company;
    }

    /// <summary>
    /// Creates the country "France" and its region "Paris", sets <see cref="TestCompanyBuilder.MyCountry"/> and
    /// <see cref="TestCompanyBuilder.MyRegion"/>, records their ids and returns the region. Ports
    /// <c>createRegion(createCountry())</c>.
    /// </summary>
    private static async Task<Region> CreateCountryAndRegionAsync(
        TestCompanyBuilder builder,
        Dictionary<string, long> ids,
        CancellationToken ct)
    {
        Country country = Add(ids, Keys.Country, await builder.CreateCountryAsync(ct));
        builder.MyCountry = country;

        Region region = Add(ids, Keys.Region, await builder.CreateRegionAsync(country, ct));
        builder.MyRegion = region;
        return region;
    }

    /// <summary>
    /// Creates, in this order: the client "Clive Entworth"; the agent "John Smith"; the country "France" and its region
    /// "Paris"; the item category "Category Item" ("CATI", "Some Category Description"); a tender item; a tender of
    /// 500 by the client on it; a retail item. Sets the builder's <c>My*</c> property of each and records each id.
    /// Ports the statements <c>TestViewSale.setUp</c> and <c>TestViewSaleItem.setUp</c> share.
    /// </summary>
    private static async Task<(Client Client, TenderItem TenderItem, Tender Tender, RetailItem RetailItem)>
        CreateSaleItemObjectsAsync(TestCompanyBuilder builder, Dictionary<string, long> ids, CancellationToken ct)
    {
        Client client = Add(ids, Keys.Client, await builder.CreateClientAsync(ct));
        builder.MyClient = client;

        Agent agent = Add(ids, Keys.Agent, await builder.CreateAgentAsync(ct));
        builder.MyAgent = agent;

        Region region = await CreateCountryAndRegionAsync(builder, ids, ct);

        ItemCategory itemCategory = Add(
            ids,
            Keys.ItemCategory,
            await builder.CreateItemCategoryAsync("Category Item", "CATI", "Some Category Description", ct));
        builder.MyItemCategory = itemCategory;

        TenderItem tenderItem = Add(
            ids, Keys.TenderItem, await builder.CreateTenderItemAsync(region, itemCategory, agent, ct));
        builder.MyTenderItem = tenderItem;

        Tender tender = Add(ids, Keys.Tender, await builder.CreateTenderAsync(client, tenderItem, 500m, ct));
        builder.MyTender = tender;

        RetailItem retailItem = Add(
            ids, Keys.RetailItem, await builder.CreateRetailItemAsync(region, itemCategory, agent, ct));
        builder.MyRetailItem = retailItem;

        return (client, tenderItem, tender, retailItem);
    }

    /// <summary>
    /// Updates <paramref name="tenderItem"/> through <see cref="ItemService"/> with its existing values and the closure
    /// date one day before the server date (<see cref="TransactionType.Persist"/>, <see cref="TransactionMode.Own"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The update failed; the message is the operation's <see cref="OperationResult.FullErrorText"/>.
    /// </exception>
    private static async Task CloseTenderItemAsync(
        IServiceProvider services,
        TenderItem tenderItem,
        CancellationToken ct)
    {
        DateOnly serverDate = await services.GetRequiredService<IServerClock>().GetServerDateAsync(ct);

        var command = new TenderItemCommand();
        command.PopulateFromObject(tenderItem);
        command.ClosureDate = serverDate.AddDays(-1);

        OperationResult result = await services.GetRequiredService<ItemService>()
            .ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own, ct);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.FullErrorText);
        }
    }

    /// <summary>
    /// Records the id of <paramref name="entity"/> under <paramref name="key"/> and returns the entity.
    /// </summary>
    private static T Add<T>(Dictionary<string, long> ids, string key, T entity)
        where T : PersistentModel
    {
        ids.Add(key, entity.Id);
        return entity;
    }

    /// <summary>
    /// Returns the build result: the company's id and name, the recorded ids, and the id and name of the builder's
    /// <see cref="TestCompanyBuilder.MyAgent"/> and <see cref="TestCompanyBuilder.MyClient"/>.
    /// </summary>
    private static FixtureBuildResult Result(
        TestCompanyBuilder builder,
        Company company,
        Dictionary<string, long> ids) =>
        new(
            company.Id,
            company.Name,
            ids.AsReadOnly(),
            builder.MyAgent?.Id,
            builder.MyAgent?.Name,
            builder.MyClient?.Id,
            builder.MyClient?.Name);

    /// <summary>The keys of <see cref="FixtureBuildResult.Ids"/>, one per fixture object.</summary>
    public static class Keys
    {
        /// <summary>The company.</summary>
        public const string Company = "company";

        /// <summary>The agent "John Smith".</summary>
        public const string Agent = "agent";

        /// <summary>The agent "Jane Doe" of <see cref="AdminGuards"/>.</summary>
        public const string OtherAgent = "otherAgent";

        /// <summary>The client "Clive Entworth".</summary>
        public const string Client = "client";

        /// <summary>The country "France".</summary>
        public const string Country = "country";

        /// <summary>The region "Paris".</summary>
        public const string Region = "region";

        /// <summary>The item category.</summary>
        public const string ItemCategory = "itemCategory";

        /// <summary>The commission rate of <see cref="AdminGuards"/>.</summary>
        public const string CommissionRate = "commissionRate";

        /// <summary>
        /// The retail item of "John Smith": sold through <see cref="RetailSale"/> in <see cref="TestViewSale"/>, unsold
        /// in the other fixtures.
        /// </summary>
        public const string RetailItem = "retailItem";

        /// <summary>The sold retail item of "John Smith" in <see cref="AdminGuards"/>.</summary>
        public const string SoldRetailItem = "soldRetailItem";

        /// <summary>The unsold retail item of "Jane Doe" in <see cref="AdminGuards"/>.</summary>
        public const string OtherRetailItem = "otherRetailItem";

        /// <summary>The sold retail item of "Jane Doe" in <see cref="AdminGuards"/>.</summary>
        public const string OtherSoldRetailItem = "otherSoldRetailItem";

        /// <summary>The tender item that closes on the server date.</summary>
        public const string TenderItem = "tenderItem";

        /// <summary>The tender item of <see cref="AdminGuards"/> that closed the day before the server date.</summary>
        public const string ClosedTenderItem = "closedTenderItem";

        /// <summary>The tender item of <see cref="AdminGuards"/> sold through <see cref="Tender"/>.</summary>
        public const string SoldTenderItem = "soldTenderItem";

        /// <summary>
        /// The tender of 500 on <see cref="TenderItem"/>, or in <see cref="AdminGuards"/> the winning tender of 600.00
        /// on <see cref="SoldTenderItem"/>.
        /// </summary>
        public const string Tender = "tender";

        /// <summary>The tender of 500.00 on <see cref="TenderItem"/> in <see cref="AdminGuards"/>.</summary>
        public const string OpenTender = "openTender";

        /// <summary>The tender of 510.00 on <see cref="ClosedTenderItem"/>.</summary>
        public const string ClosedTender = "closedTender";

        /// <summary>The tender of 550.00 on <see cref="SoldTenderItem"/>.</summary>
        public const string OtherTender = "otherTender";

        /// <summary>
        /// The retail sale to the client of <see cref="RetailItem"/>, or in <see cref="AdminGuards"/> of
        /// <see cref="SoldRetailItem"/>.
        /// </summary>
        public const string RetailSale = "retailSale";

        /// <summary>The retail sale to the client of <see cref="OtherSoldRetailItem"/>.</summary>
        public const string OtherRetailSale = "otherRetailSale";

        /// <summary>The tender sale to the client through <see cref="Tender"/>.</summary>
        public const string TenderSale = "tenderSale";
    }
}

/// <summary>A named fixture: its name, the role of the session opened on it by default, and its build.</summary>
/// <param name="Name">The fixture name, as <see cref="FixtureCatalog.Names"/> lists it.</param>
/// <param name="DefaultSession">The role of the session opened on the fixture when the request names none.</param>
/// <param name="BuildAsync">
/// Builds the fixture with the given builder and the services of the builder's scope, and returns its ids.
/// </param>
public sealed record FixtureDefinition(
    string Name,
    SessionRole DefaultSession,
    Func<TestCompanyBuilder, IServiceProvider, CancellationToken, Task<FixtureBuildResult>> BuildAsync);

/// <summary>The rows one fixture build created.</summary>
/// <param name="CompanyId">The internal id of the fixture company.</param>
/// <param name="CompanyName">The name of the fixture company.</param>
/// <param name="Ids">
/// The internal id of each created row, keyed by a <see cref="FixtureCatalog.Keys"/> constant.
/// </param>
/// <param name="AgentId">The internal id of the builder's <see cref="TestCompanyBuilder.MyAgent"/>, if set.</param>
/// <param name="AgentName">The name of the builder's <see cref="TestCompanyBuilder.MyAgent"/>, if set.</param>
/// <param name="ClientId">The internal id of the builder's <see cref="TestCompanyBuilder.MyClient"/>, if set.</param>
/// <param name="ClientName">The name of the builder's <see cref="TestCompanyBuilder.MyClient"/>, if set.</param>
public sealed record FixtureBuildResult(
    long CompanyId,
    string CompanyName,
    IReadOnlyDictionary<string, long> Ids,
    long? AgentId,
    string? AgentName,
    long? ClientId,
    string? ClientName);

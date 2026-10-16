using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Application.Tests.Model;

/// <summary>
/// Ports the JADE <c>TestAgent</c> unit tests: the commission rates an agent holds, its self-documentation, its sold
/// items, its debug and name strings, its initial properties and the update of the agent with its address. Each test
/// starts with the company "Jade Software Corporation", bound, and its agent "John Smith" at 314 Nohwere Lane.
/// </summary>
public sealed class TestAgent : ErewhonTestBase
{
    private const int UpdateOutsideTransaction = 1048;

    private Company MyCompany =>
        Builder.MyCompany ?? throw new InvalidOperationException("The fixture company has not been created.");

    private Agent MyAgent =>
        Builder.MyAgent ?? throw new InvalidOperationException("The fixture agent has not been created.");

    /// <summary>
    /// Creates and binds the company, then creates the agent "John Smith". Ports <c>TestAgent.setUp</c>.
    /// </summary>
    /// <returns>A task that completes when the company and the agent exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();
        Builder.MyAgent = await Builder.CreateAgentAsync();
    }

    /// <summary>
    /// A rate of 25 on "Abstractions" created for the agent leaves it with that one rate, and a rate of 15 on "Art"
    /// then created for it leaves it with 2 rates, the last, in category name order, at 15.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testAddCommissionRate()
    {
        // Arrange
        ItemCategory itemCategory1 = await Builder.CreateItemCategoryAsync(
            "Abstractions", "ABST", "Items that do not model real world observables");
        ItemCategory itemCategory2 = await Builder.CreateItemCategoryAsync("Art", "ARTS", "Pictures and Paintings");
        await Builder.CreateCommissionRateAsync(itemCategory1, 25m, MyAgent);

        // Assert
        CommissionRate initialRate = Assert.Single(CommissionRatesByCategoryName(await ReadAgentAsync()));
        Assert.Equal(itemCategory1.Id, initialRate.ItemCategoryId);
        Assert.Equal(25m, initialRate.Percentage);

        // Act
        const decimal percentage = 15m;
        var commissionRateCommand = new CommissionRateCommand
        {
            ItemCategoryId = itemCategory2.Id,
            Percentage = percentage,
            AgentIds = [MyAgent.Id],
        };
        OperationResult result = await GetService<CommissionRateService>()
            .ExecuteAsync(commissionRateCommand, TransactionType.Persist, TransactionMode.Own);

        // Assert
        Assert.True(result.Succeeded, result.FullErrorText);
        IReadOnlyList<CommissionRate> agentRates = CommissionRatesByCategoryName(await ReadAgentAsync());
        Assert.Equal(2, agentRates.Count);
        Assert.Equal(percentage, agentRates[^1].Percentage);
    }

    /// <summary>
    /// Creating a rate for the agent outside a transaction raises error 1048 and leaves neither the category nor the
    /// agent with a rate.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testAddCommissionRateInTransStateException()
    {
        // Arrange
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync(
            "Widgets", "WIDG", "Most delightful trinkets and doodads");
        var commissionRateCommand = new CommissionRateCommand
        {
            ItemCategoryId = itemCategory.Id,
            Percentage = 25m,
            AgentIds = [MyAgent.Id],
        };

        // Act
        NotInTransactionStateException exception = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => GetService<CommissionRateService>()
                .ExecuteAsync(commissionRateCommand, TransactionType.Persist, TransactionMode.Ambient));

        // Assert
        Assert.Equal(UpdateOutsideTransaction, exception.ErrorCode);
        long itemCategoryId = itemCategory.Id;
        bool categoryHasRate = await ReadAsync(services => services.GetRequiredService<IErewhonDbContext>()
            .CommissionRates
            .AsNoTracking()
            .AnyAsync(rate => rate.ItemCategoryId == itemCategoryId));
        Assert.False(categoryHasRate);
        Assert.Empty((await ReadAgentAsync()).AgentCommissionRates);
    }

    /// <summary>
    /// Rates of 25 and then 15 on one category, each created for the agent, leave the category with 2 rates and the
    /// agent's last rate the second.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testAddCommissionRateWithExistingRate()
    {
        // Arrange
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync(
            "Widgets", "WDGT", "Most delightful trinkets and doodads");

        // Act
        await Builder.CreateCommissionRateAsync(itemCategory, 25m, MyAgent);
        CommissionRate commissionRate2 = await Builder.CreateCommissionRateAsync(itemCategory, 15m, MyAgent);

        // Assert
        string categoryName = itemCategory.Name;
        int categoryRateCount = await ReadAsync(async services =>
        {
            ItemCategory? category = await services.GetRequiredService<ItemCategoryService>()
                .FindByNameAsync(categoryName);
            Assert.NotNull(category);
            long categoryId = category.Id;
            return await services.GetRequiredService<IErewhonDbContext>()
                .CommissionRates
                .AsNoTracking()
                .CountAsync(rate => rate.ItemCategoryId == categoryId);
        });
        Assert.Equal(2, categoryRateCount);
        Assert.Equal(commissionRate2.Id, CommissionRatesByCategoryName(await ReadAgentAsync())[^1].Id);
    }

    /// <summary>
    /// A second rate of 25.00 on a category that already holds 25.00 for the agent is not persisted and reports only
    /// <c>$ItemCategoryHasCommissionRate</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testAddDuplicateCommissionRate()
    {
        // Arrange
        const decimal percentage = 25.00m;
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync(
            "Widgets", "WDGT", "Most delightful trinkets and doodads");
        await Builder.CreateCommissionRateAsync(itemCategory, percentage, MyAgent);

        // Act
        var commissionRateCommand = new CommissionRateCommand
        {
            ItemCategoryId = itemCategory.Id,
            Percentage = percentage,
            AgentIds = [MyAgent.Id],
        };
        OperationResult result = await GetService<CommissionRateService>()
            .ExecuteAsync(commissionRateCommand, TransactionType.Persist, TransactionMode.Own);

        // Assert
        Assert.False(result.Succeeded);
        string error = Assert.Single(result.Errors);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemCategoryHasCommissionRate), error);
    }

    /// <summary>
    /// A rate with percentage 0 reports exactly one error, <c>$CommissionRatePercentageInvalid</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testAddNullCommissionRate()
    {
        // Arrange
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync(
            "Test Category", "TEST", "Test Category Description");

        // Act
        var commissionRateCommand = new CommissionRateCommand
        {
            ItemCategoryId = itemCategory.Id,
            Percentage = 0m,
            AgentIds = [MyAgent.Id],
        };
        OperationResult result = await GetService<CommissionRateService>()
            .ExecuteAsync(commissionRateCommand, TransactionType.Persist, TransactionMode.Own);

        // Assert
        string error = Assert.Single(result.Errors);
        Assert.Equal(MessageCatalog.Get(MessageKeys.CommissionRatePercentageInvalid), error);
    }

    /// <summary>
    /// Rates of 25.00, 15.00 and 55.00 on "Widgets", each created for the agent, leave the third as the agent's rate
    /// for "Widgets".
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCommissionRateForCategory()
    {
        // Arrange
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync(
            "Widgets", "WDGT", "Most delightful trinkets and doodads");
        await Builder.CreateCommissionRateAsync(itemCategory, 25.00m, MyAgent);
        await Builder.CreateCommissionRateAsync(itemCategory, 15.00m, MyAgent);
        CommissionRate commissionRate3 = await Builder.CreateCommissionRateAsync(itemCategory, 55.00m, MyAgent);

        // Act
        CommissionRate? foundRate = (await ReadAgentAsync()).GetCommissionRateForCategory("Widgets");

        // Assert
        Assert.NotNull(foundRate);
        Assert.Equal(commissionRate3.Id, foundRate.Id);
    }

    /// <summary>
    /// The agent's RTF document has "John Smith" as its heading and <c>&lt;oid&gt; Agent</c> as its body.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testDocumentSelfRTF()
    {
        // Arrange
        string rtf = @"{\rtf1\ansi\ansicpg1252\deff0\deflang5129"
            + @"{\fonttbl{\f0\fnil\fprq15\fcharset0 Arial;}"
            + @"{\f1\fnil\fprq8\fcharset0 Arial;}"
            + @"{\f2\fnil\fcharset0 MS Sans Serif;}}"
            + @"{\colortbl ;\red0\green0\blue128;\red0\green0\blue0;}"
            + @"\viewkind4\uc1\pard\cf1\b\f0\fs23 John Smith\par\par\cf2\b0\f1\fs18 "
            + ObjectIdentity.Of(MyAgent)
            + @" Agent\f2\fs17\par}";

        // Act
        DocumentationDetails? details = await GetService<SelfDocumentationService>()
            .GetDetailsAsync("Agent", MyAgent.Id);

        // Assert
        Assert.NotNull(details);
        Assert.Equal(rtf, details.Rtf);
    }

    /// <summary>
    /// After a retail sale and a tender sale of the agent's items, the agent's sold items are the retail item first and
    /// the tender item last.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetAllSales()
    {
        // Arrange
        Client client = await Builder.CreateClientAsync();
        Country country = await Builder.CreateCountryAsync();
        Region region = await Builder.CreateRegionAsync(country);
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync(
            "Test Name", "TSTN", "Test Category Description");

        RetailItem retailItem = await Builder.CreateRetailItemAsync(region, itemCategory, MyAgent);
        await Builder.CreateRetailSaleAsync(retailItem, client);

        TenderItem tenderItem = await Builder.CreateTenderItemAsync(region, itemCategory, MyAgent);
        Tender tender = await Builder.CreateTenderAsync(client, tenderItem, 500m);
        await Builder.CreateTenderSaleAsync(tenderItem, tender, client);

        // Act
        IReadOnlyList<Item> agentSales = (await ReadAgentAsync()).GetSoldItems();

        // Assert
        Assert.Equal(2, agentSales.Count);

        RetailItem firstSale = Assert.IsAssignableFrom<RetailItem>(agentSales[0]);
        Assert.Equal(retailItem.Id, firstSale.Id);

        TenderItem lastSale = Assert.IsAssignableFrom<TenderItem>(agentSales[^1]);
        Assert.Equal(tenderItem.Id, lastSale.Id);
    }

    /// <summary>A new agent has no sold items.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetAllSalesEmptyAgent()
    {
        // Act
        Agent agent = await ReadAgentAsync();

        // Assert
        Assert.Empty(agent.GetSoldItems());
    }

    /// <summary>
    /// Rates of 25.00 and then 15.00 on "Widgets", each created for the agent, leave the second as the agent's rate for
    /// "Widgets".
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetCommissionRateForCategory()
    {
        // Arrange
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync(
            "Widgets", "WDGT", "Most delightful trinkets and doodads");
        await Builder.CreateCommissionRateAsync(itemCategory, 25.00m, MyAgent);
        CommissionRate commissionRate2 = await Builder.CreateCommissionRateAsync(itemCategory, 15.00m, MyAgent);

        // Act
        CommissionRate? foundCommissionRate = (await ReadAgentAsync()).GetCommissionRateForCategory("Widgets");

        // Assert
        Assert.NotNull(foundCommissionRate);
        Assert.Equal(commissionRate2.Id, foundCommissionRate.Id);
    }

    /// <summary>The agent's debug string is its OID text, a space and <c>$Agent</c>.</summary>
    [Fact]
    public void testGetDebugString()
    {
        // Arrange
        string oid = ObjectIdentity.Of(MyAgent);

        // Act
        string debugString = MyAgent.GetDebugString();

        // Assert
        Assert.Equal(oid + " " + MessageCatalog.Get(MessageKeys.Agent), debugString);
    }

    /// <summary>The agent's name string is "John Smith".</summary>
    [Fact]
    public void testGetNameString()
    {
        // Assert
        Assert.Equal("John Smith", MyAgent.GetNameString());
    }

    /// <summary>
    /// The stored agent has the fixture name and address, its rate, item and sold-item collections, and its company.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInitialProperties()
    {
        // Act
        Agent agent = await ReadAgentAsync();

        // Assert
        Assert.Equal("John Smith", agent.Name);
        AssertAddress(
            agent.Address,
            "314 Nohwere Lane",
            "Christchurch",
            "New Zealand",
            "admin@Erewhon.nz",
            "FAX",
            "555 1234567",
            "www.jadeworld.com");

        AssertCollectionsAndCompany(agent);
    }

    /// <summary>The agent's rate for a category named <see langword="null"/> is <see langword="null"/>.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testNullCommissionRateForCategory()
    {
        // Arrange
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync(
            "Widgets", "WDGT", "Most delightful trinkets and doodads");
        await Builder.CreateCommissionRateAsync(itemCategory, 25.00m, MyAgent);
        await Builder.CreateCommissionRateAsync(itemCategory, 15.00m, MyAgent);

        // Act
        CommissionRate? foundCommissionRate = (await ReadAgentAsync()).GetCommissionRateForCategory(null!);

        // Assert
        Assert.Null(foundCommissionRate);
    }

    /// <summary>
    /// Updating the agent to "Jane Doe" at 123 Another Pl, Auckland, persists the name and the seven address fields
    /// and keeps the agent's collections and company.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdateAgentAndSubordinate()
    {
        // Arrange
        var agentCommand = new AgentCommand();
        agentCommand.PopulateFromObject(MyAgent);
        SetUpdatedValues(agentCommand);

        // Act
        OperationResult result = await GetService<AgentService>()
            .ExecuteAsync(agentCommand, TransactionType.Persist, TransactionMode.Own);

        // Assert
        Assert.True(result.Succeeded, result.FullErrorText);
        Agent agent = await ReadAgentAsync();
        Assert.Equal("Jane Doe", agent.Name);
        AssertAddress(
            agent.Address,
            "123 Another Pl",
            "Auckland",
            "Aotearoa",
            "support@Erewhon.nz",
            "FAX2",
            "555 7654321",
            "https://www.jadeworld.com/contact-us/");

        AssertCollectionsAndCompany(agent);
    }

    /// <summary>
    /// Updating the agent outside a transaction raises error 1048 and leaves the stored name and address unchanged.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdateInTransStateException()
    {
        // Arrange
        var agentCommand = new AgentCommand();
        agentCommand.PopulateFromObject(MyAgent);
        SetUpdatedValues(agentCommand);

        // Act
        NotInTransactionStateException exception = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => GetService<AgentService>()
                .ExecuteAsync(agentCommand, TransactionType.Persist, TransactionMode.Ambient));

        // Assert
        Assert.Equal(UpdateOutsideTransaction, exception.ErrorCode);
        Agent agent = await ReadAgentAsync();
        Assert.Equal("John Smith", agent.Name);
        AssertAddress(
            agent.Address,
            "314 Nohwere Lane",
            "Christchurch",
            "New Zealand",
            "admin@Erewhon.nz",
            "FAX",
            "555 1234567",
            "www.jadeworld.com");
    }

    /// <summary>Sets the name "Jane Doe" and the 123 Another Pl, Auckland, address on <paramref name="agentCommand"/>.</summary>
    private static void SetUpdatedValues(AgentCommand agentCommand)
    {
        Assert.NotNull(agentCommand.Address);
        AddressCommand address = agentCommand.Address;
        address.Street = "123 Another Pl";
        address.City = "Auckland";
        address.Country = "Aotearoa";
        address.Email = "support@Erewhon.nz";
        address.Fax = "FAX2";
        address.Phone = "555 7654321";
        address.WebSite = "https://www.jadeworld.com/contact-us/";

        agentCommand.Name = "Jane Doe";
    }

    /// <summary>Asserts the seven fields of <paramref name="address"/>.</summary>
    private static void AssertAddress(
        Address address,
        string street,
        string city,
        string country,
        string email,
        string fax,
        string phone,
        string webSite)
    {
        Assert.Equal(street, address.Street);
        Assert.Equal(city, address.City);
        Assert.Equal(country, address.Country);
        Assert.Equal(email, address.Email);
        Assert.Equal(fax, address.Fax);
        Assert.Equal(phone, address.Phone);
        Assert.Equal(webSite, address.WebSite);
    }

    /// <summary>
    /// Asserts that <paramref name="agent"/> has its commission-rate, item and sold-item collections and belongs to the
    /// fixture company.
    /// </summary>
    private void AssertCollectionsAndCompany(Agent agent)
    {
        Assert.NotNull(agent.AgentCommissionRates);
        Assert.NotNull(agent.Items);
        Assert.NotNull(agent.GetSoldItems());

        Assert.NotNull(agent.Company);
        Assert.Equal(MyCompany.Id, agent.CompanyId);
    }

    /// <summary>
    /// Returns the commission rates of <paramref name="agent"/> ordered by the ordinal name of their item category.
    /// </summary>
    private static IReadOnlyList<CommissionRate> CommissionRatesByCategoryName(Agent agent) =>
        agent.AgentCommissionRates
            .Select(link => link.CommissionRate!)
            .OrderBy(rate => rate.ItemCategory!.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Reads the fixture agent untracked in a new scope: its address, its commission-rate links with each rate and the
    /// rate's item category, its items with each item's category and sale, and its company with the company's item
    /// categories and their rates.
    /// </summary>
    private Task<Agent> ReadAgentAsync()
    {
        long agentId = MyAgent.Id;
        return ReadAsync(services => services.GetRequiredService<IErewhonDbContext>()
            .Agents
            .AsNoTrackingWithIdentityResolution()
            .AsSplitQuery()
            .Include(agent => agent.AgentCommissionRates)
                .ThenInclude(link => link.CommissionRate)
                    .ThenInclude(rate => rate!.ItemCategory)
            .Include(agent => agent.Items)
                .ThenInclude(item => item.ItemCategory)
            .Include(agent => agent.Items)
                .ThenInclude(item => item.Sale)
            .Include(agent => agent.Company)
                .ThenInclude(company => company!.ItemCategories)
                    .ThenInclude(category => category.CommissionRates)
            .SingleAsync(agent => agent.Id == agentId));
    }

    /// <summary>Runs <paramref name="read"/> in a new scope of the API with the fixture company bound.</summary>
    private async Task<T> ReadAsync<T>(Func<IServiceProvider, Task<T>> read)
    {
        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(MyCompany.Id);
        return await read(scope.ServiceProvider);
    }
}

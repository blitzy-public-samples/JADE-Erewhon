using System.Data.Common;
using System.Text.RegularExpressions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Cart;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Persistence;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Application.Tests.Services;

/// <summary>
/// Characterises the agent commission of retail sales, tender sales and checkouts persisted through the production
/// services on PostgreSQL: the agent's rate for the item's category name, otherwise the lowest rate of the category of
/// that name in the agent's company, taken from the rates stored when each sale is created; the outcome of a checkout of
/// several retail and tender lines; and the item-category reads of a checkout. Each case
/// arranges, acts and asserts in separate dependency-injection scopes, and deletes every company it creates.
/// </summary>
public sealed class SaleCommissionTests : IAsyncLifetime
{
    private readonly ItemCategoryQueryCounter _itemCategoryQueries = new();
    private ServiceProvider? _services;

    /// <summary>
    /// The sale kind, whether the agent belongs to another company than the item, the lowest and the other rate of the
    /// agent company's category, the sale price or winning offer, and the expected commission.
    /// </summary>
    public static TheoryData<bool, bool, decimal, decimal, decimal, decimal> FallbackCases => new()
    {
        { false, true, 3.5m, 5m, 23m, 0.81m },
        { true, true, 3.5m, 5m, 23m, 0.81m },
        { false, false, 2m, 3.5m, 100m, 2.00m },
        { true, false, 2m, 3.5m, 100m, 2.00m },
    };

    /// <summary>
    /// The sale kind, the category's only rate when the first sale is created, the rate another scope then commits
    /// on the category, whether the agent holds that rate, and the expected commissions of the first and second sale.
    /// </summary>
    public static TheoryData<bool, decimal, decimal, bool, decimal, decimal> RatesStoredAtCreationCases => new()
    {
        { false, 2m, 10m, true, 2.00m, 10.00m },
        { true, 2m, 10m, true, 2.00m, 10.00m },
        { false, 5m, 2m, false, 5.00m, 2.00m },
        { true, 5m, 2m, false, 5.00m, 2.00m },
    };

    /// <summary>Creates and migrates the test database of the assembly and builds the services over it.</summary>
    /// <returns>A task that completes when the services are ready.</returns>
    public async Task InitializeAsync()
    {
        PostgresDatabase database = PostgresDatabase.ForAssembly(typeof(SaleCommissionTests).Assembly);
        await database.EnsureCreatedAndMigratedAsync();
        _services = BuildServices(database.ConnectionString, _itemCategoryQueries);
    }

    /// <summary>Disposes the services.</summary>
    /// <returns>A task that completes when the services are disposed.</returns>
    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
    }

    /// <summary>
    /// An item of a category without rates whose agent holds no rate earns the lowest rate of the agent company's
    /// category of the same name: from another company 3.5% of 23 → 0.81, from the item's own company 2% of 100 → 2.00,
    /// for a retail sale on its sale price and for a tender sale on its winning offer.
    /// </summary>
    /// <param name="tenderSale"><see langword="true"/> for a tender sale; <see langword="false"/> for a retail sale.</param>
    /// <param name="agentInOtherCompany">Whether the agent belongs to another company than the item.</param>
    /// <param name="lowestRate">The lowest rate of the agent company's category.</param>
    /// <param name="otherRate">The other rate of the agent company's category.</param>
    /// <param name="amount">The retail sale price or the winning tender offer.</param>
    /// <param name="expectedCommission">The commission expected on the persisted sale.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [MemberData(nameof(FallbackCases))]
    public async Task SaleCommissionFallsBackToLowestRateOfAgentCompanyCategoryOfThatName(
        bool tenderSale,
        bool agentInOtherCompany,
        decimal lowestRate,
        decimal otherRate,
        decimal amount,
        decimal expectedCommission)
    {
        var companyIds = new List<long>();
        try
        {
            long itemCompanyId;
            long itemId;
            long clientId;
            long? tenderId = null;

            await using (AsyncServiceScope arrange = Services.CreateAsyncScope())
            {
                var builder = new TestCompanyBuilder(arrange.ServiceProvider);
                Company itemCompany = await builder.CreateCompanyAsync();
                companyIds.Add(itemCompany.Id);
                Company agentCompany = itemCompany;
                if (agentInOtherCompany)
                {
                    agentCompany = await builder.CreateCompanyAsync();
                    companyIds.Add(agentCompany.Id);
                }

                builder.Bind(agentCompany.Id);
                Agent agent = await builder.CreateAgentAsync();
                ItemCategory agentCategory = await builder.CreateItemCategoryAsync("Widgets", "WIDA", "Agent company widgets");
                await builder.CreateTestCommissionRateAsync(agentCategory, otherRate);
                await builder.CreateTestCommissionRateAsync(agentCategory, lowestRate);

                builder.Bind(itemCompany.Id);
                ItemCategory itemCategory = agentInOtherCompany
                    ? await builder.CreateItemCategoryAsync("Widgets", "WIDI", "Item company widgets")
                    : agentCategory;
                Region region = await builder.CreateRegionAsync(await builder.CreateCountryAsync());
                Client client = await builder.CreateClientAsync();

                if (tenderSale)
                {
                    TenderItem tenderItem = await builder.CreateTenderItemAsync(region, itemCategory, agent);
                    Tender tender = await builder.CreateTenderAsync(client, tenderItem, amount);
                    itemId = tenderItem.Id;
                    tenderId = tender.Id;
                }
                else
                {
                    RetailItem retailItem = await builder.CreateTestRetailItemAsync(
                        "Commission Item", "An item sold for commission", null, region, itemCategory, agent, amount);
                    itemId = retailItem.Id;
                }

                itemCompanyId = itemCompany.Id;
                clientId = client.Id;
            }

            long saleId;
            await using (AsyncServiceScope act = Services.CreateAsyncScope())
            {
                act.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(itemCompanyId);
                OperationResult result = tenderSale
                    ? await act.ServiceProvider.GetRequiredService<TenderSaleService>().ExecuteAsync(
                        new TenderSaleCommand { ItemId = itemId, TenderId = tenderId, ClientId = clientId },
                        TransactionType.Persist,
                        TransactionMode.Own)
                    : await act.ServiceProvider.GetRequiredService<RetailSaleService>().ExecuteAsync(
                        new RetailSaleCommand { ItemId = itemId, ClientId = clientId, SalePrice = amount },
                        TransactionType.Persist,
                        TransactionMode.Own);

                Assert.True(result.Succeeded, result.FullErrorText);
                saleId = Assert.IsType<long>(result.Id);
            }

            await using AsyncServiceScope assert = Services.CreateAsyncScope();
            decimal commission = await assert.ServiceProvider.GetRequiredService<IErewhonDbContext>().Sales
                .AsNoTracking()
                .Where(sale => sale.Id == saleId)
                .Select(sale => sale.AgentCommission)
                .SingleAsync();
            Assert.Equal(expectedCommission, commission);
        }
        finally
        {
            await DeleteCompaniesAsync(companyIds);
        }
    }

    /// <summary>
    /// Two sales of one agent's items, priced 100, persisted through the services of one scope each take the rates
    /// stored when the sale is created, the agent holding no rate when the first is created: the first earns the
    /// category's only rate; after another scope commits a rate on the category, the second earns that rate when the
    /// agent holds it (2.00, then 10% → 10.00) and when it is the category's new lowest rate (5.00, then 2% → 2.00),
    /// for retail sales on their sale price and for tender sales on their winning offer.
    /// </summary>
    /// <param name="tenderSale"><see langword="true"/> for tender sales; <see langword="false"/> for retail sales.</param>
    /// <param name="initialRate">The category's only rate when the first sale is created.</param>
    /// <param name="addedRate">The rate another scope commits on the category after the first sale.</param>
    /// <param name="addedRateHeldByAgent">Whether the agent holds the added rate.</param>
    /// <param name="expectedFirstCommission">The commission expected on the first sale.</param>
    /// <param name="expectedSecondCommission">The commission expected on the second sale.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [MemberData(nameof(RatesStoredAtCreationCases))]
    public async Task EachSaleOfOneScopeTakesTheRatesStoredAtItsCreation(
        bool tenderSale,
        decimal initialRate,
        decimal addedRate,
        bool addedRateHeldByAgent,
        decimal expectedFirstCommission,
        decimal expectedSecondCommission)
    {
        var companyIds = new List<long>();
        try
        {
            long shopId;
            long clientId;
            Agent agent;
            ItemCategory category;
            var itemIds = new long[2];
            var tenderIds = new long?[2];

            await using (AsyncServiceScope arrange = Services.CreateAsyncScope())
            {
                var builder = new TestCompanyBuilder(arrange.ServiceProvider);
                Company shop = await builder.CreateCompanyAsync();
                companyIds.Add(shop.Id);
                builder.Bind(shop.Id);

                agent = await builder.CreateAgentAsync();
                Client client = await builder.CreateClientAsync();
                Region region = await builder.CreateRegionAsync(await builder.CreateCountryAsync());
                category = await builder.CreateItemCategoryAsync("Widgets", "WIDG", "Widgets");
                await builder.CreateTestCommissionRateAsync(category, initialRate);
                DateOnly closureDate = (await arrange.ServiceProvider.GetRequiredService<IServerClock>().GetServerDateAsync())
                    .AddDays(7);

                for (int index = 0; index < itemIds.Length; index++)
                {
                    if (tenderSale)
                    {
                        TenderItem tenderItem = await builder.CreateTestTenderItemAsync(
                            "Tender Item " + index, "Sold in one scope", null, region, category, agent, 5.50m, closureDate);
                        itemIds[index] = tenderItem.Id;
                        tenderIds[index] = (await builder.CreateTenderAsync(client, tenderItem, 100m)).Id;
                    }
                    else
                    {
                        itemIds[index] = (await builder.CreateTestRetailItemAsync(
                            "Retail Item " + index, "Sold in one scope", null, region, category, agent, 100m)).Id;
                    }
                }

                shopId = shop.Id;
                clientId = client.Id;
            }

            var saleIds = new long[2];
            await using (AsyncServiceScope act = Services.CreateAsyncScope())
            {
                act.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(shopId);
                saleIds[0] = await PersistSaleAsync(act.ServiceProvider, itemIds[0], clientId, tenderIds[0], 100m);

                await using (AsyncServiceScope change = Services.CreateAsyncScope())
                {
                    var builder = new TestCompanyBuilder(change.ServiceProvider);
                    builder.Bind(shopId);
                    if (addedRateHeldByAgent)
                    {
                        await builder.CreateCommissionRateAsync(category, addedRate, agent);
                    }
                    else
                    {
                        await builder.CreateTestCommissionRateAsync(category, addedRate);
                    }
                }

                saleIds[1] = await PersistSaleAsync(act.ServiceProvider, itemIds[1], clientId, tenderIds[1], 100m);
            }

            await using AsyncServiceScope assert = Services.CreateAsyncScope();
            IErewhonDbContext db = assert.ServiceProvider.GetRequiredService<IErewhonDbContext>();
            var commissions = new List<decimal>();
            foreach (long saleId in saleIds)
            {
                commissions.Add(await db.Sales
                    .AsNoTracking()
                    .Where(sale => sale.Id == saleId)
                    .Select(sale => sale.AgentCommission)
                    .SingleAsync());
            }

            decimal[] expectedCommissions = [expectedFirstCommission, expectedSecondCommission];
            Assert.Equal(expectedCommissions, commissions);
        }
        finally
        {
            await DeleteCompaniesAsync(companyIds);
        }
    }

    /// <summary>
    /// A checkout of three retail lines and one tender line persists one retail sale per retail line at the line's
    /// price, with the agent's own rate (10% of 50 → 5.00), the item category's lowest rate (2% of 100 → 2.00) and the
    /// lowest rate of the agent company's category of the same name (3.5% of 23 → 0.81), and one tender of 23 with no
    /// tender sale for the tender line.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task CheckoutPersistsEachRetailLineWithItsCommissionAndEachTenderLineAsTender()
    {
        var companyIds = new List<long>();
        string sessionId = "sale-commission-" + Guid.NewGuid().ToString("N");
        try
        {
            long shopId;
            long clientId;
            long ownRateItemId;
            long fallbackItemId;
            long otherCompanyAgentItemId;
            long tenderItemId;

            await using (AsyncServiceScope arrange = Services.CreateAsyncScope())
            {
                var builder = new TestCompanyBuilder(arrange.ServiceProvider);
                Company shop = await builder.CreateCompanyAsync();
                companyIds.Add(shop.Id);
                Company agentCompany = await builder.CreateCompanyAsync();
                companyIds.Add(agentCompany.Id);

                builder.Bind(agentCompany.Id);
                Agent otherCompanyAgent = await builder.CreateAgentAsync();
                ItemCategory agentCompanyGadgets = await builder.CreateItemCategoryAsync("Gadgets", "GADB", "Agent company gadgets");
                await builder.CreateTestCommissionRateAsync(agentCompanyGadgets, 5m);
                await builder.CreateTestCommissionRateAsync(agentCompanyGadgets, 3.5m);

                builder.Bind(shop.Id);
                Agent agent = await builder.CreateAgentAsync();
                Client client = await builder.CreateClientAsync();
                Region region = await builder.CreateRegionAsync(await builder.CreateCountryAsync());
                ItemCategory widgets = await builder.CreateItemCategoryAsync("Widgets", "WIDG", "Widgets with an agent rate");
                await builder.CreateTestCommissionRateAsync(widgets, 1m);
                await builder.CreateCommissionRateAsync(widgets, 10m, agent);
                ItemCategory doohickeys = await builder.CreateItemCategoryAsync(
                    "Doohickeys", "DOOH", "Doohickeys without an agent rate");
                await builder.CreateTestCommissionRateAsync(doohickeys, 3.5m);
                await builder.CreateTestCommissionRateAsync(doohickeys, 2m);
                ItemCategory gadgets = await builder.CreateItemCategoryAsync("Gadgets", "GADG", "Gadgets without rates");

                ownRateItemId = (await builder.CreateTestRetailItemAsync(
                    "Own Rate Item", "Sold at the agent's rate", null, region, widgets, agent, 50m)).Id;
                fallbackItemId = (await builder.CreateTestRetailItemAsync(
                    "Fallback Item", "Sold at the lowest rate", null, region, doohickeys, agent, 100m)).Id;
                otherCompanyAgentItemId = (await builder.CreateTestRetailItemAsync(
                    "Other Agent Item", "Sold by another company's agent", null, region, gadgets, otherCompanyAgent, 23m)).Id;
                DateOnly serverDate = await arrange.ServiceProvider.GetRequiredService<IServerClock>().GetServerDateAsync();
                tenderItemId = (await builder.CreateTestTenderItemAsync(
                    "Tender Line Item", "Tendered at checkout", null, region, widgets, agent, 5.50m, serverDate.AddDays(7))).Id;

                shopId = shop.Id;
                clientId = client.Id;
            }

            await using (AsyncServiceScope act = Services.CreateAsyncScope())
            {
                act.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(shopId);
                act.ServiceProvider.GetRequiredService<ISessionContext>().SetClient(clientId);
                ShoppingCartService cart = act.ServiceProvider.GetRequiredService<ShoppingCartService>();
                foreach ((long itemId, decimal offer) in new[]
                {
                    (ownRateItemId, 0m), (fallbackItemId, 0m), (otherCompanyAgentItemId, 0m), (tenderItemId, 23m),
                })
                {
                    OperationResult added = await cart.AddItemAsync(sessionId, itemId, offer);
                    Assert.True(added.Succeeded, added.FullErrorText);
                }

                CheckoutResult checkout = await cart.CheckoutAsync(sessionId);

                Assert.True(checkout.Result.Succeeded, checkout.Result.FullErrorText);
                Assert.NotNull(checkout.Receipt);
            }

            await using AsyncServiceScope assert = Services.CreateAsyncScope();
            IErewhonDbContext db = assert.ServiceProvider.GetRequiredService<IErewhonDbContext>();
            var sales = await db.Sales
                .AsNoTracking()
                .OfType<RetailSale>()
                .Where(sale => sale.CompanyId == shopId)
                .OrderBy(sale => sale.ItemId)
                .Select(sale => new { sale.ItemId, sale.ClientId, sale.SalePrice, sale.AgentCommission })
                .ToListAsync();
            var expectedSales = new[]
            {
                new { ItemId = ownRateItemId, ClientId = clientId, SalePrice = 50.00m, AgentCommission = 5.00m },
                new { ItemId = fallbackItemId, ClientId = clientId, SalePrice = 100.00m, AgentCommission = 2.00m },
                new { ItemId = otherCompanyAgentItemId, ClientId = clientId, SalePrice = 23.00m, AgentCommission = 0.81m },
            };
            Assert.Equal(expectedSales, sales);

            var tender = Assert.Single(await db.Tenders
                .AsNoTracking()
                .Where(t => t.TenderItemId == tenderItemId)
                .Select(t => new { t.ClientId, t.Offer })
                .ToListAsync());
            Assert.Equal(new { ClientId = clientId, Offer = 23.00m }, tender);
            Assert.False(await db.Sales.AsNoTracking().AnyAsync(sale => sale.ItemId == tenderItemId));
        }
        finally
        {
            Services.GetRequiredService<ICartStore>().RemoveCart(sessionId);
            await DeleteCompaniesAsync(companyIds);
        }
    }

    /// <summary>
    /// A checkout of three retail lines whose agent holds no rate and belongs to another company, which has no item
    /// category of the items' category name, persists each sale with a commission of 0.00 although the items' own
    /// category has a 3.5% rate, and reads the <c>item_categories</c> table once, in the commission preload, not once
    /// per line.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task CheckoutOfLinesWhoseAgentCompanyHasNoCategoryOfTheNameReadsTheCategoriesOnce()
    {
        var companyIds = new List<long>();
        string sessionId = "sale-commission-" + Guid.NewGuid().ToString("N");
        try
        {
            long shopId;
            long clientId;
            var itemIds = new List<long>();

            await using (AsyncServiceScope arrange = Services.CreateAsyncScope())
            {
                var builder = new TestCompanyBuilder(arrange.ServiceProvider);
                Company shop = await builder.CreateCompanyAsync();
                companyIds.Add(shop.Id);
                Company agentCompany = await builder.CreateCompanyAsync();
                companyIds.Add(agentCompany.Id);

                builder.Bind(agentCompany.Id);
                Agent agent = await builder.CreateAgentAsync();

                builder.Bind(shop.Id);
                Client client = await builder.CreateClientAsync();
                Region region = await builder.CreateRegionAsync(await builder.CreateCountryAsync());
                ItemCategory gizmos = await builder.CreateItemCategoryAsync("Gizmos", "GIZM", "Gizmos of the shop only");
                await builder.CreateTestCommissionRateAsync(gizmos, 3.5m);
                foreach (decimal price in new[] { 23m, 40m, 100m })
                {
                    itemIds.Add((await builder.CreateTestRetailItemAsync(
                        "Gizmo " + price, "Sold by another company's agent", null, region, gizmos, agent, price)).Id);
                }

                shopId = shop.Id;
                clientId = client.Id;
            }

            int itemCategoryQueries;
            await using (AsyncServiceScope act = Services.CreateAsyncScope())
            {
                act.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(shopId);
                act.ServiceProvider.GetRequiredService<ISessionContext>().SetClient(clientId);
                ShoppingCartService cart = act.ServiceProvider.GetRequiredService<ShoppingCartService>();
                foreach (long itemId in itemIds)
                {
                    OperationResult added = await cart.AddItemAsync(sessionId, itemId, 0m);
                    Assert.True(added.Succeeded, added.FullErrorText);
                }

                _itemCategoryQueries.Start();
                CheckoutResult checkout = await cart.CheckoutAsync(sessionId);
                itemCategoryQueries = _itemCategoryQueries.Stop();

                Assert.True(checkout.Result.Succeeded, checkout.Result.FullErrorText);
            }

            Assert.Equal(1, itemCategoryQueries);

            await using AsyncServiceScope assert = Services.CreateAsyncScope();
            var sales = await assert.ServiceProvider.GetRequiredService<IErewhonDbContext>().Sales
                .AsNoTracking()
                .Where(sale => sale.CompanyId == shopId)
                .OrderBy(sale => sale.ItemId)
                .Select(sale => new { sale.ItemId, sale.AgentCommission })
                .ToListAsync();
            var expectedSales = itemIds.Order().Select(itemId => new { ItemId = itemId, AgentCommission = 0.00m }).ToList();
            Assert.Equal(expectedSales, sales);
        }
        finally
        {
            Services.GetRequiredService<ICartStore>().RemoveCart(sessionId);
            await DeleteCompaniesAsync(companyIds);
        }
    }

    /// <summary>
    /// A checkout whose first retail line's item another scope has sold, whose first tender line's item has been
    /// awarded and whose second tender line repeats the client's stored offer fails with
    /// <see cref="MessageKeys.ItemSold"/>, <see cref="MessageKeys.TenderItemAlreadySold"/> and
    /// <see cref="MessageKeys.TenderAlreadyPlaced"/> in line order, the first focus field being the item's, persists
    /// no sale or tender for the client and leaves every line in the cart.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task CheckoutOfSoldItemsFailsInLineOrderAndPersistsNothing()
    {
        var companyIds = new List<long>();
        string sessionId = "sale-commission-" + Guid.NewGuid().ToString("N");
        try
        {
            long shopId;
            long clientId;
            long soldItemId;
            long unsoldItemId;
            long awardedItemId;
            long tenderedItemId;

            await using (AsyncServiceScope arrange = Services.CreateAsyncScope())
            {
                var builder = new TestCompanyBuilder(arrange.ServiceProvider);
                Company shop = await builder.CreateCompanyAsync();
                companyIds.Add(shop.Id);
                builder.Bind(shop.Id);

                Agent agent = await builder.CreateAgentAsync();
                Client client = await builder.CreateClientAsync();
                Client otherClient = await builder.CreateTestClientAsync(
                    "Other Client",
                    builder.CreateTestAddress(
                        "1 Other St", "Dunedin", "New Zealand", "555 1111111", "FAX", "other@client.nz", "www.client.nz"));
                Region region = await builder.CreateRegionAsync(await builder.CreateCountryAsync());
                ItemCategory category = await builder.CreateItemCategoryAsync("Widgets", "WIDG", "Widgets");
                await builder.CreateTestCommissionRateAsync(category, 2m);
                IServerClock clock = arrange.ServiceProvider.GetRequiredService<IServerClock>();
                DateOnly closureDate = (await clock.GetServerDateAsync()).AddDays(7);

                RetailItem soldItem = await builder.CreateTestRetailItemAsync(
                    "Sold Item", "Sold before checkout", null, region, category, agent, 40m);
                RetailItem unsoldItem = await builder.CreateTestRetailItemAsync(
                    "Unsold Item", "Unsold at checkout", null, region, category, agent, 60m);
                TenderItem awardedItem = await builder.CreateTestTenderItemAsync(
                    "Awarded Item", "Awarded before checkout", null, region, category, agent, 5.50m, closureDate);
                TenderItem tenderedItem = await builder.CreateTestTenderItemAsync(
                    "Tendered Item", "Tendered before checkout", null, region, category, agent, 5.50m, closureDate);

                await builder.CreateTestRetailSaleAsync(soldItem, otherClient, await clock.GetServerTimeStampAsync());
                Tender winningTender = await builder.CreateTenderAsync(otherClient, awardedItem, 30m);
                await builder.CreateTenderSaleAsync(awardedItem, winningTender, otherClient);
                await builder.CreateTenderAsync(client, tenderedItem, 23m);

                shopId = shop.Id;
                clientId = client.Id;
                soldItemId = soldItem.Id;
                unsoldItemId = unsoldItem.Id;
                awardedItemId = awardedItem.Id;
                tenderedItemId = tenderedItem.Id;
            }

            await using (AsyncServiceScope act = Services.CreateAsyncScope())
            {
                act.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(shopId);
                act.ServiceProvider.GetRequiredService<ISessionContext>().SetClient(clientId);
                ShoppingCartService cart = act.ServiceProvider.GetRequiredService<ShoppingCartService>();
                foreach ((long itemId, decimal offer) in new[]
                {
                    (soldItemId, 0m), (unsoldItemId, 0m), (awardedItemId, 23m), (tenderedItemId, 23m),
                })
                {
                    OperationResult added = await cart.AddItemAsync(sessionId, itemId, offer);
                    Assert.True(added.Succeeded, added.FullErrorText);
                }

                CheckoutResult checkout = await cart.CheckoutAsync(sessionId);

                Assert.False(checkout.Result.Succeeded);
                Assert.Null(checkout.Receipt);
                string[] expectedErrors =
                [
                    MessageCatalog.Get(MessageKeys.ItemSold),
                    MessageCatalog.Get(MessageKeys.TenderItemAlreadySold),
                    MessageCatalog.Get(MessageKeys.TenderAlreadyPlaced),
                ];
                Assert.Equal(expectedErrors, checkout.Result.Errors);
                Assert.Equal(FocusFields.Item, checkout.Result.FocusField);
            }

            await using AsyncServiceScope assert = Services.CreateAsyncScope();
            IErewhonDbContext db = assert.ServiceProvider.GetRequiredService<IErewhonDbContext>();
            Assert.False(await db.Sales.AsNoTracking().AnyAsync(sale => sale.ClientId == clientId));
            long tenderedByClient = Assert.Single(await db.Tenders
                .AsNoTracking()
                .Where(tender => tender.ClientId == clientId)
                .Select(tender => tender.TenderItemId)
                .ToListAsync());
            Assert.Equal(tenderedItemId, tenderedByClient);
            long[] expectedCartItemIds = [soldItemId, unsoldItemId, awardedItemId, tenderedItemId];
            Assert.Equal(
                expectedCartItemIds,
                assert.ServiceProvider.GetRequiredService<ICartStore>().GetCart(sessionId).Items.Select(line => line.ItemId));
        }
        finally
        {
            Services.GetRequiredService<ICartStore>().RemoveCart(sessionId);
            await DeleteCompaniesAsync(companyIds);
        }
    }

    /// <summary>
    /// A checkout with two retail lines for one item persists the first line's sale inside the checkout, fails the
    /// second line with <see cref="MessageKeys.ItemSold"/> and persists no sale of the item.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task CheckoutWithTwoLinesForOneItemFailsTheSecondLineWithItemSold()
    {
        var companyIds = new List<long>();
        try
        {
            (long shopId, long clientId, long itemId) = await ArrangeRetailItemAsync(companyIds);

            await using (AsyncServiceScope act = Services.CreateAsyncScope())
            {
                act.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(shopId);
                var lines = new List<ShoppingCartLineCommand>();
                for (int index = 0; index < 2; index++)
                {
                    lines.Add(new ShoppingCartLineCommand(
                        RetailLine(itemId, clientId),
                        new RetailSaleCommand { ItemId = itemId, ClientId = clientId, SalePrice = 40m }));
                }

                var command = new ShoppingCartCommand(new ShoppingCart("two-lines"), clientId, lines);
                OperationResult result = await act.ServiceProvider.GetRequiredService<ShoppingCartService>()
                    .ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);

                Assert.False(result.Succeeded);
                Assert.Equal(MessageCatalog.Get(MessageKeys.ItemSold), Assert.Single(result.Errors));
            }

            await using AsyncServiceScope assert = Services.CreateAsyncScope();
            Assert.False(await assert.ServiceProvider.GetRequiredService<IErewhonDbContext>().Sales
                .AsNoTracking()
                .AnyAsync(sale => sale.ItemId == itemId));
        }
        finally
        {
            await DeleteCompaniesAsync(companyIds);
        }
    }

    /// <summary>
    /// A checkout whose tender line names a retail item raises <see cref="InvalidOperationException"/> "No tender item
    /// has the id …" from that line's validation and persists no tender.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task CheckoutOfTenderLineForNoTenderItemRaisesInvalidOperationException()
    {
        var companyIds = new List<long>();
        try
        {
            (long shopId, long clientId, long itemId) = await ArrangeRetailItemAsync(companyIds);

            await using (AsyncServiceScope act = Services.CreateAsyncScope())
            {
                act.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(shopId);
                DateTime dateTendered = await act.ServiceProvider.GetRequiredService<IServerClock>().GetServerTimeStampAsync();
                var line = new ShoppingCartLineCommand(
                    new CartItem
                    {
                        ItemId = itemId,
                        IsTender = true,
                        Code = "LINE",
                        Transaction = MessageCatalog.Get(MessageKeys.TenderItemType),
                        Offer = 23m,
                        ClientId = clientId,
                        DateTendered = dateTendered,
                    },
                    new TenderCommand { TenderItemId = itemId, ClientId = clientId, Offer = 23m, DateTendered = dateTendered });
                var command = new ShoppingCartCommand(new ShoppingCart("no-tender-item"), clientId, [line]);

                InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => act.ServiceProvider.GetRequiredService<ShoppingCartService>()
                        .ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own));

                Assert.Equal($"No tender item has the id {itemId}.", exception.Message);
            }

            await using AsyncServiceScope assert = Services.CreateAsyncScope();
            Assert.False(await assert.ServiceProvider.GetRequiredService<IErewhonDbContext>().Tenders
                .AsNoTracking()
                .AnyAsync(tender => tender.ClientId == clientId));
        }
        finally
        {
            await DeleteCompaniesAsync(companyIds);
        }
    }

    /// <summary>The services built by <see cref="InitializeAsync"/>.</summary>
    private ServiceProvider Services =>
        _services ?? throw new InvalidOperationException("The services are built by InitializeAsync.");

    /// <summary>
    /// Returns the production application and persistence services over the database at
    /// <paramref name="connectionString"/>: one database context, unit of work, lock and clock adapter, notification
    /// queue, bound company and session per scope, the session carts shared by every scope, notifications
    /// discarded instead of published, and <paramref name="itemCategoryQueries"/> intercepting every context's commands.
    /// </summary>
    private static ServiceProvider BuildServices(string connectionString, ItemCategoryQueryCounter itemCategoryQueries)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICartStore, InMemoryCartStore>();
        services.AddSingleton<INotificationPublisher, DiscardingNotificationPublisher>();
        services.AddSingleton(new EntityLockSettings());
        services.AddSingleton<PostgresErrorMapper>();
        services.AddScoped<NotificationQueue>();
        services.AddScoped<NotificationDeriver>();
        services.AddScoped<ChangeNotificationInterceptor>();
        services.AddScoped(provider => new ErewhonDbContext(
            new DbContextOptionsBuilder<ErewhonDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(itemCategoryQueries)
                .Options,
            provider.GetRequiredService<ChangeNotificationInterceptor>()));
        services.AddScoped<IErewhonDbContext>(provider => provider.GetRequiredService<ErewhonDbContext>());
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IEntityLocks, PostgresEntityLocks>();
        services.AddScoped<IServerClock, PostgresServerClock>();
        services.AddScoped<ICompanyContext, CompanyContext>();
        services.AddScoped<ISessionContext, SessionlessContext>();
        services.AddScoped<ItemCodeAllocator>();
        services.AddScoped<CompanyService>();
        services.AddScoped<AgentService>();
        services.AddScoped<ClientService>();
        services.AddScoped<CommissionRateService>();
        services.AddScoped<ItemCategoryService>();
        services.AddScoped<CountryService>();
        services.AddScoped<RegionService>();
        services.AddScoped<ItemService>();
        services.AddScoped<RetailSaleService>();
        services.AddScoped<TenderSaleService>();
        services.AddScoped<TenderService>();
        services.AddScoped<ShoppingCartService>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>
    /// Persists, in its own transaction through the services of <paramref name="provider"/>, a sale of the item with
    /// row id <paramref name="itemId"/> to the client: a tender sale on the winning tender <paramref name="tenderId"/>
    /// when there is one, otherwise a retail sale at <paramref name="salePrice"/>; asserts that it succeeds.
    /// </summary>
    /// <returns>The row id of the new sale.</returns>
    private static async Task<long> PersistSaleAsync(
        IServiceProvider provider,
        long itemId,
        long clientId,
        long? tenderId,
        decimal salePrice)
    {
        OperationResult result = tenderId is not null
            ? await provider.GetRequiredService<TenderSaleService>().ExecuteAsync(
                new TenderSaleCommand { ItemId = itemId, TenderId = tenderId, ClientId = clientId },
                TransactionType.Persist,
                TransactionMode.Own)
            : await provider.GetRequiredService<RetailSaleService>().ExecuteAsync(
                new RetailSaleCommand { ItemId = itemId, ClientId = clientId, SalePrice = salePrice },
                TransactionType.Persist,
                TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        return Assert.IsType<long>(result.Id);
    }

    /// <summary>Returns a cart line of the retail item with row id <paramref name="itemId"/> for the client, priced 40.</summary>
    private static CartItem RetailLine(long itemId, long clientId) => new()
    {
        ItemId = itemId,
        IsTender = false,
        Code = "LINE",
        Transaction = MessageCatalog.Get(MessageKeys.RetailItemType),
        Offer = 0m,
        SalePrice = 40m,
        ClientId = clientId,
    };

    /// <summary>
    /// Creates, in a new scope, a company with an agent, a client, a region and a category with a rate of 2%, and a
    /// retail item of that category priced 40; adds the company's id to <paramref name="companyIds"/>.
    /// </summary>
    /// <returns>The row ids of the company, the client and the retail item.</returns>
    private async Task<(long ShopId, long ClientId, long ItemId)> ArrangeRetailItemAsync(List<long> companyIds)
    {
        await using AsyncServiceScope arrange = Services.CreateAsyncScope();
        var builder = new TestCompanyBuilder(arrange.ServiceProvider);
        Company shop = await builder.CreateCompanyAsync();
        companyIds.Add(shop.Id);
        builder.Bind(shop.Id);

        Agent agent = await builder.CreateAgentAsync();
        Client client = await builder.CreateClientAsync();
        Region region = await builder.CreateRegionAsync(await builder.CreateCountryAsync());
        ItemCategory category = await builder.CreateItemCategoryAsync("Widgets", "WIDG", "Widgets");
        await builder.CreateTestCommissionRateAsync(category, 2m);
        RetailItem item = await builder.CreateTestRetailItemAsync(
            "Line Item", "Named by every line", null, region, category, agent, 40m);
        return (shop.Id, client.Id, item.Id);
    }

    /// <summary>Deletes, in a new scope and in reverse order of creation, every company in <paramref name="companyIds"/>.</summary>
    private async Task DeleteCompaniesAsync(List<long> companyIds)
    {
        await using AsyncServiceScope cleanup = Services.CreateAsyncScope();
        var builder = new TestCompanyBuilder(cleanup.ServiceProvider);
        for (int index = companyIds.Count - 1; index >= 0; index--)
        {
            await builder.DeleteCompanyAsync(companyIds[index]);
        }
    }

    /// <summary>A scope with no user session; its agent and client are those last set.</summary>
    private sealed class SessionlessContext : ISessionContext
    {
        /// <inheritdoc />
        public bool HasSession => false;

        /// <inheritdoc />
        public string? SessionId => null;

        /// <inheritdoc />
        public SessionRole Role => SessionRole.None;

        /// <inheritdoc />
        public long? AgentId { get; private set; }

        /// <inheritdoc />
        public long? ClientId { get; private set; }

        /// <inheritdoc />
        public bool IsCompanyUser => AgentId is null && ClientId is null;

        /// <inheritdoc />
        public void SetAgent(long? agentId) => AgentId = agentId;

        /// <inheritdoc />
        public void SetClient(long? clientId) => ClientId = clientId;
    }

    /// <summary>
    /// Counts, between <see cref="Start"/> and <see cref="Stop"/>, the SELECT commands EF Core executes whose FROM clause
    /// reads the <c>item_categories</c> table.
    /// </summary>
    private sealed class ItemCategoryQueryCounter : DbCommandInterceptor
    {
        private static readonly Regex ReadsItemCategories = new(
            @"^\s*SELECT\b.*\bFROM\s+item_categories\b",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private int _count;
        private int _counting;

        /// <summary>Sets the count to 0 and starts counting.</summary>
        public void Start()
        {
            Interlocked.Exchange(ref _count, 0);
            Interlocked.Exchange(ref _counting, 1);
        }

        /// <summary>Stops counting.</summary>
        /// <returns>The number of commands counted since <see cref="Start"/>.</returns>
        public int Stop()
        {
            Interlocked.Exchange(ref _counting, 0);
            return Volatile.Read(ref _count);
        }

        /// <inheritdoc />
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        /// <summary>Counts <paramref name="command"/> when counting is on and it reads the item categories.</summary>
        private void Count(DbCommand command)
        {
            if (Volatile.Read(ref _counting) == 1 && ReadsItemCategories.IsMatch(command.CommandText))
            {
                Interlocked.Increment(ref _count);
            }
        }
    }

    /// <summary>Discards every notification it is given.</summary>
    private sealed class DiscardingNotificationPublisher : INotificationPublisher
    {
        /// <inheritdoc />
        public Task PublishEntityChangesAsync(IReadOnlyList<ChangeEvent> events, CancellationToken ct = default) =>
            Task.CompletedTask;

        /// <inheritdoc />
        public Task PublishCartItemDeletedAsync(string sessionId, long itemId, CancellationToken ct = default) =>
            Task.CompletedTask;

        /// <inheritdoc />
        public Task PublishSaveOutstandingChangesAsync(
            string sessionId,
            string entityType,
            long id,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}

using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Erewhon.Api.Cli;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Erewhon.Persistence;
using Erewhon.Persistence.Seed;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Erewhon.Seed.Tests;

/// <summary>
/// Seed-data equivalence: runs <see cref="InitialDataLoader"/> over <c>Erewhon/DataFiles</c> on an empty PostgreSQL
/// database behind the real Erewhon host and compares the persisted rows, the totals, the post-load tender closure and
/// the output lines with <see cref="ExpectedSeedModel"/> and the governing seed values. Each test recreates and migrates
/// the assembly database <c>erewhon_test_erewhon_seed_tests</c> and starts its own host. The data files are only read.
/// </summary>
public sealed class SeedEquivalenceTests : IAsyncLifetime
{
    private const string SeedDataDirectoryKey = "Erewhon:Seed:DataDirectory";
    private const string WinnerItemGuid = "{43565cee-e216-ee11-9fbf-0050568d0023}";
    private const string FieldSeparator = ", ";
    private const string Space = " ";
    private const string Sentinel = "\u0001duration\u0001";
    private const string ConnectionSentinel = "Host=secret-host;Username=secret-user";
    private const string RollbackWarningTemplate = "The seed load transaction could not be rolled back.";
    private const string EndLineWarningTemplate = "The end line of seed data file {SeedFile} was not written.";
    private const string SeedFileProperty = "SeedFile";
    private const string SourceContextProperty = "SourceContext";

    private const string LocationsFile = "Locations.txt";
    private const string AgentsFile = "Agents.txt";
    private const string ClientsFile = "Clients.txt";
    private const string CategoriesFile = "Categories.txt";
    private const string RetailItemsFile = "RetailItems.txt";
    private const string TenderItemsFile = "TenderItems.txt";
    private const string CategoryRatesFile = "CategoryRates.txt";
    private const string AgentRatesFile = "AgentRates.txt";
    private const string TendersFile = "Tenders.txt";
    private const string RetailSalesFile = "RetailSales.txt";

    private const string MissingPhotoFile = "missing-photo.jpg";
    private const int ItemGuidToken = 0;
    private const int ItemPhotoToken = 3;

    private static readonly string[] DataFileOrder =
    [
        LocationsFile,
        AgentsFile,
        ClientsFile,
        CategoriesFile,
        RetailItemsFile,
        TenderItemsFile,
        CategoryRatesFile,
        AgentRatesFile,
        TendersFile,
        RetailSalesFile,
    ];

    private static readonly Regex GeneratedGuid = new(ExpectedSeedModel.GeneratedGuidPattern, RegexOptions.CultureInvariant);

    private static readonly Regex Duration = new(
        "^(" + Regex.Escape(MessageCatalog.Get(MessageKeys.LessThanOneSecond))
        + @"|\d+ " + Regex.Escape(MessageCatalog.Get(MessageKeys.Seconds)) + ")$",
        RegexOptions.CultureInvariant);

    private static readonly Regex WholeSeconds = new(@"^\d+$", RegexOptions.CultureInvariant);

    private readonly PostgresDatabase _database = PostgresDatabase.ForAssembly(typeof(SeedEquivalenceTests).Assembly);
    private readonly CollectingLogSink _logSink = new();
    private readonly string _dataDirectory = ExpectedSeedModel.FindDataDirectory();
    private readonly ExpectedSeedModel _expected;
    private ErewhonApiHost? _host;

    /// <summary>Reads the expected seed model from <c>Erewhon/DataFiles</c>.</summary>
    public SeedEquivalenceTests()
    {
        _expected = ExpectedSeedModel.Load(_dataDirectory);
    }

    private ErewhonApiHost Host =>
        _host ?? throw new InvalidOperationException("The host is started by InitializeAsync.");

    /// <summary>
    /// Drops and recreates the test database, applies the migrations, and starts the Erewhon host on it with the seed
    /// data directory set to <c>Erewhon/DataFiles</c> and every log event collected.
    /// </summary>
    /// <returns>A task that completes when the host listens.</returns>
    public async Task InitializeAsync()
    {
        await _database.RecreateAsync();
        await _database.MigrateAsync();
        _host = await ErewhonApiHost.StartAsync(new ErewhonApiHostOptions
        {
            ConnectionString = _database.ConnectionString,
            LogSink = _logSink,
            Settings = new Dictionary<string, string?> { [SeedDataDirectoryKey] = _dataDirectory },
        });
    }

    /// <summary>Stops and disposes the host, then closes every pooled connection of the process.</summary>
    /// <returns>A task that completes when the host is disposed.</returns>
    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
            _host = null;
        }

        NpgsqlConnection.ClearAllPools();
    }

    /// <summary>
    /// After one load the table counts are the governing seed counts, each equal to the AAP value and to the model:
    /// 1 company, 6 countries, 27 regions, 8 agents, 20 clients, 3 categories, 15 commission rates, 24 agent-rate links,
    /// 58 retail items, 48 tender items, 100 tenders, 19 retail sales, 1 tender sale and 20 sales. The model holds 160
    /// tender lines, 60 of them rejected, on 17 closed tender items.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task Counts()
    {
        await LoadAsync();

        AssertSeedCounts(await CountTablesAsync());

        // C-02, Q-20: every tender line is read, 60 of 160 target the 17 closed items.
        Assert.Equal(160, _expected.TenderLines.Count);
        Assert.Equal(160, _expected.DataLineCounts[TendersFile]);
        Assert.Equal(60, _expected.TenderLines.Count(line => !line.Persisted));
        Assert.Equal(17, _expected.ClosedItemGuids.Count);
        Assert.All(
            _expected.TenderLines.Where(line => !line.Persisted),
            line => Assert.Contains(line.ItemGuid, _expected.ClosedItemGuids));
    }

    /// <summary>
    /// The money totals equal the AAP values and the model: retail prices 24,895,781.00, reserves 90,566,246.00, retail
    /// sale prices 9,540,238.95 and retail commissions 382,724.34. The single tender sale is on the "Castle" item with the
    /// winner GUID, to Philip Jenkins, for an offer of 6,500,931.00 with commission 227,532.59; the item's agent is
    /// Suzie Wu, whose Properties rate is 3.5.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task Totals()
    {
        await LoadAsync();
        SeedSnapshot snapshot = await ReadSnapshotAsync();

        AssertAmount(24_895_781.00m, _expected.RetailPriceSum, snapshot.RetailItems.Sum(item => item.Price));
        AssertAmount(90_566_246.00m, _expected.ReservePriceSum, snapshot.TenderItems.Sum(item => item.Price));
        AssertAmount(9_540_238.95m, _expected.RetailSalePriceTotal, snapshot.RetailSales.Sum(sale => sale.SalePrice));
        AssertAmount(382_724.34m, _expected.RetailCommissionTotal, snapshot.RetailSales.Sum(sale => sale.AgentCommission));

        ExpectedTenderSale expectedSale = Assert.Single(_expected.TenderSales);
        TenderSale tenderSale = Assert.Single(snapshot.TenderSales);

        // The awarded item: "Castle" with the winner GUID.
        Assert.Equal(WinnerItemGuid, expectedSale.ItemGuid);
        Item soldItem = Required(tenderSale.Item);
        Assert.Equal(WinnerItemGuid, soldItem.Guid);
        ExpectedItem expectedCastle = Assert.Single(_expected.TenderItems, item => item.Guid == WinnerItemGuid);
        Assert.Equal("Castle", expectedCastle.Name);
        Assert.Equal("Castle", soldItem.Name);

        // The winning tender: Philip Jenkins, 6,500,931.00; commission 227,532.59.
        Assert.Equal("Philip Jenkins", expectedSale.ClientName);
        Assert.Equal("Philip Jenkins", Required(tenderSale.Client).Name);
        AssertAmount(6_500_931.00m, expectedSale.Offer, Required(tenderSale.Tender).Offer);
        AssertAmount(227_532.59m, expectedSale.AgentCommission, tenderSale.AgentCommission);

        // The item's agent Suzie Wu and her Properties rate 3.5.
        TenderItem castle = Assert.Single(snapshot.TenderItems, item => item.Guid == WinnerItemGuid);
        Assert.Equal("Suzie Wu", expectedCastle.AgentName);
        Assert.Equal("Suzie Wu", Required(castle.Agent).Name);
        Assert.Equal("Properties", expectedCastle.CategoryName);
        Assert.Equal("Properties", Required(castle.ItemCategory).Name);

        ExpectedAgentRate expectedRate = Assert.Single(
            _expected.AgentRates,
            rate => rate.AgentName == "Suzie Wu" && rate.CategoryName == "Properties");
        AgentCommissionRate rateLink = Assert.Single(
            snapshot.AgentCommissionRates,
            link => Required(link.Agent).Name == "Suzie Wu"
                && Required(Required(link.CommissionRate).ItemCategory).Name == "Properties");
        AssertAmount(3.5m, expectedRate.Percentage, Required(rateLink.CommissionRate).Percentage);
    }

    /// <summary>
    /// Every loaded row equals its <see cref="ExpectedSeedModel"/> record, with every file date shifted by the one
    /// offset derived from the load: the company and its address; the country and region names and each region's
    /// country; each agent's and client's address; each category's prefix and description; the category rates and the
    /// agent-rate links; each item's type, code number, name, description, price, shifted dates, agent, region, country,
    /// category and photo bytes; the tenders; the retail sales; and the tender sale with its winning tender. Every
    /// generated GUID has the <see cref="ExpectedSeedModel.GeneratedGuidPattern"/> format and is distinct in its table.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task Content()
    {
        DateOnly before = await ServerDateAsync();
        await LoadAsync();
        DateOnly after = await ServerDateAsync();
        SeedSnapshot snapshot = await ReadSnapshotAsync();
        int offset = OffsetDays(before, after, snapshot.RetailItems);

        // Company: name and address.
        Company company = Assert.Single(snapshot.Companies);
        Assert.Equal(ExpectedSeedModel.Company.Name, company.Name);
        AssertAddress(ExpectedSeedModel.Company.Address, company.Address);

        // Countries and regions: names, and each region in its country.
        AssertSameElements(
            _expected.Countries.Select(country => country.Name),
            snapshot.Countries.Select(country => country.Name));
        Assert.All(snapshot.Countries, country => Assert.Equal(company.Id, country.CompanyId));
        AssertSameElements(
            _expected.Regions.Select(region => (region.CountryName, region.Name)),
            snapshot.Regions.Select(region => (Required(region.Country).Name, region.Name)));
        AssertSameElements(
            _expected.Countries.SelectMany(country => country.RegionNames.Select(region => (country.Name, region))),
            snapshot.Regions.Select(region => (Required(region.Country).Name, region.Name)));

        // Agents and clients: matched by name, the seven address fields.
        AssertParties(_expected.Agents, snapshot.Agents.Select(agent => (agent.Name, agent.Address)));
        AssertParties(_expected.Clients, snapshot.Clients.Select(client => (client.Name, client.Address)));

        // Categories: matched by name, upper-case prefix and description.
        AssertSameElements(
            _expected.Categories.Select(category => category.Name),
            snapshot.ItemCategories.Select(category => category.Name));
        foreach (ExpectedCategory expectedCategory in _expected.Categories)
        {
            ItemCategory category = Assert.Single(snapshot.ItemCategories, loaded => loaded.Name == expectedCategory.Name);
            Assert.Equal(expectedCategory.Prefix, category.Prefix);
            Assert.Equal(category.Prefix.ToUpperInvariant(), category.Prefix);
            Assert.Equal(expectedCategory.Description, category.Description);
        }

        // Commission rates: (category, percentage).
        AssertSameElements(
            _expected.CategoryRates.Select(rate => (rate.CategoryName, rate.Percentage)),
            snapshot.CommissionRates.Select(rate => (Required(rate.ItemCategory).Name, rate.Percentage)));

        // Agent-rate links: (agent, category, percentage), each on its rate's category.
        AssertSameElements(
            _expected.AgentRates.Select(link => (link.AgentName, link.CategoryName, link.Percentage)),
            snapshot.AgentCommissionRates.Select(link => (
                Required(link.Agent).Name,
                Required(Required(link.CommissionRate).ItemCategory).Name,
                Required(link.CommissionRate).Percentage)));
        Assert.All(
            snapshot.AgentCommissionRates,
            link => Assert.Equal(Required(link.CommissionRate).ItemCategoryId, link.ItemCategoryId));

        // Items: matched by the file GUID.
        List<ExpectedItem> expectedItems = [.. _expected.RetailItems, .. _expected.TenderItems];
        List<Item> loadedItems = [.. snapshot.RetailItems, .. snapshot.TenderItems];
        AssertSameElements(expectedItems.Select(item => item.Guid), loadedItems.Select(item => item.Guid));
        Dictionary<string, Item> itemsByGuid = loadedItems.ToDictionary(item => item.Guid, StringComparer.Ordinal);
        foreach (ExpectedItem expectedItem in expectedItems)
        {
            AssertItem(expectedItem, itemsByGuid[expectedItem.Guid], offset);
        }

        // Tenders: (item GUID, client, offer, shifted timestamp) as a multiset.
        AssertSameElements(
            _expected.PersistedTenders.Select(line => (
                line.ItemGuid,
                line.ClientName,
                line.Offer,
                Unspecified(line.Timestamp(offset)))),
            snapshot.Tenders.Select(tender => (
                Required(tender.TenderItem).Guid,
                Required(tender.Client).Name,
                tender.Offer,
                Unspecified(tender.DateTendered))));

        // Retail sales: matched by item GUID.
        AssertSameElements(
            _expected.RetailSales.Select(sale => sale.ItemGuid),
            snapshot.RetailSales.Select(sale => Required(sale.Item).Guid));
        Dictionary<string, RetailSale> retailSalesByItem = snapshot.RetailSales
            .ToDictionary(sale => Required(sale.Item).Guid, StringComparer.Ordinal);
        foreach (ExpectedRetailSale expectedSale in _expected.RetailSales)
        {
            RetailSale sale = retailSalesByItem[expectedSale.ItemGuid];
            Assert.IsType<RetailItem>(sale.Item);
            Assert.Equal(expectedSale.ClientName, Required(sale.Client).Name);
            Assert.Equal(expectedSale.SalePrice, sale.SalePrice);
            Assert.Equal(Unspecified(expectedSale.DateSold(offset)), Unspecified(sale.DateSold));
            Assert.Equal(expectedSale.AgentCommission, sale.AgentCommission);
        }

        // Tender sale: item, client, commission, and its winning tender.
        ExpectedTenderSale expectedTenderSale = Assert.Single(_expected.TenderSales);
        TenderSale tenderSale = Assert.Single(snapshot.TenderSales);
        Assert.Equal(expectedTenderSale.ItemGuid, Required(tenderSale.Item).Guid);
        Assert.Equal(expectedTenderSale.ClientName, Required(tenderSale.Client).Name);
        Assert.Equal(expectedTenderSale.AgentCommission, tenderSale.AgentCommission);
        Tender winningTender = Required(tenderSale.Tender);
        Assert.Equal(tenderSale.ItemId, winningTender.TenderItemId);
        Assert.Equal(expectedTenderSale.ClientName, Required(winningTender.Client).Name);
        Assert.Equal(expectedTenderSale.Offer, winningTender.Offer);
        Assert.Equal(
            Unspecified(expectedTenderSale.TenderTimestamp(offset)),
            Unspecified(winningTender.DateTendered));

        // EE-09, D-32: generated GUIDs of every non-item row and every address.
        AssertGeneratedGuids(snapshot.Companies.Select(row => row.Guid));
        AssertGeneratedGuids(snapshot.Countries.Select(row => row.Guid));
        AssertGeneratedGuids(snapshot.Regions.Select(row => row.Guid));
        AssertGeneratedGuids(snapshot.Agents.Select(row => row.Guid));
        AssertGeneratedGuids(snapshot.Clients.Select(row => row.Guid));
        AssertGeneratedGuids(snapshot.ItemCategories.Select(row => row.Guid));
        AssertGeneratedGuids(snapshot.CommissionRates.Select(row => row.Guid));
        AssertGeneratedGuids(snapshot.RetailSales.Select(row => row.Guid).Concat(snapshot.TenderSales.Select(row => row.Guid)));
        AssertGeneratedGuids(snapshot.Tenders.Select(row => row.Guid));
        AssertAddressGuids(snapshot.Companies.Select(row => row.Address));
        AssertAddressGuids(snapshot.Agents.Select(row => row.Address));
        AssertAddressGuids(snapshot.Clients.Select(row => row.Address));
    }

    /// <summary>
    /// After one load, a second load from a directory that holds only <c>Locations.txt</c> fails; afterwards no
    /// company, country, region, agent, item or sale exists.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task PurgeCommittedSeparately()
    {
        await LoadAsync();
        Assert.Equal(1, (await CountTablesAsync()).Companies);

        string partialDirectory = Path.Combine(
            Path.GetTempPath(),
            "erewhon-seed-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(partialDirectory);
        try
        {
            File.Copy(Path.Combine(_dataDirectory, LocationsFile), Path.Combine(partialDirectory, LocationsFile));

            await Assert.ThrowsAnyAsync<Exception>(() => LoadAsync(partialDirectory));

            // Q-18: purge persists after the failed load transaction.
            TableCounts counts = await CountTablesAsync();
            Assert.Equal(0, counts.Companies);
            Assert.Equal(0, counts.Countries);
            Assert.Equal(0, counts.Regions);
            Assert.Equal(0, counts.Agents);
            Assert.Equal(0, counts.Items);
            Assert.Equal(0, counts.Sales);
        }
        finally
        {
            Directory.Delete(partialDirectory, recursive: true);
        }
    }

    /// <summary>
    /// A load from a copy of the data directory whose first <paramref name="itemsFile"/> data line holds a 40-character
    /// GUID and names a photo file that does not exist fails with error 1035 on the <c>Guid</c> field, not with a
    /// missing-file error; afterwards no company or item exists.
    /// </summary>
    /// <param name="itemsFile">The item data file whose first data line is rewritten.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData(RetailItemsFile)]
    [InlineData(TenderItemsFile)]
    public async Task OverlongItemGuidFailsBeforePhotoRead(string itemsFile)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "erewhon-seed-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string file in Directory.EnumerateFiles(_dataDirectory))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }

            RewriteFirstItemLine(Path.Combine(directory, itemsFile), FieldLengths.Guid + 1, MissingPhotoFile);
            Assert.False(File.Exists(Path.Combine(directory, MissingPhotoFile)));

            // D-41: the GUID is checked where it is assigned, before the code number, the lookups and the photo.
            StringTooLongException exception =
                await Assert.ThrowsAsync<StringTooLongException>(() => LoadAsync(directory));
            Assert.Equal(1035, exception.ErrorCode);
            Assert.Equal("Guid", exception.FieldName);
            Assert.Equal(FieldLengths.Guid, exception.MaxLength);
            Assert.Equal(FieldLengths.Guid + 1, exception.ActualLength);

            TableCounts counts = await CountTablesAsync();
            Assert.Equal(0, counts.Companies);
            Assert.Equal(0, counts.Items);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The lines written to the output writer equal <see cref="SeedLoadResult.OutputLines"/> and follow the loader's
    /// sequence: the created company; per data file in load order a <c>Loading &lt;path&gt;...</c> line, its record
    /// lines and its summary line with the count and an elapsed time; the closing line; the tender sales built; and the
    /// database initialised line. Record lines that carry formatted numbers, dates or times are compared by their
    /// identifying fields (D-137). Each summary line and the tender sales line is also carried by a log event.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task ConsoleOutput()
    {
        (SeedLoadResult result, string output) = await LoadAsync();

        string[] written = output.Split(Environment.NewLine);
        Assert.Equal(string.Empty, written[^1]);
        string[] lines = written[..^1];
        Assert.Equal(lines, result.OutputLines);

        // File lines: one per data file, in load order.
        int[] fileLineIndexes = DataFileOrder.Select(fileName => FileLineIndex(lines, fileName)).ToArray();
        for (int index = 1; index < fileLineIndexes.Length; index++)
        {
            Assert.True(
                fileLineIndexes[index - 1] < fileLineIndexes[index],
                $"The {DataFileOrder[index]} line precedes the {DataFileOrder[index - 1]} line.");
        }

        string closingLine = MessageCatalog.Get(MessageKeys.ClosingTendersSever);
        Assert.Single(lines, line => line == closingLine);
        int closingIndex = Array.IndexOf(lines, closingLine);
        Assert.True(fileLineIndexes[^1] < closingIndex, "The closing line precedes the last data file line.");

        // The created company precedes the first file line.
        ExpectedAddress companyAddress = ExpectedSeedModel.Company.Address;
        string createdLine = MessageCatalog.Get(MessageKeys.Created) + Space + Join(
            ExpectedSeedModel.Company.Name,
            companyAddress.Street,
            companyAddress.City,
            companyAddress.Country,
            companyAddress.Email,
            companyAddress.Phone,
            companyAddress.Fax,
            companyAddress.WebSite);
        Assert.Equal(new[] { createdLine }, lines[..fileLineIndexes[0]]);

        var sections = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (int index = 0; index < DataFileOrder.Length; index++)
        {
            int end = index + 1 < fileLineIndexes.Length ? fileLineIndexes[index + 1] : closingIndex;
            sections[DataFileOrder[index]] = lines[(fileLineIndexes[index] + 1)..end];
        }

        List<string> summaryLines = [];
        summaryLines.Add(AssertLocationsSection(sections[LocationsFile]));
        summaryLines.Add(AssertAgentsSection(sections[AgentsFile]));
        summaryLines.Add(AssertClientsSection(sections[ClientsFile]));
        summaryLines.Add(AssertCategoriesSection(sections[CategoriesFile]));
        summaryLines.Add(AssertItemsSection(sections[RetailItemsFile], _expected.RetailItems, 58, MessageKeys.RetailItemsLoaded));
        summaryLines.Add(AssertItemsSection(sections[TenderItemsFile], _expected.TenderItems, 48, MessageKeys.TenderItemsLoaded));
        summaryLines.Add(AssertCategoryRatesSection(sections[CategoryRatesFile]));
        summaryLines.Add(AssertAgentRatesSection(sections[AgentRatesFile]));
        summaryLines.Add(AssertTendersSection(sections[TendersFile]));
        summaryLines.Add(AssertRetailSalesSection(sections[RetailSalesFile]));

        // Closure lines: the tender sales built, then the database initialised line, last.
        Assert.Equal(closingIndex + 3, lines.Length);
        string tenderSalesLine = lines[closingIndex + 1];
        AssertDurationLine(tenderSalesLine, MessageCatalog.Get(MessageKeys.TenderSalesBuilt, 1, Sentinel));
        AssertDurationLine(
            tenderSalesLine,
            MessageCatalog.Get(MessageKeys.TenderSalesBuilt, _expected.TenderSales.Count, Sentinel));
        summaryLines.Add(tenderSalesLine);

        (string initializedPrefix, string initializedSuffix) =
            SplitAtSentinel(MessageCatalog.Get(MessageKeys.DatabaseInitialized, Sentinel));
        string initializedLine = lines[closingIndex + 2];
        Assert.StartsWith(initializedPrefix, initializedLine, StringComparison.Ordinal);
        Assert.EndsWith(initializedSuffix, initializedLine, StringComparison.Ordinal);
        Assert.Matches(WholeSeconds, initializedLine[initializedPrefix.Length..^initializedSuffix.Length]);

        (string noTendersPrefix, _) = SplitAtSentinel(MessageCatalog.Get(MessageKeys.NoTendersToCloseAsAt, Sentinel));
        Assert.DoesNotContain(lines, line => line.StartsWith(noTendersPrefix, StringComparison.Ordinal));

        // Log sink: each summary line and the tender sales line in a log event.
        IReadOnlyList<LogEvent> events = _logSink.Events;
        Assert.All(summaryLines, line => Assert.Contains(events, logEvent => CarriesLine(logEvent, line)));
    }

    /// <summary>
    /// Over a loaded company, the CLI <c>seed &lt;dataDir&gt;</c> verb exits with 0 and leaves exactly one company, a
    /// new one, holding the seed counts once: 8 agents, 20 clients, 106 items, 100 tenders, 19 retail sales and 1 tender
    /// sale.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task ReloadOverExistingCompanySucceeds()
    {
        await LoadAsync();
        long firstCompanyId = Assert.Single(await CompanyIdsAsync());

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        int exitCode = await CommandLineVerbs.RunAsync(
            Host.Services,
            new[] { CommandLineVerbs.SeedVerb, _dataDirectory },
            writer);

        // B-80, D-43: the existing company is purged and a new one created, with no 500003.
        Assert.Equal(0, exitCode);
        long secondCompanyId = Assert.Single(await CompanyIdsAsync());
        Assert.NotEqual(firstCompanyId, secondCompanyId);
        Assert.Contains(
            MessageCatalog.Get(MessageKeys.Created) + Space + ExpectedSeedModel.Company.Name + FieldSeparator,
            writer.ToString(),
            StringComparison.Ordinal);

        AssertSeedCounts(await CountTablesAsync());
    }

    /// <summary>
    /// After one load, a load from an existing directory holding a copy of the data files fails with error 1035 on the
    /// <c>DataDirectory</c> field (at most 301 characters, 302 given) when the directory is named by its
    /// <paramref name="pathLength"/>-character path followed by <paramref name="suffix"/>, with
    /// <paramref name="padding"/> on both sides, so that the trimmed path followed by <c>/</c> unless it ends with
    /// <c>/</c> is 302 characters long; afterwards the loaded company is the only company and holds the seed counts.
    /// </summary>
    /// <param name="pathLength">The length of the directory's full path, without a trailing separator.</param>
    /// <param name="suffix">The text written after the path.</param>
    /// <param name="padding">The spaces written before and after the path.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData(301, "", "")]
    [InlineData(301, "/", " ")]
    public async Task OverlongDataDirectoryFailsBeforePurge(int pathLength, string suffix, string padding)
    {
        await LoadAsync();
        long companyId = Assert.Single(await CompanyIdsAsync());

        string root = Path.Combine(
            Path.GetTempPath(),
            "erewhon-seed-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        try
        {
            string directory = CreateDataFilesCopy(root, pathLength);

            // D-41: the 302-character directory path fails before the purge.
            StringTooLongException exception = await Assert.ThrowsAsync<StringTooLongException>(
                () => LoadAsync(padding + directory + suffix + padding));
            Assert.Equal(1035, exception.ErrorCode);
            Assert.Equal("DataDirectory", exception.FieldName);
            Assert.Equal(301, exception.MaxLength);
            Assert.Equal(302, exception.ActualLength);

            Assert.Equal(companyId, Assert.Single(await CompanyIdsAsync()));
            AssertSeedCounts(await CountTablesAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A load from an existing directory holding a copy of the data files loads the seed counts and builds one tender
    /// sale when the directory is named by its <paramref name="pathLength"/>-character path followed by
    /// <paramref name="suffix"/>, with <paramref name="padding"/> on both sides, so that the trimmed path followed by
    /// <c>/</c> unless it ends with <c>/</c> is 301 characters long.
    /// </summary>
    /// <param name="pathLength">The length of the directory's full path, without a trailing separator.</param>
    /// <param name="suffix">The text written after the path.</param>
    /// <param name="padding">The spaces written before and after the path.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData(300, "", "")]
    [InlineData(300, "/", "")]
    [InlineData(300, "", "  ")]
    public async Task DataDirectoryOf301CharactersLoads(int pathLength, string suffix, string padding)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "erewhon-seed-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        try
        {
            string directory = CreateDataFilesCopy(root, pathLength);

            (SeedLoadResult result, _) = await LoadAsync(padding + directory + suffix + padding);

            Assert.Equal(1, result.TenderSalesBuilt);
            AssertSeedCounts(await CountTablesAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The CLI <c>seed .</c> verb, run with a current directory whose full path is 400 characters long and holds a copy
    /// of the data files, exits with 0, writes the <c>Loading</c> line of <c>Locations.txt</c> under that full path and
    /// loads the seed counts. The current directory is restored afterwards.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task RelativeDataDirectoryInDeepCurrentDirectoryLoads()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "erewhon-seed-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        string previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(CreateDataFilesCopy(root, 400));
            string currentDirectory = Directory.GetCurrentDirectory();
            Assert.True(currentDirectory.Length > 301, currentDirectory);

            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            int exitCode = await CommandLineVerbs.RunAsync(
                Host.Services,
                new[] { CommandLineVerbs.SeedVerb, "." },
                writer);

            // D-41: the length check applies to the operand as given, not to its full path.
            Assert.Equal(0, exitCode);
            Assert.Contains(
                MessageCatalog.Get(MessageKeys.Loading, Path.Combine(currentDirectory, LocationsFile)),
                writer.ToString(),
                StringComparison.Ordinal);
            AssertSeedCounts(await CountTablesAsync());
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The post-load closure at the server date awards exactly one tender item: the winner GUID, whose shifted closure
    /// date is the load date. Every other due tender item stays unsold, and no tender item that is not due is sold.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task PostLoadClosureAtCurrentDate()
    {
        DateOnly before = await ServerDateAsync();
        (SeedLoadResult result, _) = await LoadAsync();
        DateOnly after = await ServerDateAsync();
        SeedSnapshot snapshot = await ReadSnapshotAsync();
        int offset = OffsetDays(before, after, snapshot.RetailItems);

        // B-67: one tender sale built.
        Assert.Equal(1, result.TenderSalesBuilt);
        Assert.Equal(_expected.TenderSales.Count, result.TenderSalesBuilt);

        ExpectedTenderSale expectedSale = Assert.Single(_expected.TenderSales);
        Assert.Equal(WinnerItemGuid, expectedSale.ItemGuid);
        TenderSale tenderSale = Assert.Single(snapshot.TenderSales);
        Assert.Equal(WinnerItemGuid, Required(tenderSale.Item).Guid);

        // Q-27: the winner closes on its closure day, the server date of the load.
        TenderItem winner = Assert.Single(snapshot.TenderItems, item => item.Guid == WinnerItemGuid);
        Assert.IsType<TenderSale>(winner.Sale);
        Assert.Equal(ExpectedSeedModel.OffsetStartDate.AddDays(offset), winner.ClosureDate);
        Assert.Contains(winner.ClosureDate, new[] { before, after });

        Assert.Contains(WinnerItemGuid, _expected.DueItemGuids);
        List<TenderItem> otherDueItems = snapshot.TenderItems
            .Where(item => item.Guid != WinnerItemGuid && _expected.DueItemGuids.Contains(item.Guid))
            .ToList();
        Assert.Equal(20, otherDueItems.Count);
        Assert.All(otherDueItems, item => Assert.Null(item.Sale));

        List<TenderItem> notDueItems = snapshot.TenderItems
            .Where(item => !_expected.DueItemGuids.Contains(item.Guid))
            .ToList();
        Assert.Equal(_expected.TenderItems.Count - _expected.DueItemGuids.Count, notDueItems.Count);
        Assert.All(notDueItems, item => Assert.Null(item.Sale));
    }

    /// <summary>
    /// A load from a copy of the data directory whose output writer throws on the first <c>Loading &lt;path&gt;...</c>
    /// line, the one for <c>Locations.txt</c>, fails with that writer's exception; afterwards the copy's
    /// <c>Locations.txt</c> is closed: it opens for read and write access shared with no other handle.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task DataFileClosedWhenLoadingLineFails()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "erewhon-seed-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string file in Directory.EnumerateFiles(_dataDirectory))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }

            (string loadingPrefix, _) = SplitAtSentinel(MessageCatalog.Get(MessageKeys.Loading, Sentinel));
            string locationsPath = Path.Combine(Path.GetFullPath(directory), LocationsFile);
            await using AsyncServiceScope scope = Host.Services.CreateAsyncScope();
            InitialDataLoader loader = scope.ServiceProvider.GetRequiredService<InitialDataLoader>();
            using var writer = new FailingLineWriter(loadingPrefix);

            Exception exception = await Assert.ThrowsAnyAsync<Exception>(
                () => loader.LoadAsync(directory, writer, CancellationToken.None));
            Assert.Same(writer.Failure, exception);
            Assert.Equal(MessageCatalog.Get(MessageKeys.Loading, locationsPath), writer.FailedLine);

            // D-114: the file opened before the failed Loading line is closed.
            using var exclusive = new FileStream(locationsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(exclusive.CanWrite);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A load from a directory that holds only <c>Locations.txt</c> fails with <see cref="FileNotFoundException"/> for
    /// <c>Agents.txt</c>; the output holds the <c>Loading &lt;path&gt;...</c> line of <c>Locations.txt</c>, none for
    /// <c>Agents.txt</c>, and ends with the <c>Database Initialized in &lt;n&gt; seconds</c> line.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task MissingDataFileWritesNoLoadingLine()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "erewhon-seed-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(Path.Combine(_dataDirectory, LocationsFile), Path.Combine(directory, LocationsFile));
            string fullDirectory = Path.GetFullPath(directory);
            await using AsyncServiceScope scope = Host.Services.CreateAsyncScope();
            InitialDataLoader loader = scope.ServiceProvider.GetRequiredService<InitialDataLoader>();
            using var writer = new StringWriter(CultureInfo.InvariantCulture);

            FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(
                () => loader.LoadAsync(directory, writer, CancellationToken.None));
            Assert.Equal(Path.Combine(fullDirectory, AgentsFile), exception.FileName);

            // D-114: each data file is opened before its Loading line is written.
            string[] lines = writer.ToString().Split(Environment.NewLine)[..^1];
            Assert.Contains(MessageCatalog.Get(MessageKeys.Loading, Path.Combine(fullDirectory, LocationsFile)), lines);
            Assert.DoesNotContain(MessageCatalog.Get(MessageKeys.Loading, Path.Combine(fullDirectory, AgentsFile)), lines);
            (string initializedPrefix, _) =
                SplitAtSentinel(MessageCatalog.Get(MessageKeys.DatabaseInitialized, Sentinel));
            Assert.StartsWith(initializedPrefix, lines[^1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A load from a directory that holds only <c>Locations.txt</c>, on a host where the count query of the
    /// <c>Agents.txt</c> end line and the rollback of the load transaction each fail with an exception whose message,
    /// inner <see cref="NpgsqlException"/> and data hold connection text, fails with the
    /// <see cref="FileNotFoundException"/> of <c>Agents.txt</c> and publishes no notification. The loader writes exactly
    /// two Warning events, one per clean-up failure, the end-line event with <c>SeedFile</c> = <c>Agents.txt</c>. Neither
    /// event holds the connection text or a failure message in its rendered message, its properties, its attached
    /// exception or its compact JSON form; the attached exception has the failure's type name as its message, no inner
    /// exception and no data, and renders the failure chain as type names.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task CleanupFailuresLoggedWithoutFailureText()
    {
        var faults = new SeedFaults { FailRollback = true };
        var logSink = new CollectingLogSink();
        var publisher = new RecordingNotificationPublisher();
        await using ErewhonApiHost host = await StartFaultingHostAsync(faults, logSink, publisher);

        string partialDirectory = Path.Combine(
            Path.GetTempPath(),
            "erewhon-seed-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(partialDirectory);
        try
        {
            File.Copy(Path.Combine(_dataDirectory, LocationsFile), Path.Combine(partialDirectory, LocationsFile));
            (string locationsEndPrefix, _) = SplitAtSentinel(
                MessageCatalog.Get(MessageKeys.LocationsLoaded, _expected.NumberOfLocations, Sentinel));
            using var writer = new ArmingLineWriter(locationsEndPrefix, faults.FailNextCommand);

            // D-114 (4), (5): the missing file's exception propagates past both clean-up failures.
            FileNotFoundException failure = await Assert.ThrowsAsync<FileNotFoundException>(
                () => LoadOnHostAsync(host, partialDirectory, writer, CancellationToken.None));
            Assert.EndsWith(AgentsFile, failure.FileName, StringComparison.Ordinal);
            Assert.True(writer.Armed, "The Locations.txt end line was not written.");
            Assert.False(faults.CommandFailurePending, "No database command ran after the Locations.txt end line.");
            Assert.Empty(publisher.EntityChanges);

            List<LogEvent> warnings = logSink.Events
                .Where(logEvent => logEvent.Level == LogEventLevel.Warning && IsFromCategory(logEvent, typeof(InitialDataLoader)))
                .ToList();
            Assert.Equal(2, warnings.Count);
            LogEvent endLineEvent = Assert.Single(warnings, logEvent => logEvent.MessageTemplate.Text == EndLineWarningTemplate);
            LogEvent rollbackEvent = Assert.Single(warnings, logEvent => logEvent.MessageTemplate.Text == RollbackWarningTemplate);
            Assert.Equal(new ScalarValue(AgentsFile), endLineEvent.Properties[SeedFileProperty]);
            Assert.DoesNotContain(SeedFileProperty, rollbackEvent.Properties.Keys);

            Assert.All(new[] { endLineEvent, rollbackEvent }, AssertCarriesNoFailureText);
        }
        finally
        {
            Directory.Delete(partialDirectory, recursive: true);
        }
    }

    /// <summary>
    /// When the token of a load is cancelled as the commit of the load transaction returns, the committed load's
    /// notifications are still published, among them <c>Company</c> Added for the loaded company and <c>Agent</c> Added
    /// for each of its 8 agents; the load then writes the closing line and fails with
    /// <see cref="OperationCanceledException"/> in the post-load closure, and the loaded rows stay committed with no
    /// tender sale built.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task NotificationsPublishedWhenCancelledAfterCommit()
    {
        var faults = new SeedFaults();
        var publisher = new RecordingNotificationPublisher();
        await using ErewhonApiHost host = await StartFaultingHostAsync(faults, new CollectingLogSink(), publisher);
        using var cancellation = new CancellationTokenSource();
        faults.CancelAfterNextCommit(cancellation);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LoadOnHostAsync(host, _dataDirectory, writer, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested, "The load transaction did not commit.");
        Assert.Contains(MessageCatalog.Get(MessageKeys.ClosingTendersSever), writer.ToString(), StringComparison.Ordinal);

        // B-64: the committed batch is published after the commit, whatever the load's token.
        long companyId = Assert.Single(await CompanyIdsAsync());
        IReadOnlyList<ChangeEvent> published = publisher.EntityChanges;
        Assert.Contains(
            published,
            change => change.EntityType == nameof(Company) && change.Id == companyId && change.Kind == ChangeKind.Added);
        Assert.Equal(8, published.Count(change => change.EntityType == nameof(Agent) && change.Kind == ChangeKind.Added));

        TableCounts counts = await CountTablesAsync();
        Assert.Equal(8, counts.Agents);
        Assert.Equal(20, counts.Clients);
        Assert.Equal(106, counts.Items);
        Assert.Equal(100, counts.Tenders);
        Assert.Equal(19, counts.RetailSales);
        Assert.Equal(0, counts.TenderSales);
    }

    /// <summary>
    /// Runs <see cref="InitialDataLoader.LoadAsync"/> in a new scope of the host over <paramref name="directory"/>, or
    /// over <c>Erewhon/DataFiles</c>, writing to a new string writer.
    /// </summary>
    private async Task<(SeedLoadResult Result, string Output)> LoadAsync(string? directory = null)
    {
        await using AsyncServiceScope scope = Host.Services.CreateAsyncScope();
        InitialDataLoader loader = scope.ServiceProvider.GetRequiredService<InitialDataLoader>();
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        SeedLoadResult result = await loader.LoadAsync(directory ?? _dataDirectory, writer, CancellationToken.None);
        return (result, writer.ToString());
    }

    /// <summary>
    /// Starts a second host on the test database, with the seed data directory set to <c>Erewhon/DataFiles</c> and every
    /// log event collected in <paramref name="logSink"/>, whose <see cref="IUnitOfWork"/> and database commands carry the
    /// faults of <paramref name="faults"/> and whose notification publisher is <paramref name="publisher"/>. The caller
    /// disposes it.
    /// </summary>
    private Task<ErewhonApiHost> StartFaultingHostAsync(
        SeedFaults faults,
        CollectingLogSink logSink,
        RecordingNotificationPublisher publisher) =>
        ErewhonApiHost.StartAsync(new ErewhonApiHostOptions
        {
            ConnectionString = _database.ConnectionString,
            LogSink = logSink,
            Settings = new Dictionary<string, string?> { [SeedDataDirectoryKey] = _dataDirectory },
            ConfigureServices = services =>
            {
                ServiceDescriptor unitOfWork = services.Single(descriptor => descriptor.ServiceType == typeof(IUnitOfWork));
                Type unitOfWorkType = unitOfWork.ImplementationType
                    ?? throw new InvalidOperationException("IUnitOfWork is not registered with an implementation type.");
                services.Replace(ServiceDescriptor.Describe(
                    typeof(IUnitOfWork),
                    provider => new FaultingUnitOfWork(
                        (IUnitOfWork)ActivatorUtilities.CreateInstance(provider, unitOfWorkType),
                        faults),
                    unitOfWork.Lifetime));

                ServiceDescriptor contextOptions = services.Single(
                    descriptor => descriptor.ServiceType == typeof(DbContextOptions<ErewhonDbContext>));
                Func<IServiceProvider, object> createContextOptions = contextOptions.ImplementationFactory
                    ?? throw new InvalidOperationException("The ErewhonDbContext options are not registered with a factory.");
                var interceptor = new FailingCommandInterceptor(faults);
                services.Replace(ServiceDescriptor.Describe(
                    typeof(DbContextOptions<ErewhonDbContext>),
                    provider => new DbContextOptionsBuilder<ErewhonDbContext>(
                            (DbContextOptions<ErewhonDbContext>)createContextOptions(provider))
                        .AddInterceptors(interceptor)
                        .Options,
                    contextOptions.Lifetime));

                services.Replace(ServiceDescriptor.Singleton<INotificationPublisher>(publisher));
            },
        });

    /// <summary>
    /// Runs <see cref="InitialDataLoader.LoadAsync"/> over <paramref name="directory"/> in a new scope of
    /// <paramref name="host"/>, writing to <paramref name="writer"/>.
    /// </summary>
    private static async Task<SeedLoadResult> LoadOnHostAsync(
        ErewhonApiHost host,
        string directory,
        TextWriter writer,
        CancellationToken ct)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        InitialDataLoader loader = scope.ServiceProvider.GetRequiredService<InitialDataLoader>();
        return await loader.LoadAsync(directory, writer, ct);
    }

    /// <summary>
    /// Returns an <see cref="InvalidOperationException"/> naming <paramref name="operation"/> whose message, inner
    /// <see cref="NpgsqlException"/> message and data each hold <see cref="ConnectionSentinel"/>.
    /// </summary>
    private static InvalidOperationException ConnectionFailure(string operation)
    {
        var failure = new InvalidOperationException(
            operation + " failed on " + ConnectionSentinel,
            new NpgsqlException("Failed to connect to " + ConnectionSentinel));
        failure.Data["ConnectionString"] = ConnectionSentinel;
        return failure;
    }

    /// <summary>Whether the log event's <c>SourceContext</c> is the full name of <paramref name="category"/>.</summary>
    private static bool IsFromCategory(LogEvent logEvent, Type category) =>
        logEvent.Properties.TryGetValue(SourceContextProperty, out LogEventPropertyValue? value)
        && value is ScalarValue { Value: string source }
        && source == category.FullName;

    /// <summary>
    /// The event's rendered message, property values, compact JSON form and attached exception hold neither
    /// <see cref="ConnectionSentinel"/> nor the text of a <see cref="ConnectionFailure"/>; the attached exception has the
    /// full name of <see cref="InvalidOperationException"/> as its message, no inner exception and no data, and its
    /// string form starts with that name and lists the inner <see cref="NpgsqlException"/> by type name.
    /// </summary>
    private static void AssertCarriesNoFailureText(LogEvent logEvent)
    {
        using var json = new StringWriter(CultureInfo.InvariantCulture);
        new CompactJsonFormatter().Format(logEvent, json);
        Exception attached = Assert.IsAssignableFrom<Exception>(logEvent.Exception);
        string[] texts =
        [
            logEvent.RenderMessage(CultureInfo.InvariantCulture),
            .. logEvent.Properties.Values.Select(value => value.ToString()),
            json.ToString(),
            attached.Message,
            attached.ToString(),
        ];
        Assert.All(texts, text =>
        {
            Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" failed on ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Failed to connect", text, StringComparison.Ordinal);
        });

        string failureType = typeof(InvalidOperationException).FullName!;
        Assert.Equal(failureType, attached.Message);
        Assert.Null(attached.InnerException);
        Assert.Empty(attached.Data);
        Assert.StartsWith(failureType, attached.ToString(), StringComparison.Ordinal);
        Assert.Contains(" ---> " + typeof(NpgsqlException).FullName, attached.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Rewrites the first data line of the Latin-1 item data file <paramref name="path"/>: its GUID token is padded with
    /// zeros to <paramref name="guidLength"/> characters and its photo token becomes <paramref name="photoFileName"/>.
    /// </summary>
    private static void RewriteFirstItemLine(string path, int guidLength, string photoFileName)
    {
        string[] lines = File.ReadAllLines(path, Encoding.Latin1);
        int index = Array.FindIndex(lines, line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'));
        string[] tokens = lines[index].Split('\t');
        tokens[ItemGuidToken] = tokens[ItemGuidToken].Trim().PadRight(guidLength, '0');
        tokens[ItemPhotoToken] = photoFileName;
        lines[index] = string.Join('\t', tokens);
        File.WriteAllLines(path, lines, Encoding.Latin1);
    }

    /// <summary>Reads the database server date in a new scope of the host.</summary>
    private async Task<DateOnly> ServerDateAsync()
    {
        await using AsyncServiceScope scope = Host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IServerClock>().GetServerDateAsync(CancellationToken.None);
    }

    /// <summary>Runs <paramref name="query"/> on the <see cref="ErewhonDbContext"/> of a new scope of the host.</summary>
    private async Task<T> QueryAsync<T>(Func<ErewhonDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = Host.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ErewhonDbContext>());
    }

    /// <summary>Reads every table, untracked, with the navigations the comparisons follow.</summary>
    private Task<SeedSnapshot> ReadSnapshotAsync() => QueryAsync(async db => new SeedSnapshot(
        Companies: await db.Companies.AsNoTracking().OrderBy(company => company.Id).ToListAsync(),
        Countries: await db.Countries.AsNoTracking().ToListAsync(),
        Regions: await db.Regions.AsNoTracking().Include(region => region.Country).ToListAsync(),
        Agents: await db.Agents.AsNoTracking().ToListAsync(),
        Clients: await db.Clients.AsNoTracking().ToListAsync(),
        ItemCategories: await db.ItemCategories.AsNoTracking().ToListAsync(),
        CommissionRates: await db.CommissionRates.AsNoTracking().Include(rate => rate.ItemCategory).ToListAsync(),
        AgentCommissionRates: await db.AgentCommissionRates
            .AsNoTracking()
            .Include(link => link.Agent)
            .Include(link => link.CommissionRate)
            .ThenInclude(rate => rate!.ItemCategory)
            .ToListAsync(),
        RetailItems: await db.Items
            .AsNoTracking()
            .OfType<RetailItem>()
            .Include(item => item.Agent)
            .Include(item => item.ItemCategory)
            .Include(item => item.Region)
            .ThenInclude(region => region!.Country)
            .Include(item => item.Sale)
            .ToListAsync(),
        TenderItems: await db.Items
            .AsNoTracking()
            .OfType<TenderItem>()
            .Include(item => item.Agent)
            .Include(item => item.ItemCategory)
            .Include(item => item.Region)
            .ThenInclude(region => region!.Country)
            .Include(item => item.Sale)
            .ToListAsync(),
        RetailSales: await db.Sales
            .AsNoTracking()
            .OfType<RetailSale>()
            .Include(sale => sale.Client)
            .Include(sale => sale.Item)
            .ToListAsync(),
        TenderSales: await db.Sales
            .AsNoTracking()
            .OfType<TenderSale>()
            .Include(sale => sale.Client)
            .Include(sale => sale.Item)
            .Include(sale => sale.Tender)
            .ThenInclude(tender => tender!.Client)
            .ToListAsync(),
        Tenders: await db.Tenders
            .AsNoTracking()
            .Include(tender => tender.Client)
            .Include(tender => tender.TenderItem)
            .ToListAsync()));

    /// <summary>Counts the rows of every table, and of each item and sale type.</summary>
    private Task<TableCounts> CountTablesAsync() => QueryAsync(async db => new TableCounts(
        Companies: await db.Companies.CountAsync(),
        Countries: await db.Countries.CountAsync(),
        Regions: await db.Regions.CountAsync(),
        Agents: await db.Agents.CountAsync(),
        Clients: await db.Clients.CountAsync(),
        ItemCategories: await db.ItemCategories.CountAsync(),
        CommissionRates: await db.CommissionRates.CountAsync(),
        AgentCommissionRates: await db.AgentCommissionRates.CountAsync(),
        Items: await db.Items.CountAsync(),
        RetailItems: await db.Items.OfType<RetailItem>().CountAsync(),
        TenderItems: await db.Items.OfType<TenderItem>().CountAsync(),
        Tenders: await db.Tenders.CountAsync(),
        Sales: await db.Sales.CountAsync(),
        RetailSales: await db.Sales.OfType<RetailSale>().CountAsync(),
        TenderSales: await db.Sales.OfType<TenderSale>().CountAsync()));

    /// <summary>Reads the company ids in ascending order.</summary>
    private Task<List<long>> CompanyIdsAsync() =>
        QueryAsync(db => db.Companies.OrderBy(company => company.Id).Select(company => company.Id).ToListAsync());

    /// <summary>
    /// Creates, under the existing directory <paramref name="root"/>, nested directories of names of at most 200
    /// characters whose innermost full path is exactly <paramref name="length"/> characters long, copies the files of
    /// <c>Erewhon/DataFiles</c> into the innermost one and returns its path, which ends with no separator.
    /// </summary>
    private string CreateDataFilesCopy(string root, int length)
    {
        const int MaxNameLength = 200;
        Assert.True(length - root.Length >= 2, root);

        string path = root;
        while (path.Length < length)
        {
            int remaining = length - path.Length - 1;
            int nameLength = Math.Min(MaxNameLength, remaining);
            if (remaining - nameLength == 1)
            {
                nameLength--;
            }

            path = path + Path.DirectorySeparatorChar + new string('d', nameLength);
        }

        Assert.Equal(length, path.Length);
        Directory.CreateDirectory(path);
        foreach (string file in Directory.EnumerateFiles(_dataDirectory))
        {
            File.Copy(file, Path.Combine(path, Path.GetFileName(file)));
        }

        return path;
    }

    /// <summary>
    /// Returns the day offset of the load: the first retail item's loaded listed date less its file listed date, which
    /// equals the server date read before or after the load less <see cref="ExpectedSeedModel.OffsetStartDate"/>.
    /// </summary>
    private int OffsetDays(DateOnly before, DateOnly after, IReadOnlyList<RetailItem> retailItems)
    {
        ExpectedItem first = _expected.RetailItems[0];
        RetailItem loaded = Assert.Single(retailItems, item => item.Guid == first.Guid);
        int offset = loaded.ListedDate.DayNumber - first.FileListedDate.DayNumber;
        Assert.Contains(
            offset,
            new[]
            {
                before.DayNumber - ExpectedSeedModel.OffsetStartDate.DayNumber,
                after.DayNumber - ExpectedSeedModel.OffsetStartDate.DayNumber,
            });
        return offset;
    }

    /// <summary>
    /// Returns the index of the single line <c>Loading &lt;data directory&gt;/&lt;file&gt;...</c>, which ends with
    /// <c>&lt;file&gt;...</c>.
    /// </summary>
    private int FileLineIndex(string[] lines, string fileName)
    {
        string fileLine = MessageCatalog.Get(MessageKeys.Loading, Path.Combine(_dataDirectory, fileName));
        Assert.EndsWith(fileName + "...", fileLine, StringComparison.Ordinal);
        Assert.Single(lines, line => line == fileLine);
        return Array.IndexOf(lines, fileLine);
    }

    /// <summary>Locations: one <c>Loaded &lt;country&gt;, &lt;region&gt;</c> line per region, then 33 locations.</summary>
    private string AssertLocationsSection(string[] section)
    {
        Assert.Equal(33, _expected.NumberOfLocations);
        return AssertSection(
            section,
            27,
            records => Assert.Equal(
                _expected.Regions.Select(region => MessageCatalog.Get(
                    MessageKeys.LoadedObject,
                    Join(region.CountryName, region.Name))),
                records),
            MessageCatalog.Get(MessageKeys.LocationsLoaded, _expected.NumberOfLocations, Sentinel));
    }

    /// <summary>
    /// Agents: one <c>Loading &lt;name&gt;, &lt;address&gt;...</c> line per agent and no <c>Loaded</c> line, then the
    /// agent count.
    /// </summary>
    private string AssertAgentsSection(string[] section)
    {
        (string loadingPrefix, _) = SplitAtSentinel(MessageCatalog.Get(MessageKeys.Loading, Sentinel));
        (string loadedPrefix, _) = SplitAtSentinel(MessageCatalog.Get(MessageKeys.LoadedObject, Sentinel));
        return AssertSection(
            section,
            8,
            records =>
            {
                Assert.Equal(
                    _expected.Agents.Select(agent => MessageCatalog.Get(MessageKeys.Loading, PartyFields(agent))),
                    records);
                Assert.StartsWith(loadingPrefix + "Peter Smallsmith" + FieldSeparator, records[0], StringComparison.Ordinal);
                Assert.DoesNotContain(records, line => line.StartsWith(loadedPrefix, StringComparison.Ordinal));
            },
            MessageCatalog.Get(MessageKeys.AgentsNumberLoadedInMS, _expected.Agents.Count, Sentinel));
    }

    /// <summary>Clients: one <c>Loaded &lt;name&gt;, &lt;address&gt;</c> line per client, then the client count.</summary>
    private string AssertClientsSection(string[] section) =>
        AssertSection(
            section,
            20,
            records => Assert.Equal(
                _expected.Clients.Select(client => MessageCatalog.Get(MessageKeys.LoadedObject, PartyFields(client))),
                records),
            MessageCatalog.Get(MessageKeys.ClientNumberLoadedInMS, _expected.Clients.Count, Sentinel));

    /// <summary>
    /// Categories: one <c>Loaded &lt;name&gt;, &lt;description&gt;</c> line per category, the first for Holidays, then
    /// the agent count.
    /// </summary>
    private string AssertCategoriesSection(string[] section)
    {
        // Q-19: the categories summary carries the agent count 8.
        Assert.Equal(8, _expected.Agents.Count);
        return AssertSection(
            section,
            3,
            records =>
            {
                Assert.Equal(
                    _expected.Categories.Select(category => MessageCatalog.Get(
                        MessageKeys.LoadedObject,
                        Join(category.Name, category.Description))),
                    records);
                Assert.Equal(
                    MessageCatalog.Get(MessageKeys.LoadedObject, "Holidays, Once in a lifetime holiday opportunities"),
                    records[0]);
            },
            MessageCatalog.Get(MessageKeys.CategoriesLoadedInMS, _expected.Agents.Count, Sentinel));
    }

    /// <summary>
    /// Retail or tender items: one <c>Loaded</c> line per item in file order, starting with its GUID, name,
    /// description and photo path and ending with its agent, country, region and category; then the item count.
    /// </summary>
    private string AssertItemsSection(
        string[] section,
        IReadOnlyList<ExpectedItem> items,
        int count,
        string summaryKey) =>
        AssertSection(
            section,
            count,
            records => Assert.All(
                records.Zip(items),
                pair =>
                {
                    (string record, ExpectedItem item) = pair;
                    string content = LoadedContent(record);
                    string start = Join(item.Guid, item.Name, item.Description) + FieldSeparator;
                    if (item.PhotoFileName is not null)
                    {
                        start += Path.Combine(_dataDirectory, item.PhotoFileName) + FieldSeparator;
                    }

                    Assert.StartsWith(start, content, StringComparison.Ordinal);
                    Assert.EndsWith(
                        FieldSeparator + Join(item.AgentName, item.CountryName, item.RegionName, item.CategoryName),
                        content,
                        StringComparison.Ordinal);
                }),
            MessageCatalog.Get(summaryKey, items.Count, Sentinel));

    /// <summary>Category rates: one <c>Loaded &lt;category&gt;, ...</c> line per rate, then the rate count.</summary>
    private string AssertCategoryRatesSection(string[] section) =>
        AssertSection(
            section,
            15,
            records => Assert.All(
                records.Zip(_expected.CategoryRates),
                pair => Assert.StartsWith(
                    pair.Second.CategoryName + FieldSeparator,
                    LoadedContent(pair.First),
                    StringComparison.Ordinal)),
            MessageCatalog.Get(MessageKeys.CategoryRatesLoadedInMS, _expected.CategoryRates.Count, Sentinel));

    /// <summary>
    /// Agent rates: one <c>Loaded &lt;agent&gt;, &lt;category&gt;, ...</c> line per link, then
    /// <c>24 Agent rates loaded in &lt;duration&gt;</c>.
    /// </summary>
    private string AssertAgentRatesSection(string[] section) =>
        AssertSection(
            section,
            24,
            records => Assert.All(
                records.Zip(_expected.AgentRates),
                pair => Assert.StartsWith(
                    Join(pair.Second.AgentName, pair.Second.CategoryName) + FieldSeparator,
                    LoadedContent(pair.First),
                    StringComparison.Ordinal)),
            _expected.AgentRates.Count.ToString(CultureInfo.InvariantCulture) + Space
                + MessageCatalog.Get(MessageKeys.AgentRatesLoadedInMS, Sentinel));

    /// <summary>
    /// Tenders: one <c>Loaded ..., &lt;client&gt;, &lt;item GUID&gt;</c> line per file line, then 160, every line
    /// counted.
    /// </summary>
    private string AssertTendersSection(string[] section) =>
        AssertSection(
            section,
            160,
            records => Assert.All(
                records.Zip(_expected.TenderLines),
                pair => Assert.EndsWith(
                    FieldSeparator + Join(pair.Second.ClientName, pair.Second.ItemGuid),
                    LoadedContent(pair.First),
                    StringComparison.Ordinal)),
            MessageCatalog.Get(MessageKeys.TendersLoaded, _expected.TenderLines.Count, Sentinel));

    /// <summary>
    /// Retail sales: one <c>Loaded ..., &lt;client&gt;, &lt;item GUID&gt;</c> line per sale, then the sale count.
    /// </summary>
    private string AssertRetailSalesSection(string[] section) =>
        AssertSection(
            section,
            19,
            records => Assert.All(
                records.Zip(_expected.RetailSales),
                pair => Assert.EndsWith(
                    FieldSeparator + Join(pair.Second.ClientName, pair.Second.ItemGuid),
                    LoadedContent(pair.First),
                    StringComparison.Ordinal)),
            MessageCatalog.Get(MessageKeys.RetailSalesLoaded, _expected.RetailSales.Count, Sentinel));

    /// <summary>
    /// A section holds <paramref name="recordCount"/> record lines, checked by <paramref name="assertRecords"/>, then
    /// its summary line, which matches <paramref name="summaryWithSentinel"/> with an elapsed time at the sentinel.
    /// Returns the summary line.
    /// </summary>
    private static string AssertSection(
        string[] section,
        int recordCount,
        Action<string[]> assertRecords,
        string summaryWithSentinel)
    {
        Assert.NotEmpty(section);
        string[] records = section[..^1];
        Assert.Equal(recordCount, records.Length);
        assertRecords(records);

        string summary = section[^1];
        AssertDurationLine(summary, summaryWithSentinel);
        return summary;
    }

    /// <summary>
    /// The line is <paramref name="formattedWithSentinel"/> with the sentinel replaced by <c>Less than 1 second</c> or
    /// <c>&lt;n&gt; Seconds</c>.
    /// </summary>
    private static void AssertDurationLine(string line, string formattedWithSentinel)
    {
        (string prefix, string suffix) = SplitAtSentinel(formattedWithSentinel);
        Assert.StartsWith(prefix, line, StringComparison.Ordinal);
        Assert.EndsWith(suffix, line, StringComparison.Ordinal);
        Assert.True(line.Length >= prefix.Length + suffix.Length, $"'{line}' is shorter than its fixed text.");
        Assert.Matches(Duration, line[prefix.Length..^suffix.Length]);
    }

    /// <summary>Returns the text before and after the sentinel in <paramref name="formatted"/>.</summary>
    private static (string Prefix, string Suffix) SplitAtSentinel(string formatted)
    {
        int at = formatted.IndexOf(Sentinel, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{formatted}' holds no sentinel.");
        return (formatted[..at], formatted[(at + Sentinel.Length)..]);
    }

    /// <summary>Returns the text a <c>Loaded &lt;text&gt;</c> line carries.</summary>
    private static string LoadedContent(string line)
    {
        (string prefix, string suffix) = SplitAtSentinel(MessageCatalog.Get(MessageKeys.LoadedObject, Sentinel));
        Assert.StartsWith(prefix, line, StringComparison.Ordinal);
        Assert.EndsWith(suffix, line, StringComparison.Ordinal);
        return line[prefix.Length..^suffix.Length];
    }

    /// <summary>
    /// Returns the party's name and address fields in output order: name, street, city, country, email, phone, fax,
    /// web site; a null field is empty.
    /// </summary>
    private static string PartyFields(ExpectedParty party) => Join(
        party.Name,
        party.Address.Street,
        party.Address.City,
        party.Address.Country,
        party.Address.Email,
        party.Address.Phone,
        party.Address.Fax,
        party.Address.WebSite);

    /// <summary>
    /// Whether the log event renders as the line, or as the line in double quotes, or holds the line as a scalar string
    /// property value.
    /// </summary>
    private static bool CarriesLine(LogEvent logEvent, string line)
    {
        string rendered = logEvent.RenderMessage(CultureInfo.InvariantCulture);
        return rendered == line
            || rendered == "\"" + line + "\""
            || logEvent.Properties.Values.Any(value => value is ScalarValue { Value: string text } && text == line);
    }

    /// <summary>The governing seed counts, each equal to the AAP value and to the model.</summary>
    private void AssertSeedCounts(TableCounts counts)
    {
        Assert.Equal(1, counts.Companies);
        AssertCount(6, _expected.Countries.Count, counts.Countries);
        AssertCount(27, _expected.Regions.Count, counts.Regions);
        AssertCount(8, _expected.Agents.Count, counts.Agents);
        AssertCount(20, _expected.Clients.Count, counts.Clients);

        // C-01: three categories.
        AssertCount(3, _expected.Categories.Count, counts.ItemCategories);
        AssertCount(15, _expected.CategoryRates.Count, counts.CommissionRates);
        AssertCount(24, _expected.AgentRates.Count, counts.AgentCommissionRates);
        AssertCount(58, _expected.RetailItems.Count, counts.RetailItems);
        AssertCount(48, _expected.TenderItems.Count, counts.TenderItems);
        AssertCount(106, _expected.RetailItems.Count + _expected.TenderItems.Count, counts.Items);

        // C-02: 100 tenders persisted.
        AssertCount(100, _expected.PersistedTenders.Count, counts.Tenders);
        AssertCount(19, _expected.RetailSales.Count, counts.RetailSales);
        AssertCount(1, _expected.TenderSales.Count, counts.TenderSales);
        AssertCount(20, _expected.SalesTotal, counts.Sales);
    }

    /// <summary>The model value and the loaded value both equal the AAP value.</summary>
    private static void AssertCount(int aapValue, int modelValue, int loadedValue)
    {
        Assert.Equal(aapValue, modelValue);
        Assert.Equal(aapValue, loadedValue);
    }

    /// <summary>The model amount and the loaded amount both equal the AAP amount.</summary>
    private static void AssertAmount(decimal aapValue, decimal modelValue, decimal loadedValue)
    {
        Assert.Equal(aapValue, modelValue);
        Assert.Equal(aapValue, loadedValue);
    }

    /// <summary>The seven address fields equal the expected address; an expected null string is a JADE null.</summary>
    private static void AssertAddress(ExpectedAddress expected, Address actual)
    {
        AssertJadeString(expected.Street, actual.Street);
        AssertJadeString(expected.City, actual.City);
        AssertJadeString(expected.Country, actual.Country);
        AssertJadeString(expected.Email, actual.Email);
        AssertJadeString(expected.Fax, actual.Fax);
        AssertJadeString(expected.Phone, actual.Phone);
        AssertJadeString(expected.WebSite, actual.WebSite);
    }

    /// <summary>The string equals <paramref name="expected"/>, or is null or empty when <paramref name="expected"/> is null.</summary>
    private static void AssertJadeString(string? expected, string? actual)
    {
        if (expected is null)
        {
            Assert.True(JadeValues.IsNull(actual), $"Expected a null string, found '{actual}'.");
        }
        else
        {
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>The loaded names equal the expected names, and each loaded address equals its expected address.</summary>
    private static void AssertParties(
        IReadOnlyList<ExpectedParty> expected,
        IEnumerable<(string Name, Address Address)> loaded)
    {
        List<(string Name, Address Address)> parties = loaded.ToList();
        AssertSameElements(expected.Select(party => party.Name), parties.Select(party => party.Name));

        Dictionary<string, Address> addressesByName = parties.ToDictionary(
            party => party.Name,
            party => party.Address,
            StringComparer.Ordinal);
        foreach (ExpectedParty party in expected)
        {
            AssertAddress(party.Address, addressesByName[party.Name]);
        }
    }

    /// <summary>
    /// The item's type, code number, name, description, price, shifted listed and closure dates, agent, region,
    /// country, category and photo bytes equal the expected item.
    /// </summary>
    private static void AssertItem(ExpectedItem expected, Item item, int offset)
    {
        if (expected.IsTender)
        {
            TenderItem tenderItem = Assert.IsType<TenderItem>(item);
            Assert.Equal(expected.ClosureDate(offset), (DateOnly?)tenderItem.ClosureDate);
        }
        else
        {
            Assert.IsType<RetailItem>(item);
        }

        Assert.Equal(expected.CodeNumber, item.CodeNumber);
        Assert.Equal(expected.Name, item.Name);
        Assert.Equal(expected.Description, item.Description);
        Assert.Equal(expected.Price, item.Price);
        Assert.Equal(expected.ListedDate(offset), item.ListedDate);
        Assert.Equal(expected.AgentName, Required(item.Agent).Name);

        Region region = Required(item.Region);
        Assert.Equal(expected.RegionName, region.Name);
        Assert.Equal(expected.CountryName, Required(region.Country).Name);
        Assert.Equal(expected.CategoryName, Required(item.ItemCategory).Name);

        if (expected.PhotoBytes is null)
        {
            Assert.Null(item.Photo);
        }
        else
        {
            Assert.Equal(expected.PhotoBytes, Required(item.Photo));
        }
    }

    /// <summary>Both sequences hold the same elements the same number of times, in any order.</summary>
    private static void AssertSameElements<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        where T : notnull
    {
        var remaining = new Dictionary<T, int>();
        foreach (T element in expected)
        {
            remaining[element] = remaining.GetValueOrDefault(element) + 1;
        }

        var unexpected = new List<T>();
        foreach (T element in actual)
        {
            if (remaining.TryGetValue(element, out int count) && count > 0)
            {
                remaining[element] = count - 1;
            }
            else
            {
                unexpected.Add(element);
            }
        }

        var missing = new List<T>();
        foreach ((T element, int count) in remaining)
        {
            missing.AddRange(Enumerable.Repeat(element, count));
        }

        Assert.Empty(missing);
        Assert.Empty(unexpected);
    }

    /// <summary>Every GUID matches <see cref="ExpectedSeedModel.GeneratedGuidPattern"/>, and no GUID repeats.</summary>
    private static void AssertGeneratedGuids(IEnumerable<string> guids)
    {
        List<string> values = guids.ToList();
        Assert.NotEmpty(values);
        Assert.All(values, guid => Assert.Matches(GeneratedGuid, guid));
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Every address GUID that is not a JADE null matches the generated format, and no GUID repeats.</summary>
    private static void AssertAddressGuids(IEnumerable<Address> addresses)
    {
        List<string> values = addresses
            .Select(address => address.Guid)
            .Where(guid => !JadeValues.IsNull(guid))
            .ToList();
        Assert.All(values, guid => Assert.Matches(GeneratedGuid, guid));
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Returns <paramref name="value"/> after asserting it is not null.</summary>
    private static T Required<T>(T? value)
        where T : class
    {
        Assert.NotNull(value);
        return value;
    }

    /// <summary>Returns the timestamp with <see cref="DateTimeKind.Unspecified"/>.</summary>
    private static DateTime Unspecified(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    /// <summary>Joins the fields with <c>", "</c>; a null field is empty.</summary>
    private static string Join(params string?[] fields) => string.Join(FieldSeparator, fields);

    /// <summary>
    /// A string writer that throws <see cref="Failure"/> for the first line written that starts with a prefix, and
    /// writes every other line.
    /// </summary>
    /// <param name="failingPrefix">The prefix of the line that fails.</param>
    private sealed class FailingLineWriter(string failingPrefix) : StringWriter(CultureInfo.InvariantCulture)
    {
        /// <summary>The exception thrown for the failing line.</summary>
        public IOException Failure { get; } = new("The output line could not be written.");

        /// <summary>The line that failed; <see langword="null"/> until a line fails.</summary>
        public string? FailedLine { get; private set; }

        /// <summary>
        /// Throws <see cref="Failure"/> when no line has failed yet and <paramref name="value"/> starts with the
        /// prefix; otherwise writes <paramref name="value"/> and a line end.
        /// </summary>
        /// <param name="value">The line to write.</param>
        public override void WriteLine(string? value)
        {
            if (FailedLine is null && value is not null && value.StartsWith(failingPrefix, StringComparison.Ordinal))
            {
                FailedLine = value;
                throw Failure;
            }

            base.WriteLine(value);
        }
    }

    /// <summary>The rows of every table, read untracked.</summary>
    private sealed record SeedSnapshot(
        IReadOnlyList<Company> Companies,
        IReadOnlyList<Country> Countries,
        IReadOnlyList<Region> Regions,
        IReadOnlyList<Agent> Agents,
        IReadOnlyList<Client> Clients,
        IReadOnlyList<ItemCategory> ItemCategories,
        IReadOnlyList<CommissionRate> CommissionRates,
        IReadOnlyList<AgentCommissionRate> AgentCommissionRates,
        IReadOnlyList<RetailItem> RetailItems,
        IReadOnlyList<TenderItem> TenderItems,
        IReadOnlyList<RetailSale> RetailSales,
        IReadOnlyList<TenderSale> TenderSales,
        IReadOnlyList<Tender> Tenders);

    /// <summary>The row count of every table, and of each item and sale type.</summary>
    private sealed record TableCounts(
        int Companies,
        int Countries,
        int Regions,
        int Agents,
        int Clients,
        int ItemCategories,
        int CommissionRates,
        int AgentCommissionRates,
        int Items,
        int RetailItems,
        int TenderItems,
        int Tenders,
        int Sales,
        int RetailSales,
        int TenderSales);

    /// <summary>
    /// The faults a host from <see cref="StartFaultingHostAsync"/> injects. A rollback fault applies to every rollback;
    /// the commit and command faults each apply once, to the next commit or database command after they are set.
    /// </summary>
    private sealed class SeedFaults
    {
        private CancellationTokenSource? _cancelAfterCommit;
        private int _failNextCommand;

        /// <summary>Whether every rollback throws a <see cref="ConnectionFailure"/> after rolling back.</summary>
        public bool FailRollback { get; init; }

        /// <summary>Whether a command fault is set and no database command has taken it yet.</summary>
        public bool CommandFailurePending => Volatile.Read(ref _failNextCommand) != 0;

        /// <summary>Makes the next commit cancel <paramref name="source"/> once it has committed.</summary>
        /// <param name="source">The source to cancel.</param>
        public void CancelAfterNextCommit(CancellationTokenSource source) =>
            Volatile.Write(ref _cancelAfterCommit, source);

        /// <summary>Makes the next database command throw a <see cref="ConnectionFailure"/> instead of running.</summary>
        public void FailNextCommand() => Volatile.Write(ref _failNextCommand, 1);

        /// <summary>Returns the source set by <see cref="CancelAfterNextCommit"/>, if any, and clears it.</summary>
        /// <returns>The source to cancel, or <see langword="null"/>.</returns>
        public CancellationTokenSource? TakeCancelAfterCommit() => Interlocked.Exchange(ref _cancelAfterCommit, null);

        /// <summary>Returns whether a command fault was set, and clears it.</summary>
        /// <returns><see langword="true"/> when the calling command fails.</returns>
        public bool TakeFailNextCommand() => Interlocked.Exchange(ref _failNextCommand, 0) != 0;
    }

    /// <summary>
    /// A unit of work that runs every call on <paramref name="inner"/>; after a commit it cancels the source taken from
    /// <see cref="SeedFaults.TakeCancelAfterCommit"/>, and after a rollback it throws a <see cref="ConnectionFailure"/>
    /// when <see cref="SeedFaults.FailRollback"/> is set.
    /// </summary>
    /// <param name="inner">The unit of work registered by the host.</param>
    /// <param name="faults">The faults to inject.</param>
    private sealed class FaultingUnitOfWork(IUnitOfWork inner, SeedFaults faults) : IUnitOfWork
    {
        /// <inheritdoc/>
        public bool HasActiveTransaction => inner.HasActiveTransaction;

        /// <inheritdoc/>
        public Task BeginAsync(CancellationToken ct = default) => inner.BeginAsync(ct);

        /// <inheritdoc/>
        public async Task CommitAsync(CancellationToken ct = default)
        {
            await inner.CommitAsync(ct);
            faults.TakeCancelAfterCommit()?.Cancel();
        }

        /// <inheritdoc/>
        public async Task RollbackAsync(CancellationToken ct = default)
        {
            await inner.RollbackAsync(ct);
            if (faults.FailRollback)
            {
                throw ConnectionFailure("Rollback");
            }
        }

        /// <inheritdoc/>
        public Task CreateSavepointAsync(string name, CancellationToken ct = default) =>
            inner.CreateSavepointAsync(name, ct);

        /// <inheritdoc/>
        public Task RollbackToSavepointAsync(string name, CancellationToken ct = default) =>
            inner.RollbackToSavepointAsync(name, ct);

        /// <inheritdoc/>
        public Task ReleaseSavepointAsync(string name, CancellationToken ct = default) =>
            inner.ReleaseSavepointAsync(name, ct);
    }

    /// <summary>
    /// A command interceptor that throws a <see cref="ConnectionFailure"/> in place of the reader, scalar or non-query
    /// command that takes the fault set by <see cref="SeedFaults.FailNextCommand"/>, and lets every other command run.
    /// </summary>
    /// <param name="faults">The faults to inject.</param>
    private sealed class FailingCommandInterceptor(SeedFaults faults) : DbCommandInterceptor
    {
        /// <inheritdoc/>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowWhenFaulted();
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        /// <inheritdoc/>
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            ThrowWhenFaulted();
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        /// <inheritdoc/>
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowWhenFaulted();
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        /// <summary>Throws a <see cref="ConnectionFailure"/> when this command takes the command fault.</summary>
        private void ThrowWhenFaulted()
        {
            if (faults.TakeFailNextCommand())
            {
                throw ConnectionFailure("Command");
            }
        }
    }

    /// <summary>
    /// A notification publisher that keeps every entity change handed to it, in order. A call whose token is cancelled
    /// returns a cancelled task and keeps nothing.
    /// </summary>
    private sealed class RecordingNotificationPublisher : INotificationPublisher
    {
        private readonly object _gate = new();
        private readonly List<ChangeEvent> _entityChanges = [];

        /// <summary>A snapshot of every entity change kept, in the order handed over.</summary>
        public IReadOnlyList<ChangeEvent> EntityChanges
        {
            get
            {
                lock (_gate)
                {
                    return _entityChanges.ToArray();
                }
            }
        }

        /// <inheritdoc/>
        public Task PublishEntityChangesAsync(IReadOnlyList<ChangeEvent> events, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(events);
            if (ct.IsCancellationRequested)
            {
                return Task.FromCanceled(ct);
            }

            lock (_gate)
            {
                _entityChanges.AddRange(events);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task PublishCartItemDeletedAsync(string sessionId, long itemId, CancellationToken ct = default) =>
            ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;

        /// <inheritdoc/>
        public Task PublishSaveOutstandingChangesAsync(
            string sessionId,
            string entityType,
            long id,
            CancellationToken ct = default) =>
            ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
    }

    /// <summary>
    /// A string writer that writes every line and calls <paramref name="arm"/> once, just after writing the first line
    /// that starts with <paramref name="armingPrefix"/>.
    /// </summary>
    /// <param name="armingPrefix">The prefix of the line that arms.</param>
    /// <param name="arm">The action called for that line.</param>
    private sealed class ArmingLineWriter(string armingPrefix, Action arm) : StringWriter(CultureInfo.InvariantCulture)
    {
        /// <summary>Whether a line starting with the prefix has been written.</summary>
        public bool Armed { get; private set; }

        /// <summary>
        /// Writes <paramref name="value"/> and a line end, then calls the arming action when no line has armed yet and
        /// <paramref name="value"/> starts with the prefix.
        /// </summary>
        /// <param name="value">The line to write.</param>
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (!Armed && value is not null && value.StartsWith(armingPrefix, StringComparison.Ordinal))
            {
                Armed = true;
                arm();
            }
        }
    }
}

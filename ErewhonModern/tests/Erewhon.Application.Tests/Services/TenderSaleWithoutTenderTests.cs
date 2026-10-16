using System.Globalization;
using Erewhon.Api.Errors;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Projections;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Application.Tests.Services;

/// <summary>
/// Characterises a tender sale whose winning tender is gone: <see cref="TenderSale.GetDate"/>,
/// <see cref="TenderSale.GetTime"/> and <see cref="TenderSale.GetDebugString"/> on in-memory tender sales, and the sales
/// list and the client details read through the production services on PostgreSQL after the winning tender of an award
/// is deleted through <see cref="TenderService"/>, against the fixture company of <see cref="ErewhonTestBase"/>.
/// </summary>
public sealed class TenderSaleWithoutTenderTests : ErewhonTestBase
{
    /// <summary>The offer of the winning tender of <see cref="ArrangeAwardThenDeleteTenderAsync"/>.</summary>
    private const decimal WinningOffer = 20.00m;

    /// <summary>The member of <see cref="TenderSale"/> called and the <see cref="TenderSale.TenderId"/> of the sale.</summary>
    public static TheoryData<string, long?> NoTenderCases => new()
    {
        { nameof(TenderSale.GetDate), null },
        { nameof(TenderSale.GetDate), 0L },
        { nameof(TenderSale.GetTime), null },
        { nameof(TenderSale.GetTime), 0L },
        { nameof(TenderSale.GetDebugString), null },
        { nameof(TenderSale.GetDebugString), 0L },
    };

    /// <summary>
    /// The member of <see cref="TenderSale"/> called, and its result for the tender sale 7 of "Proxy Tender" (CATI1) to
    /// "Clive Entworth" whose loaded winning tender offered 500 on 10 May 2024 at 09:15:00.250.
    /// </summary>
    public static TheoryData<string, string> LoadedTenderCases => new()
    {
        { nameof(TenderSale.GetDate), "2024-05-10" },
        { nameof(TenderSale.GetTime), "09:15:00.250" },
        { nameof(TenderSale.GetDebugString), "5784.7 TenderSale: $500.00, 10 May 2024, 09:15:00, CATI1 (Proxy Tender), Clive Entworth" },
    };

    private long CompanyId => Builder.MyCompany?.Id
        ?? throw new InvalidOperationException("The fixture company has not been created.");

    /// <summary>
    /// A tender sale with no tender and a <see cref="TenderSale.TenderId"/> of <see langword="null"/> or 0 →
    /// <paramref name="member"/> raises <see cref="NoCompanyException"/> 1090 "Null object reference", whose error
    /// outcome is the web exception body titled "Error 1090".
    /// </summary>
    /// <param name="member">The member called.</param>
    /// <param name="tenderId">The <see cref="TenderSale.TenderId"/> of the sale.</param>
    [Theory]
    [MemberData(nameof(NoTenderCases))]
    public void NoTenderRaises1090(string member, long? tenderId)
    {
        TenderSale sale = NewTenderSale(tenderId, null);

        NoCompanyException failure = Assert.Throws<NoCompanyException>(() => Call(sale, member));

        AssertNullObjectReference(failure);
    }

    /// <summary>
    /// A tender sale with the <see cref="TenderSale.TenderId"/> 101 and no loaded <see cref="TenderSale.Tender"/> →
    /// <paramref name="member"/> raises <see cref="InvalidOperationException"/> "TenderSale.Tender is not loaded.", not a
    /// <see cref="NoCompanyException"/>.
    /// </summary>
    /// <param name="member">The member called.</param>
    [Theory]
    [InlineData(nameof(TenderSale.GetDate))]
    [InlineData(nameof(TenderSale.GetTime))]
    [InlineData(nameof(TenderSale.GetDebugString))]
    public void UnloadedTenderRaisesInvalidOperation(string member)
    {
        TenderSale sale = NewTenderSale(101L, null);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => Call(sale, member));

        Assert.IsNotType<NoCompanyException>(failure);
        Assert.Equal("TenderSale.Tender is not loaded.", failure.Message);
    }

    /// <summary>
    /// A tender sale whose winning tender 101 is loaded → <paramref name="member"/> gives the tender's date, the
    /// tender's time or the debug string built from them.
    /// </summary>
    /// <param name="member">The member called.</param>
    /// <param name="expected">The result, the date as <c>yyyy-MM-dd</c> and the time as <c>HH:mm:ss.fff</c>.</param>
    [Theory]
    [MemberData(nameof(LoadedTenderCases))]
    public void LoadedTenderGivesItsDateAndTime(string member, string expected)
    {
        var tender = new Tender
        {
            Id = 101,
            Offer = 500m,
            DateTendered = new DateTime(2024, 5, 10, 9, 15, 0, 250, DateTimeKind.Unspecified),
        };
        TenderSale sale = NewTenderSale(tender.Id, tender);

        Assert.Equal(expected, Call(sale, member));
    }

    /// <summary>
    /// A tender item awarded at closure to the client's tender of 20, whose winning tender is then deleted through
    /// <see cref="TenderService"/>, which leaves the tender sale with no tender → the sales list of the bound company,
    /// of the client and of the item's agent each raise <see cref="NoCompanyException"/> 1090 "Null object reference",
    /// whose error outcome is the web exception body titled "Error 1090".
    /// </summary>
    /// <param name="showFor">Whose sales are listed.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData(SalesShowFor.Company)]
    [InlineData(SalesShowFor.Client)]
    [InlineData(SalesShowFor.Agent)]
    public async Task SalesListOfTenderSaleWithoutTenderRaises1090(SalesShowFor showFor)
    {
        AwardFixture fixture = await ArrangeAwardThenDeleteTenderAsync();
        long? id = showFor switch
        {
            SalesShowFor.Agent => fixture.Agent.Id,
            SalesShowFor.Client => fixture.Client.Id,
            _ => null,
        };

        await using AsyncServiceScope scope = NewBoundScope();
        RetailSaleService sales = scope.ServiceProvider.GetRequiredService<RetailSaleService>();
        NoCompanyException failure = await Assert.ThrowsAsync<NoCompanyException>(
            () => sales.GetSalesRowsAsync(showFor, id));

        AssertNullObjectReference(failure);
    }

    /// <summary>
    /// A tender item awarded at closure to the client's tender of 20, whose winning tender is then deleted through
    /// <see cref="TenderService"/> → the client, read by <see cref="ClientService.FindByIdAsync"/> with the tender sale,
    /// whose tender is <see langword="null"/>, projects its tender sales with <see cref="SalesRowProjection.From"/> by
    /// raising <see cref="NoCompanyException"/> 1090 "Null object reference", whose error outcome is the web exception
    /// body titled "Error 1090"; its retail sales project to no rows.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task ClientDetailsOfTenderSaleWithoutTenderRaises1090()
    {
        AwardFixture fixture = await ArrangeAwardThenDeleteTenderAsync();

        await using AsyncServiceScope scope = NewBoundScope();
        Client client = await scope.ServiceProvider.GetRequiredService<ClientService>().FindByIdAsync(fixture.Client.Id)
            ?? throw new InvalidOperationException($"Client {fixture.Client.Id} does not exist.");
        TenderSale tenderSale = Assert.Single(client.GetTenderSales());
        Assert.Equal(fixture.TenderSaleId, tenderSale.Id);
        Assert.Null(tenderSale.TenderId);
        Assert.Null(tenderSale.Tender);
        Assert.Empty(client.GetRetailSales().Select(SalesRowProjection.From));

        NoCompanyException failure = Assert.Throws<NoCompanyException>(
            () => client.GetTenderSales().Select(SalesRowProjection.From).ToList());

        AssertNullObjectReference(failure);
    }

    /// <summary>
    /// Asserts that <paramref name="failure"/> is error 1090 "Null object reference" and that its error outcome is of
    /// kind exception, titled <c>$BaseExceptionHeading</c> "Error 1090", with the body <c>$BaseExceptionBodyWeb</c> for
    /// that code and text.
    /// </summary>
    private static void AssertNullObjectReference(NoCompanyException failure)
    {
        Assert.Equal(1090, failure.ErrorCode);
        Assert.Equal("Null object reference", failure.Message);

        ErrorOutcome outcome = ErrorOutcomeMiddleware.Create(failure);
        Assert.Equal(ErrorOutcome.KindException, outcome.Kind);
        Assert.Equal("Error 1090", outcome.Heading);
        Assert.Equal(MessageCatalog.Get(MessageKeys.BaseExceptionHeading, "1090"), outcome.Heading);
        Assert.Equal(
            MessageCatalog.Get(
                MessageKeys.BaseExceptionBodyWeb,
                "1090",
                "Null object reference",
                MessageCatalog.Get(MessageKeys.BaseNotAvailable),
                string.Empty),
            outcome.Body);
        Assert.Contains("Error: Null object reference (1090)", outcome.Body);
    }

    /// <summary>
    /// Returns the tender sale 7 of the tender item "Proxy Tender" (category prefix <c>CATI</c>, code number 1) to the
    /// client "Clive Entworth", attached to no context, with <paramref name="tenderId"/> and <paramref name="tender"/>.
    /// </summary>
    private static TenderSale NewTenderSale(long? tenderId, Tender? tender) => new()
    {
        Id = 7,
        TenderId = tenderId,
        Tender = tender,
        Item = new TenderItem
        {
            Name = "Proxy Tender",
            CodeNumber = 1,
            ItemCategory = new ItemCategory { Prefix = "CATI" },
        },
        Client = new Client { Name = "Clive Entworth" },
    };

    /// <summary>
    /// Calls <paramref name="member"/> on <paramref name="sale"/> and returns its result: the date as
    /// <c>yyyy-MM-dd</c>, the time as <c>HH:mm:ss.fff</c>, or the debug string.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="member"/> names no member under test.</exception>
    private static string? Call(TenderSale sale, string member) => member switch
    {
        nameof(TenderSale.GetDate) => sale.GetDate()?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        nameof(TenderSale.GetTime) => sale.GetTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
        nameof(TenderSale.GetDebugString) => sale.GetDebugString(),
        _ => throw new ArgumentOutOfRangeException(nameof(member), member, "The member is not a member under test."),
    };

    /// <summary>
    /// Creates, through <see cref="Builder"/>, the agent "John Smith", the client "Clive Entworth", the country "France"
    /// and its region "Paris", the item category "Abstractions" (<c>ABST</c>), the tender item "Proxy Tender" closing on
    /// the server date and the client's tender of <see cref="WinningOffer"/> on it; closes tenders at the server date
    /// through <see cref="TenderClosureService"/> and asserts that the item is awarded through that tender and that the
    /// company's sales list holds its row, dated with the tender's date and showing its offer; then deletes the tender
    /// through <see cref="TenderService"/> and asserts that the tender sale is kept with no tender.
    /// </summary>
    private async Task<AwardFixture> ArrangeAwardThenDeleteTenderAsync()
    {
        DateOnly today = await GetService<IServerClock>().GetServerDateAsync();
        Agent agent = await Builder.CreateAgentAsync();
        Client client = await Builder.CreateClientAsync();
        Country country = await Builder.CreateCountryAsync();
        Region region = await Builder.CreateRegionAsync(country);
        ItemCategory category = await Builder.CreateItemCategoryAsync(
            "Abstractions", "ABST", "Items that do not model real world observables");
        TenderItem tenderItem = await Builder.CreateTenderItemAsync(region, category, agent);
        Tender tender = await Builder.CreateTenderAsync(client, tenderItem, WinningOffer);

        int awarded = await InNewScopeAsync(
            services => services.GetRequiredService<TenderClosureService>().CloseTendersAtDateAsync(today));
        Assert.Equal(1, awarded);

        TenderSale awardedSale = await ReadTenderSaleAsync(tenderItem.Id);
        Assert.Equal(tender.Id, awardedSale.TenderId);
        Assert.Equal(client.Id, awardedSale.ClientId);

        IReadOnlyList<SalesRowProjection> rows = await InNewScopeAsync(
            services => services.GetRequiredService<RetailSaleService>().GetSalesRowsAsync(SalesShowFor.Company, null));
        SalesRowProjection row = Assert.Single(rows);
        Assert.Equal(awardedSale.Id, row.SaleId);
        Assert.True(row.IsTender);
        Assert.Equal(DateOnly.FromDateTime(tender.DateTendered), row.SaleDate);
        Assert.Equal(WinningOffer, row.Offer);

        OperationResult deleted = await InNewScopeAsync(
            services => services.GetRequiredService<TenderService>().DeleteByIdAsync(tender.Id));
        Assert.True(deleted.Succeeded, deleted.FullErrorText);

        TenderSale keptSale = await ReadTenderSaleAsync(tenderItem.Id);
        Assert.Equal(awardedSale.Id, keptSale.Id);
        Assert.Null(keptSale.TenderId);
        Assert.Null(await InNewScopeAsync(services => services.GetRequiredService<TenderService>().FindByIdAsync(tender.Id)));

        return new AwardFixture(agent, client, awardedSale.Id);
    }

    /// <summary>Reads, untracked in a new scope, the tender sale of the tender item with <paramref name="tenderItemId"/>.</summary>
    private Task<TenderSale> ReadTenderSaleAsync(long tenderItemId) =>
        InNewScopeAsync(services => services.GetRequiredService<IErewhonDbContext>().Sales
            .AsNoTracking()
            .OfType<TenderSale>()
            .SingleAsync(sale => sale.ItemId == tenderItemId));

    /// <summary>Runs <paramref name="action"/> in a new scope with the fixture company bound.</summary>
    private async Task<T> InNewScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using AsyncServiceScope scope = NewBoundScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>Opens a new scope of the API with the fixture company bound.</summary>
    private AsyncServiceScope NewBoundScope()
    {
        AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(CompanyId);
        return scope;
    }

    /// <summary>
    /// The fixtures of <see cref="ArrangeAwardThenDeleteTenderAsync"/>: the agent of the tender item, the client it was
    /// awarded to and the row id of the tender sale.
    /// </summary>
    private sealed record AwardFixture(Agent Agent, Client Client, long TenderSaleId);
}

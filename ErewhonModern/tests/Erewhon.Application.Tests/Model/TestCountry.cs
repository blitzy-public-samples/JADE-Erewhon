using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Tests.Model;

/// <summary>
/// Ports the JADE <c>TestCountry</c> unit tests: country create, update and debug-string rules, and region creation
/// within a country, through <see cref="CountryService"/> and <see cref="RegionService"/> on PostgreSQL. Each test
/// starts with the company "Jade Software Corporation", bound to the test scope, and its country "France".
/// </summary>
public sealed class TestCountry : ErewhonTestBase
{
    private const string France = "France";
    private const string Russia = "Russia";

    /// <summary>The fixture country "France". Ports <c>myCountry</c>.</summary>
    private Country MyCountry =>
        Builder.MyCountry ?? throw new InvalidOperationException("The fixture country has not been created.");

    /// <summary>
    /// Creates two regions of the fixture country with one region command, initialised between the creates, and
    /// asserts that both regions exist and that the country holds exactly those two.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [Fact]
    public async Task testCreateRegion()
    {
        var regionService = GetService<RegionService>();
        var command = new RegionCommand { CountryId = MyCountry.Id, Name = "Normandy" };

        OperationResult firstResult = await regionService.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);
        Region? firstRegion = command.ModelObject;

        command.Initialize();

        // The second create names the region "Provence-Alpes-Côte d'Azur", as the JADE test does after initialize.
        command.CountryId = MyCountry.Id;
        command.Name = "Provence-Alpes-Côte d'Azur";
        OperationResult secondResult = await regionService.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);
        Region? secondRegion = command.ModelObject;

        Assert.True(firstResult.Succeeded, firstResult.FullErrorText);
        Assert.True(secondResult.Succeeded, secondResult.FullErrorText);
        Assert.NotNull(firstRegion);
        Assert.NotNull(secondRegion);

        IReadOnlyList<Region> regions = await regionService.GetRegionsAsync(MyCountry.Id);
        Assert.Equal(2, regions.Count);
        Assert.Contains(regions, region => region.Id == firstRegion.Id);
        Assert.Contains(regions, region => region.Id == secondRegion.Id);
    }

    /// <summary>
    /// Creates a second country named "France" and asserts that it fails with the single error
    /// <see cref="MessageKeys.CountryAlreadyExists"/>.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [Fact]
    public async Task testDuplicateCountry()
    {
        var command = new CountryCommand { Name = France };

        OperationResult result = await GetService<CountryService>()
            .ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);

        string error = Assert.Single(result.Errors);
        Assert.Equal(MessageCatalog.Get(MessageKeys.CountryAlreadyExists), error);
        Assert.False(result.Succeeded);
    }

    /// <summary>Asserts that the fixture country's debug string is its object identity followed by " Country: France".</summary>
    [Fact]
    public void testGetDebugString()
    {
        Assert.Equal(ObjectIdentity.Of(MyCountry) + " Country: France", MyCountry.GetDebugString());
    }

    /// <summary>
    /// Creates a country with a <see langword="null"/> name and asserts that it fails with the single error
    /// <see cref="MessageKeys.CountryNameRequired"/>.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [Fact]
    public async Task testInvalidName()
    {
        var command = new CountryCommand { Name = null };

        OperationResult result = await GetService<CountryService>()
            .ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);

        string error = Assert.Single(result.Errors);
        Assert.Equal(MessageCatalog.Get(MessageKeys.CountryNameRequired), error);
        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// Updates the fixture country's name to "Russia" in its own transaction and asserts the new name on the country
    /// and in the database.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [Fact]
    public async Task testUpdate()
    {
        var command = new CountryCommand();
        command.PopulateFromObject(MyCountry);
        command.Name = Russia;

        OperationResult result = await GetService<CountryService>()
            .ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.Equal(Russia, MyCountry.Name);
        Assert.Equal(Russia, await ReadCountryNameAsync(MyCountry.Id));
    }

    /// <summary>
    /// Updates the fixture country with its own name in its own transaction and asserts that the update succeeds and
    /// the name is unchanged on the country and in the database.
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [Fact]
    public async Task testUpdateCountryToSameName()
    {
        string name = MyCountry.Name;
        var command = new CountryCommand();
        command.PopulateFromObject(MyCountry);
        command.Name = name;

        OperationResult result = await GetService<CountryService>()
            .ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.Equal(name, MyCountry.Name);
        Assert.Equal(name, await ReadCountryNameAsync(MyCountry.Id));
    }

    /// <summary>
    /// Updates the fixture country's name to "Russia" in ambient mode with no active transaction and asserts that
    /// <see cref="NotInTransactionStateException"/> (1048) is raised and the stored name is still "France".
    /// </summary>
    /// <returns>A task that completes when the assertions have run.</returns>
    [Fact]
    public async Task testUpdateOutsideTransactionStateException()
    {
        var command = new CountryCommand();
        command.PopulateFromObject(MyCountry);
        command.Name = Russia;

        var exception = await Assert.ThrowsAsync<NotInTransactionStateException>(() =>
            GetService<CountryService>().ExecuteAsync(command, TransactionType.Persist, TransactionMode.Ambient));

        Assert.Equal(1048, exception.ErrorCode);
        Assert.Equal(France, await ReadCountryNameAsync(MyCountry.Id));
    }

    /// <summary>
    /// Builds the company of <see cref="ErewhonTestBase.SetUpAsync"/>, then the country "France".
    /// Ports <c>setUp</c>: <c>self.myCountry := self.createCountry()</c>.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();
        Builder.MyCountry = await Builder.CreateCountryAsync();
    }

    /// <summary>Returns the name stored in the database for the country with internal id <paramref name="countryId"/>.</summary>
    private Task<string> ReadCountryNameAsync(long countryId) =>
        GetService<IErewhonDbContext>()
            .Countries
            .AsNoTracking()
            .Where(country => country.Id == countryId)
            .Select(country => country.Name)
            .SingleAsync();
}

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
/// Ports the JADE <c>TestRegion</c> unit tests. Each test starts with a fresh company holding the country
/// "New Zealand" and its region "Christchurch", built through <see cref="CountryService"/> and
/// <see cref="RegionService"/>, and checks the region's names, developer description and self-documentation, and the
/// outcomes of creating and updating regions.
/// </summary>
public sealed class TestRegion : ErewhonTestBase
{
    private const string CountryName = "New Zealand";
    private const string RegionName = "Christchurch";
    private const string UpdatedName = "Waikato";

    private Country MyCountry =>
        Builder.MyCountry ?? throw new InvalidOperationException("The fixture country was not created.");

    private Region MyRegion =>
        Builder.MyRegion ?? throw new InvalidOperationException("The fixture region was not created.");

    /// <summary>
    /// Creates and binds the company as the base does, then creates the country "New Zealand" and its region
    /// "Christchurch" through <see cref="TestCompanyBuilder.CreateTestCountryAsync"/> and
    /// <see cref="TestCompanyBuilder.CreateTestRegionAsync"/>, which persist each with
    /// <see cref="TransactionType.Persist"/> in <see cref="TransactionMode.Own"/> mode. Ports <c>TestRegion.setUp</c>.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Country country = await Builder.CreateTestCountryAsync(CountryName);
        Builder.MyCountry = country;
        Builder.MyRegion = await Builder.CreateTestRegionAsync(RegionName, country);
    }

    /// <summary>The RTF self-documentation of the region has its name as heading and its debug string as body.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testDocumentSelfRTF()
    {
        string expected =
            @"{\rtf1\ansi\ansicpg1252\deff0\deflang5129{\fonttbl{\f0\fnil\fprq15\fcharset0 Arial;}{\f1\fnil\fprq8\fcharset0 Arial;}{\f2\fnil\fcharset0 MS Sans Serif;}}{\colortbl ;\red0\green0\blue128;\red0\green0\blue0;}\viewkind4\uc1\pard\cf1\b\f0\fs23 Christchurch\par\par\cf2\b0\f1\fs18 "
            + ObjectIdentity.Of(MyRegion)
            + @" Region: Christchurch of New Zealand\f2\fs17\par}";

        DocumentationDetails? details =
            await GetService<SelfDocumentationService>().GetDetailsAsync("Region", MyRegion.Id);

        Assert.NotNull(details);
        Assert.Equal(expected, details.Rtf);
    }

    /// <summary>The debug string is the region's OID text, " Region: ", its name, " of " and its country name.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetDebugString()
    {
        Region region = await LoadRegionWithCountryAsync(MyRegion.Id);

        Assert.Equal(ObjectIdentity.Of(region) + " Region: Christchurch of New Zealand", region.GetDebugString());
    }

    /// <summary>The country and region names are the country name, a space and the region name.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetNames()
    {
        Region region = await LoadRegionWithCountryAsync(MyRegion.Id);

        Assert.Equal("New Zealand Christchurch", region.GetCountryAndRegionNames());
    }

    /// <summary>A region without a country is not created and reports only that a country is required.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidCountry()
    {
        const string name = "High Seas";
        var command = new RegionCommand { Name = name };

        OperationResult result =
            await GetService<RegionService>().ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);

        Assert.False(result.Succeeded);
        Assert.Null(command.ModelObject);
        Assert.Equal(MessageCatalog.Get(MessageKeys.CountryRequired), Assert.Single(result.Errors));
        Assert.Equal(0, await CountCompanyRegionsNamedAsync(name));
    }

    /// <summary>A region without a name is not created and reports only that the region name is required.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidName()
    {
        var command = new RegionCommand { Name = null, CountryId = MyCountry.Id };

        OperationResult result =
            await GetService<RegionService>().ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);

        Assert.False(result.Succeeded);
        Assert.Null(command.ModelObject);
        Assert.Equal(MessageCatalog.Get(MessageKeys.RegionNameRequired), Assert.Single(result.Errors));
        Assert.Equal(1, await CountCountryRegionsAsync(MyCountry.Id));
    }

    /// <summary>
    /// A second region with the name of an existing region of the same country is not created and reports only that
    /// the region already exists.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUniqueRegion()
    {
        const string name = "West Coast";
        RegionService service = GetService<RegionService>();
        var command = new RegionCommand { CountryId = MyCountry.Id, Name = name };

        OperationResult first = await service.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);
        Assert.True(first.Succeeded, first.FullErrorText);

        command.Initialize();
        command.CountryId = MyCountry.Id;
        command.Name = name;
        OperationResult result = await service.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);

        Assert.False(result.Succeeded);
        Assert.Null(command.ModelObject);
        Assert.Equal(MessageCatalog.Get(MessageKeys.RegionExists), Assert.Single(result.Errors));
        Assert.Equal(1, await CountCompanyRegionsNamedAsync(name));
    }

    /// <summary>Renaming the region in its own transaction persists the new name.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdate()
    {
        var command = new RegionCommand();
        command.PopulateFromObject(MyRegion);
        command.Name = UpdatedName;

        OperationResult result =
            await GetService<RegionService>().ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);
        Assert.Equal(UpdatedName, await ReadStoredRegionNameAsync(MyRegion.Id));
    }

    /// <summary>
    /// Renaming the region in Ambient mode with no active transaction raises error 1048 and leaves the region's name
    /// unchanged.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdateOutsideTransStateException()
    {
        var command = new RegionCommand();
        command.PopulateFromObject(MyRegion);
        string initialName = MyRegion.Name;
        command.Name = UpdatedName;

        NotInTransactionStateException exception = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => GetService<RegionService>().ExecuteAsync(command, TransactionType.Persist, TransactionMode.Ambient));

        Assert.Equal(1048, exception.ErrorCode);
        Assert.Equal(initialName, MyRegion.Name);
        Assert.Equal(initialName, await ReadStoredRegionNameAsync(MyRegion.Id));
    }

    /// <summary>Returns the region with internal id <paramref name="regionId"/> with its country loaded.</summary>
    private async Task<Region> LoadRegionWithCountryAsync(long regionId)
    {
        Region? region = await GetService<RegionService>().FindByIdAsync(regionId);
        Assert.NotNull(region);
        return region;
    }

    /// <summary>Returns the stored name of the region with internal id <paramref name="regionId"/>.</summary>
    private Task<string> ReadStoredRegionNameAsync(long regionId) =>
        GetService<IErewhonDbContext>().Regions
            .AsNoTracking()
            .Where(region => region.Id == regionId)
            .Select(region => region.Name)
            .SingleAsync();

    /// <summary>Returns the number of stored regions of the country with internal id <paramref name="countryId"/>.</summary>
    private Task<int> CountCountryRegionsAsync(long countryId) =>
        GetService<IErewhonDbContext>().Regions
            .AsNoTracking()
            .CountAsync(region => region.CountryId == countryId);

    /// <summary>Returns the number of stored regions named <paramref name="name"/> in the fixture company.</summary>
    private Task<int> CountCompanyRegionsNamedAsync(string name)
    {
        long companyId = Builder.MyCompany?.Id
            ?? throw new InvalidOperationException("The fixture company was not created.");

        return GetService<IErewhonDbContext>().Regions
            .AsNoTracking()
            .CountAsync(region => region.Country!.CompanyId == companyId && region.Name == name);
    }
}

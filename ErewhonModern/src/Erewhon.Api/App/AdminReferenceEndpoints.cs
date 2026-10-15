using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Projections;
using Erewhon.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erewhon.Api.App;

/// <summary>
/// Maps the <c>/api/app/admin</c> reference-data endpoints, the back end of the company editor and of the commission
/// rate, item category, country and region lists and editors: reading and updating the bound company, and listing,
/// reading, creating, updating and deleting commission rates, item categories, countries and regions.
/// </summary>
public static class AdminReferenceEndpoints
{
    private const string Tag = "App: Admin reference";

    /// <summary>
    /// Maps, under <c>/admin</c> on <paramref name="api"/>:
    /// <list type="bullet">
    /// <item><description><c>GET /admin/company</c>: the bound company as a <see cref="CompanyProjection"/>.</description></item>
    /// <item><description><c>PUT /admin/company</c>: update the bound company's name and address from a
    /// <see cref="CompanyRequest"/> and return the <see cref="OperationResult"/>.</description></item>
    /// <item><description><c>GET /admin/commission-rates?category={id}</c>: the commission rates of one item category
    /// in percentage order, or of every category of the bound company in category name and percentage order, as
    /// <see cref="CommissionRateRow"/> values; 404 for an unknown category.</description></item>
    /// <item><description><c>GET /admin/commission-rates/{id}</c>: one commission rate as a
    /// <see cref="CommissionRateRow"/>; 404 when no rate has the id.</description></item>
    /// <item><description><c>POST /admin/commission-rates</c>, <c>PUT /admin/commission-rates/{id}</c> and
    /// <c>DELETE /admin/commission-rates/{id}</c>: create, update or delete a commission rate with the agents holding
    /// it, and return the <see cref="OperationResult"/>; 404 when no rate has the id.</description></item>
    /// <item><description><c>GET /admin/item-categories</c> and <c>GET /admin/item-categories/{id}</c>: the item
    /// categories of the bound company in name order, or one category, as <see cref="ItemCategoryRow"/> values; 404
    /// when no category has the id.</description></item>
    /// <item><description><c>POST /admin/item-categories</c>, <c>PUT /admin/item-categories/{id}</c> and
    /// <c>DELETE /admin/item-categories/{id}</c>: create, update or delete an item category, and return the
    /// <see cref="OperationResult"/>; 404 when no category has the id.</description></item>
    /// <item><description><c>GET /admin/countries</c> and <c>GET /admin/countries/{id}</c>: the countries of the bound
    /// company in name order, or one country, each with its regions in name order, as <see cref="CountryRow"/>
    /// values; 404 when no country has the id.</description></item>
    /// <item><description><c>POST /admin/countries</c>, <c>PUT /admin/countries/{id}</c> and
    /// <c>DELETE /admin/countries/{id}</c>: create, update or delete a country, and return the
    /// <see cref="OperationResult"/>; 404 when no country has the id.</description></item>
    /// <item><description><c>GET /admin/regions?country={id}</c> and <c>GET /admin/regions/{id}</c>: the regions of one
    /// country in name order, or of every country of the bound company in country name and region name order, or one
    /// region, as <see cref="RegionRow"/> values; 404 for an unknown country or region.</description></item>
    /// <item><description><c>POST /admin/regions</c>, <c>PUT /admin/regions/{id}</c> and
    /// <c>DELETE /admin/regions/{id}</c>: create, update or delete a region, and return the
    /// <see cref="OperationResult"/>; 404 when no region has the id.</description></item>
    /// </list>
    /// </summary>
    /// <param name="api">The <c>/api/app</c> route group.</param>
    /// <returns><paramref name="api"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is <see langword="null"/>.</exception>
    public static RouteGroupBuilder MapAdminReferenceEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var admin = api.MapGroup("/admin");

        MapCompanyEndpoints(admin);
        MapCommissionRateEndpoints(admin);
        MapItemCategoryEndpoints(admin);
        MapCountryEndpoints(admin);
        MapRegionEndpoints(admin);

        return api;
    }

    /// <summary>Maps <c>GET</c> and <c>PUT /admin/company</c>.</summary>
    private static void MapCompanyEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/company", GetCompanyAsync)
            .WithName("AppAdminCompanyGet")
            .WithTags(Tag)
            .WithSummary("Returns the bound company with the seven fields of its address.")
            .Produces<CompanyProjection>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPut("/company", UpdateCompanyAsync)
            .WithName("AppAdminCompanyUpdate")
            .WithTags(Tag)
            .WithSummary("Updates the bound company's name and address and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);
    }

    /// <summary>Maps the <c>/admin/commission-rates</c> list, read, create, update and delete routes.</summary>
    private static void MapCommissionRateEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/commission-rates", GetCommissionRatesAsync)
            .WithName("AppAdminCommissionRateList")
            .WithTags(Tag)
            .WithSummary(
                "Returns the commission rates of one item category in percentage order, or of every category in "
                + "category name and percentage order, each with its agents in name order.")
            .Produces<IReadOnlyList<CommissionRateRow>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapGet("/commission-rates/{id:long}", GetCommissionRateAsync)
            .WithName("AppAdminCommissionRateGet")
            .WithTags(Tag)
            .WithSummary("Returns one commission rate with its item category and its agents in name order.")
            .Produces<CommissionRateRow>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPost("/commission-rates", CreateCommissionRateAsync)
            .WithName("AppAdminCommissionRateCreate")
            .WithTags(Tag)
            .WithSummary("Creates a commission rate held by the listed agents and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPut("/commission-rates/{id:long}", UpdateCommissionRateAsync)
            .WithName("AppAdminCommissionRateUpdate")
            .WithTags(Tag)
            .WithSummary(
                "Updates a commission rate's item category, percentage and, when listed, the agents holding it, and "
                + "returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapDelete("/commission-rates/{id:long}", DeleteCommissionRateAsync)
            .WithName("AppAdminCommissionRateDelete")
            .WithTags(Tag)
            .WithSummary("Deletes a commission rate, with its agent links, and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);
    }

    /// <summary>Maps the <c>/admin/item-categories</c> list, read, create, update and delete routes.</summary>
    private static void MapItemCategoryEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/item-categories", GetItemCategoriesAsync)
            .WithName("AppAdminItemCategoryList")
            .WithTags(Tag)
            .WithSummary("Returns the item categories of the bound company in name order.")
            .Produces<IReadOnlyList<ItemCategoryRow>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapGet("/item-categories/{id:long}", GetItemCategoryAsync)
            .WithName("AppAdminItemCategoryGet")
            .WithTags(Tag)
            .WithSummary("Returns one item category with its name, prefix and description.")
            .Produces<ItemCategoryRow>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPost("/item-categories", CreateItemCategoryAsync)
            .WithName("AppAdminItemCategoryCreate")
            .WithTags(Tag)
            .WithSummary("Creates an item category and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPut("/item-categories/{id:long}", UpdateItemCategoryAsync)
            .WithName("AppAdminItemCategoryUpdate")
            .WithTags(Tag)
            .WithSummary("Updates an item category's name, prefix and description and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapDelete("/item-categories/{id:long}", DeleteItemCategoryAsync)
            .WithName("AppAdminItemCategoryDelete")
            .WithTags(Tag)
            .WithSummary(
                "Deletes an item category, with its commission rates, its items and their sales and tenders, and "
                + "returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);
    }

    /// <summary>Maps the <c>/admin/countries</c> list, read, create, update and delete routes.</summary>
    private static void MapCountryEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/countries", GetCountriesAsync)
            .WithName("AppAdminCountryList")
            .WithTags(Tag)
            .WithSummary("Returns the countries of the bound company in name order, each with its regions in name order.")
            .Produces<IReadOnlyList<CountryRow>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapGet("/countries/{id:long}", GetCountryAsync)
            .WithName("AppAdminCountryGet")
            .WithTags(Tag)
            .WithSummary("Returns one country with its regions in name order.")
            .Produces<CountryRow>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPost("/countries", CreateCountryAsync)
            .WithName("AppAdminCountryCreate")
            .WithTags(Tag)
            .WithSummary("Creates a country and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPut("/countries/{id:long}", UpdateCountryAsync)
            .WithName("AppAdminCountryUpdate")
            .WithTags(Tag)
            .WithSummary("Updates a country's name and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapDelete("/countries/{id:long}", DeleteCountryAsync)
            .WithName("AppAdminCountryDelete")
            .WithTags(Tag)
            .WithSummary(
                "Deletes a country, with its regions, clears the region of the items listed in them, and returns the "
                + "operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);
    }

    /// <summary>Maps the <c>/admin/regions</c> list, read, create, update and delete routes.</summary>
    private static void MapRegionEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/regions", GetRegionsAsync)
            .WithName("AppAdminRegionList")
            .WithTags(Tag)
            .WithSummary(
                "Returns the regions of one country in name order, or of every country of the bound company in country "
                + "name and region name order.")
            .Produces<IReadOnlyList<RegionRow>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapGet("/regions/{id:long}", GetRegionAsync)
            .WithName("AppAdminRegionGet")
            .WithTags(Tag)
            .WithSummary("Returns one region with its country.")
            .Produces<RegionRow>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPost("/regions", CreateRegionAsync)
            .WithName("AppAdminRegionCreate")
            .WithTags(Tag)
            .WithSummary("Creates a region of a country and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPut("/regions/{id:long}", UpdateRegionAsync)
            .WithName("AppAdminRegionUpdate")
            .WithTags(Tag)
            .WithSummary("Updates a region's name and country and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapDelete("/regions/{id:long}", DeleteRegionAsync)
            .WithName("AppAdminRegionDelete")
            .WithTags(Tag)
            .WithSummary(
                "Deletes a region, clears the region of the items listed in it, and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);
    }

    /// <summary>Returns the bound company as its projection.</summary>
    private static async Task<Ok<CompanyProjection>> GetCompanyAsync(
        [FromServices] CompanyService companyService,
        CancellationToken ct)
    {
        Company company = await companyService.GetCompanyAsync(ct);
        return TypedResults.Ok(CompanyProjection.From(company));
    }

    /// <summary>
    /// Updates the bound company from the request: its name, and its address when the request carries one, keeping the
    /// address GUID; returns the operation result.
    /// </summary>
    private static async Task<Ok<OperationResult>> UpdateCompanyAsync(
        [FromBody] CompanyRequest request,
        [FromServices] CompanyService companyService,
        CancellationToken ct)
    {
        Company company = await companyService.GetCompanyAsync(ct);

        var command = new CompanyCommand();
        command.PopulateFromObject(company);
        command.Name = request.Name;
        command.Address = ToAddressCommand(request.Address, command.Address);

        OperationResult result = await companyService.ExecuteAsync(command, TransactionType.Persist, cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Returns the commission rates of the category with row id <paramref name="category"/>, or of every category of the
    /// bound company when no category is given, as rows; 404 when no category has that id.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<CommissionRateRow>>, NotFound>> GetCommissionRatesAsync(
        [FromQuery(Name = "category")] long? category,
        [FromServices] CommissionRateService commissionRateService,
        [FromServices] ItemCategoryService itemCategoryService,
        CancellationToken ct)
    {
        if (category is long categoryId && await itemCategoryService.FindByIdAsync(categoryId, ct) is null)
        {
            return TypedResults.NotFound();
        }

        IReadOnlyList<CommissionRate> rates = await commissionRateService.GetCommissionRatesAsync(category, ct);
        return TypedResults.Ok(ToRows(rates, ToRow));
    }

    /// <summary>Returns the commission rate with row id <paramref name="id"/> as a row, or 404 when none has that id.</summary>
    private static async Task<Results<Ok<CommissionRateRow>, NotFound>> GetCommissionRateAsync(
        [FromRoute] long id,
        [FromServices] CommissionRateService commissionRateService,
        CancellationToken ct)
    {
        CommissionRate? rate = await commissionRateService.FindByIdAsync(id, ct);
        return rate is null ? TypedResults.NotFound() : TypedResults.Ok(ToRow(rate));
    }

    /// <summary>
    /// Creates a commission rate from the request, held by the listed agents or by none when the request lists no
    /// agents, and returns the operation result.
    /// </summary>
    private static async Task<Ok<OperationResult>> CreateCommissionRateAsync(
        [FromBody] CommissionRateRequest request,
        [FromServices] CommissionRateService commissionRateService,
        CancellationToken ct)
    {
        var command = new CommissionRateCommand
        {
            ItemCategoryId = request.ItemCategoryId,
            Percentage = request.Percentage,
            AgentIds = request.AgentIds is null ? [] : [.. request.AgentIds],
        };

        OperationResult result = await commissionRateService.ExecuteAsync(
            command,
            TransactionType.Persist,
            cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Updates the commission rate with row id <paramref name="id"/> from the request: its item category, its
    /// percentage and, when the request lists agents, the agents holding it; the agents are kept when the request lists
    /// none. Returns the operation result, or 404 when no rate has that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> UpdateCommissionRateAsync(
        [FromRoute] long id,
        [FromBody] CommissionRateRequest request,
        [FromServices] CommissionRateService commissionRateService,
        CancellationToken ct)
    {
        if (await commissionRateService.FindByIdAsync(id, ct) is not { } rate)
        {
            return TypedResults.NotFound();
        }

        var command = new CommissionRateCommand();
        command.PopulateFromObject(rate);
        command.ItemCategoryId = request.ItemCategoryId;
        command.Percentage = request.Percentage;
        if (request.AgentIds is not null)
        {
            command.AgentIds = [.. request.AgentIds];
        }

        OperationResult result = await commissionRateService.ExecuteAsync(
            command,
            TransactionType.Persist,
            cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Deletes the commission rate with row id <paramref name="id"/> and returns the operation result, or 404 when no
    /// rate has that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> DeleteCommissionRateAsync(
        [FromRoute] long id,
        [FromServices] CommissionRateService commissionRateService,
        CancellationToken ct)
    {
        if (await commissionRateService.FindByIdAsync(id, ct) is null)
        {
            return TypedResults.NotFound();
        }

        OperationResult result = await commissionRateService.DeleteByIdAsync(id, ct);
        return TypedResults.Ok(result);
    }


    /// <summary>Returns the item categories of the bound company in name order as rows.</summary>
    private static async Task<Ok<IReadOnlyList<ItemCategoryRow>>> GetItemCategoriesAsync(
        [FromServices] ItemCategoryService itemCategoryService,
        CancellationToken ct)
    {
        IReadOnlyList<ItemCategory> categories = await itemCategoryService.GetItemCategoriesAsync(ct);
        return TypedResults.Ok(ToRows(categories, ToRow));
    }

    /// <summary>Returns the item category with row id <paramref name="id"/> as a row, or 404 when none has that id.</summary>
    private static async Task<Results<Ok<ItemCategoryRow>, NotFound>> GetItemCategoryAsync(
        [FromRoute] long id,
        [FromServices] ItemCategoryService itemCategoryService,
        CancellationToken ct)
    {
        ItemCategory? category = await itemCategoryService.FindByIdAsync(id, ct);
        return category is null ? TypedResults.NotFound() : TypedResults.Ok(ToRow(category));
    }

    /// <summary>Creates an item category from the request and returns the operation result.</summary>
    private static async Task<Ok<OperationResult>> CreateItemCategoryAsync(
        [FromBody] ItemCategoryRequest request,
        [FromServices] ItemCategoryService itemCategoryService,
        CancellationToken ct)
    {
        var command = new ItemCategoryCommand
        {
            Name = request.Name,
            Prefix = request.Prefix,
            Description = request.Description,
        };

        OperationResult result = await itemCategoryService.ExecuteAsync(
            command,
            TransactionType.Persist,
            cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Updates the item category with row id <paramref name="id"/> from the request and returns the operation result,
    /// or 404 when no category has that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> UpdateItemCategoryAsync(
        [FromRoute] long id,
        [FromBody] ItemCategoryRequest request,
        [FromServices] ItemCategoryService itemCategoryService,
        CancellationToken ct)
    {
        if (await itemCategoryService.FindByIdAsync(id, ct) is not { } category)
        {
            return TypedResults.NotFound();
        }

        var command = new ItemCategoryCommand();
        command.PopulateFromObject(category);
        command.Name = request.Name;
        command.Prefix = request.Prefix;
        command.Description = request.Description;

        OperationResult result = await itemCategoryService.ExecuteAsync(
            command,
            TransactionType.Persist,
            cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Deletes the item category with row id <paramref name="id"/> and returns the operation result, or 404 when no
    /// category has that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> DeleteItemCategoryAsync(
        [FromRoute] long id,
        [FromServices] ItemCategoryService itemCategoryService,
        CancellationToken ct)
    {
        if (await itemCategoryService.FindByIdAsync(id, ct) is null)
        {
            return TypedResults.NotFound();
        }

        OperationResult result = await itemCategoryService.DeleteByIdAsync(id, ct: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>Returns the countries of the bound company in name order, each with its regions, as rows.</summary>
    private static async Task<Ok<IReadOnlyList<CountryRow>>> GetCountriesAsync(
        [FromServices] CountryService countryService,
        CancellationToken ct)
    {
        IReadOnlyList<Country> countries = await countryService.GetCountriesAsync(ct);
        return TypedResults.Ok(ToRows(countries, ToRow));
    }

    /// <summary>
    /// Returns the country with row id <paramref name="id"/>, with its regions, as a row, or 404 when none has that id.
    /// </summary>
    private static async Task<Results<Ok<CountryRow>, NotFound>> GetCountryAsync(
        [FromRoute] long id,
        [FromServices] CountryService countryService,
        CancellationToken ct)
    {
        Country? country = await countryService.FindByIdAsync(id, ct);
        return country is null ? TypedResults.NotFound() : TypedResults.Ok(ToRow(country));
    }

    /// <summary>Creates a country from the request and returns the operation result.</summary>
    private static async Task<Ok<OperationResult>> CreateCountryAsync(
        [FromBody] CountryRequest request,
        [FromServices] CountryService countryService,
        CancellationToken ct)
    {
        var command = new CountryCommand { Name = request.Name };

        OperationResult result = await countryService.ExecuteAsync(command, TransactionType.Persist, cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Updates the country with row id <paramref name="id"/> from the request and returns the operation result, or 404
    /// when no country has that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> UpdateCountryAsync(
        [FromRoute] long id,
        [FromBody] CountryRequest request,
        [FromServices] CountryService countryService,
        CancellationToken ct)
    {
        if (await countryService.FindByIdAsync(id, ct) is not { } country)
        {
            return TypedResults.NotFound();
        }

        var command = new CountryCommand();
        command.PopulateFromObject(country);
        command.Name = request.Name;

        OperationResult result = await countryService.ExecuteAsync(command, TransactionType.Persist, cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Deletes the country with row id <paramref name="id"/> and returns the operation result, or 404 when no country
    /// has that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> DeleteCountryAsync(
        [FromRoute] long id,
        [FromServices] CountryService countryService,
        CancellationToken ct)
    {
        if (await countryService.FindByIdAsync(id, ct) is null)
        {
            return TypedResults.NotFound();
        }

        OperationResult result = await countryService.DeleteByIdAsync(id, ct: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Returns the regions of the country with row id <paramref name="country"/>, or of every country of the bound
    /// company when no country is given, as rows; 404 when no country has that id.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<RegionRow>>, NotFound>> GetRegionsAsync(
        [FromQuery(Name = "country")] long? country,
        [FromServices] RegionService regionService,
        [FromServices] CountryService countryService,
        CancellationToken ct)
    {
        if (country is long countryId && await countryService.FindByIdAsync(countryId, ct) is null)
        {
            return TypedResults.NotFound();
        }

        IReadOnlyList<Region> regions = await regionService.GetRegionsAsync(country, ct);
        return TypedResults.Ok(ToRows(regions, ToRow));
    }

    /// <summary>Returns the region with row id <paramref name="id"/> as a row, or 404 when none has that id.</summary>
    private static async Task<Results<Ok<RegionRow>, NotFound>> GetRegionAsync(
        [FromRoute] long id,
        [FromServices] RegionService regionService,
        CancellationToken ct)
    {
        Region? region = await regionService.FindByIdAsync(id, ct);
        return region is null ? TypedResults.NotFound() : TypedResults.Ok(ToRow(region));
    }

    /// <summary>Creates a region from the request and returns the operation result.</summary>
    private static async Task<Ok<OperationResult>> CreateRegionAsync(
        [FromBody] RegionRequest request,
        [FromServices] RegionService regionService,
        CancellationToken ct)
    {
        var command = new RegionCommand { Name = request.Name, CountryId = request.CountryId };

        OperationResult result = await regionService.ExecuteAsync(command, TransactionType.Persist, cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Updates the region with row id <paramref name="id"/> from the request and returns the operation result, or 404
    /// when no region has that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> UpdateRegionAsync(
        [FromRoute] long id,
        [FromBody] RegionRequest request,
        [FromServices] RegionService regionService,
        CancellationToken ct)
    {
        if (await regionService.FindByIdAsync(id, ct) is not { } region)
        {
            return TypedResults.NotFound();
        }

        var command = new RegionCommand();
        command.PopulateFromObject(region);
        command.Name = request.Name;
        command.CountryId = request.CountryId;

        OperationResult result = await regionService.ExecuteAsync(command, TransactionType.Persist, cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Deletes the region with row id <paramref name="id"/> and returns the operation result, or 404 when no region has
    /// that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> DeleteRegionAsync(
        [FromRoute] long id,
        [FromServices] RegionService regionService,
        CancellationToken ct)
    {
        if (await regionService.FindByIdAsync(id, ct) is null)
        {
            return TypedResults.NotFound();
        }

        OperationResult result = await regionService.DeleteByIdAsync(id, ct: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Returns the address values of an update: <paramref name="existing"/> unchanged when <paramref name="fields"/> is
    /// <see langword="null"/>; otherwise <paramref name="existing"/>, or a new address command when there is none,
    /// with its seven fields set from <paramref name="fields"/> and its GUID kept.
    /// </summary>
    private static AddressCommand? ToAddressCommand(AddressFields? fields, AddressCommand? existing)
    {
        if (fields is null)
        {
            return existing;
        }

        AddressCommand address = existing ?? new AddressCommand();
        address.Street = fields.Street;
        address.City = fields.City;
        address.Country = fields.Country;
        address.Phone = fields.Phone;
        address.Fax = fields.Fax;
        address.Email = fields.Email;
        address.WebSite = fields.WebSite;
        return address;
    }

    /// <summary>Maps each value of <paramref name="source"/>, in order, with <paramref name="map"/>.</summary>
    private static IReadOnlyList<TRow> ToRows<TSource, TRow>(IReadOnlyList<TSource> source, Func<TSource, TRow> map)
    {
        var rows = new List<TRow>(source.Count);
        foreach (TSource value in source)
        {
            rows.Add(map(value));
        }

        return rows.AsReadOnly();
    }

    /// <summary>
    /// Maps a commission rate, read with its item category and its agent links with their agents, to its row; the
    /// agents keep the order of the links.
    /// </summary>
    /// <exception cref="InvalidOperationException">The item category or an agent of a link is not loaded.</exception>
    private static CommissionRateRow ToRow(CommissionRate rate)
    {
        ItemCategory category = rate.ItemCategory
            ?? throw new InvalidOperationException($"The item category of commission rate {rate.Id} is not loaded.");

        var agents = new List<NameProjection>(rate.AgentCommissionRates.Count);
        foreach (AgentCommissionRate link in rate.AgentCommissionRates)
        {
            Agent agent = link.Agent
                ?? throw new InvalidOperationException(
                    $"Agent {link.AgentId} of commission rate {rate.Id} is not loaded.");
            agents.Add(NameProjection.From(agent));
        }

        return new CommissionRateRow(rate.Id, rate.ItemCategoryId, category.Name, rate.Percentage, agents.AsReadOnly());
    }

    /// <summary>Maps an item category to its row.</summary>
    private static ItemCategoryRow ToRow(ItemCategory category) =>
        new(category.Id, category.Name, category.Prefix, category.Description);

    /// <summary>
    /// Maps a country, read with its regions, to its row; the regions keep the order in which they were read.
    /// </summary>
    private static CountryRow ToRow(Country country)
    {
        var regions = new List<RegionRow>(country.Regions.Count);
        foreach (Region region in country.Regions)
        {
            regions.Add(new RegionRow(region.Id, region.Name, country.Id, country.Name));
        }

        return new CountryRow(country.Id, country.Name, regions.AsReadOnly());
    }

    /// <summary>Maps a region, read with its country, to its row.</summary>
    /// <exception cref="InvalidOperationException">The country of the region is not loaded.</exception>
    private static RegionRow ToRow(Region region)
    {
        Country country = region.Country
            ?? throw new InvalidOperationException($"The country of region {region.Id} is not loaded.");

        return new RegionRow(region.Id, region.Name, country.Id, country.Name);
    }
}


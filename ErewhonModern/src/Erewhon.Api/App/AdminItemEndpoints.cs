using System.Globalization;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Projections;
using Erewhon.Application.Search;
using Erewhon.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erewhon.Api.App;

/// <summary>
/// Maps the <c>/api/app/admin</c> item endpoints, the back end of the items list, the items-by-category list, the
/// find-item dialog and the retail and tender item editors: the item search, the item list of one category, the item
/// lookup by code, and the create, update and delete of retail and tender items. Item rows carry no photo bytes.
/// </summary>
public static class AdminItemEndpoints
{
    private const string Tag = "App: Admin items";

    /// <summary>
    /// Maps, under <c>/admin</c> on <paramref name="api"/>:
    /// <list type="bullet">
    /// <item><description><c>POST /admin/items/search</c>: the items matching an <see cref="AdminItemSearchRequest"/>,
    /// cart items included, as <see cref="ItemListRow"/> values in code-number order.</description></item>
    /// <item><description><c>GET /admin/items/by-category?category={id}</c>: the items of one category as an
    /// <see cref="ItemsByCategoryResponse"/>; 404 for an unknown category.</description></item>
    /// <item><description><c>GET /admin/items/find?code={code}</c>: the bound company's item whose code number is the
    /// number part of the code, as an <see cref="ItemListRow"/>; 404 for an empty code or no such item.</description></item>
    /// <item><description><c>POST /admin/retail-items</c> and <c>POST /admin/tender-items</c>: create a retail or
    /// tender item and return the <see cref="OperationResult"/>.</description></item>
    /// <item><description><c>PUT /admin/retail-items/{id}</c> and <c>PUT /admin/tender-items/{id}</c>: update a
    /// retail or tender item and return the <see cref="OperationResult"/>; 404 when no item of that type has the
    /// id.</description></item>
    /// <item><description><c>DELETE /admin/items/{id}</c>: delete an item with its sale and tenders and return the
    /// <see cref="OperationResult"/>; 404 when no item has the id.</description></item>
    /// </list>
    /// </summary>
    /// <param name="api">The <c>/api/app</c> route group.</param>
    /// <returns><paramref name="api"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is <see langword="null"/>.</exception>
    public static RouteGroupBuilder MapAdminItemEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var admin = api.MapGroup("/admin");

        admin.MapPost("/items/search", SearchItemsAsync)
            .WithName("AppAdminSearchItems")
            .WithTags(Tag)
            .WithSummary("Returns the items matching the items list filters, cart items included, in code-number order.")
            .Produces<IReadOnlyList<ItemListRow>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapGet("/items/by-category", GetItemsByCategoryAsync)
            .WithName("AppAdminGetItemsByCategory")
            .WithTags(Tag)
            .WithSummary("Returns the items of one category in code-number order.")
            .Produces<ItemsByCategoryResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapGet("/items/find", FindItemAsync)
            .WithName("AppAdminFindItem")
            .WithTags(Tag)
            .WithSummary("Returns the bound company's item whose code number is the number part of an item code.")
            .Produces<ItemListRow>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPost("/retail-items", CreateRetailItemAsync)
            .WithName("AppAdminCreateRetailItem")
            .WithTags(Tag)
            .WithSummary("Creates a retail item and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPut("/retail-items/{id:long}", UpdateRetailItemAsync)
            .WithName("AppAdminUpdateRetailItem")
            .WithTags(Tag)
            .WithSummary("Updates a retail item and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPost("/tender-items", CreateTenderItemAsync)
            .WithName("AppAdminCreateTenderItem")
            .WithTags(Tag)
            .WithSummary("Creates a tender item and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPut("/tender-items/{id:long}", UpdateTenderItemAsync)
            .WithName("AppAdminUpdateTenderItem")
            .WithTags(Tag)
            .WithSummary("Updates a tender item and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapDelete("/items/{id:long}", DeleteItemAsync)
            .WithName("AppAdminDeleteItem")
            .WithTags(Tag)
            .WithSummary("Deletes an item, with its sale and tenders, and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        return api;
    }

    /// <summary>
    /// Splits an item code into its category prefix and code number, as <c>String::getCodePrefixAndNumber</c> does:
    /// the characters are examined from the last one backwards, and the first character that is not an ASCII digit
    /// ends the prefix.
    /// </summary>
    /// <param name="code">The item code, e.g. <c>CATI7</c>.</param>
    /// <returns>
    /// The prefix, up to and including the last character that is not an ASCII digit, and the number formed by the
    /// digits after it: <c>("CATI", 7)</c> for <c>CATI7</c> and <c>("ABCDE", 12)</c> for <c>ABCDE12</c>. The number is
    /// 0 when the code ends with a character that is not a digit (<c>("CATI", 0)</c> for <c>CATI</c>) or when the
    /// digits exceed the <see cref="int"/> range. A code made of digits only, or an empty code, yields
    /// <c>(null, 0)</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is <see langword="null"/>.</exception>
    public static (string? Prefix, int Number) ParseItemCode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        int lastIndex = code.Length - 1;
        for (int i = lastIndex; i >= 0; i--)
        {
            if (char.IsAsciiDigit(code[i]))
            {
                continue;
            }

            string prefix = code[..(i + 1)];
            int number = 0;
            if (i < lastIndex)
            {
                _ = int.TryParse(code[(i + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out number);
            }

            return (prefix, number);
        }

        return (null, 0);
    }

    /// <summary>
    /// Runs a fresh item search with the request's filters and cart items included, and returns the matching items
    /// as rows in code-number order.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<ItemListRow>>> SearchItemsAsync(
        [FromBody] AdminItemSearchRequest request,
        [FromServices] ItemSearch itemSearch,
        [FromServices] ISessionContext session,
        [FromServices] IServerClock clock,
        CancellationToken ct)
    {
        var criteria = new ItemSearchCriteria
        {
            AgentId = request.AgentId,
            ClientId = request.ClientId,
            CountryId = request.CountryId,
            RegionId = request.RegionId,
            ItemCategoryId = request.ItemCategoryId,
            OnlyRetailItems = request.OnlyRetailItems,
            OnlyTenderItems = request.OnlyTenderItems,
            PriceLow = request.PriceLow,
            PriceHigh = request.PriceHigh,
            IncludeSoldItems = request.IncludeSoldItems,
            IncludeClosedTenders = request.IncludeClosedTenders,
            IncludeItemsInCart = true,
        };

        IReadOnlyList<Item> items = await itemSearch.DoSearchAsync(criteria, session.SessionId, ct);
        DateOnly serverDate = await clock.GetServerDateAsync(ct);

        return TypedResults.Ok(ToRows(items, serverDate));
    }

    /// <summary>
    /// Returns the items of the category with row id <paramref name="category"/> as rows, or 404 when no category has
    /// that id.
    /// </summary>
    private static async Task<Results<Ok<ItemsByCategoryResponse>, NotFound>> GetItemsByCategoryAsync(
        [FromQuery(Name = "category")] long category,
        [FromServices] ItemCategoryService itemCategoryService,
        [FromServices] ItemService itemService,
        [FromServices] IServerClock clock,
        CancellationToken ct)
    {
        if (!await itemCategoryService.ExistsAsync(category, ct))
        {
            return TypedResults.NotFound();
        }

        IReadOnlyList<Item> items = await itemService.GetItemsByCategoryAsync(category, ct);
        DateOnly serverDate = await clock.GetServerDateAsync(ct);

        return TypedResults.Ok(new ItemsByCategoryResponse(category, ToRows(items, serverDate)));
    }

    /// <summary>
    /// Returns the bound company's item whose code number is the number part of <paramref name="code"/> as a row, or
    /// 404 when the code is missing or empty or the company has no item with that number.
    /// </summary>
    private static async Task<Results<Ok<ItemListRow>, NotFound>> FindItemAsync(
        [FromQuery(Name = "code")] string? code,
        [FromServices] CompanyService companyService,
        [FromServices] IServerClock clock,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code))
        {
            return TypedResults.NotFound();
        }

        (_, int number) = ParseItemCode(code);
        Company company = await companyService.GetCompanyAsync(ct);
        Item? item = await companyService.GetItem(company.Id, number, ct);
        if (item is null)
        {
            return TypedResults.NotFound();
        }

        DateOnly serverDate = await clock.GetServerDateAsync(ct);
        return TypedResults.Ok(ToRow(item, serverDate));
    }

    /// <summary>Creates a retail item from the request and returns the operation result.</summary>
    private static async Task<Ok<OperationResult>> CreateRetailItemAsync(
        [FromBody] RetailItemRequest request,
        [FromServices] ItemService itemService,
        CancellationToken ct)
    {
        var command = new RetailItemCommand();
        ApplyRequest(command, request);

        OperationResult result = await itemService.ExecuteAsync(command, TransactionType.Persist, cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Updates the retail item with row id <paramref name="id"/> from the request and returns the operation result, or
    /// 404 when no retail item has that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> UpdateRetailItemAsync(
        [FromRoute] long id,
        [FromBody] RetailItemRequest request,
        [FromServices] ItemService itemService,
        CancellationToken ct)
    {
        if (await itemService.FindByIdAsync(id, ct) is not RetailItem item)
        {
            return TypedResults.NotFound();
        }

        var command = new RetailItemCommand();
        command.PopulateFromObject(item);
        ApplyRequest(command, request);

        OperationResult result = await itemService.ExecuteAsync(command, TransactionType.Persist, cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>Creates a tender item from the request and returns the operation result.</summary>
    private static async Task<Ok<OperationResult>> CreateTenderItemAsync(
        [FromBody] TenderItemRequest request,
        [FromServices] ItemService itemService,
        CancellationToken ct)
    {
        var command = new TenderItemCommand();
        ApplyRequest(command, request);

        OperationResult result = await itemService.ExecuteAsync(command, TransactionType.Persist, cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Updates the tender item with row id <paramref name="id"/> from the request and returns the operation result, or
    /// 404 when no tender item has that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> UpdateTenderItemAsync(
        [FromRoute] long id,
        [FromBody] TenderItemRequest request,
        [FromServices] ItemService itemService,
        CancellationToken ct)
    {
        if (await itemService.FindByIdAsync(id, ct) is not TenderItem item)
        {
            return TypedResults.NotFound();
        }

        var command = new TenderItemCommand();
        command.PopulateFromObject(item);
        ApplyRequest(command, request);

        OperationResult result = await itemService.ExecuteAsync(command, TransactionType.Persist, cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Deletes the item with row id <paramref name="id"/>, with its sale and tenders, and returns the operation result,
    /// or 404 when no item has that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> DeleteItemAsync(
        [FromRoute] long id,
        [FromServices] ItemService itemService,
        CancellationToken ct)
    {
        if (!await itemService.ExistsAsync(id, ct))
        {
            return TypedResults.NotFound();
        }

        OperationResult result = await itemService.DeleteByIdAsync(id, ct);
        return TypedResults.Ok(result);
    }

    /// <summary>Copies every value of a retail item request into the command.</summary>
    private static void ApplyRequest(RetailItemCommand command, RetailItemRequest request) =>
        ApplyItemValues(
            command,
            request.CodeNumber,
            request.Name,
            request.Description,
            request.ListedDate,
            request.PhotoBase64,
            request.Price,
            request.AgentId,
            request.ItemCategoryId,
            request.RegionId);

    /// <summary>Copies every value of a tender item request, closure date included, into the command.</summary>
    private static void ApplyRequest(TenderItemCommand command, TenderItemRequest request)
    {
        ApplyItemValues(
            command,
            request.CodeNumber,
            request.Name,
            request.Description,
            request.ListedDate,
            request.PhotoBase64,
            request.Price,
            request.AgentId,
            request.ItemCategoryId,
            request.RegionId);
        command.ClosureDate = request.ClosureDate;
    }

    /// <summary>Writes the item values shared by retail and tender items to the command, decoding the photo.</summary>
    private static void ApplyItemValues(
        ItemCommand command,
        int codeNumber,
        string? name,
        string? description,
        DateOnly? listedDate,
        string? photoBase64,
        decimal price,
        long? agentId,
        long? itemCategoryId,
        long? regionId)
    {
        command.CodeNumber = codeNumber;
        command.Name = name;
        command.Description = description;
        command.ListedDate = listedDate;
        command.Photo = DecodePhoto(photoBase64);
        command.Price = price;
        command.AgentId = agentId;
        command.ItemCategoryId = itemCategoryId;
        command.RegionId = regionId;
    }

    /// <summary>
    /// Returns the bytes of a base64 photo: <see langword="null"/> for a <see langword="null"/> or empty text, the
    /// decoded bytes for valid base64, and the single byte 0x00, which is not a valid picture, for text that is not
    /// valid base64.
    /// </summary>
    private static byte[]? DecodePhoto(string? photoBase64)
    {
        if (string.IsNullOrEmpty(photoBase64))
        {
            return null;
        }

        var buffer = new byte[(photoBase64.Length + 3) / 4 * 3];
        return Convert.TryFromBase64String(photoBase64, buffer, out int written)
            ? buffer[..written]
            : new byte[] { 0x00 };
    }

    /// <summary>Maps each item to its row, judging tender closure at <paramref name="serverDate"/>.</summary>
    private static IReadOnlyList<ItemListRow> ToRows(IReadOnlyList<Item> items, DateOnly serverDate)
    {
        var rows = new List<ItemListRow>(items.Count);
        foreach (Item item in items)
        {
            rows.Add(ToRow(item, serverDate));
        }

        return rows.AsReadOnly();
    }

    /// <summary>
    /// Maps an item to its row: its projection without photo bytes, and whether it is a tender item closed at
    /// <paramref name="serverDate"/>.
    /// </summary>
    private static ItemListRow ToRow(Item item, DateOnly serverDate) =>
        new(
            ItemProjection.From(item) with { Photo = null },
            item is TenderItem tenderItem && tenderItem.IsClosed(serverDate));
}

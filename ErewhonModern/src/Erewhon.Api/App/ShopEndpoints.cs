using System.Runtime.CompilerServices;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Cart;
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
/// Maps the shop endpoints, the back end of the shop's search, item details, cart, checkout and confirmation pages:
/// the search plinth's reference data and the session's stored search criteria, the item search of the session, the
/// session cart and its add, remove, empty and checkout operations, and the details and photo of one item. Every
/// route acts for the caller's session, resolved by <see cref="SessionMiddleware"/>.
/// </summary>
public static class ShopEndpoints
{
    private const string Tag = "App: Shop";

    private const string JpegContentType = "image/jpeg";

    private const string PngContentType = "image/png";

    private const string BitmapContentType = "image/bmp";

    private const string TiffContentType = "image/tiff";

    private const string IconContentType = "image/x-icon";

    private const string OctetStreamContentType = "application/octet-stream";

    private static readonly ConditionalWeakTable<ItemSearchCriteria, SemaphoreSlim> SearchGates = new();

    /// <summary>
    /// Maps these routes on <paramref name="api"/>, the first seven on its sub-group <c>/shop</c>:
    /// <list type="bullet">
    /// <item><description>
    /// <c>GET /shop/reference</c>: 200 with a <see cref="ShopReferenceResponse"/>: the bound company's countries in
    /// name order, each with its regions in name order, its item categories in name order, and the criteria held by
    /// the session's shop item search.
    /// </description></item>
    /// <item><description>
    /// <c>POST /shop/search</c>: resets the session's shop item search, sets it from the
    /// <see cref="ShopSearchRequest"/> with items in the session cart and sold items excluded, runs it, and answers 200
    /// with a <see cref="ShopSearchResponse"/> whose rows are in code-number order. A low price above a high price,
    /// both non-zero, fails with the 500 <see cref="ErrorOutcome"/> of error 500002.
    /// </description></item>
    /// <item><description>
    /// <c>GET /shop/cart</c>: 200 with the session's <see cref="CartView"/>.
    /// </description></item>
    /// <item><description>
    /// <c>POST /shop/cart/items</c>: adds the <see cref="AddCartItemRequest"/> item with its offer to the session cart
    /// and answers 200 with a <see cref="CartOperationResponse"/>: the <see cref="OperationResult"/> of the add,
    /// unchanged, and the cart after it.
    /// </description></item>
    /// <item><description>
    /// <c>POST /shop/cart/remove</c>: removes the <see cref="RemoveCartItemRequest"/> item's line from the session cart
    /// and answers 200 with a <see cref="CartOperationResponse"/>: the <see cref="OperationResult"/> of the remove,
    /// unchanged, and the cart after it.
    /// </description></item>
    /// <item><description>
    /// <c>POST /shop/cart/empty</c>: removes every line from the session cart and answers 200 with the empty
    /// <see cref="CartView"/>.
    /// </description></item>
    /// <item><description>
    /// <c>POST /shop/checkout</c>: checks out the session cart and answers 200 with a <see cref="CheckoutResponse"/>:
    /// the <see cref="OperationResult"/> of the checkout, unchanged, and the <see cref="CheckoutReceipt"/> of a
    /// successful checkout, <see langword="null"/> when it failed.
    /// </description></item>
    /// <item><description>
    /// <c>GET /items/{id}</c>: 200 with the <see cref="ItemDetailsResponse"/> of the item with row id <c>id</c>, its
    /// read model without photo bytes and with the session client's own tenders on it; 404 with an empty body when no
    /// item has that id.
    /// </description></item>
    /// <item><description>
    /// <c>GET /items/{id}/photo</c>: 200 with the photo bytes of the item with row id <c>id</c> and the content type
    /// <see cref="GetPhotoContentType"/> gives for their picture type; 404 with an empty body when no item has that id
    /// or the item has no photo.
    /// </description></item>
    /// </list>
    /// Every route answers 401 with an empty body when the request carries no registered session and fails with the
    /// 500 <see cref="ErrorOutcome"/> when an exception escapes it. The routes that read a JSON body also declare the
    /// 400 with an empty body of a request whose body cannot be read.
    /// </summary>
    /// <param name="api">The <c>/api/app</c> route group.</param>
    /// <returns><paramref name="api"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is <see langword="null"/>.</exception>
    public static RouteGroupBuilder MapShopEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder shop = api.MapGroup("/shop");

        shop.MapGet("/reference", GetReferenceAsync)
            .WithName("AppShopGetReference")
            .WithTags(Tag)
            .WithSummary("Returns the countries with their regions, the item categories and the session's current shop search criteria.")
            .Produces<ShopReferenceResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        shop.MapPost("/search", SearchAsync)
            .WithName("AppShopSearch")
            .WithTags(Tag)
            .WithSummary("Runs the session's shop item search with the given criteria and returns the matching items in code-number order.")
            .Produces<ShopSearchResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        shop.MapGet("/cart", GetCartAsync)
            .WithName("AppShopGetCart")
            .WithTags(Tag)
            .WithSummary("Returns the session cart: its lines, total and size.")
            .Produces<CartView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        shop.MapPost("/cart/items", AddCartItemAsync)
            .WithName("AppShopAddCartItem")
            .WithTags(Tag)
            .WithSummary("Adds an item with an offer to the session cart and returns the operation result and the cart.")
            .Produces<CartOperationResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        shop.MapPost("/cart/remove", RemoveCartItemAsync)
            .WithName("AppShopRemoveCartItem")
            .WithTags(Tag)
            .WithSummary("Removes an item's line from the session cart and returns the operation result and the cart.")
            .Produces<CartOperationResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        shop.MapPost("/cart/empty", EmptyCartAsync)
            .WithName("AppShopEmptyCart")
            .WithTags(Tag)
            .WithSummary("Removes every line from the session cart and returns the empty cart.")
            .Produces<CartView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        shop.MapPost("/checkout", CheckoutAsync)
            .WithName("AppShopCheckout")
            .WithTags(Tag)
            .WithSummary("Checks out the session cart and returns the operation result and, on success, the confirmation lines.")
            .Produces<CheckoutResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        api.MapGet("/items/{id:long}", GetItemDetailsAsync)
            .WithName("AppShopGetItemDetails")
            .WithTags(Tag)
            .WithSummary("Returns the details of one item without its photo bytes, with the session client's own tenders on it.")
            .Produces<ItemDetailsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        api.MapGet("/items/{id:long}/photo", GetItemPhotoAsync)
            .WithName("AppShopGetItemPhoto")
            .WithTags(Tag)
            .WithSummary("Returns the photo bytes of one item.")
            .Produces(
                StatusCodes.Status200OK,
                typeof(Stream),
                JpegContentType,
                PngContentType,
                BitmapContentType,
                TiffContentType,
                IconContentType,
                OctetStreamContentType)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        return api;
    }

    /// <summary>
    /// Returns the content type served for a photo of the given picture type: <c>image/jpeg</c> for
    /// <see cref="PictureType.Jpeg"/>, <c>image/png</c> for <see cref="PictureType.Png"/>, <c>image/bmp</c> for
    /// <see cref="PictureType.Bitmap"/>, <c>image/tiff</c> for <see cref="PictureType.Tiff"/>, <c>image/x-icon</c> for
    /// <see cref="PictureType.Icon"/> and <see cref="PictureType.Cursor"/>, and <c>application/octet-stream</c> for
    /// <see cref="PictureType.Metafile"/>, <see cref="PictureType.Invalid"/> and any value
    /// <see cref="PictureType"/> does not define.
    /// </summary>
    /// <param name="type">The picture type of the photo, as <see cref="PictureValidator.GetPictureType(byte[])"/> gives it.</param>
    /// <returns>The content type.</returns>
    public static string GetPhotoContentType(PictureType type) => type switch
    {
        PictureType.Jpeg => JpegContentType,
        PictureType.Png => PngContentType,
        PictureType.Bitmap => BitmapContentType,
        PictureType.Tiff => TiffContentType,
        PictureType.Icon or PictureType.Cursor => IconContentType,
        _ => OctetStreamContentType,
    };

    /// <summary>
    /// Returns the bound company's countries with their regions and its item categories, each in name order, and the
    /// criteria the session's shop item search holds.
    /// </summary>
    private static async Task<Results<Ok<ShopReferenceResponse>, UnauthorizedHttpResult>> GetReferenceAsync(
        HttpContext httpContext,
        [FromServices] CountryService countryService,
        [FromServices] ItemCategoryService itemCategoryService,
        CancellationToken ct)
    {
        SessionEntry? entry = SessionMiddleware.GetSession(httpContext);
        if (entry is null)
        {
            return TypedResults.Unauthorized();
        }

        IReadOnlyList<Country> countries = await countryService.GetCountriesAsync(ct);
        IReadOnlyList<ItemCategory> categories = await itemCategoryService.GetItemCategoryListAsync(ct);
        ShopSearchRequest currentSearch = await ReadCurrentSearchAsync(entry.ShopSearch, ct);

        var countryOptions = new List<ShopCountryOption>(countries.Count);
        foreach (Country country in countries)
        {
            var regionOptions = new List<ShopRegionOption>(country.Regions.Count);
            foreach (Region region in country.Regions)
            {
                regionOptions.Add(new ShopRegionOption(region.Id, region.Name));
            }

            countryOptions.Add(new ShopCountryOption(country.Id, country.Name, regionOptions));
        }

        var categoryOptions = new List<ShopCategoryOption>(categories.Count);
        foreach (ItemCategory category in categories)
        {
            categoryOptions.Add(new ShopCategoryOption(category.Id, category.Name, category.Prefix));
        }

        return TypedResults.Ok(new ShopReferenceResponse(countryOptions, categoryOptions, currentSearch));
    }

    /// <summary>
    /// Resets the session's shop item search, sets it from <paramref name="request"/> with cart and sold items
    /// excluded, runs it for the session, and returns the found items as search rows in the order found.
    /// </summary>
    private static async Task<Results<Ok<ShopSearchResponse>, UnauthorizedHttpResult>> SearchAsync(
        [FromBody] ShopSearchRequest request,
        HttpContext httpContext,
        [FromServices] ItemSearch itemSearch,
        [FromServices] IServerClock clock,
        CancellationToken ct)
    {
        SessionEntry? entry = SessionMiddleware.GetSession(httpContext);
        if (entry is null)
        {
            return TypedResults.Unauthorized();
        }

        ItemSearchCriteria criteria = entry.ShopSearch;
        IReadOnlyList<Item> items;

        SemaphoreSlim gate = GetSearchGate(criteria);
        await gate.WaitAsync(ct);
        try
        {
            criteria.Initialize();
            criteria.CountryId = request.CountryId;
            criteria.RegionId = request.RegionId;
            criteria.ItemCategoryId = request.ItemCategoryId;
            criteria.OnlyRetailItems = request.ItemKind == ShopItemKind.Retail;
            criteria.OnlyTenderItems = request.ItemKind == ShopItemKind.Tender;
            criteria.PriceHigh = request.PriceHigh;
            criteria.PriceLow = request.PriceLow;
            criteria.IncludeClosedTenders = request.IncludeClosedTenders;
            criteria.IncludeItemsInCart = false;
            criteria.IncludeSoldItems = false;

            items = await itemSearch.DoSearchAsync(criteria, entry.SessionId, ct);
        }
        finally
        {
            gate.Release();
        }

        DateOnly serverDate = await clock.GetServerDateAsync(ct);

        var rows = new List<ShopSearchRow>(items.Count);
        foreach (Item item in items)
        {
            rows.Add(new ShopSearchRow(
                item.Id,
                item is TenderItem,
                item.GetCode(),
                item.Name,
                item.ListedDate,
                item.Price,
                IsClosedTender(item, serverDate)));
        }

        return TypedResults.Ok(new ShopSearchResponse(rows));
    }

    /// <summary>Returns the session cart.</summary>
    private static async Task<Results<Ok<CartView>, UnauthorizedHttpResult>> GetCartAsync(
        HttpContext httpContext,
        [FromServices] ShoppingCartService cart,
        CancellationToken ct)
    {
        SessionEntry? entry = SessionMiddleware.GetSession(httpContext);
        if (entry is null)
        {
            return TypedResults.Unauthorized();
        }

        return TypedResults.Ok(await cart.GetCartAsync(entry.SessionId, ct));
    }

    /// <summary>
    /// Adds the request's item with its offer to the session cart and returns the result of the add with the cart
    /// after it.
    /// </summary>
    private static async Task<Results<Ok<CartOperationResponse>, UnauthorizedHttpResult>> AddCartItemAsync(
        [FromBody] AddCartItemRequest request,
        HttpContext httpContext,
        [FromServices] ShoppingCartService cart,
        CancellationToken ct)
    {
        SessionEntry? entry = SessionMiddleware.GetSession(httpContext);
        if (entry is null)
        {
            return TypedResults.Unauthorized();
        }

        OperationResult result = await cart.AddItemAsync(entry.SessionId, request.ItemId, request.Offer, ct);
        CartView view = await cart.GetCartAsync(entry.SessionId, ct);

        return TypedResults.Ok(new CartOperationResponse(result, view));
    }

    /// <summary>
    /// Removes the request's item's line from the session cart and returns the result of the remove with the cart
    /// after it.
    /// </summary>
    private static async Task<Results<Ok<CartOperationResponse>, UnauthorizedHttpResult>> RemoveCartItemAsync(
        [FromBody] RemoveCartItemRequest request,
        HttpContext httpContext,
        [FromServices] ShoppingCartService cart,
        CancellationToken ct)
    {
        SessionEntry? entry = SessionMiddleware.GetSession(httpContext);
        if (entry is null)
        {
            return TypedResults.Unauthorized();
        }

        OperationResult result = cart.RemoveItem(entry.SessionId, request.ItemId);
        CartView view = await cart.GetCartAsync(entry.SessionId, ct);

        return TypedResults.Ok(new CartOperationResponse(result, view));
    }

    /// <summary>Removes every line from the session cart and returns the cart after it.</summary>
    private static async Task<Results<Ok<CartView>, UnauthorizedHttpResult>> EmptyCartAsync(
        HttpContext httpContext,
        [FromServices] ShoppingCartService cart,
        CancellationToken ct)
    {
        SessionEntry? entry = SessionMiddleware.GetSession(httpContext);
        if (entry is null)
        {
            return TypedResults.Unauthorized();
        }

        cart.Empty(entry.SessionId);

        return TypedResults.Ok(await cart.GetCartAsync(entry.SessionId, ct));
    }

    /// <summary>Checks out the session cart and returns the result of the checkout with its receipt.</summary>
    private static async Task<Results<Ok<CheckoutResponse>, UnauthorizedHttpResult>> CheckoutAsync(
        HttpContext httpContext,
        [FromServices] ShoppingCartService cart,
        CancellationToken ct)
    {
        SessionEntry? entry = SessionMiddleware.GetSession(httpContext);
        if (entry is null)
        {
            return TypedResults.Unauthorized();
        }

        CheckoutResult checkout = await cart.CheckoutAsync(entry.SessionId, ct);

        return TypedResults.Ok(new CheckoutResponse(checkout.Result, checkout.Receipt));
    }

    /// <summary>
    /// Returns the details of the item with row id <paramref name="id"/> without its photo bytes, whether it has a
    /// photo, whether it is a closed tender item, and the tenders the session client has placed on it in descending
    /// offer order, then by time tendered; 404 when no item has that id.
    /// </summary>
    private static async Task<Results<Ok<ItemDetailsResponse>, NotFound, UnauthorizedHttpResult>> GetItemDetailsAsync(
        long id,
        HttpContext httpContext,
        [FromServices] ItemService itemService,
        [FromServices] IServerClock clock,
        CancellationToken ct)
    {
        SessionEntry? entry = SessionMiddleware.GetSession(httpContext);
        if (entry is null)
        {
            return TypedResults.Unauthorized();
        }

        Item? item = await itemService.FindByIdAsync(id, ct);
        if (item is null)
        {
            return TypedResults.NotFound();
        }

        IReadOnlyList<OwnTenderOffer> ownTenders = [];
        if (item is TenderItem && entry.ClientId is long clientId)
        {
            IReadOnlyList<Tender> tenders = await itemService.GetClientTendersOnItemAsync(item.Id, clientId, ct);
            ownTenders = tenders
                .OrderByDescending(tender => tender.Offer)
                .ThenBy(tender => tender.DateTendered)
                .Select(tender => new OwnTenderOffer(tender.Id, tender.Offer, tender.DateTendered))
                .ToList();
        }

        DateOnly serverDate = await clock.GetServerDateAsync(ct);

        return TypedResults.Ok(new ItemDetailsResponse(
            ItemProjection.From(item) with { Photo = null },
            item.Photo is { Length: > 0 },
            IsClosedTender(item, serverDate),
            ownTenders));
    }

    /// <summary>
    /// Returns the photo bytes of the item with row id <paramref name="id"/> with the content type of their picture
    /// type; 404 when no item has that id or the item has no photo.
    /// </summary>
    private static async Task<Results<FileContentHttpResult, NotFound, UnauthorizedHttpResult>> GetItemPhotoAsync(
        long id,
        HttpContext httpContext,
        [FromServices] ItemService itemService,
        CancellationToken ct)
    {
        SessionEntry? entry = SessionMiddleware.GetSession(httpContext);
        if (entry is null)
        {
            return TypedResults.Unauthorized();
        }

        byte[]? photo = await itemService.GetPhotoAsync(id, ct);
        if (photo is not { Length: > 0 })
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Bytes(photo, GetPhotoContentType(PictureValidator.GetPictureType(photo)));
    }

    /// <summary>
    /// Returns the criteria held by <paramref name="criteria"/> as a search request, read while no search of the same
    /// criteria runs.
    /// </summary>
    private static async Task<ShopSearchRequest> ReadCurrentSearchAsync(ItemSearchCriteria criteria, CancellationToken ct)
    {
        SemaphoreSlim gate = GetSearchGate(criteria);
        await gate.WaitAsync(ct);
        try
        {
            ShopItemKind itemKind = criteria.OnlyRetailItems
                ? ShopItemKind.Retail
                : criteria.OnlyTenderItems ? ShopItemKind.Tender : ShopItemKind.All;

            return new ShopSearchRequest(
                criteria.CountryId,
                criteria.RegionId,
                criteria.ItemCategoryId,
                itemKind,
                criteria.PriceLow,
                criteria.PriceHigh,
                criteria.IncludeClosedTenders);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Returns the gate that admits one search, or one read of the criteria, at a time for <paramref name="criteria"/>.
    /// </summary>
    private static SemaphoreSlim GetSearchGate(ItemSearchCriteria criteria) =>
        SearchGates.GetValue(criteria, static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Returns <see langword="true"/> for a tender item whose closure date is before <paramref name="serverDate"/>;
    /// <see langword="false"/> for an open tender item and for a retail item.
    /// </summary>
    private static bool IsClosedTender(Item item, DateOnly serverDate) =>
        item is TenderItem tenderItem && tenderItem.IsClosed(serverDate);
}

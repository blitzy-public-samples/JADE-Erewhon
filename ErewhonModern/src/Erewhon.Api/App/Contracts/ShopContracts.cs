using Erewhon.Application.Cart;
using Erewhon.Application.Common;
using Erewhon.Application.Projections;

namespace Erewhon.Api.App.Contracts;

/// <summary>
/// One entry of the shop search plinth's <c>cmbRegion</c> combo: a region of the country selected in <c>cmbCountry</c>.
/// </summary>
/// <param name="Id">The row id of the region.</param>
/// <param name="Name">The region name.</param>
public sealed record ShopRegionOption(long Id, string Name);

/// <summary>
/// One entry of the shop search plinth's <c>cmbCountry</c> combo, filled from the company's countries in name order,
/// with the entries <c>cmbRegion</c> shows once the country is selected.
/// </summary>
/// <param name="Id">The row id of the country.</param>
/// <param name="Name">The country name.</param>
/// <param name="Regions">The regions of the country in name order, the contents of <c>country.allRegions</c>.</param>
public sealed record ShopCountryOption(long Id, string Name, IReadOnlyList<ShopRegionOption> Regions);

/// <summary>
/// One entry of the shop search plinth's <c>cmbCategory</c> combo, filled from the company's item categories in name
/// order (<c>allItemCategoriesByName</c>).
/// </summary>
/// <param name="Id">The row id of the item category.</param>
/// <param name="Name">The category name.</param>
/// <param name="Prefix">The category prefix that begins the code of each of its items.</param>
public sealed record ShopCategoryOption(long Id, string Name, string Prefix);

/// <summary>
/// The item types the shop search returns, the option chosen among <c>optAllItems</c>, <c>optRetailItems</c> and
/// <c>optTenderItems</c>.
/// </summary>
public enum ShopItemKind
{
    /// <summary>
    /// Retail and tender items: <c>optAllItems</c> chosen, with neither <c>onlyRetailItems</c> nor
    /// <c>onlyTenderItems</c> set.
    /// </summary>
    All,

    /// <summary>Retail items only: <c>optRetailItems</c> chosen, setting <c>onlyRetailItems</c>.</summary>
    Retail,

    /// <summary>Tender items only: <c>optTenderItems</c> chosen, setting <c>onlyTenderItems</c>.</summary>
    Tender,
}

/// <summary>
/// Request of <c>POST /api/app/shop/search</c>, carrying the values <c>ShopItemsForm.populateItemSearch</c> copies from
/// the search plinth into the session's shop item search. A <see langword="null"/> id applies no filter on that value.
/// Also the session's current search criteria returned in <see cref="ShopReferenceResponse.CurrentSearch"/>.
/// </summary>
/// <param name="CountryId">
/// The row id of the country the items are in, the <c>myCountry</c> filter selected in <c>cmbCountry</c>;
/// <see langword="null"/> when no country is selected.
/// </param>
/// <param name="RegionId">
/// The row id of the region the items are in, the <c>myRegion</c> filter selected in <c>cmbRegion</c>;
/// <see langword="null"/> when no region is selected.
/// </param>
/// <param name="ItemCategoryId">
/// The row id of the category the items belong to, the <c>myItemCategory</c> filter selected in <c>cmbCategory</c>;
/// <see langword="null"/> when no category is selected.
/// </param>
/// <param name="ItemKind">
/// The item types returned, the option chosen among <c>optAllItems</c>, <c>optRetailItems</c> and
/// <c>optTenderItems</c>.
/// </param>
/// <param name="PriceLow">
/// The lowest price returned, inclusive, the <c>priceLow</c> decimal value of <c>txtPriceLower</c>; 0 for no lower bound.
/// </param>
/// <param name="PriceHigh">
/// The highest price returned, inclusive, the <c>priceHigh</c> decimal value of <c>txtPriceUpper</c>; 0 for no upper
/// bound.
/// </param>
/// <param name="IncludeClosedTenders">
/// <see langword="true"/> to return closed tender items as well, the <c>includeClosedTenders</c> value of
/// <c>chkClosedTenders</c>.
/// </param>
public sealed record ShopSearchRequest(
    long? CountryId,
    long? RegionId,
    long? ItemCategoryId,
    ShopItemKind ItemKind,
    decimal PriceLow,
    decimal PriceHigh,
    bool IncludeClosedTenders);

/// <summary>
/// Response of <c>GET /api/app/shop/reference</c>: the contents of the search plinth's combos and the session's current
/// search criteria, the values <c>ShopItemsForm.formLoad</c> sets into the plinth's controls.
/// </summary>
/// <param name="Countries">The <c>cmbCountry</c> entries in name order, each with its regions.</param>
/// <param name="Categories">The <c>cmbCategory</c> entries in name order.</param>
/// <param name="CurrentSearch">
/// The criteria held by the session's shop item search, which select the <c>cmbCountry</c>, <c>cmbRegion</c> and
/// <c>cmbCategory</c> entries, the item-type option, the <c>txtPriceLower</c> and <c>txtPriceUpper</c> texts and the
/// <c>chkClosedTenders</c> value.
/// </param>
public sealed record ShopReferenceResponse(
    IReadOnlyList<ShopCountryOption> Countries,
    IReadOnlyList<ShopCategoryOption> Categories,
    ShopSearchRequest CurrentSearch);

/// <summary>
/// One row of the shop's <c>tblSearchResults</c> table: the values <c>getSearchResultString</c> writes into the row's
/// cells, and whether <c>tblSearchResults_displayRow</c> paints the row red.
/// </summary>
/// <param name="Id">The row id of the item.</param>
/// <param name="IsTender">
/// <see langword="true"/> for a tender item, whose first cell is Bid; <see langword="false"/> for a retail item, whose
/// first cell is Buy.
/// </param>
/// <param name="Code">
/// The item code, category prefix followed by code number; <see langword="null"/> when the item has no category.
/// </param>
/// <param name="Name">The item name.</param>
/// <param name="ListedDate">The date the item was listed, shown in short date format.</param>
/// <param name="Price">The item price, shown in currency format; for a tender item, its reserve price.</param>
/// <param name="IsClosed">
/// <see langword="true"/> for a tender item whose closure date is before the server date, as <c>TenderItem.isClosed</c>
/// reports, a row painted red; <see langword="false"/> for an open tender item and for a retail item.
/// </param>
public sealed record ShopSearchRow(
    long Id,
    bool IsTender,
    string? Code,
    string Name,
    DateOnly ListedDate,
    decimal Price,
    bool IsClosed);

/// <summary>
/// Response of <c>POST /api/app/shop/search</c>: the rows of <c>tblSearchResults</c> for the session's shop item search.
/// </summary>
/// <param name="Items">The rows in the order the item search returns them, the company's items by code number.</param>
public sealed record ShopSearchResponse(IReadOnlyList<ShopSearchRow> Items);

/// <summary>
/// Request of <c>POST /api/app/shop/cart/items</c>: the item to add to the session cart and the offer typed for it, the
/// arguments of <c>addItem(item, txtTender.text.Decimal)</c>.
/// </summary>
/// <param name="ItemId">
/// The row id of the item selected to buy or bid on; <see langword="null"/> when no item is selected, which the cart
/// rejects with the item-required error.
/// </param>
/// <param name="Offer">The decimal value of the offer typed in <c>txtTender</c>, the bid of a tender line.</param>
public sealed record AddCartItemRequest(long? ItemId, decimal Offer);

/// <summary>
/// Request of <c>POST /api/app/shop/cart/remove</c>: the cart line selected in <c>ShopCheckoutForm.tblItems</c>.
/// </summary>
/// <param name="ItemId">
/// The row id of the item of the selected cart line; <see langword="null"/> when no line is selected, which the cart
/// rejects with the no-item-selected error.
/// </param>
public sealed record RemoveCartItemRequest(long? ItemId);

/// <summary>
/// Response of the cart operations <c>POST /api/app/shop/cart/items</c>, <c>POST /api/app/shop/cart/remove</c> and
/// <c>POST /api/app/shop/cart/empty</c>: the outcome of the operation and the session cart after it.
/// </summary>
/// <param name="Result">
/// The outcome of the operation: its success flag, its errors in the order added and their full error text.
/// </param>
/// <param name="Cart">The session cart after the operation: its lines, total and size, shown in <c>tblCart</c>.</param>
public sealed record CartOperationResponse(OperationResult Result, CartView Cart);

/// <summary>
/// Response of <c>POST /api/app/shop/checkout</c>: the outcome of checking out the session cart and, when it succeeded,
/// the confirmation the confirmation page shows.
/// </summary>
/// <param name="Result">
/// The outcome of the checkout: its success flag, the errors of every cart line in line order, and their full error
/// text, which the checkout page shows when the checkout failed.
/// </param>
/// <param name="Receipt">
/// The confirmation lines captured from the cart lines before the cart was cleared, the rows of
/// <c>ShopConfirmationForm.tblSales</c>; <see langword="null"/> when the checkout failed.
/// </param>
public sealed record CheckoutResponse(OperationResult Result, CheckoutReceipt? Receipt);

/// <summary>
/// One tender the session client has already placed on a tender item, a tender
/// <c>WebItemsForm.doBuyFromDetails</c> compares with the offer typed in <c>txtTender</c>.
/// </summary>
/// <param name="Id">The row id of the tender.</param>
/// <param name="Offer">The offer of the tender.</param>
/// <param name="DateTendered">The timestamp of the tender, to the millisecond.</param>
public sealed record OwnTenderOffer(long Id, decimal Offer, DateTime DateTendered);

/// <summary>
/// Response of <c>GET /api/app/items/{id}</c>: the details the shop's item details view shows for one item, with the
/// session client's own tenders on it.
/// </summary>
/// <param name="Item">
/// The read model of the retail or tender item, with <see cref="ItemProjection.Photo"/> <see langword="null"/>; the
/// photo bytes are served by <c>GET /api/app/items/{id}/photo</c>.
/// </param>
/// <param name="HasPhoto"><see langword="true"/> when the item has a photo; otherwise <see langword="false"/>.</param>
/// <param name="IsClosed">
/// <see langword="true"/> for a tender item whose closure date is before the server date, as <c>TenderItem.isClosed</c>
/// reports; <see langword="false"/> for an open tender item and for a retail item.
/// </param>
/// <param name="OwnTenders">
/// The tenders the session client has placed on a tender item, in the item's offer-descending, then tender-time order
/// (<c>allTendersByOfferTime</c>); empty for a retail item and when the session has no client.
/// </param>
public sealed record ItemDetailsResponse(
    ItemProjection Item,
    bool HasPhoto,
    bool IsClosed,
    IReadOnlyList<OwnTenderOffer> OwnTenders);

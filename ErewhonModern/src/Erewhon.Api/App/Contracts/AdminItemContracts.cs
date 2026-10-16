using Erewhon.Application.Projections;

namespace Erewhon.Api.App.Contracts;

/// <summary>
/// Request of <c>POST /api/app/admin/items/search</c>, carrying the filter values
/// <c>ItemsListForm.populateItemSearch</c> copies from the items list form into its <c>ItemSearch</c>.
/// A <see langword="null"/> id applies no filter on that value.
/// </summary>
/// <param name="AgentId">
/// The row id of the agent whose items are listed, the <c>myAgent</c> filter: the session agent for an agent, otherwise
/// the agent selected in <c>cmbAgent</c>; <see langword="null"/> for any agent.
/// </param>
/// <param name="ClientId">
/// The row id of the client the listed items were sold to, the <c>myClient</c> filter selected in <c>cmbClient</c>;
/// <see langword="null"/> for any client.
/// </param>
/// <param name="CountryId">
/// The row id of the country the listed items are in, the <c>myCountry</c> filter selected in <c>cmbCountry</c>;
/// <see langword="null"/> for any country.
/// </param>
/// <param name="RegionId">
/// The row id of the region the listed items are in, the <c>myRegion</c> filter selected in <c>cmbRegion</c>;
/// <see langword="null"/> for any region.
/// </param>
/// <param name="ItemCategoryId">
/// The row id of the category the listed items belong to, the <c>ItemSearch.myItemCategory</c> filter, which the items
/// list form leaves unset; <see langword="null"/> for any category.
/// </param>
/// <param name="OnlyRetailItems">
/// <see langword="true"/> to list retail items only, the <c>onlyRetailItems</c> flag set when <c>optRetailItems</c> is
/// chosen instead of <c>optAllItems</c>.
/// </param>
/// <param name="OnlyTenderItems">
/// <see langword="true"/> to list tender items only, the <c>onlyTenderItems</c> flag set when <c>optTenderItems</c> is
/// chosen instead of <c>optAllItems</c>.
/// </param>
/// <param name="PriceLow">
/// The lowest price listed, inclusive, the <c>priceLow</c> decimal value of <c>txtMinPrice</c>; 0 for no lower bound.
/// </param>
/// <param name="PriceHigh">
/// The highest price listed, inclusive, the <c>priceHigh</c> decimal value of <c>txtMaxPrice</c>; 0 for no upper bound.
/// </param>
/// <param name="IncludeSoldItems">
/// <see langword="true"/> to list sold items as well, the <c>includeSoldItems</c> value of <c>chkIncludeSoldItems</c>.
/// </param>
/// <param name="IncludeClosedTenders">
/// <see langword="true"/> to list closed tender items as well, the <c>includeClosedTenders</c> value of
/// <c>chkIncludeClosedTenders</c>.
/// </param>
public sealed record AdminItemSearchRequest(
    long? AgentId,
    long? ClientId,
    long? CountryId,
    long? RegionId,
    long? ItemCategoryId,
    bool OnlyRetailItems,
    bool OnlyTenderItems,
    decimal PriceLow,
    decimal PriceHigh,
    bool IncludeSoldItems,
    bool IncludeClosedTenders);

/// <summary>
/// One item row of the admin item screens: a row of <c>ItemsListForm.tblItems</c>, an entry of
/// <c>ItemsListByCategoryForm.lstItems</c>, or the item located by <c>FindItemModal</c> from the code entered in
/// <c>txtItem</c>.
/// </summary>
/// <param name="Item">The read model of the retail or tender item, with <see cref="ItemProjection.Photo"/> <see langword="null"/>.</param>
/// <param name="IsClosed">
/// <see langword="true"/> for a tender item whose closure date is before the server date, as <c>TenderItem.isClosed</c>
/// reports; <see langword="false"/> for an open tender item and for a retail item.
/// </param>
public sealed record ItemListRow(ItemProjection Item, bool IsClosed);

/// <summary>
/// Response of <c>GET /api/app/admin/items/by-category</c>: the items of one category, the entries
/// <c>ItemsListByCategoryForm.loadItemsInList</c> adds under that category's folder in <c>lstItems</c>.
/// </summary>
/// <param name="ItemCategoryId">The row id of the category, the folder entry selected in <c>lstItems</c>.</param>
/// <param name="Items">The category's items in list order.</param>
public sealed record ItemsByCategoryResponse(long ItemCategoryId, IReadOnlyList<ItemListRow> Items);

/// <summary>
/// Request that creates or updates a retail item, carrying the values <c>EditItemForm.populateTAFromForm</c> and
/// <c>EditRetailItemForm.populateTAFromForm</c> copy from the retail item editor into its <c>RetailItemTA</c>.
/// Text values are sent untrimmed.
/// </summary>
/// <param name="CodeNumber">
/// The item's code number, the <c>codeNumber</c> integer value of <c>txtCodeNumber</c>; 0 to allocate the company's
/// next code number.
/// </param>
/// <param name="Name">The item name, the <c>name</c> text of <c>txtShortDesc</c>.</param>
/// <param name="Description">The item description, the <c>description</c> text of <c>txtFullDesc</c>.</param>
/// <param name="ListedDate">
/// The date the item is listed, the TA's <c>listedDate</c>, which the editor form does not set. On a create,
/// <see langword="null"/> gives the server date; on an update, <see langword="null"/> keeps the item's stored listed
/// date. A date replaces it.
/// </param>
/// <param name="PhotoBase64">
/// The base64 text of the picture file loaded into <c>picItem</c>, the <c>photo</c> value;
/// <see langword="null"/> for no picture.
/// </param>
/// <param name="Price">The retail price, the <c>price</c> decimal value of <c>txtRetailPrice</c>.</param>
/// <param name="AgentId">
/// The row id of the agent selling the item, the <c>myAgent</c> value: the session agent for an agent, otherwise the
/// agent selected in <c>cmbAgent</c>; <see langword="null"/> when none is selected.
/// </param>
/// <param name="ItemCategoryId">
/// The row id of the item's category, the <c>myItemCategory</c> selected in <c>cmbCategory</c>;
/// <see langword="null"/> when none is selected.
/// </param>
/// <param name="RegionId">
/// The row id of the region the item is listed in, the <c>myRegion</c> selected in <c>cmbRegion</c>;
/// <see langword="null"/> when none is selected.
/// </param>
public sealed record RetailItemRequest(
    int CodeNumber,
    string? Name,
    string? Description,
    DateOnly? ListedDate,
    string? PhotoBase64,
    decimal Price,
    long? AgentId,
    long? ItemCategoryId,
    long? RegionId);

/// <summary>
/// Request that creates or updates a tender item, carrying the values <c>EditItemForm.populateTAFromForm</c> and
/// <c>EditTenderItemForm.populateTAFromForm</c> copy from the tender item editor into its <c>TenderItemTA</c>.
/// Text values are sent untrimmed.
/// </summary>
/// <param name="CodeNumber">
/// The item's code number, the <c>codeNumber</c> integer value of <c>txtCodeNumber</c>; 0 to allocate the company's
/// next code number.
/// </param>
/// <param name="Name">The item name, the <c>name</c> text of <c>txtShortDesc</c>.</param>
/// <param name="Description">The item description, the <c>description</c> text of <c>txtFullDesc</c>.</param>
/// <param name="ListedDate">
/// The date the item is listed, the TA's <c>listedDate</c>, which the editor form does not set. On a create,
/// <see langword="null"/> gives the server date; on an update, <see langword="null"/> keeps the item's stored listed
/// date. A date replaces it.
/// </param>
/// <param name="PhotoBase64">
/// The base64 text of the picture file loaded into <c>picItem</c>, the <c>photo</c> value;
/// <see langword="null"/> for no picture.
/// </param>
/// <param name="Price">The reserve price, the <c>price</c> decimal value of <c>txtReservePrice</c>.</param>
/// <param name="AgentId">
/// The row id of the agent selling the item, the <c>myAgent</c> value: the session agent for an agent, otherwise the
/// agent selected in <c>cmbAgent</c>; <see langword="null"/> when none is selected.
/// </param>
/// <param name="ItemCategoryId">
/// The row id of the item's category, the <c>myItemCategory</c> selected in <c>cmbCategory</c>;
/// <see langword="null"/> when none is selected.
/// </param>
/// <param name="RegionId">
/// The row id of the region the item is listed in, the <c>myRegion</c> selected in <c>cmbRegion</c>;
/// <see langword="null"/> when none is selected.
/// </param>
/// <param name="ClosureDate">
/// The date tendering closes, the <c>closureDate</c> date value of <c>txtClosureDate</c>;
/// <see langword="null"/> when no valid date is entered.
/// </param>
public sealed record TenderItemRequest(
    int CodeNumber,
    string? Name,
    string? Description,
    DateOnly? ListedDate,
    string? PhotoBase64,
    decimal Price,
    long? AgentId,
    long? ItemCategoryId,
    long? RegionId,
    DateOnly? ClosureDate);

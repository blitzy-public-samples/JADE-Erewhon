using Erewhon.Application.Domain;
using Erewhon.Application.Projections;

namespace Erewhon.Api.App.Contracts;

/// <summary>
/// Request of <c>POST /api/app/admin/tenders/search</c>, carrying the filter values
/// <c>TendersListForm</c> copies from its filter controls into its <c>TenderSearch</c>.
/// A <see langword="null"/> id applies no filter on that value.
/// </summary>
/// <param name="AgentId">
/// The row id of the agent whose items' tenders are listed, the <c>myAgent</c> filter selected in <c>cmbAgent</c>;
/// <see langword="null"/> for any agent.
/// </param>
/// <param name="ClientId">
/// The row id of the client whose tenders are listed, the <c>myClient</c> filter selected in <c>cmbClient</c>;
/// <see langword="null"/> for any client.
/// </param>
/// <param name="ItemId">
/// The row id of the tender item whose tenders are listed, the <c>myItem</c> filter selected in <c>cmbItem</c>;
/// <see langword="null"/> for any tender item.
/// </param>
/// <param name="IncludeOpenTenders">
/// <see langword="true"/> to list open tenders, the <c>includeOpenTenders</c> value of <c>chkOpenTenders</c>,
/// initially <see langword="true"/>.
/// </param>
/// <param name="IncludeClosedTenders">
/// <see langword="true"/> to list closed tenders, the <c>includeClosedTenders</c> value of <c>chkClosedTenders</c>,
/// initially <see langword="false"/>.
/// </param>
/// <param name="IncludeSuccessfulTenders">
/// <see langword="true"/> to list successful tenders, the <c>includeSuccessfulTenders</c> value of
/// <c>chkSuccessfulTenders</c>, initially <see langword="false"/>.
/// </param>
public sealed record AdminTenderSearchRequest(
    long? AgentId,
    long? ClientId,
    long? ItemId,
    bool IncludeOpenTenders,
    bool IncludeClosedTenders,
    bool IncludeSuccessfulTenders);

/// <summary>
/// One row of <c>TendersListForm.tblTenders</c>, holding the cell values <c>tblTenders_displayRow</c> paints for a
/// tender and the state that colours the row: red for a closed tender, green for the winning tender.
/// </summary>
/// <param name="Id">The row id of the tender, the row's <c>itemObject</c>.</param>
/// <param name="Status">The tender's status at the server date, the <c>getStatus</c> cell.</param>
/// <param name="TenderDate">The date part of <paramref name="DateTendered"/>, the <c>getDate</c> cell.</param>
/// <param name="DateTendered">The time the tender was placed, the tender's <c>dateTendered</c>.</param>
/// <param name="ClosureDate">The tender item's closure date, the <c>myTenderItem.closureDate</c> cell.</param>
/// <param name="ClientId">The row id of the client who placed the tender.</param>
/// <param name="ClientName">The name of the client who placed the tender, the <c>myClient.name</c> cell.</param>
/// <param name="TenderItemId">The row id of the tender item the tender is placed on.</param>
/// <param name="ItemCode">
/// The tender item's code, the <c>myTenderItem.getCode</c> part of the item cell; <see langword="null"/> when the item
/// has no category.
/// </param>
/// <param name="ItemName">The tender item's name, the <c>myTenderItem.name</c> part of the item cell.</param>
/// <param name="ItemPrice">The tender item's reserve price, the <c>myTenderItem.price</c> cell.</param>
/// <param name="Offer">The amount offered, the <c>offer</c> cell.</param>
/// <param name="AgentName">
/// The name of the tender item's agent, the <c>myTenderItem.myAgent.name</c> cell; <see langword="null"/> when the
/// item has no agent.
/// </param>
/// <param name="IsWinning">
/// <see langword="true"/> when the tender item's tender sale awards the item to this tender, the tender's
/// <c>myTenderSale</c> being set; the green rows, whose Delete <c>doDeleteTender</c> refuses with
/// <c>$CannotDeleteTenderItemSold</c>.
/// </param>
public sealed record TenderListRow(
    long Id,
    TenderStatus Status,
    DateOnly TenderDate,
    DateTime DateTendered,
    DateOnly ClosureDate,
    long ClientId,
    string ClientName,
    long TenderItemId,
    string? ItemCode,
    string ItemName,
    decimal ItemPrice,
    decimal Offer,
    string? AgentName,
    bool IsWinning);

/// <summary>
/// Response of <c>GET /api/app/admin/tenders/{id}</c>, holding the values <c>ViewTenderForm.displayObject</c> and
/// <c>ViewTenderForm.formLoad</c> show for one tender.
/// </summary>
/// <param name="Tender">
/// The read model of the tender with its client and tender item, the source of <c>txtDateTendered</c>,
/// <c>txtClient</c>, <c>txtOffer</c>, <c>txtPrefix</c>, <c>txtNumber</c>, <c>txtCategory</c>, <c>txtItemName</c>,
/// <c>txtDescription</c>, <c>txtPrice</c>, <c>txtClosureDate</c>, <c>txtAgent</c> and <c>picPhoto</c>.
/// </param>
/// <param name="Status">The tender's status at the server date, as <c>Tender.getStatus</c> reports it.</param>
/// <param name="IsWinning">
/// <see langword="true"/> when this tender is the tender of the item's tender sale, the case in which
/// <c>formLoad</c> disables <c>btnDelete</c>.
/// </param>
/// <param name="ItemSold">
/// <see langword="true"/> when the tender item has a sale, the case in which <c>formLoad</c> shows
/// <c>lblTenderSale</c>.
/// </param>
/// <param name="WinnerClientName">
/// The name of the client the item was sold to, the first <c>$SoldToClientFor</c> argument shown in
/// <c>lblTenderSale</c>; <see langword="null"/> when <paramref name="ItemSold"/> is <see langword="false"/>.
/// </param>
/// <param name="WinningOffer">
/// The offer of the tender that won the item, the second <c>$SoldToClientFor</c> argument shown in
/// <c>lblTenderSale</c>; <see langword="null"/> when <paramref name="ItemSold"/> is <see langword="false"/>.
/// </param>
public sealed record TenderDetailsResponse(
    TenderProjection Tender,
    TenderStatus Status,
    bool IsWinning,
    bool ItemSold,
    string? WinnerClientName,
    decimal? WinningOffer);

namespace Erewhon.Application.Cart;

/// <summary>
/// The confirmation of a successful checkout: one line per checked-out cart line, captured before those lines are removed from the cart.
/// </summary>
/// <param name="Lines">The receipt lines, in cart order (ascending item id).</param>
public sealed record CheckoutReceipt(IReadOnlyList<CheckoutReceiptLine> Lines);

/// <summary>
/// One row of the checkout confirmation, for a retail purchase or a tender bid.
/// </summary>
/// <param name="ItemId">The row id of the cart line's item.</param>
/// <param name="Transaction">The cart line's transaction text: "Retail" or "Tender".</param>
/// <param name="Code">The item code held by the cart line.</param>
/// <param name="ItemName">The item name read after the commit.</param>
/// <param name="ItemPrice">The item price read after the commit, unformatted.</param>
/// <param name="Offer">The cart line's offer, unformatted.</param>
/// <param name="IsTender"><see langword="true"/> when the line is a tender bid; <see langword="false"/> for a retail purchase.</param>
public sealed record CheckoutReceiptLine(
    long ItemId,
    string Transaction,
    string Code,
    string ItemName,
    decimal ItemPrice,
    decimal Offer,
    bool IsTender);

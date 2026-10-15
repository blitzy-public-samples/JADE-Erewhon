namespace Erewhon.Application.Cart;

/// <summary>
/// One line of a shopping cart: a retail purchase or a tender bid on one item, with the request values captured when the line was added.
/// </summary>
public sealed class CartItem
{
    private readonly object _dateSoldGate = new();
    private DateTime? _dateSold;

    /// <summary>The internal id of the line's item.</summary>
    public required long ItemId { get; init; }

    /// <summary>True for a tender item line; false for a retail item line.</summary>
    public required bool IsTender { get; init; }

    /// <summary>The item code (category prefix followed by code number) read when the line was added; at most 11 characters.</summary>
    public required string Code { get; init; }

    /// <summary>The transaction type text: the tender item type text ("Tender") or the retail item type text ("Retail").</summary>
    public required string Transaction { get; init; }

    /// <summary>The offer passed when the line was added, rounded to 2 decimal places; normally 0 on a retail line.</summary>
    public required decimal Offer { get; init; }

    /// <summary>Retail lines: the item price copied when the line was added, unchanged by any later repricing of the item. Tender lines: 0.</summary>
    public decimal SalePrice { get; init; }

    /// <summary>The id of the session client when the line was added; 0 when the session had no client.</summary>
    public long ClientId { get; init; }

    /// <summary>Tender lines: the server timestamp taken when the line was added. Retail lines: null.</summary>
    public DateTime? DateTendered { get; init; }

    /// <summary>
    /// Retail lines: null until <see cref="RetainDateSold(DateTime)"/> first assigns the sale timestamp, which then stays on the line for every later checkout attempt. Tender lines: null.
    /// Reads are synchronized with <see cref="RetainDateSold(DateTime)"/>.
    /// </summary>
    public DateTime? DateSold
    {
        get
        {
            lock (_dateSoldGate)
            {
                return _dateSold;
            }
        }
    }

    /// <summary>
    /// Assigns <paramref name="dateSold"/> as the line's sale timestamp when the line has none, and returns the line's sale timestamp.
    /// The check and the assignment are one atomic step, so overlapping calls keep one value and all return it, and a later call never replaces it.
    /// </summary>
    /// <param name="dateSold">The candidate sale timestamp, stored exactly as given when the line has no sale timestamp.</param>
    /// <returns>The line's sale timestamp after the call: the value the first successful call assigned.</returns>
    /// <exception cref="InvalidOperationException">The line is a tender line; <see cref="DateSold"/> stays null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="dateSold"/> is <c>default(DateTime)</c>, the null timestamp; <see cref="DateSold"/> is left unchanged.
    /// </exception>
    public DateTime RetainDateSold(DateTime dateSold)
    {
        if (IsTender)
        {
            throw new InvalidOperationException("A tender line has no sale timestamp.");
        }

        if (dateSold == default)
        {
            throw new ArgumentOutOfRangeException(nameof(dateSold), "The sale timestamp must not be the null timestamp.");
        }

        lock (_dateSoldGate)
        {
            _dateSold ??= dateSold;
            return _dateSold.Value;
        }
    }
}

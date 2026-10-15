using Erewhon.Application.Common;

namespace Erewhon.Application.Domain;

/// <summary>
/// A tender (bid) by a client on a tender item: the amount offered, the time it was placed and,
/// when it won the item, its tender sale.
/// </summary>
public class Tender : PersistentModel
{
    /// <summary>The row id of the owning company.</summary>
    public long CompanyId { get; set; }

    /// <summary>The owning company; deleting the company deletes the tender.</summary>
    public Company? Company { get; set; }

    /// <summary>The row id of the client who placed the tender.</summary>
    public long ClientId { get; set; }

    /// <summary>The client who placed the tender; deleting the client deletes the tender.</summary>
    public Client? Client { get; set; }

    /// <summary>The row id of the tender item the tender is placed on.</summary>
    public long TenderItemId { get; set; }

    /// <summary>The tender item the tender is placed on; deleting the item deletes the tender.</summary>
    public TenderItem? TenderItem { get; set; }

    /// <summary>The amount offered, to 2 decimal places.</summary>
    public decimal Offer { get; set; }

    /// <summary>
    /// The time the tender was placed, to the millisecond, in database server local time with
    /// <see cref="DateTimeKind.Unspecified"/>; for a tender from a shopping cart, the time its line was added.
    /// </summary>
    public DateTime DateTendered { get; set; }

    /// <summary>The tender sale awarding the item to this tender; <see langword="null"/> unless the tender won the item.</summary>
    public TenderSale? TenderSale { get; set; }

    /// <summary>The class number of <c>Tender</c>, <see cref="ObjectIdentity.Tender"/>.</summary>
    public override int JadeClassNumber => ObjectIdentity.Tender;

    /// <summary>Returns the date part of <see cref="DateTendered"/>.</summary>
    /// <returns>The date the tender was placed.</returns>
    public DateOnly GetDate() => DateOnly.FromDateTime(DateTendered);

    /// <summary>Returns the time-of-day part of <see cref="DateTendered"/>, including milliseconds.</summary>
    /// <returns>The time the tender was placed.</returns>
    public TimeOnly GetTime() => TimeOnly.FromDateTime(DateTendered);

    /// <summary>
    /// Returns <see cref="TenderStatus.Successful"/> when <see cref="TenderSale"/> is set; otherwise
    /// <see cref="TenderStatus.Closed"/> when the closure date of <see cref="TenderItem"/> is before
    /// <paramref name="serverDate"/>; otherwise <see cref="TenderStatus.Open"/>.
    /// </summary>
    /// <param name="serverDate">The current date on the database server.</param>
    /// <returns>The status of the tender.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="TenderSale"/> is <see langword="null"/> and <see cref="TenderItem"/> is not loaded.
    /// </exception>
    public TenderStatus GetStatus(DateOnly serverDate)
    {
        if (TenderSale is not null)
        {
            return TenderStatus.Successful;
        }

        return RequireTenderItem().ClosureDate < serverDate ? TenderStatus.Closed : TenderStatus.Open;
    }

    /// <summary>
    /// Returns the developer description <c>&lt;oid&gt; Tender: $&lt;offer&gt;, &lt;date tendered&gt;, &lt;client name&gt;</c>
    /// with the offer to 2 decimal places and the date tendered as <c>dd MMMM yyyy, HH:mm:ss</c>,
    /// e.g. <c>5789.8 Tender: $500.00, 10 May 2024, 09:15:00, Clive Entworth</c> for the tender with id 8.
    /// </summary>
    /// <returns>The debug string.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Client"/> is not loaded.</exception>
    public string GetDebugString() =>
        ObjectIdentity.Of(this) + " Tender: $" + JadeFormat.DecimalString(Offer) + ", "
        + JadeFormat.TimeStampString(DateTendered) + ", " + RequireClient().Name;

    private TenderItem RequireTenderItem() =>
        TenderItem ?? throw new InvalidOperationException("Tender.TenderItem is not loaded.");

    private Client RequireClient() =>
        Client ?? throw new InvalidOperationException("Tender.Client is not loaded.");
}

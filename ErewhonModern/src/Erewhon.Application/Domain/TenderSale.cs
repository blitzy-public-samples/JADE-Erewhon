using Erewhon.Application.Common;

namespace Erewhon.Application.Domain;

/// <summary>
/// The sale of a tender item to the client whose tender won it at closure; the sale date and time are those of the winning tender.
/// </summary>
public class TenderSale : Sale
{
    /// <summary>The row id of the winning tender; <see langword="null"/> once that tender is deleted.</summary>
    public long? TenderId { get; set; }

    /// <summary>The winning tender, whose <see cref="Domain.Tender.TenderSale"/> is this sale; deleting the tender clears it.</summary>
    public Tender? Tender { get; set; }

    /// <summary>The class number of <c>TenderSale</c>, <see cref="ObjectIdentity.TenderSale"/>.</summary>
    public override int JadeClassNumber => ObjectIdentity.TenderSale;

    /// <summary>Returns the date the winning tender was placed, <see cref="Domain.Tender.GetDate"/>.</summary>
    /// <returns>The sale date.</returns>
    /// <exception cref="NoCompanyException"><see cref="TenderId"/> is <see langword="null"/> or 0.</exception>
    /// <exception cref="InvalidOperationException"><see cref="TenderId"/> is set and <see cref="Tender"/> is not loaded.</exception>
    public override DateOnly? GetDate() => RequireTender().GetDate();

    /// <summary>Returns the time the winning tender was placed, <see cref="Domain.Tender.GetTime"/>.</summary>
    /// <returns>The sale time.</returns>
    /// <exception cref="NoCompanyException"><see cref="TenderId"/> is <see langword="null"/> or 0.</exception>
    /// <exception cref="InvalidOperationException"><see cref="TenderId"/> is set and <see cref="Tender"/> is not loaded.</exception>
    public TimeOnly GetTime() => RequireTender().GetTime();

    /// <summary>
    /// Returns the developer description
    /// <c>&lt;oid&gt; TenderSale: $&lt;offer&gt;, &lt;tender date&gt;, &lt;tender time&gt;, &lt;code&gt; (&lt;item name&gt;), &lt;client name&gt;</c>
    /// with the winning offer to 2 decimal places, the tender date as <c>dd MMMM yyyy</c> and the tender time as <c>HH:mm:ss</c>,
    /// e.g. <c>5784.7 TenderSale: $500.00, 10 May 2024, 09:15:00, CATI1 (Proxy Tender), Clive Entworth</c> for the tender sale
    /// with id 7; the code part is empty when the item has no category.
    /// </summary>
    /// <returns>The debug string.</returns>
    /// <exception cref="NoCompanyException"><see cref="TenderId"/> is <see langword="null"/> or 0.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="TenderId"/> is set and <see cref="Tender"/> is not loaded, or <see cref="Sale.Item"/> or
    /// <see cref="Sale.Client"/> is not loaded.
    /// </exception>
    public override string GetDebugString()
    {
        var tender = RequireTender();
        var offer = JadeFormat.DecimalString(tender.Offer);
        var date = JadeFormat.DateString(tender.GetDate());
        var time = JadeFormat.TimeString(tender.GetTime());
        var item = RequireItem();
        var client = RequireClient();

        return ObjectIdentity.Of(this) + " TenderSale: $" + offer + ", " + date + ", " + time + ", "
            + item.GetCode() + " (" + item.Name + "), " + client.Name;
    }

    private Tender RequireTender()
    {
        if (Tender is not null)
        {
            return Tender;
        }

        if (JadeValues.IsNull(TenderId))
        {
            throw new NoCompanyException();
        }

        throw new InvalidOperationException("TenderSale.Tender is not loaded.");
    }

    private Item RequireItem() =>
        Item ?? throw new InvalidOperationException("TenderSale.Item is not loaded.");

    private Client RequireClient() =>
        Client ?? throw new InvalidOperationException("TenderSale.Client is not loaded.");
}

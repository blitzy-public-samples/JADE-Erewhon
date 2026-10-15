namespace Erewhon.Api.Rest.Contracts;

/// <summary>
/// A tender (bid) placed by a client on a tender item.
/// </summary>
public sealed class TenderDto
{
    /// <summary>
    /// Source GUID of the tender. At most 39 characters.
    /// </summary>
    public string? Guid { get; set; }

    /// <summary>
    /// Client who placed the tender.
    /// </summary>
    public ClientDto? Client { get; set; }

    /// <summary>
    /// Tender item the tender was placed on.
    /// </summary>
    public TenderItemDto? TenderItem { get; set; }

    /// <summary>
    /// Amount offered, to 2 decimal places.
    /// </summary>
    public decimal Offer { get; set; }

    /// <summary>
    /// Date and time the tender was placed.
    /// </summary>
    public DateTime? Timestamp { get; set; }
}

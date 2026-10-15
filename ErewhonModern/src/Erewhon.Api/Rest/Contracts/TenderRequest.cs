namespace Erewhon.Api.Rest.Contracts;

/// <summary>
/// Request to place a tender on a tender item for a client.
/// </summary>
public sealed class TenderRequest
{
    /// <summary>
    /// Name of the client placing the tender.
    /// </summary>
    public string? Client { get; set; }

    /// <summary>
    /// Category prefix part of the item code.
    /// </summary>
    public string? CodePrefix { get; set; }

    /// <summary>
    /// Number part of the item code.
    /// </summary>
    public int CodeNumber { get; set; }

    /// <summary>
    /// Amount offered for the tender item.
    /// </summary>
    public decimal Offer { get; set; }
}

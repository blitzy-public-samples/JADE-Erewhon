namespace Erewhon.Api.Rest.Contracts;

/// <summary>
/// Request to record a retail sale of an item to a client.
/// </summary>
public sealed class RetailSaleRequest
{
    /// <summary>
    /// Name of the client buying the item.
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
}

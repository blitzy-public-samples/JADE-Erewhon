namespace Erewhon.Application.Domain;

/// <summary>Status of a tender relative to its tender item and the server date.</summary>
public enum TenderStatus
{
    /// <summary>The tender item's closure date is not before the server date and the tender has no tender sale.</summary>
    Open,

    /// <summary>The tender item's closure date is before the server date and this tender did not win the item.</summary>
    Closed,

    /// <summary>The tender has a tender sale: it won the item.</summary>
    Successful,
}

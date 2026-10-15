namespace Erewhon.Application.Notifications;

/// <summary>Kind of change raised for a persisted row.</summary>
public enum ChangeKind
{
    /// <summary>The row was created.</summary>
    Added,

    /// <summary>The row was updated.</summary>
    Updated,

    /// <summary>The row was deleted.</summary>
    Deleted,
}

/// <summary>Change notification for one persisted row.</summary>
/// <param name="EntityType">Domain type name of the changed row, such as <c>Agent</c>, <c>RetailItem</c> or <c>TenderSale</c>.</param>
/// <param name="Id">Internal row id of the changed row.</param>
/// <param name="Kind">Kind of change.</param>
/// <param name="Info">Optional extra text carried with the change.</param>
public sealed record ChangeEvent(string EntityType, long Id, ChangeKind Kind, string? Info = null);

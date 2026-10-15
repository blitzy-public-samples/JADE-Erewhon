using Erewhon.Application.Common;

namespace Erewhon.Application.Domain;

/// <summary>
/// Base of every persisted entity: the row identity, the source GUID and the soft edit lock.
/// </summary>
public abstract class PersistentModel : IJadeIdentifiable
{
    /// <summary>The row id; 0 while the entity is unsaved.</summary>
    public long Id { get; set; }

    /// <summary>The GUID text of the entity, at most 39 characters; empty until assigned.</summary>
    public string Guid { get; set; } = string.Empty;

    /// <summary>The id of the session holding the soft edit lock, at most 64 characters; <see langword="null"/> when unlocked.</summary>
    public string? LockedBySession { get; set; }

    /// <summary>The time the soft edit lock was set, to the millisecond, with <see cref="DateTimeKind.Unspecified"/>; <see langword="null"/> when unlocked.</summary>
    public DateTime? LockedTimestamp { get; set; }

    /// <summary>The class number of the entity's own class, one of the <see cref="ObjectIdentity"/> constants.</summary>
    public abstract int JadeClassNumber { get; }

    /// <summary>True when any session, including the caller's own, holds the soft edit lock.</summary>
    public bool IsSoftLocked => !JadeValues.IsNull(LockedBySession);

    /// <summary>
    /// Returns whether the session <paramref name="sessionId"/> holds the soft edit lock.
    /// </summary>
    /// <param name="sessionId">The session id to compare with the lock owner.</param>
    /// <returns><see langword="true"/> when the entity is soft locked and <see cref="LockedBySession"/> equals <paramref name="sessionId"/> by ordinal comparison; otherwise <see langword="false"/>.</returns>
    public bool IsSoftLockedBy(string sessionId) =>
        IsSoftLocked && string.Equals(LockedBySession, sessionId, StringComparison.Ordinal);

    /// <summary>
    /// Sets the soft edit lock to the session <paramref name="sessionId"/> at <paramref name="timestamp"/>.
    /// </summary>
    /// <param name="sessionId">The id of the session taking the lock.</param>
    /// <param name="timestamp">The time the lock is taken; stored with <see cref="DateTimeKind.Unspecified"/>.</param>
    public void SetSoftLock(string sessionId, DateTime timestamp)
    {
        LockedBySession = sessionId;
        LockedTimestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Clears the soft edit lock: sets <see cref="LockedBySession"/> and <see cref="LockedTimestamp"/> to <see langword="null"/>.
    /// </summary>
    public void ClearSoftLock()
    {
        LockedBySession = null;
        LockedTimestamp = null;
    }

    /// <summary>
    /// Returns a new GUID as lowercase hexadecimal in braces, e.g. <c>{be4f4fe3-e216-ee11-9fac-0050568d0023}</c> (38 characters).
    /// </summary>
    /// <returns>The GUID text.</returns>
    public static string GenerateGuid() => System.Guid.NewGuid().ToString("B");
}

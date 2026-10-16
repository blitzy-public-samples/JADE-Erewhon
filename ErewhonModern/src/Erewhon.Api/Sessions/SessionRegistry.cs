using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Search;
using Erewhon.Application.Services;

namespace Erewhon.Api.Sessions;

/// <summary>
/// One logged-on session of the React app: its id, logon area, role, user code, agent, client and shop search
/// criteria. Only <see cref="SessionRegistry"/> creates entries, and the agent and client change only through
/// <see cref="SessionRegistry.SetAgent"/> and <see cref="SessionRegistry.SetClient"/>.
/// </summary>
/// <remarks>
/// <see cref="AgentId"/> and <see cref="ClientId"/> are safe to read and change from several threads at once.
/// <see cref="ShopSearch"/> is not synchronised: its members are not safe to call from several threads at once.
/// The entry also counts the <see cref="SessionLease"/> objects held on it and records whether
/// <see cref="SessionRegistry.Remove(string)"/> has ended it; both change under the same lock as the agent and client,
/// and an ended entry takes no further lease.
/// </remarks>
public sealed class SessionEntry
{
    private readonly object _sync = new();
    private long? _agentId;
    private long? _clientId;
    private int _leaseCount;
    private bool _ended;

    internal SessionEntry(
        string sessionId,
        LogonArea area,
        SessionRole role,
        long? agentId,
        long? clientId,
        string userName)
    {
        SessionId = sessionId;
        Area = area;
        Role = role;
        _agentId = agentId;
        _clientId = clientId;
        UserName = userName;
    }

    /// <summary>
    /// The session id: 32 lowercase hexadecimal characters, within <see cref="FieldLengths.LockedBySession"/>.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// The area the session logged on to.
    /// </summary>
    public LogonArea Area { get; }

    /// <summary>
    /// The role of the session user; never <see cref="SessionRole.None"/>.
    /// </summary>
    public SessionRole Role { get; }

    /// <summary>
    /// The internal id (<c>agents.id</c>) of the session agent, or <see langword="null"/> when no agent is set.
    /// </summary>
    public long? AgentId
    {
        get
        {
            lock (_sync)
            {
                return _agentId;
            }
        }
    }

    /// <summary>
    /// The internal id (<c>clients.id</c>) of the session client, or <see langword="null"/> when no client is set.
    /// </summary>
    public long? ClientId
    {
        get
        {
            lock (_sync)
            {
                return _clientId;
            }
        }
    }

    /// <summary>
    /// The user code the session logged on with: the client name in the shop area, the agent name for an agent, or the
    /// company name for the Administrator.
    /// </summary>
    public string UserName { get; }

    /// <summary>
    /// The shop item search criteria of the session, created empty with the entry and kept for its lifetime.
    /// </summary>
    public ItemSearchCriteria ShopSearch { get; } = new();

    internal void SetAgentId(long? agentId)
    {
        lock (_sync)
        {
            _agentId = agentId;
        }
    }

    internal void SetClientId(long? clientId)
    {
        lock (_sync)
        {
            _clientId = clientId;
        }
    }

    /// <summary>
    /// Adds one lease to the entry unless it has ended.
    /// </summary>
    /// <returns><see langword="true"/> when the lease was added; <see langword="false"/> when the entry has ended.</returns>
    internal bool TryAddLease()
    {
        lock (_sync)
        {
            if (_ended)
            {
                return false;
            }

            _leaseCount++;
            return true;
        }
    }

    /// <summary>
    /// Takes one lease off the entry.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the entry has ended and this was its last lease; otherwise <see langword="false"/>.
    /// </returns>
    internal bool ReleaseLease()
    {
        lock (_sync)
        {
            _leaseCount--;
            return _ended && _leaseCount == 0;
        }
    }

    /// <summary>
    /// Marks the entry ended; an ended entry takes no further lease.
    /// </summary>
    /// <returns><see langword="true"/> when the entry holds no lease; otherwise <see langword="false"/>.</returns>
    internal bool End()
    {
        lock (_sync)
        {
            _ended = true;
            return _leaseCount == 0;
        }
    }
}

/// <summary>
/// A hold on a stored <see cref="SessionEntry"/>, taken by <see cref="SessionRegistry.TryAcquire"/> and given back by
/// <see cref="Dispose"/>. While any lease on a session is held, <see cref="SessionRegistry.Remove(string)"/> leaves
/// the session's cart in place, and disposing the last lease of a removed session discards its cart.
/// </summary>
public sealed class SessionLease : IDisposable
{
    private readonly SessionRegistry _registry;
    private int _disposed;

    internal SessionLease(SessionRegistry registry, SessionEntry entry)
    {
        _registry = registry;
        Entry = entry;
    }

    /// <summary>
    /// The session the lease is held on.
    /// </summary>
    public SessionEntry Entry { get; }

    /// <summary>
    /// Gives the lease back. When the session has been removed and this was its last lease, discards the session's
    /// cart. Calling it more than once has no further effect.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _registry.Release(Entry);
    }
}

/// <summary>
/// Holds the logged-on sessions of the React app in process memory, keyed by the ordinal session id.
/// Sessions do not expire: each lasts until <see cref="Remove(string)"/> discards it or the process ends.
/// <see cref="TryAcquire"/> takes a <see cref="SessionLease"/> on a stored session; a removed session's cart is
/// discarded by <see cref="Remove(string)"/> when no lease on the session is held, and otherwise by the disposal of
/// its last lease. Every member is safe to call from several threads at once.
/// </summary>
/// <param name="cartStore">
/// The carts of the sessions; a session's cart is discarded once the session is removed and holds no lease.
/// </param>
public sealed class SessionRegistry(ICartStore cartStore)
{
    private const int SessionIdByteCount = 16;

    private readonly ICartStore _cartStore = cartStore ?? throw new ArgumentNullException(nameof(cartStore));

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Raised with the session id after <see cref="Remove(string)"/> has removed a session from the registry and ended
    /// its entry, before <see cref="Remove(string)"/> returns. The session's cart has been discarded when no lease on
    /// the session was held; otherwise the disposal of its last <see cref="SessionLease"/> discards it later.
    /// </summary>
    public event Action<string>? SessionRemoved;

    /// <summary>
    /// Creates and stores a session with a new random id for a logged-on user.
    /// </summary>
    /// <param name="area">The area the user logged on to.</param>
    /// <param name="role">The role of the user.</param>
    /// <param name="agentId">The internal id (<c>agents.id</c>) of the session agent, or <see langword="null"/>.</param>
    /// <param name="clientId">The internal id (<c>clients.id</c>) of the session client, or <see langword="null"/>.</param>
    /// <param name="userName">The user code the user logged on with.</param>
    /// <returns>The stored entry, whose <see cref="SessionEntry.SessionId"/> is 32 lowercase hexadecimal characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="userName"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="area"/> is not a defined <see cref="LogonArea"/>, or <paramref name="role"/> is
    /// <see cref="SessionRole.None"/> or not a defined <see cref="SessionRole"/>.
    /// </exception>
    public SessionEntry Create(LogonArea area, SessionRole role, long? agentId, long? clientId, string userName)
    {
        ArgumentNullException.ThrowIfNull(userName);
        if (!Enum.IsDefined(area))
        {
            throw new ArgumentOutOfRangeException(nameof(area), area, "The logon area is not defined.");
        }

        if (role == SessionRole.None || !Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "A session requires a Client, Agent or Administrator role.");
        }

        while (true)
        {
            var entry = new SessionEntry(NewSessionId(), area, role, agentId, clientId, userName);
            if (_sessions.TryAdd(entry.SessionId, entry))
            {
                return entry;
            }
        }
    }

    /// <summary>
    /// Looks up a stored session by its exact id.
    /// </summary>
    /// <param name="sessionId">The session id; null, empty or white space finds nothing.</param>
    /// <param name="entry">The stored entry when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a session with the id is stored; otherwise <see langword="false"/>.</returns>
    public bool TryGet(string? sessionId, [MaybeNullWhen(false)] out SessionEntry entry)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            entry = null;
            return false;
        }

        return _sessions.TryGetValue(sessionId, out entry);
    }

    /// <summary>
    /// Takes a lease on a stored session by its exact id. The lease is taken only while the session is stored and
    /// <see cref="Remove(string)"/> has not ended its entry; the caller disposes it.
    /// </summary>
    /// <param name="sessionId">The session id; null, empty or white space finds nothing.</param>
    /// <param name="lease">The lease on the stored entry when taken; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the lease was taken; otherwise <see langword="false"/>.</returns>
    public bool TryAcquire(string? sessionId, [MaybeNullWhen(false)] out SessionLease lease)
    {
        if (TryGet(sessionId, out var entry) && entry.TryAddLease())
        {
            lease = new SessionLease(this, entry);
            return true;
        }

        lease = null;
        return false;
    }

    /// <summary>
    /// Sets the agent of the stored session, or clears it when <paramref name="agentId"/> is <see langword="null"/>,
    /// leaving its client unchanged. Does nothing when no session with the id is stored.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="agentId">The internal id (<c>agents.id</c>) of the agent, or <see langword="null"/>.</param>
    public void SetAgent(string sessionId, long? agentId)
    {
        if (TryGet(sessionId, out var entry))
        {
            entry.SetAgentId(agentId);
        }
    }

    /// <summary>
    /// Sets the client of the stored session, or clears it when <paramref name="clientId"/> is <see langword="null"/>,
    /// leaving its agent unchanged. Does nothing when no session with the id is stored.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="clientId">The internal id (<c>clients.id</c>) of the client, or <see langword="null"/>.</param>
    public void SetClient(string sessionId, long? clientId)
    {
        if (TryGet(sessionId, out var entry))
        {
            entry.SetClientId(clientId);
        }
    }

    /// <summary>
    /// Removes the stored session from the registry, ends its entry, after which <see cref="TryAcquire"/> takes no
    /// lease on it, discards its cart when no <see cref="SessionLease"/> on it is held, and then raises
    /// <see cref="SessionRemoved"/>. While a lease is held, the disposal of the session's last lease discards the cart.
    /// Does nothing when no session with the id is stored.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns><see langword="true"/> when the session was stored and has been removed; otherwise <see langword="false"/>.</returns>
    public bool Remove(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryRemove(sessionId, out var entry))
        {
            return false;
        }

        if (entry.End())
        {
            _cartStore.RemoveCart(sessionId);
        }

        SessionRemoved?.Invoke(sessionId);
        return true;
    }

    /// <summary>
    /// Takes one lease off <paramref name="entry"/> and, when the entry has ended and this was its last lease, discards
    /// the session's cart.
    /// </summary>
    /// <param name="entry">The entry the lease was held on.</param>
    internal void Release(SessionEntry entry)
    {
        if (entry.ReleaseLease())
        {
            _cartStore.RemoveCart(entry.SessionId);
        }
    }

    private static string NewSessionId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(SessionIdByteCount)).ToLowerInvariant();
}

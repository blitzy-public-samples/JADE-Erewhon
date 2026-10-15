using System.Collections.Concurrent;
using Erewhon.Application.Abstractions;

namespace Erewhon.Application.Cart;

/// <summary>
/// Holds the shopping cart of each session in process memory, keyed by the ordinal session id.
/// Carts are not persisted and last until <see cref="RemoveCart(string)"/> discards them or the process ends.
/// Every member is safe to call from several threads at once.
/// </summary>
public sealed class InMemoryCartStore : ICartStore
{
    private readonly ConcurrentDictionary<string, ShoppingCart> _carts = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the cart of the session, creating an empty cart the first time a session id is used.
    /// </summary>
    /// <param name="sessionId">The id of the session that owns the cart.</param>
    /// <returns>The session's cart; the same instance for every call until the cart is removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public ShoppingCart GetCart(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        return _carts.GetOrAdd(sessionId, static id => new ShoppingCart(id));
    }

    /// <summary>
    /// Discards the cart of the session. Does nothing when the session has no cart.
    /// </summary>
    /// <param name="sessionId">The id of the session whose cart is discarded.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public void RemoveCart(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        _carts.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Removes the line for the item from every cart that holds one, leaving the other lines unchanged.
    /// </summary>
    /// <param name="itemId">The internal id of the item.</param>
    /// <returns>
    /// The sessions whose cart held the item, each once, in ordinal session-id order; an empty list when no cart held it.
    /// </returns>
    public IReadOnlyList<string> RemoveItemFromAllCarts(long itemId)
    {
        var sessionIds = new List<string>();
        foreach (var cart in _carts.Values)
        {
            if (cart.Remove(itemId))
            {
                sessionIds.Add(cart.SessionId);
            }
        }

        sessionIds.Sort(StringComparer.Ordinal);
        return sessionIds;
    }
}

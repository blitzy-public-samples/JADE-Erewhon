using Erewhon.Application.Cart;

namespace Erewhon.Application.Abstractions;

/// <summary>
/// Holds one transient <see cref="ShoppingCart"/> per session id for the lifetime of the session.
/// Cart lines are keyed by the internal item id (<c>items.id</c>), so a retail item and a tender item
/// that share a GUID are separate lines.
/// Implementations are safe to call from several threads at once, for any mix of sessions.
/// </summary>
public interface ICartStore
{
    /// <summary>
    /// Returns the cart of the session, creating and registering an empty cart for the session on
    /// first use. Every later call with the same session id returns that same cart instance until
    /// <see cref="RemoveCart(string)"/> discards it.
    /// </summary>
    /// <param name="sessionId">The id of the session that owns the cart.</param>
    /// <returns>The session's cart.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    ShoppingCart GetCart(string sessionId);

    /// <summary>
    /// Discards the cart of the session together with its lines. Does nothing when the session has
    /// no cart. A later <see cref="GetCart(string)"/> for the session id returns a new empty cart.
    /// </summary>
    /// <param name="sessionId">The id of the session whose cart is discarded.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    void RemoveCart(string sessionId);

    /// <summary>
    /// Removes the line for the item from every registered cart that holds one, leaving each cart's
    /// other lines unchanged.
    /// </summary>
    /// <param name="itemId">The internal id (<c>items.id</c>) of the item.</param>
    /// <returns>
    /// The id of each session whose cart held a line for the item, each id once; an empty list when
    /// no cart held one.
    /// </returns>
    IReadOnlyList<string> RemoveItemFromAllCarts(long itemId);
}

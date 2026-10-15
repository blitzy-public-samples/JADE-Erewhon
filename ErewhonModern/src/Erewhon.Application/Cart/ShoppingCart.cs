namespace Erewhon.Application.Cart;

/// <summary>
/// The cart lines of one session: at most one line per item id, held in ascending item-id order.
/// Every member is safe to call from several threads at once.
/// </summary>
public sealed class ShoppingCart
{
    private readonly object _gate = new();
    private readonly SortedDictionary<long, CartItem> _lines = new();

    /// <summary>Creates an empty cart for the given session.</summary>
    /// <param name="sessionId">The id of the session that owns the cart.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public ShoppingCart(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        SessionId = sessionId;
    }

    /// <summary>The id of the session that owns the cart.</summary>
    public string SessionId { get; }

    /// <summary>
    /// A snapshot of the cart lines in ascending item-id order. Later changes to the cart do not alter a returned snapshot.
    /// </summary>
    public IReadOnlyList<CartItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _lines.Values.ToList();
            }
        }
    }

    /// <summary><see langword="true"/> when the cart holds no lines.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count == 0;
            }
        }
    }

    /// <summary>The number of lines in the cart.</summary>
    public int Size
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count;
            }
        }
    }

    /// <summary>Returns <see langword="true"/> when the cart holds a line for the item id.</summary>
    /// <param name="itemId">The internal id of the item.</param>
    public bool Contains(long itemId)
    {
        lock (_gate)
        {
            return _lines.ContainsKey(itemId);
        }
    }

    /// <summary>Adds a line for the line's item id.</summary>
    /// <param name="item">The line to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The cart already holds a line for the item id; the cart is left unchanged.
    /// </exception>
    public void Add(CartItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            if (!_lines.TryAdd(item.ItemId, item))
            {
                throw new InvalidOperationException("Key already used in this dictionary");
            }
        }
    }

    /// <summary>Removes the line for the item id.</summary>
    /// <param name="itemId">The internal id of the item.</param>
    /// <returns><see langword="true"/> when a line was removed; <see langword="false"/> when the cart held no line for the item id.</returns>
    public bool Remove(long itemId)
    {
        lock (_gate)
        {
            return _lines.Remove(itemId);
        }
    }

    /// <summary>Removes every line from the cart.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
    }
}

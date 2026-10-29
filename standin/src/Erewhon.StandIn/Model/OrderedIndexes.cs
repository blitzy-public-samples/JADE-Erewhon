using System.Collections;

namespace Erewhon.StandIn.Model;

/// <summary>
/// Root of every stand-in entity, the counterpart of JADE <c>PersistentModel</c>. Entities have
/// reference identity; equality is never overridden.
/// </summary>
public abstract class PersistentModel
{
    /// <summary>Creates an entity with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number; 0 for the company itself.</param>
    protected PersistentModel(long oid)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(oid);
        Oid = oid;
    }

    /// <summary>
    /// Creation sequence number within one company graph, standing in for the JADE object
    /// identifier: it orders reference-valued member keys and is the final tie-break between equal
    /// composite keys.
    /// </summary>
    public long Oid { get; }

    /// <summary>JADE <c>guid: String[39]</c>; the empty string is the JADE null string.</summary>
    public string Guid { get; internal set; } = "";
}

/// <summary>
/// In-memory JADE <c>MemberKeyDictionary</c>: entries are held in member-key order, and entries
/// whose composite keys are equal are held in <see cref="PersistentModel.Oid"/> (creation) order.
/// </summary>
/// <remarks>
/// <para>
/// Membership is by reference, held in a reference-identity set beside the ordered entries, so
/// <see cref="Includes"/> and the duplicate check of <see cref="Add"/> take constant time.
/// <see cref="Remove"/> and <see cref="Rekey"/> find an entry by binary search on its keys, and by
/// a scan only when its key members changed since it was placed. Changing a key member of an
/// included entry leaves the order stale until <see cref="Rekey"/> is called for it, so callers
/// re-key immediately after such a change. A key comparison that throws leaves the dictionary
/// unchanged.
/// </para>
/// <para>
/// The Erewhon dictionaries do not allow duplicate keys in JADE, which raises exception 1310 for
/// one. Here an equal composite key is kept in creation order instead of raised: the transaction
/// agents already reject the duplicates JADE validation rejects (names, commission percentages),
/// and no seed row or request produces any other duplicate.
/// </para>
/// <para>Not thread-safe: the stand-in serialises every request under one lock.</para>
/// </remarks>
/// <typeparam name="T">Element type of the dictionary.</typeparam>
public abstract class MemberKeyDictionary<T> : IReadOnlyCollection<T>
    where T : PersistentModel
{
    private readonly List<T> _entries = [];

    // The same references as _entries: every successful Add and Remove changes both.
    private readonly HashSet<T> _members = new(ReferenceEqualityComparer.Instance);

    /// <summary>Number of entries.</summary>
    public int Count => _entries.Count;

    /// <summary>True when the dictionary has no entries.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>
    /// Compares the member keys of two entries in key order, each key ascending unless declared
    /// <c>descending</c>; returns a negative, zero or positive value.
    /// </summary>
    protected abstract int CompareKeys(T x, T y);

    /// <summary>Adds an entry at the position its current keys dictate.</summary>
    /// <exception cref="InvalidOperationException">The same reference is already included.</exception>
    public void Add(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_members.Contains(item))
        {
            // JADE raises exception 1309 when an object is added to a collection that holds it.
            throw new InvalidOperationException(
                $"{GetType().Name} already includes this {item.GetType().Name} (oid {item.Oid}).");
        }

        // The position is found before either structure changes, so a key comparison that throws
        // leaves the dictionary as it was.
        int index = InsertionIndex(item, excludedIndex: -1);
        _entries.Insert(index, item);
        _members.Add(item);
    }

    /// <summary>
    /// Removes an entry by reference, which works even after its key members changed.
    /// </summary>
    /// <returns>True when the entry was included.</returns>
    public bool Remove(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_members.Contains(item))
        {
            return false;
        }

        _entries.RemoveAt(IndexOfMember(item));
        _members.Remove(item);
        return true;
    }

    /// <summary>
    /// Moves an included entry to the position its current keys dictate; does nothing when the
    /// entry is not included.
    /// </summary>
    public void Rekey(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_members.Contains(item))
        {
            return;
        }

        // Both positions are found before the entries change, so a key comparison that throws
        // leaves the entry where it was. Membership is unchanged.
        int index = IndexOfMember(item);
        int target = InsertionIndex(item, excludedIndex: index);
        if (target != index)
        {
            _entries.RemoveAt(index);
            _entries.Insert(target, item);
        }
    }

    /// <summary>True when this exact reference is included; false for null.</summary>
    public bool Includes(T? item) => item is not null && _members.Contains(item);

    /// <summary>The first entry in key order, or null when empty.</summary>
    public T? First() => _entries.Count == 0 ? null : _entries[0];

    /// <summary>The last entry in key order, or null when empty.</summary>
    public T? Last() => _entries.Count == 0 ? null : _entries[^1];

    /// <summary>
    /// Iterates a snapshot in forward key order, so the dictionary may be changed while iterating.
    /// </summary>
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_entries.ToArray()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>A snapshot in reverse key order (JADE <c>foreach ... reversed</c>).</summary>
    public IReadOnlyList<T> Reversed()
    {
        T[] snapshot = _entries.ToArray();
        Array.Reverse(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Returns the first entry, in key order, whose key equals the probe (JADE <c>getAtKey</c>), or
    /// null when none does.
    /// </summary>
    /// <param name="compareEntryToKey">
    /// Sign of the entry's key against the probe key, using the comparers of
    /// <see cref="CompareKeys"/>.
    /// </param>
    protected T? FindFirst(Func<T, int> compareEntryToKey)
    {
        ArgumentNullException.ThrowIfNull(compareEntryToKey);
        int low = 0;
        int high = _entries.Count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (compareEntryToKey(_entries[mid]) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low < _entries.Count && compareEntryToKey(_entries[low]) == 0 ? _entries[low] : null;
    }

    /// <summary>
    /// Returns a snapshot of every entry, in key order, whose key equals the probe; empty when none
    /// does. Two binary searches bound the matching range, and only that range is copied.
    /// </summary>
    /// <param name="compareEntryToKey">
    /// Sign of the entry's key against the probe key, using the comparers of
    /// <see cref="CompareKeys"/>; the probe may cover only the leading keys.
    /// </param>
    protected IReadOnlyList<T> FindAll(Func<T, int> compareEntryToKey)
    {
        ArgumentNullException.ThrowIfNull(compareEntryToKey);

        // Lower bound: the first entry that does not sort before the probe.
        int low = 0;
        int high = _entries.Count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (compareEntryToKey(_entries[mid]) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        // Upper bound: the first entry after the start that sorts after the probe.
        int start = low;
        high = _entries.Count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (compareEntryToKey(_entries[mid]) <= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        if (low == start)
        {
            return [];
        }

        var matches = new T[low - start];
        _entries.CopyTo(start, matches, 0, matches.Length);
        return matches;
    }

    // Upper bound under the total order (keys, then Oid): the item goes after every entry that
    // sorts before or equal to it. With excludedIndex >= 0 the search runs over the entries without
    // the one at that index, and the result is the position in the list once it is removed.
    private int InsertionIndex(T item, int excludedIndex)
    {
        int low = 0;
        int high = excludedIndex < 0 ? _entries.Count : _entries.Count - 1;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            T entry = _entries[excludedIndex < 0 || mid < excludedIndex ? mid : mid + 1];
            if (CompareEntries(entry, item) <= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    // Index of an included entry. An entry still at the position its keys dictate is found by
    // binary search under the total order (keys, then Oid); one whose key members changed and that
    // has not been re-keyed yet is found by scanning.
    private int IndexOfMember(T item)
    {
        int low = 0;
        int high = _entries.Count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (CompareEntries(_entries[mid], item) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        for (int i = low; i < _entries.Count && CompareEntries(_entries[i], item) == 0; i++)
        {
            if (ReferenceEquals(_entries[i], item))
            {
                return i;
            }
        }

        int index = IndexOfReference(item);
        if (index < 0)
        {
            // Unreachable while every change goes through Add and Remove, which update both.
            throw new InvalidOperationException(
                $"{GetType().Name} has this {item.GetType().Name} (oid {item.Oid}) in its " +
                "membership set but not in its entries.");
        }

        return index;
    }

    private int CompareEntries(T x, T y)
    {
        int byKeys = CompareKeys(x, y);
        return byKeys != 0 ? byKeys : x.Oid.CompareTo(y.Oid);
    }

    private int IndexOfReference(T item)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (ReferenceEquals(_entries[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// A dictionary keyed by one case-sensitive <c>name</c> member, with JADE <c>getAtKey</c> lookup.
/// </summary>
/// <typeparam name="T">Element type of the dictionary.</typeparam>
public abstract class NameKeyDictionary<T> : MemberKeyDictionary<T>
    where T : PersistentModel
{
    /// <summary>The entry with this exact name (ordinal comparison), or null.</summary>
    public T? this[string name] => GetAtKey(name);

    /// <summary>The entry with this exact name (ordinal comparison), or null.</summary>
    public T? GetAtKey(string name) => FindFirst(entry => KeyOrder.Text(NameOf(entry), name));

    /// <summary>The name member of an entry.</summary>
    protected abstract string NameOf(T item);

    /// <inheritdoc />
    protected sealed override int CompareKeys(T x, T y) => KeyOrder.Text(NameOf(x), NameOf(y));
}

/// <summary>JADE <c>AgentsByNameDict</c> of <see cref="Agent"/>, keyed by <c>name</c>.</summary>
public sealed class AgentsByNameDict : NameKeyDictionary<Agent>
{
    /// <inheritdoc />
    protected override string NameOf(Agent item) => item.Name;
}

/// <summary>JADE <c>ClientsByNameDict</c> of <see cref="Client"/>, keyed by <c>name</c>.</summary>
public sealed class ClientsByNameDict : NameKeyDictionary<Client>
{
    /// <inheritdoc />
    protected override string NameOf(Client item) => item.Name;
}

/// <summary>JADE <c>CountriesByNameDict</c> of <see cref="Country"/>, keyed by <c>name</c>.</summary>
public sealed class CountriesByNameDict : NameKeyDictionary<Country>
{
    /// <inheritdoc />
    protected override string NameOf(Country item) => item.Name;
}

/// <summary>JADE <c>RegionsByNameDict</c> of <see cref="Region"/>, keyed by <c>name</c>.</summary>
public sealed class RegionsByNameDict : NameKeyDictionary<Region>
{
    /// <inheritdoc />
    protected override string NameOf(Region item) => item.Name;
}

/// <summary>
/// JADE <c>ItemCategoriesByNameDict</c> of <see cref="ItemCategory"/>, keyed by <c>name</c>.
/// </summary>
public sealed class ItemCategoriesByNameDict : NameKeyDictionary<ItemCategory>
{
    /// <inheritdoc />
    protected override string NameOf(ItemCategory item) => item.Name;
}

/// <summary>
/// JADE <c>ItemCategoriesByPrefixDict</c> of <see cref="ItemCategory"/>, keyed by
/// <c>prefix caseInsensitive Latin1</c>.
/// </summary>
public sealed class ItemCategoriesByPrefixDict : MemberKeyDictionary<ItemCategory>
{
    /// <summary>The category whose prefix equals this one ignoring case, or null.</summary>
    public ItemCategory? GetAtKey(string prefix) =>
        FindFirst(entry => KeyOrder.TextIgnoreCase(entry.Prefix, prefix));

    /// <inheritdoc />
    protected override int CompareKeys(ItemCategory x, ItemCategory y) =>
        KeyOrder.TextIgnoreCase(x.Prefix, y.Prefix);
}

/// <summary>
/// JADE <c>CommissionRatesByCategoryDict</c> of <see cref="CommissionRate"/>, keyed by
/// <c>myItemCategory.name</c>.
/// </summary>
public sealed class CommissionRatesByCategoryDict : MemberKeyDictionary<CommissionRate>
{
    /// <summary>The rate whose category has exactly this name, or null.</summary>
    public CommissionRate? GetAtKey(string categoryName) =>
        FindFirst(entry => KeyOrder.Text(CategoryNameOf(entry), categoryName));

    /// <inheritdoc />
    protected override int CompareKeys(CommissionRate x, CommissionRate y) =>
        KeyOrder.Text(CategoryNameOf(x), CategoryNameOf(y));

    private static string CategoryNameOf(CommissionRate rate) => rate.MyItemCategory?.Name ?? "";
}

/// <summary>
/// JADE <c>CommissionRatesByPercentDict</c> of <see cref="CommissionRate"/>, keyed by
/// <c>percentage</c>.
/// </summary>
public sealed class CommissionRatesByPercentDict : MemberKeyDictionary<CommissionRate>
{
    /// <summary>The rate with this percentage (compared by value), or null.</summary>
    public CommissionRate? GetAtKey(decimal percentage) =>
        FindFirst(entry => KeyOrder.Value(entry.Percentage, percentage));

    /// <inheritdoc />
    protected override int CompareKeys(CommissionRate x, CommissionRate y) =>
        KeyOrder.Value(x.Percentage, y.Percentage);
}

/// <summary>
/// JADE <c>ItemsByNumberDict</c> of <see cref="Item"/> (retail and tender), keyed by
/// <c>codeNumber</c>.
/// </summary>
public sealed class ItemsByNumberDict : MemberKeyDictionary<Item>
{
    /// <summary>The item with this code number, or null.</summary>
    public Item? GetAtKey(int codeNumber) =>
        FindFirst(entry => KeyOrder.Value(entry.CodeNumber, codeNumber));

    /// <inheritdoc />
    protected override int CompareKeys(Item x, Item y) => KeyOrder.Value(x.CodeNumber, y.CodeNumber);
}

/// <summary>
/// JADE <c>ItemsByPrefixByNumberDict</c> of <see cref="Item"/>, keyed by
/// <c>myItemCategory.prefix</c>, <c>codeNumber</c>.
/// </summary>
public sealed class ItemsByPrefixByNumberDict : MemberKeyDictionary<Item>
{
    /// <summary>The item with exactly this category prefix and code number, or null.</summary>
    public Item? GetAtKey(string prefix, int code) =>
        FindFirst(entry => KeyOrder.ItemCode(entry, prefix, code));

    /// <inheritdoc />
    protected override int CompareKeys(Item x, Item y) => KeyOrder.ItemCode(x, y);
}

/// <summary>
/// JADE <c>ItemsByCategoryPrefixDict</c> of <see cref="Item"/>, keyed by <c>myItemCategory</c>
/// (reference), <c>myItemCategory.prefix</c>, <c>codeNumber</c>.
/// </summary>
public sealed class ItemsByCategoryPrefixDict : MemberKeyDictionary<Item>
{
    /// <inheritdoc />
    protected override int CompareKeys(Item x, Item y)
    {
        int byCategory = KeyOrder.Reference(x.MyItemCategory, y.MyItemCategory);
        return byCategory != 0 ? byCategory : KeyOrder.ItemCode(x, y);
    }
}

/// <summary>JADE <c>RetailItemsByGUIDDict</c> of <see cref="RetailItem"/>, keyed by <c>guid</c>.</summary>
public sealed class RetailItemsByGUIDDict : MemberKeyDictionary<RetailItem>
{
    /// <summary>The retail item with exactly this guid, or null.</summary>
    public RetailItem? GetAtKey(string guid) => FindFirst(entry => KeyOrder.Text(entry.Guid, guid));

    /// <inheritdoc />
    protected override int CompareKeys(RetailItem x, RetailItem y) => KeyOrder.Text(x.Guid, y.Guid);
}

/// <summary>JADE <c>TenderItemsByGUIDDict</c> of <see cref="TenderItem"/>, keyed by <c>guid</c>.</summary>
public sealed class TenderItemsByGUIDDict : MemberKeyDictionary<TenderItem>
{
    /// <summary>The tender item with exactly this guid, or null.</summary>
    public TenderItem? GetAtKey(string guid) => FindFirst(entry => KeyOrder.Text(entry.Guid, guid));

    /// <inheritdoc />
    protected override int CompareKeys(TenderItem x, TenderItem y) => KeyOrder.Text(x.Guid, y.Guid);
}

/// <summary>
/// JADE <c>TenderItemsBySaleDateCodeDict</c> of <see cref="TenderItem"/>, keyed by
/// <c>mySale descending</c>, <c>closureDate descending</c>, <c>myItemCategory.prefix</c>,
/// <c>codeNumber</c>.
/// </summary>
/// <remarks>
/// A null sale sorts lowest, so in forward order unsold items come after every sold one, and
/// <see cref="MemberKeyDictionary{T}.Reversed"/> lists unsold items first, by ascending closure
/// date. Tender closure walks the reversed order and stops at the first sold or not-yet-due item.
/// </remarks>
public sealed class TenderItemsBySaleDateCodeDict : MemberKeyDictionary<TenderItem>
{
    /// <inheritdoc />
    protected override int CompareKeys(TenderItem x, TenderItem y)
    {
        int bySale = KeyOrder.Descending(KeyOrder.Reference(x.MySale, y.MySale));
        if (bySale != 0)
        {
            return bySale;
        }

        int byClosure = KeyOrder.Descending(KeyOrder.Value(x.ClosureDate, y.ClosureDate));
        return byClosure != 0 ? byClosure : KeyOrder.ItemCode(x, y);
    }
}

/// <summary>
/// JADE <c>SalesByItemDict</c> of <see cref="Sale"/>, keyed by
/// <c>myItem.myItemCategory.prefix</c>, <c>myItem.codeNumber</c>.
/// </summary>
public sealed class SalesByItemDict : MemberKeyDictionary<Sale>
{
    /// <summary>
    /// The sale of the item with exactly this category prefix (ordinal, not trimmed) and code
    /// number, or null.
    /// </summary>
    public Sale? GetAtKey(string prefix, int code) =>
        FindFirst(entry => KeyOrder.ItemCode(entry.MyItem, prefix, code));

    /// <inheritdoc />
    protected override int CompareKeys(Sale x, Sale y) => KeyOrder.ItemCode(x.MyItem, y.MyItem);
}

/// <summary>
/// JADE <c>RetailSalesByTimeItemDict</c> of <see cref="RetailSale"/>, keyed by <c>dateSold</c>,
/// <c>myItem.myItemCategory.prefix</c>, <c>myItem.codeNumber</c>.
/// </summary>
public sealed class RetailSalesByTimeItemDict : MemberKeyDictionary<RetailSale>
{
    /// <inheritdoc />
    protected override int CompareKeys(RetailSale x, RetailSale y)
    {
        int byTime = KeyOrder.Value(x.DateSold, y.DateSold);
        return byTime != 0 ? byTime : KeyOrder.ItemCode(x.MyItem, y.MyItem);
    }
}

/// <summary>
/// JADE <c>TenderSalesByTimeItemDict</c> of <see cref="TenderSale"/>, keyed by
/// <c>myTender.dateTendered</c>, <c>myItem.myItemCategory.prefix</c>, <c>myItem.codeNumber</c>.
/// </summary>
public sealed class TenderSalesByTimeItemDict : MemberKeyDictionary<TenderSale>
{
    /// <inheritdoc />
    protected override int CompareKeys(TenderSale x, TenderSale y)
    {
        // A sale without its tender takes the JADE null timestamp, which sorts first.
        int byTime = KeyOrder.Value(x.MyTender?.DateTendered ?? default, y.MyTender?.DateTendered ?? default);
        return byTime != 0 ? byTime : KeyOrder.ItemCode(x.MyItem, y.MyItem);
    }
}

/// <summary>
/// JADE <c>TendersByItemTimeOfferDict</c> of <see cref="Tender"/>, keyed by
/// <c>myTenderItem.myItemCategory.prefix</c>, <c>myTenderItem.codeNumber</c>,
/// <c>dateTendered</c>, <c>offer descending</c>.
/// </summary>
public sealed class TendersByItemTimeOfferDict : MemberKeyDictionary<Tender>
{
    /// <inheritdoc />
    protected override int CompareKeys(Tender x, Tender y)
    {
        int byItem = KeyOrder.ItemCode(x.MyTenderItem, y.MyTenderItem);
        if (byItem != 0)
        {
            return byItem;
        }

        int byTime = KeyOrder.Value(x.DateTendered, y.DateTendered);
        return byTime != 0 ? byTime : KeyOrder.Descending(KeyOrder.Value(x.Offer, y.Offer));
    }
}

/// <summary>
/// JADE <c>TendersByOfferTimeDict</c> of <see cref="Tender"/>, keyed by <c>offer descending</c>,
/// <c>dateTendered</c>: the first entry is the highest offer, the earliest tender among equal
/// offers.
/// </summary>
public sealed class TendersByOfferTimeDict : MemberKeyDictionary<Tender>
{
    /// <summary>
    /// A snapshot of the tenders whose offer equals this one by value (3.5 equals 3.50), in key
    /// order: earliest first. Empty when none does.
    /// </summary>
    public IReadOnlyList<Tender> WithOffer(decimal offer) =>
        FindAll(entry => KeyOrder.Descending(KeyOrder.Value(entry.Offer, offer)));

    /// <inheritdoc />
    protected override int CompareKeys(Tender x, Tender y)
    {
        int byOffer = KeyOrder.Descending(KeyOrder.Value(x.Offer, y.Offer));
        return byOffer != 0 ? byOffer : KeyOrder.Value(x.DateTendered, y.DateTendered);
    }
}

/// <summary>
/// JADE <c>TendersByTimeOfferDict</c> of <see cref="Tender"/>, keyed by <c>dateTendered</c>,
/// <c>offer descending</c>.
/// </summary>
public sealed class TendersByTimeOfferDict : MemberKeyDictionary<Tender>
{
    /// <inheritdoc />
    protected override int CompareKeys(Tender x, Tender y)
    {
        int byTime = KeyOrder.Value(x.DateTendered, y.DateTendered);
        return byTime != 0 ? byTime : KeyOrder.Descending(KeyOrder.Value(x.Offer, y.Offer));
    }
}

/// <summary>
/// Member-key comparison primitives. Every result is -1, 0 or 1, and no comparison is
/// culture-sensitive. A missing reference along a key path yields the JADE null value of the key's
/// type: the null string (<c>""</c>), 0, or a null reference.
/// </summary>
file static class KeyOrder
{
    // JADE orders keys without options by Latin-1 code point, which equals UTF-16 ordinal order
    // for Latin-1 text. The null string sorts first.
    public static int Text(string? x, string? y) => Math.Sign(string.CompareOrdinal(x ?? "", y ?? ""));

    public static int TextIgnoreCase(string? x, string? y) =>
        Math.Sign(StringComparer.OrdinalIgnoreCase.Compare(x ?? "", y ?? ""));

    // Integer, Decimal (by value, so 3.5 equals 3.50), Date and TimeStamp keys.
    public static int Value<TValue>(TValue x, TValue y)
        where TValue : IComparable<TValue> => Math.Sign(x.CompareTo(y));

    // Reference keys: null sorts lowest, otherwise object identifier order.
    public static int Reference(PersistentModel? x, PersistentModel? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        return y is null ? 1 : Math.Sign(x.Oid.CompareTo(y.Oid));
    }

    public static int Descending(int comparison) => -comparison;

    // The (myItemCategory.prefix, codeNumber) key pair of two items.
    public static int ItemCode(Item? x, Item? y) =>
        ItemCode(x, y?.MyItemCategory?.Prefix ?? "", y?.CodeNumber ?? 0);

    // The (myItemCategory.prefix, codeNumber) key pair of an item against a probe.
    public static int ItemCode(Item? entry, string prefix, int codeNumber)
    {
        int byPrefix = Text(entry?.MyItemCategory?.Prefix, prefix);
        return byPrefix != 0 ? byPrefix : Value(entry?.CodeNumber ?? 0, codeNumber);
    }
}

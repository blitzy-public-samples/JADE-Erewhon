using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Services;

/// <summary>
/// The stored state that the validation of a checkout's lines reads, read for every line at once: the ids of the
/// stored sales of each line's item; the reserve price and closure date of each tender line's tender item; the
/// database server's current date; and the ids of the stored tenders on each tender line's tender item by the line's
/// client with the line's offer. The snapshot covers an item only when exactly one line refers to it; for any other
/// item it holds nothing. The sales and tenders the change tracker holds as added or deleted are not part of it.
/// </summary>
/// <remarks>Instances are immutable and safe to read from several threads at once.</remarks>
public sealed class CheckoutValidationSnapshot
{
    private readonly Dictionary<long, IReadOnlyList<long>> _storedSaleIdsByItem;
    private readonly Dictionary<long, StoredTenderItem?> _tenderItems;
    private readonly Dictionary<SameOfferKey, IReadOnlyList<long>> _storedSameOfferTenderIds;

    private CheckoutValidationSnapshot(
        Dictionary<long, IReadOnlyList<long>> storedSaleIdsByItem,
        Dictionary<long, StoredTenderItem?> tenderItems,
        Dictionary<SameOfferKey, IReadOnlyList<long>> storedSameOfferTenderIds,
        DateOnly? serverDate)
    {
        _storedSaleIdsByItem = storedSaleIdsByItem;
        _tenderItems = tenderItems;
        _storedSameOfferTenderIds = storedSameOfferTenderIds;
        ServerDate = serverDate;
    }

    /// <summary>
    /// The database server's current date, read with the snapshot; <see langword="null"/> when the snapshot covers no
    /// tender item.
    /// </summary>
    public DateOnly? ServerDate { get; }

    /// <summary>
    /// Reads the snapshot of the given checkout lines without change tracking, in at most four queries whatever the
    /// number of lines: the stored sales of the covered items; the reserve price and closure date of the covered tender
    /// items, with the server date; and the stored tenders matching each covered tender line's tender item, client and
    /// offer. A query with nothing to read is not run. An item id of <see langword="null"/> or 0 refers to no item; a
    /// tender line with no client, or with an offer of 0, reads no matching tenders.
    /// </summary>
    /// <param name="db">The database the stored state is read from.</param>
    /// <param name="clock">The database server clock that supplies the server date.</param>
    /// <param name="retailItemIds">The item id of each retail line, in line order.</param>
    /// <param name="tenders">The tender command of each tender line, in line order.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="tenders"/> holds a <see langword="null"/> command.</exception>
    public static async Task<CheckoutValidationSnapshot> ReadAsync(
        IErewhonDbContext db,
        IServerClock clock,
        IEnumerable<long?> retailItemIds,
        IEnumerable<TenderCommand> tenders,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(retailItemIds);
        ArgumentNullException.ThrowIfNull(tenders);

        List<long> retailIds = retailItemIds
            .Where(id => !JadeValues.IsNull(id))
            .Select(id => id!.Value)
            .ToList();
        List<TenderCommand> tenderLines = tenders.ToList();
        if (tenderLines.Exists(tender => tender is null))
        {
            throw new ArgumentException("A tender line has no tender command.", nameof(tenders));
        }

        List<TenderCommand> referringTenderLines = tenderLines
            .Where(tender => !JadeValues.IsNull(tender.TenderItemId))
            .ToList();

        HashSet<long> coveredItemIds = retailIds
            .Concat(referringTenderLines.Select(tender => tender.TenderItemId!.Value))
            .GroupBy(id => id)
            .Where(group => group.Count() == 1)
            .Select(group => group.Key)
            .ToHashSet();

        List<TenderCommand> coveredTenderLines = referringTenderLines
            .Where(tender => coveredItemIds.Contains(tender.TenderItemId!.Value))
            .ToList();

        Dictionary<long, IReadOnlyList<long>> storedSaleIdsByItem =
            await ReadStoredSaleIdsAsync(db, coveredItemIds.ToArray(), ct);
        Dictionary<long, StoredTenderItem?> tenderItems = await ReadTenderItemsAsync(
            db,
            coveredTenderLines.Select(tender => tender.TenderItemId!.Value).ToArray(),
            ct);
        DateOnly? serverDate = tenderItems.Count == 0 ? null : await clock.GetServerDateAsync(ct);
        Dictionary<SameOfferKey, IReadOnlyList<long>> storedSameOfferTenderIds = await ReadSameOfferTenderIdsAsync(
            db,
            coveredTenderLines
                .Where(tender => !JadeValues.IsNull(tender.ClientId) && !JadeValues.IsNull(tender.Offer))
                .Select(tender => new SameOfferKey(tender.TenderItemId!.Value, tender.ClientId!.Value, tender.Offer))
                .ToHashSet(),
            ct);

        return new CheckoutValidationSnapshot(storedSaleIdsByItem, tenderItems, storedSameOfferTenderIds, serverDate);
    }

    /// <summary>
    /// Returns whether the snapshot covers the stored sales of the item with row id <paramref name="itemId"/>; when it
    /// does, <paramref name="saleIds"/> receives the ids of the item's stored sales, empty when it has none.
    /// </summary>
    /// <param name="itemId">The row id of the item.</param>
    /// <param name="saleIds">The ids of the item's stored sales; empty when the snapshot does not cover the item.</param>
    /// <returns><see langword="true"/> when the snapshot covers the item.</returns>
    public bool TryGetStoredSaleIds(long itemId, out IReadOnlyList<long> saleIds)
    {
        if (_storedSaleIdsByItem.TryGetValue(itemId, out IReadOnlyList<long>? ids))
        {
            saleIds = ids;
            return true;
        }

        saleIds = [];
        return false;
    }

    /// <summary>Returns whether the snapshot covers the tender item with row id <paramref name="tenderItemId"/>.</summary>
    /// <param name="tenderItemId">The row id of the tender item.</param>
    /// <returns><see langword="true"/> when the snapshot covers the tender item.</returns>
    public bool CoversTenderItem(long tenderItemId) => _tenderItems.ContainsKey(tenderItemId);

    /// <summary>
    /// Returns whether the snapshot covers the tender item with row id <paramref name="tenderItemId"/>; when it does,
    /// <paramref name="tenderItem"/> receives a new, untracked <see cref="TenderItem"/> holding only that id and the
    /// stored reserve price and closure date, or <see langword="null"/> when no tender item has the id.
    /// </summary>
    /// <param name="tenderItemId">The row id of the tender item.</param>
    /// <param name="tenderItem">The stored terms of the tender item, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the snapshot covers the tender item.</returns>
    public bool TryGetTenderItem(long tenderItemId, out TenderItem? tenderItem)
    {
        if (!_tenderItems.TryGetValue(tenderItemId, out StoredTenderItem? stored))
        {
            tenderItem = null;
            return false;
        }

        tenderItem = stored is null
            ? null
            : new TenderItem { Id = tenderItemId, Price = stored.Price, ClosureDate = stored.ClosureDate };
        return true;
    }

    /// <summary>
    /// Returns whether the snapshot covers the stored tenders on the tender item with row id
    /// <paramref name="tenderItemId"/> by the client with row id <paramref name="clientId"/> whose offer equals
    /// <paramref name="offer"/>; when it does, <paramref name="tenderIds"/> receives their ids, empty when there are none.
    /// </summary>
    /// <param name="tenderItemId">The row id of the tender item.</param>
    /// <param name="clientId">The row id of the client.</param>
    /// <param name="offer">The offer.</param>
    /// <param name="tenderIds">The ids of the matching stored tenders; empty when the snapshot does not cover them.</param>
    /// <returns><see langword="true"/> when the snapshot covers the tender item, client and offer.</returns>
    public bool TryGetStoredSameOfferTenderIds(
        long tenderItemId,
        long clientId,
        decimal offer,
        out IReadOnlyList<long> tenderIds)
    {
        if (_storedSameOfferTenderIds.TryGetValue(new SameOfferKey(tenderItemId, clientId, offer), out IReadOnlyList<long>? ids))
        {
            tenderIds = ids;
            return true;
        }

        tenderIds = [];
        return false;
    }

    /// <summary>
    /// Reads, in one query, the ids of the stored sales of each item in <paramref name="itemIds"/>, keyed by item id;
    /// every item id is a key, with an empty list when the item has no stored sale.
    /// </summary>
    private static async Task<Dictionary<long, IReadOnlyList<long>>> ReadStoredSaleIdsAsync(
        IErewhonDbContext db,
        long[] itemIds,
        CancellationToken ct)
    {
        var storedSaleIds = new Dictionary<long, List<long>>(itemIds.Length);
        foreach (long itemId in itemIds)
        {
            storedSaleIds[itemId] = [];
        }

        if (itemIds.Length > 0)
        {
            var sales = await db.Sales
                .AsNoTracking()
                .Where(sale => itemIds.Contains(sale.ItemId))
                .Select(sale => new { sale.ItemId, sale.Id })
                .ToListAsync(ct);

            foreach (var sale in sales)
            {
                storedSaleIds[sale.ItemId].Add(sale.Id);
            }
        }

        return storedSaleIds.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<long>)entry.Value);
    }

    /// <summary>
    /// Reads, in one query, the reserve price and closure date of each tender item in <paramref name="tenderItemIds"/>,
    /// keyed by id; every id is a key, with <see langword="null"/> when no tender item has it.
    /// </summary>
    private static async Task<Dictionary<long, StoredTenderItem?>> ReadTenderItemsAsync(
        IErewhonDbContext db,
        long[] tenderItemIds,
        CancellationToken ct)
    {
        var tenderItems = new Dictionary<long, StoredTenderItem?>(tenderItemIds.Length);
        foreach (long tenderItemId in tenderItemIds)
        {
            tenderItems[tenderItemId] = null;
        }

        if (tenderItemIds.Length > 0)
        {
            var stored = await db.Items
                .OfType<TenderItem>()
                .AsNoTracking()
                .Where(item => tenderItemIds.Contains(item.Id))
                .Select(item => new { item.Id, item.Price, item.ClosureDate })
                .ToListAsync(ct);

            foreach (var item in stored)
            {
                tenderItems[item.Id] = new StoredTenderItem(item.Price, item.ClosureDate);
            }
        }

        return tenderItems;
    }

    /// <summary>
    /// Reads, in one query, the ids of the stored tenders matching each key's tender item, client and offer, keyed by
    /// that key; every key is a key of the result, with an empty list when no stored tender matches it.
    /// </summary>
    private static async Task<Dictionary<SameOfferKey, IReadOnlyList<long>>> ReadSameOfferTenderIdsAsync(
        IErewhonDbContext db,
        HashSet<SameOfferKey> keys,
        CancellationToken ct)
    {
        var matches = new Dictionary<SameOfferKey, List<long>>(keys.Count);
        foreach (SameOfferKey key in keys)
        {
            matches[key] = [];
        }

        if (keys.Count > 0)
        {
            long[] tenderItemIds = keys.Select(key => key.TenderItemId).Distinct().ToArray();
            long[] clientIds = keys.Select(key => key.ClientId).Distinct().ToArray();
            decimal[] offers = keys.Select(key => key.Offer).Distinct().ToArray();

            var tenders = await db.Tenders
                .AsNoTracking()
                .Where(tender => tenderItemIds.Contains(tender.TenderItemId)
                    && clientIds.Contains(tender.ClientId)
                    && offers.Contains(tender.Offer))
                .Select(tender => new { tender.Id, tender.TenderItemId, tender.ClientId, tender.Offer })
                .ToListAsync(ct);

            foreach (var tender in tenders)
            {
                if (matches.TryGetValue(new SameOfferKey(tender.TenderItemId, tender.ClientId, tender.Offer), out List<long>? ids))
                {
                    ids.Add(tender.Id);
                }
            }
        }

        return matches.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<long>)entry.Value);
    }

    /// <summary>The stored reserve price and closure date of one tender item.</summary>
    private sealed record StoredTenderItem(decimal Price, DateOnly ClosureDate);

    /// <summary>A tender item, a client and an offer whose stored tenders are counted.</summary>
    private readonly record struct SameOfferKey(long TenderItemId, long ClientId, decimal Offer);
}

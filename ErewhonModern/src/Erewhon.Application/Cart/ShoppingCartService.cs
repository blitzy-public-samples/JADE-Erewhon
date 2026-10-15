using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Cart;

/// <summary>
/// The subordinate request of one cart line at checkout: the cart's own line, the retail sale or tender command built
/// from it, and the error list of that command. Ports the <c>myRetailSaleTA</c> and <c>myTenderTA</c> references of
/// JADE <c>CartItem</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class ShoppingCartLineCommand
{
    /// <summary>Creates the request of a retail line.</summary>
    /// <param name="line">The cart's own retail line.</param>
    /// <param name="retailSale">The retail sale command built from <paramref name="line"/>.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="line"/> is a tender line.</exception>
    public ShoppingCartLineCommand(CartItem line, RetailSaleCommand retailSale)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(retailSale);
        if (line.IsTender)
        {
            throw new ArgumentException("A tender line takes a tender command.", nameof(line));
        }

        Line = line;
        RetailSale = retailSale;
    }

    /// <summary>Creates the request of a tender line.</summary>
    /// <param name="line">The cart's own tender line.</param>
    /// <param name="tender">The tender command built from <paramref name="line"/>.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="line"/> is a retail line.</exception>
    public ShoppingCartLineCommand(CartItem line, TenderCommand tender)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(tender);
        if (!line.IsTender)
        {
            throw new ArgumentException("A retail line takes a retail sale command.", nameof(line));
        }

        Line = line;
        Tender = tender;
    }

    /// <summary>The cart's own line object, which keeps the sale timestamp assigned at pre-validation.</summary>
    public CartItem Line { get; }

    /// <summary>The retail sale command of a retail line; <see langword="null"/> on a tender line.</summary>
    public RetailSaleCommand? RetailSale { get; }

    /// <summary>The tender command of a tender line; <see langword="null"/> on a retail line.</summary>
    public TenderCommand? Tender { get; }

    /// <summary>
    /// The errors and warnings of the line's command, replaced by a new, empty result whenever the subordinate errors
    /// are cleared. Ports the error and warning lists of the subordinate <c>RetailSaleTA</c> or <c>TenderTA</c>.
    /// </summary>
    public OperationResult Result { get; set; } = new();
}

/// <summary>
/// The values one checkout persists: the session cart, the session client and one subordinate request per cart line in
/// cart order. <see cref="TransactionCommand{TEntity}.ModelObject"/> stays <see langword="null"/>, so
/// <see cref="TransactionType.Persist"/> runs the create path. Ports the transient state of JADE <c>ShoppingCartTA</c>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class ShoppingCartCommand : TransactionCommand<ShoppingCart>
{
    /// <summary>Creates the checkout values.</summary>
    /// <param name="cart">The session cart being checked out.</param>
    /// <param name="clientId">The row id of the session client; <see langword="null"/> or 0 for none.</param>
    /// <param name="lines">The subordinate requests, one per cart line in cart order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cart"/> or <paramref name="lines"/> is <see langword="null"/>.</exception>
    public ShoppingCartCommand(ShoppingCart cart, long? clientId, IReadOnlyList<ShoppingCartLineCommand> lines)
    {
        ArgumentNullException.ThrowIfNull(cart);
        ArgumentNullException.ThrowIfNull(lines);
        Cart = cart;
        ClientId = clientId;
        Lines = lines;
    }

    /// <summary>The session cart being checked out.</summary>
    public ShoppingCart Cart { get; }

    /// <summary>The row id of the session client at checkout; <see langword="null"/> or 0 for none. Ports <c>app.getClient()</c>.</summary>
    public long? ClientId { get; }

    /// <summary>The subordinate requests, one per cart line in ascending item-id order. Ports <c>ShoppingCartTA.allCartItems</c>.</summary>
    public IReadOnlyList<ShoppingCartLineCommand> Lines { get; }
}

/// <summary>
/// One row of the cart table: the line's item id and kind, its transaction text and code, the item's current name and
/// price, and the line's offer.
/// </summary>
/// <param name="ItemId">The internal id of the line's item.</param>
/// <param name="IsTender"><see langword="true"/> for a tender line; <see langword="false"/> for a retail line.</param>
/// <param name="Transaction">The line's transaction text: "Retail" or "Tender".</param>
/// <param name="Code">The item code held by the line.</param>
/// <param name="ItemName">The item's current name; empty when the item no longer exists.</param>
/// <param name="ItemPrice">The item's current price; 0 when the item no longer exists.</param>
/// <param name="Offer">The line's offer.</param>
public sealed record CartLineView(
    long ItemId,
    bool IsTender,
    string Transaction,
    string Code,
    string ItemName,
    decimal ItemPrice,
    decimal Offer);

/// <summary>The cart of one session as the shop and checkout pages show it.</summary>
/// <param name="SessionId">The id of the session that owns the cart.</param>
/// <param name="Lines">One row per cart line, in ascending item-id order.</param>
/// <param name="Total">The cart total: tender offers plus the current prices of the retail items.</param>
/// <param name="IsEmpty"><see langword="true"/> when the cart holds no lines.</param>
/// <param name="Size">The number of cart lines.</param>
public sealed record CartView(
    string SessionId,
    IReadOnlyList<CartLineView> Lines,
    decimal Total,
    bool IsEmpty,
    int Size);

/// <summary>The outcome of a checkout.</summary>
/// <param name="Result">The result of the checkout: its success flag, errors and warnings.</param>
/// <param name="Receipt">The confirmation of a successful checkout; <see langword="null"/> when the checkout failed.</param>
public sealed record CheckoutResult(OperationResult Result, CheckoutReceipt? Receipt);

/// <summary>
/// Adds and removes the lines of a session cart, reports its lines and total, and checks it out in one transaction:
/// each retail line persists a retail sale and each tender line a tender, every line or none.
/// Ports JADE <c>ShoppingCartTA</c> with the <c>ModelTA</c> steps it inherits.
/// </summary>
/// <remarks>
/// One instance serves one dependency-injection scope. Instances are not safe to call from several threads at once.
/// </remarks>
public sealed class ShoppingCartService : TransactionAgent<ShoppingCart>
{
    private readonly ICartStore _cartStore;
    private readonly ISessionContext _session;
    private readonly ICompanyContext _companyContext;
    private readonly RetailSaleService _retailSales;
    private readonly TenderService _tenders;

    /// <summary>
    /// Creates the service over the database, unit of work, locks, server clock and notification queue of its scope,
    /// the session carts, the session, the bound company and the retail sale and tender services.
    /// </summary>
    /// <param name="db">The Erewhon database of the scope.</param>
    /// <param name="uow">The unit of work that begins, commits and rolls back the checkout transaction.</param>
    /// <param name="locks">The advisory locks taken for a checkout.</param>
    /// <param name="clock">The database server clock that supplies the time a tender line is added.</param>
    /// <param name="queue">The queue of change notifications raised by the scope's saves.</param>
    /// <param name="cartStore">The carts of every session.</param>
    /// <param name="session">The session bound to the scope, whose client buys and tenders.</param>
    /// <param name="companyContext">The company bound to the scope.</param>
    /// <param name="retailSales">The service that persists the retail sale of each retail line.</param>
    /// <param name="tenders">The service that persists the tender of each tender line.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ShoppingCartService(
        IErewhonDbContext db,
        IUnitOfWork uow,
        IEntityLocks locks,
        IServerClock clock,
        NotificationQueue queue,
        ICartStore cartStore,
        ISessionContext session,
        ICompanyContext companyContext,
        RetailSaleService retailSales,
        TenderService tenders)
        : base(db, uow, locks, clock, queue)
    {
        ArgumentNullException.ThrowIfNull(cartStore);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(companyContext);
        ArgumentNullException.ThrowIfNull(retailSales);
        ArgumentNullException.ThrowIfNull(tenders);

        _cartStore = cartStore;
        _session = session;
        _companyContext = companyContext;
        _retailSales = retailSales;
        _tenders = tenders;
    }

    /// <summary>
    /// <see langword="true"/>: a checkout persists only the subordinate sales and tenders.
    /// Ports <c>ShoppingCartTA.hasOnlySubordinatePersistentObjects</c>.
    /// </summary>
    protected override bool HasOnlySubordinatePersistentObjects => true;

    /// <summary>
    /// Adds the item to the session cart as one line. A tender item line holds the offer, the session client and the
    /// server timestamp read now; a retail item line holds the offer, the session client and the item price read now.
    /// No item, or no item with that id → <see cref="MessageKeys.ItemRequired"/>; a tender offer below the item's
    /// reserve price → <see cref="MessageKeys.TenderOfferLessThanReserve"/> (<see cref="FocusFields.Tender"/>); in
    /// both cases no line is added. Ports <c>ShoppingCartTA.addItem</c>.
    /// </summary>
    /// <param name="sessionId">The id of the session that owns the cart.</param>
    /// <param name="itemId">The internal id of the item; <see langword="null"/> or 0 for none.</param>
    /// <param name="offer">The offer; on a tender item, the amount tendered.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>The result: succeeded with no errors when the line was added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="StringTooLongException">The item code is longer than 11 characters.</exception>
    /// <exception cref="InvalidOperationException">The cart already holds a line for the item.</exception>
    public async Task<OperationResult> AddItemAsync(
        string sessionId,
        long? itemId,
        decimal offer,
        CancellationToken ct = default)
    {
        var result = new OperationResult();
        ShoppingCart cart = _cartStore.GetCart(sessionId);

        Item? item = null;
        if (!JadeValues.IsNull(itemId))
        {
            long id = itemId!.Value;
            item = await Db.Items
                .AsNoTracking()
                .Include(candidate => candidate.ItemCategory)
                .FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
        }

        if (item is null)
        {
            result.AddError(MessageKeys.ItemRequired);
            result.Succeeded = false;
            return result;
        }

        string code = item.GetCode() ?? string.Empty;
        FieldLengths.Check(code, FieldLengths.CartItemCode, "code");

        long clientId = _session.ClientId ?? 0;
        decimal storedOffer = DecimalRules.Round2(offer);
        CartItem line;

        if (item is TenderItem)
        {
            if (offer < item.Price)
            {
                result.AddError(MessageKeys.TenderOfferLessThanReserve, FocusFields.Tender);
                result.Succeeded = false;
                return result;
            }

            line = new CartItem
            {
                ItemId = item.Id,
                IsTender = true,
                Code = code,
                Transaction = MessageCatalog.Get(MessageKeys.TenderItemType),
                Offer = storedOffer,
                ClientId = clientId,
                DateTendered = await Clock.GetServerTimeStampAsync(ct),
            };
        }
        else
        {
            line = new CartItem
            {
                ItemId = item.Id,
                IsTender = false,
                Code = code,
                Transaction = MessageCatalog.Get(MessageKeys.RetailItemType),
                Offer = storedOffer,
                SalePrice = item.Price,
                ClientId = clientId,
            };
        }

        cart.Add(line);
        result.Succeeded = true;
        return result;
    }

    /// <summary>
    /// Removes the item's line from the session cart; succeeds whether or not the cart held one.
    /// No item → <see cref="MessageKeys.NoItemSelected"/>. Ports <c>ShoppingCartTA.removeItem</c>.
    /// </summary>
    /// <param name="sessionId">The id of the session that owns the cart.</param>
    /// <param name="itemId">The internal id of the item; <see langword="null"/> or 0 for none.</param>
    /// <returns>The result: succeeded with no errors unless no item was given.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public OperationResult RemoveItem(string sessionId, long? itemId)
    {
        var result = new OperationResult();
        ShoppingCart cart = _cartStore.GetCart(sessionId);

        if (JadeValues.IsNull(itemId))
        {
            result.AddError(MessageKeys.NoItemSelected);
            result.Succeeded = false;
            return result;
        }

        cart.Remove(itemId!.Value);
        result.Succeeded = true;
        return result;
    }

    /// <summary>Removes every line from the session cart. Ports <c>ShoppingCartTA.initialize</c>.</summary>
    /// <param name="sessionId">The id of the session that owns the cart.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public void Empty(string sessionId) => _cartStore.GetCart(sessionId).Clear();

    /// <summary>
    /// Returns the total of the session cart: the offer of each tender line plus the current price of each retail
    /// line's item, a retail line whose item no longer exists adding nothing; 0 for an empty cart.
    /// Ports <c>ShoppingCartTA.getCartTotal</c>.
    /// </summary>
    /// <param name="sessionId">The id of the session that owns the cart.</param>
    /// <param name="ct">Token that cancels the item read.</param>
    /// <returns>The cart total.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<decimal> GetCartTotalAsync(string sessionId, CancellationToken ct = default)
    {
        IReadOnlyList<CartItem> lines = _cartStore.GetCart(sessionId).Items;
        IReadOnlyDictionary<long, ItemSummary> items = await ReadItemSummariesAsync(lines, ct);
        return CartTotal(lines, items);
    }

    /// <summary>
    /// Returns the session cart: one row per line with the item's current name and price, the total of
    /// <see cref="GetCartTotalAsync"/>, and whether the cart is empty and how many lines it holds, all taken from one
    /// snapshot of the lines. Ports the cart tables of <c>ShopItemsForm</c> and <c>ShopCheckoutForm</c>.
    /// </summary>
    /// <param name="sessionId">The id of the session that owns the cart.</param>
    /// <param name="ct">Token that cancels the item read.</param>
    /// <returns>The cart.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public async Task<CartView> GetCartAsync(string sessionId, CancellationToken ct = default)
    {
        ShoppingCart cart = _cartStore.GetCart(sessionId);
        IReadOnlyList<CartItem> lines = cart.Items;
        IReadOnlyDictionary<long, ItemSummary> items = await ReadItemSummariesAsync(lines, ct);

        var rows = new List<CartLineView>(lines.Count);
        foreach (CartItem line in lines)
        {
            ItemSummary? item = items.GetValueOrDefault(line.ItemId);
            rows.Add(new CartLineView(
                line.ItemId,
                line.IsTender,
                line.Transaction,
                line.Code,
                item?.Name ?? string.Empty,
                item?.Price ?? 0m,
                line.Offer));
        }

        return new CartView(cart.SessionId, rows, CartTotal(lines, items), lines.Count == 0, lines.Count);
    }


    /// <summary>
    /// Checks out the session cart in one transaction of its own: each retail line persists a retail sale at the price
    /// the line holds, and each tender line persists a tender with the offer and time the line holds; every line
    /// persists or none does. On success the receipt lists every line in cart order, with the item's name and price
    /// read after the commit, and the cart is then emptied; on failure the cart is left as it was, its retail lines
    /// keeping the sale timestamp assigned at pre-validation. An empty cart fails with
    /// <see cref="MessageKeys.ShoppingCartHasNoItems"/>. Ports <c>ShoppingCartTA.persistEntity(TransactionType_Persist)</c>
    /// and the confirmation of <c>ShopCheckoutForm.btnProceed_click</c>.
    /// </summary>
    /// <param name="sessionId">The id of the session that owns the cart.</param>
    /// <param name="ct">Token that cancels the checkout.</param>
    /// <returns>The result of the checkout and, on success, its receipt.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="NoCompanyException">The checkout reached its locks and no company is bound.</exception>
    public async Task<CheckoutResult> CheckoutAsync(string sessionId, CancellationToken ct = default)
    {
        ShoppingCart cart = _cartStore.GetCart(sessionId);
        ShoppingCartCommand command = BuildCheckoutCommand(cart, _session.ClientId);

        OperationResult result = await ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own, ct);
        if (!result.Succeeded)
        {
            return new CheckoutResult(result, null);
        }

        IReadOnlyList<CartItem> lines = command.Lines.Select(line => line.Line).ToList();
        IReadOnlyDictionary<long, ItemSummary> items = await ReadItemSummariesAsync(lines, ct);

        var receiptLines = new List<CheckoutReceiptLine>(lines.Count);
        foreach (CartItem line in lines)
        {
            ItemSummary? item = items.GetValueOrDefault(line.ItemId);
            receiptLines.Add(new CheckoutReceiptLine(
                line.Transaction,
                line.Code,
                item?.Name ?? string.Empty,
                item?.Price ?? 0m,
                line.Offer,
                line.IsTender));
        }

        cart.Clear();
        return new CheckoutResult(result, new CheckoutReceipt(receiptLines));
    }

    /// <summary>
    /// Gives each line command a new, empty result. Ports <c>ShoppingCartTA.clearErrorsOnSubordinateTAs</c>.
    /// </summary>
    /// <param name="command">The checkout values.</param>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ShoppingCartCommand"/>.</exception>
    protected override void ClearErrorsOnSubordinateTAs(TransactionCommand<ShoppingCart> command)
    {
        foreach (ShoppingCartLineCommand line in AsCartCommand(command).Lines)
        {
            line.Result = new OperationResult();
        }
    }

    /// <summary>
    /// Clears the warnings of each line command. Ports <c>ShoppingCartTA.clearWarningsOnSubordinateTAs</c>.
    /// </summary>
    /// <param name="command">The checkout values.</param>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ShoppingCartCommand"/>.</exception>
    protected override void ClearWarningsOnSubordinateTAs(TransactionCommand<ShoppingCart> command)
    {
        foreach (ShoppingCartLineCommand line in AsCartCommand(command).Lines)
        {
            line.Result.ClearWarnings();
        }
    }

    /// <summary>
    /// Pre-validates each line command through its service, in cart order. A retail line then keeps the sale timestamp
    /// on the cart line, the line's existing timestamp taking precedence, and its command carries that timestamp.
    /// Ports <c>ShoppingCartTA.doPreValidate</c>.
    /// </summary>
    /// <param name="command">The checkout values.</param>
    /// <param name="ct">Token that cancels the clock reads.</param>
    /// <returns>A task that completes when every line command is pre-validated.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ShoppingCartCommand"/>.</exception>
    protected override async Task PreValidateCoreAsync(TransactionCommand<ShoppingCart> command, CancellationToken ct)
    {
        foreach (ShoppingCartLineCommand line in AsCartCommand(command).Lines)
        {
            if (line.RetailSale is { } retailSale)
            {
                await _retailSales.PreValidateAsync(retailSale, ct);
                if (!JadeValues.IsNull(retailSale.DateSold))
                {
                    retailSale.DateSold = line.Line.RetainDateSold(retailSale.DateSold!.Value);
                }
            }
            else if (line.Tender is { } tender)
            {
                await _tenders.PreValidateAsync(tender, ct);
            }
        }
    }

    /// <summary>
    /// Adds nothing for <see cref="ValidationType.Delete"/>. Otherwise adds
    /// <see cref="MessageKeys.ShoppingCartHasNoItems"/> when there are no lines, then validates every line command
    /// through its service into the line's result, in cart order, and copies the errors of each line that fails, the
    /// first copied focus field becoming the focus field. Ports <c>ShoppingCartTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The checkout values.</param>
    /// <param name="validationType">The set of rules to apply, passed to each line's service.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when every line is validated.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ShoppingCartCommand"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    protected override async Task ValidateCoreAsync(
        TransactionCommand<ShoppingCart> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken ct)
    {
        ShoppingCartCommand cart = AsCartCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        if (validationType == ValidationType.Delete)
        {
            return;
        }

        if (cart.Lines.Count == 0)
        {
            result.AddError(MessageKeys.ShoppingCartHasNoItems);
        }

        foreach (ShoppingCartLineCommand line in cart.Lines)
        {
            if (line.RetailSale is { } retailSale)
            {
                if (!await _retailSales.ValidateAsync(retailSale, validationType, line.Result, ct))
                {
                    result.CopyErrorsFrom(line.Result);
                }

                continue;
            }

            if (line.Tender is { } tender && !await _tenders.ValidateAsync(tender, validationType, line.Result, ct))
            {
                result.CopyErrorsFrom(line.Result);
            }
        }
    }

    /// <summary>
    /// Takes no lock for <see cref="ValidationType.Modify"/>. Otherwise takes, in this order and returning
    /// <see langword="false"/> at the first lock not obtained, <see cref="LockNames.CompanyAllSalesByItem"/> of the
    /// bound company, then <see cref="LockNames.ClientAllRetailSales"/> and <see cref="LockNames.ClientAllTenderSales"/>
    /// of the session client. Ports <c>ShoppingCartTA.lockForCreate</c>, <c>lockForUpdate</c>, <c>lockForDelete</c>
    /// and <c>lockForModify</c>.
    /// </summary>
    /// <param name="command">The checkout values.</param>
    /// <param name="validationType">The operation the locks are taken for.</param>
    /// <param name="ct">Token that cancels the lock waits.</param>
    /// <returns><see langword="true"/> when every lock is held.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ShoppingCartCommand"/>.</exception>
    /// <exception cref="NoCompanyException">No company is bound.</exception>
    /// <exception cref="InvalidOperationException">
    /// The company lock is held and the checkout has no session client: the null-object-reference error.
    /// </exception>
    protected override async Task<bool> AcquireLocksAsync(
        TransactionCommand<ShoppingCart> command,
        ValidationType validationType,
        CancellationToken ct)
    {
        ShoppingCartCommand cart = AsCartCommand(command);
        if (validationType == ValidationType.Modify)
        {
            return true;
        }

        Company company = await _companyContext.GetCurrentAsync(ct);
        if (!await Locks.TryLockAsync(LockNames.CompanyAllSalesByItem(company.Id), ct))
        {
            return false;
        }

        if (JadeValues.IsNull(cart.ClientId))
        {
            throw new InvalidOperationException("Null object reference");
        }

        long clientId = cart.ClientId!.Value;
        return await Locks.TryLockAsync(LockNames.ClientAllRetailSales(clientId), ct)
            && await Locks.TryLockAsync(LockNames.ClientAllTenderSales(clientId), ct);
    }

    /// <summary>Always throws: a checkout creates no model object of its own.</summary>
    /// <param name="command">The checkout values.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="InvalidOperationException">Always.</exception>
    protected override ShoppingCart CreateModelObject(TransactionCommand<ShoppingCart> command) =>
        throw new InvalidOperationException("A shopping cart has no model object of its own.");

    /// <summary>
    /// Persists every line command through its service inside the checkout transaction, in cart order and without
    /// stopping at a failure: a retail sale for each retail line and a tender for each tender line. Copies the errors
    /// of each line that fails. Ports <c>ShoppingCartTA.createSubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The checkout values.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when <paramref name="result"/> holds no errors.</returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ShoppingCartCommand"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    protected override async Task<bool> CreateSubordinateObjectsAsync(
        TransactionCommand<ShoppingCart> command,
        OperationResult result,
        CancellationToken ct)
    {
        ShoppingCartCommand cart = AsCartCommand(command);
        ArgumentNullException.ThrowIfNull(result);

        foreach (ShoppingCartLineCommand line in cart.Lines)
        {
            if (line.RetailSale is { } retailSale)
            {
                OperationResult saleResult = await _retailSales.ExecuteAsync(
                    retailSale,
                    TransactionType.Persist,
                    TransactionMode.Ambient,
                    ct);
                if (!saleResult.Succeeded)
                {
                    result.CopyErrorsFrom(saleResult);
                }
            }

            if (line.Tender is { } tender)
            {
                OperationResult tenderResult = await _tenders.ExecuteAsync(
                    tender,
                    TransactionType.Persist,
                    TransactionMode.Ambient,
                    ct);
                if (!tenderResult.Succeeded)
                {
                    result.CopyErrorsFrom(tenderResult);
                }
            }
        }

        return result.HasNoErrors;
    }

    /// <summary>
    /// Clears the model object of each line command after a checkout that did not complete.
    /// Ports <c>ShoppingCartTA.doAbortTransactionCleanupForSubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The checkout values.</param>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a <see cref="ShoppingCartCommand"/>.</exception>
    protected override void AbortTransactionCleanupForSubordinateObjects(TransactionCommand<ShoppingCart> command)
    {
        foreach (ShoppingCartLineCommand line in AsCartCommand(command).Lines)
        {
            if (line.RetailSale is { } retailSale)
            {
                retailSale.ModelObject = null;
            }
            else if (line.Tender is { } tender)
            {
                tender.ModelObject = null;
            }
        }
    }

    /// <summary>
    /// Builds the checkout values from a snapshot of the cart lines: a retail sale command for each retail line, with
    /// the line's item, client, sale price and sale timestamp; a tender command for each tender line, with the line's
    /// item, client, offer and time tendered. A line client of 0 becomes no client.
    /// </summary>
    private static ShoppingCartCommand BuildCheckoutCommand(ShoppingCart cart, long? clientId)
    {
        IReadOnlyList<CartItem> items = cart.Items;
        var lines = new List<ShoppingCartLineCommand>(items.Count);
        foreach (CartItem item in items)
        {
            long? lineClientId = JadeValues.IsNull(item.ClientId) ? null : item.ClientId;
            lines.Add(item.IsTender
                ? new ShoppingCartLineCommand(item, new TenderCommand
                {
                    TenderItemId = item.ItemId,
                    ClientId = lineClientId,
                    Offer = item.Offer,
                    DateTendered = item.DateTendered,
                })
                : new ShoppingCartLineCommand(item, new RetailSaleCommand
                {
                    ItemId = item.ItemId,
                    ClientId = lineClientId,
                    SalePrice = item.SalePrice,
                    DateSold = item.DateSold,
                }));
        }

        return new ShoppingCartCommand(cart, clientId, lines);
    }

    /// <summary>
    /// Returns the sum, rounded to 2 decimal places at each addition, of each tender line's offer and the current price
    /// of each retail line's item; a retail line whose item is missing from <paramref name="items"/> adds nothing.
    /// </summary>
    private static decimal CartTotal(IReadOnlyList<CartItem> lines, IReadOnlyDictionary<long, ItemSummary> items)
    {
        decimal sum = 0m;
        foreach (CartItem line in lines)
        {
            if (line.IsTender)
            {
                sum = DecimalRules.Round2(sum + line.Offer);
            }
            else if (items.TryGetValue(line.ItemId, out ItemSummary? item))
            {
                sum = DecimalRules.Round2(sum + item.Price);
            }
        }

        return sum;
    }

    /// <summary>Returns <paramref name="command"/> as a <see cref="ShoppingCartCommand"/>.</summary>
    private static ShoppingCartCommand AsCartCommand(TransactionCommand<ShoppingCart> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command as ShoppingCartCommand
            ?? throw new ArgumentException(
                $"The command is a {command.GetType().Name}, not a {nameof(ShoppingCartCommand)}.",
                nameof(command));
    }

    /// <summary>
    /// Reads, in one untracked query, the id, name and current price of every item the lines refer to, keyed by id;
    /// items that no longer exist are absent.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, ItemSummary>> ReadItemSummariesAsync(
        IReadOnlyList<CartItem> lines,
        CancellationToken ct)
    {
        if (lines.Count == 0)
        {
            return new Dictionary<long, ItemSummary>();
        }

        long[] ids = lines.Select(line => line.ItemId).Distinct().ToArray();
        return await Db.Items
            .AsNoTracking()
            .Where(item => ids.Contains(item.Id))
            .Select(item => new ItemSummary(item.Id, item.Name, item.Price))
            .ToDictionaryAsync(item => item.Id, ct);
    }

    /// <summary>The id, name and current price of one item.</summary>
    private sealed record ItemSummary(long Id, string Name, decimal Price);
}


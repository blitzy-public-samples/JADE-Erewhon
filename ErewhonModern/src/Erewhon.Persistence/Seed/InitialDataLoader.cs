using System.Diagnostics;
using System.Globalization;
using System.Security;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Erewhon.Persistence.Seed;

/// <summary>
/// The seed data directory read by <see cref="InitialDataLoader.LoadConfiguredAsync"/>.
/// </summary>
/// <param name="DataDirectory">
/// The absolute path of the directory holding the seed data files, or <see langword="null"/> when none is configured.
/// </param>
public sealed record SeedSettings(string? DataDirectory);

/// <summary>
/// The outcome of a completed seed load.
/// </summary>
/// <param name="TenderSalesBuilt">The number of tender items the post-load closure awarded.</param>
/// <param name="OutputLines">Every line the load wrote, in the order written.</param>
public sealed record SeedLoadResult(int TenderSalesBuilt, IReadOnlyList<string> OutputLines);

/// <summary>
/// Loads the seed data files of a directory into a new company through the application services. A load validates the
/// directory, deletes the lowest-id company with everything it owns in its own transaction, then, in one load
/// transaction, creates the company "Erewhon Investments Inc" and persists every data line of the ten files in
/// <see cref="SeedFileNames.LoadOrder"/> through the services in <see cref="TransactionMode.Ambient"/> mode without
/// reading the result of each persist, commits, publishes the queued notifications, and finally closes the tenders due
/// at the server date. Dates and timestamps read from the files are moved forward by the number of days from
/// 1 January 2000 to the server date. Every progress line is written to the output writer, to the log and to
/// <see cref="SeedLoadResult.OutputLines"/>. Ports JADE <c>InitialDataLoader</c>.
/// <para>
/// Every load holds the exclusive session-level PostgreSQL advisory lock
/// <c>pg_advisory_lock(hashtextextended('InitialDataLoader.loadData', 0))</c>, one lock for the whole database, so loads
/// run one after another across connections and processes. The loader opens a dedicated connection with the context's
/// connection string, <c>Pooling</c> and <c>Multiplexing</c> off, takes the lock on it before the purge reads the
/// lowest-id company, holds it through the load commit, the publication of the notifications and the post-load
/// closure, then releases it and disposes the connection, which ends its PostgreSQL session. The context's own
/// connections take no part in the lock. The wait for the lock sets no <c>lock_timeout</c>, runs with no command
/// timeout, and ends only when the lock is granted or the load's token is cancelled.
/// </para>
/// </summary>
/// <remarks>
/// The data files are only read. One instance serves one dependency-injection scope; its members are not safe to call
/// from several threads at once.
/// </remarks>
public sealed class InitialDataLoader
{
    private const string FieldSeparator = ", ";
    private const string Space = " ";
    private const string NullObjectReferenceText = "Null object reference";
    private const int MillisecondsPerSecond = 1000;
    private const int DataDirectoryMaxLength = 301;
    private const char ForwardSlash = '/';
    private const char BackSlash = '\\';
    private const string LoadLockName = "InitialDataLoader.loadData";
    private const string LoadLockNameParameter = "name";
    private const string LoadLockSql = "SELECT pg_advisory_lock(hashtextextended(@name, 0))";
    private const string LoadUnlockSql = "SELECT pg_advisory_unlock(hashtextextended(@name, 0))";
    private const int NoCommandTimeout = 0;

    private static readonly DateOnly OffsetStartDate = new(2000, 1, 1);

    private readonly ErewhonDbContext _db;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICompanyContext _companyContext;
    private readonly IServerClock _serverClock;
    private readonly NotificationQueue _notificationQueue;
    private readonly CompanyService _companyService;
    private readonly CountryService _countryService;
    private readonly RegionService _regionService;
    private readonly AgentService _agentService;
    private readonly ClientService _clientService;
    private readonly ItemCategoryService _itemCategoryService;
    private readonly CommissionRateService _commissionRateService;
    private readonly ItemService _itemService;
    private readonly ItemCodeAllocator _itemCodeAllocator;
    private readonly TenderService _tenderService;
    private readonly RetailSaleService _retailSaleService;
    private readonly TenderClosureService _tenderClosureService;
    private readonly SeedSettings _settings;
    private readonly ILogger<InitialDataLoader>? _logger;

    /// <summary>
    /// Creates the loader over the database, unit of work, bound company, server clock, notification queue and
    /// application services of its scope.
    /// </summary>
    /// <param name="db">
    /// The Erewhon database of the scope, read by the name and GUID look-ups; its connection string is that of the
    /// connection that holds the load lock.
    /// </param>
    /// <param name="unitOfWork">The unit of work that begins, commits and rolls back the load transaction.</param>
    /// <param name="companyContext">The company bound to the scope; the loader's company is bound to it on creation.</param>
    /// <param name="serverClock">The database server clock that gives the load date.</param>
    /// <param name="notificationQueue">The queue of change notifications, published after the load commits.</param>
    /// <param name="companyService">Deletes the existing company and creates the loader's company.</param>
    /// <param name="countryService">Persists the countries of <c>Locations.txt</c>.</param>
    /// <param name="regionService">Persists the regions of <c>Locations.txt</c>.</param>
    /// <param name="agentService">Persists the agents of <c>Agents.txt</c>.</param>
    /// <param name="clientService">Persists the clients of <c>Clients.txt</c>.</param>
    /// <param name="itemCategoryService">Persists the item categories of <c>Categories.txt</c>.</param>
    /// <param name="commissionRateService">Persists the rates of <c>CategoryRates.txt</c> and <c>AgentRates.txt</c>.</param>
    /// <param name="itemService">Persists the items of <c>RetailItems.txt</c> and <c>TenderItems.txt</c>.</param>
    /// <param name="itemCodeAllocator">Allocates the code number of each item line.</param>
    /// <param name="tenderService">Persists the tenders of <c>Tenders.txt</c>.</param>
    /// <param name="retailSaleService">Persists the retail sales of <c>RetailSales.txt</c>.</param>
    /// <param name="tenderClosureService">Closes the tenders due at the load date after the load commits.</param>
    /// <param name="settings">The configured seed data directory.</param>
    /// <param name="logger">The log that receives every output line, or <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentNullException">Any argument other than <paramref name="logger"/> is <see langword="null"/>.</exception>
    public InitialDataLoader(
        ErewhonDbContext db,
        IUnitOfWork unitOfWork,
        ICompanyContext companyContext,
        IServerClock serverClock,
        NotificationQueue notificationQueue,
        CompanyService companyService,
        CountryService countryService,
        RegionService regionService,
        AgentService agentService,
        ClientService clientService,
        ItemCategoryService itemCategoryService,
        CommissionRateService commissionRateService,
        ItemService itemService,
        ItemCodeAllocator itemCodeAllocator,
        TenderService tenderService,
        RetailSaleService retailSaleService,
        TenderClosureService tenderClosureService,
        SeedSettings settings,
        ILogger<InitialDataLoader>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _companyContext = companyContext ?? throw new ArgumentNullException(nameof(companyContext));
        _serverClock = serverClock ?? throw new ArgumentNullException(nameof(serverClock));
        _notificationQueue = notificationQueue ?? throw new ArgumentNullException(nameof(notificationQueue));
        _companyService = companyService ?? throw new ArgumentNullException(nameof(companyService));
        _countryService = countryService ?? throw new ArgumentNullException(nameof(countryService));
        _regionService = regionService ?? throw new ArgumentNullException(nameof(regionService));
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _clientService = clientService ?? throw new ArgumentNullException(nameof(clientService));
        _itemCategoryService = itemCategoryService ?? throw new ArgumentNullException(nameof(itemCategoryService));
        _commissionRateService = commissionRateService ?? throw new ArgumentNullException(nameof(commissionRateService));
        _itemService = itemService ?? throw new ArgumentNullException(nameof(itemService));
        _itemCodeAllocator = itemCodeAllocator ?? throw new ArgumentNullException(nameof(itemCodeAllocator));
        _tenderService = tenderService ?? throw new ArgumentNullException(nameof(tenderService));
        _retailSaleService = retailSaleService ?? throw new ArgumentNullException(nameof(retailSaleService));
        _tenderClosureService = tenderClosureService ?? throw new ArgumentNullException(nameof(tenderClosureService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger;
    }

    /// <summary>
    /// Loads the seed data files of <see cref="SeedSettings.DataDirectory"/>, as <see cref="LoadAsync"/> does; a
    /// directory that is not configured fails as an empty path does.
    /// </summary>
    /// <param name="output">The writer that receives each output line; <see cref="Console.Out"/> when <see langword="null"/>.</param>
    /// <param name="ct">
    /// Token that cancels the load; the publication of the committed load's notifications ignores it.
    /// </param>
    /// <returns>The number of tender sales the post-load closure built and every line written.</returns>
    /// <exception cref="ErewhonException">
    /// The configured directory is missing, empty, not a valid path or not an existing directory (500004), or a
    /// company still exists after the purge (500003).
    /// </exception>
    /// <exception cref="StringTooLongException">
    /// The configured directory exists and its path, trimmed of spaces and followed by <c>/</c> unless it ends with
    /// <c>/</c> or <c>\</c>, is longer than 301 characters (1035); nothing is deleted.
    /// </exception>
    /// <exception cref="MissingConnectionStringException">
    /// The configured directory is valid and the context has no connection string; nothing is deleted.
    /// </exception>
    public Task<SeedLoadResult> LoadConfiguredAsync(TextWriter? output = null, CancellationToken ct = default) =>
        LoadAsync(_settings.DataDirectory ?? string.Empty, output, ct);

    /// <summary>
    /// Opens the dedicated lock connection and waits on it for the load lock <c>InitialDataLoader.loadData</c>, then
    /// reads whether any company exists. When one does, it releases the lock, disposes the lock connection and returns
    /// <see langword="null"/>, having written no line and run no load step. Otherwise, still holding the lock, it loads
    /// the seed data files of <see cref="SeedSettings.DataDirectory"/> as <see cref="LoadConfiguredAsync"/> does, from
    /// the directory check on, then releases the lock and disposes the lock connection. The seconds of the final line
    /// <c>Database Initialized in &lt;n&gt; seconds</c> count from the call, the wait for the lock included.
    /// </summary>
    /// <param name="output">The writer that receives each output line; <see cref="Console.Out"/> when <see langword="null"/>.</param>
    /// <param name="ct">
    /// Token that cancels the wait for the lock, the company read and the load; the publication of the committed load's
    /// notifications ignores it.
    /// </param>
    /// <returns>
    /// The number of tender sales the post-load closure built and every line written; <see langword="null"/> when a
    /// company exists once the lock is held.
    /// </returns>
    /// <exception cref="ErewhonException">
    /// The configured directory is missing, empty, not a valid path or not an existing directory (500004), or a
    /// company still exists after the purge (500003).
    /// </exception>
    /// <exception cref="StringTooLongException">
    /// The configured directory exists and its path, trimmed of spaces and followed by <c>/</c> unless it ends with
    /// <c>/</c> or <c>\</c>, is longer than 301 characters (1035); nothing is deleted.
    /// </exception>
    /// <exception cref="MissingConnectionStringException">The context has no connection string.</exception>
    public async Task<SeedLoadResult?> LoadConfiguredWhenNoCompanyAsync(
        TextWriter? output = null,
        CancellationToken ct = default)
    {
        Stopwatch startTime = Stopwatch.StartNew();

        NpgsqlConnection lockConnection = await AcquireLoadLockAsync(ct);
        try
        {
            if (await _db.Companies.AnyAsync(ct))
            {
                return null;
            }

            return await RunLoadAsync(
                _settings.DataDirectory ?? string.Empty,
                output,
                startTime,
                loadLockHeld: true,
                ct);
        }
        finally
        {
            await ReleaseLoadLockAsync(lockConnection);
        }
    }

    /// <summary>
    /// Loads the seed data files of <paramref name="directoryPath"/>:
    /// <list type="number">
    /// <item><description>
    /// Validates the directory path, trimmed of spaces, and checks that it, followed by <c>/</c> unless it ends with
    /// <c>/</c> or <c>\</c>, is at most 301 characters long.
    /// </description></item>
    /// <item><description>
    /// Opens the dedicated lock connection and waits on it for the load lock <c>InitialDataLoader.loadData</c>.
    /// </description></item>
    /// <item><description>Reads the server date once; it is the load date.</description></item>
    /// <item><description>Deletes the lowest-id company, with everything it owns, in its own transaction.</description></item>
    /// <item><description>
    /// In one transaction, creates the company, then persists the data lines of the ten files in
    /// <see cref="SeedFileNames.LoadOrder"/>, and commits. Any failure rolls the load transaction back, discards its
    /// queued notifications and propagates; the committed purge stays.
    /// </description></item>
    /// <item><description>Publishes the load's notifications, then closes the tenders due at the load date.</description></item>
    /// <item><description>
    /// Releases the load lock and disposes the lock connection, whether or not the steps since the lock succeeded.
    /// </description></item>
    /// </list>
    /// The final line <c>Database Initialized in &lt;n&gt; seconds</c> is written whether or not the load succeeds; its
    /// seconds count from the call, the wait for the lock included.
    /// </summary>
    /// <param name="directoryPath">
    /// The path of the directory holding the seed data files; a relative path is resolved against the current
    /// directory.
    /// </param>
    /// <param name="output">The writer that receives each output line; <see cref="Console.Out"/> when <see langword="null"/>.</param>
    /// <param name="ct">
    /// Token that cancels the wait for the lock and the load; the publication of the committed load's notifications
    /// ignores it.
    /// </param>
    /// <returns>The number of tender sales the post-load closure built and every line written.</returns>
    /// <exception cref="ErewhonException">
    /// The directory path is <see langword="null"/>, empty, not a valid path or not an existing directory (500004), or
    /// a company still exists after the purge (500003).
    /// </exception>
    /// <exception cref="StringTooLongException">
    /// The directory exists and its path as given, trimmed of spaces and followed by <c>/</c> unless it ends with
    /// <c>/</c> or <c>\</c>, is longer than 301 characters (1035); nothing is deleted. Or an item line's GUID or name
    /// is longer than its declared length (1035), raised before the item's code number is allocated or its photo file
    /// read; the load transaction rolls back.
    /// </exception>
    /// <exception cref="FileNotFoundException">A data file, or a photo file an item line names, does not exist.</exception>
    /// <exception cref="NoCompanyException">The loader's company was not created.</exception>
    /// <exception cref="InvalidOperationException">
    /// An agent rate names an item category, or a percentage, that no stored category or rate has.
    /// </exception>
    /// <exception cref="ErewhonLockException">A tender item row lock was not obtained during the post-load closure.</exception>
    /// <exception cref="MissingConnectionStringException">
    /// The directory path is valid and the context has no connection string; nothing is deleted.
    /// </exception>
    public Task<SeedLoadResult> LoadAsync(
        string directoryPath,
        TextWriter? output = null,
        CancellationToken ct = default) =>
        RunLoadAsync(directoryPath, output, Stopwatch.StartNew(), loadLockHeld: false, ct);

    /// <summary>
    /// Runs the steps of <see cref="LoadAsync"/> over <paramref name="directoryPath"/>. When
    /// <paramref name="loadLockHeld"/> is <see langword="false"/>, the lock connection is opened and the load lock taken
    /// after the directory check, and the lock released and the lock connection disposed in a <see langword="finally"/>
    /// block after the post-load closure; when it is <see langword="true"/>, the caller holds the lock and releases it.
    /// Writes the final line <c>Database Initialized in &lt;n&gt; seconds</c>, with the whole seconds elapsed on
    /// <paramref name="startTime"/>, whether or not the load succeeds.
    /// </summary>
    /// <param name="directoryPath">The path of the directory holding the seed data files.</param>
    /// <param name="output">The writer that receives each output line; <see cref="Console.Out"/> when <see langword="null"/>.</param>
    /// <param name="startTime">The clock started by the public call.</param>
    /// <param name="loadLockHeld">Whether the caller already holds the load lock on its own lock connection.</param>
    /// <param name="ct">Token that cancels the wait for the lock and the load.</param>
    /// <returns>The number of tender sales the post-load closure built and every line written.</returns>
    private async Task<SeedLoadResult> RunLoadAsync(
        string directoryPath,
        TextWriter? output,
        Stopwatch startTime,
        bool loadLockHeld,
        CancellationToken ct)
    {
        var run = new SeedRun(output ?? Console.Out);

        int total;
        try
        {
            run.DataDirectory = ValidateDirectoryPath(directoryPath);

            if (loadLockHeld)
            {
                total = await LoadWithLockHeldAsync(run, ct);
            }
            else
            {
                NpgsqlConnection lockConnection = await AcquireLoadLockAsync(ct);
                try
                {
                    total = await LoadWithLockHeldAsync(run, ct);
                }
                finally
                {
                    await ReleaseLoadLockAsync(lockConnection);
                }
            }
        }
        finally
        {
            Write(
                run,
                MessageCatalog.Get(MessageKeys.DatabaseInitialized, Invariant(WholeSecondsSince(startTime))));
        }

        return new SeedLoadResult(total, run.Lines.ToArray());
    }

    /// <summary>
    /// Reads the server date as the load date, purges the lowest-id company, runs the load transaction and closes the
    /// tenders due at the load date. The caller holds the load lock.
    /// </summary>
    /// <param name="run">The state of the running load, with its validated data directory.</param>
    /// <param name="ct">Token that cancels the load.</param>
    /// <returns>The number of tender items the post-load closure awarded.</returns>
    private async Task<int> LoadWithLockHeldAsync(SeedRun run, CancellationToken ct)
    {
        DateOnly currentDate = await _serverClock.GetServerDateAsync(ct);

        await PurgeFirstCompanyAsync(ct);
        await LoadInTransactionAsync(run, currentDate, ct);
        return await CloseTendersAtCurrentDateAsync(run, currentDate, ct);
    }

    /// <summary>
    /// Opens the lock connection: a new <see cref="NpgsqlConnection"/> whose connection string is the context's,
    /// copied through <see cref="NpgsqlConnectionStringBuilder"/> with <c>Pooling</c> and <c>Multiplexing</c> set to
    /// <see langword="false"/> and every other setting unchanged, so it opens a physical connection, and a PostgreSQL
    /// session, of its own. On it, outside any transaction, it takes the exclusive session-level advisory lock
    /// <c>pg_advisory_lock(hashtextextended('InitialDataLoader.loadData', 0))</c> with command timeout 0, no timeout;
    /// no <c>lock_timeout</c> is set, so the wait lasts until the lock is granted or <paramref name="ct"/> is
    /// cancelled. When the connection does not open or the lock is not taken, the connection is disposed and the
    /// failure propagates.
    /// </summary>
    /// <param name="ct">Token that cancels the opening of the connection and the wait for the lock.</param>
    /// <returns>
    /// The open lock connection, whose session holds the lock; <see cref="ReleaseLoadLockAsync"/> releases the lock and
    /// disposes it.
    /// </returns>
    /// <exception cref="MissingConnectionStringException">
    /// The context's connection string is <see langword="null"/>, empty or white space; no connection is opened.
    /// </exception>
    private async Task<NpgsqlConnection> AcquireLoadLockAsync(CancellationToken ct)
    {
        string? connectionString = _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new MissingConnectionStringException();
        }

        var lockConnection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Multiplexing = false }
                .ConnectionString);
        try
        {
            await lockConnection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(LoadLockSql, lockConnection)
            {
                CommandTimeout = NoCommandTimeout,
            };
            command.Parameters.AddWithValue(LoadLockNameParameter, LoadLockName);
            await command.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            await lockConnection.DisposeAsync();
            throw;
        }

        return lockConnection;
    }

    /// <summary>
    /// Releases the load lock with <c>pg_advisory_unlock(hashtextextended('InitialDataLoader.loadData', 0))</c> on
    /// <paramref name="lockConnection"/> and <see cref="CancellationToken.None"/>, then disposes the connection, which
    /// closes its physical connection and ends its PostgreSQL session together with any lock the session still holds.
    /// A failure of the release is not thrown: it is logged as one Warning event with the fixed text
    /// <c>The seed load lock could not be released.</c>, which has no message property, and a
    /// <see cref="SanitizedException"/> of the failure attached in its place.
    /// </summary>
    /// <param name="lockConnection">The lock connection returned by <see cref="AcquireLoadLockAsync"/>.</param>
    /// <returns>A task that completes when the lock connection is disposed.</returns>
    private async Task ReleaseLoadLockAsync(NpgsqlConnection lockConnection)
    {
        try
        {
            await using var command = new NpgsqlCommand(LoadUnlockSql, lockConnection);
            command.Parameters.AddWithValue(LoadLockNameParameter, LoadLockName);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception unlockFailure)
        {
            _logger?.LogWarning(new SanitizedException(unlockFailure), "The seed load lock could not be released.");
        }
        finally
        {
            await lockConnection.DisposeAsync();
        }
    }

    /// <summary>
    /// Returns the full path of <paramref name="directoryPath"/>, trimmed of spaces, ending with a directory separator,
    /// once the trimmed path names an existing directory and, followed by <c>/</c> unless it ends with <c>/</c> or
    /// <c>\</c>, is at most 301 characters long. A relative path is resolved against the current directory; the length
    /// is that of the path as given, not of its full path. Ports <c>InitialDataLoader.validateDirectoryPath</c> and the
    /// assignment of its result to the <c>String[301]</c> attribute <c>dirPath</c>.
    /// </summary>
    /// <param name="directoryPath">The directory path to validate.</param>
    /// <returns>The full path of the existing directory, ending with a directory separator.</returns>
    /// <exception cref="ErewhonException">
    /// The trimmed path is empty, is not a valid path or does not name an existing directory (500004).
    /// </exception>
    /// <exception cref="StringTooLongException">
    /// The trimmed path names an existing directory and, followed by <c>/</c> unless it ends with <c>/</c> or
    /// <c>\</c>, is longer than 301 characters (1035, field <c>DataDirectory</c>).
    /// </exception>
    private static string ValidateDirectoryPath(string? directoryPath)
    {
        string returnDirPath = JadeValues.TrimBlanks(directoryPath);
        if (returnDirPath.Length <= 0)
        {
            throw new ErewhonException(ErewhonException.InvalidDirectoryPath);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(returnDirPath);
        }
        catch (Exception invalidPath) when (
            invalidPath is ArgumentException or NotSupportedException or IOException or SecurityException)
        {
            throw new ErewhonException(ErewhonException.InvalidDirectoryPath, invalidPath);
        }

        if (!Directory.Exists(fullPath))
        {
            throw new ErewhonException(ErewhonException.InvalidDirectoryPath);
        }

        string dirPath = returnDirPath.EndsWith(ForwardSlash) || returnDirPath.EndsWith(BackSlash)
            ? returnDirPath
            : returnDirPath + ForwardSlash;
        FieldLengths.Check(dirPath, DataDirectoryMaxLength, nameof(SeedRun.DataDirectory));

        return Path.EndsInDirectorySeparator(fullPath) ? fullPath : fullPath + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Deletes the company with the lowest id, with everything it owns, through <see cref="CompanyService"/> in its own
    /// transaction, without reading the result, then clears the change tracker. Does nothing to the database when no
    /// company exists.
    /// </summary>
    /// <param name="ct">Token that cancels the purge.</param>
    /// <returns>A task that completes when the purge has committed or failed.</returns>
    private async Task PurgeFirstCompanyAsync(CancellationToken ct)
    {
        long? firstCompanyId = await _db.Companies
            .OrderBy(company => company.Id)
            .Select(company => (long?)company.Id)
            .FirstOrDefaultAsync(ct);

        if (firstCompanyId is long companyId)
        {
            await _companyService.DeleteByIdAsync(companyId, TransactionMode.Own, ct);
        }

        _db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Begins the load transaction, creates the company, sets the date offset, loads the ten data files in
    /// <see cref="SeedFileNames.LoadOrder"/> and commits; then publishes the queued notifications with
    /// <see cref="CancellationToken.None"/> and clears the change tracker, also when publication throws. A failure before
    /// the commit returns rolls the transaction back, discards the queued notifications, clears the change tracker and
    /// rethrows.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="currentDate">The load date.</param>
    /// <param name="ct">Token that cancels the load up to the return of the commit; publication ignores it.</param>
    /// <returns>A task that completes when the load has committed and its notifications are published.</returns>
    private async Task LoadInTransactionAsync(SeedRun run, DateOnly currentDate, CancellationToken ct)
    {
        await _unitOfWork.BeginAsync(ct);
        try
        {
            await CreateCompanyAsync(run, ct);
            run.CompanyId = (await _companyContext.GetCurrentAsync(ct)).Id;
            run.DateOffset = currentDate.DayNumber - OffsetStartDate.DayNumber;

            foreach (string fileName in SeedFileNames.LoadOrder)
            {
                await LoadFileAsync(run, fileName, ct);
            }

            await _unitOfWork.CommitAsync(ct);
        }
        catch
        {
            await RollBackLoadAsync();
            throw;
        }

        try
        {
            await _notificationQueue.FlushAsync(CancellationToken.None);
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Rolls back the load transaction when it is still active, discards the queued notifications and clears the change
    /// tracker. A failure of the rollback itself is not thrown: it is logged as one Warning event with the fixed text
    /// <c>The seed load transaction could not be rolled back.</c>, which has no message property, and a
    /// <see cref="SanitizedException"/> of the failure attached in its place.
    /// </summary>
    /// <returns>A task that completes when the load's state is discarded.</returns>
    private async Task RollBackLoadAsync()
    {
        try
        {
            if (_unitOfWork.HasActiveTransaction)
            {
                await _unitOfWork.RollbackAsync(CancellationToken.None);
            }
        }
        catch (Exception rollbackFailure)
        {
            _logger?.LogWarning(
                new SanitizedException(rollbackFailure),
                "The seed load transaction could not be rolled back.");
        }
        finally
        {
            _notificationQueue.Clear();
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Creates the company named <see cref="MessageKeys.CompanyName"/> at the <c>Company*</c> address through
    /// <see cref="CompanyService.CreateCompanyAsync"/> in <see cref="TransactionMode.Ambient"/> mode, which binds it to
    /// the scope, then writes <c>Created &lt;name&gt;, &lt;street&gt;, &lt;city&gt;, &lt;country&gt;, &lt;email&gt;,
    /// &lt;phone&gt;, &lt;fax&gt;, &lt;web site&gt;</c> from the command values. Ports
    /// <c>InitialDataLoader.createCompany</c>.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="ct">Token that cancels the create.</param>
    /// <returns>A task that completes when the company is created and the line written.</returns>
    /// <exception cref="ErewhonException">A company exists (500003).</exception>
    private async Task CreateCompanyAsync(SeedRun run, CancellationToken ct)
    {
        var address = new AddressCommand
        {
            Street = MessageCatalog.Get(MessageKeys.CompanyAddress1),
            City = MessageCatalog.Get(MessageKeys.CompanyAddress2),
            Country = MessageCatalog.Get(MessageKeys.CompanyAddress3),
            Email = MessageCatalog.Get(MessageKeys.CompanyEMail),
            Fax = MessageCatalog.Get(MessageKeys.CompanyFax),
            Phone = MessageCatalog.Get(MessageKeys.CompanyPhone),
            WebSite = MessageCatalog.Get(MessageKeys.CompanyWebSite),
        };
        var companyCommand = new CompanyCommand
        {
            Name = MessageCatalog.Get(MessageKeys.CompanyName),
            Address = address,
        };

        await _companyService.CreateCompanyAsync(companyCommand, TransactionMode.Ambient, ct);

        Write(
            run,
            MessageCatalog.Get(MessageKeys.Created) + Space + JoinFields(
                companyCommand.Name,
                address.Street,
                address.City,
                address.Country,
                address.Email,
                address.Phone,
                address.Fax,
                address.WebSite));
    }

    /// <summary>
    /// Writes <see cref="MessageKeys.ClosingTendersSever"/>, closes the tenders of the bound company due at
    /// <paramref name="currentDate"/> through <see cref="TenderClosureService"/>, then writes
    /// <see cref="MessageKeys.TenderSalesBuilt"/> with the count and the elapsed time when any tender item was awarded,
    /// or <see cref="MessageKeys.NoTendersToCloseAsAt"/> with the date otherwise. Ports
    /// <c>InitialDataLoader.closeTendersAtCurrentDate</c> and its reporting in <c>loadData</c>.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="currentDate">The load date.</param>
    /// <param name="ct">Token that cancels the closure.</param>
    /// <returns>The number of tender items awarded.</returns>
    private async Task<int> CloseTendersAtCurrentDateAsync(SeedRun run, DateOnly currentDate, CancellationToken ct)
    {
        Stopwatch closeTendersStartTime = Stopwatch.StartNew();
        Write(run, MessageCatalog.Get(MessageKeys.ClosingTendersSever));

        int total = await _tenderClosureService.CloseTendersAtDateAsync(currentDate, ct);

        if (total > 0)
        {
            Write(
                run,
                MessageCatalog.Get(MessageKeys.TenderSalesBuilt, Invariant(total), ElapsedText(closeTendersStartTime)));
        }
        else
        {
            Write(run, MessageCatalog.Get(MessageKeys.NoTendersToCloseAsAt, JadeFormat.DateString(currentDate)));
        }

        return total;
    }

    /// <summary>Runs the loader of the data file named <paramref name="fileName"/>.</summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="fileName">One of the file names of <see cref="SeedFileNames.LoadOrder"/>.</param>
    /// <param name="ct">Token that cancels the load.</param>
    /// <returns>A task that completes when the file is loaded.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fileName"/> is not a seed data file name.</exception>
    private Task LoadFileAsync(SeedRun run, string fileName, CancellationToken ct) => fileName switch
    {
        SeedFileNames.LocationsDataFile => LoadLocationsAsync(run, ct),
        SeedFileNames.AgentDataFile => LoadAgentsAsync(run, ct),
        SeedFileNames.ClientsDataFile => LoadClientsAsync(run, ct),
        SeedFileNames.CategoriesDataFile => LoadCategoriesAsync(run, ct),
        SeedFileNames.RetailItemsDataFile => LoadRetailItemsAsync(run, ct),
        SeedFileNames.TenderItemsDataFile => LoadTenderItemsAsync(run, ct),
        SeedFileNames.CategoryRatesDataFile => LoadCategoryRatesAsync(run, ct),
        SeedFileNames.AgentRatesDataFile => LoadAgentRatesAsync(run, ct),
        SeedFileNames.TendersDataFile => LoadTendersAsync(run, ct),
        SeedFileNames.RetailSalesDataFile => LoadRetailSalesAsync(run, ct),
        _ => throw new ArgumentOutOfRangeException(
            nameof(fileName),
            fileName,
            "The file name is not one of the seed data file names."),
    };

    /// <summary>
    /// Loads <see cref="SeedFileNames.LocationsDataFile"/> (country, region): for each line, creates the country when
    /// the bound company has none with that name, then persists the region in it, and writes
    /// <c>Loaded &lt;country&gt;, &lt;region&gt;</c>. Ends with <see cref="MessageKeys.LocationsLoaded"/> and the
    /// company's number of locations. Ports <c>InitialDataLoader.loadLocations</c>.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="ct">Token that cancels the load.</param>
    /// <returns>A task that completes when the file is loaded and its end line written.</returns>
    private async Task LoadLocationsAsync(SeedRun run, CancellationToken ct)
    {
        Stopwatch startClock = Stopwatch.StartNew();
        bool completed = false;
        try
        {
            foreach (string line in OpenDataFile(run, SeedFileNames.LocationsDataFile))
            {
                var tokens = new LineTokens(line);
                var countryCommand = new CountryCommand { Name = tokens.Next() };
                var regionCommand = new RegionCommand { Name = tokens.Next() };

                long? countryId = await FindCountryIdAsync(run.CompanyId, countryCommand.Name, ct);
                if (countryId is null)
                {
                    OperationResult countryResult = await _countryService.ExecuteAsync(
                        countryCommand,
                        TransactionType.Persist,
                        TransactionMode.Ambient,
                        ct);
                    countryId = countryResult.Succeeded ? countryResult.Id : null;
                }

                regionCommand.CountryId = countryId;
                await _regionService.ExecuteAsync(regionCommand, TransactionType.Persist, TransactionMode.Ambient, ct);

                Write(run, MessageCatalog.Get(MessageKeys.LoadedObject, JoinFields(countryCommand.Name, regionCommand.Name)));
            }

            completed = true;
        }
        finally
        {
            await WriteEndLineAsync(
                run,
                completed,
                SeedFileNames.LocationsDataFile,
                async () => MessageCatalog.Get(
                    MessageKeys.LocationsLoaded,
                    Invariant(await _companyService.GetNumberOfLocations(run.CompanyId, ct)),
                    ElapsedText(startClock)));
        }
    }

    /// <summary>
    /// Loads <see cref="SeedFileNames.AgentDataFile"/> (name, street, city, country, email, fax, phone, web site): for
    /// each line, persists the agent with its address and writes <c>Loading &lt;name&gt;, &lt;street&gt;,
    /// &lt;city&gt;, &lt;country&gt;, &lt;email&gt;, &lt;phone&gt;, &lt;fax&gt;, &lt;web site&gt;...</c> from the
    /// command values. Ends with <see cref="MessageKeys.AgentsNumberLoadedInMS"/> and the company's number of agents.
    /// Ports <c>InitialDataLoader.loadAgents</c>.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="ct">Token that cancels the load.</param>
    /// <returns>A task that completes when the file is loaded and its end line written.</returns>
    private async Task LoadAgentsAsync(SeedRun run, CancellationToken ct)
    {
        Stopwatch startClock = Stopwatch.StartNew();
        bool completed = false;
        try
        {
            foreach (string line in OpenDataFile(run, SeedFileNames.AgentDataFile))
            {
                var tokens = new LineTokens(line);
                var agentCommand = new AgentCommand { Name = tokens.Next() };
                AddressCommand address = ReadAddress(tokens);
                agentCommand.Address = address;

                await _agentService.ExecuteAsync(agentCommand, TransactionType.Persist, TransactionMode.Ambient, ct);

                Write(
                    run,
                    MessageCatalog.Get(MessageKeys.Loading, JoinFields(agentCommand.Name, AddressFields(address))));
            }

            completed = true;
        }
        finally
        {
            await WriteEndLineAsync(
                run,
                completed,
                SeedFileNames.AgentDataFile,
                async () => MessageCatalog.Get(
                    MessageKeys.AgentsNumberLoadedInMS,
                    Invariant(await CountAgentsAsync(run.CompanyId, ct)),
                    ElapsedText(startClock)));
        }
    }

    /// <summary>
    /// Loads <see cref="SeedFileNames.ClientsDataFile"/> (name, street, city, country, email, fax, phone, web site): for
    /// each line, persists the client with its address and writes <c>Loaded &lt;name&gt;, &lt;street&gt;, &lt;city&gt;,
    /// &lt;country&gt;, &lt;email&gt;, &lt;phone&gt;, &lt;fax&gt;, &lt;web site&gt;</c> from the command values. Ends
    /// with <see cref="MessageKeys.ClientNumberLoadedInMS"/> and the company's number of clients. Ports
    /// <c>InitialDataLoader.loadClients</c>.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="ct">Token that cancels the load.</param>
    /// <returns>A task that completes when the file is loaded and its end line written.</returns>
    private async Task LoadClientsAsync(SeedRun run, CancellationToken ct)
    {
        Stopwatch startClock = Stopwatch.StartNew();
        bool completed = false;
        try
        {
            foreach (string line in OpenDataFile(run, SeedFileNames.ClientsDataFile))
            {
                var tokens = new LineTokens(line);
                var clientCommand = new ClientCommand { Name = tokens.Next() };
                AddressCommand address = ReadAddress(tokens);
                clientCommand.Address = address;

                await _clientService.ExecuteAsync(clientCommand, TransactionType.Persist, TransactionMode.Ambient, ct);

                Write(
                    run,
                    MessageCatalog.Get(MessageKeys.LoadedObject, JoinFields(clientCommand.Name, AddressFields(address))));
            }

            completed = true;
        }
        finally
        {
            await WriteEndLineAsync(
                run,
                completed,
                SeedFileNames.ClientsDataFile,
                async () => MessageCatalog.Get(
                    MessageKeys.ClientNumberLoadedInMS,
                    Invariant(await CountClientsAsync(run.CompanyId, ct)),
                    ElapsedText(startClock)));
        }
    }

    /// <summary>
    /// Loads <see cref="SeedFileNames.CategoriesDataFile"/> (prefix, name, description): for each line, persists the
    /// item category and writes <c>Loaded &lt;name&gt;, &lt;description&gt;</c> from the command values. Ends with
    /// <see cref="MessageKeys.CategoriesLoadedInMS"/> and the company's number of agents. Ports
    /// <c>InitialDataLoader.loadCategories</c>.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="ct">Token that cancels the load.</param>
    /// <returns>A task that completes when the file is loaded and its end line written.</returns>
    private async Task LoadCategoriesAsync(SeedRun run, CancellationToken ct)
    {
        Stopwatch startClock = Stopwatch.StartNew();
        bool completed = false;
        try
        {
            foreach (string line in OpenDataFile(run, SeedFileNames.CategoriesDataFile))
            {
                var tokens = new LineTokens(line);
                var categoryCommand = new ItemCategoryCommand
                {
                    Prefix = tokens.Next(),
                    Name = tokens.Next(),
                    Description = tokens.Next(),
                };

                await _itemCategoryService.ExecuteAsync(
                    categoryCommand,
                    TransactionType.Persist,
                    TransactionMode.Ambient,
                    ct);

                Write(
                    run,
                    MessageCatalog.Get(
                        MessageKeys.LoadedObject,
                        JoinFields(categoryCommand.Name, categoryCommand.Description)));
            }

            completed = true;
        }
        finally
        {
            await WriteEndLineAsync(
                run,
                completed,
                SeedFileNames.CategoriesDataFile,
                async () => MessageCatalog.Get(
                    MessageKeys.CategoriesLoadedInMS,
                    Invariant(await CountAgentsAsync(run.CompanyId, ct)),
                    ElapsedText(startClock)));
        }
    }

    /// <summary>
    /// Loads <see cref="SeedFileNames.RetailItemsDataFile"/> (GUID, name, description, photo file, price, listed date,
    /// agent, country, region, category): for each line, persists a retail item with the line's GUID, a newly allocated
    /// code number, the photo file's bytes and the shifted listed date, and writes <c>Loaded &lt;guid&gt;,
    /// &lt;name&gt;, &lt;description&gt;, &lt;photo path&gt;, &lt;price&gt;, &lt;listed date&gt;, &lt;agent&gt;,
    /// &lt;country&gt;, &lt;region&gt;, &lt;category&gt;</c> with the listed date as read. Ends with
    /// <see cref="MessageKeys.RetailItemsLoaded"/> and the number of lines. Ports
    /// <c>InitialDataLoader.loadRetailItems</c>.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="ct">Token that cancels the load.</param>
    /// <returns>A task that completes when the file is loaded and its end line written.</returns>
    private async Task LoadRetailItemsAsync(SeedRun run, CancellationToken ct)
    {
        Stopwatch startClock = Stopwatch.StartNew();
        int totalItems = 0;
        bool completed = false;
        try
        {
            foreach (string line in OpenDataFile(run, SeedFileNames.RetailItemsDataFile))
            {
                ItemLine item = ItemLine.Read(new LineTokens(line), run.DataDirectory, hasClosureDate: false);

                RetailItemCommand command = await PopulateItemCommandAsync(new RetailItemCommand(), item, run, ct);
                await _itemService.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Ambient, ct);

                totalItems++;

                Write(
                    run,
                    MessageCatalog.Get(
                        MessageKeys.LoadedObject,
                        JoinFields(
                            item.Guid,
                            item.ShortDescription,
                            item.FullDescription,
                            item.PhotoFileName,
                            JadeFormat.DecimalString(item.Price),
                            DateText(item.ListedDate),
                            item.AgentName,
                            item.CountryName,
                            item.RegionName,
                            item.CategoryName)));
            }

            completed = true;
        }
        finally
        {
            await WriteEndLineAsync(
                run,
                completed,
                SeedFileNames.RetailItemsDataFile,
                () => Task.FromResult(MessageCatalog.Get(
                    MessageKeys.RetailItemsLoaded,
                    Invariant(totalItems),
                    ElapsedText(startClock))));
        }
    }

    /// <summary>
    /// Loads <see cref="SeedFileNames.TenderItemsDataFile"/> (GUID, name, description, photo file, reserve price,
    /// listed date, closure date, agent, country, region, category): for each line, persists a tender item as
    /// <see cref="LoadRetailItemsAsync"/> persists a retail item, with the reserve as its price and the shifted closure
    /// date, and writes the same fields with the closure date as read after the listed date. Ends with
    /// <see cref="MessageKeys.TenderItemsLoaded"/> and the number of lines. Ports
    /// <c>InitialDataLoader.loadTenderItems</c>.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="ct">Token that cancels the load.</param>
    /// <returns>A task that completes when the file is loaded and its end line written.</returns>
    private async Task LoadTenderItemsAsync(SeedRun run, CancellationToken ct)
    {
        Stopwatch startClock = Stopwatch.StartNew();
        int totalItems = 0;
        bool completed = false;
        try
        {
            foreach (string line in OpenDataFile(run, SeedFileNames.TenderItemsDataFile))
            {
                ItemLine item = ItemLine.Read(new LineTokens(line), run.DataDirectory, hasClosureDate: true);

                TenderItemCommand command = await PopulateItemCommandAsync(new TenderItemCommand(), item, run, ct);
                command.ClosureDate = Shift(item.ClosureDate, run.DateOffset);
                await _itemService.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Ambient, ct);

                totalItems++;

                Write(
                    run,
                    MessageCatalog.Get(
                        MessageKeys.LoadedObject,
                        JoinFields(
                            item.Guid,
                            item.ShortDescription,
                            item.FullDescription,
                            item.PhotoFileName,
                            JadeFormat.DecimalString(item.Price),
                            DateText(item.ListedDate),
                            DateText(item.ClosureDate),
                            item.AgentName,
                            item.CountryName,
                            item.RegionName,
                            item.CategoryName)));
            }

            completed = true;
        }
        finally
        {
            await WriteEndLineAsync(
                run,
                completed,
                SeedFileNames.TenderItemsDataFile,
                () => Task.FromResult(MessageCatalog.Get(
                    MessageKeys.TenderItemsLoaded,
                    Invariant(totalItems),
                    ElapsedText(startClock))));
        }
    }

    /// <summary>
    /// Loads <see cref="SeedFileNames.CategoryRatesDataFile"/> (category name, percentage): for each line, persists a
    /// commission rate of the percentage on the named category and writes <c>Loaded &lt;category&gt;,
    /// &lt;percentage&gt;</c>. Ends with <see cref="MessageKeys.CategoryRatesLoadedInMS"/> and the number of lines.
    /// Ports <c>InitialDataLoader.loadCategoryRates</c>.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="ct">Token that cancels the load.</param>
    /// <returns>A task that completes when the file is loaded and its end line written.</returns>
    private async Task LoadCategoryRatesAsync(SeedRun run, CancellationToken ct)
    {
        Stopwatch startClock = Stopwatch.StartNew();
        int count = 0;
        bool completed = false;
        try
        {
            foreach (string line in OpenDataFile(run, SeedFileNames.CategoryRatesDataFile))
            {
                var tokens = new LineTokens(line);
                string? categoryName = tokens.Next();
                decimal percentage = DecimalRules.Round2(DataFileReader.ToDecimal(tokens.Next()));

                var command = new CommissionRateCommand
                {
                    ItemCategoryId = await FindItemCategoryIdAsync(run.CompanyId, categoryName, ct),
                    Percentage = percentage,
                };
                await _commissionRateService.ExecuteAsync(
                    command,
                    TransactionType.Persist,
                    TransactionMode.Ambient,
                    ct);

                Write(
                    run,
                    MessageCatalog.Get(
                        MessageKeys.LoadedObject,
                        JoinFields(categoryName, JadeFormat.DecimalString(percentage))));
                count++;
            }

            completed = true;
        }
        finally
        {
            await WriteEndLineAsync(
                run,
                completed,
                SeedFileNames.CategoryRatesDataFile,
                () => Task.FromResult(MessageCatalog.Get(
                    MessageKeys.CategoryRatesLoadedInMS,
                    Invariant(count),
                    ElapsedText(startClock))));
        }
    }

    /// <summary>
    /// Loads <see cref="SeedFileNames.AgentRatesDataFile"/> (agent, category name, percentage): for each line, reads the
    /// stored rate of that percentage on the named category, adds the named agent to the agents holding it, persists
    /// the rate, and writes <c>Loaded &lt;agent&gt;, &lt;category&gt;, &lt;percentage&gt;</c>. Ends with
    /// <c>&lt;count&gt; </c> and <see cref="MessageKeys.AgentRatesLoadedInMS"/>. Ports
    /// <c>InitialDataLoader.loadAgentRates</c>.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="ct">Token that cancels the load.</param>
    /// <returns>A task that completes when the file is loaded and its end line written.</returns>
    /// <exception cref="InvalidOperationException">
    /// A line names a category that the bound company does not have, or a percentage that the category holds no rate
    /// of: <c>Null object reference</c>.
    /// </exception>
    private async Task LoadAgentRatesAsync(SeedRun run, CancellationToken ct)
    {
        Stopwatch startClock = Stopwatch.StartNew();
        int count = 0;
        bool completed = false;
        try
        {
            foreach (string line in OpenDataFile(run, SeedFileNames.AgentRatesDataFile))
            {
                var tokens = new LineTokens(line);
                string? agentName = tokens.Next();
                string? categoryName = tokens.Next();
                decimal ratePercent = DecimalRules.Round2(DataFileReader.ToDecimal(tokens.Next()));

                long? agentId = await FindAgentIdAsync(run.CompanyId, agentName, ct);
                long categoryId = await FindItemCategoryIdAsync(run.CompanyId, categoryName, ct)
                    ?? throw new InvalidOperationException(NullObjectReferenceText);

                CommissionRate commissionRate = await _db.CommissionRates
                    .Include(rate => rate.AgentCommissionRates)
                    .FirstOrDefaultAsync(
                        rate => rate.ItemCategoryId == categoryId && rate.Percentage == ratePercent,
                        ct)
                    ?? throw new InvalidOperationException(NullObjectReferenceText);

                var command = new CommissionRateCommand();
                command.PopulateFromObject(commissionRate);
                if (agentId is long knownAgentId)
                {
                    command.AgentIds.Add(knownAgentId);
                }

                await _commissionRateService.ExecuteAsync(
                    command,
                    TransactionType.Persist,
                    TransactionMode.Ambient,
                    ct);

                Write(
                    run,
                    MessageCatalog.Get(
                        MessageKeys.LoadedObject,
                        JoinFields(agentName, categoryName, JadeFormat.DecimalString(ratePercent))));
                count++;
            }

            completed = true;
        }
        finally
        {
            await WriteEndLineAsync(
                run,
                completed,
                SeedFileNames.AgentRatesDataFile,
                () => Task.FromResult(
                    Invariant(count) + Space
                    + MessageCatalog.Get(MessageKeys.AgentRatesLoadedInMS, ElapsedText(startClock))));
        }
    }

    /// <summary>
    /// Loads <see cref="SeedFileNames.TendersDataFile"/> (offer, date, time, client, tender item GUID): for each line,
    /// persists a tender with the offer, the shifted date at the line's time, the named client and the tender item with
    /// that GUID, and writes <c>Loaded &lt;offer&gt;, &lt;shifted date&gt;, &lt;time&gt;, &lt;client&gt;,
    /// &lt;guid&gt;</c>, the time being the local time at which the file's load began. Ends with
    /// <see cref="MessageKeys.TendersLoaded"/> and the number of lines. Ports <c>InitialDataLoader.loadTenders</c>.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="ct">Token that cancels the load.</param>
    /// <returns>A task that completes when the file is loaded and its end line written.</returns>
    private async Task LoadTendersAsync(SeedRun run, CancellationToken ct)
    {
        Stopwatch startClock = Stopwatch.StartNew();
        TimeOnly offerTime = TimeOnly.FromDateTime(DateTime.Now);
        int totalTenders = 0;
        bool completed = false;
        try
        {
            foreach (string line in OpenDataFile(run, SeedFileNames.TendersDataFile))
            {
                var tokens = new LineTokens(line);
                decimal offerPrice = DecimalRules.Round2(DataFileReader.ToDecimal(tokens.Next()));
                DateOnly? tenderDate = Shift(DataFileReader.ToDate(tokens.Next()), run.DateOffset);
                TimeOnly? tenderTime = DataFileReader.ToTime(tokens.Next());
                string? clientName = tokens.Next();
                string? guid = tokens.Next();

                var command = new TenderCommand
                {
                    Offer = offerPrice,
                    DateTendered = TimeStamp(tenderDate, tenderTime),
                    ClientId = await FindClientIdAsync(run.CompanyId, clientName, ct),
                    TenderItemId = await FindTenderItemIdAsync(run.CompanyId, guid, ct),
                };
                await _tenderService.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Ambient, ct);

                totalTenders++;

                Write(
                    run,
                    MessageCatalog.Get(
                        MessageKeys.LoadedObject,
                        JoinFields(
                            JadeFormat.DecimalString(offerPrice),
                            DateText(tenderDate),
                            JadeFormat.TimeString(offerTime),
                            clientName,
                            guid)));
            }

            completed = true;
        }
        finally
        {
            await WriteEndLineAsync(
                run,
                completed,
                SeedFileNames.TendersDataFile,
                () => Task.FromResult(MessageCatalog.Get(
                    MessageKeys.TendersLoaded,
                    Invariant(totalTenders),
                    ElapsedText(startClock))));
        }
    }

    /// <summary>
    /// Loads <see cref="SeedFileNames.RetailSalesDataFile"/> (date, time, price, client, retail item GUID): for each
    /// line, persists a retail sale dated the shifted date at the line's time, at the price, to the named client, of
    /// the retail item with that GUID, and writes <c>Loaded &lt;sale timestamp&gt;, &lt;time&gt;, &lt;price&gt;,
    /// &lt;client&gt;, &lt;guid&gt;</c>, the time being the local time at which the file's load began. Ends with
    /// <see cref="MessageKeys.RetailSalesLoaded"/> and the number of lines. Ports
    /// <c>InitialDataLoader.loadRetailSales</c>.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="ct">Token that cancels the load.</param>
    /// <returns>A task that completes when the file is loaded and its end line written.</returns>
    private async Task LoadRetailSalesAsync(SeedRun run, CancellationToken ct)
    {
        Stopwatch startClock = Stopwatch.StartNew();
        TimeOnly saleTime = TimeOnly.FromDateTime(DateTime.Now);
        int totalSales = 0;
        bool completed = false;
        try
        {
            foreach (string line in OpenDataFile(run, SeedFileNames.RetailSalesDataFile))
            {
                var tokens = new LineTokens(line);
                DateOnly? saleDate = Shift(DataFileReader.ToDate(tokens.Next()), run.DateOffset);
                DateTime? timeStamp = TimeStamp(saleDate, DataFileReader.ToTime(tokens.Next()));
                decimal price = DecimalRules.Round2(DataFileReader.ToDecimal(tokens.Next()));
                string? clientName = tokens.Next();
                string? guid = tokens.Next();

                var command = new RetailSaleCommand
                {
                    DateSold = timeStamp,
                    SalePrice = price,
                    ClientId = await FindClientIdAsync(run.CompanyId, clientName, ct),
                    ItemId = await FindRetailItemIdAsync(run.CompanyId, guid, ct),
                };
                await _retailSaleService.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Ambient, ct);

                totalSales++;

                Write(
                    run,
                    MessageCatalog.Get(
                        MessageKeys.LoadedObject,
                        JoinFields(
                            timeStamp is DateTime sold ? JadeFormat.TimeStampString(sold) : string.Empty,
                            JadeFormat.TimeString(saleTime),
                            JadeFormat.DecimalString(price),
                            clientName,
                            guid)));
            }

            completed = true;
        }
        finally
        {
            await WriteEndLineAsync(
                run,
                completed,
                SeedFileNames.RetailSalesDataFile,
                () => Task.FromResult(MessageCatalog.Get(
                    MessageKeys.RetailSalesLoaded,
                    Invariant(totalSales),
                    ElapsedText(startClock))));
        }
    }

    /// <summary>
    /// Sets the item values of <paramref name="command"/> from <paramref name="item"/>, in this order: the GUID, a code
    /// number allocated for the bound company, the description, the agent, the region and the category found by name,
    /// the bytes of the photo file (none when the line names no photo), the price, the name and the shifted listed
    /// date. The GUID is checked against <see cref="FieldLengths.Guid"/> as it is set, before the code number is
    /// allocated, and the name against <see cref="FieldLengths.ItemName"/> as it is set, after the photo and the price.
    /// </summary>
    /// <typeparam name="TCommand">The item command type.</typeparam>
    /// <param name="command">The new command to populate.</param>
    /// <param name="item">The values read from the line.</param>
    /// <param name="run">The state of the running load.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns><paramref name="command"/>, populated.</returns>
    /// <exception cref="StringTooLongException">
    /// The GUID is longer than <see cref="FieldLengths.Guid"/> characters (field <c>Guid</c>), or the name is longer
    /// than <see cref="FieldLengths.ItemName"/> characters (field <c>Name</c>); that value and the values after it are
    /// not set.
    /// </exception>
    private async Task<TCommand> PopulateItemCommandAsync<TCommand>(
        TCommand command,
        ItemLine item,
        SeedRun run,
        CancellationToken ct)
        where TCommand : ItemCommand
    {
        FieldLengths.Check(item.Guid, FieldLengths.Guid, nameof(ItemCommand.Guid));
        command.Guid = item.Guid;
        command.CodeNumber = await _itemCodeAllocator.AllocateNextCodeNumberAsync(run.CompanyId, ct);
        command.Description = item.FullDescription;
        command.AgentId = await FindAgentIdAsync(run.CompanyId, item.AgentName, ct);
        command.RegionId = await FindRegionIdAsync(run.CompanyId, item.CountryName, item.RegionName, ct);
        command.ItemCategoryId = await FindItemCategoryIdAsync(run.CompanyId, item.CategoryName, ct);
        command.Photo = item.PhotoToken is null ? null : await File.ReadAllBytesAsync(item.PhotoFileName, ct);
        command.Price = item.Price;
        FieldLengths.Check(item.ShortDescription, FieldLengths.ItemName, nameof(ItemCommand.Name));
        command.Name = item.ShortDescription;
        command.ListedDate = Shift(item.ListedDate, run.DateOffset);
        return command;
    }

    /// <summary>
    /// Returns the data lines of the data file <paramref name="fileName"/> of the load's directory. When the
    /// enumeration starts, opens the file, then writes <c>Loading &lt;path&gt;...</c>. The file is closed when the
    /// enumeration completes or its enumerator is disposed, and before a failure of that write propagates.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="fileName">The data file name.</param>
    /// <returns>The data lines of the file, read as the enumeration proceeds.</returns>
    /// <exception cref="FileNotFoundException">
    /// The file does not exist, raised when the enumeration starts; nothing is written.
    /// </exception>
    private IEnumerable<string> OpenDataFile(SeedRun run, string fileName)
    {
        string path = Path.Combine(run.DataDirectory, fileName);
        using IEnumerator<string> lines = DataFileReader.ReadDataLines(path).GetEnumerator();
        Write(run, MessageCatalog.Get(MessageKeys.Loading, path));
        while (lines.MoveNext())
        {
            yield return lines.Current;
        }
    }

    /// <summary>
    /// Writes the end line of a loader. When the loader completed, a failure to build the line propagates; when the
    /// loader is failing, such a failure is not thrown and the line is not written: the failure is logged as one
    /// Warning event with the text <c>The end line of seed data file {SeedFile} was not written.</c>, the property
    /// <c>SeedFile</c> set to <paramref name="fileName"/>, and a <see cref="SanitizedException"/> of the failure
    /// attached in its place.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="loaderCompleted">Whether every line of the file was loaded.</param>
    /// <param name="fileName">The data file name, one of <see cref="SeedFileNames.LoadOrder"/>, for the log.</param>
    /// <param name="buildEndLine">Builds the end line.</param>
    /// <returns>A task that completes when the line is written or dropped.</returns>
    private async Task WriteEndLineAsync(
        SeedRun run,
        bool loaderCompleted,
        string fileName,
        Func<Task<string>> buildEndLine)
    {
        if (loaderCompleted)
        {
            Write(run, await buildEndLine());
            return;
        }

        try
        {
            Write(run, await buildEndLine());
        }
        catch (Exception endLineFailure)
        {
            _logger?.LogWarning(
                new SanitizedException(endLineFailure),
                "The end line of seed data file {SeedFile} was not written.",
                fileName);
        }
    }

    /// <summary>
    /// Writes <paramref name="line"/> to the run's output writer and to the log, and appends it to the run's lines.
    /// </summary>
    /// <param name="run">The state of the running load.</param>
    /// <param name="line">The line to write.</param>
    private void Write(SeedRun run, string line)
    {
        run.Output.WriteLine(line);
        _logger?.LogInformation("{SeedLine}", line);
        run.Lines.Add(line);
    }

    private Task<int> CountAgentsAsync(long companyId, CancellationToken ct) =>
        _db.Agents.CountAsync(agent => agent.CompanyId == companyId, ct);

    private Task<int> CountClientsAsync(long companyId, CancellationToken ct) =>
        _db.Clients.CountAsync(client => client.CompanyId == companyId, ct);

    /// <summary>Returns the id of the company's country named exactly <paramref name="name"/>, or <see langword="null"/>.</summary>
    private async Task<long?> FindCountryIdAsync(long companyId, string? name, CancellationToken ct) =>
        JadeValues.IsNull(name)
            ? null
            : await _db.Countries
                .Where(country => country.CompanyId == companyId && country.Name == name)
                .Select(country => (long?)country.Id)
                .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Returns the id of the region named exactly <paramref name="regionName"/> of the company's country named exactly
    /// <paramref name="countryName"/>, or <see langword="null"/>.
    /// </summary>
    private async Task<long?> FindRegionIdAsync(
        long companyId,
        string? countryName,
        string? regionName,
        CancellationToken ct) =>
        JadeValues.IsNull(countryName) || JadeValues.IsNull(regionName)
            ? null
            : await _db.Regions
                .Where(region => region.Country!.CompanyId == companyId
                    && region.Country.Name == countryName
                    && region.Name == regionName)
                .Select(region => (long?)region.Id)
                .FirstOrDefaultAsync(ct);

    /// <summary>Returns the id of the company's agent named exactly <paramref name="name"/>, or <see langword="null"/>.</summary>
    private async Task<long?> FindAgentIdAsync(long companyId, string? name, CancellationToken ct) =>
        JadeValues.IsNull(name)
            ? null
            : await _db.Agents
                .Where(agent => agent.CompanyId == companyId && agent.Name == name)
                .Select(agent => (long?)agent.Id)
                .FirstOrDefaultAsync(ct);

    /// <summary>Returns the id of the company's client named exactly <paramref name="name"/>, or <see langword="null"/>.</summary>
    private async Task<long?> FindClientIdAsync(long companyId, string? name, CancellationToken ct) =>
        JadeValues.IsNull(name)
            ? null
            : await _db.Clients
                .Where(client => client.CompanyId == companyId && client.Name == name)
                .Select(client => (long?)client.Id)
                .FirstOrDefaultAsync(ct);

    /// <summary>Returns the id of the company's item category named exactly <paramref name="name"/>, or <see langword="null"/>.</summary>
    private async Task<long?> FindItemCategoryIdAsync(long companyId, string? name, CancellationToken ct) =>
        JadeValues.IsNull(name)
            ? null
            : await _db.ItemCategories
                .Where(category => category.CompanyId == companyId && category.Name == name)
                .Select(category => (long?)category.Id)
                .FirstOrDefaultAsync(ct);

    /// <summary>Returns the id of the company's retail item with GUID <paramref name="guid"/>, or <see langword="null"/>.</summary>
    private async Task<long?> FindRetailItemIdAsync(long companyId, string? guid, CancellationToken ct) =>
        JadeValues.IsNull(guid)
            ? null
            : await _db.Items
                .OfType<RetailItem>()
                .Where(item => item.CompanyId == companyId && item.Guid == guid)
                .Select(item => (long?)item.Id)
                .FirstOrDefaultAsync(ct);

    /// <summary>Returns the id of the company's tender item with GUID <paramref name="guid"/>, or <see langword="null"/>.</summary>
    private async Task<long?> FindTenderItemIdAsync(long companyId, string? guid, CancellationToken ct) =>
        JadeValues.IsNull(guid)
            ? null
            : await _db.Items
                .OfType<TenderItem>()
                .Where(item => item.CompanyId == companyId && item.Guid == guid)
                .Select(item => (long?)item.Id)
                .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Reads the seven address tokens in file order: street, city, country, email, fax, phone, web site.
    /// </summary>
    private static AddressCommand ReadAddress(LineTokens tokens) => new()
    {
        Street = tokens.Next(),
        City = tokens.Next(),
        Country = tokens.Next(),
        Email = tokens.Next(),
        Fax = tokens.Next(),
        Phone = tokens.Next(),
        WebSite = tokens.Next(),
    };

    /// <summary>Returns the address fields in output order: street, city, country, email, phone, fax, web site.</summary>
    private static string AddressFields(AddressCommand address) => JoinFields(
        address.Street,
        address.City,
        address.Country,
        address.Email,
        address.Phone,
        address.Fax,
        address.WebSite);

    /// <summary>Joins the fields with <c>", "</c>; a <see langword="null"/> field is empty.</summary>
    private static string JoinFields(params string?[] fields) => string.Join(FieldSeparator, fields);

    /// <summary>Returns the date as <c>dd MMMM yyyy</c>, or an empty string for no date.</summary>
    private static string DateText(DateOnly? date) =>
        date is DateOnly value ? JadeFormat.DateString(value) : string.Empty;

    /// <summary>Returns the date moved forward by <paramref name="dateOffset"/> days, or <see langword="null"/> for no date.</summary>
    private static DateOnly? Shift(DateOnly? date, int dateOffset) => date?.AddDays(dateOffset);

    /// <summary>
    /// Returns the timestamp of <paramref name="date"/> at <paramref name="time"/>, midnight when there is no time, with
    /// <see cref="DateTimeKind.Unspecified"/>; <see langword="null"/> when there is no date.
    /// </summary>
    private static DateTime? TimeStamp(DateOnly? date, TimeOnly? time) =>
        date is DateOnly value ? value.ToDateTime(time ?? TimeOnly.MinValue, DateTimeKind.Unspecified) : null;

    /// <summary>
    /// Returns the whole seconds elapsed on <paramref name="startClock"/> as <c>&lt;n&gt; Seconds</c>, or
    /// <see cref="MessageKeys.LessThanOneSecond"/> below one second. Ports <c>InitialDataLoader.getElapsedTimeString</c>.
    /// </summary>
    private static string ElapsedText(Stopwatch startClock)
    {
        long elapsedTime = WholeSecondsSince(startClock);
        if (elapsedTime < 1)
        {
            return MessageCatalog.Get(MessageKeys.LessThanOneSecond);
        }

        return Invariant(elapsedTime) + Space + MessageCatalog.Get(MessageKeys.Seconds);
    }

    /// <summary>Returns the whole seconds elapsed on <paramref name="startClock"/>.</summary>
    private static long WholeSecondsSince(Stopwatch startClock) =>
        (long)startClock.Elapsed.TotalMilliseconds / MillisecondsPerSecond;

    /// <summary>Returns <paramref name="value"/> as invariant-culture text.</summary>
    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The exception attached to a Warning event of the loader in place of the failure the event reports. Its message
    /// is the message of an <see cref="IJadeException"/> failure and the full name of the failure's type for any other
    /// failure; it has no inner exception and no data. Its string form lists the failure, its inner exceptions and the
    /// inner exceptions of any <see cref="AggregateException"/>, depth first, each as its type's full name, followed for
    /// an <see cref="IJadeException"/> by <c>(&lt;error code&gt;): &lt;message&gt;</c>, and by its stack trace when it
    /// has one; its stack trace is the first stack trace in that list. No other exception message, and no data of any
    /// exception in the chain, is carried.
    /// </summary>
    private sealed class SanitizedException : Exception
    {
        private const string InnerSeparator = " ---> ";

        private readonly string _rendering;

        private readonly string? _stackTrace;

        /// <summary>Creates the rendering of <paramref name="failure"/> and its exception chain.</summary>
        /// <param name="failure">The failure the Warning event reports.</param>
        /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
        public SanitizedException(Exception failure)
            : base(MessageOf(failure))
        {
            var segments = new List<string>();
            string? stackTrace = null;
            foreach (Exception step in Chain(failure))
            {
                string segment = Header(step);
                if (step.StackTrace is { } trace)
                {
                    segment += Environment.NewLine + trace;
                    stackTrace ??= trace;
                }

                segments.Add(segment);
            }

            _rendering = string.Join(Environment.NewLine + InnerSeparator, segments);
            _stackTrace = stackTrace;
        }

        /// <summary>The first stack trace of the rendered chain, or <see langword="null"/> when none has one.</summary>
        public override string? StackTrace => _stackTrace;

        /// <summary>Returns the rendered chain.</summary>
        /// <returns>The type names, JADE error codes and messages, and stack traces of the chain.</returns>
        public override string ToString() => _rendering;

        /// <summary>
        /// Returns the message of an <see cref="IJadeException"/> and the full name of the type of any other exception.
        /// </summary>
        private static string MessageOf(Exception failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            return failure is IJadeException ? failure.Message : TypeName(failure);
        }

        /// <summary>
        /// Returns <paramref name="exception"/>, then, depth first, the inner exceptions of an
        /// <see cref="AggregateException"/> or the inner exception of any other exception.
        /// </summary>
        private static IEnumerable<Exception> Chain(Exception exception)
        {
            var pending = new Stack<Exception>();
            pending.Push(exception);
            while (pending.Count > 0)
            {
                Exception current = pending.Pop();
                yield return current;

                if (current is AggregateException aggregate)
                {
                    for (int index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                    {
                        pending.Push(aggregate.InnerExceptions[index]);
                    }
                }
                else if (current.InnerException is { } inner)
                {
                    pending.Push(inner);
                }
            }
        }

        /// <summary>
        /// Returns the full type name of <paramref name="exception"/>, followed for an <see cref="IJadeException"/> by
        /// <c>(&lt;error code&gt;): &lt;message&gt;</c>.
        /// </summary>
        private static string Header(Exception exception) =>
            exception is IJadeException jade
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{TypeName(exception)} ({jade.ErrorCode}): {exception.Message}")
                : TypeName(exception);

        /// <summary>Returns the full name of the type of <paramref name="exception"/>.</summary>
        private static string TypeName(Exception exception)
        {
            Type type = exception.GetType();
            return type.FullName ?? type.Name;
        }
    }

    /// <summary>The state of one running load: its output, its lines, its directory, its company and its date offset.</summary>
    /// <param name="output">The writer that receives each output line.</param>
    private sealed class SeedRun(TextWriter output)
    {
        /// <summary>The writer that receives each output line.</summary>
        public TextWriter Output { get; } = output;

        /// <summary>Every line written, in order.</summary>
        public List<string> Lines { get; } = [];

        /// <summary>The full path of the data directory, ending with a directory separator.</summary>
        public string DataDirectory { get; set; } = string.Empty;

        /// <summary>The id of the company created by the load.</summary>
        public long CompanyId { get; set; }

        /// <summary>The number of days from 1 January 2000 to the load date.</summary>
        public int DateOffset { get; set; }
    }

    /// <summary>Reads the tab-delimited tokens of one data line in order, from its first character.</summary>
    /// <param name="line">The data line.</param>
    private sealed class LineTokens(string line)
    {
        private int _pos = DataFileReader.FirstIndexInLine;

        /// <summary>Returns the next token of the line, or <see langword="null"/> for none or for <c>null</c>.</summary>
        /// <returns>The token.</returns>
        public string? Next() => DataFileReader.GetNextToken(line, ref _pos);
    }

    /// <summary>The values of one line of <c>RetailItems.txt</c> or <c>TenderItems.txt</c>, as read.</summary>
    /// <param name="Guid">The item GUID.</param>
    /// <param name="ShortDescription">The item name.</param>
    /// <param name="FullDescription">The item description.</param>
    /// <param name="PhotoToken">The photo file name, or <see langword="null"/> for none.</param>
    /// <param name="PhotoFileName">The data directory path followed by the photo file name.</param>
    /// <param name="Price">The price or reserve price, rounded to 2 decimal places.</param>
    /// <param name="ListedDate">The listed date, unshifted.</param>
    /// <param name="ClosureDate">The closure date of a tender item, unshifted; <see langword="null"/> for a retail item.</param>
    /// <param name="AgentName">The agent name.</param>
    /// <param name="CountryName">The country name.</param>
    /// <param name="RegionName">The region name.</param>
    /// <param name="CategoryName">The item category name.</param>
    private sealed record ItemLine(
        string? Guid,
        string? ShortDescription,
        string? FullDescription,
        string? PhotoToken,
        string PhotoFileName,
        decimal Price,
        DateOnly? ListedDate,
        DateOnly? ClosureDate,
        string? AgentName,
        string? CountryName,
        string? RegionName,
        string? CategoryName)
    {
        /// <summary>
        /// Reads the item tokens in file order: GUID, name, description, photo file, price, listed date, the closure date
        /// when <paramref name="hasClosureDate"/>, agent, country, region and category.
        /// </summary>
        /// <param name="tokens">The tokens of the line.</param>
        /// <param name="dataDirectory">The data directory path, ending with a directory separator.</param>
        /// <param name="hasClosureDate">Whether the line holds a closure date after the listed date.</param>
        /// <returns>The values of the line.</returns>
        public static ItemLine Read(LineTokens tokens, string dataDirectory, bool hasClosureDate)
        {
            string? guid = tokens.Next();
            string? shortDesc = tokens.Next();
            string? fullDesc = tokens.Next();
            string? photoToken = tokens.Next();
            decimal price = DecimalRules.Round2(DataFileReader.ToDecimal(tokens.Next()));
            DateOnly? listedDate = DataFileReader.ToDate(tokens.Next());
            DateOnly? closureDate = hasClosureDate ? DataFileReader.ToDate(tokens.Next()) : null;
            string? agentName = tokens.Next();
            string? countryName = tokens.Next();
            string? regionName = tokens.Next();
            string? categoryName = tokens.Next();

            return new ItemLine(
                guid,
                shortDesc,
                fullDesc,
                photoToken,
                Path.Join(dataDirectory, photoToken),
                price,
                listedDate,
                closureDate,
                agentName,
                countryName,
                regionName,
                categoryName);
        }
    }
}

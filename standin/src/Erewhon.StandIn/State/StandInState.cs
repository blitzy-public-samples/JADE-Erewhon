using System.Globalization;
using Erewhon.Contracts;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Seed;
using Erewhon.Contracts.Wire;
using Erewhon.StandIn.Model;
using Erewhon.StandIn.Seed;
using Microsoft.Extensions.Options;

namespace Erewhon.StandIn.State;

/// <summary>
/// The <c>StandIn</c> configuration section: where the seed files are, which date replaces JADE's
/// current date, and the three listeners.
/// </summary>
/// <remarks>
/// The property names are the keys of <c>appsettings.json</c>, so environment variables such as
/// <c>StandIn__SeedDirectory=/app/seed</c> and command-line switches such as
/// <c>--StandIn:AdminPort=15090</c> override them.
/// </remarks>
public sealed class StandInOptions
{
    /// <summary>The configuration section this class binds to.</summary>
    public const string SectionName = "StandIn";

    /// <summary>
    /// The seed directory, absolute or relative to the process working directory: the repository's
    /// <c>Erewhon/DataFiles</c> seen from <c>standin/</c> by default, <c>/app/seed</c> in the image.
    /// Leading and trailing blanks are ignored, as the seed loader's JADE <c>validateDirectoryPath</c>
    /// ignores them, so the fingerprinted directory is always the loaded one
    /// (<see cref="StandInState.ResolveSeedDirectory(string?)"/>).
    /// </summary>
    public string SeedDirectory { get; set; } = "../Erewhon/DataFiles";

    /// <summary>
    /// The date that replaces JADE's current date, in <c>yyyy-MM-dd</c> form. It must equal the
    /// harness's <c>ExpectedReferenceDate</c>, and lie from 1001-01-01 to 8999-12-31 inclusive: the
    /// loader moves every seed date (all in 1999 and 2000) by the days from 2000-01-01 to this date,
    /// and the range keeps any seed date from 1000-01-01 to 2999-12-31, and the clock that starts at
    /// this date's 12:00:00 UTC, inside the representable dates after that move.
    /// </summary>
    public string ReferenceDate { get; set; } = "2026-01-01";

    /// <summary>The REST listener port, as the JADE <c>ErewhonREST</c> application.</summary>
    public int RestPort { get; set; } = 65000;

    /// <summary>The SOAP listener port, as the JADE <c>WebServiceHTTP</c> application.</summary>
    public int SoapPort { get; set; } = 21001;

    /// <summary>The internal admin listener port.</summary>
    public int AdminPort { get; set; } = AdminRoutes.DefaultPort;

    /// <summary>
    /// The address every listener binds to; <c>harness/scripts/run-local.sh</c> uses <c>127.0.0.1</c>.
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0";
}

/// <summary>The state a reset rebuilds.</summary>
public enum ResetKind
{
    /// <summary>The model loaded from the seed files.</summary>
    Seed,

    /// <summary>No company at all, so REST <c>getCompany</c> answers 500 with <c>null</c>.</summary>
    Empty,
}

/// <summary>
/// Maps <see cref="ResetKind"/> to and from the <c>state</c> query value of
/// <see cref="AdminRoutes.Reset"/>.
/// </summary>
public static class ResetKinds
{
    /// <summary>
    /// Parses a <c>state</c> query value: <see cref="AdminRoutes.ResetStateSeed"/> or
    /// <see cref="AdminRoutes.ResetStateEmpty"/>, ignoring case.
    /// </summary>
    /// <param name="value">The query value; null and empty are not a kind.</param>
    /// <param name="kind">The parsed kind; <see cref="ResetKind.Seed"/> when the value is not a kind.</param>
    /// <returns>True when <paramref name="value"/> names a kind.</returns>
    public static bool TryParse(string? value, out ResetKind kind)
    {
        if (string.Equals(value, AdminRoutes.ResetStateSeed, StringComparison.OrdinalIgnoreCase))
        {
            kind = ResetKind.Seed;
            return true;
        }

        if (string.Equals(value, AdminRoutes.ResetStateEmpty, StringComparison.OrdinalIgnoreCase))
        {
            kind = ResetKind.Empty;
            return true;
        }

        kind = ResetKind.Seed;
        return false;
    }

    /// <summary>Returns the <c>state</c> query value that selects <paramref name="kind"/>.</summary>
    /// <param name="kind">The reset kind.</param>
    /// <returns><see cref="AdminRoutes.ResetStateSeed"/> or <see cref="AdminRoutes.ResetStateEmpty"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined kind.</exception>
    public static string ToQueryValue(ResetKind kind) => kind switch
    {
        ResetKind.Seed => AdminRoutes.ResetStateSeed,
        ResetKind.Empty => AdminRoutes.ResetStateEmpty,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a reset kind."),
    };
}

/// <summary>
/// The facts <see cref="AdminRoutes.Info"/> reports, which the harness checks after every reset:
/// the generation has increased, <paramref name="Mutations"/> is 0, <paramref name="Source"/> is
/// <see cref="SourceLabels.StandIn"/>, and the seed hash and reference date equal the baseline
/// manifest's.
/// </summary>
/// <param name="Source">Always <see cref="SourceLabels.StandIn"/>.</param>
/// <param name="SeedSha256">The <see cref="SeedFingerprint"/> of the seed directory, as 64 lowercase hex digits.</param>
/// <param name="ReferenceDate">The reference date as <c>yyyy-MM-dd</c>.</param>
/// <param name="Generation">The number of completed resets since the process started.</param>
/// <param name="Mutations">
/// The number of state-changing requests answered since the last reset, each counted once whether its
/// handler ran or it was rejected before the handler (<see cref="StandInState.Mutations"/>).
/// </param>
public sealed record StandInInfo(string Source, string SeedSha256, string ReferenceDate, long Generation, long Mutations);

/// <summary>
/// The clock and GUID source of one state-changing request, created by
/// <see cref="StandInState.Mutate{T}(Func{Company?, MutationContext, T})"/> and valid only inside
/// its callback.
/// </summary>
public sealed class MutationContext
{
    private readonly Func<string> _newGuid;

    internal MutationContext(Company? company, Func<string> newGuid, DateTime now)
    {
        _newGuid = newGuid;
        Now = now;
        Today = DateOnly.FromDateTime(now);
        Model = new ModelContext(company, newGuid, now);
    }

    /// <summary>
    /// The request's "now", in UTC: the value of a JADE <c>TimeStamp</c> local initialised to the
    /// current time, such as the <c>timestamp</c> of <c>postCreateRetailSale</c> and
    /// <c>postCreateTender</c>.
    /// </summary>
    public DateTime Now { get; }

    /// <summary>The date of <see cref="Now"/>: the server date for closure and TA date checks.</summary>
    public DateOnly Today { get; }

    /// <summary>
    /// The model context for the transaction agents: the current company, <see cref="NewGuid"/>
    /// as its GUID source and <see cref="Now"/> as its clock.
    /// </summary>
    public ModelContext Model { get; }

    /// <summary>Returns the next generated GUID of the current state, in braced JADE form.</summary>
    public string NewGuid() => _newGuid();
}

/// <summary>
/// The stand-in's one live model, registered as a singleton: the lock every request handler runs
/// under, the reset that rebuilds the model, and the deterministic GUIDs and clock.
/// </summary>
/// <remarks>
/// <para>
/// One lock serialises every handler, which mirrors the effect of a JADE transaction and keeps the
/// replay order deterministic. <c>Rest/RestEndpoints.cs</c> and <c>Soap/SoapEndpoint.cs</c> call
/// <see cref="Mutate{T}(Func{Company?, MutationContext, T})"/> exactly when the resolved operation's
/// <see cref="Erewhon.Contracts.Catalogue.OperationDescriptor.StateChanging"/> is true (the four REST
/// <c>postCreate*</c> and the four SOAP <c>update*</c> operations), never from a list of their own,
/// and <see cref="Read{T}(Func{Company?, T})"/> otherwise; a state-changing request they answer
/// without its handler is counted with <see cref="CountRejected"/>. Callers finish every asynchronous
/// step, reading and parsing the request body included, before entering either; the callbacks are
/// synchronous, never wait on I/O and never call <see cref="Read{T}(Func{Company?, T})"/> or
/// <see cref="Mutate{T}(Func{Company?, MutationContext, T})"/> themselves.
/// </para>
/// <para>
/// No real clock or random GUID is ever read. "Now" is <see cref="ClockBase"/> plus one second per
/// state-changing request answered since the last reset, whether its handler ran or it was rejected
/// before the handler (<see cref="Mutations"/>), and every generated GUID comes from a counter that
/// each reset restarts at 1, so a re-capture repeats every value on every run and every process
/// restart. The state lives in memory only and no write is recorded anywhere.
/// </para>
/// </remarks>
public sealed partial class StandInState
{
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly TimeOnly ClockStartTime = new(12, 0, 0);

    // The loader moves every seed date by the days from 2000-01-01 to the reference date, and the
    // seed's dates all lie in 1999 and 2000. With the reference date in this inclusive range, any seed
    // date from 1000-01-01 to 2999-12-31 stays inside the DateOnly and DateTime range (0001-01-01 to
    // 9999-12-31) after that shift, and the clock (the reference date at 12:00:00 UTC plus one second
    // per state-changing request) starts at least 1000 years before DateTime.MaxValue.
    private static readonly DateOnly MinReferenceDate = new(1001, 1, 1);
    private static readonly DateOnly MaxReferenceDate = new(8999, 12, 31);

    // Serialises every request handler and guards _current, _generation and _mutations.
    private readonly Lock _gate = new();

    // Serialises resets, so concurrent resets cannot interleave generations or GUID sequences while
    // the seed is loaded outside _gate. A semaphore, so an admin reset waits for it without holding a
    // thread and stops waiting when its client goes away. It is never disposed: its wait handle is
    // never requested, so it holds no unmanaged resource, and it lives as long as this singleton.
    private readonly SemaphoreSlim _resetGate = new(1, 1);

    // Before the first reset: no company, no fingerprint, and a GUID counter of this instance's own.
    private Snapshot _current = new(null, ResetKind.Empty, "", null, new GuidSequence());
    private long _generation;
    private long _mutations;

    private readonly ILogger<StandInState> _logger;

    /// <summary>Creates the state with no company; <see cref="Reset(ResetKind)"/> loads it.</summary>
    /// <param name="options">The <see cref="StandInOptions.SectionName"/> settings.</param>
    /// <param name="logger">Receives the console failures a published reset survives.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="logger"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// <c>StandIn:SeedDirectory</c> is empty or not a valid path
    /// (<see cref="ResolveSeedDirectory(string?)"/>), or <c>StandIn:ReferenceDate</c> is not a
    /// <c>yyyy-MM-dd</c> date from 1001-01-01 to 8999-12-31, the range that keeps every seed date
    /// moved by its offset from 2000-01-01, and the clock, representable.
    /// </exception>
    public StandInState(IOptions<StandInOptions> options, ILogger<StandInState> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        StandInOptions settings = options.Value;

        SeedDirectory = ResolveSeedDirectory(settings.SeedDirectory);

        if (!DateOnly.TryParseExact(settings.ReferenceDate, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly referenceDate))
        {
            throw new InvalidOperationException(
                $"{StandInOptions.SectionName}:{nameof(StandInOptions.ReferenceDate)} must be a {DateFormat} date; the value is '{settings.ReferenceDate}'.");
        }

        if (referenceDate < MinReferenceDate || referenceDate > MaxReferenceDate)
        {
            throw new InvalidOperationException(
                $"{StandInOptions.SectionName}:{nameof(StandInOptions.ReferenceDate)} must be between {MinReferenceDate.ToString(DateFormat, CultureInfo.InvariantCulture)} and {MaxReferenceDate.ToString(DateFormat, CultureInfo.InvariantCulture)}; the value is '{settings.ReferenceDate}'.");
        }

        ReferenceDate = referenceDate;
        ReferenceDateText = referenceDate.ToString(DateFormat, CultureInfo.InvariantCulture);
        ClockBase = referenceDate.ToDateTime(ClockStartTime, DateTimeKind.Utc);
    }

    /// <summary>
    /// Resolves a <c>StandIn:SeedDirectory</c> value to the absolute directory every reset
    /// fingerprints and loads. The constructor uses it, and start-up diagnostics use it to name the
    /// same directory before any state exists.
    /// </summary>
    /// <remarks>
    /// The value is trimmed of blanks with <see cref="SeedTokenizer.TrimBlanks(string)"/>, the rule the
    /// seed loader's JADE <c>validateDirectoryPath</c> applies, and then resolved against the process
    /// working directory, not the content root: <c>standin/</c> for a local process, so the relative
    /// default reaches <c>Erewhon/DataFiles</c>; the image sets an absolute path. The loader's own trim
    /// never changes the result, so the seed fingerprint and the seed load read one directory.
    /// </remarks>
    /// <param name="configured">The configured value; null, empty and white space name no directory.</param>
    /// <returns>The absolute seed directory.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="configured"/> names no directory, or is not a valid path.
    /// </exception>
    public static string ResolveSeedDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{StandInOptions.SectionName}:{nameof(StandInOptions.SeedDirectory)} must name the seed directory.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(SeedTokenizer.TrimBlanks(configured));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"{StandInOptions.SectionName}:{nameof(StandInOptions.SeedDirectory)} must be a valid directory path; the value is '{configured}'. {ex.Message}",
                ex);
        }

        // A "." or ".." segment can resolve to a last segment that ends in a blank ("dir /." is
        // "dir "). A trailing separator names the same directory and keeps the loader's trim from
        // dropping that blank.
        return fullPath.EndsWith(' ') ? fullPath + Path.DirectorySeparatorChar : fullPath;
    }

    /// <summary>The absolute seed directory every reset reads.</summary>
    public string SeedDirectory { get; }

    /// <summary>The date that replaces JADE's current date (KL-STANDIN-DATE).</summary>
    public DateOnly ReferenceDate { get; }

    /// <summary><see cref="ReferenceDate"/> as <c>yyyy-MM-dd</c>.</summary>
    public string ReferenceDateText { get; }

    /// <summary>
    /// <see cref="ReferenceDate"/> at 12:00:00 UTC: the load-time "now", and the clock after a reset
    /// before any state-changing request.
    /// </summary>
    public DateTime ClockBase { get; }

    /// <summary>The number of completed resets; 0 before the first.</summary>
    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    /// <summary>
    /// The number of state-changing requests answered since the last reset, each counted exactly once: by
    /// <see cref="Mutate{T}(Func{Company?, MutationContext, T})"/> when its handler runs, whether the handler
    /// accepts it or rejects it with 400 or 404, and by <see cref="CountRejected"/> when it is rejected
    /// before its handler runs.
    /// </summary>
    /// <remarks>
    /// A request resolved to a read operation or to no operation is never counted, nor is a state-changing
    /// one that its client abandons, or the transport refuses, before its handler runs. "Now" is
    /// <see cref="ClockBase"/> plus this many seconds.
    /// </remarks>
    public long Mutations
    {
        get
        {
            lock (_gate)
            {
                return _mutations;
            }
        }
    }

    /// <summary>The seed fingerprint taken at the last reset; empty before the first.</summary>
    public string SeedSha256
    {
        get
        {
            lock (_gate)
            {
                return _current.SeedSha256;
            }
        }
    }

    /// <summary>The kind of the last reset; <see cref="ResetKind.Empty"/> before the first.</summary>
    public ResetKind CurrentKind
    {
        get
        {
            lock (_gate)
            {
                return _current.Kind;
            }
        }
    }

    /// <summary>The seed load of the last reset; null after an empty reset and before the first.</summary>
    public SeedLoadResult? LastLoad
    {
        get
        {
            lock (_gate)
            {
                return _current.Load;
            }
        }
    }

    /// <summary>
    /// The current "now" without advancing it: <see cref="ClockBase"/> plus <see cref="Mutations"/> seconds.
    /// </summary>
    public DateTime CurrentNow
    {
        get
        {
            lock (_gate)
            {
                return ClockBase.AddSeconds(_mutations);
            }
        }
    }

    /// <summary>
    /// Runs a read-only handler under the state lock. It never changes <see cref="Mutations"/> or the clock.
    /// </summary>
    /// <typeparam name="T">The handler's result.</typeparam>
    /// <param name="read">The handler; it receives the current company, null in the empty state.</param>
    /// <returns>The handler's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is null.</exception>
    public T Read<T>(Func<Company?, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
        {
            return read(_current.Company);
        }
    }

    /// <summary>
    /// Runs a state-changing handler under the state lock, at the next second of the clock.
    /// </summary>
    /// <remarks>
    /// The mutation count advances before the handler runs, whatever its outcome, so a request
    /// rejected with 400 or 404, or one that throws, still moves the clock: "now" is always
    /// <see cref="ClockBase"/> plus <see cref="Mutations"/> seconds, and the first write after a reset
    /// runs at 12:00:01 UTC. Nothing is rolled back; handlers report business errors as results. A
    /// state-changing request answered without its handler is counted by <see cref="CountRejected"/>
    /// instead, never by both.
    /// </remarks>
    /// <typeparam name="T">The handler's result.</typeparam>
    /// <param name="mutate">
    /// The handler; it receives the current company, null in the empty state, and the request's
    /// clock and GUID source.
    /// </param>
    /// <returns>The handler's result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mutate"/> is null.</exception>
    public T Mutate<T>(Func<Company?, MutationContext, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            _mutations++;
            DateTime now = ClockBase.AddSeconds(_mutations);
            Snapshot current = _current;
            var context = new MutationContext(current.Company, current.Guids.Next, now);
            return mutate(current.Company, context);
        }
    }

    /// <summary>
    /// Accounts for a state-changing request answered without its handler running, exactly as
    /// <see cref="Mutate{T}(Func{Company?, MutationContext, T})"/> accounts for one whose handler runs:
    /// <see cref="Mutations"/> advances by one, and with it the clock.
    /// </summary>
    /// <remarks>
    /// The endpoints call it once for a request resolved to a state-changing operation and rejected
    /// before its handler: an input that does not convert, an envelope without the operation's request
    /// element or parameters, an output format the stand-in does not model, or a failure before the
    /// handler. No handler runs, no GUID is taken and nothing else changes.
    /// </remarks>
    public void CountRejected()
    {
        lock (_gate)
        {
            _mutations++;
        }
    }

    /// <summary>
    /// Rebuilds the state and swaps it in atomically: the generation advances, the mutation count and
    /// clock restart, and the GUID counter restarts at 1. Start-up calls this form, on the calling
    /// thread; the admin endpoint calls <see cref="ResetAsync(ResetKind, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// The new state is built outside the request lock, so requests are held only for the swap; a
    /// request served during the load still sees the previous state, with its own GUID counter. Both
    /// kinds fingerprint the seed directory, because the harness compares the reported hash with the
    /// baseline manifest after an empty reset too. Every exception is thrown before the swap, so a
    /// reset that throws leaves the previous state, generation and mutation count as they were, and a
    /// reset that swaps always returns its <see cref="StandInInfo"/>. Every reset prints
    /// <see cref="SourceLabels.Banner"/>, a summary line and, for <see cref="ResetKind.Seed"/>, the
    /// load description; they are composed before the swap and written after it, and a console that
    /// cannot be written is logged as a warning without failing the reset.
    /// </remarks>
    /// <param name="kind">The state to build.</param>
    /// <returns>The <see cref="StandInInfo"/> of the new state.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined kind.</exception>
    /// <exception cref="DirectoryNotFoundException">The seed directory does not exist.</exception>
    /// <exception cref="IOException">A seed file could not be read or is not a regular file.</exception>
    /// <exception cref="InvalidDataException">
    /// A seed row cannot be loaded: a value does not convert, an offset date is out of range, a photo
    /// name is refused, its image file is missing, unreadable or not a regular file, or an agent rate
    /// names a missing agent, category or rate or repeats an agent the rate already holds (see
    /// <see cref="SeedLoader.Load(string, DateOnly, ModelContext, CancellationToken)"/>). The message
    /// names the seed file, its line and the field, category or rate at fault.
    /// </exception>
    /// <exception cref="InvalidOperationException">The seed company could not be created.</exception>
    public StandInInfo Reset(ResetKind kind)
    {
        string kindText = ResetKinds.ToQueryValue(kind);

        _resetGate.Wait();
        try
        {
            return BuildAndPublish(kind, kindText, CancellationToken.None);
        }
        finally
        {
            _resetGate.Release();
        }
    }

    /// <summary>
    /// The admin form of <see cref="Reset(ResetKind)"/>: the same reset, which waits for a running
    /// reset without holding a thread, builds the new state off the request thread and stops before
    /// the swap once <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reset gate admits one reset at a time, so at most one worker builds a state, whatever the
    /// number of waiting requests. Cancellation is cooperative: it is observed while waiting for the
    /// gate, before and after the seed fingerprint, between the seed files, lines, image chunks and
    /// rows of the load, and once more immediately before the swap. A cancelled reset leaves the
    /// previous state, generation and mutation count as they were; once that last check passes, the
    /// swap and the console lines are no longer cancellable and the reset completes.
    /// </para>
    /// <para>
    /// The load never opens a seed or image entry of length 0, so a FIFO or device in the seed
    /// directory cannot block it. Two points remain that no cancellation interrupts, and the request
    /// waits for the worker to stop rather than answer while it may still publish: the shared
    /// <see cref="SeedFingerprint"/>, which opens and reads every file in the directory synchronously
    /// and so waits on a FIFO there until a writer opens it, and an entry replaced by a FIFO between
    /// the load's length check and its open.
    /// </para>
    /// </remarks>
    /// <param name="kind">The state to build.</param>
    /// <param name="cancellationToken">Stops the reset before the swap, normally the request's abort token.</param>
    /// <returns>The <see cref="StandInInfo"/> of the new state.</returns>
    /// <inheritdoc cref="Reset(ResetKind)" path="/exception"/>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the swap; nothing changed.
    /// </exception>
    public async Task<StandInInfo> ResetAsync(ResetKind kind, CancellationToken cancellationToken)
    {
        string kindText = ResetKinds.ToQueryValue(kind);

        await _resetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The one reset worker: the gate is held until it ends, so no second build can start.
            return await Task.Run(() => BuildAndPublish(kind, kindText, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _resetGate.Release();
        }
    }

    /// <summary>
    /// Builds the new state, then swaps it in and prints the reset lines; callers hold the reset
    /// gate. Every exception, a cancellation included, is thrown before the swap; from the swap on
    /// nothing throws.
    /// </summary>
    private StandInInfo BuildAndPublish(ResetKind kind, string kindText, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string seedSha256 = SeedFingerprint.ComputeSha256Hex(SeedDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        // Created before the load, so load-time GUIDs take 1, 2, 3, ... in creation order and
        // request-time GUIDs continue from where the load stopped.
        var guids = new GuidSequence();
        SeedLoadResult? load = null;
        if (kind == ResetKind.Seed)
        {
            var loadContext = new ModelContext(null, guids.Next, ClockBase);
            load = SeedLoader.Load(SeedDirectory, ReferenceDate, loadContext, cancellationToken);
        }

        var snapshot = new Snapshot(load?.Company, kind, seedSha256, load, guids);

        // Only a reset changes the generation and resets are serialised, so the generation this
        // reset publishes is known now. Every console line is composed before the swap, so that
        // nothing after it can fail.
        long generation = Generation + 1;
        string summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{SourceLabels.ConsolePrefix} reset generation={generation} kind={kindText} referenceDate={ReferenceDateText} seedSha256={seedSha256}");
        string? description = load?.Describe();

        // The last point at which the reset can still stop with nothing changed.
        cancellationToken.ThrowIfCancellationRequested();

        StandInInfo info;
        lock (_gate)
        {
            _current = snapshot;
            _generation = generation;
            _mutations = 0;
            info = CreateInfo();
        }

        WriteResetLines(generation, summary, description);
        return info;
    }

    /// <summary>Returns the facts <see cref="AdminRoutes.Info"/> reports.</summary>
    /// <returns>The current <see cref="StandInInfo"/>.</returns>
    public StandInInfo GetInfo()
    {
        lock (_gate)
        {
            return CreateInfo();
        }
    }

    // Callers hold _gate.
    private StandInInfo CreateInfo() =>
        new(SourceLabels.StandIn, _current.SeedSha256, ReferenceDateText, _generation, _mutations);

    /// <summary>
    /// Prints <see cref="SourceLabels.Banner"/>, the reset summary and, when present, the load
    /// description, after the new state is published. The reset has then already happened, so a
    /// console that cannot be written (a full disk, a file-size limit, a closed or redirected stream
    /// that rejects writes) is logged as a warning instead of failing the reset.
    /// </summary>
    private void WriteResetLines(long generation, string summary, string? description)
    {
        try
        {
            Console.WriteLine(SourceLabels.Banner);
            Console.WriteLine(summary);
            if (description != null)
            {
                Console.WriteLine(description);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or ObjectDisposedException
            or UnauthorizedAccessException or NotSupportedException)
        {
            LogResetOutputFailed(_logger, ex, generation);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Reset generation {Generation} is published, but its console lines could not be written")]
    private static partial void LogResetOutputFailed(ILogger logger, Exception exception, long generation);

    /// <summary>
    /// One built state. The fields never change; the company's object graph is the live model that
    /// state-changing handlers modify under the request lock.
    /// </summary>
    private sealed record Snapshot(
        Company? Company,
        ResetKind Kind,
        string SeedSha256,
        SeedLoadResult? Load,
        GuidSequence Guids);

    /// <summary>
    /// The deterministic GUID source of one state: call n returns
    /// <c>{00000000-0000-4000-8000-</c> n as 12 lowercase hex digits <c>}</c>, a braced 38-character
    /// JADE GUID such as <c>{00000000-0000-4000-8000-00000000001a}</c> for n = 26.
    /// </summary>
    /// <remarks>
    /// Not thread-safe: it is called only while loading under the reset lock, before its state is
    /// published, and afterwards only under the request lock.
    /// </remarks>
    private sealed class GuidSequence
    {
        private const string Prefix = "00000000-0000-4000-8000-";
        private const long MaxCounter = 0xFFFF_FFFF_FFFF;

        private long _next = 1;

        public string Next()
        {
            long n = _next;
            if (n > MaxCounter)
            {
                throw new InvalidOperationException(
                    "The generated GUID counter is exhausted; reset the stand-in state.");
            }

            _next = n + 1;

            string digits = Prefix + n.ToString("x12", CultureInfo.InvariantCulture);
            string expected = "{" + digits + "}";
            string text = JadeWireFormats.FormatJadeGuid(Guid.ParseExact(digits, "D"));

            // Generated GUIDs are the N1 normalisation targets; a drift in the shared formatter must
            // fail here rather than silently change every recorded value.
            if (!JadeWireFormats.IsValidJadeGuid(text) || !string.Equals(text, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"JadeWireFormats.FormatJadeGuid produced '{text}' for generated GUID {n}; expected '{expected}'.");
            }

            return text;
        }
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.Json;
using Erewhon.Contracts;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Seed;
using Erewhon.Contracts.Wire;
using Erewhon.Harness.Http;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Lifecycle;

/// <summary>
/// The members of a component's <c>GET /admin/info</c> response the harness reads. The stand-in reports
/// its source label, seed fingerprint, reference date, reset generation and mutation count; the facade
/// reports its framework description and whether it publishes the contracts WSDL. Every member is null
/// when the response did not supply it.
/// </summary>
/// <param name="Source">The source label; <c>jade-standin</c> for the stand-in.</param>
/// <param name="SeedSha256">The seed fingerprint, lowercase hexadecimal.</param>
/// <param name="ReferenceDate">The reference date, normally <c>yyyy-MM-dd</c>.</param>
/// <param name="Generation">The reset generation, which every reset increases.</param>
/// <param name="Mutations">The state-changing requests handled since the last reset.</param>
/// <param name="FrameworkDescription">The component's <c>RuntimeInformation.FrameworkDescription</c>.</param>
/// <param name="Wsdl">
/// The facade's WSDL publication status: <c>present</c> when it loaded
/// <c>contracts/wsdl/ErewhonWebService.wsdl</c> and serves it, <c>absent</c> when it forwards WSDL requests
/// to its default target instead.
/// </param>
public sealed record AdminInfo(
    string? Source,
    string? SeedSha256,
    string? ReferenceDate,
    long? Generation,
    long? Mutations,
    string? FrameworkDescription,
    string? Wsdl)
{
    private const string SourceMember = "source";
    private const string SeedSha256Member = "seedSha256";
    private const string ReferenceDateMember = "referenceDate";
    private const string GenerationMember = "generation";
    private const string MutationsMember = "mutations";
    private const string FrameworkDescriptionMember = "frameworkDescription";
    private const string WsdlMember = "wsdl";

    // The stand-in's /admin/info names the same value "framework", the facade's "frameworkDescription";
    // reading both lets either component's info supply it.
    private const string FrameworkMember = "framework";

    /// <summary>
    /// Reads an <c>/admin/info</c> body tolerantly: missing members become null, unknown members (the
    /// facade's component name and routing table) are ignored, integers may also be sent as strings, and
    /// each member is looked up by its exact camel-case name first, then case-insensitively.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <returns>
    /// The parsed values, or null when the body is not JSON, its root is not an object, or it holds a string
    /// that cannot be decoded: a value the reader decodes, or a member name, holding an unpaired surrogate
    /// escape such as <c>"\uD800"</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    public static AdminInfo? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json.TrimStart('\uFEFF'));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new AdminInfo(
                ReadString(root, SourceMember),
                ReadString(root, SeedSha256Member),
                ReadDateText(root, ReferenceDateMember),
                ReadInt64(root, GenerationMember),
                ReadInt64(root, MutationsMember),
                ReadString(root, FrameworkDescriptionMember) ?? ReadString(root, FrameworkMember),
                ReadString(root, WsdlMember));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
        {
            // A member value or member name holding an unpaired surrogate escape such as \uD800 parses, but
            // throws when it is decoded, so the info cannot be read as a whole. A disposed document would be
            // a defect here rather than a bad body, so it still throws.
            return null;
        }
    }

    private static bool TryGetMember(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var member in root.EnumerateObject())
        {
            if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = member.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement root, string name) =>
        TryGetMember(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // A date sent as a bare number or literal is kept as its JSON text, so a wrongly typed value is
    // reported as observed rather than as missing.
    private static string? ReadDateText(JsonElement root, string name)
    {
        if (!TryGetMember(root, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText().Trim('"'),
            _ => null,
        };
    }

    private static long? ReadInt64(JsonElement root, string name)
    {
        if (!TryGetMember(root, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}

/// <summary>
/// The seed facts every reset is confirmed against, alongside the generation, mutation count and source.
/// A null member is not checked. Both apply after a reset to <see cref="AdminRoutes.ResetStateEmpty"/> as
/// well as to <see cref="AdminRoutes.ResetStateSeed"/>, because the stand-in fingerprints its seed
/// directory and reports its reference date whichever state it builds.
/// </summary>
/// <remarks>Which factory applies is the caller's choice: <see cref="FirstCapture(HarnessOptions)"/> when no
/// <c>baseline/manifest.json</c> exists, <see cref="FromManifest"/> for a re-capture or a replay, and
/// <see cref="None"/> for a manifest recording <c>captureExecution: "not-run"</c>, which holds no seed
/// facts, so only the generation, mutation count and source are checked.</remarks>
/// <param name="SeedSha256">The expected <c>seedSha256</c>; null when not checked.</param>
/// <param name="ReferenceDate">The expected <c>referenceDate</c>; null when not checked.</param>
public sealed record ResetExpectations(string? SeedSha256, string? ReferenceDate)
{
    /// <summary>
    /// The expected seed hash when the harness could not fingerprint its seed directory. No stand-in
    /// reports it, so the check records a <c>seedSha256</c> mismatch instead of silently passing.
    /// </summary>
    internal const string UnavailableSeedSha256 = "unavailable";

    /// <summary>
    /// The expected reference date when <see cref="HarnessOptions.ExpectedReferenceDate"/> is not a
    /// <c>yyyy-MM-dd</c> date. The check records a <c>referenceDate</c> mismatch for it whatever the
    /// stand-in reports, instead of silently passing or comparing against a malformed setting.
    /// </summary>
    internal const string InvalidExpectedReferenceDate = "invalid";

    /// <summary>The configuration key of <see cref="HarnessOptions.ExpectedReferenceDate"/>, as diagnostics name it.</summary>
    internal const string ExpectedReferenceDateKey =
        HarnessOptions.SectionName + ":" + nameof(HarnessOptions.ExpectedReferenceDate);

    private const string ReferenceDateFormat = "yyyy-MM-dd";

    /// <summary>No seed facts: only generation, mutations and source are checked.</summary>
    public static ResetExpectations None { get; } = new(null, null);

    /// <summary>
    /// The expectations of a first capture: the shared seed fingerprint of
    /// <see cref="HarnessOptions.EffectiveSeedDirectory"/>, which holds the files the stand-in loads, and
    /// <see cref="HarnessOptions.EffectiveExpectedReferenceDate"/>.
    /// </summary>
    /// <remarks>Settings problems never throw; each prints one <c>[jade-standin]</c> console line. A blank
    /// reference date expects the default <c>2026-01-01</c>. A seed directory that cannot be fingerprinted,
    /// or a reference date that is not a <c>yyyy-MM-dd</c> date, becomes an expectation no stand-in
    /// meets, so every reset records a <c>seedSha256</c> or <c>referenceDate</c> mismatch.</remarks>
    /// <param name="options">The harness settings.</param>
    /// <returns>The expectations; both members are always set, so both facts are always checked.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static ResetExpectations FirstCapture(HarnessOptions options) => FirstCapture(options, Console.Out);

    /// <summary>
    /// <see cref="FirstCapture(HarnessOptions)"/> with its console lines written to <paramref name="output"/>.
    /// </summary>
    /// <param name="options">The harness settings.</param>
    /// <param name="output">Where console lines go, for example a test's writer.</param>
    /// <returns>The expectations; both members are always set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="output"/> is null.</exception>
    internal static ResetExpectations FirstCapture(HarnessOptions options, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        var directory = options.SeedDirectory;
        string seedSha256;
        try
        {
            directory = options.EffectiveSeedDirectory;
            seedSha256 = SeedFingerprint.ComputeSha256Hex(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or SecurityException)
        {
            LifecycleConsole.WriteLine(
                output,
                $"{SourceLabels.ConsolePrefix} seed fingerprint of '{directory}' unavailable: {ex.GetType().Name}");
            seedSha256 = UnavailableSeedSha256;
        }

        return new ResetExpectations(seedSha256, ResolveExpectedReferenceDate(options, output));
    }

    /// <summary>The expectations recorded in <c>baseline/manifest.json</c>.</summary>
    /// <param name="seedSha256">The manifest's <c>seedSha256</c>; blank is not checked.</param>
    /// <param name="referenceDate">The manifest's <c>referenceDate</c>; blank is not checked.</param>
    /// <returns>The expectations.</returns>
    public static ResetExpectations FromManifest(string? seedSha256, string? referenceDate) =>
        new(NullIfBlank(seedSha256), NullIfBlank(referenceDate));

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // A blank setting falls back to the default; any other value must be exactly yyyy-MM-dd, so a
    // lenient parse can never turn a malformed setting into a date that happens to match.
    private static string ResolveExpectedReferenceDate(HarnessOptions options, TextWriter output)
    {
        var date = options.EffectiveExpectedReferenceDate;
        if (string.IsNullOrWhiteSpace(options.ExpectedReferenceDate))
        {
            LifecycleConsole.WriteLine(
                output,
                $"{SourceLabels.ConsolePrefix} {ExpectedReferenceDateKey} is blank; expecting the default reference date {date}");
            return date;
        }

        if (DateOnly.TryParseExact(date, ReferenceDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return date;
        }

        LifecycleConsole.WriteLine(
            output,
            $"{SourceLabels.ConsolePrefix} {ExpectedReferenceDateKey} '{date}' is not a {ReferenceDateFormat} date; "
            + "every reset records a referenceDate mismatch");
        return InvalidExpectedReferenceDate;
    }
}

/// <summary>The outcome of one state reset and its confirmation.</summary>
/// <param name="Failures">
/// One <c>CHK-RESET:&lt;point&gt;</c> item per mismatch, in check order; empty when the reset was
/// confirmed or skipped.
/// </param>
/// <param name="After">The <c>/admin/info</c> values read after the reset; null when unavailable or skipped.</param>
public sealed record StateResetResult(IReadOnlyList<FailureItem> Failures, AdminInfo? After)
{
    /// <summary>
    /// The result of a reset that was not attempted because the backend has no admin URL. The caller
    /// records that substitution (KL-SUB-NORESET) through <see cref="HarnessOptions.IsExternalBackend"/>.
    /// </summary>
    public static StateResetResult Skipped { get; } = new(Array.Empty<FailureItem>(), null);

    /// <summary>True when no mismatch was recorded.</summary>
    public bool Succeeded => Failures.Count == 0;
}

/// <summary>
/// The names of the points in a run where the harness resets the stand-in; each becomes the
/// <c>&lt;point&gt;</c> of a <c>CHK-RESET:&lt;point&gt;</c> item, so the register can document it.
/// </summary>
public static class ResetPoints
{
    /// <summary>The seed reset before capture runs any case.</summary>
    public const string CaptureStart = "capture-start";

    /// <summary>The seed reset before replay runs any case.</summary>
    public const string ReplayStart = "replay-start";

    /// <summary>The reset before a case with a non-seed pre-state, such as the <c>getCompany</c> empty-state case.</summary>
    /// <param name="caseId">The case id from the case matrix.</param>
    /// <exception cref="ArgumentException"><paramref name="caseId"/> is null or blank.</exception>
    public static string Before(string caseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        return $"before:{caseId}";
    }

    /// <summary>The seed reset that restores the state after such a case.</summary>
    /// <param name="caseId">The case id from the case matrix.</param>
    /// <exception cref="ArgumentException"><paramref name="caseId"/> is null or blank.</exception>
    public static string After(string caseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        return $"after:{caseId}";
    }
}

/// <summary>
/// Resets the stand-in through <c>POST /admin/reset</c> on its admin listener and confirms the new state
/// through <c>GET /admin/info</c>: the generation must have increased, the mutation count must be 0, the
/// source must be <see cref="SourceLabels.StandIn"/>, and the seed fingerprint and reference date must
/// equal the expectations, after a seed and an empty reset alike. Also reads any component's
/// <c>/admin/info</c> tolerantly.
/// </summary>
/// <remarks>
/// <para>Backend state is reached only through these admin endpoints. They are unauthenticated, so no
/// credential or extra header is sent, and an admin URL carrying user information is rejected before any
/// request.</para>
/// <para>Every mismatch becomes a <c>CHK-RESET:&lt;point&gt;</c> item with nature
/// <c>mismatch:&lt;field&gt;:&lt;observed&gt;</c> and the run continues. Such items can be documented, so
/// the observed part is always a deterministic token (a status code, <c>connect</c>, <c>timeout</c>,
/// <c>reset</c>, <c>oversized</c>, <c>before-</c> followed by the pre-reset read's token or <c>null</c> when
/// the generation the reset had to increase is unknown, or the raw observed value); timings, URLs and
/// exception messages go into <see cref="FailureItem.Detail"/> only. Network conditions never throw; only the
/// caller's cancellation does.</para>
/// <para>Each request is bounded by <see cref="HarnessOptions.RequestTimeout"/>, and each 2xx body by a
/// mebibyte, far above any admin answer: a body over it is the failure <c>oversized</c>, never read in full
/// or truncated. The instance is safe for concurrent use.</para>
/// </remarks>
public sealed class StateReset : IDisposable
{
    // The mismatch fields and observed tokens are internal so FailureItemDisplay decides from the same
    // spellings which observed values a console line may show.
    internal const string HttpField = "http";
    internal const string GenerationField = "generation";
    internal const string MutationsField = "mutations";
    internal const string SourceField = "source";
    internal const string SeedSha256Field = "seedSha256";
    internal const string ReferenceDateField = "referenceDate";

    internal const string ConnectFailure = "connect";
    internal const string TimeoutFailure = "timeout";
    internal const string ResetFailure = "reset";
    internal const string InvalidBodyFailure = "invalid";
    internal const string OversizedBodyFailure = "oversized";
    internal const string InvalidUrlFailure = "invalid-url";
    internal const string BlankUrlFailure = "blank-url";
    internal const string InfoFailurePrefix = "info-";
    internal const string BeforeFailurePrefix = "before-";
    internal const string NullObserved = "null";

    private const string DateFormat = "yyyy-MM-dd";

    // The most bytes one admin response body may hold. Admin answers are JSON objects of a few hundred bytes,
    // so a body over a mebibyte is no admin answer and is refused, never read in full or truncated.
    private const int AdminBodyByteLimit = 1024 * 1024;

    // The first buffer of an admin body read, which holds every admin answer whole.
    private const int AdminBodyInitialBytes = 4 * 1024;

    private readonly HarnessOptions _options;
    private readonly HttpClient _client;
    private readonly TextWriter _output;

    /// <summary>Creates a reset client over its own HTTP handler.</summary>
    /// <param name="options">The harness settings; <see cref="HarnessOptions.Backend"/> names the stand-in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public StateReset(HarnessOptions options)
        : this(options, output: null)
    {
    }

    /// <summary>
    /// Creates a reset client over its own HTTP handler that writes its console lines to <paramref name="output"/>.
    /// </summary>
    /// <param name="options">The harness settings; <see cref="HarnessOptions.Backend"/> names the stand-in.</param>
    /// <param name="output">Where console lines go, for example the mode's writer; null means <see cref="Console.Out"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    internal StateReset(HarnessOptions options, TextWriter? output)
        : this(options ?? throw new ArgumentNullException(nameof(options)), CreateHandler(), output)
    {
    }

    /// <summary>Creates a reset client over <paramref name="handler"/>, which it disposes.</summary>
    /// <param name="options">The harness settings.</param>
    /// <param name="handler">The HTTP handler, for example a test stub.</param>
    /// <param name="output">Where console lines go; null means <see cref="Console.Out"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="handler"/> is null.</exception>
    internal StateReset(HarnessOptions options, HttpMessageHandler handler, TextWriter? output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);

        _options = options;
        _output = output ?? Console.Out;

        // Each request carries its own deadline through a linked token, so the client-wide timeout is off.
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Reads a component's <c>GET /admin/info</c>: the stand-in's state facts, or the facade's framework
    /// description for the report's run facts.
    /// </summary>
    /// <param name="adminUrl">The component's admin base URL, for example <c>http://facade:9090</c>.</param>
    /// <param name="ct">Cancels the read; its cancellation is the only exception thrown.</param>
    /// <returns>
    /// The parsed info, or null when the URL is blank, is not an absolute <c>http</c>/<c>https</c> URL
    /// or carries user information, the request fails, the status is not 2xx or the body is not a JSON
    /// object or holds a string that cannot be decoded.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<AdminInfo?> ReadInfoAsync(string? adminUrl, CancellationToken ct = default) =>
        (await ReadInfoOutcomeAsync(adminUrl, ct).ConfigureAwait(false)).Info;

    /// <summary>
    /// Reads a component's <c>GET /admin/info</c> like <see cref="ReadInfoAsync"/>, and also states why
    /// no info was read, so a caller can report what it observed.
    /// </summary>
    /// <param name="adminUrl">The component's admin base URL, for example <c>http://facade:9090</c>.</param>
    /// <param name="ct">Cancels the read; its cancellation is the only exception thrown.</param>
    /// <returns>
    /// The parsed info, or the failure token and a credential-free detail. A blank URL, or one that is not
    /// an absolute <c>http</c>/<c>https</c> URL without user information, sends no request; its detail
    /// never quotes the URL.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<AdminInfoRead> ReadInfoOutcomeAsync(string? adminUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adminUrl))
        {
            return new AdminInfoRead(null, BlankUrlFailure, "no admin URL is configured, so no request was sent");
        }

        if (!TryBuildUri(adminUrl, AdminRoutes.Info, out var infoUri, out var urlProblem))
        {
            return new AdminInfoRead(null, InvalidUrlFailure, $"no request was sent; {urlProblem}");
        }

        return await ReadInfoCoreAsync(infoUri, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets the stand-in to <paramref name="state"/> and confirms the result against
    /// <paramref name="expected"/>. Does nothing when <see cref="HarnessOptions.Backend"/> has no admin URL.
    /// </summary>
    /// <param name="point">The point in the run, from <see cref="ResetPoints"/>; it names the failure items.</param>
    /// <param name="state"><see cref="AdminRoutes.ResetStateSeed"/> or <see cref="AdminRoutes.ResetStateEmpty"/>.</param>
    /// <param name="expected">
    /// The seed facts to confirm, after either reset state; <see cref="ResetExpectations.None"/> confirms none.
    /// </param>
    /// <param name="ct">Cancels the reset; its cancellation is the only exception thrown for I/O.</param>
    /// <returns>
    /// Every mismatch, and the info read after the reset. A reset request that fails yields the single
    /// item <c>mismatch:http:&lt;observed&gt;</c> and no field checks, because a reset that did not
    /// happen cannot be confirmed. The generation must exceed the one read before the reset; when that
    /// read failed or reported no generation, the reset and the other checks still run and the
    /// unproven increase is <c>mismatch:generation:before-&lt;token&gt;</c>, for example
    /// <c>before-503</c>, <c>before-connect</c> or <c>before-null</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="point"/> is blank, or <paramref name="state"/> is not a reset state.
    /// </exception>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<StateResetResult> ResetAsync(
        string point,
        string state,
        ResetExpectations expected,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(point);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(expected);
        if (!string.Equals(state, AdminRoutes.ResetStateSeed, StringComparison.Ordinal)
            && !string.Equals(state, AdminRoutes.ResetStateEmpty, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The reset state must be '{AdminRoutes.ResetStateSeed}' or '{AdminRoutes.ResetStateEmpty}'.",
                nameof(state));
        }

        var adminUrl = _options.Backend.AdminUrl;
        if (string.IsNullOrWhiteSpace(adminUrl))
        {
            LifecycleConsole.WriteLine(_output, $"{SourceLabels.ConsolePrefix} reset {state} at {point}: skipped (no backend admin URL)");
            return StateResetResult.Skipped;
        }

        var failures = new List<FailureItem>();

        // An admin URL that is not an absolute http(s) URL without user information can never be reset;
        // its own token keeps it apart from a stand-in that is merely down. The detail gives the reason
        // and never quotes the setting, which may carry a password.
        var resetPath = $"{AdminRoutes.Reset}?{AdminRoutes.ResetStateQueryKey}={Uri.EscapeDataString(state)}";
        if (!TryBuildUri(adminUrl, AdminRoutes.Info, out var infoUri, out var urlProblem)
            || !TryBuildUri(adminUrl, resetPath, out var resetUri, out urlProblem))
        {
            AddMismatch(
                failures, point, state, HttpField, InvalidUrlFailure, $"expected an absolute http(s) admin URL without user information; {urlProblem}");
            return new StateResetResult(failures.AsReadOnly(), null);
        }

        // One bounded read of the generation the reset must increase. When it fails or reports no
        // generation, the reset still runs, because the run needs it, but the increase stays unproven
        // and CheckFields records it under this read's failure token.
        var before = await ReadInfoCoreAsync(infoUri, ct).ConfigureAwait(false);

        var reset = await SendAsync(HttpMethod.Post, resetUri, ct).ConfigureAwait(false);
        if (reset.Failure is not null)
        {
            AddMismatch(failures, point, state, HttpField, reset.Failure, $"expected a 2xx reset response; {reset.Detail}");
            var bestEffort = await ReadInfoCoreAsync(infoUri, ct).ConfigureAwait(false);
            return new StateResetResult(failures.AsReadOnly(), bestEffort.Info);
        }

        var read = await ReadInfoCoreAsync(infoUri, ct).ConfigureAwait(false);
        if (read.Info is not { } after)
        {
            AddMismatch(
                failures, point, state, HttpField, InfoFailurePrefix + read.Failure, $"expected a 2xx JSON object from {AdminRoutes.Info}; {read.Detail}");
            return new StateResetResult(failures.AsReadOnly(), null);
        }

        CheckFields(failures, point, state, expected, before, after);

        if (failures.Count == 0)
        {
            LifecycleConsole.WriteLine(_output, $"{SourceLabels.ConsolePrefix} reset {state} at {point}: generation {after.Generation}");
        }

        return new StateResetResult(failures.AsReadOnly(), after);
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Confirms the info read after a reset, adding at most one item per field. The generation passes
    /// only when both reads report one and the later is greater. Otherwise it is observed as
    /// <c>null</c> when the read after the reset has none; as <c>before-&lt;token&gt;</c> when the
    /// prior generation is unknown, where the token is the pre-reset read's failure token
    /// (<c>connect</c>, <c>timeout</c>, <c>reset</c>, the status code, <c>oversized</c> or <c>invalid</c>) or <c>null</c>
    /// when that read reported no generation; and as itself when it did not increase.
    /// </summary>
    private void CheckFields(
        List<FailureItem> failures,
        string point,
        string state,
        ResetExpectations expected,
        AdminInfoRead before,
        AdminInfo after)
    {
        var previousGeneration = before.Info?.Generation;
        if (after.Generation is not { } generation)
        {
            AddMismatch(
                failures,
                point,
                state,
                GenerationField,
                NullObserved,
                previousGeneration is { } known ? $"expected a generation greater than {known}" : "expected a generation");
        }
        else if (previousGeneration is not { } previous)
        {
            // A generation present after the reset proves nothing on its own: a reset that changed
            // nothing reports one too.
            var cause = before.Failure is null
                ? $"its {AdminRoutes.Info} reported no generation"
                : $"its {AdminRoutes.Info} read failed: {before.Detail}";
            AddMismatch(
                failures,
                point,
                state,
                GenerationField,
                BeforeFailurePrefix + (before.Failure ?? NullObserved),
                $"expected a generation increase, unproven because the pre-reset generation is unavailable ({cause}); "
                + $"the generation read after the reset is {generation}");
        }
        else if (generation <= previous)
        {
            AddMismatch(failures, point, state, GenerationField, Text(generation), $"expected a generation greater than {previous}");
        }

        if (after.Mutations != 0)
        {
            AddMismatch(failures, point, state, MutationsField, Text(after.Mutations), "expected 0");
        }

        if (!string.Equals(after.Source, SourceLabels.StandIn, StringComparison.Ordinal))
        {
            AddMismatch(failures, point, state, SourceField, after.Source ?? NullObserved, $"expected {SourceLabels.StandIn}");
        }

        if (expected.SeedSha256 is { } expectedSeed
            && !string.Equals(after.SeedSha256?.Trim(), expectedSeed.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            AddMismatch(failures, point, state, SeedSha256Field, after.SeedSha256 ?? NullObserved, $"expected {expectedSeed}");
        }

        if (string.Equals(expected.ReferenceDate, ResetExpectations.InvalidExpectedReferenceDate, StringComparison.Ordinal))
        {
            AddMismatch(
                failures,
                point,
                state,
                ReferenceDateField,
                after.ReferenceDate ?? NullObserved,
                $"{ResetExpectations.ExpectedReferenceDateKey} is not a {DateFormat} date, so the reference date cannot be confirmed");
        }
        else if (expected.ReferenceDate is { } expectedDate && !DatesMatch(expectedDate, after.ReferenceDate))
        {
            var detail = JadeWireFormats.IsXsdDate(expectedDate)
                ? $"expected {expectedDate}"
                : $"expected {expectedDate}, which is not a {DateFormat} date, so the reference date cannot be confirmed";
            AddMismatch(failures, point, state, ReferenceDateField, after.ReferenceDate ?? NullObserved, detail);
        }
    }

    private void AddMismatch(
        List<FailureItem> failures,
        string point,
        string state,
        string field,
        string observed,
        string detail)
    {
        // The item keeps the exact value; the console line shows only what FailureItemDisplay allows, because
        // a wrong backend can put personal data into any field it reports.
        failures.Add(new FailureItem(FailureItemIds.Reset(point), FailureItemIds.Mismatch(field, observed), Detail: detail));
        LifecycleConsole.WriteLine(
            _output,
            $"{SourceLabels.ConsolePrefix} reset {state} at {point}: mismatch {field} observed {FailureItemDisplay.Observed(field, observed)}");
    }

    private async Task<AdminInfoRead> ReadInfoCoreAsync(Uri infoUri, CancellationToken ct)
    {
        var call = await SendAsync(HttpMethod.Get, infoUri, ct).ConfigureAwait(false);
        if (call.Failure is not null)
        {
            return new AdminInfoRead(null, call.Failure, call.Detail);
        }

        var info = AdminInfo.Parse(call.Body ?? string.Empty);
        return info is null
            ? new AdminInfoRead(null, InvalidBodyFailure, $"GET {infoUri}: body is not a JSON object whose strings can be decoded")
            : new AdminInfoRead(info, null, null);
    }

    /// <summary>
    /// Sends one admin request with no body and no extra header, bounded by the request limit and its 2xx
    /// body by the admin body limit, and classifies every way it can fail into a deterministic token.
    /// </summary>
    private async Task<AdminCall> SendAsync(HttpMethod method, Uri uri, CancellationToken ct)
    {
        var limit = _options.RequestTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);

        try
        {
            // Only the headers are awaited, so the status decides before any body byte is read, and the body
            // that is read is bounded.
            using var request = new HttpRequestMessage(method, uri);
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status is < 200 or > 299)
            {
                return new AdminCall(null, status.ToString(CultureInfo.InvariantCulture), $"{method} {uri}: HTTP {status}");
            }

            // Both admin listeners write UTF-8 JSON; decoding the bytes directly cannot fail on an
            // unexpected charset parameter the way ReadAsStringAsync can.
            var bytes = await ReadBoundedBodyAsync(response.Content, deadline.Token).ConfigureAwait(false);
            return bytes is null
                ? new AdminCall(
                    null,
                    OversizedBodyFailure,
                    string.Create(CultureInfo.InvariantCulture, $"{method} {uri}: body exceeds the {AdminBodyByteLimit}-byte admin limit"))
                : new AdminCall(Encoding.UTF8.GetString(bytes), null, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AdminCall(null, TimeoutFailure, $"{method} {uri}: no response within {limit.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            ct.ThrowIfCancellationRequested();
            return new AdminCall(null, Classify(ex), Describe(method, uri, ex));
        }
        catch (IOException ex)
        {
            ct.ThrowIfCancellationRequested();
            return new AdminCall(null, ResetFailure, Describe(method, uri, ex));
        }
    }

    // The whole admin body, or null when it exceeds AdminBodyByteLimit: a declared Content-Length over the
    // limit is refused before any byte is read, and otherwise at most the limit plus one byte is read, into a
    // buffer that starts small and grows by doubling, so a longer body is told apart without reading the rest
    // of it. A failure while reading is classified by the caller exactly as one while sending.
    private static async Task<byte[]?> ReadBoundedBodyAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > AdminBodyByteLimit)
        {
            return null;
        }

        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[AdminBodyInitialBytes];
            var length = 0;
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length > AdminBodyByteLimit)
                    {
                        return null;
                    }

                    Array.Resize(ref buffer, Math.Min(2 * buffer.Length, AdminBodyByteLimit + 1));
                }

                var read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer[..length];
                }

                length += read;
            }
        }
    }

    private static string Classify(HttpRequestException ex)
    {
        if (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            return ConnectFailure;
        }

        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException
                {
                    SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostNotFound
                        or SocketError.HostUnreachable or SocketError.NetworkUnreachable,
                })
            {
                return ConnectFailure;
            }
        }

        return ResetFailure;
    }

    private static string Describe(HttpMethod method, Uri uri, Exception ex) =>
        $"{method} {uri}: {ex.GetType().Name}: {ex.Message}";

    // The base keeps its path and the admin route is appended to it. The base and the result must both be
    // absolute http(s) URLs without user information, so no admin request and no detail built from the
    // result carries a credential; problem never quotes the base.
    private static bool TryBuildUri(
        string baseUrl,
        string pathAndQuery,
        [NotNullWhen(true)] out Uri? uri,
        out string problem)
    {
        if (!CaseRunner.TryParseHttpUrl(baseUrl, out _, out problem))
        {
            uri = null;
            return false;
        }

        return CaseRunner.TryParseHttpUrl(baseUrl.Trim().TrimEnd('/') + pathAndQuery, out uri, out problem);
    }

    // Both values must be xsd:dates as the shared wire helper defines them, and name the same calendar
    // date. The stand-in writes yyyy-MM-dd; an xsd:date timezone does not change the date, so the result
    // never depends on the host's timezone. Surrounding whitespace, unpadded parts, impossible dates,
    // dateTimes and other forms never match, and a null or unparsable value on either side is a mismatch:
    // the texts are never compared as strings.
    private static bool DatesMatch(string expected, string? observed) =>
        JadeWireFormats.TryParseXsdDate(expected, out var expectedDate)
        && JadeWireFormats.TryParseXsdDate(observed, out var observedDate)
        && expectedDate == observedDate;

    private static string Text(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? NullObserved;

    private static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
    };

    /// <summary>One admin request's outcome: the body on a 2xx response, otherwise the failure token.</summary>
    private readonly record struct AdminCall(string? Body, string? Failure, string? Detail);
}

/// <summary>One <c>GET /admin/info</c> read: the parsed info, otherwise why none was read.</summary>
/// <param name="Info">The parsed info; null when the read failed.</param>
/// <param name="Failure">
/// Null when <paramref name="Info"/> is set. Otherwise a deterministic token: <c>blank-url</c> or
/// <c>invalid-url</c> when no request was sent; <c>connect</c>, <c>timeout</c> or <c>reset</c> when the
/// request got no complete response; the status code of a response that is not 2xx; <c>oversized</c> when the
/// 2xx body exceeds the admin body limit of a mebibyte; or <c>invalid</c> when the 2xx body is not a JSON
/// object whose strings can be decoded.
/// </param>
/// <param name="Detail">
/// Why the read failed, for the console: the method, the credential-free URL and what went wrong, never the
/// response body; null when <paramref name="Info"/> is set.
/// </param>
public readonly record struct AdminInfoRead(AdminInfo? Info, string? Failure, string? Detail);

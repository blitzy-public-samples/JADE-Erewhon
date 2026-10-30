using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Erewhon.Contracts.Admin;
using Erewhon.Harness.Checks;
using Erewhon.Harness.Http;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Lifecycle;

/// <summary>
/// Waits until a component's admin listener answers <c>GET /health</c> with 200, polling once per
/// <see cref="HarnessOptions.ReadinessPollInterval"/> up to <see cref="HarnessOptions.ReadinessTimeout"/>.
/// A component that never becomes ready is reported as a <c>CHK-READY:&lt;component&gt;</c>
/// <c>timeout</c> item and the run continues to the report.
/// </summary>
/// <remarks>
/// <para>Readiness is established only here: the <c>aspnet</c> runtime images carry no <c>curl</c>, so
/// Compose cannot health-check the facade or the stand-in.</para>
/// <para>Components are reached only through their admin URL; the probe sends no credential and no
/// header of its own, because the admin listeners are unauthenticated and contained by network
/// placement. An admin URL carrying user information is therefore rejected before any probe.</para>
/// <para>Network conditions and caller cancellation never throw: they end in the timeout item, so
/// replay's report and shutdown steps always run. Instances are thread-safe, so one instance can wait on
/// the facade and the stand-in concurrently. One line is printed per wait, never one per probe, through
/// <see cref="LifecycleConsole"/>, so a console that cannot take it never changes the result. The line
/// starts with the prefix the caller gave, so capture's readiness lines carry <c>[jade-standin]</c> like
/// every other line of that mode, while replay and <c>export-openapi</c> print theirs unprefixed.</para>
/// </remarks>
/// <param name="options">The harness settings that own the readiness limit and poll interval.</param>
/// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
public sealed class Readiness(HarnessOptions options) : IDisposable
{
    private static readonly TimeSpan MinProbeTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly HarnessOptions options = options ?? throw new ArgumentNullException(nameof(options));

    // The first initializer has already rejected a null options, so the parameter is non-null here.
    private readonly HttpClient client = CreateClient(ProbeTimeoutFor(options.ReadinessPollInterval));

    // Null writes to Console.Out as it stands when the line is printed.
    private readonly TextWriter? output;

    // Starts every line this instance prints; empty for replay and export-openapi.
    private readonly string linePrefix = "";

    private volatile bool disposed;

    /// <summary>
    /// Creates a readiness client whose console line goes to <paramref name="output"/>, starting with
    /// <paramref name="linePrefix"/>.
    /// </summary>
    /// <param name="options">The harness settings that own the readiness limit and poll interval.</param>
    /// <param name="output">Where the line of each wait goes, for example a test's writer; null means <see cref="Console.Out"/>.</param>
    /// <param name="linePrefix">
    /// Text that starts every line, including any separator, for example capture's
    /// <c>"[jade-standin] "</c>; null or empty prints the lines unprefixed. It is escaped with the line.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    internal Readiness(HarnessOptions options, TextWriter? output, string? linePrefix = null)
        : this(options)
    {
        this.output = output;
        this.linePrefix = linePrefix ?? "";
    }

    private TextWriter Output => output ?? Console.Out;

    /// <summary>
    /// Polls <c>GET &lt;adminUrl&gt;/health</c> until it answers 200, the readiness limit passes or
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="component">The component label, <c>facade</c> or <c>standin</c>.</param>
    /// <param name="adminUrl">
    /// The component's admin base URL, for example <c>http://standin:9090</c>. Blank for an external
    /// backend, which has no admin listener.
    /// </param>
    /// <param name="ct">Stops polling early; the component then counts as not ready.</param>
    /// <returns>
    /// Null when the component answered 200, or when <paramref name="adminUrl"/> is blank and nothing
    /// was probed. Otherwise <c>CHK-READY:&lt;component&gt;</c> with nature <c>timeout</c>: the limit
    /// passed, the caller cancelled, or <paramref name="adminUrl"/> is not an absolute
    /// <c>http</c>/<c>https</c> URL without user information. The detail of a rejected URL gives the
    /// reason without quoting the setting.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="component"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    public async Task<FailureItem?> WaitAsync(string component, string? adminUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ObjectDisposedException.ThrowIf(disposed, this);

        // An external backend has no admin listener; the caller records its substitution separately.
        if (string.IsNullOrWhiteSpace(adminUrl))
        {
            return null;
        }

        // The setting is never quoted: a rejected URL may carry a password.
        if (!TryCreateProbeUri(adminUrl, out var probeUri, out var problem))
        {
            return NotReady(component, $"invalid admin URL: {problem}", limit: null);
        }

        var limit = options.ReadinessTimeout;
        var interval = options.ReadinessPollInterval;
        var probeTimeout = ProbeTimeoutFor(interval);
        var watch = Stopwatch.StartNew();
        var last = "no probe completed";

        FailureItem Expired() => NotReady(
            component,
            string.Create(CultureInfo.InvariantCulture, $"no 200 from {probeUri} within {limit.TotalSeconds:0} s; last: {last}"),
            limit);

        while (!ct.IsCancellationRequested)
        {
            var probeStarted = watch.Elapsed;
            var remaining = limit - probeStarted;
            if (remaining <= TimeSpan.Zero)
            {
                return Expired();
            }

            var outcome = await ProbeAsync(probeUri, probeTimeout < remaining ? probeTimeout : remaining, ct);
            if (outcome.Ready)
            {
                LifecycleConsole.WriteLine(
                    Output,
                    string.Create(CultureInfo.InvariantCulture, $"{linePrefix}ready: {component} ({watch.ElapsedMilliseconds} ms)"));
                return null;
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            last = outcome.Observation;

            // Sleep out the rest of the interval. A next probe that would get less than the minimum probe
            // time before the deadline is not started: it could only end in its own cut-off, and that
            // would replace the last real observation in the detail.
            var now = watch.Elapsed;
            var untilDeadline = limit - now;
            var pause = interval - (now - probeStarted);
            var finalPoll = untilDeadline - pause < MinProbeTimeout;
            if (finalPoll)
            {
                pause = untilDeadline;
            }

            if (pause > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(pause, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }

            if (finalPoll)
            {
                return Expired();
            }
        }

        var cancelled = string.Create(
            CultureInfo.InvariantCulture,
            $"cancelled after {watch.ElapsedMilliseconds} ms; last: {last}");
        return NotReady(component, cancelled, limit: null);
    }

    /// <summary>Releases the probe client.</summary>
    public void Dispose()
    {
        disposed = true;
        client.Dispose();
    }

    // One probe, bounded by its own linked token rather than HttpClient.Timeout, so a probe timeout
    // (caller token not cancelled) stays distinguishable from caller cancellation.
    private async Task<ProbeOutcome> ProbeAsync(Uri probeUri, TimeSpan timeout, CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUri);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                probeCts.Token);
            return response.StatusCode == HttpStatusCode.OK
                ? new ProbeOutcome(true, "status 200")
                : new ProbeOutcome(false, string.Create(CultureInfo.InvariantCulture, $"status {(int)response.StatusCode}"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var observation = string.Create(
                CultureInfo.InvariantCulture,
                $"no answer within {timeout.TotalMilliseconds:0} ms");
            return new ProbeOutcome(false, observation);
        }
        catch (OperationCanceledException)
        {
            return new ProbeOutcome(false, "cancelled by the caller");
        }
        catch (HttpRequestException ex)
        {
            return new ProbeOutcome(false, $"{nameof(HttpRequestException)} ({ex.HttpRequestError}): {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return new ProbeOutcome(false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private FailureItem NotReady(string component, string detail, TimeSpan? limit)
    {
        var item = new FailureItem(FailureItemIds.Ready(component), FailureItemIds.Timeout, Detail: detail);
        LifecycleConsole.WriteLine(
            Output,
            limit is { } l
                ? string.Create(CultureInfo.InvariantCulture, $"{linePrefix}not ready: {component} after {l.TotalSeconds:0} s ({detail})")
                : $"{linePrefix}not ready: {component} ({detail})");
        return item;
    }

    // The admin URL is an opaque base whose path is kept, and /health is appended to it. Only an absolute
    // http or https URL without user information can be probed, so any other scheme (including a
    // host:port that parses as a scheme) or a URL carrying credentials counts as invalid rather than
    // throwing. The probe URL is checked under the same rule, so a timeout detail never holds credentials.
    private static bool TryCreateProbeUri(
        string adminUrl,
        [NotNullWhen(true)] out Uri? probeUri,
        out string problem)
    {
        if (!CaseRunner.TryParseHttpUrl(adminUrl, out _, out problem))
        {
            probeUri = null;
            return false;
        }

        return CaseRunner.TryParseHttpUrl(adminUrl.Trim().TrimEnd('/') + AdminRoutes.Health, out probeUri, out problem);
    }

    // min(interval, 5 s), but at least 500 ms; the caller further caps it at the time left.
    private static TimeSpan ProbeTimeoutFor(TimeSpan interval)
    {
        var timeout = interval < MaxProbeTimeout ? interval : MaxProbeTimeout;
        return timeout < MinProbeTimeout ? MinProbeTimeout : timeout;
    }

    // No proxy, redirects, cookies or decompression: the probe must reach the admin listener itself, and
    // a 3xx or a proxy's answer is not the component saying it is ready.
    private static HttpClient CreateClient(TimeSpan connectTimeout) =>
        new(
            new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = connectTimeout,
            },
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private readonly record struct ProbeOutcome(bool Ready, string Observation);
}

/// <summary>
/// The one console sink of the lifecycle clients (readiness, state reset and shutdown): every line is
/// made printable by <see cref="Display"/> and written best-effort.
/// </summary>
/// <remarks>
/// <para>A line is written only after the readiness item, reset result or shutdown outcome it reports
/// has been computed, so a console that cannot take it never replaces that result.</para>
/// <para>Lines quote values the harness does not control: admin responses, settings, URLs and
/// exception messages. Escaping them keeps each call to exactly one console line, so such a value can
/// neither forge another <c>[jade-standin]</c> lifecycle line nor drive the terminal. Failure items
/// keep the exact values; only what is printed is escaped.</para>
/// <para><see cref="Display"/> also escapes the ordinary diagnostics that the entry point and the
/// <c>export-openapi</c> and <c>summary</c> modes write to sinks of their own, so each keeps its own
/// fallback behaviour.</para>
/// </remarks>
internal static class LifecycleConsole
{
    /// <summary>Writes <see cref="Display"/> of <paramref name="line"/> to <paramref name="output"/>, best-effort.</summary>
    /// <param name="output">The console writer.</param>
    /// <param name="line">The line, without its terminator.</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> or <paramref name="line"/> is null.</exception>
    internal static void WriteLine(TextWriter output, string line)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(line);

        var display = Display(line);
        try
        {
            output.WriteLine(display);
        }
        catch (Exception)
        {
            // Deliberately empty: the line only reports a result the caller has already computed, so a
            // closed, full or disposed writer (or any other fault a writer raises) drops the line rather
            // than replace that result. No other sink is tried, because none is safer than this one.
        }
    }

    /// <summary>
    /// Returns <paramref name="text"/> with every character that could break or disguise a console line
    /// written as <c>\uXXXX</c> (uppercase hexadecimal): control characters (U+0000 to U+001F and U+007F
    /// to U+009F, NEL included), the line and paragraph separators U+2028 and U+2029, every Unicode
    /// bidirectional control, that is the marks, embeddings, overrides and isolates (U+061C, U+200E,
    /// U+200F, U+202A to U+202E, U+2066 to U+2069), and every surrogate that is not half of a pair.
    /// Every other character, backslashes included, is kept unchanged.
    /// </summary>
    /// <param name="text">The text to print.</param>
    /// <returns>The printable text; <paramref name="text"/> itself when nothing needed escaping.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    internal static string Display(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder? display = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                display?.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (MustEscape(c))
            {
                display ??= new StringBuilder(text.Length + 16).Append(text, 0, i);
                display.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                display?.Append(c);
            }
        }

        return display?.ToString() ?? text;
    }

    // A surrogate reaching this test is unpaired: Display keeps whole pairs before asking.
    private static bool MustEscape(char c) =>
        char.IsControl(c)
        || char.IsSurrogate(c)
        || c is '\u2028' or '\u2029' or '\u061C' or '\u200E' or '\u200F'
            or (>= '\u202A' and <= '\u202E')
            or (>= '\u2066' and <= '\u2069');
}

/// <summary>
/// The console form of a failure nature. Three natures embed a value read from a response:
/// <c>differs:&lt;observed&gt;</c> holds a seed client's actual property,
/// <c>mismatch:&lt;field&gt;:&lt;observed&gt;</c> an <c>/admin/info</c> value and
/// <c>wrong-type:&lt;observed type&gt;</c> may hold the name of an XML element a SOAP response put in a
/// collection or the type name an <c>xsi:type</c> attribute gave, and any of them may be personal data. Ordinary console lines show natures through this
/// class, which replaces such a value with a marker; the failing item, register matching, the report and
/// every evidence file keep the exact nature.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>differs:&lt;observed&gt;</c> shows an empty value and the conformance check's own
///   <c>absent</c> token as they are; any other value becomes the marker.</item>
///   <item><c>mismatch:&lt;field&gt;:&lt;observed&gt;</c> shows its value through <see cref="Observed"/>. When
///   the field is not one the state reset confirms, everything after <c>mismatch:</c> becomes the marker.</item>
///   <item><c>wrong-type:&lt;observed type&gt;</c> is read with the conformance check's token grammar,
///   <c>token := base | "item:" token | "item-name:" &lt;XML item name&gt; | "xsi-type:unresolved" |
///   "xsi-type:" &lt;Clark type name&gt;</c>, where base is one of its JSON or XML wire-type tokens
///   (<c>object</c>, <c>array</c>, <c>string</c>, <c>number</c>, <c>boolean</c>, <c>element</c>,
///   <c>text</c>). A base or <c>xsi-type:unresolved</c> behind any number of <c>item:</c> prefixes is
///   shown as it is; after <c>item-name:</c> or <c>xsi-type:</c> the name becomes the marker and the
///   prefixes stay; any other token, the empty one included, becomes the marker as a whole.</item>
///   <item>Every other nature is shown unchanged: the fixed vocabulary, <c>d:</c> and <c>failed:</c>
///   signatures, blob ids and exception type names hold no response value.</item>
/// </list>
/// The marker, <c>&lt;redacted: N chars&gt;</c>, states only the value's length in UTF-16 code units. It
/// holds no character of the value and encodes nothing of it, so the console never reveals the value.
/// Callers still write the result through the lifecycle console sink, which escapes control characters.
/// All members are pure and thread-safe.
/// </remarks>
internal static class FailureItemDisplay
{
    private const int SeedSha256Length = 64;
    private const int MaxIntegerDigits = 19;

    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    /// <summary>Returns the console form of <paramref name="nature"/>.</summary>
    /// <param name="nature">The exact nature of a failing item.</param>
    /// <returns>
    /// <paramref name="nature"/> itself, unless it embeds an observed value that must not be printed; then
    /// the nature with that value replaced by the marker.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="nature"/> is null.</exception>
    internal static string Nature(string nature)
    {
        ArgumentNullException.ThrowIfNull(nature);

        if (nature.StartsWith(FailureItemIds.DiffersPrefix, StringComparison.Ordinal))
        {
            var observed = nature[FailureItemIds.DiffersPrefix.Length..];
            return observed.Length == 0 || string.Equals(observed, ConformanceCheck.AbsentObservation, StringComparison.Ordinal)
                ? nature
                : FailureItemIds.DiffersPrefix + Redacted(observed);
        }

        if (nature.StartsWith(FailureItemIds.MismatchPrefix, StringComparison.Ordinal))
        {
            var rest = nature[FailureItemIds.MismatchPrefix.Length..];
            var separator = rest.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0 && IsResetField(rest.AsSpan(0, separator)))
            {
                var field = rest[..separator];
                return $"{FailureItemIds.MismatchPrefix}{field}:{Observed(field, rest[(separator + 1)..])}";
            }

            return rest.Length == 0 ? nature : FailureItemIds.MismatchPrefix + Redacted(rest);
        }

        if (nature.StartsWith(FailureItemIds.WrongTypePrefix, StringComparison.Ordinal))
        {
            return WrongTypeNature(nature);
        }

        return nature;
    }

    /// <summary>Returns <c>&lt;ItemId&gt; &lt;console form of the nature&gt;</c> for a console line.</summary>
    /// <param name="item">The failing item.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    internal static string Describe(FailureItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return $"{item.ItemId} {Nature(item.Nature)}";
    }

    /// <summary>
    /// Returns the console form of a value a state reset observed for <paramref name="field"/>: the value
    /// itself when it is empty, one of the reset's own tokens (<c>null</c>, <c>connect</c>, <c>timeout</c>,
    /// <c>reset</c>, <c>invalid</c>, <c>oversized</c>, <c>invalid-url</c>, a three-digit status code, or
    /// <c>info-</c> or <c>before-</c> followed by such a token) or of the strict form of its field (64
    /// hexadecimal digits for <c>seedSha256</c>, <c>yyyy-MM-dd</c> digits for <c>referenceDate</c>, an integer
    /// for <c>generation</c> and <c>mutations</c>); otherwise the marker.
    /// </summary>
    /// <param name="field">The <c>/admin/info</c> field, for example <c>source</c>.</param>
    /// <param name="observed">The observed value, exactly as the nature holds it.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal static string Observed(string field, string observed)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(observed);

        return observed.Length == 0 || IsResetToken(observed) || HasFieldForm(field, observed)
            ? observed
            : Redacted(observed);
    }

    private static string Redacted(string value) =>
        string.Create(CultureInfo.InvariantCulture, $"<redacted: {value.Length} {(value.Length == 1 ? "char" : "chars")}>");

    // The console form of a nature that starts with wrong-type:, by the allowlist the class remarks state:
    // only a known wire-type token, the unresolved xsi:type token and the prefixes, which the conformance
    // check itself writes, are shown; an item or type name and any token outside the grammar are a
    // response's text.
    private static string WrongTypeNature(string nature)
    {
        var tokenStart = FailureItemIds.WrongTypePrefix.Length;
        var rest = nature.AsSpan(tokenStart);
        while (rest.StartsWith(ConformanceCheck.CollectionItemPrefix, StringComparison.Ordinal))
        {
            rest = rest[ConformanceCheck.CollectionItemPrefix.Length..];
        }

        if (IsWireTypeToken(rest) || rest.SequenceEqual(ConformanceCheck.UnresolvedXsiTypeToken))
        {
            return nature;
        }

        foreach (var namePrefix in (ReadOnlySpan<string>)[ConformanceCheck.CollectionItemNamePrefix, ConformanceCheck.XsiTypeTokenPrefix])
        {
            if (rest.StartsWith(namePrefix, StringComparison.Ordinal))
            {
                var nameStart = nature.Length - rest.Length + namePrefix.Length;
                return nature[..nameStart] + Redacted(nature[nameStart..]);
            }
        }

        return FailureItemIds.WrongTypePrefix + Redacted(nature[tokenStart..]);
    }

    private static bool IsWireTypeToken(ReadOnlySpan<char> token) =>
        token is ConformanceCheck.JsonObjectToken or ConformanceCheck.JsonArrayToken or ConformanceCheck.JsonStringToken
            or ConformanceCheck.JsonNumberToken or ConformanceCheck.JsonBooleanToken
            or ConformanceCheck.XmlElementToken or ConformanceCheck.XmlTextToken;

    private static bool IsResetField(ReadOnlySpan<char> field) =>
        field is StateReset.HttpField or StateReset.GenerationField or StateReset.MutationsField
            or StateReset.SourceField or StateReset.SeedSha256Field or StateReset.ReferenceDateField;

    private static bool IsResetToken(ReadOnlySpan<char> value)
    {
        if (value.StartsWith(StateReset.InfoFailurePrefix, StringComparison.Ordinal))
        {
            return IsFailureToken(value[StateReset.InfoFailurePrefix.Length..]);
        }

        if (value.StartsWith(StateReset.BeforeFailurePrefix, StringComparison.Ordinal))
        {
            var token = value[StateReset.BeforeFailurePrefix.Length..];
            return token is StateReset.NullObserved || IsFailureToken(token);
        }

        return value is StateReset.NullObserved or StateReset.InvalidUrlFailure || IsFailureToken(value);
    }

    // What one admin request or info read can fail with.
    private static bool IsFailureToken(ReadOnlySpan<char> value) =>
        value is StateReset.ConnectFailure or StateReset.TimeoutFailure or StateReset.ResetFailure or StateReset.InvalidBodyFailure
            or StateReset.OversizedBodyFailure
        || (value.Length == 3 && !value.ContainsAnyExceptInRange('0', '9'));

    private static bool HasFieldForm(string field, ReadOnlySpan<char> value) => field switch
    {
        StateReset.SeedSha256Field => value.Length == SeedSha256Length && !value.ContainsAnyExcept(HexDigits),
        StateReset.ReferenceDateField => IsDigitDate(value),
        StateReset.GenerationField or StateReset.MutationsField => IsInteger(value),
        _ => false,
    };

    private static bool IsDigitDate(ReadOnlySpan<char> value) =>
        value.Length == 10
        && value[4] == '-'
        && value[7] == '-'
        && !value[..4].ContainsAnyExceptInRange('0', '9')
        && !value[5..7].ContainsAnyExceptInRange('0', '9')
        && !value[8..].ContainsAnyExceptInRange('0', '9');

    private static bool IsInteger(ReadOnlySpan<char> value)
    {
        var digits = value.StartsWith('-') ? value[1..] : value;
        return digits.Length is > 0 and <= MaxIntegerDigits && !digits.ContainsAnyExceptInRange('0', '9');
    }
}

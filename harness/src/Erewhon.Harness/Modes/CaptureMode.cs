using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Erewhon.Contracts;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Baseline;
using Erewhon.Harness.Cases;
using Erewhon.Harness.Checks;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Http;
using Erewhon.Harness.Lifecycle;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Modes;

/// <summary>
/// The <c>capture</c> mode: records the behaviour baseline from the JADE stand-in before any facade code
/// exists. It sends every case of <c>harness/config/case-matrix.json</c>, in matrix order, straight to the
/// backend (<see cref="HarnessOptions.Backend"/>), and writes the records, the stand-in WSDL, the
/// normalisation rules, the conformance result and, last, the manifest under <c>baseline/</c>.
/// </summary>
/// <remarks>
/// <para>Only <c>harness/scripts/run-local.sh capture</c> runs this mode, against a stand-in it has just
/// started as a local process, so the stand-in's reset generation, clock and generated identifiers start
/// from the same values on every capture. Nothing written carries a wall-clock time, so a re-capture of an
/// unchanged stand-in reproduces <c>baseline/</c> byte for byte apart from the manifest's
/// <c>captureCommit</c>.</para>
/// <para>Capture writes <c>baseline/**</c> only through <see cref="BaselineStore"/>, and
/// <c>contracts/wsdl/ErewhonWebService.wsdl</c> only when that file is absent, through
/// <see cref="ConfinedTree.PublishNewFileAsync"/> rooted at the repository, so neither write follows a link out of
/// the tree: a symbolic link on the way or at the name fails the capture, and an existing contract is never
/// replaced. A WSDL the facade would refuse to publish (<see cref="IsPublishableWsdl"/>) writes neither WSDL file
/// and is recorded as not captured. It never writes the report,
/// <c>artifacts/</c>, the OpenAPI contract or anything under <c>Erewhon/</c>; the seed directory is only
/// fingerprinted, through <see cref="ResetExpectations.FirstCapture(HarnessOptions)"/>. The backend's state
/// is reached only through the stand-in's admin endpoints, and no request carries a header the case
/// matrix does not state.</para>
/// <para>Every check that can refuse a capture (settings, case matrix, an existing manifest, expected seed
/// facts no stand-in can confirm, readiness, and the confirmation of the capture-start seed reset) runs
/// before the first write, so a capture that cannot start leaves an existing baseline untouched, and the
/// seed facts a manifest records are always the ones that reset confirmed. The first write then withdraws
/// the existing manifest (<see cref="BaselineStore.DeleteManifest"/>), and the new one is published last and
/// atomically, so a capture that fails or is cancelled once it has begun writing leaves no manifest beside
/// the partial baseline. An empty-state bracket reset that is not confirmed stops the capture once the
/// bracket's seed restore has been attempted, and no manifest is written. The answered bodies are kept for
/// the conformance check, together at most <see cref="KeptBodyLimit"/> bytes: the case whose body would
/// exceed it stops the capture before its record is written, with one line naming its position, its id and
/// the limit, and no manifest is written. Every line this mode prints
/// starts with <see cref="SourceLabels.ConsolePrefix"/>, readiness lines included. It announces the
/// readiness wait with its limit, and prints one progress line per case (position, case id, status and
/// outcome), never a request path, body or header.
/// The exit code is 0 when the manifest was written, even when some cases were not captured (replay
/// reports each as <c>not-captured</c>), and 1 otherwise, a refused or stopped capture included;
/// <see cref="RunAsync"/> never throws.</para>
/// </remarks>
/// <param name="options">The harness settings: the backend URLs, limits and every baseline path.</param>
/// <param name="catalogue">The operation catalogue, normally <see cref="OperationCatalogue.Default"/>.</param>
/// <param name="stdout">Where the mode's console lines go.</param>
/// <exception cref="ArgumentNullException">An argument is null.</exception>
public sealed class CaptureMode(HarnessOptions options, OperationCatalogue catalogue, TextWriter stdout)
{
    private const string Component = "standin";

    // The one operation whose bodies the response comparer checks byte for byte, so its records keep the
    // raw body in a .body.bin file beside the record.
    private const string BinaryBodySoapMethod = "getPhoto";

    // The DTOResult member that carries the SOAP status constants.
    private const string ResultStatusCodeElement = "statusCode";

    private const string RestBaseUrlKey = HarnessOptions.SectionName + ":Backend:" + nameof(HarnessEndpoints.RestBaseUrl);
    private const string SoapBaseUrlKey = HarnessOptions.SectionName + ":Backend:" + nameof(HarnessEndpoints.SoapBaseUrl);
    private const string AdminUrlKey = HarnessOptions.SectionName + ":Backend:" + nameof(HarnessEndpoints.AdminUrl);
    private const string ExecutionModeKey = HarnessOptions.SectionName + ":" + nameof(HarnessOptions.ExecutionMode);

    private const string NotTouched = "capture cannot start; baseline/ not touched";

    // The WSDL 1.1 SOAP binding namespace and the one element and attribute of it the facade rewrites.
    private const string SoapBindingNamespace = "http://schemas.xmlsoap.org/wsdl/soap/";
    private const string SoapAddressElement = "address";
    private const string SoapAddressLocationAttribute = "location";

    private static readonly XmlReaderSettings ResultReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    // As the facade reads the contracts WSDL: a DTD is refused and nothing is resolved, so a hostile body can
    // neither expand an entity nor reach the network.
    private static readonly XmlReaderSettings WsdlReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    // Decoding throws on invalid bytes instead of substituting U+FFFD, as the facade's does.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HarnessOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly OperationCatalogue _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
    private readonly TextWriter _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
    private readonly TimeSpan _cancelledRestorationLimit = TimeSpan.FromSeconds(ReplayMode.CancelledRestorationLimitSeconds);
    private readonly int _keptBodyLimit = HarnessOptions.EvidenceByteLimit;

    /// <summary>
    /// How long the seed restoration closing the empty-state bracket may still run once the capture is
    /// cancelled, counted from the later of the cancellation and the restoration's start, before it is
    /// abandoned as unconfirmed; 15 s unless a test shortens it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive or is longer than <see cref="int.MaxValue"/> milliseconds.</exception>
    internal TimeSpan CancelledRestorationLimit
    {
        get => _cancelledRestorationLimit;
        init => _cancelledRestorationLimit = CancellationGrace.RequireLimit(value);
    }

    /// <summary>
    /// The most bytes of answered case bodies one capture keeps for its conformance check, all cases together;
    /// <see cref="HarnessOptions.EvidenceByteLimit"/> unless a test lowers it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    internal int KeptBodyLimit
    {
        get => _keptBodyLimit;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _keptBodyLimit = value;
        }
    }

    /// <summary>
    /// Waits for the stand-in, resets it to seed, runs every case, and writes the baseline.
    /// </summary>
    /// <param name="ct">Cancels the capture; cancellation exits 1.</param>
    /// <returns>
    /// 0 when the manifest was written; 1 otherwise, a capture stopped by an unconfirmed reset bracket or by
    /// answered bodies over <see cref="KeptBodyLimit"/> included.
    /// </returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        try
        {
            return await CaptureAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"capture failed: {ex.GetType().FullName}: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> CaptureAsync(CancellationToken ct)
    {
        if (!TryResolveBackend(out var restUri, out var soapUri, out var executionMode))
        {
            return 1;
        }

        CaseMatrix matrix;
        try
        {
            matrix = CaseMatrix.Load(_options.CaseMatrixPath, _catalogue, _options.ResolvePath("."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or ArgumentException or NotSupportedException)
        {
            Log($"case matrix not loaded ({ex.GetType().Name}: {ex.Message}); {NotTouched}");
            return 1;
        }

        var binaryOperationId = _catalogue.Find(Surface.Soap, BinaryBodySoapMethod).Id;
        var store = new BaselineStore(_options);
        var expectations = ResolveExpectations(store);
        if (UnconfirmableSeedFacts(expectations) is { } unconfirmable)
        {
            Log($"seed facts cannot be confirmed (expected {unconfirmable}); {NotTouched}");
            return 1;
        }

        // The wait can last the whole readiness limit, so it is announced; the URL is never quoted.
        Log(string.Create(
            CultureInfo.InvariantCulture,
            $"waiting up to {_options.ReadinessTimeout.TotalSeconds:0} s for the {Component} admin listener"));
        using (var readiness = new Readiness(_options, _stdout, SourceLabels.ConsolePrefix + " "))
        {
            var notReady = await readiness.WaitAsync(Component, _options.Backend.AdminUrl, ct).ConfigureAwait(false);
            if (notReady is not null)
            {
                Log($"stand-in not ready ({notReady.ItemId} {notReady.Nature}); {NotTouched}");
                return 1;
            }
        }

        using var reset = new StateReset(_options);
        var start = await reset
            .ResetAsync(ResetPoints.CaptureStart, AdminRoutes.ResetStateSeed, expectations, ct)
            .ConfigureAwait(false);
        LogResetFailures(start);
        if (!start.Succeeded || start.After is null)
        {
            Log($"{ResetPoints.CaptureStart} reset not confirmed; {NotTouched}");
            return 1;
        }

        // The first write withdraws the old completion record, so a capture that ends early, by failure or
        // cancellation, never leaves a manifest beside the partial baseline it replaces.
        store.DeleteManifest();
        Log("baseline/manifest.json withdrawn until this capture completes");
        store.ClearRecords(ct);

        var records = new List<BaselineRecord>(matrix.PlannedCaseCount);
        var bodies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var notCaptured = new List<string>();
        var unconfirmedBrackets = new List<StateResetResult>();
        long keptBodyBytes = 0;
        bool wsdlCaptured;

        using (var runner = CaseRunner.FromOptions(_options))
        {
            var timeout = CaseRunner.RequestTimeoutOf(_options);
            foreach (var definition in matrix.Cases)
            {
                ct.ThrowIfCancellationRequested();
                var baseUri = definition.Surface == Surface.Rest ? restUri : soapUri;
                var response = await SendCaseAsync(
                        runner, reset, expectations, baseUri, definition, timeout, unconfirmedBrackets, ct)
                    .ConfigureAwait(false);

                // A case recorded against an unconfirmed state is no baseline, so the capture ends here with
                // no manifest, once the bracket's seed restore has been attempted.
                if (unconfirmedBrackets.Count > 0)
                {
                    Log($"reset bracket of {definition.Id} not confirmed; capture stopped, baseline/manifest.json not written");
                    return 1;
                }

                // One line per case, so a slow capture shows which case last completed. It names the
                // position, case id, status and outcome only: request paths carry personal names, and
                // bodies and headers are response content.
                var position = matrix.PositionOf(definition.Id);
                var progress = Progress(position, matrix.PlannedCaseCount);
                if (!response.Answered || response.Status is not int status)
                {
                    notCaptured.Add(definition.Id);
                    Log($"WARN {progress} {definition.Id} not captured: {response.NoResponse} {response.Detail}");
                    continue;
                }

                // Every answered body is kept for the conformance check until the capture ends, so their total is
                // held to the limit before this one is recorded: a body over it stops the capture with no record
                // of it and no manifest, rather than dropping it from the baseline.
                keptBodyBytes += response.Body.Length;
                if (keptBodyBytes > _keptBodyLimit)
                {
                    Log(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{progress} {definition.Id}: the response bodies this capture keeps for its conformance check exceed the "
                        + $"{_keptBodyLimit}-byte limit; capture stopped, baseline/manifest.json not written"));
                    return 1;
                }

                var record = BuildRecord(definition, position, status, response, binaryOperationId);
                await store.WriteRecordAsync(record, response.Body, ct).ConfigureAwait(false);
                records.Add(record);
                bodies[definition.Id] = response.Body;
                var outcome = CheckPlannedOutcome(definition, status, response.Body) ? "captured" : "captured, not as planned";
                Log(string.Create(CultureInfo.InvariantCulture, $"{progress} {definition.Id}: status {status}, {outcome}"));
            }

            wsdlCaptured = await CaptureWsdlAsync(runner, store, soapUri, timeout, ct).ConfigureAwait(false);
        }

        await store
            .WriteNormalisationRulesAsync(NormalisationRules.FromCatalogue(_catalogue).ToJsonNode(), ct)
            .ConfigureAwait(false);

        // Bodies come from memory: only a record whose body lives in a .body.bin file is asked for.
        var conformance = new ConformanceCheck(_catalogue).Run(records, record => bodies[record.CaseId]);
        await store.WriteConformanceAsync(conformance.ToJsonNode(), ct).ConfigureAwait(false);
        LogConformance(conformance);

        var captureCommit = await ReadCaptureCommitAsync(ct).ConfigureAwait(false);
        var capturedOperations = records.Select(record => record.Operation).Distinct(StringComparer.Ordinal).Count();
        var manifest = new BaselineManifest
        {
            Source = SourceLabels.StandIn,
            Label = SourceLabels.BaselineLabel,
            StandIn = new StandInInfo(start.After?.SeedSha256, start.After?.ReferenceDate, start.After?.Generation),
            SeedSha256 = expectations.SeedSha256,
            ReferenceDate = expectations.ReferenceDate,
            Counts = new ManifestCounts(matrix.PlannedOperationCount, matrix.PlannedCaseCount, capturedOperations, records.Count),
            NotCaptured = notCaptured.AsReadOnly(),
            WsdlCaptured = wsdlCaptured,
            HarnessVersion = HarnessOptions.HarnessVersion,
            StartingCommit = _options.StartingCommit,
            CaptureCommit = captureCommit,
            CaptureExecution = executionMode,
            BlockingError = null,
        };

        // Published last, and atomically: a capture that fails before this point leaves no manifest, because
        // the old one was withdrawn before the first write, and exits 1.
        await store.WriteManifestAsync(manifest, ct).ConfigureAwait(false);

        var missing = notCaptured.Count == 0 ? "none" : string.Join(", ", notCaptured);
        Log(string.Create(
            CultureInfo.InvariantCulture,
            $"captured {records.Count} of {matrix.PlannedCaseCount} cases ({capturedOperations} operations), "
            + $"not captured: {missing}, wsdl: {(wsdlCaptured ? "captured" : "absent")}"));
        return 0;
    }

    // Checked before readiness, so unusable settings never reach the baseline, and without quoting any URL
    // setting, which may carry a password. Capture needs the admin listener: it resets and confirms the
    // stand-in's state, so an external backend (blank admin URL) cannot be captured.
    private bool TryResolveBackend(
        [NotNullWhen(true)] out Uri? restUri,
        [NotNullWhen(true)] out Uri? soapUri,
        [NotNullWhen(true)] out string? executionMode)
    {
        restUri = null;
        soapUri = null;
        executionMode = null;

        var backend = _options.Backend;
        if (string.IsNullOrWhiteSpace(backend.AdminUrl))
        {
            Log($"{AdminUrlKey} is blank; capture resets and confirms the stand-in through its admin listener; {NotTouched}");
            return false;
        }

        if (!TryParseSetting(AdminUrlKey, backend.AdminUrl, out _)
            || !TryParseSetting(RestBaseUrlKey, backend.RestBaseUrl, out restUri)
            || !TryParseSetting(SoapBaseUrlKey, backend.SoapBaseUrl, out soapUri))
        {
            return false;
        }

        // A capture that runs is executed one of these two ways; NotRun is reserved for the hand-written
        // manifest of a capture that could not start, which replay reads as holding no seed facts.
        var mode = _options.ExecutionMode?.Trim();
        if (mode is not (CaptureExecutionValues.LocalProcesses or CaptureExecutionValues.Docker))
        {
            Log($"{ExecutionModeKey} '{mode}' is neither '{CaptureExecutionValues.LocalProcesses}' "
                + $"nor '{CaptureExecutionValues.Docker}'; {NotTouched}");
            return false;
        }

        executionMode = mode;
        return true;
    }

    private bool TryParseSetting(string key, string? value, [NotNullWhen(true)] out Uri? uri)
    {
        if (CaseRunner.TryParseHttpUrl(value, out uri, out var problem))
        {
            return true;
        }

        Log($"{key} is not usable ({problem}); {NotTouched}");
        return false;
    }

    // A re-capture confirms the stand-in against the facts the baseline already records; the first capture
    // (or one over a manifest without seed facts) against the shared fingerprint of the seed directory and
    // the configured reference date. A manifest that exists but cannot be read stops the capture before any
    // write, so the cause is fixed rather than silently replaced.
    private ResetExpectations ResolveExpectations(BaselineStore store)
    {
        if (store.TryReadManifest(out var existing) && existing.HasSeedFacts)
        {
            Log($"re-capture: expecting seedSha256 {existing.SeedSha256} and referenceDate {existing.ReferenceDate} "
                + "from baseline/manifest.json");
            return ResetExpectations.FromManifest(existing.SeedSha256, existing.ReferenceDate);
        }

        var expectations = ResetExpectations.FirstCapture(_options, _stdout);
        Log($"first capture: expecting seedSha256 {expectations.SeedSha256} and referenceDate {expectations.ReferenceDate}");
        return expectations;
    }

    // The seed facts a capture could never confirm, or null when both can be: the placeholders a first
    // capture expects when the seed directory cannot be fingerprinted or the configured reference date is
    // malformed, which no stand-in reports, or a fact the manifest leaves blank, which no reset checks. The
    // manifest records only facts the capture-start reset confirmed, so such a capture is refused instead.
    private static string? UnconfirmableSeedFacts(ResetExpectations expectations)
    {
        string[] unconfirmable =
        [
            .. Unconfirmable("seedSha256", expectations.SeedSha256, ResetExpectations.UnavailableSeedSha256),
            .. Unconfirmable("referenceDate", expectations.ReferenceDate, ResetExpectations.InvalidExpectedReferenceDate),
        ];
        return unconfirmable.Length == 0 ? null : string.Join(" and ", unconfirmable);

        static string[] Unconfirmable(string field, string? expected, string placeholder) => expected switch
        {
            null => [$"{field} blank"],
            _ when string.Equals(expected, placeholder, StringComparison.Ordinal) => [$"{field} '{placeholder}'"],
            _ => [],
        };
    }

    // The empty-state case is bracketed by an empty reset before it and a seed reset after it. The empty
    // reset runs inside the protected block, so once it has started the seed reset runs whatever happens
    // (the case failing, the capture being cancelled included), and nothing after it runs against the
    // empty state. Each bracket reset that is not confirmed is added to unconfirmed, which ends the capture.
    private async Task<CaseResponse> SendCaseAsync(
        CaseRunner runner,
        StateReset reset,
        ResetExpectations expectations,
        Uri baseUri,
        CaseDefinition definition,
        TimeSpan timeout,
        ICollection<StateResetResult> unconfirmed,
        CancellationToken ct)
    {
        if (!definition.IsEmptyState)
        {
            return await runner.SendAsync(baseUri, definition.Request, timeout, ct).ConfigureAwait(false);
        }

        try
        {
            LogResetFailures(await reset
                .ResetAsync(ResetPoints.Before(definition.Id), AdminRoutes.ResetStateEmpty, expectations, ct)
                .ConfigureAwait(false), unconfirmed);
            return await runner.SendAsync(baseUri, definition.Request, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            await RestoreSeedAsync(reset, expectations, definition.Id, unconfirmed, ct).ConfigureAwait(false);
        }
    }

    // The seed reset closing the empty-state bracket. Its token is not the capture's: nothing cancels it
    // while the capture runs, so it still runs after a cancellation, and StateReset bounds each of its at
    // most three admin requests by HarnessOptions.RequestTimeout. Once the capture is cancelled it is
    // cancelled CancelledRestorationLimit after the later of the cancellation and the restoration's start, so
    // a cancelled capture never waits on it for longer, whatever the configured request limit. It never
    // throws, so the case's own failure or the cancellation propagates unchanged: a mismatch is logged like
    // every other reset's, and an abandoned restoration or any other failure of the restore on a line of its
    // own. Either way the restore is not confirmed, so it is added to unconfirmed and ends the capture.
    private async Task RestoreSeedAsync(
        StateReset reset,
        ResetExpectations expectations,
        string caseId,
        ICollection<StateResetResult> unconfirmed,
        CancellationToken ct)
    {
        var point = ResetPoints.After(caseId);
        using var grace = new CancellationGrace(ct, _cancelledRestorationLimit);
        try
        {
            LogResetFailures(await reset
                .ResetAsync(point, AdminRoutes.ResetStateSeed, expectations, grace.Token)
                .ConfigureAwait(false), unconfirmed);
        }
        catch (OperationCanceledException ex) when (grace.Token.IsCancellationRequested)
        {
            var detail = string.Create(
                CultureInfo.InvariantCulture,
                $"seed restoration after {caseId} was abandoned {_cancelledRestorationLimit.TotalSeconds:0.###} s after the capture was cancelled");
            Log($"WARN {detail}");
            unconfirmed.Add(new StateResetResult(
                [new FailureItem(FailureItemIds.Reset(point), FailureItemIds.ExceptionNature(ex), Detail: detail)],
                After: null));
        }
        catch (Exception ex)
        {
            Log($"WARN seed restoration after {caseId} failed: {ReplayMode.Describe(ex)}");
            unconfirmed.Add(new StateResetResult(
                [new FailureItem(FailureItemIds.Reset(point), FailureItemIds.ExceptionNature(ex))],
                After: null));
        }
    }

    private BaselineRecord BuildRecord(
        CaseDefinition definition,
        int position,
        int status,
        CaseResponse response,
        string binaryOperationId)
    {
        // getPhoto bodies always go to a .body.bin file. Any other body that is not UTF-8 text a decode
        // reproduces byte for byte goes there too: BaselineStore refuses bodyText that does not reproduce the
        // raw body, and the baseline must keep the bytes the stand-in sent rather than lose the case.
        var binary = string.Equals(definition.Operation, binaryOperationId, StringComparison.Ordinal);
        string? bodyFile = null;
        if (binary || !BaselineResponse.IsTextRoundTrippable(response.Body))
        {
            bodyFile = BaselineStore.BodyFileName(position, definition.JadeMethod, definition.IdNumber);
            if (!binary)
            {
                Log($"WARN {definition.Id} body is not UTF-8 text; recorded as raw bytes in {bodyFile}");
            }
        }

        var request = definition.Request;
        return new BaselineRecord(
            Order: position,
            CaseId: definition.Id,
            Operation: definition.Operation,
            Kind: definition.Kind,
            StateChanging: definition.StateChanging,
            PreState: definition.PreState,
            Source: SourceLabels.StandIn,
            Request: new BaselineRequest(request.Method, request.PathAndQuery, request.Headers, request.BodyText),
            Response: BaselineResponse.FromRawBody(status, response.ContentType, response.Body, bodyFile));
    }

    // Console only: the baseline records what the stand-in answered, and an outcome other than the planned
    // one is the execution agent's to judge, never a reason to alter or drop the record. Prints a WARN line
    // per deviation and returns true when there was none.
    private bool CheckPlannedOutcome(CaseDefinition definition, int status, byte[] body)
    {
        var asPlanned = true;
        if (status != definition.ExpectedStatus)
        {
            asPlanned = false;
            Log(string.Create(
                CultureInfo.InvariantCulture,
                $"WARN {definition.Id} status {status} expected {definition.ExpectedStatus}"));
        }

        if (definition.ExpectedResultStatusCode is not int expectedCode)
        {
            return asPlanned;
        }

        if (ReadResultStatusCode(body, out var problem) is not int observedCode)
        {
            Log($"WARN {definition.Id} result statusCode unreadable: {problem}");
            return false;
        }

        if (observedCode != expectedCode)
        {
            Log(string.Create(
                CultureInfo.InvariantCulture,
                $"WARN {definition.Id} result statusCode {observedCode} expected {expectedCode}"));
            return false;
        }

        return asPlanned;
    }

    // "012/056": the case's position among the planned cases, numbered as its record's file name is.
    private static string Progress(int position, int planned) =>
        string.Create(CultureInfo.InvariantCulture, $"{position:D3}/{planned:D3}");

    /// <summary>Reads the SOAP <c>DTOResult</c> <c>statusCode</c> of a captured response body.</summary>
    /// <remarks>
    /// The problem is a fixed classification, with at most the exception's type name: response bodies
    /// carry personal data (names, addresses, email), and both the element's text and a parser message
    /// (which quotes offending tokens) are body content. The exact body stays only in the baseline record.
    /// </remarks>
    /// <param name="body">The raw response body.</param>
    /// <param name="problem">
    /// Empty when a code was read; otherwise why not: <c>the body is not XML (&lt;exception type&gt;)</c>,
    /// <c>the body has no statusCode element</c> or <c>statusCode is not an integer</c>.
    /// </param>
    /// <returns>The first <c>statusCode</c> element's integer value, or null when none could be read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is null.</exception>
    internal static int? ReadResultStatusCode(byte[] body, out string problem)
    {
        ArgumentNullException.ThrowIfNull(body);

        XDocument document;
        try
        {
            using var stream = new MemoryStream(body, writable: false);
            using var reader = XmlReader.Create(stream, ResultReaderSettings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            problem = $"the body is not XML ({ex.GetType().Name})";
            return null;
        }

        var element = document
            .Descendants()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name.LocalName, ResultStatusCodeElement, StringComparison.Ordinal));
        if (element is null)
        {
            problem = $"the body has no {ResultStatusCodeElement} element";
            return null;
        }

        // xsd:int collapses surrounding whitespace.
        if (!int.TryParse(element.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code))
        {
            problem = $"{ResultStatusCodeElement} is not an integer";
            return null;
        }

        problem = "";
        return code;
    }

    // The baseline WSDL is exactly what the stand-in served; the contracts WSDL is that file labelled, and is
    // written only when absent, because once published it is the facade's and capture never replaces it. A body
    // the facade would refuse to publish writes neither file.
    private async Task<bool> CaptureWsdlAsync(
        CaseRunner runner,
        BaselineStore store,
        Uri soapUri,
        TimeSpan timeout,
        CancellationToken ct)
    {
        CaseResponse response;
        try
        {
            var wsdlUri = CaseRunner.Combine(soapUri, _catalogue.Addresses.Soap.WsdlPathAndQuery);
            response = await runner.GetAsync(wsdlUri, timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return DropWsdl(store, $"{ex.GetType().Name}: {ex.Message}");
        }

        if (!response.Answered)
        {
            return DropWsdl(store, $"no response ({response.NoResponse} {response.Detail})");
        }

        if (response.Status != 200)
        {
            return DropWsdl(store, string.Create(CultureInfo.InvariantCulture, $"status {response.Status}, not 200"));
        }

        if (response.Body.Length == 0)
        {
            return DropWsdl(store, "the body is empty");
        }

        // A body the facade would refuse to publish is no WSDL obtained: the facade would forward WSDL requests
        // to the backend instead, so neither file records it and the manifest says wsdlCaptured: false.
        if (!IsPublishableWsdl(response.Body, out var problem))
        {
            return DropWsdl(store, $"the facade would not publish it ({problem})");
        }

        await store.WriteWsdlAsync(response.Body, ct).ConfigureAwait(false);
        Log(string.Create(CultureInfo.InvariantCulture, $"wrote baseline WSDL ({response.Body.Length} bytes)"));

        // Published create-only through directory handles rooted at the repository, as BaselineStore writes
        // baseline/: a linked contracts/ or contracts/wsdl, or anything but a regular file at the name, is
        // refused and fails the capture, and an existing contract is kept byte for byte.
        var contracts = new ConfinedTree(_options.ResolvePath("."));
        var published = await contracts
            .PublishNewFileAsync(_options.ContractsWsdlPath, WithHeaderComment(response.Body), ct)
            .ConfigureAwait(false);
        Log(published ? "wrote contracts WSDL" : "contracts WSDL present; not overwritten");
        return true;
    }

    // A stale baseline WSDL from an earlier capture would contradict wsdlCaptured: false. The contracts WSDL
    // is left as it is, so the facade keeps whatever it already publishes.
    private bool DropWsdl(BaselineStore store, string reason)
    {
        store.DeleteWsdl();
        Log($"WARN WSDL not captured: {reason}; baseline WSDL removed, contracts WSDL left as it is");
        return false;
    }

    /// <summary>
    /// Whether the facade would publish <paramref name="wsdl"/> as <c>contracts/wsdl/ErewhonWebService.wsdl</c>.
    /// </summary>
    /// <remarks>
    /// This is the facade's own publication rule (<c>WsdlDocument</c> in
    /// <c>facade/src/Erewhon.Facade/Soap/SoapEndpoints.cs</c>), restated here because the harness does not
    /// reference the facade, so capture never records a WSDL the facade would refuse and replace by
    /// forwarding: the bytes after an optional UTF-8 byte-order mark decode as strict UTF-8, the whole
    /// document reads as well-formed XML without a DTD, and it holds exactly one <c>soap:address</c> element
    /// (WSDL 1.1 SOAP binding namespace), which carries a <c>location</c> attribute. The problem names the
    /// rule that failed in the facade's words, never document content.
    /// </remarks>
    /// <param name="wsdl">The WSDL bytes as served.</param>
    /// <param name="problem">
    /// Empty when the facade would publish the bytes; otherwise <c>not UTF-8</c>, <c>not well-formed XML</c>,
    /// <c>expected exactly one soap:address, found &lt;count&gt;</c> or
    /// <c>soap:address has no location attribute</c>.
    /// </param>
    /// <returns>True when the facade would publish the bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="wsdl"/> is null.</exception>
    internal static bool IsPublishableWsdl(byte[] wsdl, out string problem)
    {
        ArgumentNullException.ThrowIfNull(wsdl);

        ReadOnlySpan<byte> utf8Bom = [0xEF, 0xBB, 0xBF];
        var contentStart = wsdl.AsSpan().StartsWith(utf8Bom) ? utf8Bom.Length : 0;
        string text;
        try
        {
            text = StrictUtf8.GetString(wsdl, contentStart, wsdl.Length - contentStart);
        }
        catch (DecoderFallbackException)
        {
            problem = "not UTF-8";
            return false;
        }

        // Reading to the end proves the whole document well-formed, not only the part before the address.
        var addresses = 0;
        var hasLocation = false;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), WsdlReaderSettings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element
                    || !string.Equals(reader.LocalName, SoapAddressElement, StringComparison.Ordinal)
                    || !string.Equals(reader.NamespaceURI, SoapBindingNamespace, StringComparison.Ordinal))
                {
                    continue;
                }

                addresses++;
                if (addresses == 1)
                {
                    hasLocation = reader.GetAttribute(SoapAddressLocationAttribute) is not null;
                }
            }
        }
        catch (XmlException)
        {
            problem = "not well-formed XML";
            return false;
        }

        if (addresses != 1)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"expected exactly one soap:address, found {addresses}");
            return false;
        }

        if (!hasLocation)
        {
            problem = "soap:address has no location attribute";
            return false;
        }

        problem = "";
        return true;
    }

    /// <summary>
    /// Returns the served WSDL with the comment <c>&lt;!-- </c><see cref="SourceLabels.WsdlHeaderComment"/><c> --&gt;</c>
    /// on its own line right after the XML declaration, or at the start of the content (after a UTF-8
    /// byte-order mark) when there is no declaration. Every other byte is kept, so the labelled file differs
    /// from the served one only by that comment, which the XML comparers drop.
    /// </summary>
    /// <param name="wsdl">The WSDL bytes as served.</param>
    /// <returns>The labelled bytes.</returns>
    /// <exception cref="InvalidDataException">
    /// The bytes are UTF-16 or UTF-32, which the UTF-8 comment would corrupt, or the declaration has no
    /// closing <c>?&gt;</c>.
    /// </exception>
    internal static byte[] WithHeaderComment(byte[] wsdl)
    {
        ArgumentNullException.ThrowIfNull(wsdl);

        ReadOnlySpan<byte> served = wsdl;
        ReadOnlySpan<byte> utf8Bom = [0xEF, 0xBB, 0xBF];
        ReadOnlySpan<byte> bigEndianBom = [0xFE, 0xFF];
        ReadOnlySpan<byte> littleEndianBom = [0xFF, 0xFE];
        if (served.StartsWith(bigEndianBom) || served.StartsWith(littleEndianBom))
        {
            throw new InvalidDataException(
                "The WSDL is not in an ASCII-compatible encoding, so it cannot be labelled byte for byte.");
        }

        var contentStart = served.StartsWith(utf8Bom) ? utf8Bom.Length : 0;
        var content = served[contentStart..];
        var comment = Encoding.UTF8.GetBytes($"<!-- {SourceLabels.WsdlHeaderComment} -->");

        int insertAt;
        byte[] insertion;
        if (IsXmlDeclaration(content))
        {
            var close = content.IndexOf("?>"u8);
            if (close < 0)
            {
                throw new InvalidDataException("The WSDL's XML declaration has no closing '?>'.");
            }

            insertAt = contentStart + close + 2;
            insertion = [(byte)'\n', .. comment];
        }
        else
        {
            insertAt = contentStart;
            insertion = [.. comment, (byte)'\n'];
        }

        return [.. served[..insertAt], .. insertion, .. served[insertAt..]];
    }

    // "<?xml" followed by white space; "<?xml-stylesheet" and other processing instructions do not count.
    private static bool IsXmlDeclaration(ReadOnlySpan<byte> content) =>
        content.StartsWith("<?xml"u8)
        && content.Length > 5
        && content[5] is 0x20 or 0x09 or 0x0D or 0x0A;

    // Each gap is listed so the execution agent can apply the three-attempt correction rule; the
    // unreachable-in-source gaps are expected and registered in harness/config/known-limitations.json.
    private void LogConformance(ConformanceResult conformance)
    {
        foreach (var failure in conformance.Failures)
        {
            Log($"gap {FailureItemDisplay.Describe(failure)}");
        }

        var counts = conformance.Counts;
        var gaps = string.Join(
            ", ",
            counts.GapsByNature.Select(gap => string.Create(CultureInfo.InvariantCulture, $"{gap.Key} {gap.Value}")));
        var holding = conformance.SeedAssertions.Count(
            assertion => string.Equals(assertion.Result, ConformanceResult.PassResult, StringComparison.Ordinal));
        Log(string.Create(
            CultureInfo.InvariantCulture,
            $"conformance: {counts.Passed} of {counts.MembersChecked} members passed; gaps: {gaps}; "
            + $"seed-token assertions: {holding} of {conformance.SeedAssertions.Count} hold"));
    }

    // HEAD at capture is evidence only: a missing git, a checkout without metadata or any git failure
    // records null rather than failing the capture.
    private async Task<string?> ReadCaptureCommitAsync(CancellationToken ct)
    {
        GitResult result;
        try
        {
            result = await Git
                .RunAsync(_options.ResolvePath("."), ["rev-parse", "HEAD"], Git.DefaultTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log($"WARN captureCommit not recorded: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        var commit = result.Succeeded ? result.StdOut.Trim() : "";
        if (commit.Length > 0)
        {
            return commit;
        }

        var reason = result switch
        {
            { Unavailable: true } => "git is unavailable",
            { TimedOut: true } => "git rev-parse HEAD timed out",
            { Succeeded: true } => "git rev-parse HEAD printed nothing",
            _ => string.Create(CultureInfo.InvariantCulture, $"git rev-parse HEAD exited {result.ExitCode}"),
        };
        Log($"WARN captureCommit not recorded: {reason}");
        return null;
    }

    // Prints each mismatch of a reset, and adds an unconfirmed reset to unconfirmed when one is given.
    private void LogResetFailures(StateResetResult result, ICollection<StateResetResult>? unconfirmed = null)
    {
        foreach (var failure in result.Failures)
        {
            Log($"WARN {FailureItemDisplay.Describe(failure)}");
        }

        if (!result.Succeeded)
        {
            unconfirmed?.Add(result);
        }
    }

    // Every line carries the stand-in prefix and goes through the lifecycle sink, which escapes control
    // characters in quoted values (response details, exception messages) and never throws.
    private void Log(string message) =>
        LifecycleConsole.WriteLine(_stdout, SourceLabels.ConsolePrefix + " " + message);
}

using System.Globalization;
using System.Security;
using System.Text;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Http;
using Erewhon.Harness.Lifecycle;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Checks;

/// <summary>
/// Checks that the facade publishes the contracts WSDL and that the WSDL it publishes equals the backend's
/// WSDL under XML semantic comparison, with the single <c>soap:address/@location</c> value left uncompared:
/// the facade rewrites only that value per request, and the attribute must still be present on both sides.
/// Comments are ignored, so the stand-in label comment of <c>contracts/wsdl/ErewhonWebService.wsdl</c> never
/// causes a difference.
/// </summary>
/// <remarks>
/// <para>Evaluation order, first match wins:</para>
/// <list type="number">
///   <item>No <c>contracts/wsdl/ErewhonWebService.wsdl</c> (capture obtained no WSDL): <c>CHK-WSDL-CONTRACT</c>
///   <c>not-run</c>, outcome <c>not-run</c>, with no request and no comparison.</item>
///   <item>A SOAP base URL from which no WSDL address can be formed, or a <c>Harness:Target:AdminUrl</c> from
///   which no <c>/admin/info</c> address can be formed: <c>not-run</c>, with no request.</item>
///   <item>Concurrently, each within the per-request limit: <c>GET</c> of the catalogue's WSDL path and query on
///   <c>Harness:Backend:SoapBaseUrl</c> and on <c>Harness:Target:SoapBaseUrl</c>, and <c>GET</c> of
///   <c>/admin/info</c> on <c>Harness:Target:AdminUrl</c>. Each answered WSDL body, whatever its status, is
///   saved byte for byte to <c>artifacts/wsdl/backend.wsdl</c> or <c>artifacts/wsdl/facade.wsdl</c>; the info
///   body is only read.</item>
///   <item>A side that was not answered gives <c>$response.&lt;side&gt;</c> with kind
///   <c>no-response:&lt;reason&gt;</c>; a side answered with a status other than 200 gives
///   <c>$status.&lt;side&gt;</c> with kind <c>value</c>.</item>
///   <item>Unless the facade answered its info with 200 and a JSON object whose <c>wsdl</c> string is exactly
///   <c>present</c>, one <c>$published.facade</c> entry whose baseline is <c>present</c>: kind <c>value</c>
///   with the reported string as observed, or kind <c>missing</c> with nothing observed when no <c>wsdl</c>
///   string could be read. A facade that cannot publish the contract forwards WSDL requests to the backend,
///   so its WSDL is the backend's own and the comparison alone would pass.</item>
///   <item>Only when both sides answered 200 are the bodies compared, the backend as the baseline and the
///   facade as the observed side.</item>
///   <item>Any discrepancy: <c>d:&lt;signature&gt;</c> over the canonical list, outcome <c>fail</c>; none: pass.</item>
/// </list>
/// <para>Every discrepancy is built from fixed paths, kinds and values, so the same failure gives the same
/// signature on every run; run-varying transport text goes to the detail only. The check writes only
/// <c>artifacts/wsdl/**</c>. It never writes <c>contracts/**</c> or <c>baseline/**</c>, which capture alone
/// writes, and the discrepancy artifact is written by the caller from
/// <see cref="ContractCheckResult.Canonical"/> through
/// <see cref="CanonicalDiscrepancies.ToArtifact(FailureItem)"/>. A failure to save or
/// remove an artifact is reported in the detail and never thrown.</para>
/// <para>The instance keeps no per-run state, but concurrent runs would race on the artifact files, so one
/// run at a time is expected. The <see cref="CaseRunner"/> belongs to the caller and is not disposed here.</para>
/// </remarks>
/// <param name="options">
/// The harness settings: both SOAP base URLs, the facade admin URL, the request limit, the contract and
/// artifact paths.
/// </param>
/// <param name="catalogue">The operation catalogue, whose SOAP address supplies the WSDL path and query.</param>
/// <param name="comparer">The XML comparer; it is called with the WSDL context.</param>
/// <param name="http">The runner both documents and the facade's info are fetched with.</param>
/// <exception cref="ArgumentNullException">An argument is null.</exception>
public sealed class WsdlContractCheck(
    HarnessOptions options,
    OperationCatalogue catalogue,
    XmlSemanticComparer comparer,
    CaseRunner http)
{
    private const int ExpectedStatus = 200;

    // Path roots of the fetch discrepancies, in the convention every comparer in Compare/ uses, suffixed
    // with the side so the two sides' entries never share a path and canonical ordering stays total.
    private const string ResponsePath = "$response";
    private const string StatusPath = "$status";

    // The path root of the publication discrepancy, in the same convention and suffixed with the facade side.
    private const string PublishedPath = "$published";

    // The wsdl value of the facade's /admin/info when it loaded the contracts WSDL and serves it; the facade
    // reports absent, and forwards WSDL requests to its default target, when it could not.
    private const string PublishedStatus = "present";

    private const string FacadeInfoSettingName = "Harness:Target:AdminUrl";

    // The body as a whole, in the convention every comparer in Compare/ uses: a body that is not UTF-8 has no XML
    // tree, and no XML path is this one, so its entries never match those XmlSemanticComparer reports.
    private const string BodyPath = "$body";

    // The baseline token of a no-response discrepancy: a response was expected on that side.
    private const string ExpectedResponse = "response";

    private const string AbsentContractDetail = "contracts WSDL absent; capture obtained no WSDL";
    private const string ArtifactSubdirectory = "wsdl";

    // Strict, so two documents that differ only in invalid byte sequences cannot both decode to U+FFFD
    // and compare equal; an undecodable side is reported as malformed instead.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly WsdlSide Backend = new("backend", "Harness:Backend:SoapBaseUrl", "backend.wsdl");
    private static readonly WsdlSide Facade = new("facade", "Harness:Target:SoapBaseUrl", "facade.wsdl");

    private readonly HarnessOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly string _wsdlPathAndQuery =
        (catalogue ?? throw new ArgumentNullException(nameof(catalogue))).Addresses.Soap.WsdlPathAndQuery;
    private readonly XmlSemanticComparer _comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
    private readonly CaseRunner _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>
    /// Fetches the backend and facade WSDLs and the facade's <c>/admin/info</c>, saves the two WSDLs as
    /// artifacts, requires the facade to report the contracts WSDL as published and compares the two WSDLs,
    /// unless the contracts WSDL is absent.
    /// </summary>
    /// <param name="ct">The caller's cancellation, which propagates as <see cref="OperationCanceledException"/>.</param>
    /// <returns>The check row, the failing item when the check did not pass, and the discrepancies of a difference.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The configured request limit exceeds <see cref="int.MaxValue"/> milliseconds.</exception>
    /// <exception cref="InvalidDataException">
    /// A fetched body exceeds <see cref="HarnessOptions.EvidenceByteLimit"/>, as <see cref="CaseRunner"/>
    /// refuses it, or the comparison of the two WSDLs would retain more discrepancy evidence than the
    /// <see cref="XmlSemanticComparer"/> allows.
    /// </exception>
    public async Task<ContractCheckResult> RunAsync(CancellationToken ct)
    {
        // File.Exists is false for a directory, which therefore also counts as absent. Artifacts left by an
        // earlier run would pass for this run's evidence, so they are removed on every path that fetches nothing.
        if (!File.Exists(_options.ContractsWsdlPath))
        {
            return NotRun(Join(AbsentContractDetail, RemoveArtifacts()));
        }

        // An unusable base URL is reported as not-run, as the OpenAPI drift check does for its admin URL:
        // with either WSDL address missing there is nothing the facade WSDL could be compared with, and
        // without the facade's admin address its publication of the contract cannot be established.
        var addressProblems = new List<string>();
        var backendUri = Address(Backend, _options.Backend.SoapBaseUrl, addressProblems);
        var facadeUri = Address(Facade, _options.Target.SoapBaseUrl, addressProblems);
        var infoUri = Address("facade info", FacadeInfoSettingName, _options.Target.AdminUrl, AdminRoutes.Info, addressProblems);
        if (backendUri is null || facadeUri is null || infoUri is null)
        {
            return NotRun(Join(string.Join("; ", addressProblems), RemoveArtifacts()));
        }

        var timeout = CaseRunner.RequestTimeoutOf(_options);
        var responses = await Task.WhenAll(
                _http.GetAsync(backendUri, timeout, ct),
                _http.GetAsync(facadeUri, timeout, ct),
                _http.GetAsync(infoUri, timeout, ct))
            .ConfigureAwait(false);
        var backend = responses[0];
        var facade = responses[1];
        var facadeInfo = responses[2];

        var artifactNotes = new List<string>();
        AddNote(artifactNotes, await KeepArtifactAsync(Backend, backend, ct).ConfigureAwait(false));
        AddNote(artifactNotes, await KeepArtifactAsync(Facade, facade, ct).ConfigureAwait(false));
        var artifactNote = artifactNotes.Count == 0 ? null : string.Join("; ", artifactNotes);

        var discrepancies = new List<Discrepancy>();
        var fetchProblems = new List<string>();
        AddFetchDiscrepancies(Backend, backend, discrepancies, fetchProblems);
        AddFetchDiscrepancies(Facade, facade, discrepancies, fetchProblems);
        AddPublicationDiscrepancy(facadeInfo, infoUri, discrepancies, fetchProblems);

        if (backend.Status == ExpectedStatus && facade.Status == ExpectedStatus)
        {
            discrepancies.AddRange(CompareDocuments(backend.Body, facade.Body));
        }

        if (discrepancies.Count == 0)
        {
            return new ContractCheckResult(
                new CheckResult(CheckNames.WsdlContract, CheckOutcomes.Pass, Cases: 1, Passed: 1, artifactNote),
                Failure: null,
                []);
        }

        // Sorted into canonical order, written and hashed once; the caller writes the artifact from the same result.
        var canonical = DiscrepancySignature.Prepare(discrepancies);
        var first = canonical.Entries[0];
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{discrepancies.Count} difference{(discrepancies.Count == 1 ? "" : "s")} for the facade WSDL ({facadeUri}) "
            + $"against the backend WSDL ({backendUri}); first at {first.Path} ({first.Kind})");
        foreach (var problem in fetchProblems)
        {
            summary = Join(summary, problem);
        }

        var detail = Join(summary, artifactNote);
        return new ContractCheckResult(
            new CheckResult(CheckNames.WsdlContract, CheckOutcomes.Fail, Cases: 1, Passed: 0, detail),
            new FailureItem(FailureItemIds.WsdlContract, FailureItemIds.Diff(canonical.Signature), Detail: detail),
            canonical.Entries)
        {
            Canonical = canonical,
        };
    }

    private static ContractCheckResult NotRun(string detail) =>
        new(
            new CheckResult(CheckNames.WsdlContract, CheckOutcomes.NotRun, Cases: 0, Passed: 0, detail),
            new FailureItem(FailureItemIds.WsdlContract, FailureItemIds.NotRun, Detail: detail),
            []);

    private static string Join(string detail, string? note) => note is null ? detail : $"{detail}; {note}";

    private static void AddNote(List<string> notes, string? note)
    {
        if (note is not null)
        {
            notes.Add(note);
        }
    }

    // Returns the side's WSDL address, or null after adding the reason to problems.
    private Uri? Address(WsdlSide side, string baseUrl, List<string> problems) =>
        Address($"{side.Name} WSDL", side.SettingName, baseUrl, _wsdlPathAndQuery, problems);

    // Returns pathAndQuery on the authority of baseUrl, or null after adding the reason to problems. The
    // reason names the setting and never quotes its value, which may carry a password.
    private static Uri? Address(string target, string settingName, string baseUrl, string pathAndQuery, List<string> problems)
    {
        try
        {
            return CaseRunner.Combine(baseUrl, pathAndQuery);
        }
        catch (ArgumentException ex)
        {
            // A null or blank base URL lands here too; ArgumentNullException derives from ArgumentException.
            problems.Add($"no {target} address can be formed from {settingName}: {ex.Message}");
            return null;
        }
    }

    // The values are fixed per side and reason, so a stopped facade gives the same signature on every run;
    // CaseResponse.Detail varies (ports, host names, operating-system text) and goes to the detail only.
    private static void AddFetchDiscrepancies(
        WsdlSide side,
        CaseResponse response,
        List<Discrepancy> discrepancies,
        List<string> problems)
    {
        if (!response.Answered)
        {
            var kind = DiscrepancyKinds.NoResponse(response.NoResponse!);
            discrepancies.Add(new Discrepancy(
                $"{ResponsePath}.{side.Name}",
                kind,
                DiscrepancyValue.Literal(ExpectedResponse),
                DiscrepancyValue.Literal(kind)));
            var reason = response.Detail is null ? "" : $"; {response.Detail}";
            problems.Add($"no response from the {side.Name} ({kind}{reason})");
            return;
        }

        if (response.Status != ExpectedStatus)
        {
            var status = response.Status?.ToString(CultureInfo.InvariantCulture);
            discrepancies.Add(new Discrepancy(
                $"{StatusPath}.{side.Name}",
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal(ExpectedStatus.ToString(CultureInfo.InvariantCulture)),
                DiscrepancyValue.Literal(status)));
            problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"the {side.Name} answered status {status}, not {ExpectedStatus}"));
        }
    }

    /// <summary>
    /// Adds the <c>$published.facade</c> entry unless the facade's info declares the contracts WSDL published:
    /// status 200 and a UTF-8 JSON object whose <c>wsdl</c> string is exactly <c>present</c>. A reported string
    /// that is not <c>present</c> is kind <c>value</c> with that string as observed; an info that cannot be read
    /// (no response, another status, a body that is not such an object, no <c>wsdl</c> string) is kind
    /// <c>missing</c> with nothing observed. The values never carry transport text, a status or a parse
    /// problem, so the same facade state gives the same signature on every run; the reason goes to the detail.
    /// </summary>
    /// <param name="response">The facade's answer to <c>GET /admin/info</c>.</param>
    /// <param name="infoUri">The address it was fetched from, named in the detail only.</param>
    /// <param name="discrepancies">Receives the entry.</param>
    /// <param name="problems">Receives the reason.</param>
    private static void AddPublicationDiscrepancy(
        CaseResponse response,
        Uri infoUri,
        List<Discrepancy> discrepancies,
        List<string> problems)
    {
        var path = $"{PublishedPath}.{Facade.Name}";
        string unreadable;
        if (!response.Answered)
        {
            var kind = DiscrepancyKinds.NoResponse(response.NoResponse!);
            var reason = response.Detail is null ? "" : $"; {response.Detail}";
            unreadable = $"no response from {infoUri} ({kind}{reason})";
        }
        else if (response.Status != ExpectedStatus)
        {
            unreadable = string.Create(
                CultureInfo.InvariantCulture,
                $"{infoUri} answered status {response.Status}, not {ExpectedStatus}");
        }
        else if (TryDecode(WithoutBom(response.Body)) is not { } text || AdminInfo.Parse(text) is not { } info)
        {
            // Strict decoding, as for the WSDL bodies: replacement decoding would turn different invalid bytes
            // into the same reported text, so two different answers would share one signature.
            unreadable = $"{infoUri} answered a body that is not a UTF-8 JSON object whose strings can be decoded";
        }
        else if (info.Wsdl is not { } reported)
        {
            unreadable = $"{infoUri} answered no wsdl string";
        }
        else
        {
            if (!string.Equals(reported, PublishedStatus, StringComparison.Ordinal))
            {
                discrepancies.Add(new Discrepancy(
                    path,
                    DiscrepancyKinds.Value,
                    DiscrepancyValue.Literal(PublishedStatus),
                    DiscrepancyValue.Literal(reported)));
                problems.Add(
                    $"the facade reports wsdl '{reported}', not '{PublishedStatus}', at {infoUri}: it does not declare "
                    + "the contracts WSDL published, and a facade that reports absent forwards WSDL requests to its default target");
            }

            return;
        }

        discrepancies.Add(new Discrepancy(
            path,
            DiscrepancyKinds.Missing,
            DiscrepancyValue.Literal(PublishedStatus),
            DiscrepancyValue.Absent));
        problems.Add($"the facade's publication of the contracts WSDL is unconfirmed: {unreadable}");
    }

    // The backend WSDL is the baseline side and the facade WSDL the observed side.
    private IReadOnlyList<Discrepancy> CompareDocuments(byte[] backendBody, byte[] facadeBody)
    {
        var baselineBody = WithoutBom(backendBody);
        var observedBody = WithoutBom(facadeBody);
        var baselineText = TryDecode(baselineBody);
        var observedText = TryDecode(observedBody);

        if (baselineText is null || observedText is null)
        {
            return UndecodableDiscrepancies(baselineBody, baselineText, observedBody, observedText);
        }

        return _comparer.Compare(baselineText, observedText, XmlComparisonContext.Wsdl(excludeSoapAddressLocation: true));
    }

    /// <summary>
    /// The discrepancies at <c>$body</c>, the body as a whole, when at least one document is not valid UTF-8.
    /// Such a document has no XML tree, and <c>$body</c> is never a path the XML comparer or a fetch
    /// discrepancy reports, so these entries cannot equal the comparer's <c>malformed</c> entry at <c>/</c>
    /// for decodable text that is not XML, such as the text <c>ff</c>. An undecodable document is the
    /// lowercase hexadecimal of its bytes after the byte-order mark, which keeps every byte where replacement
    /// decoding would merge, for example, 0xFF and 0xFE into one character. The <c>malformed</c> entry holds
    /// that value on each side that does not decode and nothing on a side that does, so it names the failing
    /// side. When the other document decodes, a <c>type</c> entry also holds both documents, the decodable one
    /// as its exact text, so a change to either document changes the signature.
    /// </summary>
    /// <param name="baselineBody">The backend bytes after the byte-order mark.</param>
    /// <param name="baselineText">The decoded backend text; null when it is not valid UTF-8.</param>
    /// <param name="observedBody">The facade bytes after the byte-order mark.</param>
    /// <param name="observedText">The decoded facade text; null when it is not valid UTF-8.</param>
    private static IReadOnlyList<Discrepancy> UndecodableDiscrepancies(
        ReadOnlySpan<byte> baselineBody,
        string? baselineText,
        ReadOnlySpan<byte> observedBody,
        string? observedText)
    {
        var baselineHex = baselineText is null ? Convert.ToHexStringLower(baselineBody) : null;
        var observedHex = observedText is null ? Convert.ToHexStringLower(observedBody) : null;

        List<Discrepancy> discrepancies =
        [
            new Discrepancy(
                BodyPath,
                DiscrepancyKinds.Malformed,
                baselineHex is null ? DiscrepancyValue.Absent : DiscrepancyValue.Literal(baselineHex),
                observedHex is null ? DiscrepancyValue.Absent : DiscrepancyValue.Literal(observedHex)),
        ];

        if (baselineText is not null || observedText is not null)
        {
            discrepancies.Add(new Discrepancy(
                BodyPath,
                DiscrepancyKinds.Type,
                DiscrepancyValue.Literal(baselineText ?? baselineHex),
                DiscrepancyValue.Literal(observedText ?? observedHex)));
        }

        return discrepancies;
    }

    private static string? TryDecode(ReadOnlySpan<byte> body)
    {
        try
        {
            return StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static ReadOnlySpan<byte> WithoutBom(byte[] bytes)
    {
        var span = bytes.AsSpan();
        return span.StartsWith(Encoding.UTF8.Preamble) ? span[Encoding.UTF8.Preamble.Length..] : span;
    }

    // An answered body is saved whatever its status, so a 404 or an error page is kept as evidence too; an
    // unanswered side's artifact from an earlier run is removed instead.
    private Task<string?> KeepArtifactAsync(WsdlSide side, CaseResponse response, CancellationToken ct) =>
        response.Answered ? SaveArtifactAsync(side, response.Body, ct) : Task.FromResult(RemoveArtifact(side));

    private async Task<string?> SaveArtifactAsync(WsdlSide side, byte[] body, CancellationToken ct)
    {
        string? path = null;
        try
        {
            path = ArtifactPath(side);
            await ArtifactTree().WriteFileAsync(path, body, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            // A partly written file would misstate the response, so it is removed as well.
            var removal = path is null ? null : RemoveArtifact(side);
            return Join($"{side.Name} WSDL not saved to {path ?? "artifacts/wsdl"}: {Describe(ex)}", removal);
        }
    }

    private string? RemoveArtifacts()
    {
        var backendNote = RemoveArtifact(Backend);
        var facadeNote = RemoveArtifact(Facade);
        return backendNote is null ? facadeNote : Join(backendNote, facadeNote);
    }

    private string? RemoveArtifact(WsdlSide side)
    {
        string? path = null;
        try
        {
            path = ArtifactPath(side);

            // An absent directory or file is nothing to remove, and a directory in the file's place is kept.
            ArtifactTree().DeleteFile(path);
            return null;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return $"earlier {side.Name} WSDL {path ?? "artifacts/wsdl"} not removed: {Describe(ex)}";
        }
    }

    private string ArtifactPath(WsdlSide side) =>
        Path.Combine(_options.ArtifactsDirectory, ArtifactSubdirectory, side.FileName);

    // Both artifacts are saved and removed through directory handles anchored at the repository root, so a
    // link at artifacts/ or artifacts/wsdl refuses the call and a link at an artifact is never written through:
    // nothing outside artifacts/wsdl is written or deleted.
    private ConfinedTree ArtifactTree() => new(_options.ResolvePath("."));

    private static bool IsFileSystemFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException
            or ArgumentException;

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>One side of the comparison: its name in paths and artifacts, its setting and its artifact file.</summary>
    private sealed record WsdlSide(string Name, string SettingName, string FileName);
}

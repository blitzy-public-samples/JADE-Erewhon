using System.Globalization;
using System.Security;
using System.Text;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Http;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Checks;

/// <summary>
/// Checks that the WSDL the facade publishes equals the backend's WSDL under XML semantic comparison,
/// with the single <c>soap:address/@location</c> value left uncompared: the facade rewrites only that
/// value per request, and the attribute must still be present on both sides. Comments are ignored, so the
/// stand-in label comment of <c>contracts/wsdl/ErewhonWebService.wsdl</c> never causes a difference.
/// </summary>
/// <remarks>
/// <para>Evaluation order, first match wins:</para>
/// <list type="number">
///   <item>No <c>contracts/wsdl/ErewhonWebService.wsdl</c> (capture obtained no WSDL): <c>CHK-WSDL-CONTRACT</c>
///   <c>not-run</c>, outcome <c>not-run</c>, with no request and no comparison.</item>
///   <item>A SOAP base URL from which no WSDL address can be formed: <c>not-run</c>, with no request.</item>
///   <item><c>GET</c> of the catalogue's WSDL path and query on <c>Harness:Backend:SoapBaseUrl</c> and on
///   <c>Harness:Target:SoapBaseUrl</c>, each within the per-request limit. Each answered body, whatever its
///   status, is saved byte for byte to <c>artifacts/wsdl/backend.wsdl</c> or <c>artifacts/wsdl/facade.wsdl</c>.</item>
///   <item>A side that was not answered gives <c>$response.&lt;side&gt;</c> with kind
///   <c>no-response:&lt;reason&gt;</c>; a side answered with a status other than 200 gives
///   <c>$status.&lt;side&gt;</c> with kind <c>value</c>.</item>
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
/// <param name="options">The harness settings: both SOAP base URLs, the request limit, the contract and artifact paths.</param>
/// <param name="catalogue">The operation catalogue, whose SOAP address supplies the WSDL path and query.</param>
/// <param name="comparer">The XML comparer; it is called with the WSDL context.</param>
/// <param name="http">The runner both documents are fetched with.</param>
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

    // The XML root path, in the convention of XmlSemanticComparer.
    private const string XmlRootPath = "/";

    // The baseline token of a no-response discrepancy: a response was expected on that side.
    private const string ExpectedResponse = "response";

    private const string AbsentContractDetail = "contracts WSDL absent; capture obtained no WSDL";
    private const string ArtifactSubdirectory = "wsdl";

    // Strict, so two documents that differ only in invalid byte sequences cannot both decode to U+FFFD
    // and compare equal; an undecodable side is reported as malformed instead.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Only for the literal text of an undecodable side, so the discrepancy can still show it.
    private static readonly UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly WsdlSide Backend = new("backend", "Harness:Backend:SoapBaseUrl", "backend.wsdl");
    private static readonly WsdlSide Facade = new("facade", "Harness:Target:SoapBaseUrl", "facade.wsdl");

    private readonly HarnessOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly string _wsdlPathAndQuery =
        (catalogue ?? throw new ArgumentNullException(nameof(catalogue))).Addresses.Soap.WsdlPathAndQuery;
    private readonly XmlSemanticComparer _comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
    private readonly CaseRunner _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>
    /// Fetches the backend and facade WSDLs, saves them as artifacts and compares them, unless the contracts
    /// WSDL is absent.
    /// </summary>
    /// <param name="ct">The caller's cancellation, which propagates as <see cref="OperationCanceledException"/>.</param>
    /// <returns>The check row, the failing item when the check did not pass, and the discrepancies of a difference.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The configured request limit exceeds <see cref="int.MaxValue"/> milliseconds.</exception>
    public async Task<ContractCheckResult> RunAsync(CancellationToken ct)
    {
        // File.Exists is false for a directory, which therefore also counts as absent. Artifacts left by an
        // earlier run would pass for this run's evidence, so they are removed on every path that fetches nothing.
        if (!File.Exists(_options.ContractsWsdlPath))
        {
            return NotRun(Join(AbsentContractDetail, RemoveArtifacts()));
        }

        // An unusable base URL is reported as not-run, as the OpenAPI drift check does for its admin URL:
        // with either address missing there is nothing the facade WSDL could be compared with.
        var addressProblems = new List<string>();
        var backendUri = Address(Backend, _options.Backend.SoapBaseUrl, addressProblems);
        var facadeUri = Address(Facade, _options.Target.SoapBaseUrl, addressProblems);
        if (backendUri is null || facadeUri is null)
        {
            return NotRun(Join(string.Join("; ", addressProblems), RemoveArtifacts()));
        }

        var timeout = CaseRunner.RequestTimeoutOf(_options);
        var responses = await Task.WhenAll(
                _http.GetAsync(backendUri, timeout, ct),
                _http.GetAsync(facadeUri, timeout, ct))
            .ConfigureAwait(false);
        var backend = responses[0];
        var facade = responses[1];

        var artifactNotes = new List<string>();
        AddNote(artifactNotes, await KeepArtifactAsync(Backend, backend, ct).ConfigureAwait(false));
        AddNote(artifactNotes, await KeepArtifactAsync(Facade, facade, ct).ConfigureAwait(false));
        var artifactNote = artifactNotes.Count == 0 ? null : string.Join("; ", artifactNotes);

        var discrepancies = new List<Discrepancy>();
        var fetchProblems = new List<string>();
        AddFetchDiscrepancies(Backend, backend, discrepancies, fetchProblems);
        AddFetchDiscrepancies(Facade, facade, discrepancies, fetchProblems);

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
            $"{discrepancies.Count} difference{(discrepancies.Count == 1 ? "" : "s")} between the backend WSDL ({backendUri}) "
            + $"and the facade WSDL ({facadeUri}); first at {first.Path} ({first.Kind})");
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
    private Uri? Address(WsdlSide side, string baseUrl, List<string> problems)
    {
        try
        {
            return CaseRunner.Combine(baseUrl, _wsdlPathAndQuery);
        }
        catch (ArgumentException ex)
        {
            // A null or blank base URL lands here too; ArgumentNullException derives from ArgumentException.
            problems.Add($"no {side.Name} WSDL address can be formed from {side.SettingName}: {ex.Message}");
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

    // The backend WSDL is the baseline side and the facade WSDL the observed side.
    private IReadOnlyList<Discrepancy> CompareDocuments(byte[] backendBody, byte[] facadeBody)
    {
        var baselineText = TryDecode(backendBody);
        var observedText = TryDecode(facadeBody);

        if (baselineText is null || observedText is null)
        {
            return
            [
                new Discrepancy(
                    XmlRootPath,
                    DiscrepancyKinds.Malformed,
                    baselineText is null ? DiscrepancyValue.Literal(LenientUtf8.GetString(WithoutBom(backendBody))) : DiscrepancyValue.Absent,
                    observedText is null ? DiscrepancyValue.Literal(LenientUtf8.GetString(WithoutBom(facadeBody))) : DiscrepancyValue.Absent),
            ];
        }

        return _comparer.Compare(baselineText, observedText, XmlComparisonContext.Wsdl(excludeSoapAddressLocation: true));
    }

    private static string? TryDecode(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(WithoutBom(bytes));
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
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, body, ct).ConfigureAwait(false);
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

            // File.Delete throws when the directory is absent, which here just means there is nothing to remove.
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return null;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return $"earlier {side.Name} WSDL {path ?? "artifacts/wsdl"} not removed: {Describe(ex)}";
        }
    }

    private string ArtifactPath(WsdlSide side) =>
        Path.Combine(_options.ArtifactsDirectory, ArtifactSubdirectory, side.FileName);

    private static bool IsFileSystemFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException
            or ArgumentException;

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>One side of the comparison: its name in paths and artifacts, its setting and its artifact file.</summary>
    private sealed record WsdlSide(string Name, string SettingName, string FileName);
}

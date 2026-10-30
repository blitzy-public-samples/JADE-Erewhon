using System.Globalization;
using System.Security;
using System.Text;
using Erewhon.Contracts.Admin;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Http;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Checks;

/// <summary>
/// The outcome of one contract check (OpenAPI drift or WSDL contract): the check row for the report, the
/// failing item when the check did not pass, and the discrepancies behind a <c>d:&lt;signature&gt;</c>
/// nature, which the caller writes to <c>artifacts/discrepancies/&lt;itemId&gt;.json</c> through
/// <see cref="CanonicalDiscrepancies.ToArtifact(FailureItem)"/> on
/// <see cref="Canonical"/>, so the list is not prepared a second time.
/// </summary>
/// <param name="Result">The check row keyed by its <see cref="CheckNames"/> value.</param>
/// <param name="Failure">
/// The failing item (<c>missing</c>, <c>not-run</c> or <c>d:&lt;signature&gt;</c>); null when the check passed.
/// </param>
/// <param name="Discrepancies">
/// The differences the signature was computed over, in <see cref="Discrepancy.CanonicalOrder"/>; empty unless
/// the nature is <c>d:&lt;signature&gt;</c>.
/// </param>
public sealed record ContractCheckResult(CheckResult Result, FailureItem? Failure, IReadOnlyList<Discrepancy> Discrepancies)
{
    /// <summary>The check row; never null.</summary>
    public CheckResult Result { get; init; } = Result ?? throw new ArgumentNullException(nameof(Result));

    /// <summary>The differences behind a <c>d:</c> nature; never null.</summary>
    public IReadOnlyList<Discrepancy> Discrepancies { get; init; } =
        Discrepancies ?? throw new ArgumentNullException(nameof(Discrepancies));

    /// <summary>
    /// The prepared list behind a <c>d:</c> nature, whose <see cref="CanonicalDiscrepancies.Entries"/> are
    /// <see cref="Discrepancies"/> and whose signature is the nature's; null for every other result.
    /// </summary>
    public CanonicalDiscrepancies? Canonical { get; init; }
}

/// <summary>
/// Checks that the facade's live OpenAPI document equals the checked-in
/// <c>contracts/openapi/erewhon-rest.openapi.json</c> under JSON semantic comparison: member order and
/// whitespace are irrelevant, array order is significant, numbers compare by value and strings exactly.
/// The comparison is untyped, so neither normalisation rule applies.
/// </summary>
/// <remarks>
/// <para>Evaluation order, first match wins:</para>
/// <list type="number">
///   <item><c>GET &lt;Target.AdminUrl&gt;/openapi/v1.json</c> within the per-request limit. An answered response,
///   whatever its status, is saved byte for byte to <c>artifacts/openapi/live.json</c>. A document over
///   <see cref="HarnessOptions.EvidenceByteLimit"/> is refused unread: <c>live.json</c> is removed and the
///   refusal propagates.</item>
///   <item>No response, a status other than 200, or an unusable admin URL: <c>CHK-OPENAPI-DRIFT</c>
///   <c>not-run</c>, outcome <c>not-run</c>.</item>
///   <item>No checked-in file: <c>missing</c>, outcome <c>fail</c>.</item>
///   <item>Differences: <c>d:&lt;signature&gt;</c>, outcome <c>fail</c>; none: pass.</item>
/// </list>
/// <para>The check writes only <c>artifacts/openapi/live.json</c>. It never creates, rewrites or regenerates
/// the checked-in contract, which only <c>harness export-openapi</c> writes, and never touches
/// <c>baseline/**</c>. The discrepancy artifact is written by the caller from
/// <see cref="ContractCheckResult.Canonical"/> through
/// <see cref="CanonicalDiscrepancies.ToArtifact(FailureItem)"/>. A failure to save the
/// artifact is reported in the detail and never thrown.</para>
/// <para>The instance keeps no per-run state, but concurrent runs would race on the artifact file, so one
/// run at a time is expected. The <see cref="CaseRunner"/> belongs to the caller and is not disposed here.</para>
/// </remarks>
/// <param name="options">The harness settings: the facade admin URL, the request limit, the contract and artifact paths.</param>
/// <param name="comparer">The JSON comparer; it is called untyped.</param>
/// <param name="http">The runner the document is fetched with.</param>
/// <exception cref="ArgumentNullException">An argument is null.</exception>
public sealed class OpenApiDriftCheck(HarnessOptions options, JsonSemanticComparer comparer, CaseRunner http)
{
    private const int ExpectedStatus = 200;

    // The body as a whole, in the convention every comparer in Compare/ uses: a document that is not UTF-8 has
    // no JSON tree, and no JSON path is this one, so its entries never match those JsonSemanticComparer reports.
    private const string BodyPath = "$body";

    private const string ArtifactSubdirectory = "openapi";
    private const string LiveDocumentFileName = "live.json";

    // Strict, so two documents that differ only in invalid byte sequences cannot both decode to U+FFFD
    // and compare equal; an undecodable side is reported as malformed instead.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HarnessOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly JsonSemanticComparer _comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
    private readonly CaseRunner _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Fetches the live document, saves it as an artifact and compares it with the checked-in contract.</summary>
    /// <param name="ct">The caller's cancellation, which propagates as <see cref="OperationCanceledException"/>.</param>
    /// <returns>The check row, the failing item when the check did not pass, and the discrepancies of a difference.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="IOException">
    /// The checked-in contract, or a directory on its way below the repository root, is a symbolic link; the
    /// contract is not a regular file or exceeds <see cref="HarnessOptions.EvidenceByteLimit"/>; or it exists but
    /// cannot be read.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The checked-in contract exists but may not be read.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The operating system is not Linux, or the processor architecture's Linux open flags are unknown, so the
    /// contract cannot be read confined to the repository root.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The live document exceeds <see cref="HarnessOptions.EvidenceByteLimit"/>, as <see cref="CaseRunner"/>
    /// refuses it, or its comparison with the checked-in contract would retain more discrepancy evidence than
    /// the <see cref="JsonSemanticComparer"/> allows.
    /// </exception>
    public async Task<ContractCheckResult> RunAsync(CancellationToken ct)
    {
        Uri documentUri;
        try
        {
            documentUri = CaseRunner.Combine(_options.Target.AdminUrl, AdminRoutes.OpenApiDocument);
        }
        catch (ArgumentException ex)
        {
            // A blank AdminUrl lands here too: there is no facade admin listener to fetch from.
            return NotRun(Join($"Harness:Target:AdminUrl is not usable: {ex.Message}", RemoveLiveDocument()));
        }

        CaseResponse response;
        try
        {
            response = await _http
                .GetAsync(documentUri, CaseRunner.RequestTimeoutOf(_options), ct)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // An oversized document is refused unread, so a live.json left by an earlier run would pass for its
            // evidence; it is removed before the refusal propagates, and a failed removal never replaces it.
            _ = RemoveLiveDocument();
            throw;
        }

        // A live.json left by an earlier run would pass for this run's evidence, so it is removed when
        // nothing was answered.
        var artifactNote = response.Answered
            ? await SaveLiveDocumentAsync(response.Body, ct).ConfigureAwait(false)
            : RemoveLiveDocument();

        if (!response.Answered)
        {
            var reason = response.Detail is null ? "" : $"; {response.Detail}";
            return NotRun(Join($"no response from {documentUri} (no-response:{response.NoResponse}{reason})", artifactNote));
        }

        if (response.Status != ExpectedStatus)
        {
            return NotRun(Join(
                string.Create(CultureInfo.InvariantCulture, $"{documentUri} answered status {response.Status}, not {ExpectedStatus}"),
                artifactNote));
        }

        var contractPath = _options.ContractsOpenApiPath;
        var contractName = DisplayPath(contractPath);
        var checkedIn = await TryReadContractAsync(contractPath, ct).ConfigureAwait(false);
        if (checkedIn is null)
        {
            return Failed(
                FailureItemIds.Missing,
                Join($"checked-in {contractName} is absent; replay never creates it, harness export-openapi generates it", artifactNote),
                canonical: null);
        }

        var discrepancies = CompareDocuments(checkedIn, response.Body);
        if (discrepancies.Count == 0)
        {
            return new ContractCheckResult(
                new CheckResult(CheckNames.OpenApiDrift, CheckOutcomes.Pass, Cases: 1, Passed: 1, artifactNote),
                Failure: null,
                []);
        }

        // Sorted, written and hashed once; the caller writes the artifact from the same result.
        var canonical = DiscrepancySignature.Prepare(discrepancies);
        var first = canonical.Entries[0];
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{discrepancies.Count} difference{(discrepancies.Count == 1 ? "" : "s")} from {contractName}; first at {first.Path} ({first.Kind})");
        return Failed(
            FailureItemIds.Diff(canonical.Signature),
            Join(summary, artifactNote),
            canonical);
    }

    private static ContractCheckResult NotRun(string detail) =>
        new(
            new CheckResult(CheckNames.OpenApiDrift, CheckOutcomes.NotRun, Cases: 0, Passed: 0, detail),
            new FailureItem(FailureItemIds.OpenApiDrift, FailureItemIds.NotRun, Detail: detail),
            []);

    private static ContractCheckResult Failed(string nature, string detail, CanonicalDiscrepancies? canonical) =>
        new(
            new CheckResult(CheckNames.OpenApiDrift, CheckOutcomes.Fail, Cases: 1, Passed: 0, detail),
            new FailureItem(FailureItemIds.OpenApiDrift, nature, Detail: detail),
            canonical?.Entries ?? [])
        {
            Canonical = canonical,
        };

    private static string Join(string detail, string? note) => note is null ? detail : $"{detail}; {note}";

    // The checked-in contract is the baseline side and the live document the observed side.
    private List<Discrepancy> CompareDocuments(byte[] checkedIn, byte[] live)
    {
        var baselineBody = WithoutBom(checkedIn);
        var observedBody = WithoutBom(live);
        var baselineText = TryDecode(baselineBody);
        var observedText = TryDecode(observedBody);

        List<Discrepancy> discrepancies;
        if (baselineText is null || observedText is null)
        {
            discrepancies = UndecodableDiscrepancies(baselineBody, baselineText, observedBody, observedText);
        }
        else
        {
            discrepancies = [.. _comparer.Compare(baselineText, observedText, declaredType: null)];
        }

        // Unsorted: DiscrepancySignature.Prepare puts the list into canonical order once.
        return discrepancies;
    }

    /// <summary>
    /// The discrepancies at <c>$body</c>, the body as a whole, when at least one document is not valid UTF-8.
    /// Such a document has no JSON tree, and <c>$body</c> is never a path the JSON comparer reports, so these
    /// entries cannot equal its <c>malformed</c> entry at <c>$</c> for decodable text that is not JSON, such as
    /// the text <c>ff</c>. An undecodable document is the lowercase hexadecimal of its bytes after the
    /// byte-order mark, which keeps every byte where replacement decoding would merge, for example, 0xFF and
    /// 0xFE into one character. The <c>malformed</c> entry holds that value on each side that does not decode
    /// and nothing on a side that does, so it names the failing side. When the other document decodes, a
    /// <c>type</c> entry also holds both documents, the decodable one as its exact text, so a change to either
    /// document changes the signature.
    /// </summary>
    /// <param name="baselineBody">The checked-in bytes after the byte-order mark.</param>
    /// <param name="baselineText">The decoded checked-in text; null when it is not valid UTF-8.</param>
    /// <param name="observedBody">The live bytes after the byte-order mark.</param>
    /// <param name="observedText">The decoded live text; null when it is not valid UTF-8.</param>
    private static List<Discrepancy> UndecodableDiscrepancies(
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

    // The contract is read through directory handles anchored at the repository root, as the live document is
    // written: a missing directory on the way, a missing file or a directory at the name, a file removed before
    // the read included, is missing. A link on the way or at the name, a FIFO, a socket, a device or a file over
    // HarnessOptions.EvidenceByteLimit is refused at once rather than followed or waited on, and the refusal
    // propagates, as an unreadable contract does.
    private async Task<byte[]?> TryReadContractAsync(string path, CancellationToken ct) =>
        await new ConfinedTree(_options.ResolvePath("."))
            .ReadFileAsync(path, HarnessOptions.EvidenceByteLimit, ct)
            .ConfigureAwait(false);

    private async Task<string?> SaveLiveDocumentAsync(byte[] body, CancellationToken ct)
    {
        string? path = null;
        try
        {
            path = LiveDocumentPath();
            await ArtifactTree().WriteFileAsync(path, body, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            // A partly written file would misstate the response, so it is removed as well.
            var removal = path is null ? null : RemoveLiveDocument();
            return Join($"live document not saved to {path ?? "artifacts/openapi"}: {Describe(ex)}", removal);
        }
    }

    private string? RemoveLiveDocument()
    {
        string? path = null;
        try
        {
            path = LiveDocumentPath();

            // An absent directory or file is nothing to remove, and a directory in the file's place is kept.
            ArtifactTree().DeleteFile(path);
            return null;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return $"earlier live document {path ?? "artifacts/openapi"} not removed: {Describe(ex)}";
        }
    }

    private string LiveDocumentPath() =>
        Path.Combine(_options.ArtifactsDirectory, ArtifactSubdirectory, LiveDocumentFileName);

    // The live document is saved and removed through directory handles anchored at the repository root, so a
    // link at artifacts/ or artifacts/openapi refuses the call and a link at live.json is never written through:
    // nothing outside artifacts/openapi is written or deleted.
    private ConfinedTree ArtifactTree() => new(_options.ResolvePath("."));

    // Repository-relative, so the report reads the same under Compose (/repo) and in local-process mode.
    private string DisplayPath(string path) =>
        Path.GetRelativePath(_options.ResolvePath("."), path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsFileSystemFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException
            or ArgumentException;

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Compare;

namespace Erewhon.Harness.Report;

/// <summary>
/// One failing item of a harness run. Its identity is the pair (<see cref="ItemId"/>,
/// <see cref="Nature"/>): the known-limitations register documents an item exactly when one of its
/// entries lists that pair, compared ordinally. <see cref="Operation"/> and <see cref="Detail"/> are
/// report context only and never take part in matching.
/// </summary>
/// <remarks>
/// Build ids and natures with <see cref="FailureItemIds"/> so every component spells them the same
/// way. Record equality compares all four members; matching against the register is done by
/// <c>KnownLimitationsRegister</c> on the pair alone. Instances are immutable.
/// </remarks>
/// <param name="ItemId">
/// The stable identity of the failing item, for example <c>CASE:REST-getAgent-01</c>,
/// <c>CONF:Soap:DTOResult.allWarnings</c>, <c>TEST:&lt;class&gt;/&lt;display name&gt;</c> or
/// <c>CHK-SHUTDOWN:facade</c>; never blank.
/// </param>
/// <param name="Nature">
/// The exact nature of the failure, for example <c>d:&lt;signature&gt;</c>, <c>not-captured</c> or
/// <c>still-running</c>; never blank.
/// </param>
/// <param name="Operation">
/// The catalogue id of the operation the item belongs to, such as <c>Rest.getAgent</c> or
/// <c>Soap.getPhoto</c>; null when the item belongs to no single operation.
/// </param>
/// <param name="Detail">Human-readable context for the report; null when there is none.</param>
public sealed record FailureItem(string ItemId, string Nature, string? Operation = null, string? Detail = null)
{
    // The init accessors validate again, so a `with` expression cannot blank the identity either.
    private readonly string _itemId = Required(ItemId, nameof(ItemId));
    private readonly string _nature = Required(Nature, nameof(Nature));

    /// <summary>The stable identity of the failing item; never blank.</summary>
    /// <exception cref="ArgumentException">The value is null, empty or whitespace.</exception>
    public string ItemId
    {
        get => _itemId;
        init => _itemId = Required(value, nameof(ItemId));
    }

    /// <summary>The exact nature of the failure; never blank.</summary>
    /// <exception cref="ArgumentException">The value is null, empty or whitespace.</exception>
    public string Nature
    {
        get => _nature;
        init => _nature = Required(value, nameof(Nature));
    }

    /// <summary>
    /// True for the three kinds of item no register entry can ever document: a failed report write
    /// (<c>CHK-REPORT</c>), an unhandled exception (<c>CHK-EXCEPTION</c>) and a component that did not
    /// shut down (<c>CHK-SHUTDOWN:&lt;component&gt;</c>). Any such item makes the harness exit 1.
    /// </summary>
    [JsonIgnore]
    public bool IsNeverDocumentable =>
        string.Equals(ItemId, FailureItemIds.Report, StringComparison.Ordinal)
        || string.Equals(ItemId, FailureItemIds.Exception, StringComparison.Ordinal)
        || string.Equals(ItemId, FailureItemIds.ShutdownRoot, StringComparison.Ordinal)
        || ItemId.StartsWith(FailureItemIds.ShutdownPrefix, StringComparison.Ordinal);

    /// <summary>True when the item is an equivalence case, <c>CASE:&lt;caseId&gt;</c>.</summary>
    [JsonIgnore]
    public bool IsCaseItem => ItemId.StartsWith(FailureItemIds.CasePrefix, StringComparison.Ordinal);

    /// <summary>Returns <c>&lt;ItemId&gt; [&lt;Nature&gt;]</c>.</summary>
    public override string ToString() => $"{ItemId} [{Nature}]";

    private static string Required(string? value, string memberName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"A failure item needs a non-blank {memberName}.", memberName)
            : value;
}

/// <summary>
/// The item ids and natures of every failing item the harness reports, spelled exactly as the
/// known-limitations register lists them.
/// </summary>
/// <remarks>
/// <list type="table">
///   <listheader><term>Item</term><description>Id, then natures</description></listheader>
///   <item><term>Equivalence case</term><description><c>CASE:&lt;caseId&gt;</c>; <c>d:&lt;signature&gt;</c>, or <c>not-captured</c> for a case the stand-in could not answer at capture.</description></item>
///   <item><term>Conformance member</term><description><c>CONF:&lt;Rest|Soap&gt;:&lt;Class&gt;.&lt;property&gt;</c> or <c>CONF:&lt;Rest|Soap&gt;:STATUS:&lt;constant&gt;</c>; <c>unreachable-in-source</c>, <c>unobserved</c>, <c>null-only</c>, <c>wrong-type:&lt;observed type&gt;</c>.</description></item>
///   <item><term>Seed-token assertion</term><description><c>CONF:SEED:&lt;assertionId&gt;</c>; <c>differs:&lt;observed value&gt;</c>.</description></item>
///   <item><term>Contract check</term><description><c>CHK-OPENAPI-DRIFT</c>, <c>CHK-WSDL-CONTRACT</c>; <c>missing</c>, <c>not-run</c>, <c>d:&lt;signature&gt;</c>.</description></item>
///   <item><term>Facade test</term><description><c>TEST:&lt;className&gt;/&lt;testName&gt;</c>; <c>failed:&lt;signature&gt;</c>, <c>not-run</c>. <c>CHK-FACADE-TRX</c>; <c>missing</c>.</description></item>
///   <item><term>JADE unchanged</term><description><c>CHK-JADE-UNCHANGED:&lt;path&gt;</c>; <c>modified:&lt;blob id&gt;</c>, <c>added:&lt;blob id&gt;</c>, <c>deleted</c>. <c>CHK-JADE-UNCHANGED</c>; <c>not-run</c>.</description></item>
///   <item><term>Ordering</term><description><c>CHK-ORDERING</c>; <c>not-ancestor</c>, <c>no-facade-commit</c>, <c>not-run</c>.</description></item>
///   <item><term>Lifecycle</term><description><c>CHK-READY:&lt;component&gt;</c> <c>timeout</c>; <c>CHK-RESET:&lt;point&gt;</c> <c>mismatch:&lt;field&gt;:&lt;observed&gt;</c>; <c>CHK-SHUTDOWN:&lt;component&gt;</c> <c>still-running</c>; <c>CHK-REPORT</c> <c>write-failed</c>; <c>CHK-EXCEPTION</c> <c>&lt;exception type&gt;</c>.</description></item>
/// </list>
/// Builders reject a null part. Parts that come from fixed names (case ids, components, catalogue
/// names, signatures) must also be non-blank; observed values are kept exactly, empty included.
/// </remarks>
public static class FailureItemIds
{
    /// <summary>The prefix of an equivalence case item, <c>CASE:</c>.</summary>
    public const string CasePrefix = "CASE:";

    /// <summary>The prefix of a component shutdown item, <c>CHK-SHUTDOWN:</c>.</summary>
    public const string ShutdownPrefix = ShutdownRoot + ":";

    /// <summary>The OpenAPI drift check, <c>CHK-OPENAPI-DRIFT</c>.</summary>
    public const string OpenApiDrift = "CHK-OPENAPI-DRIFT";

    /// <summary>The WSDL contract check, <c>CHK-WSDL-CONTRACT</c>.</summary>
    public const string WsdlContract = "CHK-WSDL-CONTRACT";

    /// <summary>The facade tests' TRX file, <c>CHK-FACADE-TRX</c>; its only nature is <c>missing</c>.</summary>
    public const string FacadeTrx = "CHK-FACADE-TRX";

    /// <summary>The JADE-unchanged check as a whole, <c>CHK-JADE-UNCHANGED</c>; its only nature is <c>not-run</c>.</summary>
    public const string JadeUnchanged = "CHK-JADE-UNCHANGED";

    /// <summary>The ordering-evidence check, <c>CHK-ORDERING</c>.</summary>
    public const string Ordering = "CHK-ORDERING";

    /// <summary>A failed report write, <c>CHK-REPORT</c>; never documentable.</summary>
    public const string Report = "CHK-REPORT";

    /// <summary>An unhandled exception, <c>CHK-EXCEPTION</c>; never documentable.</summary>
    public const string Exception = "CHK-EXCEPTION";

    /// <summary>The nature of a case listed in the manifest's <c>notCaptured</c>.</summary>
    public const string NotCaptured = "not-captured";

    /// <summary>The nature of an absent contract file or TRX file.</summary>
    public const string Missing = "missing";

    /// <summary>The nature of a check or test that did not run.</summary>
    public const string NotRun = "not-run";

    /// <summary>The nature of a path deleted under <c>Erewhon/</c>.</summary>
    public const string Deleted = "deleted";

    /// <summary>The nature of an ordering check whose baseline commit is not an ancestor of the first facade commit.</summary>
    public const string NotAncestor = "not-ancestor";

    /// <summary>The nature of an ordering check that found no commit adding a <c>facade/</c> path.</summary>
    public const string NoFacadeCommit = "no-facade-commit";

    /// <summary>The nature of a component that never became ready.</summary>
    public const string Timeout = "timeout";

    /// <summary>The nature of a component still answering at the shutdown limit, or whose shutdown call failed.</summary>
    public const string StillRunning = "still-running";

    /// <summary>The nature of a report that could not be written.</summary>
    public const string WriteFailed = "write-failed";

    /// <summary>The nature of a conformance member no in-scope method body can produce.</summary>
    public const string UnreachableInSource = "unreachable-in-source";

    /// <summary>The nature of a reachable conformance member that no captured case returns.</summary>
    public const string Unobserved = "unobserved";

    /// <summary>The nature of a conformance member seen only as null, so its declared type is unverified.</summary>
    public const string NullOnly = "null-only";

    /// <summary>
    /// The bare shutdown id. Builders always add a component, but an item carrying the bare id is
    /// still never documentable.
    /// </summary>
    internal const string ShutdownRoot = "CHK-SHUTDOWN";

    private const string ConformancePrefix = "CONF:";
    private const string ConformanceStatusInfix = ":STATUS:";
    private const string SeedPrefix = "CONF:SEED:";
    private const string TestPrefix = "TEST:";
    private const string JadeUnchangedPrefix = JadeUnchanged + ":";
    private const string ReadyPrefix = "CHK-READY:";
    private const string ResetPrefix = "CHK-RESET:";

    private const string DiffPrefix = "d:";
    private const string FailedPrefix = "failed:";
    private const string ModifiedPrefix = "modified:";
    private const string AddedPrefix = "added:";
    private const string MismatchPrefix = "mismatch:";
    private const string DiffersPrefix = "differs:";
    private const string WrongTypePrefix = "wrong-type:";

    private const string RestCasePrefix = "REST";
    private const string SoapCasePrefix = "SOAP";

    /// <summary>Returns the equivalence case id <c>CASE:&lt;caseId&gt;</c>.</summary>
    /// <param name="caseId">The case id from the case matrix, for example <c>REST-getAgent-01</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="caseId"/> is null or blank.</exception>
    public static string Case(string caseId) => CasePrefix + NonBlank(caseId);

    /// <summary>Returns the conformance member id <c>CONF:&lt;Rest|Soap&gt;:&lt;dtoClass&gt;.&lt;property&gt;</c>.</summary>
    /// <param name="surface">The surface the member is checked on.</param>
    /// <param name="dtoClass">The DTO class name exactly as declared, for example <c>DTOItem</c>.</param>
    /// <param name="property">The property name exactly as declared, for example <c>categoryPrefix</c>.</param>
    /// <exception cref="ArgumentException">A name is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="surface"/> is not a defined surface.</exception>
    public static string Conformance(Surface surface, string dtoClass, string property) =>
        $"{ConformancePrefix}{SurfaceToken(surface)}:{NonBlank(dtoClass)}.{NonBlank(property)}";

    /// <summary>Returns the conformance status id <c>CONF:&lt;Rest|Soap&gt;:STATUS:&lt;constant&gt;</c>.</summary>
    /// <param name="surface">The surface that declares the constant.</param>
    /// <param name="constant">The constant name exactly as declared, for example <c>HTTP_Conflict</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="constant"/> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="surface"/> is not a defined surface.</exception>
    public static string ConformanceStatus(Surface surface, string constant) =>
        $"{ConformancePrefix}{SurfaceToken(surface)}{ConformanceStatusInfix}{NonBlank(constant)}";

    /// <summary>Returns the seed-token assertion id <c>CONF:SEED:&lt;assertionId&gt;</c>.</summary>
    /// <param name="assertionId">The assertion id.</param>
    /// <exception cref="ArgumentException"><paramref name="assertionId"/> is null or blank.</exception>
    public static string Seed(string assertionId) => SeedPrefix + NonBlank(assertionId);

    /// <summary>
    /// Returns the facade test id <c>TEST:&lt;className&gt;/&lt;testName&gt;</c>. The test name is the TRX
    /// display name, which includes theory arguments, so each theory invocation is a distinct item.
    /// </summary>
    /// <remarks>
    /// Empty parts are accepted: a class name derived from a display name without a dot is empty, and
    /// the failing test must still be reported rather than lost to an exception.
    /// </remarks>
    /// <param name="className">The test class name from the TRX.</param>
    /// <param name="testName">The test display name from the TRX, kept exactly.</param>
    /// <exception cref="ArgumentNullException">A part is null.</exception>
    public static string Test(string className, string testName) =>
        $"{TestPrefix}{NonNull(className)}/{NonNull(testName)}";

    /// <summary>Returns the JADE-unchanged path id <c>CHK-JADE-UNCHANGED:&lt;path&gt;</c>.</summary>
    /// <param name="path">The repository path exactly as git prints it, with forward slashes.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    public static string JadeUnchangedPath(string path) => JadeUnchangedPrefix + NonEmpty(path);

    /// <summary>Returns the readiness id <c>CHK-READY:&lt;component&gt;</c>.</summary>
    /// <param name="component">The component name, for example <c>facade</c> or <c>standin</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="component"/> is null or blank.</exception>
    public static string Ready(string component) => ReadyPrefix + NonBlank(component);

    /// <summary>Returns the state-reset id <c>CHK-RESET:&lt;point&gt;</c>.</summary>
    /// <param name="point">The point in the run where the reset was confirmed.</param>
    /// <exception cref="ArgumentException"><paramref name="point"/> is null or blank.</exception>
    public static string Reset(string point) => ResetPrefix + NonBlank(point);

    /// <summary>Returns the shutdown id <c>CHK-SHUTDOWN:&lt;component&gt;</c>; such an item is never documentable.</summary>
    /// <param name="component">The component name, for example <c>facade</c> or <c>standin</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="component"/> is null or blank.</exception>
    public static string Shutdown(string component) => ShutdownPrefix + NonBlank(component);

    /// <summary>Returns the case id of a <c>CASE:&lt;caseId&gt;</c> item id.</summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="caseId">The case id; empty when the method returns false.</param>
    /// <returns>True when <paramref name="itemId"/> starts with <c>CASE:</c> and names a case.</returns>
    public static bool TryGetCaseId(string? itemId, out string caseId)
    {
        if (itemId is not null
            && itemId.Length > CasePrefix.Length
            && itemId.StartsWith(CasePrefix, StringComparison.Ordinal))
        {
            caseId = itemId[CasePrefix.Length..];
            return true;
        }

        caseId = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns the catalogue operation id a case id belongs to: <c>REST-&lt;jadeMethod&gt;-&lt;NN&gt;</c>
    /// gives <c>Rest.&lt;jadeMethod&gt;</c> and <c>SOAP-&lt;jadeMethod&gt;-&lt;NN&gt;</c> gives
    /// <c>Soap.&lt;jadeMethod&gt;</c>.
    /// </summary>
    /// <param name="caseId">The case id, for example <c>SOAP-getPhoto-03</c>.</param>
    /// <returns>
    /// The operation id, or null when the id has any other shape: another prefix, a method name that is
    /// not an identifier, or a suffix that is not all digits.
    /// </returns>
    public static string? OperationIdFromCaseId(string? caseId)
    {
        if (string.IsNullOrEmpty(caseId))
        {
            return null;
        }

        var first = caseId.IndexOf('-', StringComparison.Ordinal);
        var last = caseId.LastIndexOf('-');
        if (first <= 0 || last <= first + 1 || last == caseId.Length - 1)
        {
            return null;
        }

        var surface = caseId.AsSpan(0, first) switch
        {
            RestCasePrefix => SurfaceToken(Surface.Rest),
            SoapCasePrefix => SurfaceToken(Surface.Soap),
            _ => null,
        };

        var method = caseId.AsSpan(first + 1, last - first - 1);
        var number = caseId.AsSpan(last + 1);
        if (surface is null || !IsIdentifier(method) || number.ContainsAnyExceptInRange('0', '9'))
        {
            return null;
        }

        return string.Concat(surface, ".", method);
    }

    /// <summary>Returns the discrepancy nature <c>d:&lt;signature&gt;</c>.</summary>
    /// <param name="signature">The <see cref="DiscrepancySignature.Compute"/> value.</param>
    /// <exception cref="ArgumentException"><paramref name="signature"/> is null or blank.</exception>
    public static string Diff(string signature) => DiffPrefix + NonBlank(signature);

    /// <summary>Returns the failed-test nature <c>failed:&lt;signature&gt;</c>.</summary>
    /// <param name="signature">
    /// The <see cref="DiscrepancySignature.Hash16"/> value over the exception type, the failing
    /// assertion's file and line, and the assertion message.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="signature"/> is null or blank.</exception>
    public static string Failed(string signature) => FailedPrefix + NonBlank(signature);

    /// <summary>Returns the nature <c>modified:&lt;blobId&gt;</c> of a path changed under <c>Erewhon/</c>.</summary>
    /// <param name="blobId">The git blob id of the current content.</param>
    /// <exception cref="ArgumentException"><paramref name="blobId"/> is null or blank.</exception>
    public static string Modified(string blobId) => ModifiedPrefix + NonBlank(blobId);

    /// <summary>Returns the nature <c>added:&lt;blobId&gt;</c> of a path added under <c>Erewhon/</c>.</summary>
    /// <param name="blobId">The git blob id of the added content.</param>
    /// <exception cref="ArgumentException"><paramref name="blobId"/> is null or blank.</exception>
    public static string Added(string blobId) => AddedPrefix + NonBlank(blobId);

    /// <summary>Returns the state-reset nature <c>mismatch:&lt;field&gt;:&lt;observed&gt;</c>.</summary>
    /// <param name="field">The <c>/admin/info</c> field that did not match, for example <c>seedSha256</c>.</param>
    /// <param name="observed">The observed value, kept exactly; empty is allowed.</param>
    /// <exception cref="ArgumentException"><paramref name="field"/> is null or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="observed"/> is null.</exception>
    public static string Mismatch(string field, string observed) =>
        $"{MismatchPrefix}{NonBlank(field)}:{NonNull(observed)}";

    /// <summary>Returns the seed-token nature <c>differs:&lt;observed&gt;</c>.</summary>
    /// <param name="observed">The observed value exactly, untrimmed; empty is allowed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="observed"/> is null.</exception>
    public static string Differs(string observed) => DiffersPrefix + NonNull(observed);

    /// <summary>Returns the conformance nature <c>wrong-type:&lt;observedType&gt;</c>.</summary>
    /// <param name="observedType">The wire type the member was observed with.</param>
    /// <exception cref="ArgumentException"><paramref name="observedType"/> is null or blank.</exception>
    public static string WrongType(string observedType) => WrongTypePrefix + NonBlank(observedType);

    /// <summary>Returns the nature of a <c>CHK-EXCEPTION</c> item: the exception's full type name.</summary>
    /// <param name="ex">The unhandled exception.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ex"/> is null.</exception>
    public static string ExceptionNature(System.Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var type = ex.GetType();
        return type.FullName ?? type.Name;
    }

    // An explicit switch rather than Surface.ToString(), so renaming an enum member cannot change an
    // id the register already lists.
    private static string SurfaceToken(Surface surface) => surface switch
    {
        Surface.Rest => "Rest",
        Surface.Soap => "Soap",
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Not a defined surface."),
    };

    private static bool IsIdentifier(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || !char.IsAsciiLetter(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static string NonBlank([NotNull] string? value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value;
    }

    private static string NonEmpty([NotNull] string? value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, paramName);
        return value;
    }

    private static string NonNull([NotNull] string? value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value;
    }
}

/// <summary>
/// The failure signature of a list of discrepancies: the first 16 lowercase hex digits of the SHA-256
/// of the list's canonical JSON text. The stand-in is deterministic and the facade forwards unchanged,
/// so the same discrepancies give the same signature on every run, and any change to them (a value,
/// an extra entry, a valid value turning literal) gives a new one.
/// </summary>
/// <remarks>
/// The canonical text is a compact JSON array of
/// <c>{"path":…,"kind":…,"baseline":&lt;token&gt;,"observed":&lt;token&gt;}</c> objects, members in that
/// order. A token is <c>{"valid":"N1"}</c> or <c>{"valid":"N2"}</c> for a value valid under a
/// normalisation rule and <c>{"literal":&lt;exact text&gt;}</c> otherwise, with JSON <c>null</c> as the
/// literal of an absent value, so a literal can never collide with a normalised value. Entries are
/// sorted ordinally by path, then kind, then the baseline token's text, then the observed token's text,
/// which is the same order as <see cref="Discrepancy.CanonicalOrder"/>; permuted input therefore gives
/// the same text. The encoder is fixed here, so the text does not depend on any caller's options.
/// All members are pure and thread-safe.
/// </remarks>
public static class DiscrepancySignature
{
    private const int SignatureBytes = 8;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Returns the canonical JSON text of <paramref name="discrepancies"/>; <c>[]</c> for none.</summary>
    /// <param name="discrepancies">The discrepancies in any order; null is treated as empty.</param>
    /// <exception cref="ArgumentException">The list contains a null entry.</exception>
    public static string CanonicalJson(IEnumerable<Discrepancy>? discrepancies)
    {
        var entries = Canonicalise(discrepancies);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("path", entry.Discrepancy.Path);
                writer.WriteString("kind", entry.Discrepancy.Kind);
                writer.WritePropertyName("baseline");
                WriteToken(writer, entry.Discrepancy.Baseline);
                writer.WritePropertyName("observed");
                WriteToken(writer, entry.Discrepancy.Observed);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Returns the first 16 lowercase hex digits of the SHA-256 of <paramref name="canonicalText"/>'s
    /// UTF-8 bytes. Callers with their own canonical text, such as a failed test's exception type,
    /// assertion location and message, hash it here so every signature has the same form.
    /// </summary>
    /// <param name="canonicalText">The canonical text to hash.</param>
    /// <exception cref="ArgumentNullException"><paramref name="canonicalText"/> is null.</exception>
    public static string Hash16(string canonicalText)
    {
        ArgumentNullException.ThrowIfNull(canonicalText);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText)), 0, SignatureBytes);
    }

    /// <summary>Returns the signature of <paramref name="discrepancies"/>; an empty list hashes <c>[]</c>.</summary>
    /// <param name="discrepancies">The discrepancies in any order; null is treated as empty.</param>
    /// <exception cref="ArgumentException">The list contains a null entry.</exception>
    public static string Compute(IEnumerable<Discrepancy>? discrepancies) => Hash16(CanonicalJson(discrepancies));

    /// <summary>
    /// Returns the discrepancy artifact of a failing item: <c>itemId</c>, <c>nature</c>,
    /// <c>operation</c>, <c>detail</c>, <c>signature</c> and <c>discrepancies</c>, the last being the
    /// canonical list the signature is computed over. The caller writes it to
    /// <c>artifacts/discrepancies/&lt;SanitiseFileName(itemId)&gt;.json</c>.
    /// </summary>
    /// <param name="item">The failing item the discrepancies belong to.</param>
    /// <param name="discrepancies">The discrepancies in any order; null is treated as empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    /// <exception cref="ArgumentException">The list contains a null entry.</exception>
    public static JsonNode ToArtifact(FailureItem item, IEnumerable<Discrepancy>? discrepancies)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Parsing the hashed text guarantees the artifact lists exactly what the signature covers.
        var canonical = CanonicalJson(discrepancies);
        return new JsonObject
        {
            ["itemId"] = item.ItemId,
            ["nature"] = item.Nature,
            ["operation"] = item.Operation,
            ["detail"] = item.Detail,
            ["signature"] = Hash16(canonical),
            ["discrepancies"] = JsonNode.Parse(canonical),
        };
    }

    /// <summary>
    /// Returns <paramref name="itemId"/> with every character outside <c>[A-Za-z0-9._-]</c> replaced
    /// by <c>_</c>, for example <c>CASE_REST-getAgent-01</c> for <c>CASE:REST-getAgent-01</c>.
    /// </summary>
    /// <remarks>
    /// Ids that differ only in replaced characters share a name. The ids that carry discrepancy
    /// artifacts, <c>CASE:&lt;caseId&gt;</c> and the contract checks, never do.
    /// </remarks>
    /// <param name="itemId">The item id.</param>
    /// <exception cref="ArgumentException"><paramref name="itemId"/> is null or empty.</exception>
    public static string SanitiseFileName(string itemId)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        return string.Create(itemId.Length, itemId, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_';
            }
        });
    }

    private static List<CanonicalEntry> Canonicalise(IEnumerable<Discrepancy>? discrepancies)
    {
        var entries = new List<CanonicalEntry>();
        if (discrepancies is null)
        {
            return entries;
        }

        foreach (var discrepancy in discrepancies)
        {
            if (discrepancy is null)
            {
                throw new ArgumentException("The discrepancy list contains a null entry.", nameof(discrepancies));
            }

            entries.Add(new CanonicalEntry(discrepancy, TokenText(discrepancy.Baseline), TokenText(discrepancy.Observed)));
        }

        // The four keys are exactly what each entry writes, so entries equal under this order write the
        // same text and the unstable sort cannot change the output.
        entries.Sort(static (x, y) =>
        {
            var order = string.CompareOrdinal(x.Discrepancy.Path, y.Discrepancy.Path);
            if (order == 0)
            {
                order = string.CompareOrdinal(x.Discrepancy.Kind, y.Discrepancy.Kind);
            }

            if (order == 0)
            {
                order = string.CompareOrdinal(x.BaselineToken, y.BaselineToken);
            }

            return order != 0 ? order : string.CompareOrdinal(x.ObservedToken, y.ObservedToken);
        });

        return entries;
    }

    private static string TokenText(DiscrepancyValue? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteToken(writer, value);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // Written from IsValid, ValidRule and Text rather than through DiscrepancyValue.ToToken(), so the
    // token shape is fixed here with the signature; the two produce the same JSON.
    private static void WriteToken(Utf8JsonWriter writer, DiscrepancyValue? value)
    {
        writer.WriteStartObject();
        if (value is { IsValid: true })
        {
            writer.WriteString("valid", value.ValidRule);
        }
        else
        {
            writer.WriteString("literal", value?.Text);
        }

        writer.WriteEndObject();
    }

    private sealed record CanonicalEntry(Discrepancy Discrepancy, string BaselineToken, string ObservedToken);
}

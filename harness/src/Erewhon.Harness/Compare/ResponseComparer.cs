using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Erewhon.Contracts.Catalogue;

namespace Erewhon.Harness.Compare;

/// <summary>
/// Why no HTTP response was obtained for a replayed case. Each value becomes the suffix of the
/// <c>no-response:&lt;reason&gt;</c> discrepancy kind.
/// </summary>
public static class NoResponseReasons
{
    /// <summary>The connection could not be established, or the target was never ready.</summary>
    public const string Connect = "connect";

    /// <summary>The per-request limit expired before a response arrived.</summary>
    public const string Timeout = "timeout";

    /// <summary>The connection was reset or closed before a complete response arrived.</summary>
    public const string Reset = "reset";

    /// <summary>True when <paramref name="reason"/> is one of the three reasons, compared ordinally.</summary>
    /// <param name="reason">The candidate reason.</param>
    internal static bool IsKnown(string? reason) =>
        string.Equals(reason, Connect, StringComparison.Ordinal)
        || string.Equals(reason, Timeout, StringComparison.Ordinal)
        || string.Equals(reason, Reset, StringComparison.Ordinal);
}

/// <summary>
/// The discrepancy kinds every comparer in this folder reports. Together with a
/// discrepancy's path and typed values, the kind is part of the failure signature, so the spellings
/// are fixed.
/// </summary>
public static class DiscrepancyKinds
{
    /// <summary>Both sides hold a value at the path and the values differ.</summary>
    public const string Value = "value";

    /// <summary>The baseline holds a value at the path and the observed response does not.</summary>
    public const string Missing = "missing";

    /// <summary>The observed response holds a value at the path and the baseline does not.</summary>
    public const string Extra = "extra";

    /// <summary>The two values at the path are of different kinds, for example a string and a number.</summary>
    public const string Type = "type";

    /// <summary>The two sides hold different counts at the path: array items, repeated elements or bytes.</summary>
    public const string Length = "length";

    /// <summary>The baseline body is well formed and the observed body is not, or a body does not parse.</summary>
    public const string Malformed = "malformed";

    /// <summary>Returns the kind <c>no-response:&lt;reason&gt;</c> for a case that received no HTTP response.</summary>
    /// <param name="reason">One of the <see cref="NoResponseReasons"/> values.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is not one of the <see cref="NoResponseReasons"/> values.</exception>
    public static string NoResponse(string reason) =>
        NoResponseReasons.IsKnown(reason)
            ? "no-response:" + reason
            : throw new ArgumentException(
                $"'{reason}' is not a no-response reason; expected '{NoResponseReasons.Connect}', "
                + $"'{NoResponseReasons.Timeout}' or '{NoResponseReasons.Reset}'.",
                nameof(reason));
}

/// <summary>
/// One side's value in a <see cref="Discrepancy"/>: either a value valid under a normalisation rule
/// (<c>N1</c> generated identifiers, <c>N2</c> timestamps), whose content is irrelevant to equality, or
/// a literal text kept exactly as found, or absent.
/// </summary>
/// <remarks>
/// The two forms are kept apart in the failure signature by <see cref="ToToken"/>: a valid value is the
/// token <c>{"valid":"N1"}</c> and every other value is <c>{"literal":&lt;text&gt;}</c>, so a body that
/// literally contains <c>{generated-guid}</c> can never produce the same signature as a valid GUID.
/// The properties have no setters, so a <c>with</c> expression cannot build a value that
/// bypasses the factories' validation.
/// </remarks>
public sealed record DiscrepancyValue
{
    /// <summary>The rule id <c>N1</c>.</summary>
    private const string RuleN1 = "N1";

    /// <summary>The rule id <c>N2</c>.</summary>
    private const string RuleN2 = "N2";

    private DiscrepancyValue(string? validRule, string? text)
    {
        ValidRule = validRule;
        Text = text;
    }

    /// <summary>The absent value: nothing exists at the path on this side.</summary>
    public static DiscrepancyValue Absent { get; } = new(null, null);

    /// <summary>The normalisation rule the value is valid under, <c>N1</c> or <c>N2</c>; null for a literal.</summary>
    public string? ValidRule { get; }

    /// <summary>The exact literal text, untrimmed; null for a valid value and for an absent one.</summary>
    public string? Text { get; }

    /// <summary>True when the value is valid under <see cref="ValidRule"/>.</summary>
    public bool IsValid => ValidRule is not null;

    /// <summary>True when nothing exists at the path on this side.</summary>
    public bool IsAbsent => ValidRule is null && Text is null;

    /// <summary>Returns a value valid under a normalisation rule; its content is deliberately not kept.</summary>
    /// <param name="rule">The rule id, exactly <c>N1</c> or <c>N2</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="rule"/> is neither <c>N1</c> nor <c>N2</c>.</exception>
    public static DiscrepancyValue Valid(string rule) =>
        string.Equals(rule, RuleN1, StringComparison.Ordinal) || string.Equals(rule, RuleN2, StringComparison.Ordinal)
            ? new DiscrepancyValue(rule, null)
            : throw new ArgumentException($"'{rule}' is not a normalisation rule; expected '{RuleN1}' or '{RuleN2}'.", nameof(rule));

    /// <summary>Returns a literal value holding <paramref name="text"/> exactly, with no trimming.</summary>
    /// <param name="text">The exact text; null gives <see cref="Absent"/>.</param>
    public static DiscrepancyValue Literal(string? text) => text is null ? Absent : new DiscrepancyValue(null, text);

    /// <summary>
    /// Returns the typed token of the failure signature: <c>{"valid":"N1"}</c> or
    /// <c>{"valid":"N2"}</c> for a valid value, otherwise <c>{"literal":"&lt;exact text&gt;"}</c>, with
    /// JSON <c>null</c> as the literal of an absent value. A new node is returned on every call.
    /// </summary>
    public JsonObject ToToken() =>
        IsValid
            ? new JsonObject { ["valid"] = ValidRule }
            : new JsonObject { ["literal"] = Text };

    /// <summary>
    /// Returns <c>{valid:N1}</c>, the literal text or <c>(absent)</c>, for report display only; the
    /// failure signature is built from <see cref="ToToken"/>, never from this text.
    /// </summary>
    public override string ToString() =>
        IsValid ? $"{{valid:{ValidRule}}}" : Text ?? "(absent)";
}

/// <summary>
/// One difference between a baseline response and an observed one, as every comparer in this folder
/// reports it.
/// </summary>
/// <remarks>
/// Path conventions shared by all comparers:
/// <list type="bullet">
///   <item><c>$response</c>: no HTTP response was obtained (kind <c>no-response:&lt;reason&gt;</c>).</item>
///   <item><c>$status</c>: the HTTP status code.</item>
///   <item><c>$content-type</c>: the <c>Content-Type</c> header, compared by media type and charset.</item>
///   <item><c>$body</c>: the body as a whole (exact text, well-formedness or byte length), and
///   <c>$body[&lt;offset&gt;]</c> for the first differing byte of a binary comparison.</item>
///   <item>JSON body paths start with <c>$</c>, for example <c>$.guid</c> or <c>$[3].item.agent.name</c>.</item>
///   <item>XML body paths start with <c>/</c>, for example <c>/Envelope/Body/getAgentResponse/getAgentResult/name/text()</c>.</item>
/// </list>
/// </remarks>
/// <param name="Path">Where the difference is, in the conventions above; never empty.</param>
/// <param name="Kind">One of the <see cref="DiscrepancyKinds"/> values; never empty.</param>
/// <param name="Baseline">The baseline side's value at <paramref name="Path"/>.</param>
/// <param name="Observed">The observed side's value at <paramref name="Path"/>.</param>
public sealed record Discrepancy(string Path, string Kind, DiscrepancyValue Baseline, DiscrepancyValue Observed)
{
    // Token texts for ordering use the same relaxed, fixed encoder as the canonical signature list, so
    // the order here and the order the signature is computed in agree on non-ASCII literals.
    private static readonly JsonWriterOptions TokenWriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Where the difference is; never empty.</summary>
    public string Path { get; init; } = string.IsNullOrEmpty(Path)
        ? throw new ArgumentException("A discrepancy needs a non-empty path.", nameof(Path))
        : Path;

    /// <summary>The kind of difference; never empty.</summary>
    public string Kind { get; init; } = string.IsNullOrEmpty(Kind)
        ? throw new ArgumentException("A discrepancy needs a non-empty kind.", nameof(Kind))
        : Kind;

    /// <summary>The baseline side's value.</summary>
    public DiscrepancyValue Baseline { get; init; } = Baseline ?? throw new ArgumentNullException(nameof(Baseline));

    /// <summary>The observed side's value.</summary>
    public DiscrepancyValue Observed { get; init; } = Observed ?? throw new ArgumentNullException(nameof(Observed));

    /// <summary>
    /// A total, deterministic order: ordinal by <see cref="Path"/>, then <see cref="Kind"/>, then the
    /// compact JSON text of the baseline token, then of the observed token. Permuted input therefore
    /// always sorts to the same list. Null sorts first.
    /// </summary>
    public static IComparer<Discrepancy> CanonicalOrder { get; } = new CanonicalComparer();

    /// <summary>
    /// Returns <c>{"path":…,"kind":…,"baseline":&lt;token&gt;,"observed":&lt;token&gt;}</c>, the entry
    /// written to <c>artifacts/discrepancies/&lt;itemId&gt;.json</c> and hashed into the failure
    /// signature. A new node is returned on every call.
    /// </summary>
    public JsonObject ToJsonNode() => new()
    {
        ["path"] = Path,
        ["kind"] = Kind,
        ["baseline"] = Baseline.ToToken(),
        ["observed"] = Observed.ToToken(),
    };

    private static string TokenText(DiscrepancyValue value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, TokenWriterOptions))
        {
            value.ToToken().WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private sealed class CanonicalComparer : IComparer<Discrepancy>
    {
        public int Compare(Discrepancy? x, Discrepancy? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var order = string.CompareOrdinal(x.Path, y.Path);
            if (order != 0)
            {
                return order;
            }

            order = string.CompareOrdinal(x.Kind, y.Kind);
            if (order != 0)
            {
                return order;
            }

            order = string.CompareOrdinal(TokenText(x.Baseline), TokenText(y.Baseline));
            return order != 0 ? order : string.CompareOrdinal(TokenText(x.Observed), TokenText(y.Observed));
        }
    }
}

/// <summary>
/// The parts of one HTTP response that take part in comparison: the status code, the
/// <c>Content-Type</c> header and the raw body bytes. Every other header is left out on purpose, so
/// <c>Date</c>, <c>Server</c>, <c>Content-Length</c>, <c>Transfer-Encoding</c>, <c>Connection</c> and
/// <c>Keep-Alive</c> can never cause a difference.
/// </summary>
/// <param name="Status">The HTTP status code; null when no response was obtained.</param>
/// <param name="ContentType">The raw <c>Content-Type</c> header text; null when the response has none.</param>
/// <param name="Body">
/// The raw body bytes exactly as received or recorded: the UTF-8 bytes of a record's <c>bodyText</c>, or
/// the <c>.body.bin</c> bytes for <c>getPhoto</c>.
/// </param>
/// <param name="NoResponse">
/// A <see cref="NoResponseReasons"/> value when no HTTP response was obtained; null when one was. A
/// baseline always holds a response.
/// </param>
public sealed record ResponseSnapshot(int? Status, string? ContentType, ReadOnlyMemory<byte> Body, string? NoResponse = null);

/// <summary>The outcome of comparing one observed response with its baseline record.</summary>
/// <param name="Equivalent">True when <paramref name="Discrepancies"/> is empty.</param>
/// <param name="Discrepancies">Every difference found, in <see cref="Discrepancy.CanonicalOrder"/>.</param>
/// <param name="WellFormed">
/// True when the OBSERVED body is well formed for its surface: JSON that parses for REST, and a SOAP 1.1
/// envelope with a <c>Body</c> for SOAP. Demo acceptance judges a non-equivalent operation by it.
/// </param>
/// <param name="StatusMatched">True when the observed status code equals the baseline's.</param>
public sealed record ResponseComparison(
    bool Equivalent,
    IReadOnlyList<Discrepancy> Discrepancies,
    bool WellFormed,
    bool StatusMatched);

/// <summary>
/// Compares an observed response with its baseline record: the status code, the
/// <c>Content-Type</c> media type and charset, and the body, which goes to the JSON, XML or binary
/// comparer according to the operation's catalogue descriptor. Bodies are never rewritten; the two
/// normalisation rules only decide pairwise equality inside the semantic comparers.
/// </summary>
/// <remarks>
/// The instance holds only immutable state and performs no I/O, so concurrent <see cref="Compare"/>
/// calls are safe.
/// </remarks>
/// <param name="catalogue">The operation catalogue the semantic comparers type bodies by.</param>
/// <param name="normaliser">The N1/N2 normaliser the semantic comparers apply at targeted positions.</param>
public sealed class ResponseComparer(OperationCatalogue catalogue, Normaliser normaliser)
{
    private const string ResponsePath = "$response";
    private const string StatusPath = "$status";
    private const string ContentTypePath = "$content-type";
    private const string BodyPath = "$body";

    private const string SoapEnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XName SoapEnvelope = XName.Get("Envelope", SoapEnvelopeNamespace);
    private static readonly XName SoapBody = XName.Get("Body", SoapEnvelopeNamespace);

    // getPhoto is the one operation whose bodies are compared byte for byte: its response carries the
    // photo as base64Binary, and the raw bytes are compared with the recorded .body.bin file.
    private const string BinaryComparedSoapMethod = "getPhoto";

    private readonly JsonSemanticComparer _jsonComparer = new(
        catalogue ?? throw new ArgumentNullException(nameof(catalogue)),
        normaliser ?? throw new ArgumentNullException(nameof(normaliser)));

    private readonly XmlSemanticComparer _xmlComparer = new(catalogue, normaliser);

    /// <summary>Compares <paramref name="observed"/> with <paramref name="baseline"/> for <paramref name="operation"/>.</summary>
    /// <param name="operation">The catalogue descriptor of the case's operation; its surface selects the body comparer.</param>
    /// <param name="baseline">The recorded response; it always holds a response.</param>
    /// <param name="observed">The response the replay obtained, or the reason none was obtained.</param>
    /// <returns>The discrepancies in canonical order, with the observed body's well-formedness and the status match.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="baseline"/> holds a no-response reason, or <paramref name="observed"/> holds one that
    /// is not a <see cref="NoResponseReasons"/> value.
    /// </exception>
    public ResponseComparison Compare(OperationDescriptor operation, ResponseSnapshot baseline, ResponseSnapshot observed)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(observed);

        if (baseline.NoResponse is not null)
        {
            throw new ArgumentException(
                $"A baseline always holds a response, but this one records no-response '{baseline.NoResponse}'.",
                nameof(baseline));
        }

        if (observed.NoResponse is not null)
        {
            Discrepancy[] noResponse =
            [
                new(
                    ResponsePath,
                    DiscrepancyKinds.NoResponse(observed.NoResponse),
                    DiscrepancyValue.Literal(StatusText(baseline.Status)),
                    DiscrepancyValue.Absent),
            ];
            return new ResponseComparison(Equivalent: false, noResponse, WellFormed: false, StatusMatched: false);
        }

        var discrepancies = new List<Discrepancy>();

        if (baseline.Status != observed.Status)
        {
            discrepancies.Add(new Discrepancy(
                StatusPath,
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal(StatusText(baseline.Status)),
                DiscrepancyValue.Literal(StatusText(observed.Status))));
        }

        if (!ContentTypesEqual(baseline.ContentType, observed.ContentType))
        {
            discrepancies.Add(new Discrepancy(
                ContentTypePath,
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal(baseline.ContentType),
                DiscrepancyValue.Literal(observed.ContentType)));
        }

        var observedWellFormed = operation.Surface switch
        {
            Surface.Rest => CompareRestBody(operation, baseline, observed, discrepancies),
            Surface.Soap => CompareSoapBody(operation, baseline, observed, discrepancies),
            _ => throw new ArgumentException(
                $"Operation '{operation.Id}' has surface '{operation.Surface}', which no body comparer handles.",
                nameof(operation)),
        };

        discrepancies.Sort(Discrepancy.CanonicalOrder);
        return new ResponseComparison(
            Equivalent: discrepancies.Count == 0,
            discrepancies.AsReadOnly(),
            observedWellFormed,
            StatusMatched: baseline.Status == observed.Status);
    }

    /// <summary>Compares two REST bodies as JSON and returns whether the observed body is well formed.</summary>
    private bool CompareRestBody(
        OperationDescriptor operation,
        ResponseSnapshot baseline,
        ResponseSnapshot observed,
        List<Discrepancy> discrepancies)
    {
        var baselineText = DecodeBody(baseline.Body.Span);
        var observedText = DecodeBody(observed.Body.Span);

        // For REST, a body is well formed exactly when it parses as JSON; the literal null parses.
        var baselineWellFormed = IsJson(baselineText);
        var observedWellFormed = IsJson(observedText);

        if (baselineWellFormed && observedWellFormed)
        {
            discrepancies.AddRange(_jsonComparer.Compare(baselineText, observedText, RestDeclaredType(operation, baseline.Status)));
        }
        else
        {
            AddExactTextDifference(baselineText, observedText, discrepancies);
        }

        AddMalformed(baselineWellFormed, observedWellFormed, discrepancies);
        return observedWellFormed;
    }

    /// <summary>
    /// Compares two SOAP bodies, as XML or, for the binary-compared operation, byte for byte, and returns
    /// whether the observed body is a well-formed SOAP 1.1 envelope.
    /// </summary>
    private bool CompareSoapBody(
        OperationDescriptor operation,
        ResponseSnapshot baseline,
        ResponseSnapshot observed,
        List<Discrepancy> discrepancies)
    {
        var baselineText = DecodeBody(baseline.Body.Span);
        var observedText = DecodeBody(observed.Body.Span);

        var baselineDocument = TryLoadXml(baselineText);
        var observedDocument = TryLoadXml(observedText);
        var baselineWellFormed = IsSoapEnvelope(baselineDocument);
        var observedWellFormed = IsSoapEnvelope(observedDocument);

        if (string.Equals(operation.JadeMethod, BinaryComparedSoapMethod, StringComparison.Ordinal))
        {
            // The raw bytes as received, BOM included: no rule and no canonicalisation applies.
            discrepancies.AddRange(BinaryComparer.Compare(baseline.Body.Span, observed.Body.Span));
        }
        else if (baselineDocument is not null && observedDocument is not null)
        {
            discrepancies.AddRange(_xmlComparer.Compare(baselineText, observedText, XmlComparisonContext.SoapResponse(operation)));
        }
        else
        {
            AddExactTextDifference(baselineText, observedText, discrepancies);
        }

        AddMalformed(baselineWellFormed, observedWellFormed, discrepancies);
        return observedWellFormed;
    }

    /// <summary>
    /// The type the JSON comparer walks a REST body by, selected by the BASELINE status: the body type of
    /// the operation's first catalogue status with that HTTP status, or the declared return type when none
    /// has it. The literal-null body names no DTO, so it is compared untyped.
    /// </summary>
    private static string? RestDeclaredType(OperationDescriptor operation, int? baselineStatus)
    {
        var status = operation.Statuses.FirstOrDefault(candidate => candidate.HttpStatus == baselineStatus);
        if (status is null)
        {
            return operation.ReturnType;
        }

        return status.IsNullBody ? null : status.BodyType;
    }

    private static string? StatusText(int? status) => status?.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Compares two <c>Content-Type</c> headers by media type and charset, case-insensitively. Headers
    /// that do not both parse are compared as trimmed raw text, case-insensitively.
    /// </summary>
    private static bool ContentTypesEqual(string? baseline, string? observed)
    {
        if (baseline is null && observed is null)
        {
            return true;
        }

        if (baseline is not null
            && observed is not null
            && MediaTypeHeaderValue.TryParse(baseline, out var baselineMediaType)
            && MediaTypeHeaderValue.TryParse(observed, out var observedMediaType))
        {
            return string.Equals(baselineMediaType.MediaType, observedMediaType.MediaType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Unquote(baselineMediaType.CharSet), Unquote(observedMediaType.CharSet), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(baseline?.Trim(), observed?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Unquote(string? value) =>
        value is { Length: >= 2 } && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    /// <summary>Removes one leading UTF-8 byte-order mark and decodes the rest as UTF-8.</summary>
    private static string DecodeBody(ReadOnlySpan<byte> body)
    {
        ReadOnlySpan<byte> utf8Bom = [0xEF, 0xBB, 0xBF];
        return Encoding.UTF8.GetString(body.StartsWith(utf8Bom) ? body[utf8Bom.Length..] : body);
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Loads <paramref name="text"/> as XML with DTDs prohibited and no resolver; null when it does not load.</summary>
    private static XDocument? TryLoadXml(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static bool IsSoapEnvelope(XDocument? document) =>
        document?.Root is { } root
        && root.Name == SoapEnvelope
        && root.Element(SoapBody) is not null;

    private static void AddExactTextDifference(string baselineText, string observedText, List<Discrepancy> discrepancies)
    {
        if (!string.Equals(baselineText, observedText, StringComparison.Ordinal))
        {
            discrepancies.Add(new Discrepancy(
                BodyPath,
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal(baselineText),
                DiscrepancyValue.Literal(observedText)));
        }
    }

    private static void AddMalformed(bool baselineWellFormed, bool observedWellFormed, List<Discrepancy> discrepancies)
    {
        if (baselineWellFormed && !observedWellFormed)
        {
            discrepancies.Add(new Discrepancy(BodyPath, DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Absent));
        }
    }
}

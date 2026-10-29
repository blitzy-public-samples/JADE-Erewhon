using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Erewhon.Harness.Baseline;

/// <summary>
/// One captured case of the behaviour baseline: the request the harness sent to the backend during
/// <c>capture</c> and exactly what came back. It is stored as
/// <c>baseline/records/&lt;rest|soap&gt;/NNN-&lt;jadeMethod&gt;-&lt;NN&gt;.json</c> and stays the
/// equivalence reference for every later replay.
/// </summary>
/// <remarks>
/// The JSON shape is fixed by the positional parameters: they serialise in declaration order under the
/// baseline store's camel-case options, and null members are written, so every record has the same keys.
/// Bodies are stored raw. The generated-identifier and timestamp normalisations apply only when two
/// responses are compared, to both sides, never to what a record holds.
/// </remarks>
/// <param name="Order">The 1-based replay position, which is the <c>NNN</c> of the file name.</param>
/// <param name="CaseId">
/// The case id from <c>harness/config/case-matrix.json</c>, <c>&lt;REST|SOAP&gt;-&lt;jadeMethod&gt;-&lt;NN&gt;</c>,
/// for example <c>REST-getAgent-01</c>.
/// </param>
/// <param name="Operation">The catalogue operation id, <c>Rest.&lt;jadeMethod&gt;</c> or <c>Soap.&lt;jadeMethod&gt;</c>.</param>
/// <param name="Kind"><c>success</c> or <c>error</c>.</param>
/// <param name="StateChanging">True for the <c>postCreate*</c> and <c>update*</c> cases.</param>
/// <param name="PreState">The backend state the case runs against: <c>seed</c> or <c>empty</c>.</param>
/// <param name="Source">The source label, always <see cref="Erewhon.Contracts.SourceLabels.StandIn"/>.</param>
/// <param name="Request">What the harness sent.</param>
/// <param name="Response">What the backend returned.</param>
public sealed record BaselineRecord(
    int Order,
    string CaseId,
    string Operation,
    string Kind,
    bool StateChanging,
    string PreState,
    string Source,
    BaselineRequest Request,
    BaselineResponse Response)
{
    /// <summary>
    /// The surface directory of the record: <c>rest</c> for a <c>REST-</c> case id and <c>soap</c> for a
    /// <c>SOAP-</c> one.
    /// </summary>
    /// <exception cref="InvalidDataException">The case id has neither prefix.</exception>
    [JsonIgnore]
    public string Surface => SurfaceOf(CaseId);

    /// <summary>
    /// The JADE method name: the part of <see cref="Operation"/> after its first <c>.</c>, so
    /// <c>Rest.getAgent</c> gives <c>getAgent</c>.
    /// </summary>
    /// <exception cref="InvalidDataException">The operation has no <c>.</c> or nothing after it.</exception>
    [JsonIgnore]
    public string JadeMethod => JadeMethodOf(Operation);

    /// <summary>
    /// The case number: the segment of <see cref="CaseId"/> after its last <c>-</c>, for example <c>01</c>.
    /// </summary>
    /// <exception cref="InvalidDataException">That segment is empty or holds anything but ASCII digits.</exception>
    [JsonIgnore]
    public string CaseNumber => CaseNumberOf(CaseId);

    // The string members can still be null after deserialising a malformed file, so each helper takes a
    // nullable value and reports the record as invalid data instead of failing with a null reference.
    private static string SurfaceOf(string? caseId) => caseId switch
    {
        not null when caseId.StartsWith("REST-", StringComparison.Ordinal) => "rest",
        not null when caseId.StartsWith("SOAP-", StringComparison.Ordinal) => "soap",
        _ => throw new InvalidDataException(
            $"Baseline case id '{caseId}' starts with neither 'REST-' nor 'SOAP-'."),
    };

    private static string JadeMethodOf(string? operation) =>
        operation?.IndexOf('.', StringComparison.Ordinal) is int dot and >= 0 && dot < operation.Length - 1
            ? operation[(dot + 1)..]
            : throw new InvalidDataException(
                $"Baseline operation '{operation}' is not of the form '<Rest|Soap>.<jadeMethod>'.");

    private static string CaseNumberOf(string? caseId) =>
        caseId?.LastIndexOf('-') is int dash and >= 0
        && caseId[(dash + 1)..] is { Length: > 0 } number
        && !number.AsSpan().ContainsAnyExceptInRange('0', '9')
            ? number
            : throw new InvalidDataException(
                $"Baseline case id '{caseId}' does not end in '-' followed by ASCII digits.");
}

/// <summary>
/// The request half of a <see cref="BaselineRecord"/>, exactly as the harness sent it.
/// </summary>
/// <param name="Method">The HTTP verb as sent, for example <c>GET</c> or <c>POST</c>.</param>
/// <param name="PathAndQuery">
/// The raw path and query as sent, without scheme or host, for example <c>/agent/Hank%20Williams</c> or
/// <c>/jade/jadehttp.dll?WebServiceHTTP&amp;serviceName=ErewhonWebService&amp;listName=WebServiceHTTP</c>.
/// </param>
/// <param name="Headers">
/// Exactly the request headers the case sent among <c>Content-Type</c>, <c>SOAPAction</c> and
/// <c>Accept</c>, with names as sent and values verbatim (a <c>SOAPAction</c> keeps its quotes). It is
/// serialised in enumeration order, so callers pass an insertion-ordered dictionary; it is empty when the
/// case sent none of them.
/// </param>
/// <param name="BodyText">The exact request body text, or <see langword="null"/> when no body was sent.</param>
public sealed record BaselineRequest(
    string Method,
    string PathAndQuery,
    IReadOnlyDictionary<string, string> Headers,
    string? BodyText);

/// <summary>
/// The response half of a <see cref="BaselineRecord"/>, exactly as the backend returned it.
/// </summary>
/// <param name="Status">The HTTP status code.</param>
/// <param name="ContentType">
/// The response <c>Content-Type</c> header value verbatim, for example
/// <c>application/json; charset=utf-8</c>, or <see langword="null"/> when the header was absent.
/// </param>
/// <param name="BodyText">
/// The body bytes decoded as UTF-8 with nothing stripped, trimmed or re-encoded, so a byte-order mark
/// stays as U+FEFF; <c>""</c> for an empty body. It is <see langword="null"/> exactly when
/// <paramref name="BodyFile"/> holds the body instead.
/// </param>
/// <param name="BodySha256">The lowercase hexadecimal SHA-256 of the raw body bytes.</param>
/// <param name="BodyFile">
/// The file name, without a directory, of the sibling <c>.body.bin</c> file that holds the raw body,
/// <c>NNN-getPhoto-&lt;NN&gt;.body.bin</c> for the <c>Soap.getPhoto</c> cases; otherwise
/// <see langword="null"/>.
/// </param>
public sealed record BaselineResponse(
    int Status,
    string? ContentType,
    string? BodyText,
    string BodySha256,
    string? BodyFile)
{
    // Rejects invalid byte sequences instead of replacing them with U+FFFD, which would make a lossy
    // decode look like a faithful one.
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Builds the response half of a record from the raw body bytes.
    /// </summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="contentType">The <c>Content-Type</c> header value verbatim, or <see langword="null"/>.</param>
    /// <param name="raw">The raw response body.</param>
    /// <param name="bodyFile">
    /// The <c>.body.bin</c> file name that will hold <paramref name="raw"/>, or <see langword="null"/> to
    /// keep the body as text in the record.
    /// </param>
    /// <returns>
    /// A response whose <see cref="BodySha256"/> covers <paramref name="raw"/>, and whose
    /// <see cref="BodyText"/> is the UTF-8 decode of <paramref name="raw"/> when no body file is named.
    /// </returns>
    public static BaselineResponse FromRawBody(
        int status, string? contentType, ReadOnlySpan<byte> raw, string? bodyFile) =>
        new(
            status,
            contentType,
            bodyFile is null ? Encoding.UTF8.GetString(raw) : null,
            ComputeSha256Hex(raw),
            bodyFile);

    /// <summary>
    /// The lowercase hexadecimal SHA-256 of <paramref name="bytes"/>: the one hash used to write and to
    /// verify <see cref="BodySha256"/>.
    /// </summary>
    public static string ComputeSha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Whether <paramref name="raw"/> is valid UTF-8 that a decode and re-encode reproduces byte for byte,
    /// so that <see cref="BodyText"/> alone can stand for it. A body that fails this test must be stored
    /// in a body file.
    /// </summary>
    public static bool IsTextRoundTrippable(ReadOnlySpan<byte> raw)
    {
        try
        {
            return StrictUtf8.GetBytes(StrictUtf8.GetString(raw)).AsSpan().SequenceEqual(raw);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

using Erewhon.Contracts.Catalogue;
using System.Text.Json.Serialization;

namespace Erewhon.Harness.Cases;

/// <summary>
/// The case groups of <c>harness/config/case-matrix.json</c>, which fix the replay order of AAP 0.7.3:
/// REST reads, SOAP reads, REST writes, SOAP writes, and last the <c>getCompany</c> empty-state case.
/// Because every read runs before any write, no read depends on a write.
/// </summary>
public static class CaseGroups
{
    /// <summary>REST cases that only read backend state.</summary>
    public const string RestRead = "rest-read";

    /// <summary>SOAP cases that only read backend state.</summary>
    public const string SoapRead = "soap-read";

    /// <summary>REST <c>postCreate*</c> cases, which change backend state.</summary>
    public const string RestWrite = "rest-write";

    /// <summary>SOAP <c>update*</c> cases, which change backend state.</summary>
    public const string SoapWrite = "soap-write";

    /// <summary>
    /// The case run against a backend with no company, bracketed by <c>reset?state=empty</c> before it
    /// and <c>reset?state=seed</c> after it (AAP 0.7.3).
    /// </summary>
    public const string EmptyState = "empty-state";

    /// <summary>
    /// Every group in replay order (AAP 0.7.3). A matrix is in order when the <see cref="IndexOf"/> of
    /// its cases' groups never decreases.
    /// </summary>
    public static IReadOnlyList<string> Order { get; } = [RestRead, SoapRead, RestWrite, SoapWrite, EmptyState];

    /// <summary>
    /// The zero-based position of <paramref name="group"/> in <see cref="Order"/>, compared ordinally.
    /// </summary>
    /// <param name="group">A group name as written in the matrix.</param>
    /// <returns>The index in <see cref="Order"/>, or -1 when the name is not a known group.</returns>
    public static int IndexOf(string group)
    {
        for (var index = 0; index < Order.Count; index++)
        {
            if (string.Equals(Order[index], group, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>
/// The backend states a case can run against (AAP 0.7.3), each established by
/// <c>POST /admin/reset?state=&lt;value&gt;</c> on the stand-in.
/// </summary>
public static class CasePreStates
{
    /// <summary>State rebuilt from the seed files in <c>Erewhon/DataFiles</c>.</summary>
    public const string Seed = "seed";

    /// <summary>State with no company, for the <c>getCompany</c> 500 case.</summary>
    public const string Empty = "empty";
}

/// <summary>The kinds of case in the matrix (AAP 0.7.3).</summary>
public static class CaseKinds
{
    /// <summary>A case that exercises the operation's success path.</summary>
    public const string Success = "success";

    /// <summary>A case that exercises one of the operation's error paths.</summary>
    public const string Error = "error";
}

/// <summary>
/// The request a case sends, exactly as the matrix states it and as the baseline records it. It is
/// sent unchanged to the backend at capture and to the facade at replay.
/// </summary>
/// <param name="Method">The HTTP method as sent, <c>GET</c> or <c>POST</c>.</param>
/// <param name="PathAndQuery">
/// The raw request target with its percent-encoding exactly as written, for example
/// <c>/client/%20Pauline%20Wild%20</c>, <c>/company.json</c> or
/// <c>/jade/jadehttp.dll?WebServiceHTTP&amp;serviceName=ErewhonWebService&amp;listName=WebServiceHTTP</c>.
/// It is never decoded, normalised or re-encoded.
/// </param>
/// <param name="Headers">
/// Exactly the headers to send, such as <c>Content-Type</c> and <c>SOAPAction</c> with its surrounding
/// double quotes, keyed by their original name casing. No case carries <c>Accept</c>, because the
/// characterised unsuffixed REST requests are sent without one (AAP 0.7.1).
/// </param>
/// <param name="BodyText">The exact body text, or null for a request with no body.</param>
public sealed record CaseRequest(
    string Method,
    string PathAndQuery,
    IReadOnlyDictionary<string, string> Headers,
    string? BodyText)
{
    /// <summary>
    /// <see cref="PathAndQuery"/> up to, and excluding, its first <c>?</c>; the whole value when it has
    /// no query. Percent-encoding is kept.
    /// </summary>
    [JsonIgnore]
    public string PathPart => QueryStart is int start ? PathAndQuery[..start] : PathAndQuery;

    /// <summary>
    /// The raw text after the first <c>?</c> of <see cref="PathAndQuery"/>, without the <c>?</c>; null
    /// when it has no query.
    /// </summary>
    [JsonIgnore]
    public string? QueryPart => QueryStart is int start ? PathAndQuery[(start + 1)..] : null;

    /// <summary>
    /// The value of the first header whose name equals <paramref name="name"/> ignoring case. The
    /// headers are searched in order instead of through the dictionary's comparer, so the lookup does not
    /// depend on how the dictionary was built.
    /// </summary>
    /// <param name="name">The header name, for example <c>SOAPAction</c>.</param>
    /// <returns>The header value as stored, or null when no header has that name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public string? GetHeader(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var (headerName, value) in Headers)
        {
            if (string.Equals(headerName, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private int? QueryStart =>
        PathAndQuery.IndexOf('?', StringComparison.Ordinal) is var index and >= 0 ? index : null;
}

/// <summary>
/// One case of <c>harness/config/case-matrix.json</c> (AAP 0.7.3): a request against one catalogue
/// operation, the backend state it needs and the outcome it was planned to produce. The case's 1-based
/// replay position, the <c>NNN</c> of its baseline record, is not held here: it is the case's position in
/// the matrix, given by <c>CaseMatrix.PositionOf</c>.
/// </summary>
/// <remarks>
/// The positional members are the matrix's fields; every computed member is excluded from JSON, so a
/// camel-case serialisation of a case has exactly the matrix's shape.
/// </remarks>
/// <param name="Id">
/// The case id, unique across the matrix, with the grammar <c>^(REST|SOAP)-[A-Za-z]+-[0-9]{2}$</c>
/// (AAP 0.7.3): surface, JADE method and case number, for example <c>REST-getAgent-01</c> or
/// <c>SOAP-getPhoto-03</c>. It identifies the case's failure item <c>CASE:&lt;caseId&gt;</c>.
/// </param>
/// <param name="Operation">
/// The catalogue operation id <c>Rest.&lt;jadeMethod&gt;</c> or <c>Soap.&lt;jadeMethod&gt;</c>, for
/// example <c>Rest.getAgent</c>.
/// </param>
/// <param name="Kind"><see cref="CaseKinds.Success"/> or <see cref="CaseKinds.Error"/>.</param>
/// <param name="StateChanging">True for the <c>postCreate*</c> and <c>update*</c> cases.</param>
/// <param name="PreState">
/// The backend state the case runs against: <see cref="CasePreStates.Seed"/> or
/// <see cref="CasePreStates.Empty"/>.
/// </param>
/// <param name="Group">The case's group, one of <see cref="CaseGroups.Order"/>.</param>
/// <param name="Request">The request the case sends.</param>
/// <param name="ExpectedStatus">The HTTP status the case was planned to return.</param>
/// <param name="ExpectedResultStatusCode">
/// The planned <c>DTOResult.statusCode</c> of a SOAP case whose operation returns <c>DTOResult</c>, where
/// business errors travel in the result under HTTP 200; null for every other case.
/// </param>
/// <param name="Description">What the case exercises, with its source evidence.</param>
public sealed record CaseDefinition(
    string Id,
    string Operation,
    string Kind,
    bool StateChanging,
    string PreState,
    string Group,
    CaseRequest Request,
    int ExpectedStatus,
    int? ExpectedResultStatusCode,
    string Description)
{
    /// <summary>
    /// The part of <see cref="Id"/> before its first <c>-</c>, <c>REST</c> or <c>SOAP</c> for a
    /// well-formed id; empty when the id has no <c>-</c>. Never throws, so a malformed id can still be
    /// named in a validation message.
    /// </summary>
    [JsonIgnore]
    public string SurfacePrefix =>
        Id?.IndexOf('-', StringComparison.Ordinal) is int dash and >= 0 ? Id[..dash] : string.Empty;

    /// <summary>
    /// The JADE method part of <see cref="Id"/>, for example <c>getAgent</c> in <c>REST-getAgent-01</c>;
    /// empty when the id does not split on <c>-</c> into exactly three parts. Never throws.
    /// </summary>
    [JsonIgnore]
    public string JadeMethod => IdPart(1);

    /// <summary>
    /// The case-number part of <see cref="Id"/>, for example <c>01</c> in <c>REST-getAgent-01</c>; empty
    /// when the id does not split on <c>-</c> into exactly three parts. Never throws.
    /// </summary>
    [JsonIgnore]
    public string IdNumber => IdPart(2);

    /// <summary>
    /// The surface named by the id prefix: <see cref="Surface.Rest"/> for <c>REST</c> and
    /// <see cref="Surface.Soap"/> for <c>SOAP</c>, compared ordinally.
    /// </summary>
    /// <exception cref="InvalidOperationException">The id has neither prefix.</exception>
    [JsonIgnore]
    public Surface Surface => SurfacePrefix switch
    {
        "REST" => Surface.Rest,
        "SOAP" => Surface.Soap,
        _ => throw new InvalidOperationException($"Case id '{Id}' has no REST/SOAP prefix."),
    };

    /// <summary>
    /// True for the case that runs against the empty backend, which capture and replay bracket with an
    /// empty reset before it and a seed reset after it (AAP 0.7.3).
    /// </summary>
    [JsonIgnore]
    public bool IsEmptyState => string.Equals(PreState, CasePreStates.Empty, StringComparison.Ordinal);

    /// <summary>True for a <see cref="CaseKinds.Success"/> case.</summary>
    [JsonIgnore]
    public bool IsSuccess => string.Equals(Kind, CaseKinds.Success, StringComparison.Ordinal);

    /// <summary>The value of the request header named <paramref name="name"/>, ignoring case.</summary>
    /// <param name="name">The header name, for example <c>SOAPAction</c>.</param>
    /// <returns>The header value as stored, or null when the request has no such header.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public string? GetHeader(string name) => Request.GetHeader(name);

    // Id is null-checked although declared non-nullable, because these helpers must not throw on a case
    // built from a malformed matrix entry before it is validated.
    private string IdPart(int index) =>
        Id?.Split('-') is { Length: 3 } parts ? parts[index] : string.Empty;
}

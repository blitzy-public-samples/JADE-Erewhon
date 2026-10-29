using System.Text;
using System.Xml;

namespace Erewhon.Facade.Soap;

/// <summary>
/// Writes the SOAP 1.1 Fault the facade returns on the SOAP surface when the backend cannot be reached
/// or does not answer before the deadline. These two Faults and the WSDL address are the only SOAP
/// bytes the facade builds itself; every other SOAP response is the backend's, forwarded unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The JADE service is SOAP 1.1 (<c>_useSOAP12 = false</c>,
/// Erewhon/ErewhonWebServiceConsumerSchema.scm:1726), so a Fault travels with HTTP 500 and
/// <c>text/xml</c>, and <c>faultcode</c> and <c>faultstring</c> are unqualified children of
/// <c>soap:Fault</c>. The envelope carries no <c>faultactor</c>, <c>detail</c> or header, and no
/// run-varying value (time, port, URL, operation, exception text): the harness's discrepancy signatures
/// and the facade tests' assertions depend on these bytes being identical on every run.
/// </para>
/// <para>
/// The class holds no mutable state and is safe for concurrent use as a singleton. It never logs;
/// <c>BackendForwarder</c> writes the one log line per forwarded call and decides when a Fault is due.
/// </para>
/// </remarks>
public sealed class SoapFaultWriter
{
    /// <summary>Fault string for a backend that refused, could not be resolved or failed to connect.</summary>
    public const string BackendUnavailable = "Backend unavailable";

    /// <summary>Fault string for a backend that did not answer before the per-target deadline.</summary>
    public const string BackendTimedOut = "Backend timed out";

    /// <summary>Content type of every Fault, also used by the facade's WSDL response.</summary>
    public const string ContentType = "text/xml; charset=utf-8";

    /// <summary>The SOAP 1.1 envelope namespace, bound to the prefix <c>soap</c> on the envelope.</summary>
    public const string SoapEnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";

    /// <summary>The <c>faultcode</c> QName; its prefix is the one bound on the envelope.</summary>
    public const string FaultCodeServer = "soap:Server";

    private const string EnvelopeStart =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<soap:Envelope xmlns:soap=\"" + SoapEnvelopeNamespace + "\">"
        + "<soap:Body><soap:Fault><faultcode>" + FaultCodeServer + "</faultcode><faultstring>";

    private const string EnvelopeEnd = "</faultstring></soap:Fault></soap:Body></soap:Envelope>";

    // Declared before the precomputed envelopes, because static field initialisers run in textual order.
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly byte[] BackendUnavailableEnvelope = BuildEnvelope(BackendUnavailable);

    private static readonly byte[] BackendTimedOutEnvelope = BuildEnvelope(BackendTimedOut);

    /// <summary>
    /// Returns the UTF-8 envelope bytes, without a byte order mark, of the Fault carrying
    /// <paramref name="faultString"/>.
    /// </summary>
    /// <param name="faultString">
    /// The <c>faultstring</c> text. <see cref="BackendUnavailable"/> and <see cref="BackendTimedOut"/>
    /// return precomputed bytes; any other text is XML-escaped into a newly built envelope.
    /// </param>
    /// <returns>The complete envelope, a single line with no trailing newline.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="faultString"/> is null.</exception>
    public static ReadOnlyMemory<byte> GetEnvelopeBytes(string faultString)
    {
        ArgumentNullException.ThrowIfNull(faultString);

        return faultString switch
        {
            BackendUnavailable => BackendUnavailableEnvelope,
            BackendTimedOut => BackendTimedOutEnvelope,
            _ => BuildEnvelope(faultString),
        };
    }

    /// <summary>
    /// Replaces whatever status and headers the response holds with a SOAP 1.1 Fault: HTTP 500,
    /// <see cref="ContentType"/>, <c>Content-Length</c> and the envelope from
    /// <see cref="GetEnvelopeBytes"/>. No other header is set.
    /// </summary>
    /// <param name="response">The response of the SOAP request whose backend call failed.</param>
    /// <param name="faultString">The <c>faultstring</c> text, normally one of the two constants.</param>
    /// <param name="cancellationToken">Cancels the body write, normally the request's abort token.</param>
    /// <returns>
    /// A task that completes when the body is written. When the response has already started, the
    /// connection is aborted instead and the returned task is already complete, because the backend's
    /// status line and headers have gone to the caller and must never be replaced.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="response"/> or <paramref name="faultString"/> is null.
    /// </exception>
    public Task WriteAsync(HttpResponse response, string faultString, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(faultString);

        if (response.HasStarted)
        {
            response.HttpContext.Abort();
            return Task.CompletedTask;
        }

        var envelope = GetEnvelopeBytes(faultString);

        // Drops the status and headers YARP sets on its error path (502 or 504, empty body).
        response.Clear();
        response.StatusCode = StatusCodes.Status500InternalServerError;
        response.ContentType = ContentType;
        response.ContentLength = envelope.Length;

        return response.Body.WriteAsync(envelope, cancellationToken).AsTask();
    }

    private static byte[] BuildEnvelope(string faultString) =>
        Utf8WithoutBom.GetBytes(EnvelopeStart + EscapeText(faultString) + EnvelopeEnd);

    /// <summary>
    /// Escapes text for element content so that every input gives a well-formed envelope whose parsed
    /// <c>faultstring</c> equals the input wherever XML 1.0 can carry it.
    /// </summary>
    /// <remarks>
    /// The five markup characters use the same entities as <c>SecurityElement.Escape</c>. A carriage
    /// return is written as a character reference, because a parser would otherwise normalise it to a
    /// line feed. A character XML 1.0 cannot carry (a control character, a lone surrogate, U+FFFE or
    /// U+FFFF) becomes U+FFFD.
    /// </remarks>
    private static string EscapeText(string text)
    {
        StringBuilder? escaped = null;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            string? replacement = c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&apos;",
                '\r' => "&#xD;",
                _ => null,
            };

            if (replacement is null)
            {
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], c))
                {
                    escaped?.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }

                if (XmlConvert.IsXmlChar(c))
                {
                    escaped?.Append(c);
                    continue;
                }

                replacement = "\uFFFD";
            }

            escaped ??= new StringBuilder(text.Length + 16).Append(text, 0, i);
            escaped.Append(replacement);
        }

        return escaped?.ToString() ?? text;
    }
}

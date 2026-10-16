using System.ServiceModel;
using System.Xml;
using AdminClient = Erewhon.SoapClient.Admin.ErewhonWebServiceAdminClient;
using WebServiceClient = Erewhon.SoapClient.WebService.ErewhonWebServiceClient;

namespace Erewhon.SoapClient;

/// <summary>
/// Builds the generated <c>ErewhonWebService</c> and <c>ErewhonWebServiceAdmin</c> SOAP clients
/// from one base address.
/// </summary>
public sealed class ErewhonSoapClientFactory
{
    /// <summary>Path of the <c>ErewhonWebService</c> endpoint, relative to the base address.</summary>
    public const string WebServicePath = "soap/ErewhonWebService";

    /// <summary>Path of the <c>ErewhonWebServiceAdmin</c> endpoint, relative to the base address.</summary>
    public const string AdminServicePath = "soap/ErewhonWebServiceAdmin";

    /// <summary>Creates a factory for the services hosted under <paramref name="baseAddress"/>.</summary>
    /// <param name="baseAddress">Absolute <c>http</c> address of the host, with no query and no fragment.</param>
    /// <exception cref="ArgumentNullException"><paramref name="baseAddress"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="baseAddress"/> is not an absolute <c>http</c> address,
    /// or it carries a query or a fragment.</exception>
    public ErewhonSoapClientFactory(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        BaseAddress = baseAddress;
        WebServiceAddress = new EndpointAddress(BuildServiceUri(baseAddress, WebServicePath));
        AdminServiceAddress = new EndpointAddress(BuildServiceUri(baseAddress, AdminServicePath));
    }

    /// <summary>Creates a factory for the services hosted under the address text <paramref name="baseAddress"/>.</summary>
    /// <param name="baseAddress">Absolute <c>http</c> address of the host, with no query and no fragment.</param>
    /// <exception cref="ArgumentNullException"><paramref name="baseAddress"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="baseAddress"/> does not parse as an absolute URI,
    /// is not an <c>http</c> address, or carries a query or a fragment.</exception>
    public ErewhonSoapClientFactory(string baseAddress)
        : this(ParseBaseAddress(baseAddress))
    {
    }

    /// <summary>The base address the factory was created with.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The endpoint address of <c>ErewhonWebService</c>: the base address followed by <see cref="WebServicePath"/>.</summary>
    public EndpointAddress WebServiceAddress { get; }

    /// <summary>The endpoint address of <c>ErewhonWebServiceAdmin</c>: the base address followed by <see cref="AdminServicePath"/>.</summary>
    public EndpointAddress AdminServiceAddress { get; }

    /// <summary>
    /// Joins a base address and a service path with exactly one <c>/</c>: the scheme, authority and path of
    /// <paramref name="baseAddress"/> without trailing <c>/</c>, then <c>/</c>, then <paramref name="servicePath"/>
    /// without leading <c>/</c>. A path prefix in the base address is kept.
    /// </summary>
    /// <param name="baseAddress">Absolute <c>http</c> address, with no query and no fragment.</param>
    /// <param name="servicePath">Service path relative to the base address.</param>
    /// <returns>The absolute address of the service.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="baseAddress"/> or <paramref name="servicePath"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="baseAddress"/> is relative, is not an <c>http</c> address,
    /// or carries a query or a fragment; or <paramref name="servicePath"/> is empty or whitespace.</exception>
    public static Uri BuildServiceUri(Uri baseAddress, string servicePath)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("The base address must be an absolute URI.", nameof(baseAddress));
        }

        if (!string.Equals(baseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The base address must use the '{Uri.UriSchemeHttp}' scheme, not '{baseAddress.Scheme}'.",
                nameof(baseAddress));
        }

        if (!string.IsNullOrEmpty(baseAddress.Query) || !string.IsNullOrEmpty(baseAddress.Fragment))
        {
            throw new ArgumentException("The base address must not carry a query or a fragment.", nameof(baseAddress));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(servicePath);

        var root = baseAddress.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri(root + "/" + servicePath.TrimStart('/'), UriKind.Absolute);
    }

    /// <summary>
    /// Creates a new binding for both clients: SOAP 1.1 over plain HTTP with text encoding and no security,
    /// with the maximum received message size, buffer size and reader quotas, and every other setting at its default.
    /// </summary>
    /// <returns>A new <see cref="BasicHttpBinding"/> instance.</returns>
    public static BasicHttpBinding CreateBinding() => new(BasicHttpSecurityMode.None)
    {
        MaxReceivedMessageSize = int.MaxValue,
        MaxBufferSize = int.MaxValue,
        ReaderQuotas = XmlDictionaryReaderQuotas.Max,
    };

    /// <summary>
    /// Creates a new <c>ErewhonWebService</c> client on a new binding, addressed to <see cref="WebServiceAddress"/>.
    /// The caller owns the client and closes it.
    /// </summary>
    /// <returns>A new, unopened client.</returns>
    public WebServiceClient CreateWebServiceClient() => new(CreateBinding(), WebServiceAddress);

    /// <summary>
    /// Creates a new <c>ErewhonWebServiceAdmin</c> client on a new binding, addressed to <see cref="AdminServiceAddress"/>.
    /// The client carries the <c>createAgent</c>, <c>createClient</c>, <c>deleteAgent</c> and <c>deleteClient</c> operations.
    /// The caller owns the client and closes it.
    /// </summary>
    /// <returns>A new, unopened client.</returns>
    public AdminClient CreateAdminClient() => new(CreateBinding(), AdminServiceAddress);

    private static Uri ParseBaseAddress(string baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var parsed))
        {
            throw new ArgumentException($"'{baseAddress}' is not an absolute URI.", nameof(baseAddress));
        }

        return parsed;
    }
}

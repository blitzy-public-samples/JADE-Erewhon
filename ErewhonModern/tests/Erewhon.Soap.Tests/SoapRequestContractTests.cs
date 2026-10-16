using System.Net;
using System.Text;
using System.Xml.Linq;
using Erewhon.Api.Errors;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;

namespace Erewhon.Soap.Tests;

/// <summary>
/// The request contract of the <c>ErewhonWebService</c> and <c>ErewhonWebServiceAdmin</c> endpoints, exercised with raw
/// SOAP 1.1 envelopes over HTTP on the API of the test assembly: a body whose wrapper, parts or fields are not those
/// of the action's document/literal wrapped request message gets a <c>Client</c> fault and changes no stored agent or
/// client; the request shapes the XmlSerializer binds keep their answers; an unknown action keeps CoreWCF's
/// <c>ActionNotSupported</c> fault; and a method other than <c>GET</c> and <c>POST</c> gets 405 with
/// <c>Allow: GET, POST</c>. Each test starts from <see cref="TestCompanyBuilder.CreateTestDataAsync"/>.
/// </summary>
public sealed class SoapRequestContractTests : ErewhonTestBase
{
    private const string WebService = "ErewhonWebService";
    private const string AdminService = "ErewhonWebServiceAdmin";
    private const string ServiceNamespace = "urn:erewhon:soap";
    private const string SoapActionHeader = "SOAPAction";
    private const string XmlMediaType = "text/xml";
    private const string FixtureAgentName = "Test Agent";

    private const string EnvelopeStart =
        "<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/' "
        + "xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'><s:Body>";

    private const string EnvelopeEnd = "</s:Body></s:Envelope>";

    private const string UpdateFixtureAgentBody =
        "<updateAgent xmlns='urn:erewhon:soap'><pName>Test Agent</pName><pStreet>238 Ness Drive</pStreet>"
        + "<pCity>Shaldon</pCity><pCountry>United Kingdom</pCountry><pPhone>01626 873480</pPhone>"
        + "<pFax>01626 476764</pFax><pEmail>tabai@shaldon.uk</pEmail><pWebsite>www.shaldon.uk</pWebsite></updateAgent>";

    private const string DeleteFixtureAgentBody = "<deleteAgent xmlns='urn:erewhon:soap'><pName>Test Agent</pName></deleteAgent>";

    private static readonly XNamespace EnvelopeNamespace = "http://schemas.xmlsoap.org/soap/envelope/";

    private static readonly XNamespace ContractNamespace = ServiceNamespace;

    private HttpClient? _http;

    private HttpClient Http =>
        _http ?? throw new InvalidOperationException($"The HTTP client is created by {nameof(SetUpAsync)}.");

    /// <summary>
    /// Posts a body that departs from the request message of its action: the response is HTTP 500 with a SOAP 1.1
    /// fault whose code is <c>Client</c> and whose reason holds <paramref name="expectedReason"/>, which names the
    /// received and the expected element; the stored agents and clients, with their names, seven address fields and
    /// GUIDs, are those read before the call; and no <c>ErewhonUnhandledException</c> event is written.
    /// </summary>
    /// <param name="service">The service name, which is also the last segment of its path.</param>
    /// <param name="operation">The operation the SOAPAction names.</param>
    /// <param name="body">The content of the SOAP body.</param>
    /// <param name="expectedReason">Text the fault reason holds.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(WebService, "getAgentNames", "<getAgentNames xmlns='urn:wrong'/>",
        "The SOAP body element is {urn:wrong}getAgentNames; action urn:erewhon:soap/ErewhonWebService/getAgentNames takes the element {urn:erewhon:soap}getAgentNames.")]
    [InlineData(WebService, "getAgentNames", "<bogusElement xmlns='urn:nothing'/>",
        "The SOAP body element is {urn:nothing}bogusElement;")]
    [InlineData(WebService, "getAgentNames", "<getAgent xmlns='urn:erewhon:soap'><pName>Test Agent</pName></getAgent>",
        "The SOAP body element is {urn:erewhon:soap}getAgent; action urn:erewhon:soap/ErewhonWebService/getAgentNames")]
    [InlineData(WebService, "getClientNames", "<getClientNames xmlns='urn:wrong'/>",
        "The SOAP body element is {urn:wrong}getClientNames;")]
    [InlineData(WebService, "getAgent", "<getAgent xmlns='urn:wrong'><pName>Test Agent</pName></getAgent>",
        "The SOAP body element is {urn:wrong}getAgent;")]
    [InlineData(WebService, "getClient", "<getClient xmlns='urn:wrong'><pName>Test Client</pName></getClient>",
        "The SOAP body element is {urn:wrong}getClient;")]
    [InlineData(WebService, "getPhoto", "<getPhoto xmlns='urn:wrong'><pName>Test Client</pName></getPhoto>",
        "The SOAP body element is {urn:wrong}getPhoto;")]
    [InlineData(WebService, "updateAgent",
        "<updateAgent xmlns='urn:wrong'><pName>Test Agent</pName><pStreet>238 Ness Drive</pStreet></updateAgent>",
        "The SOAP body element is {urn:wrong}updateAgent;")]
    [InlineData(WebService, "updateAgent",
        "<updateAgent xmlns='urn:erewhon:soap'><pName>Test Agent</pName><pStreet>Street</pStreet><pCity>City</pCity>"
        + "<pCountry>Country</pCountry><pPhone>Phone</pPhone><pFax xmlns='urn:other'>44 2</pFax>"
        + "<pEmail>agent@email.com</pEmail><pWebsite xmlns=''>www.fieldplus.co.uk</pWebsite></updateAgent>",
        "The element {urn:erewhon:soap}updateAgent holds the element {urn:other}pFax; it takes only the elements {urn:erewhon:soap}pName, ")]
    [InlineData(WebService, "updateAgent",
        "<updateAgent xmlns='urn:erewhon:soap'><pName>Test Agent</pName><pWebsite xmlns=''>www.fieldplus.co.uk</pWebsite></updateAgent>",
        "The element {urn:erewhon:soap}updateAgent holds the element {}pWebsite;")]
    [InlineData(WebService, "updateClient",
        "<updateClient xmlns='urn:wrong'><pName>Test Client</pName><pFax>44 2</pFax></updateClient>",
        "The SOAP body element is {urn:wrong}updateClient;")]
    [InlineData(WebService, "updateAgentWithDTO",
        "<updateAgentWithDTO xmlns='urn:erewhon:soap'><pDTOAgent><name>Test Agent</name><fax xmlns='urn:other'>44 2</fax>"
        + "</pDTOAgent></updateAgentWithDTO>",
        "The element {urn:erewhon:soap}pDTOAgent holds the element {urn:other}fax; it takes only the elements {urn:erewhon:soap}guid, ")]
    [InlineData(WebService, "updateClientWithDTO",
        "<updateClientWithDTO xmlns='urn:erewhon:soap'><pDTOClient><name>Test Client</name><name>Test Agent</name>"
        + "</pDTOClient></updateClientWithDTO>",
        "The element {urn:erewhon:soap}pDTOClient holds the element {urn:erewhon:soap}name more than once.")]
    [InlineData(WebService, "getAgent", "<getAgent xmlns='urn:erewhon:soap'><pName><b>Test Agent</b></pName></getAgent>",
        "The element {urn:erewhon:soap}pName holds the element {urn:erewhon:soap}b; it takes text only.")]
    [InlineData(WebService, "getAgent",
        "<getAgent xmlns='urn:erewhon:soap'><pName>Test Agent</pName></getAgent><getAgent xmlns='urn:erewhon:soap'/>",
        "The SOAP body holds the element {urn:erewhon:soap}getAgent after the element {urn:erewhon:soap}getAgent;")]
    [InlineData(WebService, "getAgent", "",
        "The SOAP body is empty; action urn:erewhon:soap/ErewhonWebService/getAgent takes the element {urn:erewhon:soap}getAgent.")]
    [InlineData(AdminService, "createAgent",
        "<createAgent xmlns='urn:wrong:ns'><pName>QA Mal Agent</pName><pStreet>1 S</pStreet><pCity>C</pCity>"
        + "<pCountry>NZ</pCountry><pPhone>1</pPhone><pFax/><pEmail>a@b.nz</pEmail><pWebsite/></createAgent>",
        "The SOAP body element is {urn:wrong:ns}createAgent; action urn:erewhon:soap/ErewhonWebServiceAdmin/createAgent takes the element {urn:erewhon:soap}createAgent.")]
    [InlineData(AdminService, "createAgent",
        "<createAgent><pName>QA Mal Agent</pName><pStreet>1 S</pStreet><pCity>C</pCity><pCountry>NZ</pCountry>"
        + "<pPhone>1</pPhone><pEmail>a@b.nz</pEmail></createAgent>",
        "The SOAP body element is {}createAgent;")]
    [InlineData(AdminService, "createAgent",
        "<createAgent xmlns='urn:erewhon:soap'><x:pName xmlns:x='urn:wrong'>QA Mal Agent</x:pName><pStreet>1 S</pStreet>"
        + "<pCity>C</pCity><pCountry>NZ</pCountry><pPhone>1</pPhone><pEmail>a@b.nz</pEmail></createAgent>",
        "The element {urn:erewhon:soap}createAgent holds the element {urn:wrong}pName;")]
    [InlineData(AdminService, "deleteAgent", "<createAgent xmlns='urn:erewhon:soap'><pName>Test Agent</pName></createAgent>",
        "The SOAP body element is {urn:erewhon:soap}createAgent; action urn:erewhon:soap/ErewhonWebServiceAdmin/deleteAgent takes the element {urn:erewhon:soap}deleteAgent.")]
    [InlineData(AdminService, "createClient",
        "<createClient xmlns='urn:erewhon:soap'><pName>QA Mal Client</pName><pName>QA Other Client</pName>"
        + "<pStreet>1 S</pStreet><pCity>C</pCity><pCountry>NZ</pCountry><pPhone>1</pPhone><pEmail>a@b.nz</pEmail>"
        + "</createClient>",
        "The element {urn:erewhon:soap}createClient holds the element {urn:erewhon:soap}pName more than once.")]
    [InlineData(AdminService, "createAgent",
        "<createAgent xmlns='urn:erewhon:soap'><pName>QA Mal Agent</pName><pStreet>1 S</pStreet><pCity>C</pCity>"
        + "<pCountry>NZ</pCountry><pPhone>1</pPhone><pEmail>a@b.nz</pEmail><pExtra>1</pExtra></createAgent>",
        "The element {urn:erewhon:soap}createAgent holds the element {urn:erewhon:soap}pExtra;")]
    [InlineData(AdminService, "deleteClient", "<deleteClient xmlns='urn:erewhon:soap'><pName>Test Client</pNam></deleteClient>",
        "The SOAP body cannot be read by the XML reader of the binding; action urn:erewhon:soap/ErewhonWebServiceAdmin/deleteClient takes the element {urn:erewhon:soap}deleteClient.")]
    [InlineData(AdminService, "deleteAgent", "<deleteAgent xmlns='urn:erewhon:soap'><pName>Test Agent</pName><?note text?></deleteAgent>",
        "The SOAP body cannot be read by the XML reader of the binding; action urn:erewhon:soap/ErewhonWebServiceAdmin/deleteAgent takes the element {urn:erewhon:soap}deleteAgent.")]
    public async Task RejectedEnvelopeGetsClientFaultAndChangesNothing(
        string service,
        string operation,
        string body,
        string expectedReason)
    {
        IReadOnlyList<string> before = await ReadStoredPartiesAsync();

        using HttpResponseMessage response = await PostAsync(service, ActionOf(service, operation), body);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        (XName code, string reason) = await ReadFaultAsync(response);
        Assert.Equal(EnvelopeNamespace + "Client", code);
        Assert.Contains(expectedReason, reason, StringComparison.Ordinal);
        Assert.DoesNotContain(" at Erewhon.", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(" at CoreWCF.", reason, StringComparison.Ordinal);

        Assert.Equal(before, await ReadStoredPartiesAsync());
        Assert.DoesNotContain(LogSink.Events, IsUnhandledExceptionEvent);
    }

    /// <summary>
    /// Posts <c>getAgent</c> bodies the XmlSerializer binds: the response is HTTP 200 with no fault and the agent named
    /// <paramref name="expectedName"/>, or no agent when it is <see langword="null"/>.
    /// </summary>
    /// <param name="body">The content of the SOAP body.</param>
    /// <param name="expectedName">The name of the returned agent, or <see langword="null"/> for none.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("<getAgent xmlns='urn:erewhon:soap'><pName>Test Agent</pName></getAgent>", FixtureAgentName)]
    [InlineData("<e:getAgent xmlns:e='urn:erewhon:soap'><e:pName>Test Agent</e:pName></e:getAgent>", FixtureAgentName)]
    [InlineData(
        "<getAgent xmlns='urn:erewhon:soap' xmlns:x='urn:other' x:note='1'>  <pName x:note='2'><![CDATA[Test Agent]]></pName>"
        + " <!-- note --> </getAgent>",
        FixtureAgentName)]
    [InlineData("<getAgent xmlns='urn:erewhon:soap'/>", null)]
    [InlineData("<getAgent xmlns='urn:erewhon:soap'><pName xsi:nil='true'/></getAgent>", null)]
    public async Task AcceptedGetAgentShapesAnswerAsBefore(string body, string? expectedName)
    {
        using HttpResponseMessage response = await PostAsync(WebService, ActionOf(WebService, "getAgent"), body);
        string text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}: {text}");
        XElement result = Assert.Single(BodyOf(text).Elements(ContractNamespace + "getAgentResponse"));
        Assert.Equal(expectedName, result.Element(ContractNamespace + "getAgentResult")?.Element(ContractNamespace + "name")?.Value);
    }

    /// <summary>
    /// Posts <c>createAgent</c> with its eight parameters in reverse order and <c>pFax</c> as <c>xsi:nil</c>: the
    /// result has status 201 and a GUID, and the stored agent holds the sent name and address with an empty fax and
    /// that GUID.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task OutOfOrderCreateAgentParametersAreStored()
    {
        const string Body =
            "<createAgent xmlns='urn:erewhon:soap'><pWebsite>www.funchal.pt</pWebsite><pEmail>rowan@funchal.pt</pEmail>"
            + "<pFax xsi:nil='true'/><pPhone>291 225 023</pPhone><pCountry>Portugal</pCountry><pCity>Funchal</pCity>"
            + "<pStreet>Rua Dom Carlos I 62</pStreet><pName>Rowan Stracke</pName></createAgent>";

        using HttpResponseMessage response = await PostAsync(AdminService, ActionOf(AdminService, "createAgent"), Body);
        string text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}: {text}");
        XElement result = Assert.Single(
            BodyOf(text).Elements(ContractNamespace + "createAgentResponse").Elements(ContractNamespace + "createAgentResult"));
        Assert.Equal("201", result.Element(ContractNamespace + "statusCode")?.Value);
        string? guid = result.Element(ContractNamespace + "guid")?.Value;
        Assert.False(string.IsNullOrEmpty(guid));

        Assert.Contains(
            Describe("Agent", "Rowan Stracke", "Rua Dom Carlos I 62", "Funchal", "Portugal", "291 225 023", string.Empty,
                "rowan@funchal.pt", "www.funchal.pt", guid),
            await ReadStoredPartiesAsync());
    }

    /// <summary>
    /// Posts a <c>DTO*</c> update whose DTO part is absent: the request reaches the operation, which answers HTTP 500
    /// with the <c>Client</c> fault "Null object reference (1090)", and the stored agents and clients are those read
    /// before the call.
    /// </summary>
    /// <param name="operation">The <c>ErewhonWebService</c> operation the SOAPAction names.</param>
    /// <param name="body">The content of the SOAP body.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("updateAgentWithDTO", "<updateAgentWithDTO xmlns='urn:erewhon:soap'/>")]
    [InlineData("updateClientWithDTO", "<updateClientWithDTO xmlns='urn:erewhon:soap'>  <!-- no client --> </updateClientWithDTO>")]
    public async Task AbsentDtoPartStillReachesNullObjectFault(string operation, string body)
    {
        IReadOnlyList<string> before = await ReadStoredPartiesAsync();

        using HttpResponseMessage response = await PostAsync(WebService, ActionOf(WebService, operation), body);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        (XName code, string reason) = await ReadFaultAsync(response);
        Assert.Equal(EnvelopeNamespace + "Client", code);
        Assert.Equal("Null object reference (1090)", reason);
        Assert.Equal(before, await ReadStoredPartiesAsync());
    }

    /// <summary>
    /// Posts a body with a SOAPAction that is not a request action of the endpoint's contract, an empty one, or none:
    /// the response is HTTP 500 with CoreWCF's <c>ActionNotSupported</c> fault.
    /// </summary>
    /// <param name="service">The service name, which is also the last segment of its path.</param>
    /// <param name="soapAction">The SOAPAction header value, or <see langword="null"/> to send none.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData(WebService, "urn:erewhon:soap/ErewhonWebService/dropAll")]
    [InlineData(WebService, "urn:erewhon:soap/ErewhonWebServiceAdmin/createAgent")]
    [InlineData(AdminService, "urn:erewhon:soap/ErewhonWebService/getAgent")]
    [InlineData(WebService, "")]
    [InlineData(AdminService, null)]
    public async Task UnknownActionKeepsActionNotSupported(string service, string? soapAction)
    {
        using HttpResponseMessage response = await PostAsync(service, soapAction, "<getAgentNames xmlns='urn:wrong'/>");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        (XName code, _) = await ReadFaultAsync(response);
        Assert.Equal("ActionNotSupported", code.LocalName);
    }

    /// <summary>
    /// Sends <paramref name="method"/> to a SOAP service path with, unless the method is <c>HEAD</c>, an envelope and
    /// SOAPAction that write the fixture agent: <c>deleteAgent</c> on an <c>ErewhonWebServiceAdmin</c> path and
    /// <c>updateAgent</c> on an <c>ErewhonWebService</c> path. The response is 405 with an empty body and
    /// <c>Allow: GET, POST</c>, and the stored agents and clients are those read before the call.
    /// </summary>
    /// <param name="path">The request path, relative to the API's base address.</param>
    /// <param name="method">The HTTP method.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("soap/ErewhonWebService", "PUT")]
    [InlineData("soap/ErewhonWebService", "DELETE")]
    [InlineData("soap/ErewhonWebService", "PATCH")]
    [InlineData("soap/ErewhonWebService", "HEAD")]
    [InlineData("soap/ErewhonWebService", "OPTIONS")]
    [InlineData("soap/ErewhonWebServiceAdmin", "PUT")]
    [InlineData("soap/ErewhonWebServiceAdmin", "DELETE")]
    [InlineData("soap/ErewhonWebServiceAdmin", "PATCH")]
    [InlineData("soap/ErewhonWebServiceAdmin", "HEAD")]
    [InlineData("soap/ErewhonWebServiceAdmin", "OPTIONS")]
    [InlineData("SOAP/erewhonwebservice/", "PUT")]
    [InlineData("soap/ErewhonWebServiceAdmin/below?wsdl", "DELETE")]
    public async Task MethodOtherThanGetOrPostGets405(string path, string method)
    {
        IReadOnlyList<string> before = await ReadStoredPartiesAsync();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != HttpMethod.Head.Method)
        {
            bool admin = path.Contains(AdminService, StringComparison.OrdinalIgnoreCase);
            request.Content = Envelope(admin ? DeleteFixtureAgentBody : UpdateFixtureAgentBody);
            string action = admin ? ActionOf(AdminService, "deleteAgent") : ActionOf(WebService, "updateAgent");
            request.Headers.TryAddWithoutValidation(SoapActionHeader, $"\"{action}\"");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(new[] { "GET", "POST" }, response.Content.Headers.Allow);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(before, await ReadStoredPartiesAsync());
    }

    /// <summary>
    /// Gets a metadata document of a SOAP service: the response is 200 with an XML document whose target namespace
    /// is <c>urn:erewhon:soap</c>.
    /// </summary>
    /// <param name="pathAndQuery">The request path and query, relative to the API's base address.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("soap/ErewhonWebService?wsdl")]
    [InlineData("soap/ErewhonWebService?singleWsdl")]
    [InlineData("soap/ErewhonWebServiceAdmin?wsdl")]
    [InlineData("soap/ErewhonWebServiceAdmin?singleWsdl")]
    public async Task MetadataIsStillServedByGet(string pathAndQuery)
    {
        using HttpResponseMessage response = await Http.GetAsync(pathAndQuery);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(XmlMediaType, response.Content.Headers.ContentType?.MediaType);
        XDocument document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ServiceNamespace, document.Root?.Attribute("targetNamespace")?.Value);
    }

    /// <summary>
    /// Sends <paramref name="method"/> to <c>/soap/ErewhonWebServiceAdmin2</c>, a path next to a SOAP service path:
    /// the response is 404.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task NeighbouringPathStillAnswers404(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "soap/ErewhonWebServiceAdmin2");

        using HttpResponseMessage response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Builds <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which creates and binds its own company, then creates
    /// the HTTP client of <see cref="ErewhonTestBase.Api"/>.
    /// </summary>
    /// <returns>A task that completes when the fixtures and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
        _http = Api.CreateHttpClient();
    }

    /// <summary>Disposes the HTTP client, then deletes the test's companies through the base implementation.</summary>
    /// <returns>A task that completes when the client is disposed and the companies are deleted.</returns>
    protected override async Task TearDownAsync()
    {
        _http?.Dispose();
        _http = null;
        await base.TearDownAsync();
    }

    /// <summary>Returns the SOAPAction of <paramref name="operation"/> of <paramref name="service"/>.</summary>
    private static string ActionOf(string service, string operation) => $"{ServiceNamespace}/{service}/{operation}";

    /// <summary>Returns a SOAP 1.1 envelope whose body holds <paramref name="body"/>, as UTF-8 <c>text/xml</c>.</summary>
    private static StringContent Envelope(string body) =>
        new(EnvelopeStart + body + EnvelopeEnd, Encoding.UTF8, XmlMediaType);

    /// <summary>
    /// Posts an envelope holding <paramref name="body"/> to the path of <paramref name="service"/> with the SOAPAction
    /// <paramref name="soapAction"/> in quotes, or with no SOAPAction when it is <see langword="null"/>.
    /// </summary>
    private async Task<HttpResponseMessage> PostAsync(string service, string? soapAction, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"soap/{service}") { Content = Envelope(body) };
        if (soapAction is not null)
        {
            request.Headers.TryAddWithoutValidation(SoapActionHeader, $"\"{soapAction}\"");
        }

        return await Http.SendAsync(request);
    }

    /// <summary>Returns the SOAP body element of the envelope <paramref name="text"/>.</summary>
    private static XElement BodyOf(string text)
    {
        XElement? body = XDocument.Parse(text).Root?.Element(EnvelopeNamespace + "Body");
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// Returns the fault code, resolved to its namespace, and the reason of the SOAP 1.1 fault in
    /// <paramref name="response"/>.
    /// </summary>
    private static async Task<(XName Code, string Reason)> ReadFaultAsync(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        XElement? fault = BodyOf(text).Element(EnvelopeNamespace + "Fault");
        Assert.True(fault is not null, $"No SOAP fault in: {text}");

        XElement? code = fault.Element("faultcode");
        Assert.NotNull(code);
        string[] parts = code.Value.Split(':', 2);
        XNamespace codeNamespace = parts.Length == 2
            ? code.GetNamespaceOfPrefix(parts[0]) ?? XNamespace.None
            : code.GetDefaultNamespace();

        return (codeNamespace + parts[^1], fault.Element("faultstring")?.Value ?? string.Empty);
    }

    /// <summary>
    /// Returns the stored agents and then the stored clients of the fixture company, each in name order and described
    /// by <see cref="Describe"/>, read in a new scope of <see cref="ErewhonTestBase.Api"/> bound to that company.
    /// </summary>
    /// <exception cref="InvalidOperationException">The fixture company has not been created.</exception>
    private async Task<IReadOnlyList<string>> ReadStoredPartiesAsync()
    {
        Company company = Builder.MyCompany
            ?? throw new InvalidOperationException($"The fixture company is created by {nameof(SetUpAsync)}.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(company.Id);

        IReadOnlyList<Agent> agents = await scope.ServiceProvider.GetRequiredService<AgentService>().GetAgentsAsync();
        IReadOnlyList<Client> clients = await scope.ServiceProvider.GetRequiredService<ClientService>().GetClientsAsync();
        return
        [
            .. agents.Select(agent => Describe("Agent", agent.Name, agent.Address, agent.Guid)),
            .. clients.Select(client => Describe("Client", client.Name, client.Address, client.Guid)),
        ];
    }

    /// <summary>Returns <paramref name="kind"/>, the name, the seven fields of <paramref name="address"/> and the GUID.</summary>
    private static string Describe(string kind, string name, Address address, string? guid) =>
        Describe(kind, name, address.Street, address.City, address.Country, address.Phone, address.Fax, address.Email,
            address.WebSite, guid);

    /// <summary>Returns the given values separated by <c> ; </c>, with <see langword="null"/> written as <c>(null)</c>.</summary>
    private static string Describe(params string?[] values) =>
        string.Join(" ; ", values.Select(value => value ?? "(null)"));

    /// <summary>Returns whether the event's <c>EventId</c> is <see cref="ErrorOutcomeMiddleware.UnhandledExceptionEventId"/>.</summary>
    private static bool IsUnhandledExceptionEvent(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("EventId", out LogEventPropertyValue? eventId)
        && eventId is StructureValue structure
        && structure.Properties.Any(property =>
            property.Name == "Id" && Equals(property.Value, new ScalarValue(ErrorOutcomeMiddleware.UnhandledExceptionEventId.Id)));
}

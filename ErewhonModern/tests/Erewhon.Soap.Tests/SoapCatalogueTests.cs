using System.ServiceModel;
using Erewhon.Api.Soap;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;
using Admin = Erewhon.SoapClient.Admin;
using WebService = Erewhon.SoapClient.WebService;

namespace Erewhon.Soap.Tests;

/// <summary>
/// The SOAP behaviour-catalogue entries that no JADE SOAP test pins, run over the real <c>ErewhonWebService</c> and
/// <c>ErewhonWebServiceAdmin</c> endpoints through the generated clients: B-51, the status constants and the absence
/// of status 403 from every reachable operation outcome, and the SOAP cases of B-22, error 1035 for a street longer
/// than its 51 characters. Each test starts from <see cref="TestCompanyBuilder.CreateTestDataAsync"/>.
/// </summary>
public sealed class SoapCatalogueTests : ErewhonTestBase
{
    private const string UnknownAgentName = "Some Agent That Does Not Exist";
    private const string UnknownClientName = "Client Name Does Not Exist";
    private const string FixtureAgentName = "Test Agent";
    private const string FixtureClientName = "Test Client";
    private const string FixtureStreet = "Street";
    private const string NewAgentName = "Rowan Stracke";
    private const string NewClientName = "Dariush Kamran";
    private const string StringTooLongFault = "String too long (1035)";
    private const int OverLongStreetLength = 60;

    private static readonly SoapAddress TamilNaduAddress = new(
        "1, TVS School Road",
        "Tamil Nadu",
        "India",
        "044 2476 4393",
        "044 2476 7364",
        "tabai@tamil.in",
        "www.tamil.in");

    private static readonly SoapAddress FunchalAddress = new(
        "Rua Dom Carlos I 62",
        "Funchal",
        "Portugal",
        "291 225 023",
        "291 244 2464",
        "rowan@funchal.pt",
        "www.funchal.pt");

    private static readonly SoapAddress MashhadAddress = new(
        "No. 13, Khorshid Trade Ctr, Ahmad Abad St",
        "Mashhad City",
        "Iran",
        "0511 841 4543",
        "0511 324 2464",
        "Dariush@iran.ir",
        "www.iran.ir");

    private WebService.ErewhonWebServiceClient? _webClient;
    private Admin.ErewhonWebServiceAdminClient? _adminClient;

    private WebService.ErewhonWebServiceClient WebClient =>
        _webClient ?? throw new InvalidOperationException($"The web service client is created by {nameof(SetUpAsync)}.");

    private Admin.ErewhonWebServiceAdminClient AdminClient =>
        _adminClient ?? throw new InvalidOperationException($"The admin service client is created by {nameof(SetUpAsync)}.");

    /// <summary>
    /// Every status constant holds its JADE value, and no outcome of the eight <c>DTOResult</c> operations reached
    /// below is 403: <c>updateAgent</c>, <c>updateAgentWithDTO</c>, <c>updateClient</c> and <c>updateClientWithDTO</c>
    /// for an unknown name (404), a valid address (200) and an empty street (400); <c>createAgent</c> and
    /// <c>createClient</c> for an existing name (409), a new name (201) and an empty name (400); <c>deleteAgent</c>
    /// and <c>deleteClient</c> for an unknown name (404) and the name created before (200).
    /// </summary>
    /// <returns>A task that completes when the sweep has run.</returns>
    [Fact]
    public async Task B51_ErrorUnauthorizedIs403()
    {
        Assert.Equal(403, SoapStatusCodes.ErrorUnauthorized);
        Assert.Equal(403, SoapStatusCodes.ErrorForbidden);
        Assert.Equal(400, SoapStatusCodes.ErrorBadRequest);
        Assert.Equal(409, SoapStatusCodes.ErrorConflict);
        Assert.Equal(404, SoapStatusCodes.ErrorNotFound);
        Assert.Equal(500, SoapStatusCodes.ErrorServerError);
        Assert.Equal(201, SoapStatusCodes.StatusCreated);
        Assert.Equal(200, SoapStatusCodes.StatusSuccess);

        SoapAddress emptyStreet = TamilNaduAddress with { Street = string.Empty };
        string streetRequired = MessageCatalog.Get(MessageKeys.AddressStreetRequired);

        SweepStep[] steps =
        [
            new("updateAgent: unknown agent",
                SoapStatusCodes.ErrorNotFound,
                [MessageCatalog.Get(MessageKeys.AgentNameNotFound, UnknownAgentName)],
                async () => Read(await UpdateAgentAsync(UnknownAgentName, TamilNaduAddress))),
            new("updateAgent: valid address",
                SoapStatusCodes.StatusSuccess,
                [],
                async () => Read(await UpdateAgentAsync(FixtureAgentName, TamilNaduAddress))),
            new("updateAgent: empty street",
                SoapStatusCodes.ErrorBadRequest,
                [streetRequired],
                async () => Read(await UpdateAgentAsync(FixtureAgentName, emptyStreet))),
            new("updateAgentWithDTO: unknown agent",
                SoapStatusCodes.ErrorNotFound,
                [MessageCatalog.Get(MessageKeys.AgentNameNotFound, UnknownAgentName)],
                async () => Read(await WebClient.updateAgentWithDTOAsync(AgentDto(UnknownAgentName, TamilNaduAddress)))),
            new("updateAgentWithDTO: valid address",
                SoapStatusCodes.StatusSuccess,
                [],
                async () => Read(await WebClient.updateAgentWithDTOAsync(AgentDto(FixtureAgentName, TamilNaduAddress)))),
            new("updateAgentWithDTO: empty street",
                SoapStatusCodes.ErrorBadRequest,
                [streetRequired],
                async () => Read(await WebClient.updateAgentWithDTOAsync(AgentDto(FixtureAgentName, emptyStreet)))),
            new("updateClient: unknown client",
                SoapStatusCodes.ErrorNotFound,
                [MessageCatalog.Get(MessageKeys.ClientNameNotFound, UnknownClientName)],
                async () => Read(await UpdateClientAsync(UnknownClientName, TamilNaduAddress))),
            new("updateClient: valid address",
                SoapStatusCodes.StatusSuccess,
                [],
                async () => Read(await UpdateClientAsync(FixtureClientName, TamilNaduAddress))),
            new("updateClient: empty street",
                SoapStatusCodes.ErrorBadRequest,
                [streetRequired],
                async () => Read(await UpdateClientAsync(FixtureClientName, emptyStreet))),
            new("updateClientWithDTO: unknown client",
                SoapStatusCodes.ErrorNotFound,
                [MessageCatalog.Get(MessageKeys.ClientNameNotFound, UnknownClientName)],
                async () => Read(await WebClient.updateClientWithDTOAsync(ClientDto(UnknownClientName, TamilNaduAddress)))),
            new("updateClientWithDTO: valid address",
                SoapStatusCodes.StatusSuccess,
                [],
                async () => Read(await WebClient.updateClientWithDTOAsync(ClientDto(FixtureClientName, TamilNaduAddress)))),
            new("updateClientWithDTO: empty street",
                SoapStatusCodes.ErrorBadRequest,
                [streetRequired],
                async () => Read(await WebClient.updateClientWithDTOAsync(ClientDto(FixtureClientName, emptyStreet)))),
            new("createAgent: existing agent",
                SoapStatusCodes.ErrorConflict,
                [MessageCatalog.Get(MessageKeys.AgentNameAlreadyExists, FixtureAgentName)],
                async () => Read(await CreateAgentAsync(FixtureAgentName, FunchalAddress))),
            new("createAgent: new agent",
                SoapStatusCodes.StatusCreated,
                [],
                async () => Read(await CreateAgentAsync(NewAgentName, FunchalAddress))),
            new("createAgent: empty name",
                SoapStatusCodes.ErrorBadRequest,
                [MessageCatalog.Get(MessageKeys.AgentNameRequired)],
                async () => Read(await CreateAgentAsync(string.Empty, FunchalAddress))),
            new("createClient: existing client",
                SoapStatusCodes.ErrorConflict,
                [MessageCatalog.Get(MessageKeys.ClientNameAlreadyExists, FixtureClientName)],
                async () => Read(await CreateClientAsync(FixtureClientName, MashhadAddress))),
            new("createClient: new client",
                SoapStatusCodes.StatusCreated,
                [],
                async () => Read(await CreateClientAsync(NewClientName, MashhadAddress))),
            new("createClient: empty name",
                SoapStatusCodes.ErrorBadRequest,
                [MessageCatalog.Get(MessageKeys.ClientNameRequired)],
                async () => Read(await CreateClientAsync(string.Empty, MashhadAddress))),
            new("deleteAgent: unknown agent",
                SoapStatusCodes.ErrorNotFound,
                [MessageCatalog.Get(MessageKeys.AgentNameNotFound, UnknownAgentName)],
                async () => Read(await AdminClient.deleteAgentAsync(UnknownAgentName))),
            new("deleteAgent: created agent",
                SoapStatusCodes.StatusSuccess,
                [],
                async () => Read(await AdminClient.deleteAgentAsync(NewAgentName))),
            new("deleteClient: unknown client",
                SoapStatusCodes.ErrorNotFound,
                [MessageCatalog.Get(MessageKeys.ClientNameNotFound, UnknownClientName)],
                async () => Read(await AdminClient.deleteClientAsync(UnknownClientName))),
            new("deleteClient: created client",
                SoapStatusCodes.StatusSuccess,
                [],
                async () => Read(await AdminClient.deleteClientAsync(NewClientName))),
        ];

        var actual = new List<SweepOutcome>(steps.Length);
        foreach (SweepStep step in steps)
        {
            DtoOutcome outcome = await step.Call();
            actual.Add(new SweepOutcome(step.Branch, outcome.StatusCode, JoinErrors(outcome.Errors)));
        }

        // Each branch returns its expected status code and errors, in sweep order.
        Assert.Equal(
            steps.Select(step => new SweepOutcome(step.Branch, step.ExpectedStatusCode, JoinErrors(step.ExpectedErrors))),
            actual);

        // No reached branch returns 403.
        Assert.All(actual, outcome =>
        {
            Assert.NotEqual(SoapStatusCodes.ErrorUnauthorized, outcome.StatusCode);
            Assert.NotEqual(SoapStatusCodes.ErrorForbidden, outcome.StatusCode);
        });
    }

    /// <summary>
    /// <c>updateAgentWithDTO</c> with a 60-character street faults with "String too long (1035)" for an agent name that
    /// does not exist, returning no <c>DTOResult</c>.
    /// </summary>
    /// <returns>A task that completes when the call has faulted.</returns>
    [Fact]
    public async Task B22_DtoLongStreetFaultsBeforeLookup()
    {
        WebService.DTOAgent dto = AgentDto(UnknownAgentName, TamilNaduAddress with { Street = OverLongStreet() });

        FaultException fault = await Assert.ThrowsAnyAsync<FaultException>(() => WebClient.updateAgentWithDTOAsync(dto));

        Assert.Contains(StringTooLongFault, fault.Message, StringComparison.Ordinal);
        Assert.Contains(StringTooLongFault, fault.Reason.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>updateAgent</c> with a 60-character street for an agent name that does not exist returns status 404 with the
    /// single error "Agent Some Agent That Does Not Exist not found" and no GUID.
    /// </summary>
    /// <returns>A task that completes when the result has been checked.</returns>
    [Fact]
    public async Task B22_ScalarUnknownAgentWithLongStreetIs404()
    {
        WebService.DTOResult result = await UpdateAgentAsync(
            UnknownAgentName,
            TamilNaduAddress with { Street = OverLongStreet() });

        Assert.Equal(SoapStatusCodes.ErrorNotFound, result.statusCode);
        string error = Assert.Single(result.allErrors ?? []);
        Assert.Equal(MessageCatalog.Get(MessageKeys.AgentNameNotFound, UnknownAgentName), error);
        Assert.True(JadeValues.IsNull(result.guid), $"Expected no GUID, got '{result.guid}'.");
    }

    /// <summary>
    /// <c>updateAgent</c> with a 60-character street for the fixture agent faults with "String too long (1035)", and
    /// the agent's stored street is still "Street".
    /// </summary>
    /// <returns>A task that completes when the fault and the stored street have been checked.</returns>
    [Fact]
    public async Task B22_ScalarKnownAgentWithLongStreetFaults()
    {
        var agent = Builder.MyAgent;
        Assert.NotNull(agent);
        Assert.Equal(FixtureAgentName, agent.Name);

        FaultException fault = await Assert.ThrowsAnyAsync<FaultException>(() =>
            UpdateAgentAsync(agent.Name, TamilNaduAddress with { Street = OverLongStreet() }));

        Assert.Contains(StringTooLongFault, fault.Message, StringComparison.Ordinal);
        Assert.Contains(StringTooLongFault, fault.Reason.ToString(), StringComparison.Ordinal);

        // The stored address is unchanged.
        Assert.Equal(FixtureStreet, await ReadStoredAgentStreetAsync(agent.Name));
    }

    /// <summary>
    /// Builds <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which creates and binds its own company, then
    /// creates both SOAP clients from one factory addressed to <see cref="ErewhonTestBase.Api"/>.
    /// </summary>
    /// <returns>A task that completes when the fixtures and clients exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();

        var factory = Api.CreateSoapClientFactory();
        _webClient = factory.CreateWebServiceClient();
        _adminClient = factory.CreateAdminClient();
    }

    /// <summary>
    /// Closes both SOAP clients, aborting a client that is faulted or fails to close, then deletes the test's
    /// companies through the base implementation.
    /// </summary>
    /// <returns>A task that completes when the clients are closed and the companies are deleted.</returns>
    protected override async Task TearDownAsync()
    {
        try
        {
            await CloseClientAsync(_webClient);
            await CloseClientAsync(_adminClient);
        }
        finally
        {
            _webClient = null;
            _adminClient = null;
            await base.TearDownAsync();
        }
    }

    /// <summary>
    /// Closes <paramref name="client"/>; aborts it instead when it is faulted, or when closing throws a
    /// <see cref="CommunicationException"/> or a <see cref="TimeoutException"/>. Does nothing for
    /// <see langword="null"/>.
    /// </summary>
    private static async Task CloseClientAsync<TChannel>(ClientBase<TChannel>? client)
        where TChannel : class
    {
        if (client is null)
        {
            return;
        }

        if (client.State == CommunicationState.Faulted)
        {
            client.Abort();
            return;
        }

        try
        {
            await client.CloseAsync();
        }
        catch (CommunicationException)
        {
            client.Abort();
        }
        catch (TimeoutException)
        {
            client.Abort();
        }
    }

    /// <summary>Returns a street of 60 characters, longer than the 51 of <c>Address.street</c>.</summary>
    private static string OverLongStreet() => new('S', OverLongStreetLength);

    /// <summary>Returns the agent data for <paramref name="name"/> at <paramref name="address"/>, with no GUID.</summary>
    private static WebService.DTOAgent AgentDto(string name, SoapAddress address) =>
        new()
        {
            name = name,
            street = address.Street,
            city = address.City,
            country = address.Country,
            phone = address.Phone,
            fax = address.Fax,
            email = address.Email,
            website = address.Website,
        };

    /// <summary>Returns the client data for <paramref name="name"/> at <paramref name="address"/>, with no GUID.</summary>
    private static WebService.DTOClient ClientDto(string name, SoapAddress address) =>
        new()
        {
            name = name,
            street = address.Street,
            city = address.City,
            country = address.Country,
            phone = address.Phone,
            fax = address.Fax,
            email = address.Email,
            website = address.Website,
        };

    /// <summary>Returns the status code and errors of a web service result.</summary>
    private static DtoOutcome Read(WebService.DTOResult result) => new(result.statusCode, result.allErrors ?? []);

    /// <summary>Returns the status code and errors of an admin service result.</summary>
    private static DtoOutcome Read(Admin.DTOResult result) => new(result.statusCode, result.allErrors ?? []);

    /// <summary>Returns <paramref name="errors"/> in order, separated by <c> | </c>.</summary>
    private static string JoinErrors(IReadOnlyList<string> errors) => string.Join(" | ", errors);

    /// <summary>Calls <c>updateAgent</c> with <paramref name="name"/> and the seven values of <paramref name="address"/>.</summary>
    private Task<WebService.DTOResult> UpdateAgentAsync(string name, SoapAddress address) =>
        WebClient.updateAgentAsync(
            name, address.Street, address.City, address.Country, address.Phone, address.Fax, address.Email, address.Website);

    /// <summary>Calls <c>updateClient</c> with <paramref name="name"/> and the seven values of <paramref name="address"/>.</summary>
    private Task<WebService.DTOResult> UpdateClientAsync(string name, SoapAddress address) =>
        WebClient.updateClientAsync(
            name, address.Street, address.City, address.Country, address.Phone, address.Fax, address.Email, address.Website);

    /// <summary>Calls <c>createAgent</c> with <paramref name="name"/> and the seven values of <paramref name="address"/>.</summary>
    private Task<Admin.DTOResult> CreateAgentAsync(string name, SoapAddress address) =>
        AdminClient.createAgentAsync(
            name, address.Street, address.City, address.Country, address.Phone, address.Fax, address.Email, address.Website);

    /// <summary>Calls <c>createClient</c> with <paramref name="name"/> and the seven values of <paramref name="address"/>.</summary>
    private Task<Admin.DTOResult> CreateClientAsync(string name, SoapAddress address) =>
        AdminClient.createClientAsync(
            name, address.Street, address.City, address.Country, address.Phone, address.Fax, address.Email, address.Website);

    /// <summary>
    /// Returns the stored street of the agent named <paramref name="name"/> in the fixture company, read through
    /// <see cref="AgentService.FindByNameAsync"/> in a new scope of <see cref="ErewhonTestBase.Api"/>, or
    /// <see langword="null"/> when there is no such agent.
    /// </summary>
    private async Task<string?> ReadStoredAgentStreetAsync(string name)
    {
        var company = Builder.MyCompany ?? throw new InvalidOperationException(
            $"The fixture company is created by {nameof(SetUpAsync)}.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(company.Id);

        var agent = await scope.ServiceProvider.GetRequiredService<AgentService>().FindByNameAsync(name);
        return agent?.Address.Street;
    }

    /// <summary>The seven address values an operation sends, in the order of its parameters.</summary>
    private sealed record SoapAddress(
        string Street,
        string City,
        string Country,
        string Phone,
        string Fax,
        string Email,
        string Website);

    /// <summary>The status code and errors of one <c>DTOResult</c>.</summary>
    private sealed record DtoOutcome(int StatusCode, IReadOnlyList<string> Errors);

    /// <summary>One branch of the B-51 sweep: its expected status code and errors, and the call that reaches it.</summary>
    private sealed record SweepStep(
        string Branch,
        int ExpectedStatusCode,
        IReadOnlyList<string> ExpectedErrors,
        Func<Task<DtoOutcome>> Call);

    /// <summary>The status code and joined errors one branch of the B-51 sweep returned or is expected to return.</summary>
    private sealed record SweepOutcome(string Branch, int StatusCode, string Errors);
}

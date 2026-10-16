using System.Globalization;
using System.ServiceModel;
using Erewhon.Api.Errors;
using Erewhon.Api.Soap;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serilog.Events;
using Admin = Erewhon.SoapClient.Admin;
using WebService = Erewhon.SoapClient.WebService;

namespace Erewhon.Soap.Tests;

/// <summary>
/// The SOAP behaviour-catalogue entries, and parts of entries, that no JADE SOAP test pins, run over the real
/// <c>ErewhonWebService</c> and <c>ErewhonWebServiceAdmin</c> endpoints through the generated clients: B-50, status
/// 500 with <c>$CouldNotDeleteAgent</c> or <c>$CouldNotDeleteClient</c> when <c>deleteAgent</c> or
/// <c>deleteClient</c> cannot take the company's agent or client lock; B-51, the status constants and the absence of
/// status 403 from every reachable operation outcome; the SOAP cases of B-22, error 1035 for a street longer than its
/// 51 characters; B-47 with Q-13, the photo <c>getPhoto</c> returns among a client's sales, that of the last
/// retail sale whose item has a photo; and the unknown-name outcomes of B-45, B-47 and B-48 for a name holding the NUL
/// character, with the fault each of those operations gives for such a name when no company exists. Each test starts
/// from <see cref="TestCompanyBuilder.CreateTestDataAsync"/>.
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
    private const string PhotoClientName = "Aroha Ngata";
    private const string StringTooLongFault = "String too long (1035)";
    private const string NoStoredGuid = "(no stored GUID)";
    private const string LockTimeoutKey = "Erewhon:Locks:TimeoutMs";
    private const string ShortLockWaitMs = "500";
    private const int OverLongStreetLength = 60;
    private const string EventIdProperty = "EventId";
    private const string ErrorCodeProperty = "ErrorCode";

    private static readonly SoapAddress FixtureAgentAddress = new(
        FixtureStreet,
        "City",
        "Country",
        "Phone",
        "Fax",
        "agent@email.com",
        "www.website.com");

    private static readonly SoapAddress FixtureClientAddress = new(
        FixtureStreet,
        "City",
        "Country",
        "Phone",
        "Fax",
        "client@email.com",
        "www.website.com");

    private static readonly SoapAddress ShaldonAddress = new(
        "238 Ness Drive",
        "Shaldon",
        "United Kingdon",
        "01626 873480",
        "01626 476764",
        "tabai@shaldon.uk",
        "www.shaldon.uk");

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

    private Region FixtureRegion =>
        Builder.MyRegion ?? throw new InvalidOperationException($"The fixture region is created by {nameof(SetUpAsync)}.");

    private Agent FixtureAgent =>
        Builder.MyAgent ?? throw new InvalidOperationException($"The fixture agent is created by {nameof(SetUpAsync)}.");

    /// <summary>An <c>ErewhonWebService</c> operation that looks an agent or a client up by the name it is sent.</summary>
    public enum NameLookupOperation
    {
        /// <summary><c>getAgent</c>.</summary>
        GetAgent,

        /// <summary><c>getClient</c>.</summary>
        GetClient,

        /// <summary><c>getPhoto</c>.</summary>
        GetPhoto,

        /// <summary><c>updateAgent</c>.</summary>
        UpdateAgent,

        /// <summary><c>updateAgentWithDTO</c>.</summary>
        UpdateAgentWithDto,

        /// <summary><c>updateClient</c>.</summary>
        UpdateClient,

        /// <summary><c>updateClientWithDTO</c>.</summary>
        UpdateClientWithDto,
    }

    /// <summary>Where the NUL character sits in the fixture agent's or client's name.</summary>
    public enum NulPlacement
    {
        /// <summary>In place of the name's space: "Test\0Agent", "Test\0Client".</summary>
        InPlaceOfSpace,

        /// <summary>After the name's last character: "Test Agent\0", "Test Client\0".</summary>
        AfterName,
    }

    /// <summary>Every <see cref="NameLookupOperation"/> with every <see cref="NulPlacement"/>.</summary>
    public static TheoryData<NameLookupOperation, NulPlacement> NulNameLookups
    {
        get
        {
            var data = new TheoryData<NameLookupOperation, NulPlacement>();
            foreach (NameLookupOperation operation in Enum.GetValues<NameLookupOperation>())
            {
                foreach (NulPlacement placement in Enum.GetValues<NulPlacement>())
                {
                    data.Add(operation, placement);
                }
            }

            return data;
        }
    }

    /// <summary>Every <see cref="NameLookupOperation"/>.</summary>
    public static TheoryData<NameLookupOperation> NameLookupOperations
    {
        get
        {
            var data = new TheoryData<NameLookupOperation>();
            foreach (NameLookupOperation operation in Enum.GetValues<NameLookupOperation>())
            {
                data.Add(operation);
            }

            return data;
        }
    }

    /// <summary>
    /// Every status constant holds its JADE value, and no outcome of the eight <c>DTOResult</c> operations reached
    /// below is 403: <c>updateAgent</c>, <c>updateAgentWithDTO</c>, <c>updateClient</c> and <c>updateClientWithDTO</c>
    /// for an unknown name (404), a valid address (200) and an empty street (400); <c>createAgent</c> and
    /// <c>createClient</c> for an existing name (409), a new name (201) and an empty name (400); <c>deleteAgent</c>
    /// and <c>deleteClient</c> for an unknown name (404) and the name created before (200). Each outcome also carries
    /// its GUID: the fixture agent's or client's GUID for a 200 update, the GUID stored for the new row for a 201
    /// create, and none for every other outcome. After each call the company's stored agents (agent operations) or
    /// clients (client operations), read in a new scope, hold the expected names, seven address values and GUIDs: a
    /// 200 update stores the sent address under the unchanged name and GUID, a 201 create adds a row with the sent name
    /// and address and the returned GUID, a 200 delete removes the row, and every other outcome leaves the rows as the
    /// previous call left them.
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

        var fixtureAgent = Builder.MyAgent;
        var fixtureClient = Builder.MyClient;
        Assert.NotNull(fixtureAgent);
        Assert.NotNull(fixtureClient);
        Assert.False(JadeValues.IsNull(fixtureAgent.Guid));
        Assert.False(JadeValues.IsNull(fixtureClient.Guid));

        string agentGuid = fixtureAgent.Guid;
        string clientGuid = fixtureClient.Guid;
        var agentAtFixture = new StoredParty(FixtureAgentName, FixtureAgentAddress, agentGuid);
        StoredParty agentAtTamilNadu = agentAtFixture with { Address = TamilNaduAddress };
        StoredParty agentAtShaldon = agentAtFixture with { Address = ShaldonAddress };
        var clientAtFixture = new StoredParty(FixtureClientName, FixtureClientAddress, clientGuid);
        StoredParty clientAtTamilNadu = clientAtFixture with { Address = TamilNaduAddress };
        StoredParty clientAtShaldon = clientAtFixture with { Address = ShaldonAddress };

        string? createdAgentGuid = null;
        string? createdClientGuid = null;
        StoredParty CreatedAgent() => new(NewAgentName, FunchalAddress, createdAgentGuid);
        StoredParty CreatedClient() => new(NewClientName, MashhadAddress, createdClientGuid);

        SoapAddress emptyStreet = FunchalAddress with { Street = string.Empty };
        string streetRequired = MessageCatalog.Get(MessageKeys.AddressStreetRequired);

        SweepStep[] steps =
        [
            new("updateAgent: unknown agent",
                SoapStatusCodes.ErrorNotFound,
                [MessageCatalog.Get(MessageKeys.AgentNameNotFound, UnknownAgentName)],
                async () => Read(await UpdateAgentAsync(UnknownAgentName, TamilNaduAddress)),
                ReadStoredAgentsAsync,
                _ => null,
                () => [agentAtFixture]),
            new("updateAgent: valid address",
                SoapStatusCodes.StatusSuccess,
                [],
                async () => Read(await UpdateAgentAsync(FixtureAgentName, TamilNaduAddress)),
                ReadStoredAgentsAsync,
                _ => agentGuid,
                () => [agentAtTamilNadu]),
            new("updateAgent: empty street",
                SoapStatusCodes.ErrorBadRequest,
                [streetRequired],
                async () => Read(await UpdateAgentAsync(FixtureAgentName, emptyStreet)),
                ReadStoredAgentsAsync,
                _ => null,
                () => [agentAtTamilNadu]),
            new("updateAgentWithDTO: unknown agent",
                SoapStatusCodes.ErrorNotFound,
                [MessageCatalog.Get(MessageKeys.AgentNameNotFound, UnknownAgentName)],
                async () => Read(await WebClient.updateAgentWithDTOAsync(AgentDto(UnknownAgentName, TamilNaduAddress))),
                ReadStoredAgentsAsync,
                _ => null,
                () => [agentAtTamilNadu]),
            new("updateAgentWithDTO: valid address",
                SoapStatusCodes.StatusSuccess,
                [],
                async () => Read(await WebClient.updateAgentWithDTOAsync(AgentDto(FixtureAgentName, ShaldonAddress))),
                ReadStoredAgentsAsync,
                _ => agentGuid,
                () => [agentAtShaldon]),
            new("updateAgentWithDTO: empty street",
                SoapStatusCodes.ErrorBadRequest,
                [streetRequired],
                async () => Read(await WebClient.updateAgentWithDTOAsync(AgentDto(FixtureAgentName, emptyStreet))),
                ReadStoredAgentsAsync,
                _ => null,
                () => [agentAtShaldon]),
            new("updateClient: unknown client",
                SoapStatusCodes.ErrorNotFound,
                [MessageCatalog.Get(MessageKeys.ClientNameNotFound, UnknownClientName)],
                async () => Read(await UpdateClientAsync(UnknownClientName, TamilNaduAddress)),
                ReadStoredClientsAsync,
                _ => null,
                () => [clientAtFixture]),
            new("updateClient: valid address",
                SoapStatusCodes.StatusSuccess,
                [],
                async () => Read(await UpdateClientAsync(FixtureClientName, TamilNaduAddress)),
                ReadStoredClientsAsync,
                _ => clientGuid,
                () => [clientAtTamilNadu]),
            new("updateClient: empty street",
                SoapStatusCodes.ErrorBadRequest,
                [streetRequired],
                async () => Read(await UpdateClientAsync(FixtureClientName, emptyStreet)),
                ReadStoredClientsAsync,
                _ => null,
                () => [clientAtTamilNadu]),
            new("updateClientWithDTO: unknown client",
                SoapStatusCodes.ErrorNotFound,
                [MessageCatalog.Get(MessageKeys.ClientNameNotFound, UnknownClientName)],
                async () => Read(await WebClient.updateClientWithDTOAsync(ClientDto(UnknownClientName, TamilNaduAddress))),
                ReadStoredClientsAsync,
                _ => null,
                () => [clientAtTamilNadu]),
            new("updateClientWithDTO: valid address",
                SoapStatusCodes.StatusSuccess,
                [],
                async () => Read(await WebClient.updateClientWithDTOAsync(ClientDto(FixtureClientName, ShaldonAddress))),
                ReadStoredClientsAsync,
                _ => clientGuid,
                () => [clientAtShaldon]),
            new("updateClientWithDTO: empty street",
                SoapStatusCodes.ErrorBadRequest,
                [streetRequired],
                async () => Read(await WebClient.updateClientWithDTOAsync(ClientDto(FixtureClientName, emptyStreet))),
                ReadStoredClientsAsync,
                _ => null,
                () => [clientAtShaldon]),
            new("createAgent: existing agent",
                SoapStatusCodes.ErrorConflict,
                [MessageCatalog.Get(MessageKeys.AgentNameAlreadyExists, FixtureAgentName)],
                async () => Read(await CreateAgentAsync(FixtureAgentName, FunchalAddress)),
                ReadStoredAgentsAsync,
                _ => null,
                () => [agentAtShaldon]),
            new("createAgent: new agent",
                SoapStatusCodes.StatusCreated,
                [],
                async () =>
                {
                    DtoOutcome outcome = Read(await CreateAgentAsync(NewAgentName, FunchalAddress));
                    createdAgentGuid = outcome.Guid;
                    return outcome;
                },
                ReadStoredAgentsAsync,
                stored => StoredGuidOf(stored, NewAgentName),
                () => [CreatedAgent(), agentAtShaldon]),
            new("createAgent: empty name",
                SoapStatusCodes.ErrorBadRequest,
                [MessageCatalog.Get(MessageKeys.AgentNameRequired)],
                async () => Read(await CreateAgentAsync(string.Empty, FunchalAddress)),
                ReadStoredAgentsAsync,
                _ => null,
                () => [CreatedAgent(), agentAtShaldon]),
            new("createClient: existing client",
                SoapStatusCodes.ErrorConflict,
                [MessageCatalog.Get(MessageKeys.ClientNameAlreadyExists, FixtureClientName)],
                async () => Read(await CreateClientAsync(FixtureClientName, MashhadAddress)),
                ReadStoredClientsAsync,
                _ => null,
                () => [clientAtShaldon]),
            new("createClient: new client",
                SoapStatusCodes.StatusCreated,
                [],
                async () =>
                {
                    DtoOutcome outcome = Read(await CreateClientAsync(NewClientName, MashhadAddress));
                    createdClientGuid = outcome.Guid;
                    return outcome;
                },
                ReadStoredClientsAsync,
                stored => StoredGuidOf(stored, NewClientName),
                () => [CreatedClient(), clientAtShaldon]),
            new("createClient: empty name",
                SoapStatusCodes.ErrorBadRequest,
                [MessageCatalog.Get(MessageKeys.ClientNameRequired)],
                async () => Read(await CreateClientAsync(string.Empty, MashhadAddress)),
                ReadStoredClientsAsync,
                _ => null,
                () => [CreatedClient(), clientAtShaldon]),
            new("deleteAgent: unknown agent",
                SoapStatusCodes.ErrorNotFound,
                [MessageCatalog.Get(MessageKeys.AgentNameNotFound, UnknownAgentName)],
                async () => Read(await AdminClient.deleteAgentAsync(UnknownAgentName)),
                ReadStoredAgentsAsync,
                _ => null,
                () => [CreatedAgent(), agentAtShaldon]),
            new("deleteAgent: created agent",
                SoapStatusCodes.StatusSuccess,
                [],
                async () => Read(await AdminClient.deleteAgentAsync(NewAgentName)),
                ReadStoredAgentsAsync,
                _ => null,
                () => [agentAtShaldon]),
            new("deleteClient: unknown client",
                SoapStatusCodes.ErrorNotFound,
                [MessageCatalog.Get(MessageKeys.ClientNameNotFound, UnknownClientName)],
                async () => Read(await AdminClient.deleteClientAsync(UnknownClientName)),
                ReadStoredClientsAsync,
                _ => null,
                () => [CreatedClient(), clientAtShaldon]),
            new("deleteClient: created client",
                SoapStatusCodes.StatusSuccess,
                [],
                async () => Read(await AdminClient.deleteClientAsync(NewClientName)),
                ReadStoredClientsAsync,
                _ => null,
                () => [clientAtShaldon]),
        ];

        var expected = new List<SweepOutcome>(steps.Length);
        var actual = new List<SweepOutcome>(steps.Length);
        foreach (SweepStep step in steps)
        {
            DtoOutcome outcome = await step.Call();
            IReadOnlyList<StoredParty> stored = await step.ReadStored();

            actual.Add(new SweepOutcome(
                step.Branch,
                outcome.StatusCode,
                JoinErrors(outcome.Errors),
                NormalizedGuid(outcome.Guid),
                DescribeStored(stored)));
            expected.Add(new SweepOutcome(
                step.Branch,
                step.ExpectedStatusCode,
                JoinErrors(step.ExpectedErrors),
                NormalizedGuid(step.ExpectedGuid(stored)),
                DescribeStored(step.ExpectedStored())));
        }

        // Each branch returns its expected status code, errors and GUID and leaves the expected stored rows,
        // in sweep order.
        Assert.Equal(expected, actual);

        // No reached branch returns 403.
        Assert.All(actual, outcome =>
        {
            Assert.NotEqual(SoapStatusCodes.ErrorUnauthorized, outcome.StatusCode);
            Assert.NotEqual(SoapStatusCodes.ErrorForbidden, outcome.StatusCode);
        });
    }

    /// <summary>
    /// <c>deleteAgent</c> for the fixture agent "Test Agent", sent to an API whose <c>Erewhon:Locks:TimeoutMs</c> is
    /// 500 while another database session holds the advisory lock <c>Company.allAgents:&lt;companyId&gt;</c>, returns
    /// status 500, not 403, with the single error "Could not delete the Agent Test Agent" and no GUID; once the lock is
    /// released the company's only stored agent is "Test Agent" with its fixture address and GUID.
    /// </summary>
    /// <returns>A task that completes when the result and the stored agents have been checked.</returns>
    [Fact]
    public async Task B50_DeleteAgentLockTimeoutIs500()
    {
        var company = Builder.MyCompany;
        var fixtureAgent = Builder.MyAgent;
        Assert.NotNull(company);
        Assert.NotNull(fixtureAgent);

        Admin.DTOResult result = await DeleteWhileLockHeldAsync(
            LockNames.CompanyAllAgents(company.Id),
            admin => admin.deleteAgentAsync(FixtureAgentName));

        Assert.Equal(SoapStatusCodes.ErrorServerError, result.statusCode);
        string error = Assert.Single(result.allErrors ?? []);
        Assert.Equal(MessageCatalog.Get(MessageKeys.CouldNotDeleteAgent, FixtureAgentName), error);
        Assert.Equal("Could not delete the Agent Test Agent", error);
        Assert.True(JadeValues.IsNull(result.guid), $"Expected no GUID, got '{result.guid}'.");
        Assert.NotEqual(SoapStatusCodes.ErrorUnauthorized, result.statusCode);
        Assert.NotEqual(SoapStatusCodes.ErrorForbidden, result.statusCode);

        Assert.Equal(
            new[] { new StoredParty(FixtureAgentName, FixtureAgentAddress, fixtureAgent.Guid) },
            await ReadStoredAgentsAsync());
    }

    /// <summary>
    /// <c>deleteClient</c> for the fixture client "Test Client", sent to an API whose <c>Erewhon:Locks:TimeoutMs</c> is
    /// 500 while another database session holds the advisory lock <c>Company.allClients:&lt;companyId&gt;</c>, returns
    /// status 500, not 403, with the single error "Could not delete the Client Test Client" and no GUID; once the lock
    /// is released the company's only stored client is "Test Client" with its fixture address and GUID.
    /// </summary>
    /// <returns>A task that completes when the result and the stored clients have been checked.</returns>
    [Fact]
    public async Task B50_DeleteClientLockTimeoutIs500()
    {
        var company = Builder.MyCompany;
        var fixtureClient = Builder.MyClient;
        Assert.NotNull(company);
        Assert.NotNull(fixtureClient);

        Admin.DTOResult result = await DeleteWhileLockHeldAsync(
            LockNames.CompanyAllClients(company.Id),
            admin => admin.deleteClientAsync(FixtureClientName));

        Assert.Equal(SoapStatusCodes.ErrorServerError, result.statusCode);
        string error = Assert.Single(result.allErrors ?? []);
        Assert.Equal(MessageCatalog.Get(MessageKeys.CouldNotDeleteClient, FixtureClientName), error);
        Assert.Equal("Could not delete the Client Test Client", error);
        Assert.True(JadeValues.IsNull(result.guid), $"Expected no GUID, got '{result.guid}'.");
        Assert.NotEqual(SoapStatusCodes.ErrorUnauthorized, result.statusCode);
        Assert.NotEqual(SoapStatusCodes.ErrorForbidden, result.statusCode);

        Assert.Equal(
            new[] { new StoredParty(FixtureClientName, FixtureClientAddress, fixtureClient.Guid) },
            await ReadStoredClientsAsync());
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
        StoredParty stored = Assert.Single(await ReadStoredAgentsAsync(), party => party.Name == agent.Name);
        Assert.Equal(FixtureStreet, stored.Address.Street);
    }

    /// <summary>
    /// After each sale added to a new client, <c>getPhoto</c> returns the photo of the item of the client's last retail
    /// sale whose item has a photo, in order of time sold, then category prefix, then item code number: no photo after
    /// a tender sale of a tender item with a photo, tendered after every retail sale that follows; of two retail sales
    /// created later-dated first, the later-dated sale's photo; the same photo after a retail sale dated after every
    /// other of an item with no photo; at one time sold, the photo of the item whose category prefix sorts last,
    /// although the item whose prefix sorts first has the highest code number; at a later time sold and one prefix,
    /// the photo of the item with the higher code number, although its sale is created before the other's. Every photo
    /// is distinct from the others and from <see cref="TestPhoto"/>.
    /// </summary>
    /// <returns>A task that completes when every step has been checked.</returns>
    [Fact]
    public async Task B47_PhotoOfLastRetailSaleWithPhoto()
    {
        var category = Builder.MyItemCategory ?? throw new InvalidOperationException(
            $"The fixture item category is created by {nameof(SetUpAsync)}.");

        DateOnly serverDate = await GetService<IServerClock>().GetServerDateAsync();
        DateTime earlier = TenAm(serverDate.AddDays(-30));
        DateTime later = TenAm(serverDate.AddDays(-20));
        DateTime latest = TenAm(serverDate.AddDays(-15));
        DateTime afterEveryPhotoSale = TenAm(serverDate.AddDays(-10));

        Client client = await Builder.CreateTestClientAsync(
            PhotoClientName,
            Builder.CreateTestAddress(
                MashhadAddress.Street,
                MashhadAddress.City,
                MashhadAddress.Country,
                MashhadAddress.Phone,
                MashhadAddress.Fax,
                MashhadAddress.Email,
                MashhadAddress.Website));

        // A tender sale of a tender item with a photo, tendered after every retail sale: no photo.
        await CreateTenderSaleAsync(
            client, MarkedPhoto(1), category, TenAm(serverDate.AddDays(-5)), serverDate.AddDays(7));
        byte[]? tenderOnlyPhoto = await GetPhotoBytesAsync(PhotoClientName);
        Assert.True(
            JadeValues.IsNull(tenderOnlyPhoto),
            $"Expected no photo, got {tenderOnlyPhoto?.Length} bytes.");

        // Two retail sales created later-dated first: the later-dated sale's photo.
        byte[] laterPhoto = MarkedPhoto(2);
        RetailItem laterItem = await CreateRetailItemAsync("Later Item", laterPhoto, category);
        await Builder.CreateTestRetailSaleAsync(laterItem, client, later);
        Assert.Equal(laterPhoto, await GetPhotoBytesAsync(PhotoClientName));

        byte[] earlierPhoto = MarkedPhoto(3);
        RetailItem earlierItem = await CreateRetailItemAsync("Earlier Item", earlierPhoto, category);
        await Builder.CreateTestRetailSaleAsync(earlierItem, client, earlier);
        Assert.Equal(laterPhoto, await GetPhotoBytesAsync(PhotoClientName));

        // A retail sale dated after every other, of an item with no photo: unchanged.
        RetailItem noPhotoItem = await CreateRetailItemAsync("No Photo Item", null, category);
        await Builder.CreateTestRetailSaleAsync(noPhotoItem, client, afterEveryPhotoSale);
        Assert.Equal(laterPhoto, await GetPhotoBytesAsync(PhotoClientName));

        // At one time sold, the category prefix decides before the code number.
        ItemCategory firstPrefixCategory = await Builder.CreateTestItemCategoryAsync(
            "Photo Category AAAA", "AAAA", "A category whose prefix sorts first");
        ItemCategory lastPrefixCategory = await Builder.CreateTestItemCategoryAsync(
            "Photo Category ZZZZ", "ZZZZ", "A category whose prefix sorts last");

        byte[] lastPrefixPhoto = MarkedPhoto(4);
        RetailItem lastPrefixItem = await CreateRetailItemAsync(
            "Last Prefix Item", lastPrefixPhoto, lastPrefixCategory);
        byte[] firstPrefixPhoto = MarkedPhoto(5);
        RetailItem firstPrefixItem = await CreateRetailItemAsync(
            "First Prefix Item", firstPrefixPhoto, firstPrefixCategory);
        Assert.True(
            laterItem.CodeNumber < lastPrefixItem.CodeNumber && lastPrefixItem.CodeNumber < firstPrefixItem.CodeNumber,
            $"Expected code numbers {laterItem.CodeNumber} < {lastPrefixItem.CodeNumber} < "
            + $"{firstPrefixItem.CodeNumber}.");

        await Builder.CreateTestRetailSaleAsync(firstPrefixItem, client, later);
        Assert.Equal(laterPhoto, await GetPhotoBytesAsync(PhotoClientName));

        await Builder.CreateTestRetailSaleAsync(lastPrefixItem, client, later);
        Assert.Equal(lastPrefixPhoto, await GetPhotoBytesAsync(PhotoClientName));

        // At one later time sold and one prefix, the code number decides, not the order the sales are created in.
        byte[] lowerCodePhoto = MarkedPhoto(6);
        RetailItem lowerCodeItem = await CreateRetailItemAsync("Lower Code Item", lowerCodePhoto, category);
        byte[] higherCodePhoto = MarkedPhoto(7);
        RetailItem higherCodeItem = await CreateRetailItemAsync("Higher Code Item", higherCodePhoto, category);
        Assert.Equal(lowerCodeItem.CodeNumber + 1, higherCodeItem.CodeNumber);

        await Builder.CreateTestRetailSaleAsync(higherCodeItem, client, latest);
        Assert.Equal(higherCodePhoto, await GetPhotoBytesAsync(PhotoClientName));

        await Builder.CreateTestRetailSaleAsync(lowerCodeItem, client, latest);
        Assert.Equal(higherCodePhoto, await GetPhotoBytesAsync(PhotoClientName));
    }

    /// <summary>
    /// <paramref name="operation"/> sent the fixture agent's name (agent operations) or the fixture client's name
    /// (client operations) with the NUL character at <paramref name="placement"/> gives the unknown-name outcome:
    /// <c>getAgent</c>, <c>getClient</c> and <c>getPhoto</c> return <see langword="null"/>; <c>updateAgent</c> and
    /// <c>updateAgentWithDTO</c> return status 404 with the single error <c>$AgentNameNotFound</c> of the sent name and
    /// no GUID, and <c>updateClient</c> and <c>updateClientWithDTO</c> the same with <c>$ClientNameNotFound</c>.
    /// Afterwards the company's only stored agent and client hold their fixture names, addresses and GUIDs, and the API
    /// has written no error-level event.
    /// </summary>
    /// <param name="operation">The operation called.</param>
    /// <param name="placement">Where the NUL character sits in the name sent.</param>
    /// <returns>A task that completes when the outcome, the stored rows and the log have been checked.</returns>
    [Theory]
    [MemberData(nameof(NulNameLookups))]
    public async Task B45_NameWithNulCharacterIsNotFound(NameLookupOperation operation, NulPlacement placement)
    {
        Agent fixtureAgent = FixtureAgent;
        Client fixtureClient = Builder.MyClient ?? throw new InvalidOperationException(
            $"The fixture client is created by {nameof(SetUpAsync)}.");
        string name = NulName(operation, placement);
        Assert.Contains("\0", name, StringComparison.Ordinal);

        object? returned = await CallWithNameAsync(operation, name);

        if (NotFoundKey(operation) is { } notFoundKey)
        {
            WebService.DTOResult result = Assert.IsType<WebService.DTOResult>(returned);
            Assert.Equal(SoapStatusCodes.ErrorNotFound, result.statusCode);
            string error = Assert.Single(result.allErrors ?? []);
            Assert.Equal(MessageCatalog.Get(notFoundKey, name), error);
            Assert.True(JadeValues.IsNull(result.guid), $"Expected no GUID, got '{result.guid}'.");
        }
        else
        {
            Assert.Null(returned);
        }

        Assert.Equal(
            new[] { new StoredParty(FixtureAgentName, FixtureAgentAddress, fixtureAgent.Guid) },
            await ReadStoredAgentsAsync());
        Assert.Equal(
            new[] { new StoredParty(FixtureClientName, FixtureClientAddress, fixtureClient.Guid) },
            await ReadStoredClientsAsync());
        Assert.Empty(LogSink.ErrorEvents);
    }

    /// <summary>
    /// With no company, <paramref name="operation"/> sent "Test\0Agent" (agent operations) or "Test\0Client" (client
    /// operations) faults, and the API writes one <see cref="ErrorOutcomeMiddleware.UnhandledExceptionEventId"/> event,
    /// whose error code is 1090, that of <see cref="NoCompanyException"/>.
    /// </summary>
    /// <param name="operation">The operation called.</param>
    /// <returns>A task that completes when the fault and the logged event have been checked.</returns>
    [Theory]
    [MemberData(nameof(NameLookupOperations))]
    public async Task B45_NameWithNulCharacterWithoutCompanyFaults(NameLookupOperation operation)
    {
        var company = Builder.MyCompany ?? throw new InvalidOperationException(
            $"The fixture company is created by {nameof(SetUpAsync)}.");
        await Builder.DeleteCompanyAsync(company.Id);
        string name = NulName(operation, NulPlacement.InPlaceOfSpace);

        await Assert.ThrowsAnyAsync<FaultException>(() => CallWithNameAsync(operation, name));

        LogEvent unhandled = Assert.Single(LogSink.Events, IsUnhandledExceptionEvent);
        Assert.Equal(
            new ScalarValue(NoCompanyException.Code.ToString(CultureInfo.InvariantCulture)),
            unhandled.Properties[ErrorCodeProperty]);
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

    /// <summary>
    /// Releases the advisory lock <c>pg_advisory_unlock(hashtextextended(lockName, 0))</c> held by
    /// <paramref name="holder"/>, then disposes the connection, also when the release fails.
    /// </summary>
    private static async Task ReleaseAdvisoryLockAsync(NpgsqlConnection holder, string lockName)
    {
        try
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@name, 0))", holder);
            release.Parameters.AddWithValue("name", lockName);
            await release.ExecuteNonQueryAsync();
        }
        finally
        {
            await holder.DisposeAsync();
        }
    }

    /// <summary>Returns a street of 60 characters, longer than the 51 of <c>Address.street</c>.</summary>
    private static string OverLongStreet() => new('S', OverLongStreetLength);

    /// <summary>
    /// Returns the fixture agent's name for an agent operation, or the fixture client's name for a client operation,
    /// with the NUL character at <paramref name="placement"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="operation"/> or <paramref name="placement"/> is not a declared value.
    /// </exception>
    private static string NulName(NameLookupOperation operation, NulPlacement placement)
    {
        string fixtureName = operation switch
        {
            NameLookupOperation.GetAgent
                or NameLookupOperation.UpdateAgent
                or NameLookupOperation.UpdateAgentWithDto => FixtureAgentName,
            NameLookupOperation.GetClient
                or NameLookupOperation.GetPhoto
                or NameLookupOperation.UpdateClient
                or NameLookupOperation.UpdateClientWithDto => FixtureClientName,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

        return placement switch
        {
            NulPlacement.InPlaceOfSpace => fixtureName.Replace(' ', '\0'),
            NulPlacement.AfterName => fixtureName + '\0',
            _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, null),
        };
    }

    /// <summary>
    /// Returns the message key of the error a <c>DTOResult</c> of <paramref name="operation"/> carries for an unknown
    /// name, or <see langword="null"/> for an operation that returns <see langword="null"/> for one.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is not a declared value.</exception>
    private static string? NotFoundKey(NameLookupOperation operation) =>
        operation switch
        {
            NameLookupOperation.GetAgent or NameLookupOperation.GetClient or NameLookupOperation.GetPhoto => null,
            NameLookupOperation.UpdateAgent or NameLookupOperation.UpdateAgentWithDto => MessageKeys.AgentNameNotFound,
            NameLookupOperation.UpdateClient or NameLookupOperation.UpdateClientWithDto => MessageKeys.ClientNameNotFound,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

    /// <summary>Returns whether the event's <c>EventId</c> is <see cref="ErrorOutcomeMiddleware.UnhandledExceptionEventId"/>.</summary>
    private static bool IsUnhandledExceptionEvent(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(EventIdProperty, out LogEventPropertyValue? eventId)
        && eventId is StructureValue structure
        && structure.Properties.Any(property =>
            property.Name == "Id" && Equals(property.Value, new ScalarValue(ErrorOutcomeMiddleware.UnhandledExceptionEventId.Id)));

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

    /// <summary>Returns the status code, errors and GUID of a web service result.</summary>
    private static DtoOutcome Read(WebService.DTOResult result) =>
        new(result.statusCode, result.allErrors ?? [], result.guid);

    /// <summary>Returns the status code, errors and GUID of an admin service result.</summary>
    private static DtoOutcome Read(Admin.DTOResult result) =>
        new(result.statusCode, result.allErrors ?? [], result.guid);

    /// <summary>Returns <paramref name="errors"/> in order, separated by <c> | </c>.</summary>
    private static string JoinErrors(IReadOnlyList<string> errors) => string.Join(" | ", errors);

    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="guid"/> is JADE-null (null or empty), otherwise the GUID.
    /// </summary>
    private static string? NormalizedGuid(string? guid) => JadeValues.IsNull(guid) ? null : guid;

    /// <summary>
    /// Returns the GUID of the entry of <paramref name="stored"/> named <paramref name="name"/>, or
    /// <see cref="NoStoredGuid"/> when there is no such entry or its GUID is JADE-null.
    /// </summary>
    private static string StoredGuidOf(IReadOnlyList<StoredParty> stored, string name) =>
        stored.FirstOrDefault(party => party.Name == name)?.Guid is { } guid && !JadeValues.IsNull(guid)
            ? guid
            : NoStoredGuid;

    /// <summary>
    /// Returns the entries of <paramref name="stored"/> in order, separated by <c> ; </c>, each with a JADE-null GUID
    /// written as <see langword="null"/>.
    /// </summary>
    private static string DescribeStored(IReadOnlyList<StoredParty> stored) =>
        string.Join(" ; ", stored.Select(party => party with { Guid = NormalizedGuid(party.Guid) }));

    /// <summary>
    /// Returns the seven values of <paramref name="address"/> in the order of an operation's parameters.
    /// </summary>
    private static SoapAddress StoredAddress(Address address) =>
        new(address.Street, address.City, address.Country, address.Phone, address.Fax, address.Email, address.WebSite);

    /// <summary>Returns 10:00:00 on <paramref name="date"/>.</summary>
    private static DateTime TenAm(DateOnly date) => date.ToDateTime(new TimeOnly(10, 0, 0));

    /// <summary>Returns the bytes of <see cref="TestPhoto"/> followed by the one byte <paramref name="marker"/>.</summary>
    private static byte[] MarkedPhoto(byte marker) => [.. TestPhoto.ToArray(), marker];

    /// <summary>Calls <c>updateAgent</c> with <paramref name="name"/> and the seven values of <paramref name="address"/>.</summary>
    private Task<WebService.DTOResult> UpdateAgentAsync(string name, SoapAddress address) =>
        WebClient.updateAgentAsync(
            name, address.Street, address.City, address.Country, address.Phone, address.Fax, address.Email, address.Website);

    /// <summary>Calls <c>updateClient</c> with <paramref name="name"/> and the seven values of <paramref name="address"/>.</summary>
    private Task<WebService.DTOResult> UpdateClientAsync(string name, SoapAddress address) =>
        WebClient.updateClientAsync(
            name, address.Street, address.City, address.Country, address.Phone, address.Fax, address.Email, address.Website);

    /// <summary>
    /// Calls <paramref name="operation"/> with <paramref name="name"/>, and for an update with the seven values of
    /// <see cref="TamilNaduAddress"/>, and returns what it returns: a <c>DTOAgent</c>, <c>DTOClient</c> or
    /// <c>DTOPhoto</c> or <see langword="null"/> for a lookup, a <c>DTOResult</c> for an update.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is not a declared value.</exception>
    private async Task<object?> CallWithNameAsync(NameLookupOperation operation, string name) =>
        operation switch
        {
            NameLookupOperation.GetAgent => await WebClient.getAgentAsync(name),
            NameLookupOperation.GetClient => await WebClient.getClientAsync(name),
            NameLookupOperation.GetPhoto => await WebClient.getPhotoAsync(name),
            NameLookupOperation.UpdateAgent => await UpdateAgentAsync(name, TamilNaduAddress),
            NameLookupOperation.UpdateAgentWithDto => await WebClient.updateAgentWithDTOAsync(AgentDto(name, TamilNaduAddress)),
            NameLookupOperation.UpdateClient => await UpdateClientAsync(name, TamilNaduAddress),
            NameLookupOperation.UpdateClientWithDto => await WebClient.updateClientWithDTOAsync(ClientDto(name, TamilNaduAddress)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

    /// <summary>Calls <c>createAgent</c> with <paramref name="name"/> and the seven values of <paramref name="address"/>.</summary>
    private Task<Admin.DTOResult> CreateAgentAsync(string name, SoapAddress address) =>
        AdminClient.createAgentAsync(
            name, address.Street, address.City, address.Country, address.Phone, address.Fax, address.Email, address.Website);

    /// <summary>Calls <c>createClient</c> with <paramref name="name"/> and the seven values of <paramref name="address"/>.</summary>
    private Task<Admin.DTOResult> CreateClientAsync(string name, SoapAddress address) =>
        AdminClient.createClientAsync(
            name, address.Street, address.City, address.Country, address.Phone, address.Fax, address.Email, address.Website);

    /// <summary>
    /// Starts a further API over <see cref="ErewhonTestBase.Database"/> with <c>Erewhon:Locks:TimeoutMs</c> set to
    /// <see cref="ShortLockWaitMs"/>, holds the advisory lock <paramref name="lockName"/> from a separate connection,
    /// calls <paramref name="delete"/> with an <c>ErewhonWebServiceAdmin</c> client of that API and returns its result.
    /// Afterwards releases the lock, closes the client, aborting it when it is faulted or fails to close, and disposes
    /// the API, also when a step fails.
    /// </summary>
    private async Task<Admin.DTOResult> DeleteWhileLockHeldAsync(
        string lockName,
        Func<Admin.ErewhonWebServiceAdminClient, Task<Admin.DTOResult>> delete)
    {
        await using ErewhonApiHost host = await StartDedicatedHostAsync(
            new Dictionary<string, string?> { [LockTimeoutKey] = ShortLockWaitMs });

        Admin.ErewhonWebServiceAdminClient admin = host.CreateSoapClientFactory().CreateAdminClient();
        try
        {
            NpgsqlConnection holder = await HoldAdvisoryLockAsync(lockName);
            try
            {
                return await delete(admin);
            }
            finally
            {
                await ReleaseAdvisoryLockAsync(holder, lockName);
            }
        }
        finally
        {
            await CloseClientAsync(admin);
        }
    }

    /// <summary>
    /// Opens an unpooled connection to <see cref="ErewhonTestBase.Database"/> that holds the session-level advisory lock
    /// <c>pg_advisory_lock(hashtextextended(lockName, 0))</c> and returns it; disposes the connection when the lock
    /// cannot be taken.
    /// </summary>
    private async Task<NpgsqlConnection> HoldAdvisoryLockAsync(string lockName)
    {
        var holder = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(Database.ConnectionString) { Pooling = false }.ConnectionString);
        try
        {
            await holder.OpenAsync();
            await using var hold = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@name, 0))", holder);
            hold.Parameters.AddWithValue("name", lockName);
            await hold.ExecuteNonQueryAsync();
            return holder;
        }
        catch
        {
            await holder.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Returns the stored agents of the fixture company in name order, each with its name, seven address values and
    /// GUID, read through <see cref="AgentService.GetAgentsAsync"/> in a new scope of <see cref="ErewhonTestBase.Api"/>.
    /// </summary>
    private Task<IReadOnlyList<StoredParty>> ReadStoredAgentsAsync() =>
        ReadStoredAsync(async services =>
            (await services.GetRequiredService<AgentService>().GetAgentsAsync())
                .Select(agent => new StoredParty(agent.Name, StoredAddress(agent.Address), agent.Guid))
                .ToList());

    /// <summary>
    /// Returns the stored clients of the fixture company in name order, each with its name, seven address values and
    /// GUID, read through <see cref="ClientService.GetClientsAsync"/> in a new scope of <see cref="ErewhonTestBase.Api"/>.
    /// </summary>
    private Task<IReadOnlyList<StoredParty>> ReadStoredClientsAsync() =>
        ReadStoredAsync(async services =>
            (await services.GetRequiredService<ClientService>().GetClientsAsync())
                .Select(client => new StoredParty(client.Name, StoredAddress(client.Address), client.Guid))
                .ToList());

    /// <summary>
    /// Runs <paramref name="read"/> over a new scope of <see cref="ErewhonTestBase.Api"/> bound to the fixture company
    /// and returns its result, disposing the scope afterwards.
    /// </summary>
    /// <exception cref="InvalidOperationException">The fixture company has not been created.</exception>
    private async Task<IReadOnlyList<StoredParty>> ReadStoredAsync(
        Func<IServiceProvider, Task<IReadOnlyList<StoredParty>>> read)
    {
        var company = Builder.MyCompany ?? throw new InvalidOperationException(
            $"The fixture company is created by {nameof(SetUpAsync)}.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(company.Id);

        return await read(scope.ServiceProvider);
    }

    /// <summary>
    /// Creates the retail item <paramref name="name"/> in <paramref name="category"/> with <paramref name="photo"/>,
    /// priced 100, listed in the fixture region and sold by the fixture agent.
    /// </summary>
    private Task<RetailItem> CreateRetailItemAsync(string name, byte[]? photo, ItemCategory category) =>
        Builder.CreateTestRetailItemAsync(
            name, "An item sold to the photo client", photo, FixtureRegion, category, FixtureAgent, 100m);

    /// <summary>
    /// Creates a tender item in <paramref name="category"/> with <paramref name="photo"/>, reserve 500 and closure date
    /// <paramref name="closureDate"/>, listed in the fixture region and sold by the fixture agent; a tender by
    /// <paramref name="client"/> on it of its reserve, placed at <paramref name="dateTendered"/>; and the tender sale
    /// that awards the item to <paramref name="client"/> through that tender.
    /// </summary>
    private async Task CreateTenderSaleAsync(
        Client client,
        byte[] photo,
        ItemCategory category,
        DateTime dateTendered,
        DateOnly closureDate)
    {
        TenderItem tenderItem = await Builder.CreateTestTenderItemAsync(
            "Photo Tender Item",
            "A tender item sold to the photo client",
            photo,
            FixtureRegion,
            category,
            FixtureAgent,
            500m,
            closureDate);
        Tender tender = await Builder.CreateTestTenderAsync(tenderItem.Price, dateTendered, client, tenderItem);
        await Builder.CreateTestTenderSaleAsync(tenderItem, tender, client);
    }

    /// <summary>
    /// Calls <c>getPhoto</c> for <paramref name="clientName"/>, checks that it returns a <c>DTOPhoto</c>, and returns
    /// that DTO's photo bytes.
    /// </summary>
    private async Task<byte[]?> GetPhotoBytesAsync(string clientName)
    {
        WebService.DTOPhoto dtoPhoto = await WebClient.getPhotoAsync(clientName);
        Assert.NotNull(dtoPhoto);
        return dtoPhoto.photo;
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

    /// <summary>The name, seven address values and GUID of one stored agent or client.</summary>
    private sealed record StoredParty(string Name, SoapAddress Address, string? Guid);

    /// <summary>The status code, errors and GUID of one <c>DTOResult</c>.</summary>
    private sealed record DtoOutcome(int StatusCode, IReadOnlyList<string> Errors, string? Guid);

    /// <summary>
    /// One branch of the B-51 sweep: its expected status code and errors, the call that reaches it, the read of the
    /// stored rows it acts on, its expected GUID given the rows read after the call, and the rows expected after the
    /// call.
    /// </summary>
    private sealed record SweepStep(
        string Branch,
        int ExpectedStatusCode,
        IReadOnlyList<string> ExpectedErrors,
        Func<Task<DtoOutcome>> Call,
        Func<Task<IReadOnlyList<StoredParty>>> ReadStored,
        Func<IReadOnlyList<StoredParty>, string?> ExpectedGuid,
        Func<IReadOnlyList<StoredParty>> ExpectedStored);

    /// <summary>
    /// The status code, joined errors, GUID (<see langword="null"/> when JADE-null) and described stored rows one branch
    /// of the B-51 sweep returned or is expected to return.
    /// </summary>
    private sealed record SweepOutcome(string Branch, int StatusCode, string Errors, string? Guid, string Stored);
}

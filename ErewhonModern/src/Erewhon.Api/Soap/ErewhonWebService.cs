using CoreWCF;
using Erewhon.Api.Soap.Contracts;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Projections;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Soap;

/// <summary>
/// The <c>ErewhonWebService</c> SOAP service: agent and client lookups by name, the agent and client name lists, the
/// photo of a client's purchased retail items, and address updates of an existing agent or client. Ports JADE
/// <c>ErewhonWebService</c>. Each operation runs in its own dependency-injection scope. The first agent or client
/// lookup of an operation reads the bound company, after the DTO checks of <c>updateAgentWithDTO</c> and
/// <c>updateClientWithDTO</c>; a <see cref="NoCompanyException"/> from that read faults unchanged.
/// </summary>
[ServiceBehavior(
    Name = "ErewhonWebService",
    Namespace = "urn:erewhon:soap",
    InstanceContextMode = InstanceContextMode.PerCall)]
public sealed class ErewhonWebService : IErewhonWebService
{
    private const string NullObjectReferenceText = "Null object reference";

    private const int NullObjectReferenceCode = 1090;

    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Creates the service over the factory of the scopes its operations run in.</summary>
    /// <param name="scopeFactory">Creates one dependency-injection scope per operation call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scopeFactory"/> is <see langword="null"/>.</exception>
    public ErewhonWebService(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public Task<DTOAgent?> getAgent(string? pName) =>
        InvokeAsync<DTOAgent?>(async services =>
        {
            Agent? agent = await services.GetRequiredService<AgentService>().FindByNameAsync(pName ?? string.Empty);
            return agent is null ? null : ToDto(AgentProjection.From(agent));
        });

    /// <inheritdoc />
    public Task<DTOName[]> getAgentNames() =>
        InvokeAsync(async services =>
        {
            IReadOnlyList<NameProjection> names = await services.GetRequiredService<AgentService>().GetAgentNamesAsync();
            return names.Select(ToDto).ToArray();
        });

    /// <inheritdoc />
    public Task<DTOClient?> getClient(string? pName) =>
        InvokeAsync<DTOClient?>(async services =>
        {
            Client? client = await services.GetRequiredService<ClientService>()
                .FindByNameAsync(JadeValues.TrimBlanks(pName));
            return client is null ? null : ToDto(ClientProjection.From(client));
        });

    /// <inheritdoc />
    public Task<DTOName[]> getClientNames() =>
        InvokeAsync(async services =>
        {
            IReadOnlyList<NameProjection> names = await services.GetRequiredService<ClientService>().GetClientNamesAsync();
            return names.Select(ToDto).ToArray();
        });

    /// <inheritdoc />
    public Task<DTOPhoto?> getPhoto(string? pName) =>
        InvokeAsync<DTOPhoto?>(async services =>
        {
            ClientService clients = services.GetRequiredService<ClientService>();
            Client? client = await clients.FindByNameAsync(pName ?? string.Empty);
            if (client is null)
            {
                return null;
            }

            var dtoPhoto = new DTOPhoto();
            byte[]? photo = await clients.GetLastRetailSalePhotoAsync(client.Id);
            if (!JadeValues.IsNull(photo))
            {
                dtoPhoto.photo = photo;
            }

            return dtoPhoto;
        });

    /// <inheritdoc />
    public Task<DTOResult> updateAgent(
        string? pName,
        string? pStreet,
        string? pCity,
        string? pCountry,
        string? pPhone,
        string? pFax,
        string? pEmail,
        string? pWebsite) =>
        InvokeAsync(services => UpdateAgentAsync(
            services,
            pName ?? string.Empty,
            new AddressValues(pStreet, pCity, pCountry, pPhone, pFax, pEmail, pWebsite)));

    /// <inheritdoc />
    public Task<DTOResult> updateAgentWithDTO(DTOAgent? pDTOAgent) =>
        InvokeAsync(services =>
        {
            if (pDTOAgent is null)
            {
                throw Fault(NullObjectReferenceText, NullObjectReferenceCode);
            }

            FieldLengths.Check(pDTOAgent.guid, FieldLengths.Guid, nameof(DTOAgent.guid));
            FieldLengths.Check(pDTOAgent.name, FieldLengths.AgentName, nameof(DTOAgent.name));
            var address = new AddressValues(
                pDTOAgent.street,
                pDTOAgent.city,
                pDTOAgent.country,
                pDTOAgent.phone,
                pDTOAgent.fax,
                pDTOAgent.email,
                pDTOAgent.website);
            address.CheckFieldLengths();

            return UpdateAgentAsync(services, pDTOAgent.name ?? string.Empty, address);
        });

    /// <inheritdoc />
    public Task<DTOResult> updateClient(
        string? pName,
        string? pStreet,
        string? pCity,
        string? pCountry,
        string? pPhone,
        string? pFax,
        string? pEmail,
        string? pWebsite) =>
        InvokeAsync(services => UpdateClientAsync(
            services,
            pName ?? string.Empty,
            new AddressValues(pStreet, pCity, pCountry, pPhone, pFax, pEmail, pWebsite)));

    /// <inheritdoc />
    public Task<DTOResult> updateClientWithDTO(DTOClient? pDTOClient) =>
        InvokeAsync(services =>
        {
            if (pDTOClient is null)
            {
                throw Fault(NullObjectReferenceText, NullObjectReferenceCode);
            }

            FieldLengths.Check(pDTOClient.guid, FieldLengths.Guid, nameof(DTOClient.guid));
            FieldLengths.Check(pDTOClient.name, FieldLengths.ClientName, nameof(DTOClient.name));
            var address = new AddressValues(
                pDTOClient.street,
                pDTOClient.city,
                pDTOClient.country,
                pDTOClient.phone,
                pDTOClient.fax,
                pDTOClient.email,
                pDTOClient.website);
            address.CheckFieldLengths();

            return UpdateClientAsync(services, pDTOClient.name ?? string.Empty, address);
        });

    /// <summary>
    /// Runs <paramref name="operation"/> with the service provider of a new scope, disposing the scope afterwards. A
    /// <see cref="StringTooLongException"/> becomes a fault whose reason is its message and error number, e.g.
    /// <c>String too long (1035)</c>; every other exception propagates unchanged.
    /// </summary>
    private async Task<T> InvokeAsync<T>(Func<IServiceProvider, Task<T>> operation)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            return await operation(scope.ServiceProvider);
        }
        catch (StringTooLongException ex)
        {
            throw Fault(ex.Message, ex.ErrorCode);
        }
    }

    /// <summary>
    /// Updates the address of the agent of the bound company named exactly <paramref name="name"/> with
    /// <paramref name="address"/> in its own transaction: status 404 and <see cref="MessageKeys.AgentNameNotFound"/>
    /// when there is no such agent, 400 and the errors when the update fails, 200 and the agent's GUID when it succeeds.
    /// </summary>
    private static async Task<DTOResult> UpdateAgentAsync(IServiceProvider services, string name, AddressValues address)
    {
        AgentService agents = services.GetRequiredService<AgentService>();
        Agent? agent = await agents.FindByNameAsync(name);
        if (agent is null)
        {
            return Result(SoapStatusCodes.ErrorNotFound, [MessageCatalog.Get(MessageKeys.AgentNameNotFound, name)]);
        }

        var command = new AgentCommand();
        command.PopulateFromObject(agent);
        address.WriteTo(command.Address ??= AddressCommand.FromAddress(agent.Address));

        OperationResult result = await agents.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);
        return UpdateOutcome(result, agent.Guid);
    }

    /// <summary>
    /// Updates the address of the client of the bound company named exactly <paramref name="name"/> with
    /// <paramref name="address"/> in its own transaction: status 404 and <see cref="MessageKeys.ClientNameNotFound"/>
    /// when there is no such client, 400 and the errors when the update fails, 200 and the client's GUID when it
    /// succeeds.
    /// </summary>
    private static async Task<DTOResult> UpdateClientAsync(IServiceProvider services, string name, AddressValues address)
    {
        ClientService clients = services.GetRequiredService<ClientService>();
        Client? client = await clients.FindByNameAsync(name);
        if (client is null)
        {
            return Result(SoapStatusCodes.ErrorNotFound, [MessageCatalog.Get(MessageKeys.ClientNameNotFound, name)]);
        }

        var command = new ClientCommand();
        command.PopulateFromObject(client);
        address.WriteTo(command.Address ??= AddressCommand.FromAddress(client.Address));

        OperationResult result = await clients.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);
        return UpdateOutcome(result, client.Guid);
    }

    /// <summary>
    /// Returns status 200 with the GUID of the result, or <paramref name="entityGuid"/> when the result has none, for a
    /// succeeded update; otherwise status 400 with the errors of the result in order.
    /// </summary>
    private static DTOResult UpdateOutcome(OperationResult result, string entityGuid) =>
        result.Succeeded
            ? Result(SoapStatusCodes.StatusSuccess, guid: JadeValues.IsNull(result.Guid) ? entityGuid : result.Guid)
            : Result(SoapStatusCodes.ErrorBadRequest, result.Errors);

    /// <summary>
    /// Returns a result with <paramref name="statusCode"/>, the errors in order or none, no warnings and
    /// <paramref name="guid"/>.
    /// </summary>
    private static DTOResult Result(int statusCode, IEnumerable<string>? errors = null, string? guid = null) =>
        new()
        {
            statusCode = statusCode,
            allErrors = errors?.ToArray() ?? [],
            allWarnings = [],
            guid = guid,
        };

    /// <summary>Returns the agent data of <paramref name="agent"/>: its GUID, name and seven address fields.</summary>
    private static DTOAgent ToDto(AgentProjection agent) =>
        new()
        {
            guid = agent.Guid,
            name = agent.Name,
            street = agent.Street,
            city = agent.City,
            country = agent.Country,
            phone = agent.Phone,
            fax = agent.Fax,
            email = agent.Email,
            website = agent.WebSite,
        };

    /// <summary>Returns the client data of <paramref name="client"/>: its GUID, name and seven address fields.</summary>
    private static DTOClient ToDto(ClientProjection client) =>
        new()
        {
            guid = client.Guid,
            name = client.Name,
            street = client.Street,
            city = client.City,
            country = client.Country,
            phone = client.Phone,
            fax = client.Fax,
            email = client.Email,
            website = client.WebSite,
        };

    /// <summary>Returns the name data of <paramref name="name"/>, holding its name and no GUID.</summary>
    private static DTOName ToDto(NameProjection name) => new() { name = name.Name };

    /// <summary>Returns a fault whose reason is <paramref name="text"/> followed by the error number in parentheses.</summary>
    private static FaultException Fault(string text, int errorCode) => new(new FaultReason($"{text} ({errorCode})"));

    /// <summary>
    /// The seven address values an update operation writes, as received; a <see langword="null"/> value is written as
    /// the empty string.
    /// </summary>
    private sealed record AddressValues(
        string? Street,
        string? City,
        string? Country,
        string? Phone,
        string? Fax,
        string? Email,
        string? WebSite)
    {
        /// <summary>
        /// Checks each value against its declared length, in the order street, city, country, phone, fax, e-mail, web
        /// site; throws <see cref="StringTooLongException"/> at the first value that is too long.
        /// </summary>
        public void CheckFieldLengths() =>
            AddressRules.CheckFieldLengths(new AddressCommand
            {
                Street = Street,
                City = City,
                Country = Country,
                Phone = Phone,
                Fax = Fax,
                Email = Email,
                WebSite = WebSite,
            });

        /// <summary>
        /// Writes each value to <paramref name="address"/> in the order street, city, country, phone, fax, e-mail, web
        /// site, checking each against its declared length just before it is written; throws
        /// <see cref="StringTooLongException"/> at the first value that is too long.
        /// </summary>
        public void WriteTo(AddressCommand address)
        {
            address.Street = FieldLengths.CheckAndReturn(Street, FieldLengths.AddressStreet, nameof(AddressCommand.Street));
            address.City = FieldLengths.CheckAndReturn(City, FieldLengths.AddressCity, nameof(AddressCommand.City));
            address.Country = FieldLengths.CheckAndReturn(Country, FieldLengths.AddressCountry, nameof(AddressCommand.Country));
            address.Phone = FieldLengths.CheckAndReturn(Phone, FieldLengths.AddressPhone, nameof(AddressCommand.Phone));
            address.Fax = FieldLengths.CheckAndReturn(Fax, FieldLengths.AddressFax, nameof(AddressCommand.Fax));
            address.Email = FieldLengths.CheckAndReturn(Email, FieldLengths.AddressEmail, nameof(AddressCommand.Email));
            address.WebSite = FieldLengths.CheckAndReturn(WebSite, FieldLengths.AddressWebSite, nameof(AddressCommand.WebSite));
        }
    }
}

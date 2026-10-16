using CoreWCF;
using Erewhon.Api.Errors;
using Erewhon.Api.Soap.Contracts;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erewhon.Api.Soap;

/// <summary>
/// The <c>ErewhonWebServiceAdmin</c> SOAP service: creates and deletes the agents and clients of the bound company
/// by name through <see cref="AgentService"/> and <see cref="ClientService"/>, each operation in its own
/// dependency-injection scope. The name lookup of each operation reads the bound company, before the duplicate or
/// not-found check and before any field-length check. A value longer than its field faults with the reason
/// <c>String too long (1035)</c>; every other exception from the operation or from disposing its scope except
/// <see cref="FaultException"/>, including <see cref="NoCompanyException"/>, is passed to <see cref="Report"/>, which
/// writes one error event with event id 500 for every outcome except the lock outcome of an
/// <see cref="ErewhonLockException"/>, and then propagates unchanged to CoreWCF's generic fault.
/// </summary>
[ServiceBehavior(
    Name = "ErewhonWebServiceAdmin",
    Namespace = "urn:erewhon:soap",
    InstanceContextMode = InstanceContextMode.PerCall)]
public sealed class ErewhonWebServiceAdmin : IErewhonWebServiceAdmin
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Creates the service over the factory of the scope each operation runs in.</summary>
    /// <param name="scopeFactory">Creates one dependency-injection scope per operation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scopeFactory"/> is <see langword="null"/>.</exception>
    public ErewhonWebServiceAdmin(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public Task<DTOResult> createAgent(
        string? pName,
        string? pStreet,
        string? pCity,
        string? pCountry,
        string? pPhone,
        string? pFax,
        string? pEmail,
        string? pWebsite)
    {
        string name = pName ?? string.Empty;
        string street = pStreet ?? string.Empty;
        string city = pCity ?? string.Empty;
        string country = pCountry ?? string.Empty;
        string phone = pPhone ?? string.Empty;
        string fax = pFax ?? string.Empty;
        string email = pEmail ?? string.Empty;
        string webSite = pWebsite ?? string.Empty;

        return InvokeAsync(async services =>
        {
            AgentService agents = services.GetRequiredService<AgentService>();

            if (await agents.FindByNameAsync(name) is not null)
            {
                return Result(
                    SoapStatusCodes.ErrorConflict,
                    [MessageCatalog.Get(MessageKeys.AgentNameAlreadyExists, name)]);
            }

            FieldLengths.Check(name, FieldLengths.AgentName, "name");
            var command = new AgentCommand
            {
                Name = name,
                Address = CheckedAddress(street, city, country, phone, fax, email, webSite),
            };

            OperationResult result = await agents.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);

            return result.Succeeded
                ? Result(SoapStatusCodes.StatusCreated, guid: result.Guid)
                : Result(SoapStatusCodes.ErrorBadRequest, result.Errors);
        });
    }

    /// <inheritdoc />
    public Task<DTOResult> createClient(
        string? pName,
        string? pStreet,
        string? pCity,
        string? pCountry,
        string? pPhone,
        string? pFax,
        string? pEmail,
        string? pWebsite)
    {
        string name = pName ?? string.Empty;
        string street = pStreet ?? string.Empty;
        string city = pCity ?? string.Empty;
        string country = pCountry ?? string.Empty;
        string phone = pPhone ?? string.Empty;
        string fax = pFax ?? string.Empty;
        string email = pEmail ?? string.Empty;
        string webSite = pWebsite ?? string.Empty;

        return InvokeAsync(async services =>
        {
            ClientService clients = services.GetRequiredService<ClientService>();

            if (await clients.FindByNameAsync(name) is not null)
            {
                return Result(
                    SoapStatusCodes.ErrorConflict,
                    [MessageCatalog.Get(MessageKeys.ClientNameAlreadyExists, name)]);
            }

            FieldLengths.Check(name, FieldLengths.ClientName, "name");
            var command = new ClientCommand
            {
                Name = name,
                Address = CheckedAddress(street, city, country, phone, fax, email, webSite),
            };

            OperationResult result = await clients.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);

            return result.Succeeded
                ? Result(SoapStatusCodes.StatusCreated, guid: result.Guid)
                : Result(SoapStatusCodes.ErrorBadRequest, result.Errors);
        });
    }

    /// <inheritdoc />
    public Task<DTOResult> deleteAgent(string? pName)
    {
        string name = pName ?? string.Empty;

        return InvokeAsync(async services =>
        {
            AgentService agents = services.GetRequiredService<AgentService>();

            Agent? agent = await agents.FindByNameAsync(name);
            if (agent is null)
            {
                return Result(
                    SoapStatusCodes.ErrorNotFound,
                    [MessageCatalog.Get(MessageKeys.AgentNameNotFound, name)]);
            }

            var command = new AgentCommand();
            command.PopulateFromObject(agent);

            OperationResult result = await agents.ExecuteAsync(command, TransactionType.Delete, TransactionMode.Own);

            return result.Succeeded
                ? Result(SoapStatusCodes.StatusSuccess)
                : Result(SoapStatusCodes.ErrorServerError, [MessageCatalog.Get(MessageKeys.CouldNotDeleteAgent, name)]);
        });
    }

    /// <inheritdoc />
    public Task<DTOResult> deleteClient(string? pName)
    {
        string name = pName ?? string.Empty;

        return InvokeAsync(async services =>
        {
            ClientService clients = services.GetRequiredService<ClientService>();

            Client? client = await clients.FindByNameAsync(name);
            if (client is null)
            {
                return Result(
                    SoapStatusCodes.ErrorNotFound,
                    [MessageCatalog.Get(MessageKeys.ClientNameNotFound, name)]);
            }

            var command = new ClientCommand();
            command.PopulateFromObject(client);

            OperationResult result = await clients.ExecuteAsync(command, TransactionType.Delete, TransactionMode.Own);

            return result.Succeeded
                ? Result(SoapStatusCodes.StatusSuccess)
                : Result(SoapStatusCodes.ErrorServerError, [MessageCatalog.Get(MessageKeys.CouldNotDeleteClient, name)]);
        });
    }

    /// <summary>
    /// Runs <paramref name="operation"/> in a new dependency-injection scope and returns its result, disposing the
    /// scope afterwards; the scope's <see cref="ILogger{TCategoryName}"/> of this service and its
    /// <see cref="PostgresErrorMapper"/> are resolved before the operation runs. A
    /// <see cref="StringTooLongException"/> becomes a fault whose reason is the exception text followed by its error
    /// number in parentheses; a <see cref="FaultException"/> propagates unchanged; every other exception, from the
    /// operation or from disposing the scope, is passed once to <see cref="Report"/>, which writes one error event with
    /// event id 500 for every outcome except the lock outcome of an <see cref="ErewhonLockException"/>, and then
    /// propagates unchanged to CoreWCF's generic fault.
    /// </summary>
    private async Task<DTOResult> InvokeAsync(Func<IServiceProvider, Task<DTOResult>> operation)
    {
        ILogger<ErewhonWebServiceAdmin>? logger = null;
        PostgresErrorMapper? mapper = null;
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            logger = scope.ServiceProvider.GetService<ILogger<ErewhonWebServiceAdmin>>();
            mapper = scope.ServiceProvider.GetService<PostgresErrorMapper>();
            try
            {
                return await operation(scope.ServiceProvider);
            }
            catch (StringTooLongException ex)
            {
                throw new FaultException(new FaultReason($"{ex.Message} ({ex.ErrorCode})"));
            }
        }
        catch (Exception ex) when (ex is not FaultException)
        {
            Report(logger, mapper, ex);
            throw;
        }
    }

    /// <summary>
    /// Translates <paramref name="exception"/> with <see cref="ErrorOutcomeMiddleware.Translate"/> and
    /// <paramref name="mapper"/>, or with no mapper when it is <see langword="null"/>, and, when
    /// <see cref="ErrorOutcomeMiddleware.IsLogged"/> is <see langword="true"/> for the result and
    /// <paramref name="logger"/> is not <see langword="null"/>, writes its error event to <paramref name="logger"/>
    /// with <see cref="ErrorOutcomeMiddleware.LogOutcome"/> and no session.
    /// </summary>
    private static void Report(ILogger? logger, PostgresErrorMapper? mapper, Exception exception)
    {
        Exception translated = ErrorOutcomeMiddleware.Translate(exception, mapper);
        if (logger is not null && ErrorOutcomeMiddleware.IsLogged(translated))
        {
            ErrorOutcomeMiddleware.LogOutcome(logger, translated, null);
        }
    }

    /// <summary>
    /// Checks the seven address values against their field lengths, in the order street, city, country, phone, fax,
    /// e-mail and web site, and returns them as an address command.
    /// </summary>
    /// <exception cref="StringTooLongException">A value is longer than its field length.</exception>
    private static AddressCommand CheckedAddress(
        string street,
        string city,
        string country,
        string phone,
        string fax,
        string email,
        string webSite)
    {
        FieldLengths.Check(street, FieldLengths.AddressStreet, "street");
        FieldLengths.Check(city, FieldLengths.AddressCity, "city");
        FieldLengths.Check(country, FieldLengths.AddressCountry, "country");
        FieldLengths.Check(phone, FieldLengths.AddressPhone, "phone");
        FieldLengths.Check(fax, FieldLengths.AddressFax, "fax");
        FieldLengths.Check(email, FieldLengths.AddressEmail, "email");
        FieldLengths.Check(webSite, FieldLengths.AddressWebSite, "webSite");

        return new AddressCommand
        {
            Street = street,
            City = city,
            Country = country,
            Phone = phone,
            Fax = fax,
            Email = email,
            WebSite = webSite,
        };
    }

    /// <summary>
    /// Returns a result with <paramref name="statusCode"/>, the given errors in order (none when
    /// <see langword="null"/>), no warnings and <paramref name="guid"/>.
    /// </summary>
    private static DTOResult Result(int statusCode, IEnumerable<string>? errors = null, string? guid = null) =>
        new()
        {
            statusCode = statusCode,
            allErrors = errors?.ToArray() ?? [],
            allWarnings = [],
            guid = guid,
        };
}

using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Erewhon.Application.Common;
using Erewhon.Application.Projections;
using Erewhon.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erewhon.Api.App;

/// <summary>
/// Maps the <c>/api/app/admin</c> agent, agent commission-rate and client endpoints, the back end of the agents list,
/// the agent editor (also opened as My Details), the agent commission rates screen, the clients list and the client
/// editor: the agent and client lists and details, the create, update and delete of agents and clients, the
/// commission rates of one item category with the agents using each, and the replacement of the agents holding a
/// rate. Item rows carry no photo bytes.
/// </summary>
public static class AdminPartyEndpoints
{
    private const string AgentsTag = "App: Admin agents";
    private const string ClientsTag = "App: Admin clients";

    /// <summary>
    /// Maps, under <c>/admin</c> on <paramref name="api"/>:
    /// <list type="bullet">
    /// <item><description><c>GET /admin/agents</c>: the bound company's agents as <see cref="AgentProjection"/> values in
    /// name order.</description></item>
    /// <item><description><c>GET /admin/agents/{id}</c>: the agent with its items and commission rates as an
    /// <see cref="AgentDetailsResponse"/>; 404 when no agent has the id.</description></item>
    /// <item><description><c>POST /admin/agents</c>: create an agent from an <see cref="AgentRequest"/> and return the
    /// <see cref="OperationResult"/>.</description></item>
    /// <item><description><c>PUT /admin/agents/{id}</c>: update the agent's name and address from an
    /// <see cref="AgentRequest"/> and return the <see cref="OperationResult"/>; 404 when no agent has the
    /// id.</description></item>
    /// <item><description><c>DELETE /admin/agents/{id}</c>: delete the agent and return the
    /// <see cref="OperationResult"/>; 404 when no agent has the id.</description></item>
    /// <item><description><c>GET /admin/agent-commission-rates?category={id}</c>: the item category's commission
    /// rates, each with the agents using it, and every agent of the bound company, as an
    /// <see cref="AgentCommissionRatesResponse"/>; 404 when no item category has the id.</description></item>
    /// <item><description><c>PUT /admin/agent-commission-rates</c>: make the agents of a
    /// <see cref="ReplaceRateAgentsRequest"/> the agents holding the rate and return the
    /// <see cref="OperationResult"/>; 400 when the request has no agent id list; 404 when no commission rate has the
    /// id.</description></item>
    /// <item><description><c>GET /admin/clients</c>: the bound company's clients as <see cref="ClientProjection"/>
    /// values in name order.</description></item>
    /// <item><description><c>GET /admin/clients/{id}</c>: the client with its retail and tender sales as a
    /// <see cref="ClientDetailsResponse"/>; 404 when no client has the id.</description></item>
    /// <item><description><c>POST /admin/clients</c>: create a client from a <see cref="ClientRequest"/> and return the
    /// <see cref="OperationResult"/>.</description></item>
    /// <item><description><c>PUT /admin/clients/{id}</c>: update the client's name and address from a
    /// <see cref="ClientRequest"/> and return the <see cref="OperationResult"/>; 404 when no client has the
    /// id.</description></item>
    /// <item><description><c>DELETE /admin/clients/{id}</c>: delete the client with its sales and tenders and return
    /// the <see cref="OperationResult"/>; 404 when no client has the id.</description></item>
    /// </list>
    /// Every write answers 200 with the service's <see cref="OperationResult"/>, which reports a failed validation
    /// through <see cref="OperationResult.Succeeded"/> and its errors. Every route also declares the 401 of a request
    /// without a registered session and the 500 <see cref="ErrorOutcome"/>.
    /// </summary>
    /// <param name="api">The <c>/api/app</c> route group.</param>
    /// <returns><paramref name="api"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is <see langword="null"/>.</exception>
    public static RouteGroupBuilder MapAdminPartyEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var admin = api.MapGroup("/admin");

        admin.MapGet("/agents", GetAgentsAsync)
            .WithName("AppAdminGetAgents")
            .WithTags(AgentsTag)
            .WithSummary("Returns the agents of the company in name order.")
            .Produces<IReadOnlyList<AgentProjection>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapGet("/agents/{id:long}", GetAgentAsync)
            .WithName("AppAdminGetAgent")
            .WithTags(AgentsTag)
            .WithSummary("Returns an agent with its items and its commission rates.")
            .Produces<AgentDetailsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPost("/agents", CreateAgentAsync)
            .WithName("AppAdminCreateAgent")
            .WithTags(AgentsTag)
            .WithSummary("Creates an agent with its address and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPut("/agents/{id:long}", UpdateAgentAsync)
            .WithName("AppAdminUpdateAgent")
            .WithTags(AgentsTag)
            .WithSummary("Updates the name and address of an agent and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapDelete("/agents/{id:long}", DeleteAgentAsync)
            .WithName("AppAdminDeleteAgent")
            .WithTags(AgentsTag)
            .WithSummary("Deletes an agent and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapGet("/agent-commission-rates", GetAgentCommissionRatesAsync)
            .WithName("AppAdminGetAgentCommissionRates")
            .WithTags(AgentsTag)
            .WithSummary("Returns the commission rates of an item category with the agents using each, and every agent of the company.")
            .Produces<AgentCommissionRatesResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPut("/agent-commission-rates", ReplaceCommissionRateAgentsAsync)
            .WithName("AppAdminReplaceCommissionRateAgents")
            .WithTags(AgentsTag)
            .WithSummary("Makes the listed agents the agents holding a commission rate and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapGet("/clients", GetClientsAsync)
            .WithName("AppAdminGetClients")
            .WithTags(ClientsTag)
            .WithSummary("Returns the clients of the company in name order.")
            .Produces<IReadOnlyList<ClientProjection>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapGet("/clients/{id:long}", GetClientAsync)
            .WithName("AppAdminGetClient")
            .WithTags(ClientsTag)
            .WithSummary("Returns a client with its retail sales and its tender sales.")
            .Produces<ClientDetailsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPost("/clients", CreateClientAsync)
            .WithName("AppAdminCreateClient")
            .WithTags(ClientsTag)
            .WithSummary("Creates a client with its address and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapPut("/clients/{id:long}", UpdateClientAsync)
            .WithName("AppAdminUpdateClient")
            .WithTags(ClientsTag)
            .WithSummary("Updates the name and address of a client and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        admin.MapDelete("/clients/{id:long}", DeleteClientAsync)
            .WithName("AppAdminDeleteClient")
            .WithTags(ClientsTag)
            .WithSummary("Deletes a client, with its sales and tenders, and returns the operation result.")
            .Produces<OperationResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        return api;
    }

    /// <summary>Returns the projection of each agent of the bound company in name order.</summary>
    private static async Task<Ok<IReadOnlyList<AgentProjection>>> GetAgentsAsync(
        [FromServices] AgentService agentService,
        CancellationToken ct) =>
        TypedResults.Ok(await agentService.GetAllAgentsAsync(ct));

    /// <summary>
    /// Returns the agent with row id <paramref name="id"/> with its items, ordered by item category row id, category
    /// prefix and code number, and its commission rates in item category name order; or 404 when no agent has that
    /// id. Each item carries its category, its agent and, when sold, its sale with the buying client, read with that
    /// client, and no photo bytes; the item's region name and country and the tenders of an unsold tender item are not
    /// read.
    /// </summary>
    private static async Task<Results<Ok<AgentDetailsResponse>, NotFound>> GetAgentAsync(
        [FromRoute] long id,
        [FromServices] AgentService agentService,
        [FromServices] ClientService clientService,
        CancellationToken ct)
    {
        var agent = await agentService.FindByIdAsync(id, ct);
        if (agent is null)
        {
            return TypedResults.NotFound();
        }

        var items = agent.Items
            .OrderBy(item => item.ItemCategoryId)
            .ThenBy(item => item.ItemCategory?.Prefix, StringComparer.Ordinal)
            .ThenBy(item => item.CodeNumber)
            .ToList();

        var buyerIds = new SortedSet<long>();
        foreach (var item in items)
        {
            if (item.Sale is { Client: null } sale)
            {
                buyerIds.Add(sale.ClientId);
            }
        }

        foreach (long buyerId in buyerIds)
        {
            await clientService.FindByIdAsync(buyerId, ct);
        }

        var itemRows = items
            .Select(item => ItemProjection.From(item) with { Photo = null })
            .ToList();

        var rateRows = agent.AgentCommissionRates
            .Select(link =>
            {
                var rate = link.CommissionRate
                    ?? throw new InvalidOperationException("AgentCommissionRate.CommissionRate must be loaded.");
                var category = rate.ItemCategory
                    ?? throw new InvalidOperationException("CommissionRate.ItemCategory must be loaded.");
                return new AgentRateRow(rate.Id, category.Id, category.Name, rate.Percentage);
            })
            .OrderBy(row => row.ItemCategoryName, StringComparer.Ordinal)
            .ThenBy(row => row.CommissionRateId)
            .ToList();

        return TypedResults.Ok(new AgentDetailsResponse(AgentProjection.From(agent), itemRows, rateRows));
    }

    /// <summary>Creates an agent with the request's name and address and returns the operation result.</summary>
    private static async Task<Ok<OperationResult>> CreateAgentAsync(
        [FromBody] AgentRequest request,
        [FromServices] AgentService agentService,
        CancellationToken ct)
    {
        var command = new AgentCommand
        {
            Name = request.Name,
            Address = ToAddressCommand(request.Address, existing: null),
        };

        OperationResult result = await agentService.ExecuteAsync(command, TransactionType.Persist, cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Updates the name and the seven address fields of the agent with row id <paramref name="id"/> from the request
    /// and returns the operation result, or 404 when no agent has that id. The agent's commission rates are not changed.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> UpdateAgentAsync(
        [FromRoute] long id,
        [FromBody] AgentRequest request,
        [FromServices] AgentService agentService,
        CancellationToken ct)
    {
        var agent = await agentService.FindByIdAsync(id, ct);
        if (agent is null)
        {
            return TypedResults.NotFound();
        }

        var command = new AgentCommand();
        command.PopulateFromObject(agent);
        command.Name = request.Name;
        command.Address = ToAddressCommand(request.Address, command.Address);

        OperationResult result = await agentService.ExecuteAsync(command, TransactionType.Persist, cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Deletes the agent with row id <paramref name="id"/> and returns the operation result, or 404 when no agent has
    /// that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> DeleteAgentAsync(
        [FromRoute] long id,
        [FromServices] AgentService agentService,
        CancellationToken ct)
    {
        if (!await agentService.ExistsAsync(id, ct))
        {
            return TypedResults.NotFound();
        }

        OperationResult result = await agentService.DeleteByIdAsync(id, ct: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Returns the commission rates of the item category with row id <paramref name="category"/> that belong to the
    /// bound company, in percentage order, each with the agents using it in agent name order, and every agent of the
    /// bound company in name order; or 404 when no item category has that id.
    /// </summary>
    private static async Task<Results<Ok<AgentCommissionRatesResponse>, NotFound>> GetAgentCommissionRatesAsync(
        [FromQuery(Name = "category")] long category,
        [FromServices] ItemCategoryService itemCategoryService,
        [FromServices] CommissionRateService commissionRateService,
        [FromServices] AgentService agentService,
        CancellationToken ct)
    {
        if (!await itemCategoryService.ExistsAsync(category, ct))
        {
            return TypedResults.NotFound();
        }

        var rates = await commissionRateService.GetCommissionRatesAsync(category, ct);
        var rateRows = rates
            .Select(rate => new CommissionRateAgents(
                rate.Id,
                rate.Percentage,
                rate.AgentCommissionRates
                    .Select(link => NameProjection.From(
                        link.Agent ?? throw new InvalidOperationException("AgentCommissionRate.Agent must be loaded.")))
                    .ToList()))
            .ToList();

        IReadOnlyList<NameProjection> allAgents = await agentService.GetAgentNamesAsync(ct);

        return TypedResults.Ok(new AgentCommissionRatesResponse(category, rateRows, allAgents));
    }

    /// <summary>
    /// Makes the agents of the request the agents holding its commission rate, adding the rate to each listed agent
    /// that does not hold it and removing it from each holder not listed, and returns the operation result; 400 when
    /// the request has no agent id list, 404 when no commission rate has the request's id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, BadRequest, NotFound>> ReplaceCommissionRateAgentsAsync(
        [FromBody] ReplaceRateAgentsRequest request,
        [FromServices] CommissionRateService commissionRateService,
        CancellationToken ct)
    {
        if (request.AgentIds is null)
        {
            return TypedResults.BadRequest();
        }

        if (!await commissionRateService.ExistsAsync(request.CommissionRateId, ct))
        {
            return TypedResults.NotFound();
        }

        OperationResult result = await commissionRateService.ReplaceAgentsAsync(
            request.CommissionRateId,
            request.AgentIds,
            ct);
        return TypedResults.Ok(result);
    }

    /// <summary>Returns the projection of each client of the bound company in name order.</summary>
    private static async Task<Ok<IReadOnlyList<ClientProjection>>> GetClientsAsync(
        [FromServices] ClientService clientService,
        CancellationToken ct) =>
        TypedResults.Ok(await clientService.GetAllClientsAsync(ct));

    /// <summary>
    /// Returns the client with row id <paramref name="id"/> with the rows of its retail sales, ordered by date sold,
    /// category prefix and code number, and of its tender sales, ordered by tender time, category prefix and code
    /// number; or 404 when no client has that id.
    /// </summary>
    private static async Task<Results<Ok<ClientDetailsResponse>, NotFound>> GetClientAsync(
        [FromRoute] long id,
        [FromServices] ClientService clientService,
        CancellationToken ct)
    {
        var client = await clientService.FindByIdAsync(id, ct);
        if (client is null)
        {
            return TypedResults.NotFound();
        }

        var retailSales = client.GetRetailSales().Select(SalesRowProjection.From).ToList();
        var tenderSales = client.GetTenderSales().Select(SalesRowProjection.From).ToList();

        return TypedResults.Ok(new ClientDetailsResponse(ClientProjection.From(client), retailSales, tenderSales));
    }

    /// <summary>Creates a client with the request's name and address and returns the operation result.</summary>
    private static async Task<Ok<OperationResult>> CreateClientAsync(
        [FromBody] ClientRequest request,
        [FromServices] ClientService clientService,
        CancellationToken ct)
    {
        var command = new ClientCommand
        {
            Name = request.Name,
            Address = ToAddressCommand(request.Address, existing: null),
        };

        OperationResult result = await clientService.ExecuteAsync(command, TransactionType.Persist, cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Updates the name and the seven address fields of the client with row id <paramref name="id"/> from the request
    /// and returns the operation result, or 404 when no client has that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> UpdateClientAsync(
        [FromRoute] long id,
        [FromBody] ClientRequest request,
        [FromServices] ClientService clientService,
        CancellationToken ct)
    {
        var client = await clientService.FindByIdAsync(id, ct);
        if (client is null)
        {
            return TypedResults.NotFound();
        }

        var command = new ClientCommand();
        command.PopulateFromObject(client);
        command.Name = request.Name;
        command.Address = ToAddressCommand(request.Address, command.Address);

        OperationResult result = await clientService.ExecuteAsync(command, TransactionType.Persist, cancellationToken: ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Deletes the client with row id <paramref name="id"/>, with its retail sales, tender sales and tenders, and
    /// returns the operation result, or 404 when no client has that id.
    /// </summary>
    private static async Task<Results<Ok<OperationResult>, NotFound>> DeleteClientAsync(
        [FromRoute] long id,
        [FromServices] ClientService clientService,
        CancellationToken ct)
    {
        if (!await clientService.ExistsAsync(id, ct))
        {
            return TypedResults.NotFound();
        }

        OperationResult result = await clientService.DeleteByIdAsync(id, ct);
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Returns <paramref name="existing"/>, or a new address command when it is <see langword="null"/>, with its
    /// street, city, country, phone, fax, e-mail and web site set from <paramref name="fields"/>; each is
    /// <see langword="null"/> when <paramref name="fields"/> is <see langword="null"/>. The GUID of
    /// <paramref name="existing"/> is kept.
    /// </summary>
    private static AddressCommand ToAddressCommand(AddressFields? fields, AddressCommand? existing)
    {
        var address = existing ?? new AddressCommand();
        address.Street = fields?.Street;
        address.City = fields?.City;
        address.Country = fields?.Country;
        address.Phone = fields?.Phone;
        address.Fax = fields?.Fax;
        address.Email = fields?.Email;
        address.WebSite = fields?.WebSite;
        return address;
    }
}

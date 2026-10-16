using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Projections;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Erewhon.Api.Rest;

/// <summary>
/// REST operations on the clients of the bound company: look up a client by name, list every client, and create a
/// client. Every operation answers 500 with an empty body when no company exists.
/// </summary>
/// <param name="companyContext">The company bound to the request scope.</param>
/// <param name="clientService">The client lookups and the client create operation.</param>
[ApiController]
[Route("api/rest/clients")]
[ApiExplorerSettings(GroupName = "rest")]
[Produces("application/json")]
public sealed class ClientsController(ICompanyContext companyContext, ClientService clientService) : ControllerBase
{
    /// <summary>
    /// Returns the client of the bound company whose name equals <paramref name="name"/> with its leading and
    /// trailing spaces removed.
    /// </summary>
    /// <param name="name">The client name to look up; leading and trailing spaces are ignored.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The client, or an empty response.</returns>
    /// <response code="200">The client with its GUID, name and the seven address fields.</response>
    /// <response code="404">Empty body: no client has that name.</response>
    /// <response code="500">Empty body: no company exists.</response>
    [HttpGet("by-name")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ClientDto>> GetClient(
        [FromQuery] string? name,
        CancellationToken cancellationToken)
    {
        await companyContext.GetCurrentAsync(cancellationToken);

        var client = await clientService.FindByNameAsync(JadeValues.TrimBlanks(name), cancellationToken);
        if (client is null)
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status404NotFound);
        }

        return RestDtoMapper.ToClientDto(ClientProjection.From(client));
    }

    /// <summary>
    /// Returns every client of the bound company in name order.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The clients, or an empty response.</returns>
    /// <response code="200">The clients in name order, each with its GUID, name and the seven address fields; an empty
    /// array when the company has no clients.</response>
    /// <response code="500">Empty body: no company exists.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ClientDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IReadOnlyList<ClientDto>>> GetAllClients(CancellationToken cancellationToken)
    {
        await companyContext.GetCurrentAsync(cancellationToken);

        IReadOnlyList<ClientProjection> clients = await clientService.GetAllClientsAsync(cancellationToken);
        List<ClientDto> dtos = clients.Select(RestDtoMapper.ToClientDto).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Creates a client of the bound company from <paramref name="client"/>: its name and the seven address fields.
    /// The <c>guid</c> field of the request is not stored.
    /// </summary>
    /// <param name="client">The client to create.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome of the create, or an empty response.</returns>
    /// <response code="201">The client was created: <c>statusCode</c> 201, no errors and the <c>guid</c> of the new
    /// client.</response>
    /// <response code="400">The client was not created: <c>statusCode</c> 400, every validation or conflict error in
    /// order, including a name that another client already holds, and no <c>guid</c>.</response>
    /// <response code="500">Empty body: the request has no body, a field is longer than its declared length, or no
    /// company exists.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ResultDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ResultDto>> PostCreateClient(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ClientDto? client,
        CancellationToken cancellationToken)
    {
        if (client is null)
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status500InternalServerError);
        }

        RestDtoMapper.CheckFieldLengths(client);

        await companyContext.GetCurrentAsync(cancellationToken);

        ClientCommand command = RestDtoMapper.ToClientCommand(client);
        OperationResult result = await clientService.ExecuteAsync(
            command,
            TransactionType.Persist,
            TransactionMode.Own,
            cancellationToken);

        if (!result.Succeeded)
        {
            return StatusCode(
                StatusCodes.Status400BadRequest,
                RestDtoMapper.ToResultDto(StatusCodes.Status400BadRequest, result));
        }

        return StatusCode(
            StatusCodes.Status201Created,
            RestDtoMapper.ToResultDto(StatusCodes.Status201Created, result));
    }
}

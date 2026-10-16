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
/// REST operations on the agents of the bound company under <c>/api/rest/agents</c>: look up one agent by name, list
/// every agent, and create an agent.
/// </summary>
/// <remarks>
/// Ports <c>ErewhonRestService.getAgent</c>, <c>getAllAgents</c> and <c>postCreateAgent</c>.
/// </remarks>
/// <param name="companyContext">The company bound to the request scope.</param>
/// <param name="agentService">The agent lookups and the agent create operation of the request scope.</param>
[ApiController]
[Route("api/rest/agents")]
[ApiExplorerSettings(GroupName = "rest")]
[Produces("application/json")]
public sealed class AgentsController(ICompanyContext companyContext, AgentService agentService) : ControllerBase
{
    /// <summary>
    /// Returns the agent of the bound company whose name equals <paramref name="name"/> exactly, with case and
    /// untrimmed.
    /// </summary>
    /// <remarks>
    /// 200 with the agent's GUID, name and seven address fields; 404 with an empty body when no agent has that name,
    /// including a missing, empty or overlong name; 500 with an empty body when no company exists.
    /// </remarks>
    /// <param name="name">The agent name to look up, used as given.</param>
    /// <param name="cancellationToken">Token that cancels the lookup.</param>
    /// <returns>The agent, or an empty response with the status of the failure.</returns>
    [HttpGet("by-name")]
    [ProducesResponseType(typeof(AgentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<AgentDto>> GetAgent(
        [FromQuery] string? name,
        CancellationToken cancellationToken)
    {
        await companyContext.GetCurrentAsync(cancellationToken);

        var agent = await agentService.FindByNameAsync(name ?? string.Empty, cancellationToken);
        if (agent is null)
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status404NotFound);
        }

        AgentProjection projection = AgentProjection.From(agent);
        return RestDtoMapper.ToAgentDto(projection);
    }

    /// <summary>
    /// Returns every agent of the bound company in name order.
    /// </summary>
    /// <remarks>
    /// 200 with an array of agents, each with its GUID, name and seven address fields, empty when the company has no
    /// agents; 500 with an empty body when no company exists.
    /// </remarks>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>The agents, or an empty response with status 500.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AgentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IReadOnlyList<AgentDto>>> GetAllAgents(CancellationToken cancellationToken)
    {
        await companyContext.GetCurrentAsync(cancellationToken);

        IReadOnlyList<AgentProjection> agents = await agentService.GetAllAgentsAsync(cancellationToken);
        List<AgentDto> dtos = agents.Select(RestDtoMapper.ToAgentDto).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Creates an agent of the bound company from <paramref name="agent"/>: its name and seven address fields, each
    /// trimmed and validated by the agent create operation. The agent's <c>guid</c> field is not used.
    /// </summary>
    /// <remarks>
    /// 201 with a result whose <c>statusCode</c> is 201, no errors and the GUID of the new agent; 400 with a result
    /// whose <c>statusCode</c> is 400 and the validation errors in order, including <c>Agent already exists</c> for a
    /// name already in use, when the agent is not created; 500 with an empty body when the request has no body, when a
    /// field is longer than its declared length before trimming, or when no company exists.
    /// </remarks>
    /// <param name="agent">The agent to create, as given in the request body.</param>
    /// <param name="cancellationToken">Token that cancels the create.</param>
    /// <returns>The outcome of the create, or an empty response with status 500.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(ResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ResultDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ResultDto>> PostCreateAgent(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] AgentDto? agent,
        CancellationToken cancellationToken)
    {
        if (agent is null)
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status500InternalServerError);
        }

        RestDtoMapper.CheckFieldLengths(agent);

        await companyContext.GetCurrentAsync(cancellationToken);

        AgentCommand command = RestDtoMapper.ToAgentCommand(agent);
        OperationResult result = await agentService.ExecuteAsync(
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

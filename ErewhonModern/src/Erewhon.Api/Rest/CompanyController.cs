using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Projections;
using Microsoft.AspNetCore.Mvc;

namespace Erewhon.Api.Rest;

/// <summary>
/// REST operation <c>getCompany</c> at <c>GET /api/rest/company</c>.
/// </summary>
/// <param name="companyContext">The company bound to the request scope.</param>
[ApiController]
[Route("api/rest/company")]
[ApiExplorerSettings(GroupName = "rest")]
[Produces("application/json")]
public sealed class CompanyController(ICompanyContext companyContext) : ControllerBase
{
    /// <summary>
    /// Returns the bound company: 200 with the company's GUID, name and the seven fields of its address; 500 with an
    /// empty body when no company exists.
    /// </summary>
    /// <param name="cancellationToken">Cancels the company read.</param>
    /// <returns>200 with a <see cref="CompanyDto"/>, or 500 with an empty body.</returns>
    [HttpGet]
    [ProducesResponseType<CompanyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<CompanyDto>> GetCompany(CancellationToken cancellationToken)
    {
        var company = await companyContext.FindCurrentAsync(cancellationToken);
        if (company is null)
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status500InternalServerError);
        }

        return RestDtoMapper.ToCompanyDto(CompanyProjection.From(company));
    }
}

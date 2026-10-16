using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Projections;
using Erewhon.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace Erewhon.Api.Rest;

/// <summary>
/// REST operations on the sales of the bound company: list every sale, and look up the sale of an item by the item's
/// category prefix and code number. Each sale is returned typed <c>retailSale</c> or <c>tenderSale</c>.
/// </summary>
/// <param name="companyContext">The company bound to the request scope.</param>
/// <param name="retailSaleService">The service that reads the company's sales.</param>
[ApiController]
[Route("api/rest/sales")]
[ApiExplorerSettings(GroupName = "rest")]
[Produces("application/json")]
public sealed class SalesController(ICompanyContext companyContext, RetailSaleService retailSaleService)
    : ControllerBase
{
    private const int MaxCodePrefixLength = 4;

    /// <summary>
    /// Lists every sale of the bound company, retail and tender, ordered by the prefix of the sold item's category and
    /// then by the item's code number.
    /// </summary>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The sales of the bound company.</returns>
    /// <response code="200">
    /// An array of sales, each typed <c>retailSale</c> (with <c>timestamp</c>) or <c>tenderSale</c> (with
    /// <c>tender</c> and <c>offer</c>); an empty array when the company has no sales.
    /// </response>
    /// <response code="500">No company exists. The body is empty.</response>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SaleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IReadOnlyList<SaleDto>>> GetAllSales(CancellationToken ct)
    {
        await companyContext.GetCurrentAsync(ct);

        IReadOnlyList<SaleProjection> sales = await retailSaleService.GetAllSalesByItemAsync(ct);

        var dtos = new List<SaleDto>(sales.Count);
        foreach (SaleProjection sale in sales)
        {
            dtos.Add(RestDtoMapper.ToSaleDto(sale));
        }

        return new ActionResult<IReadOnlyList<SaleDto>>(dtos);
    }

    /// <summary>
    /// Returns the sale of the bound company's item whose category prefix is <paramref name="codePrefix"/>, as given,
    /// and whose code number is <paramref name="codeNumber"/>. The prefix is checked with leading and trailing spaces
    /// removed, and looked up with them kept.
    /// </summary>
    /// <param name="codePrefix">The category prefix of the sold item.</param>
    /// <param name="codeNumber">The code number of the sold item.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>The sale of the item.</returns>
    /// <response code="200">The sale, typed <c>retailSale</c> or <c>tenderSale</c>.</response>
    /// <response code="400">
    /// The prefix without leading and trailing spaces is empty or longer than 4 characters, or the code number is 0.
    /// The body is empty. These checks precede the company read.
    /// </response>
    /// <response code="404">The company has no sale of an item with that prefix and code number. The body is empty.</response>
    /// <response code="500">The input is valid and no company exists. The body is empty.</response>
    [HttpGet("by-code")]
    [ProducesResponseType<SaleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<SaleDto>> GetSale(
        [FromQuery] string? codePrefix,
        [FromQuery] int codeNumber,
        CancellationToken ct)
    {
        string trimmedPrefix = JadeValues.TrimBlanks(codePrefix);
        if (JadeValues.IsNull(trimmedPrefix) || trimmedPrefix.Length > MaxCodePrefixLength)
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status400BadRequest);
        }

        if (JadeValues.IsNull(codeNumber))
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status400BadRequest);
        }

        await companyContext.GetCurrentAsync(ct);

        SaleProjection? sale = await retailSaleService.FindSaleByItemCodeAsync(
            codePrefix ?? string.Empty,
            codeNumber,
            ct);
        if (sale is null)
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status404NotFound);
        }

        SaleDto dto = RestDtoMapper.ToSaleDto(sale);
        return dto;
    }
}

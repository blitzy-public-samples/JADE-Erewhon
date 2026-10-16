using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Projections;
using Erewhon.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace Erewhon.Api.Rest;

/// <summary>
/// REST operations on the items of the company: <c>getItemByCode</c> and <c>getItemsByPrice</c> of the JADE
/// <c>ErewhonRestService</c>. Each item is returned as a retail or tender item carrying the <c>type</c> discriminator
/// <c>retailItem</c> or <c>tenderItem</c>, without its category prefix or photo.
/// </summary>
/// <param name="companyContext">The company bound to the request.</param>
/// <param name="companyService">Looks up an item of the company by its code number.</param>
/// <param name="itemService">Lists the items of the company in a price band.</param>
[ApiController]
[Route("api/rest/items")]
[ApiExplorerSettings(GroupName = "rest")]
[Produces("application/json")]
[ProducesErrorResponseType(typeof(void))]
public sealed class ItemsController(
    ICompanyContext companyContext,
    CompanyService companyService,
    ItemService itemService) : ControllerBase
{
    private const int MaxLookupPrefixLength = 4;

    /// <summary>
    /// Returns the item of the company with code number <paramref name="codeNumber"/>. The item is found by its
    /// number alone; the prefix is only checked.
    /// </summary>
    /// <param name="codePrefix">The category prefix of the item code; leading and trailing spaces are ignored.</param>
    /// <param name="codeNumber">The code number of the item; 0 when absent.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>
    /// 400 with an empty body when the prefix, with leading and trailing spaces removed, is empty or longer than 4
    /// characters, or the code number is 0; 500 with an empty body when no company exists; 404 with an empty body
    /// when the company has no item with that number; otherwise 200 with the retail or tender item.
    /// </returns>
    [HttpGet("by-code")]
    [ProducesResponseType(typeof(ItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ItemDto>> GetItemByCode(
        [FromQuery] string? codePrefix,
        [FromQuery] int codeNumber,
        CancellationToken ct)
    {
        string prefix = JadeValues.TrimBlanks(codePrefix);
        if (JadeValues.IsNull(prefix) || prefix.Length > MaxLookupPrefixLength)
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status400BadRequest);
        }

        if (JadeValues.IsNull(codeNumber))
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status400BadRequest);
        }

        Company company = await companyContext.GetCurrentAsync(ct);

        ItemProjection? item = await companyService.GetItemProjectionAsync(company.Id, codeNumber, ct);
        if (item is null)
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status404NotFound);
        }

        ItemDto dto = RestDtoMapper.ToItemDto(item);
        return dto;
    }

    /// <summary>
    /// Returns the items of the company whose price lies between <paramref name="minPrice"/> and
    /// <paramref name="maxPrice"/>, both included, in code-number order.
    /// </summary>
    /// <param name="minPrice">The lowest price included; 0 when absent.</param>
    /// <param name="maxPrice">The highest price included; 0 when absent.</param>
    /// <param name="ct">Token that cancels the read.</param>
    /// <returns>
    /// 400 with an empty body when the minimum price is below 0, the maximum price is below 0, or the maximum price
    /// is below the minimum price, checked in that order; 500 with an empty body when no company exists; otherwise
    /// 200 with the retail and tender items in the band, an empty array when there are none.
    /// </returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<IReadOnlyList<ItemDto>>> GetItemsByPrice(
        [FromQuery] int minPrice,
        [FromQuery] int maxPrice,
        CancellationToken ct)
    {
        if (minPrice < 0)
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status400BadRequest);
        }

        if (maxPrice < 0)
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status400BadRequest);
        }

        if (maxPrice < minPrice)
        {
            return RestExceptionFilter.EmptyResponse(StatusCodes.Status400BadRequest);
        }

        await companyContext.GetCurrentAsync(ct);

        IReadOnlyList<ItemProjection> items = await itemService.GetItemsByPriceAsync(minPrice, maxPrice, ct);

        List<ItemDto> dtos = new(items.Count);
        foreach (ItemProjection item in items)
        {
            dtos.Add(RestDtoMapper.ToItemDto(item));
        }

        return new ActionResult<IReadOnlyList<ItemDto>>(dtos);
    }
}

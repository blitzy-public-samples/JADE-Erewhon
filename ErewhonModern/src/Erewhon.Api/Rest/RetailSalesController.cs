using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Erewhon.Api.Rest;

/// <summary>
/// REST operation <c>postCreateRetailSale</c>: records a retail sale of an item of the bound company to one of its
/// clients at <c>POST /api/rest/retail-sales</c>.
/// </summary>
/// <param name="companyContext">The company bound to the request scope.</param>
/// <param name="clientService">Looks up the buying client by name.</param>
/// <param name="companyService">Looks up the sold item by code number.</param>
/// <param name="retailSaleService">Validates and persists the retail sale.</param>
[ApiController]
[Route("api/rest/retail-sales")]
[ApiExplorerSettings(GroupName = "rest")]
[Produces("application/json")]
public sealed class RetailSalesController(
    ICompanyContext companyContext,
    ClientService clientService,
    CompanyService companyService,
    RetailSaleService retailSaleService) : ControllerBase
{
    /// <summary>
    /// Records a retail sale of the item with code number <see cref="RetailSaleRequest.CodeNumber"/> to the client
    /// named <see cref="RetailSaleRequest.Client"/>, at the item's price, sold at the database server's current
    /// timestamp, with the agent commission computed at creation. The client name is matched exactly, untrimmed; the
    /// item is looked up by its number alone and <see cref="RetailSaleRequest.CodePrefix"/> is not used. A request
    /// with no body is handled as one whose fields are all absent. Outcomes, checked in this order:
    /// <list type="bullet">
    /// <item><description>500 with an empty body when no company is bound.</description></item>
    /// <item><description>
    /// 404 with <see cref="MessageKeys.ClientNotFound"/> as the only error when the bound company has no client with
    /// that name.
    /// </description></item>
    /// <item><description>
    /// 404 with <see cref="MessageKeys.ItemNotFound"/> as the only error when the bound company has no item with
    /// that number.
    /// </description></item>
    /// <item><description>
    /// 400 with <see cref="MessageKeys.ItemNotRetailItem"/> as the only error when the item is a tender item.
    /// </description></item>
    /// <item><description>
    /// 400 with the retail sale's validation and lock errors in the order they were raised, for example
    /// <see cref="MessageKeys.ItemSold"/>, when the sale is not persisted.
    /// </description></item>
    /// <item><description>
    /// 201 with no errors and the new retail sale's source GUID when the sale is persisted.
    /// </description></item>
    /// </list>
    /// Every <see cref="ResultDto"/> returned carries the HTTP status as <see cref="ResultDto.StatusCode"/>.
    /// </summary>
    /// <param name="request">
    /// The client name, category prefix and code number; <see langword="null"/> when the request has no body.
    /// </param>
    /// <param name="ct">Token that cancels the operation when the request is aborted.</param>
    /// <returns>The result of the operation with its HTTP status.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(ResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ResultDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ResultDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ResultDto>> PostCreateRetailSale(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RetailSaleRequest? request,
        CancellationToken ct)
    {
        request ??= new RetailSaleRequest();

        Company company = await companyContext.GetCurrentAsync(ct);

        Client? client = await clientService.FindByNameAsync(request.Client ?? string.Empty, ct);
        if (client is null)
        {
            return StatusCode(
                StatusCodes.Status404NotFound,
                RestDtoMapper.ErrorResult(
                    StatusCodes.Status404NotFound,
                    MessageCatalog.Get(MessageKeys.ClientNotFound)));
        }

        Item? item = await companyService.GetItem(company.Id, request.CodeNumber, ct);
        if (item is null)
        {
            return StatusCode(
                StatusCodes.Status404NotFound,
                RestDtoMapper.ErrorResult(
                    StatusCodes.Status404NotFound,
                    MessageCatalog.Get(MessageKeys.ItemNotFound)));
        }

        if (item is not RetailItem retailItem)
        {
            return StatusCode(
                StatusCodes.Status400BadRequest,
                RestDtoMapper.ErrorResult(
                    StatusCodes.Status400BadRequest,
                    MessageCatalog.Get(MessageKeys.ItemNotRetailItem)));
        }

        var command = new RetailSaleCommand
        {
            ItemId = retailItem.Id,
            ClientId = client.Id,
            SalePrice = retailItem.Price,
        };

        OperationResult result = await retailSaleService.ExecuteAsync(
            command,
            TransactionType.Persist,
            TransactionMode.Own,
            ct);

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

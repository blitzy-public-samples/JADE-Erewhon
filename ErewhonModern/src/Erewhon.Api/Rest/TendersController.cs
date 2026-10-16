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
/// The REST tender operation: places a tender on a tender item of the bound company for a client named in the request.
/// Ports <c>ErewhonRestService.postCreateTender</c>.
/// </summary>
/// <param name="companyContext">The company bound to the request scope.</param>
/// <param name="clientService">Finds the client by name.</param>
/// <param name="companyService">Finds the item by code number.</param>
/// <param name="tenderService">Validates and persists the tender.</param>
[ApiController]
[Route("api/rest/tenders")]
[ApiExplorerSettings(GroupName = "rest")]
[Produces("application/json")]
public sealed class TendersController(
    ICompanyContext companyContext,
    ClientService clientService,
    CompanyService companyService,
    TenderService tenderService) : ControllerBase
{
    /// <summary>
    /// Places a tender of <c>offer</c>, rounded to 2 decimal places with ties away from zero, by the client whose name
    /// equals <c>client</c> exactly, untrimmed, on the item of the bound company with code number <c>codeNumber</c>,
    /// timestamped with the database server's current timestamp, in its own transaction. <c>codePrefix</c> is not read.
    /// An absent body is read as a request with every field unset. The checks run in this order: the bound company, the
    /// client, the item, the item kind, then the tender's own validation.
    /// </summary>
    /// <param name="request">The client name, the item code and the offer.</param>
    /// <param name="ct">Token that cancels the reads and the persist.</param>
    /// <returns>A <see cref="ResultDto"/> whose <see cref="ResultDto.StatusCode"/> equals the HTTP status.</returns>
    /// <response code="201">The tender was persisted: no errors, no warnings and the new tender's source GUID.</response>
    /// <response code="400">
    /// The item is a retail item: the single error "Item is not a Tender Item". Or the tender was not persisted: its
    /// validation or lock errors in the order they were raised, and no GUID.
    /// </response>
    /// <response code="404">
    /// No client of the bound company has the name: the single error "Client not found". Or the bound company has no
    /// item with the code number: the single error "Item was not found".
    /// </response>
    /// <response code="500">No company is bound: an empty body.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ResultDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ResultDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ResultDto>> PostCreateTender(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] TenderRequest? request,
        CancellationToken ct)
    {
        request ??= new TenderRequest();

        var company = await companyContext.GetCurrentAsync(ct);

        var client = await clientService.FindByNameAsync(request.Client, ct);
        if (client is null)
        {
            return StatusCode(
                StatusCodes.Status404NotFound,
                RestDtoMapper.ErrorResult(
                    StatusCodes.Status404NotFound,
                    MessageCatalog.Get(MessageKeys.ClientNotFound)));
        }

        var item = await companyService.GetItem(company.Id, request.CodeNumber, ct);
        if (item is null)
        {
            return StatusCode(
                StatusCodes.Status404NotFound,
                RestDtoMapper.ErrorResult(
                    StatusCodes.Status404NotFound,
                    MessageCatalog.Get(MessageKeys.ItemNotFound)));
        }

        if (item is not TenderItem tenderItem)
        {
            return StatusCode(
                StatusCodes.Status400BadRequest,
                RestDtoMapper.ErrorResult(
                    StatusCodes.Status400BadRequest,
                    MessageCatalog.Get(MessageKeys.ItemNotTenderItem)));
        }

        var command = new TenderCommand
        {
            Offer = DecimalRules.Round2(request.Offer),
            ClientId = client.Id,
            TenderItemId = tenderItem.Id,
        };

        OperationResult result = await tenderService.ExecuteAsync(
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

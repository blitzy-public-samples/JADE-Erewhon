using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Common;
using Erewhon.Application.Projections;
using Erewhon.Application.Services;

namespace Erewhon.Api.Rest;

/// <summary>
/// Maps application read models to the REST contracts, the REST contracts to the agent and client commands, and
/// operation results to <see cref="ResultDto"/>. Ports the JADE <c>DTO*.populateFromObject</c> methods.
/// </summary>
public static class RestDtoMapper
{
    /// <summary>
    /// Maps an agent to an <see cref="AgentDto"/>: source GUID, name and the seven address fields.
    /// </summary>
    /// <param name="agent">The agent to map.</param>
    /// <returns>The agent contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is <see langword="null"/>.</exception>
    public static AgentDto ToAgentDto(AgentProjection agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new AgentDto
        {
            Guid = agent.Guid,
            Name = agent.Name,
            Street = agent.Street,
            City = agent.City,
            Country = agent.Country,
            Phone = agent.Phone,
            Fax = agent.Fax,
            Email = agent.Email,
            Website = agent.WebSite,
        };
    }

    /// <summary>
    /// Maps a client to a <see cref="ClientDto"/>: source GUID, name and the seven address fields.
    /// </summary>
    /// <param name="client">The client to map.</param>
    /// <returns>The client contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public static ClientDto ToClientDto(ClientProjection client)
    {
        ArgumentNullException.ThrowIfNull(client);

        return new ClientDto
        {
            Guid = client.Guid,
            Name = client.Name,
            Street = client.Street,
            City = client.City,
            Country = client.Country,
            Phone = client.Phone,
            Fax = client.Fax,
            Email = client.Email,
            Website = client.WebSite,
        };
    }

    /// <summary>
    /// Maps the company to a <see cref="CompanyDto"/>: source GUID, name and the seven address fields.
    /// </summary>
    /// <param name="company">The company to map.</param>
    /// <returns>The company contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="company"/> is <see langword="null"/>.</exception>
    public static CompanyDto ToCompanyDto(CompanyProjection company)
    {
        ArgumentNullException.ThrowIfNull(company);

        return new CompanyDto
        {
            Guid = company.Guid,
            Name = company.Name,
            Street = company.Street,
            City = company.City,
            Country = company.Country,
            Phone = company.Phone,
            Fax = company.Fax,
            Email = company.Email,
            Website = company.WebSite,
        };
    }

    /// <summary>
    /// Maps a retail or tender item to a <see cref="RetailItemDto"/> or <see cref="TenderItemDto"/> by its kind,
    /// returned as <see cref="ItemDto"/>, which serialises with the <c>type</c> discriminator.
    /// </summary>
    /// <param name="item">The item to map, loaded with its agent, region, category and sale.</param>
    /// <returns>The retail or tender item contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    /// <exception cref="NoCompanyException">The item has no agent or no region.</exception>
    /// <exception cref="InvalidOperationException">The item's agent or region is set but was not loaded.</exception>
    public static ItemDto ToItemDto(ItemProjection item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.IsTender ? ToTenderItemDto(item) : ToRetailItemDto(item);
    }

    /// <summary>
    /// Maps a retail item to a <see cref="RetailItemDto"/>: the item fields of <see cref="ToBaseItemDto"/> and, when
    /// the item is sold, a <see cref="RetailSaleDetailDto"/> with the sale's source GUID, client, agent commission,
    /// date sold and sale price. Leaves <see cref="ItemDto.CategoryPrefix"/> and <see cref="ItemDto.Photo"/> unset.
    /// </summary>
    /// <param name="item">The retail item to map, loaded with its agent, region, category and sale.</param>
    /// <returns>The retail item contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="item"/> is a tender item.</exception>
    /// <exception cref="NoCompanyException">The item has no agent or no region.</exception>
    /// <exception cref="InvalidOperationException">The item's agent or region is set but was not loaded.</exception>
    public static RetailItemDto ToRetailItemDto(ItemProjection item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsTender)
        {
            throw new ArgumentException("A tender item cannot be mapped to a retail item contract.", nameof(item));
        }

        var dto = new RetailItemDto();
        PopulateItem(dto, item);

        if (item.Sale is not null)
        {
            var sale = new RetailSaleDetailDto();
            PopulateSaleDetail(sale, item.Sale);
            sale.SoldDate = item.Sale.SoldDate;
            sale.Price = item.Sale.Price ?? 0m;
            dto.Sale = sale;
        }

        return dto;
    }

    /// <summary>
    /// Maps a tender item to a <see cref="TenderItemDto"/>: the item fields of <see cref="ToBaseItemDto"/>, the
    /// closure date and, when the item is sold, a <see cref="SaleDetailDto"/> holding only the sale's source GUID,
    /// client and agent commission. Leaves <see cref="ItemDto.CategoryPrefix"/> and <see cref="ItemDto.Photo"/> unset.
    /// </summary>
    /// <param name="item">The tender item to map, loaded with its agent, region, category and sale.</param>
    /// <returns>The tender item contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="item"/> is a retail item.</exception>
    /// <exception cref="NoCompanyException">The item has no agent or no region.</exception>
    /// <exception cref="InvalidOperationException">The item's agent or region is set but was not loaded.</exception>
    public static TenderItemDto ToTenderItemDto(ItemProjection item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsTender)
        {
            throw new ArgumentException("A retail item cannot be mapped to a tender item contract.", nameof(item));
        }

        var dto = new TenderItemDto();
        PopulateItem(dto, item);
        dto.ClosureDate = item.ClosureDate;

        if (item.Sale is not null)
        {
            var sale = new SaleDetailDto();
            PopulateSaleDetail(sale, item.Sale);
            dto.Sale = sale;
        }

        return dto;
    }

    /// <summary>
    /// Maps a retail or tender item to a plain <see cref="ItemDto"/>, which serialises without a <c>type</c>
    /// discriminator: source GUID, agent, code number, description, listed date, price, region name, category name
    /// and name. Leaves <see cref="ItemDto.CategoryPrefix"/>, <see cref="ItemDto.Photo"/> and
    /// <see cref="ItemDto.Sale"/> unset.
    /// </summary>
    /// <param name="item">The item to map, loaded with its agent, region and category.</param>
    /// <returns>The item contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    /// <exception cref="NoCompanyException">The item has no agent or no region.</exception>
    /// <exception cref="InvalidOperationException">The item's agent or region is set but was not loaded.</exception>
    public static ItemDto ToBaseItemDto(ItemProjection item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var dto = new ItemDto();
        PopulateItem(dto, item);
        return dto;
    }

    /// <summary>
    /// Maps a retail or tender sale to a <see cref="RetailSaleDto"/> or <see cref="TenderSaleDto"/> by its kind,
    /// returned as <see cref="SaleDto"/>, which serialises with the <c>type</c> discriminator. Both carry the
    /// sale's source GUID, agent commission, client and the item as mapped by <see cref="ToBaseItemDto"/>; a retail
    /// sale adds the time it was made, and a tender sale adds the winning tender as mapped by
    /// <see cref="ToTenderDto"/>. Leaves <see cref="TenderSaleDto.Offer"/> at 0.
    /// </summary>
    /// <param name="sale">The sale to map, loaded with its client, item and, for a tender sale, its tender.</param>
    /// <returns>The retail or tender sale contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sale"/> is <see langword="null"/>.</exception>
    /// <exception cref="NoCompanyException">
    /// The sold item has no agent or no region, or a tender sale has no tender.
    /// </exception>
    /// <exception cref="InvalidOperationException">An item's agent or region is set but was not loaded.</exception>
    public static SaleDto ToSaleDto(SaleProjection sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        if (!sale.IsTender)
        {
            var retailSale = new RetailSaleDto();
            PopulateSale(retailSale, sale);
            retailSale.Timestamp = sale.Timestamp;
            return retailSale;
        }

        var tenderSale = new TenderSaleDto();
        PopulateSale(tenderSale, sale);
        tenderSale.Tender = ToTenderDto(sale.Tender ?? throw new NoCompanyException());
        return tenderSale;
    }

    /// <summary>
    /// Maps a tender to a <see cref="TenderDto"/>: source GUID, client, offer, the tender item as mapped by
    /// <see cref="ToTenderItemDto"/> and the time the tender was placed.
    /// </summary>
    /// <param name="tender">The tender to map, loaded with its client and its tender item.</param>
    /// <returns>The tender contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tender"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The tender's item is a retail item.</exception>
    /// <exception cref="NoCompanyException">The tender item has no agent or no region.</exception>
    /// <exception cref="InvalidOperationException">The tender item's agent or region is set but was not loaded.</exception>
    public static TenderDto ToTenderDto(TenderProjection tender)
    {
        ArgumentNullException.ThrowIfNull(tender);

        return new TenderDto
        {
            Guid = tender.Guid,
            Client = ToClientDto(tender.Client),
            Offer = tender.Offer,
            TenderItem = ToTenderItemDto(tender.TenderItem),
            Timestamp = tender.Timestamp,
        };
    }

    /// <summary>
    /// Maps an operation result to a <see cref="ResultDto"/> with <paramref name="statusCode"/>. A failed operation
    /// gives its errors in order, no warnings and no GUID; a successful one gives no errors, no warnings and the
    /// source GUID of the object it created or changed.
    /// </summary>
    /// <param name="statusCode">The HTTP status code of the outcome.</param>
    /// <param name="result">The result of the operation.</param>
    /// <returns>The result contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static ResultDto ToResultDto(int statusCode, OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Succeeded)
        {
            return new ResultDto
            {
                StatusCode = statusCode,
                AllErrors = [.. result.Errors],
                AllWarnings = [],
                Guid = null,
            };
        }

        return new ResultDto
        {
            StatusCode = statusCode,
            AllErrors = [],
            AllWarnings = [],
            Guid = result.Guid,
        };
    }

    /// <summary>
    /// Creates a <see cref="ResultDto"/> with <paramref name="statusCode"/> holding <paramref name="errorText"/> as
    /// its only error, no warnings and no GUID.
    /// </summary>
    /// <param name="statusCode">The HTTP status code of the outcome.</param>
    /// <param name="errorText">The error message text.</param>
    /// <returns>The result contract.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="errorText"/> is <see langword="null"/>.</exception>
    public static ResultDto ErrorResult(int statusCode, string errorText)
    {
        ArgumentNullException.ThrowIfNull(errorText);

        return new ResultDto
        {
            StatusCode = statusCode,
            AllErrors = [errorText],
            AllWarnings = [],
            Guid = null,
        };
    }

    /// <summary>
    /// Checks each field of <paramref name="agent"/>, untrimmed, against its declared length: GUID 39; name 81;
    /// street, city and country 51; phone and fax 26; e-mail and web site 61. A <see langword="null"/> field is not
    /// checked.
    /// </summary>
    /// <param name="agent">The agent contract to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is <see langword="null"/>.</exception>
    /// <exception cref="StringTooLongException">A field is longer than its declared length.</exception>
    public static void CheckFieldLengths(AgentDto agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        FieldLengths.Check(agent.Guid, FieldLengths.Guid, nameof(AgentDto.Guid));
        FieldLengths.Check(agent.Name, FieldLengths.AgentName, nameof(AgentDto.Name));
        FieldLengths.Check(agent.Street, FieldLengths.AddressStreet, nameof(AgentDto.Street));
        FieldLengths.Check(agent.City, FieldLengths.AddressCity, nameof(AgentDto.City));
        FieldLengths.Check(agent.Country, FieldLengths.AddressCountry, nameof(AgentDto.Country));
        FieldLengths.Check(agent.Phone, FieldLengths.AddressPhone, nameof(AgentDto.Phone));
        FieldLengths.Check(agent.Fax, FieldLengths.AddressFax, nameof(AgentDto.Fax));
        FieldLengths.Check(agent.Email, FieldLengths.AddressEmail, nameof(AgentDto.Email));
        FieldLengths.Check(agent.Website, FieldLengths.AddressWebSite, nameof(AgentDto.Website));
    }

    /// <summary>
    /// Checks each field of <paramref name="client"/>, untrimmed, against its declared length: GUID 39; name 81;
    /// street, city and country 51; phone and fax 26; e-mail and web site 61. A <see langword="null"/> field is not
    /// checked.
    /// </summary>
    /// <param name="client">The client contract to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="StringTooLongException">A field is longer than its declared length.</exception>
    public static void CheckFieldLengths(ClientDto client)
    {
        ArgumentNullException.ThrowIfNull(client);

        FieldLengths.Check(client.Guid, FieldLengths.Guid, nameof(ClientDto.Guid));
        FieldLengths.Check(client.Name, FieldLengths.ClientName, nameof(ClientDto.Name));
        FieldLengths.Check(client.Street, FieldLengths.AddressStreet, nameof(ClientDto.Street));
        FieldLengths.Check(client.City, FieldLengths.AddressCity, nameof(ClientDto.City));
        FieldLengths.Check(client.Country, FieldLengths.AddressCountry, nameof(ClientDto.Country));
        FieldLengths.Check(client.Phone, FieldLengths.AddressPhone, nameof(ClientDto.Phone));
        FieldLengths.Check(client.Fax, FieldLengths.AddressFax, nameof(ClientDto.Fax));
        FieldLengths.Check(client.Email, FieldLengths.AddressEmail, nameof(ClientDto.Email));
        FieldLengths.Check(client.Website, FieldLengths.AddressWebSite, nameof(ClientDto.Website));
    }

    /// <summary>
    /// Maps an agent contract to a create command: the name and the seven address fields, copied untrimmed, with
    /// <see langword="null"/> fields passed through. The command's GUIDs are left unset.
    /// </summary>
    /// <param name="agent">The agent contract to map.</param>
    /// <returns>The agent command.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is <see langword="null"/>.</exception>
    public static AgentCommand ToAgentCommand(AgentDto agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new AgentCommand
        {
            Name = agent.Name,
            Address = new AddressCommand
            {
                Street = agent.Street,
                City = agent.City,
                Country = agent.Country,
                Email = agent.Email,
                Fax = agent.Fax,
                Phone = agent.Phone,
                WebSite = agent.Website,
            },
        };
    }

    /// <summary>
    /// Maps a client contract to a create command: the name and the seven address fields, copied untrimmed, with
    /// <see langword="null"/> fields passed through. The command's GUIDs are left unset.
    /// </summary>
    /// <param name="client">The client contract to map.</param>
    /// <returns>The client command.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public static ClientCommand ToClientCommand(ClientDto client)
    {
        ArgumentNullException.ThrowIfNull(client);

        return new ClientCommand
        {
            Name = client.Name,
            Address = new AddressCommand
            {
                Street = client.Street,
                City = client.City,
                Country = client.Country,
                Email = client.Email,
                Fax = client.Fax,
                Phone = client.Phone,
                WebSite = client.Website,
            },
        };
    }

    private static void PopulateItem(ItemDto dto, ItemProjection item)
    {
        dto.Guid = item.Guid;

        if (item.AgentId is null)
        {
            throw new NoCompanyException();
        }

        dto.Agent = ToAgentDto(
            item.Agent ?? throw new InvalidOperationException("Item projection loaded without its agent."));
        dto.Number = item.CodeNumber;
        dto.Description = item.Description;
        dto.ListedDate = item.ListedDate;
        dto.Price = item.Price;

        if (item.RegionId is null)
        {
            throw new NoCompanyException();
        }

        dto.Region = item.RegionName
            ?? throw new InvalidOperationException("Item projection loaded without its region.");
        dto.ItemCategory = item.ItemCategoryName;
        dto.Name = item.Name;
    }

    private static void PopulateSaleDetail(SaleDetailDto dto, SaleDetailProjection sale)
    {
        dto.Guid = sale.Guid;
        dto.Client = ToClientDto(sale.Client);
        dto.AgentCommission = sale.AgentCommission;
    }

    private static void PopulateSale(SaleDto dto, SaleProjection sale)
    {
        dto.Guid = sale.Guid;
        dto.AgentCommission = sale.AgentCommission;
        dto.Client = ToClientDto(sale.Client);
        dto.Item = ToBaseItemDto(sale.Item);
    }
}

using Erewhon.Application.Domain;

namespace Erewhon.Application.Projections;

/// <summary>
/// Read model of the JADE <c>DTOCompany</c> populated by <c>DTOCompany.populateFromObject</c>: the company's row id,
/// source GUID, name and the seven fields of its address.
/// </summary>
/// <param name="Id">The row id of the company.</param>
/// <param name="Guid">The source GUID text of the company.</param>
/// <param name="Name">The company name.</param>
/// <param name="Street">The street of the company's address.</param>
/// <param name="City">The city of the company's address.</param>
/// <param name="Country">The country of the company's address.</param>
/// <param name="Phone">The phone number of the company's address.</param>
/// <param name="Fax">The fax number of the company's address.</param>
/// <param name="Email">The e-mail address of the company's address.</param>
/// <param name="WebSite">The web site of the company's address.</param>
public sealed record CompanyProjection(
    long Id,
    string Guid,
    string Name,
    string Street,
    string City,
    string Country,
    string Phone,
    string Fax,
    string Email,
    string WebSite)
{
    /// <summary>
    /// Builds the projection from a company and its address, copying each value as stored.
    /// </summary>
    /// <param name="company">The company to project.</param>
    /// <returns>The projection of <paramref name="company"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="company"/> is <see langword="null"/>.</exception>
    public static CompanyProjection From(Company company)
    {
        ArgumentNullException.ThrowIfNull(company);

        var address = company.Address;
        return new CompanyProjection(
            company.Id,
            company.Guid,
            company.Name,
            address.Street,
            address.City,
            address.Country,
            address.Phone,
            address.Fax,
            address.Email,
            address.WebSite);
    }
}

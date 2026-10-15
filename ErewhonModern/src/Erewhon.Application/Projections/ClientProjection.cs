using Erewhon.Application.Domain;

namespace Erewhon.Application.Projections;

/// <summary>
/// Read model of the JADE <c>DTOClient</c> populated by <c>DTOClient.populateFromObject</c>: the client's row id,
/// source GUID, name and the seven fields of its address.
/// </summary>
/// <param name="Id">The row id of the client.</param>
/// <param name="Guid">The source GUID text of the client.</param>
/// <param name="Name">The client name.</param>
/// <param name="Street">The street of the client's address.</param>
/// <param name="City">The city of the client's address.</param>
/// <param name="Country">The country of the client's address.</param>
/// <param name="Phone">The phone number of the client's address.</param>
/// <param name="Fax">The fax number of the client's address.</param>
/// <param name="Email">The e-mail address of the client's address.</param>
/// <param name="WebSite">The web site of the client's address.</param>
public sealed record ClientProjection(
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
    /// Builds the projection from a client and its address, copying each value as stored.
    /// </summary>
    /// <param name="client">The client to project.</param>
    /// <returns>The projection of <paramref name="client"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public static ClientProjection From(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);

        var address = client.Address;
        return new ClientProjection(
            client.Id,
            client.Guid,
            client.Name,
            address.Street,
            address.City,
            address.Country,
            address.Phone,
            address.Fax,
            address.Email,
            address.WebSite);
    }
}

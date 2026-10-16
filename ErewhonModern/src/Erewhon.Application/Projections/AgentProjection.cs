using System.Text.Json.Serialization;
using Erewhon.Application.Domain;

namespace Erewhon.Application.Projections;

/// <summary>
/// Read model of the JADE <c>DTOAgent</c> populated by <c>DTOAgent.populateFromObject</c>: the agent's row id,
/// source GUID, name and the seven fields of its address.
/// </summary>
/// <param name="Id">The row id of the agent.</param>
/// <param name="Guid">The source GUID text of the agent; not written to JSON.</param>
/// <param name="Name">The agent name.</param>
/// <param name="Street">The street of the agent's address.</param>
/// <param name="City">The city of the agent's address.</param>
/// <param name="Country">The country of the agent's address.</param>
/// <param name="Phone">The phone number of the agent's address.</param>
/// <param name="Fax">The fax number of the agent's address.</param>
/// <param name="Email">The e-mail address of the agent's address.</param>
/// <param name="WebSite">The web site of the agent's address.</param>
public sealed record AgentProjection(
    long Id,
    [property: JsonIgnore] string Guid,
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
    /// Builds the projection from an agent and its address, copying each value as stored.
    /// </summary>
    /// <param name="agent">The agent to project.</param>
    /// <returns>The projection of <paramref name="agent"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is <see langword="null"/>.</exception>
    public static AgentProjection From(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var address = agent.Address;
        return new AgentProjection(
            agent.Id,
            agent.Guid,
            agent.Name,
            address.Street,
            address.City,
            address.Country,
            address.Phone,
            address.Fax,
            address.Email,
            address.WebSite);
    }
}

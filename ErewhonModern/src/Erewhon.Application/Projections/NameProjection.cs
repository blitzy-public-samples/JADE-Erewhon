using Erewhon.Application.Domain;

namespace Erewhon.Application.Projections;

/// <summary>
/// Read model of the JADE <c>DTOName</c> filled by <c>getAgentNames</c>/<c>getClientNames</c>: the row id and name of
/// an agent or client.
/// </summary>
/// <param name="Id">The row id of the agent or client.</param>
/// <param name="Name">The agent or client name.</param>
public sealed record NameProjection(long Id, string Name)
{
    /// <summary>
    /// Builds the projection from an agent, copying its row id and name as stored.
    /// </summary>
    /// <param name="agent">The agent to project.</param>
    /// <returns>The projection of <paramref name="agent"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is <see langword="null"/>.</exception>
    public static NameProjection From(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new NameProjection(agent.Id, agent.Name);
    }

    /// <summary>
    /// Builds the projection from a client, copying its row id and name as stored.
    /// </summary>
    /// <param name="client">The client to project.</param>
    /// <returns>The projection of <paramref name="client"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public static NameProjection From(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);

        return new NameProjection(client.Id, client.Name);
    }
}

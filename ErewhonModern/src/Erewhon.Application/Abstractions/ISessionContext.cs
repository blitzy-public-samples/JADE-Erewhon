namespace Erewhon.Application.Abstractions;

/// <summary>
/// The role of the user bound to the current session.
/// </summary>
public enum SessionRole
{
    /// <summary>
    /// No session is bound to the current scope, as for REST, SOAP, CLI and seed operations.
    /// </summary>
    None,

    /// <summary>
    /// A client logged on to the shop area.
    /// </summary>
    Client,

    /// <summary>
    /// An agent logged on to the admin area.
    /// </summary>
    Agent,

    /// <summary>
    /// The company user logged on to the admin area as Administrator, with no agent and no client.
    /// </summary>
    Administrator,
}

/// <summary>
/// The user session bound to the current scope: its id, role, agent and client.
/// Without a session, as for REST, SOAP, CLI and seed operations, <see cref="HasSession"/> is
/// <see langword="false"/>, <see cref="SessionId"/> is <see langword="null"/>, <see cref="Role"/> is
/// <see cref="SessionRole.None"/>, and <see cref="AgentId"/> and <see cref="ClientId"/> are
/// <see langword="null"/> unless set through <see cref="SetAgent"/> or <see cref="SetClient"/>.
/// </summary>
public interface ISessionContext
{
    /// <summary>
    /// <see langword="true"/> when a registered session is bound to the current scope; otherwise
    /// <see langword="false"/>.
    /// </summary>
    bool HasSession { get; }

    /// <summary>
    /// The id of the bound session, at most 64 characters, which is also the soft-lock owner value
    /// written to <c>locked_by_session</c>; <see langword="null"/> without a session.
    /// </summary>
    string? SessionId { get; }

    /// <summary>
    /// The role of the session user; <see cref="SessionRole.None"/> without a session.
    /// </summary>
    SessionRole Role { get; }

    /// <summary>
    /// The internal id (<c>agents.id</c>) of the session agent, or <see langword="null"/> when no agent is set.
    /// </summary>
    long? AgentId { get; }

    /// <summary>
    /// The internal id (<c>clients.id</c>) of the session client, or <see langword="null"/> when no client is set.
    /// </summary>
    long? ClientId { get; }

    /// <summary>
    /// <see langword="true"/> when both <see cref="AgentId"/> and <see cref="ClientId"/> are
    /// <see langword="null"/>, including when no session is bound; otherwise <see langword="false"/>.
    /// </summary>
    bool IsCompanyUser { get; }

    /// <summary>
    /// Sets the session agent to the agent with internal id <paramref name="agentId"/>, or clears it
    /// when <paramref name="agentId"/> is <see langword="null"/>.
    /// </summary>
    /// <param name="agentId">The internal id (<c>agents.id</c>) of the agent, or <see langword="null"/>.</param>
    void SetAgent(long? agentId);

    /// <summary>
    /// Sets the session client to the client with internal id <paramref name="clientId"/>, or clears it
    /// when <paramref name="clientId"/> is <see langword="null"/>.
    /// </summary>
    /// <param name="clientId">The internal id (<c>clients.id</c>) of the client, or <see langword="null"/>.</param>
    void SetClient(long? clientId);
}

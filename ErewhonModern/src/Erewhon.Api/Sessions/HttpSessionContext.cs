using Erewhon.Application.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Erewhon.Api.Sessions;

/// <summary>
/// The <see cref="ISessionContext"/> of one HTTP request: the <see cref="SessionEntry"/> that
/// <see cref="SessionMiddleware"/> resolved for the request and stored under
/// <see cref="SessionMiddleware.HttpContextItemKey"/>. Every member reads that entry live, so a change made through
/// <see cref="SetAgent"/> or <see cref="SetClient"/> is visible to the rest of the request and to later requests of
/// the session. Without a resolved entry, as for REST and SOAP requests, background services and the CLI, the context
/// has no session: <see cref="HasSession"/> is <see langword="false"/>, <see cref="SessionId"/>,
/// <see cref="AgentId"/> and <see cref="ClientId"/> are <see langword="null"/>, <see cref="Role"/> is
/// <see cref="SessionRole.None"/>, and <see cref="SetAgent"/> and <see cref="SetClient"/> change nothing.
/// </summary>
/// <param name="accessor">The accessor of the HTTP context of the current request.</param>
/// <param name="registry">The registry of logged-on sessions through which the agent and client are changed.</param>
public sealed class HttpSessionContext(IHttpContextAccessor accessor, SessionRegistry registry) : ISessionContext
{
    private readonly IHttpContextAccessor _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));

    private readonly SessionRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <inheritdoc />
    public bool HasSession => Entry is not null;

    /// <inheritdoc />
    public string? SessionId => Entry?.SessionId;

    /// <inheritdoc />
    public SessionRole Role => Entry?.Role ?? SessionRole.None;

    /// <inheritdoc />
    public long? AgentId => Entry?.AgentId;

    /// <inheritdoc />
    public long? ClientId => Entry?.ClientId;

    /// <inheritdoc />
    public bool IsCompanyUser => AgentId is null && ClientId is null;

    private SessionEntry? Entry => _accessor.HttpContext is { } context ? SessionMiddleware.GetSession(context) : null;

    /// <summary>
    /// Sets the agent of the resolved session through <see cref="SessionRegistry.SetAgent"/>, or clears it when
    /// <paramref name="agentId"/> is <see langword="null"/>, leaving its client unchanged. Does nothing when the
    /// request has no resolved session.
    /// </summary>
    /// <param name="agentId">The internal id (<c>agents.id</c>) of the agent, or <see langword="null"/>.</param>
    public void SetAgent(long? agentId)
    {
        if (Entry is { } entry)
        {
            _registry.SetAgent(entry.SessionId, agentId);
        }
    }

    /// <summary>
    /// Sets the client of the resolved session through <see cref="SessionRegistry.SetClient"/>, or clears it when
    /// <paramref name="clientId"/> is <see langword="null"/>, leaving its agent unchanged. Does nothing when the
    /// request has no resolved session.
    /// </summary>
    /// <param name="clientId">The internal id (<c>clients.id</c>) of the client, or <see langword="null"/>.</param>
    public void SetClient(long? clientId)
    {
        if (Entry is { } entry)
        {
            _registry.SetClient(entry.SessionId, clientId);
        }
    }
}

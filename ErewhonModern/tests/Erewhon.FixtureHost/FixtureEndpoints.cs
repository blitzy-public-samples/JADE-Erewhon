using System.Collections.Concurrent;
using System.Net.Mime;
using System.Text;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Services;
using Erewhon.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erewhon.FixtureHost;

/// <summary>
/// Maps the <c>/__fixtures</c> routes of the fixture bridge, which build and delete one <see cref="FixtureCatalog"/>
/// fixture company through <see cref="TestCompanyBuilder"/> and open sessions on it in the host's
/// <see cref="SessionRegistry"/>.
/// </summary>
public static class FixtureEndpoints
{
    private const string RoutePrefix = "/__fixtures";

    private const string LoggerCategory = "Erewhon.FixtureHost";

    /// <summary>
    /// Maps, on a route group excluded from every OpenAPI document:
    /// <list type="bullet">
    /// <item><description>
    /// <c>POST /__fixtures/{name}</c> with the optional query <c>session</c> (<c>client</c>, <c>agent</c> or
    /// <c>administrator</c>, any letter case; absent or empty gives the fixture's
    /// <see cref="FixtureDefinition.DefaultSession"/>). Builds the named fixture through <see cref="TestCompanyBuilder"/>
    /// in a new service scope, then opens a session on it with <see cref="SessionRegistry.Create"/>: a client session in
    /// the shop area for the fixture client, an agent session in the admin area for the fixture agent, or the
    /// administrator session in the admin area with no agent and no client and the company name as user code. Answers
    /// 201 with a <see cref="FixtureCreatedResponse"/> and the location <c>/__fixtures/{companyId}</c>; 404 text naming
    /// the unknown fixture and listing <see cref="FixtureCatalog.Names"/>; 400 text listing the accepted values for any
    /// other <c>session</c> value, before anything is built; 400 text, after deleting the built company, when the
    /// fixture has no client or agent for the requested session; 500 text with the exception message, after deleting
    /// the company the build had created, when the build fails.
    /// </description></item>
    /// <item><description>
    /// <c>DELETE /__fixtures/{companyId}</c>. Removes every session the POST route opened on that company with
    /// <see cref="SessionRegistry.Remove"/>, then deletes the company and everything it owns through
    /// <see cref="TestCompanyBuilder.DeleteCompanyAsync"/>. Answers 204, also when no company has that id; 500 text with
    /// the exception message when the delete fails.
    /// </description></item>
    /// </list>
    /// Build and delete failures are logged at Error level under the category <c>Erewhon.FixtureHost</c>. Both routes
    /// must be mapped before the application starts.
    /// </summary>
    /// <param name="app">The built, unstarted Erewhon web application.</param>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static void MapFixtureEndpoints(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var bridge = new FixtureBridge(
            app.Services.GetRequiredService<IServiceScopeFactory>(),
            app.Services.GetRequiredService<SessionRegistry>(),
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory));

        RouteGroupBuilder group = app.MapGroup(RoutePrefix).ExcludeFromDescription();
        group.MapPost(
            "/{name}",
            (string name, string? session, CancellationToken ct) => bridge.CreateAsync(name, session, ct));
        group.MapDelete(
            "/{companyId:long}",
            (long companyId) => bridge.DeleteAsync(companyId));
    }

    /// <summary>
    /// Builds and deletes fixture companies and tracks, per company id, the session ids opened on each company.
    /// Every member is safe to call from several threads at once.
    /// </summary>
    private sealed class FixtureBridge(
        IServiceScopeFactory scopeFactory,
        SessionRegistry registry,
        ILogger logger)
    {
        private static readonly IReadOnlyDictionary<string, SessionRole> SessionValues =
            new Dictionary<string, SessionRole>(StringComparer.OrdinalIgnoreCase)
            {
                ["client"] = SessionRole.Client,
                ["agent"] = SessionRole.Agent,
                ["administrator"] = SessionRole.Administrator,
            };

        private readonly ConcurrentDictionary<long, ConcurrentBag<string>> _sessionsByCompany = new();

        /// <summary>Handles <c>POST /__fixtures/{name}</c> as <see cref="MapFixtureEndpoints"/> describes.</summary>
        /// <param name="name">The fixture name, compared ordinally.</param>
        /// <param name="session">The requested session role, or <see langword="null"/> or empty for the default.</param>
        /// <param name="ct">The request-aborted token, which cancels the build.</param>
        /// <returns>The 201, 400, 404 or 500 result.</returns>
        public async Task<IResult> CreateAsync(string name, string? session, CancellationToken ct)
        {
            if (!FixtureCatalog.TryGet(name, out FixtureDefinition? definition))
            {
                return PlainText(
                    StatusCodes.Status404NotFound,
                    $"Unknown fixture '{name}'. Known fixtures: {string.Join(", ", FixtureCatalog.Names)}.");
            }

            if (!TryResolveRole(session, definition.DefaultSession, out SessionRole role))
            {
                return PlainText(
                    StatusCodes.Status400BadRequest,
                    $"Unknown session '{session}'. Expected one of: {string.Join(", ", SessionValues.Keys)}.");
            }

            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            var builder = new TestCompanyBuilder(scope.ServiceProvider);

            FixtureBuildResult result;
            try
            {
                result = await definition.BuildAsync(builder, scope.ServiceProvider, ct);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Fixture {FixtureName} could not be built.", definition.Name);
                await DeleteBuiltCompanyAsync(builder.MyCompany?.Id, definition.Name);
                return PlainText(StatusCodes.Status500InternalServerError, exception.Message);
            }

            SessionEntry? entry = OpenSession(role, result);
            if (entry is null)
            {
                await DeleteBuiltCompanyAsync(result.CompanyId, definition.Name);
                return PlainText(
                    StatusCodes.Status400BadRequest,
                    $"Fixture '{definition.Name}' has no {role.ToString().ToLowerInvariant()} for the requested session.");
            }

            _sessionsByCompany.GetOrAdd(result.CompanyId, _ => new ConcurrentBag<string>()).Add(entry.SessionId);

            return Results.Created(
                $"{RoutePrefix}/{result.CompanyId}",
                new FixtureCreatedResponse(result.CompanyId, result.Ids, entry.SessionId));
        }

        /// <summary>Handles <c>DELETE /__fixtures/{companyId}</c> as <see cref="MapFixtureEndpoints"/> describes.</summary>
        /// <param name="companyId">The internal id (<c>companies.id</c>) of the fixture company.</param>
        /// <returns>The 204 or 500 result.</returns>
        public async Task<IResult> DeleteAsync(long companyId)
        {
            try
            {
                if (_sessionsByCompany.TryRemove(companyId, out ConcurrentBag<string>? sessionIds))
                {
                    foreach (string sessionId in sessionIds)
                    {
                        registry.Remove(sessionId);
                    }
                }

                await DeleteCompanyAsync(companyId);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Fixture company {CompanyId} could not be deleted.", companyId);
                return PlainText(StatusCodes.Status500InternalServerError, exception.Message);
            }

            return Results.NoContent();
        }

        /// <summary>
        /// Resolves the session role from the <c>session</c> query value: <paramref name="defaultRole"/> when the value is
        /// <see langword="null"/> or empty, otherwise the <see cref="SessionValues"/> entry, compared case-insensitively.
        /// </summary>
        private static bool TryResolveRole(string? session, SessionRole defaultRole, out SessionRole role)
        {
            if (string.IsNullOrEmpty(session))
            {
                role = defaultRole;
                return true;
            }

            return SessionValues.TryGetValue(session, out role);
        }

        /// <summary>
        /// Opens the session of <paramref name="role"/> on the built fixture, or returns <see langword="null"/> when the
        /// fixture has no client for a client session or no agent for an agent session, or the role is
        /// <see cref="SessionRole.None"/>.
        /// </summary>
        private SessionEntry? OpenSession(SessionRole role, FixtureBuildResult result) => role switch
        {
            SessionRole.Client when result is { ClientId: { } clientId, ClientName: { } clientName } =>
                registry.Create(LogonArea.Shop, SessionRole.Client, null, clientId, clientName),
            SessionRole.Agent when result is { AgentId: { } agentId, AgentName: { } agentName } =>
                registry.Create(LogonArea.Admin, SessionRole.Agent, agentId, null, agentName),
            SessionRole.Administrator =>
                registry.Create(LogonArea.Admin, SessionRole.Administrator, null, null, result.CompanyName),
            _ => null,
        };

        /// <summary>
        /// Deletes the company a fixture build created, when it created one; a failure of the delete is logged at Error
        /// level and not rethrown.
        /// </summary>
        private async Task DeleteBuiltCompanyAsync(long? companyId, string fixtureName)
        {
            if (companyId is not { } id)
            {
                return;
            }

            try
            {
                await DeleteCompanyAsync(id);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Company {CompanyId} of fixture {FixtureName} could not be deleted after the request failed.",
                    id,
                    fixtureName);
            }
        }

        /// <summary>
        /// Deletes the company with everything it owns through <see cref="TestCompanyBuilder.DeleteCompanyAsync"/> in a
        /// new service scope, without a cancellation token.
        /// </summary>
        private async Task DeleteCompanyAsync(long companyId)
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            await new TestCompanyBuilder(scope.ServiceProvider).DeleteCompanyAsync(companyId, CancellationToken.None);
        }

        /// <summary>Returns a <c>text/plain; charset=utf-8</c> result with the status code and body.</summary>
        private static IResult PlainText(int statusCode, string body) =>
            Results.Text(body, MediaTypeNames.Text.Plain, Encoding.UTF8, statusCode);
    }
}

/// <summary>
/// The 201 body of <c>POST /__fixtures/{name}</c>, serialised as
/// <c>{ "companyId": …, "ids": { "&lt;key&gt;": …, … }, "sessionId": "…" }</c>.
/// </summary>
/// <param name="CompanyId">The internal id (<c>companies.id</c>) of the fixture company.</param>
/// <param name="Ids">
/// The internal id of each row the fixture created, keyed by a <see cref="FixtureCatalog.Keys"/> constant.
/// </param>
/// <param name="SessionId">
/// The id of the session opened on the fixture, accepted as the <c>X-Erewhon-Session</c> header and as the hub's
/// <c>session</c> query value until <c>DELETE /__fixtures/{companyId}</c> removes it.
/// </param>
public sealed record FixtureCreatedResponse(long CompanyId, IReadOnlyDictionary<string, long> Ids, string SessionId);

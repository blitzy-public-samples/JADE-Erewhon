using System.Globalization;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Messages;
using Erewhon.Application.Projections;

namespace Erewhon.Application.Services;

/// <summary>
/// The area of the application a user logs on to.
/// </summary>
public enum LogonArea
{
    /// <summary>
    /// The shop area, where a client logs on.
    /// </summary>
    Shop,

    /// <summary>
    /// The admin area, where an agent or the company user (Administrator) logs on.
    /// </summary>
    Admin,
}

/// <summary>
/// What the logon screen of an area offers. Ports the state <c>LogonForm.formLoad</c> sets up.
/// </summary>
/// <param name="DataLoaded">
/// <see langword="true"/> when the bound company exists; <see langword="false"/> when no company exists, in which case
/// the load-data prompt is shown instead of the logon screen.
/// </param>
/// <param name="UserOptionCaption">
/// The caption of the user option: <c>$Client</c> ("Client") in the shop area, <c>$Agent</c> ("Agent") in the admin area.
/// </param>
/// <param name="Users">
/// The users that can be selected: the company's clients in name order in the shop area, its agents in name order in
/// the admin area; empty when <paramref name="DataLoaded"/> is <see langword="false"/>.
/// </param>
/// <param name="AdministratorEnabled">
/// <see langword="true"/> when the Administrator option can be chosen: in the admin area with a company present.
/// </param>
/// <param name="AdministratorSelected">
/// <see langword="true"/> when the Administrator option is selected when the screen opens: in the admin area with a
/// company present.
/// </param>
public sealed record LogonOptions(
    bool DataLoaded,
    string UserOptionCaption,
    IReadOnlyList<NameProjection> Users,
    bool AdministratorEnabled,
    bool AdministratorSelected);

/// <summary>
/// A logon attempt from the logon screen of an area.
/// </summary>
/// <param name="Area">The area logged on to.</param>
/// <param name="Administrator">
/// <see langword="true"/> when the Administrator option is selected; read in the admin area only.
/// </param>
/// <param name="UserName">The selected user name, as entered; trimmed of leading and trailing spaces by the logon.</param>
public sealed record LogonRequest(LogonArea Area, bool Administrator, string? UserName);

/// <summary>
/// The outcome of a logon attempt.
/// </summary>
/// <param name="Succeeded"><see langword="true"/> when the user code identifies a valid user of the area.</param>
/// <param name="Role">
/// The role of the user: <see cref="SessionRole.Client"/>, <see cref="SessionRole.Agent"/> or
/// <see cref="SessionRole.Administrator"/>; <see cref="SessionRole.None"/> when the logon failed.
/// </param>
/// <param name="AgentId">The internal id (<c>agents.id</c>) of the agent who logged on, otherwise <see langword="null"/>.</param>
/// <param name="ClientId">The internal id (<c>clients.id</c>) of the client who logged on, otherwise <see langword="null"/>.</param>
/// <param name="UserCode">
/// The user code the logon validated: the company name for the Administrator option, otherwise the user name trimmed
/// of leading and trailing spaces.
/// </param>
/// <param name="ErrorTitle">The title of the message shown when the logon failed (<c>$Error</c>), otherwise <see langword="null"/>.</param>
/// <param name="ErrorText">
/// The text of the message shown when the logon failed, <c>200011: </c> followed by <c>$UnknownErrorNumber</c>;
/// otherwise <see langword="null"/>.
/// </param>
public sealed record LogonResult(
    bool Succeeded,
    SessionRole Role,
    long? AgentId,
    long? ClientId,
    string? UserCode,
    string? ErrorTitle,
    string? ErrorText);

/// <summary>
/// Offers the logon choices of the shop and admin areas and validates a selected user against the bound company.
/// Ports <c>LogonForm.formLoad</c> and <c>LogonForm.doValidate</c>, <c>ErewhonViewSchemaGlobal.isUserValid</c>, and
/// the tender closure at the server date that <c>AdminMainParentForm.formLoad</c> runs when the admin area opens.
/// </summary>
/// <remarks>
/// The service identifies the selected user only: it checks no credentials and creates no session. One instance
/// serves one dependency-injection scope; its members are not safe to call from several threads at once.
/// </remarks>
/// <param name="companyContext">The company bound to the scope.</param>
/// <param name="clock">The database server clock that gives the closure date.</param>
/// <param name="agents">The agent lookups of the admin area.</param>
/// <param name="clients">The client lookups of the shop area.</param>
/// <param name="closure">The tender closure run after each successful admin-area logon.</param>
public sealed class LogonService(
    ICompanyContext companyContext,
    IServerClock clock,
    AgentService agents,
    ClientService clients,
    TenderClosureService closure)
{
    private const int UserNameIsInvalid = 200011;

    private readonly ICompanyContext _companyContext =
        companyContext ?? throw new ArgumentNullException(nameof(companyContext));

    private readonly IServerClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly AgentService _agents = agents ?? throw new ArgumentNullException(nameof(agents));

    private readonly ClientService _clients = clients ?? throw new ArgumentNullException(nameof(clients));

    private readonly TenderClosureService _closure = closure ?? throw new ArgumentNullException(nameof(closure));

    /// <summary>
    /// Returns what the logon screen of <paramref name="area"/> offers. With no company,
    /// <see cref="LogonOptions.DataLoaded"/> is <see langword="false"/>, no users are listed and both Administrator
    /// flags are <see langword="false"/>. In the shop area the user option is captioned "Client", the company's clients
    /// are listed in name order and the Administrator option is disabled and not selected. In the admin area the user
    /// option is captioned "Agent", the company's agents are listed in name order and the Administrator option is
    /// enabled and selected.
    /// </summary>
    /// <param name="area">The area whose logon screen is shown.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>The logon options of the area.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="area"/> is not a defined <see cref="LogonArea"/>.</exception>
    /// <exception cref="NoCompanyException">The bound company was deleted while its users were being read.</exception>
    public async Task<LogonOptions> GetLogonOptionsAsync(LogonArea area, CancellationToken ct = default)
    {
        EnsureDefined(area, nameof(area));

        string caption = MessageCatalog.Get(area == LogonArea.Shop ? MessageKeys.Client : MessageKeys.Agent);

        var company = await _companyContext.FindCurrentAsync(ct);
        if (company is null)
        {
            return new LogonOptions(
                DataLoaded: false,
                UserOptionCaption: caption,
                Users: Array.Empty<NameProjection>(),
                AdministratorEnabled: false,
                AdministratorSelected: false);
        }

        if (area == LogonArea.Shop)
        {
            IReadOnlyList<NameProjection> clientNames = await _clients.GetClientNamesAsync(ct);
            return new LogonOptions(
                DataLoaded: true,
                UserOptionCaption: caption,
                Users: clientNames,
                AdministratorEnabled: false,
                AdministratorSelected: false);
        }

        IReadOnlyList<NameProjection> agentNames = await _agents.GetAgentNamesAsync(ct);
        return new LogonOptions(
            DataLoaded: true,
            UserOptionCaption: caption,
            Users: agentNames,
            AdministratorEnabled: true,
            AdministratorSelected: true);
    }

    /// <summary>
    /// Validates the user selected on the logon screen of <paramref name="request"/>'s area.
    /// <para>
    /// The user code is the bound company's name when the admin area's Administrator option is selected, and otherwise
    /// <see cref="LogonRequest.UserName"/> trimmed of leading and trailing spaces; the shop area ignores
    /// <see cref="LogonRequest.Administrator"/>. An empty user code raises <see cref="ErewhonException"/> 500006
    /// (<see cref="ErewhonException.UserNotProvided"/>) before the user is looked up.
    /// </para>
    /// <para>
    /// In the shop area the user code must equal the name of one of the company's clients exactly; the logon is then
    /// the client's. In the admin area a user code equal to the company name, compared ordinally, logs on the company
    /// user as Administrator, with no agent and no client, whichever option named it; any other user code must equal
    /// the name of one of the company's agents exactly, and the logon is then the agent's. A user code that names no
    /// such user gives a failed result titled "Error" with the text <c>200011: </c> followed by
    /// <c>$UnknownErrorNumber</c>.
    /// </para>
    /// <para>
    /// After every successful admin-area logon, the company's tenders due at the database server's current date are
    /// closed before the result is returned. The session context is not changed.
    /// </para>
    /// </summary>
    /// <param name="request">The area, the Administrator option and the selected user name.</param>
    /// <param name="ct">Token that cancels the reads and the tender closure.</param>
    /// <returns>The logon outcome, carrying the user code that was validated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="LogonRequest.Area"/> is not a defined <see cref="LogonArea"/>.</exception>
    /// <exception cref="ErewhonException">The user code is empty (error code 500006).</exception>
    /// <exception cref="NoCompanyException">The bound company does not exist when it is read.</exception>
    /// <exception cref="ErewhonLockException">
    /// The admin-area tender closure did not obtain a tender item's row lock; the awards committed before it stay.
    /// </exception>
    /// <exception cref="ErewhonDeadlockException">
    /// The database reported a deadlock during the admin-area tender closure; the awards committed before it stay.
    /// </exception>
    public async Task<LogonResult> LogonAsync(LogonRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureDefined(request.Area, nameof(request));

        string userCode;
        if (request.Area == LogonArea.Admin && request.Administrator)
        {
            var company = await _companyContext.GetCurrentAsync(ct);
            userCode = company.Name;
        }
        else
        {
            userCode = JadeValues.TrimBlanks(request.UserName);
        }

        if (JadeValues.IsNull(userCode))
        {
            throw new ErewhonException(ErewhonException.UserNotProvided);
        }

        LogonResult result = request.Area == LogonArea.Shop
            ? await LogonClientAsync(userCode, ct)
            : await LogonAdminUserAsync(userCode, ct);

        if (result.Succeeded && request.Area == LogonArea.Admin)
        {
            DateOnly serverDate = await _clock.GetServerDateAsync(ct);
            await _closure.CloseTendersAtDateAsync(serverDate, ct);
        }

        return result;
    }

    private async Task<LogonResult> LogonClientAsync(string userCode, CancellationToken ct)
    {
        var client = await _clients.FindByNameAsync(userCode, ct);
        if (client is null)
        {
            return Rejected(userCode);
        }

        return new LogonResult(
            Succeeded: true,
            Role: SessionRole.Client,
            AgentId: null,
            ClientId: client.Id,
            UserCode: userCode,
            ErrorTitle: null,
            ErrorText: null);
    }

    private async Task<LogonResult> LogonAdminUserAsync(string userCode, CancellationToken ct)
    {
        var company = await _companyContext.GetCurrentAsync(ct);
        if (string.Equals(userCode, company.Name, StringComparison.Ordinal))
        {
            return new LogonResult(
                Succeeded: true,
                Role: SessionRole.Administrator,
                AgentId: null,
                ClientId: null,
                UserCode: userCode,
                ErrorTitle: null,
                ErrorText: null);
        }

        var agent = await _agents.FindByNameAsync(userCode, ct);
        if (agent is null)
        {
            return Rejected(userCode);
        }

        return new LogonResult(
            Succeeded: true,
            Role: SessionRole.Agent,
            AgentId: agent.Id,
            ClientId: null,
            UserCode: userCode,
            ErrorTitle: null,
            ErrorText: null);
    }

    private static LogonResult Rejected(string userCode) =>
        new(
            Succeeded: false,
            Role: SessionRole.None,
            AgentId: null,
            ClientId: null,
            UserCode: userCode,
            ErrorTitle: MessageCatalog.Get(MessageKeys.Error),
            ErrorText: UserNameIsInvalid.ToString(CultureInfo.InvariantCulture)
                + ": "
                + MessageCatalog.Get(MessageKeys.UnknownErrorNumber));

    private static void EnsureDefined(LogonArea area, string paramName)
    {
        if (!Enum.IsDefined(area))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                area,
                $"{area} is not a logon area; expected {LogonArea.Shop} or {LogonArea.Admin}.");
        }
    }
}

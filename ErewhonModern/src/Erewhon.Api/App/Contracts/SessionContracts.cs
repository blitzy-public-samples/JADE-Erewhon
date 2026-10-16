using Erewhon.Application.Abstractions;
using Erewhon.Application.Projections;
using Erewhon.Application.Services;

namespace Erewhon.Api.App.Contracts;

/// <summary>
/// Response of <c>GET /api/app/logon-options</c>: what the logon screen of an area shows when it opens, the state
/// <c>LogonForm.formLoad</c> sets up for the shop area (also <c>WebLogonForm</c>) and for the admin area.
/// </summary>
/// <param name="DataLoaded">
/// <see langword="true"/> when a company exists; <see langword="false"/> when none exists, in which case the load-data
/// prompt (<c>$DataHasNotBeenLoaded</c> titled <c>$PleaseLoadDataFiles</c>) is shown instead of the logon screen.
/// </param>
/// <param name="Area">The area whose logon screen is shown: <see cref="LogonArea.Shop"/> or <see cref="LogonArea.Admin"/>.</param>
/// <param name="UserOptionCaption">
/// The caption of the <c>optUser</c> option: the <c>$Client</c> text ("Client") in the shop area, the <c>$Agent</c>
/// text ("Agent") in the admin area.
/// </param>
/// <param name="AdministratorEnabled">
/// The <c>optAdmin.enabled</c> state: <see langword="false"/> in the shop area, <see langword="true"/> in the admin
/// area with a company present.
/// </param>
/// <param name="AdministratorSelected">
/// The initial <c>optAdmin.value</c>: <see langword="true"/> in the admin area with a company present, in which case
/// <c>cmbUser</c> starts disabled; <see langword="false"/> in the shop area, where <c>optUser</c> is selected.
/// </param>
/// <param name="Users">
/// The <c>cmbUser</c> entries, each the row id and name of a user: the company's clients in name order in the shop
/// area, its agents in name order in the admin area; empty when <paramref name="DataLoaded"/> is
/// <see langword="false"/>.
/// </param>
/// <param name="Version">
/// The application version string shown in <c>lblVersion</c> after the <c>$BaseVersion</c> text and a space ("2.0").
/// </param>
public sealed record LogonOptionsResponse(
    bool DataLoaded,
    LogonArea Area,
    string UserOptionCaption,
    bool AdministratorEnabled,
    bool AdministratorSelected,
    IReadOnlyList<NameProjection> Users,
    string Version);

/// <summary>
/// Request of <c>POST /api/app/sessions</c>: the choices made on the logon screen of an area when OK is pressed,
/// the values <c>LogonForm.doValidate</c> reads.
/// </summary>
/// <param name="Area">The area logged on to: <see cref="LogonArea.Shop"/> or <see cref="LogonArea.Admin"/>.</param>
/// <param name="Administrator">
/// The <c>optAdmin.value</c>: <see langword="true"/> when the Administrator option is selected, which makes the
/// company name the user code; read in the admin area only.
/// </param>
/// <param name="UserName">
/// The <c>cmbUser.text</c> of the selected user, as entered and untrimmed; <see langword="null"/> or empty when no user
/// is selected. The logon trims leading and trailing spaces from it.
/// </param>
public sealed record CreateSessionRequest(LogonArea Area, bool Administrator, string? UserName);

/// <summary>
/// Response of <c>POST /api/app/sessions</c>: the outcome of validating the selected user, as
/// <c>ErewhonViewSchemaGlobal.isUserValid</c> decides it, and on success the session opened for that user.
/// </summary>
/// <param name="Succeeded">
/// <see langword="true"/> when the user code identifies a valid user of the area and a session was opened;
/// otherwise <see langword="false"/>.
/// </param>
/// <param name="SessionId">
/// The id of the opened session, sent in the <c>X-Erewhon-Session</c> header of every later app request and as the
/// <c>session</c> query value of the hub connection; <see langword="null"/> when <paramref name="Succeeded"/> is
/// <see langword="false"/>.
/// </param>
/// <param name="Area">The area logged on to: <see cref="LogonArea.Shop"/> or <see cref="LogonArea.Admin"/>.</param>
/// <param name="Role">
/// The role of the session user: <see cref="SessionRole.Client"/>, <see cref="SessionRole.Agent"/> or
/// <see cref="SessionRole.Administrator"/>; <see cref="SessionRole.None"/> when <paramref name="Succeeded"/> is
/// <see langword="false"/>.
/// </param>
/// <param name="AgentId">
/// The row id of the session agent (<c>app.myAgent</c>); <see langword="null"/> for a client, for the company user and
/// when <paramref name="Succeeded"/> is <see langword="false"/>.
/// </param>
/// <param name="ClientId">
/// The row id of the session client (<c>app.myClient</c>); <see langword="null"/> for an agent, for the company user
/// and when <paramref name="Succeeded"/> is <see langword="false"/>.
/// </param>
/// <param name="UserName">
/// The user code that was validated: the company name for the Administrator option, otherwise the selected user name
/// trimmed of leading and trailing spaces.
/// </param>
/// <param name="MessageTitle">
/// The title of the message box shown for a rejected user (<c>$Error</c>); <see langword="null"/> when
/// <paramref name="Succeeded"/> is <see langword="true"/>.
/// </param>
/// <param name="Message">
/// The text of the message box shown for a rejected user, <c>200011: </c> followed by <c>$UnknownErrorNumber</c>;
/// <see langword="null"/> when <paramref name="Succeeded"/> is <see langword="true"/>.
/// </param>
public sealed record CreateSessionResponse(
    bool Succeeded,
    string? SessionId,
    LogonArea Area,
    SessionRole Role,
    long? AgentId,
    long? ClientId,
    string? UserName,
    string? MessageTitle,
    string? Message);

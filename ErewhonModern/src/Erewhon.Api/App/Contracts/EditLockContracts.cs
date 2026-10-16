namespace Erewhon.Api.App.Contracts;

/// <summary>
/// Result of <c>POST /api/app/edit-locks/{type}/{id}/save-request</c>, the save request that replaces the Yes answer
/// of the <c>$ThingCurrentlyEdited</c> prompt titled <c>$ThingAlreadyBeingEdited</c> ("already being edited"), shown by
/// <c>ErewhonBaseForm.displayObject</c> when an editor opens a row whose soft edit lock is set.
/// </summary>
/// <param name="Sent">
/// <see langword="true"/> when the <c>SaveOutstandingChanges</c> event was delivered to the session holding the row's
/// soft edit lock, the caller's own session included; <see langword="false"/> when the row holds no soft edit lock and
/// nothing was sent.
/// </param>
public sealed record SaveRequestResponse(bool Sent);

namespace Erewhon.Api.App.Contracts;

/// <summary>
/// Response of <c>GET /api/app/about</c>: the version data shown by the About dialog, replacing the
/// <c>AboutForm.lblVersion</c> caption.
/// </summary>
/// <param name="Version">The application version string, major and minor version joined by a full stop ("2.0").</param>
public sealed record AboutResponse(string Version);

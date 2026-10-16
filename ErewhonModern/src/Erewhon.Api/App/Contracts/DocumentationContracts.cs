namespace Erewhon.Api.App.Contracts;

/// <summary>
/// Response of <c>GET /api/app/admin/documentation/{type}/{id}</c>: the documentation of one agent or region chosen
/// from the <c>DocumentorSetupForm</c> candidate list, replacing the two views of
/// <c>DisplaySelfDocumentorDetails</c>, "Document as Text" (<c>txtDetails</c>) and "Document as RTF"
/// (<c>rtfDetails</c>).
/// </summary>
/// <param name="EntityType">The documented class, <c>Agent</c> or <c>Region</c>.</param>
/// <param name="Id">The row id of the agent or region.</param>
/// <param name="Name">
/// The agent or region name: the heading of <c>rtf</c> and the file name <c>&lt;name&gt;.rtf</c> of the RTF download.
/// </param>
/// <param name="Text">
/// The "Document as Text" content (<c>txtDetails</c>), the entity's developer description: <c>&lt;oid&gt; Agent</c>
/// for an agent, or <c>&lt;oid&gt; Region: &lt;name&gt; of &lt;country&gt;</c> for a region.
/// </param>
/// <param name="Rtf">
/// The "Document as RTF" content (<c>rtfDetails</c>), the Rich Text Format source built with <c>name</c> as heading
/// and <c>text</c> as body.
/// </param>
public sealed record DocumentationDetailsResponse(string EntityType, long Id, string Name, string Text, string Rtf);

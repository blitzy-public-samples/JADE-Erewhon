using System.Text;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Erewhon.Application.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erewhon.Api.App;

/// <summary>
/// Maps the self-documentation endpoints, the back end of the documentation candidate list and the documentation
/// details dialog: the list of agents and regions that can be documented, the text and RTF documentation of one of
/// them, and that RTF document as a file download.
/// </summary>
public static class DocumentationEndpoints
{
    private const string Tag = "App: Admin documentation";

    private const string RtfContentType = "text/rtf";

    private const string RtfExtension = ".rtf";

    private const string AttrCharSymbols = "!#$&+-.^_`|~";

    private const string HexDigits = "0123456789ABCDEF";

    /// <summary>
    /// Maps these routes on the sub-group <c>/admin/documentation</c> of <paramref name="api"/>, all served by
    /// <see cref="SelfDocumentationService"/>:
    /// <list type="bullet">
    /// <item><description>
    /// <c>GET /admin/documentation/candidates</c>: 200 with the <see cref="DocumentationCandidate"/> list of the bound
    /// company, unchanged: the heading <c>-- Agents --</c>, each agent by name, the heading <c>-- Regions --</c>, then
    /// each region of each country as <c>&lt;country&gt; &lt;region&gt;</c>. It has no 404 response; with no bound
    /// company the request fails with the 500 <see cref="ErrorOutcome"/>.
    /// </description></item>
    /// <item><description>
    /// <c>GET /admin/documentation/{type}/{id}</c>: 200 with the <see cref="DocumentationDetailsResponse"/> of the
    /// agent or region with row id <c>id</c>; 404 with an empty body when <c>type</c> is neither <c>Agent</c> nor
    /// <c>Region</c> (compared case-insensitively), no company is bound, or the bound company has no such row.
    /// </description></item>
    /// <item><description>
    /// <c>GET /admin/documentation/{type}/{id}/rtf</c>: 200 with the RTF document as its Latin-1 bytes, content type
    /// <c>text/rtf</c> and <c>Content-Disposition: attachment; filename="&lt;name&gt;.rtf"</c>, where a backslash or
    /// quotation mark in the name is escaped with a backslash and every character outside printable ASCII becomes
    /// <c>_</c>; when a character was replaced, <c>; filename*=UTF-8''&lt;percent-encoded name&gt;.rtf</c> follows.
    /// Its 200 response is described as a binary <c>text/rtf</c> body. 404 with an empty body in the same cases as
    /// the details route.
    /// </description></item>
    /// </list>
    /// Every route also declares the 401 of a request without a registered session and the 500
    /// <see cref="ErrorOutcome"/>.
    /// </summary>
    /// <param name="api">The <c>/api/app</c> route group.</param>
    /// <returns><paramref name="api"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is <see langword="null"/>.</exception>
    public static RouteGroupBuilder MapDocumentationEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RouteGroupBuilder documentation = api.MapGroup("/admin/documentation");

        documentation.MapGet("/candidates", GetCandidatesAsync)
            .WithName("AppAdminDocumentationGetCandidates")
            .WithTags(Tag)
            .WithSummary("Lists the agents and regions that can be documented, each group under its heading.")
            .Produces<IReadOnlyList<DocumentationCandidate>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        documentation.MapGet("/{type}/{id:long}", GetDetailsAsync)
            .WithName("AppAdminDocumentationGetDetails")
            .WithTags(Tag)
            .WithSummary("Returns the text and RTF documentation of one agent or region.")
            .Produces<DocumentationDetailsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        documentation.MapGet("/{type}/{id:long}/rtf", DownloadRtfAsync)
            .WithName("AppAdminDocumentationDownloadRtf")
            .WithTags(Tag)
            .WithSummary("Downloads the RTF documentation of one agent or region as <name>.rtf.")
            .Produces(StatusCodes.Status200OK, typeof(Stream), RtfContentType)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        return api;
    }

    private static async Task<Ok<IReadOnlyList<DocumentationCandidate>>> GetCandidatesAsync(
        [FromServices] SelfDocumentationService service,
        CancellationToken ct) =>
        TypedResults.Ok(await service.GetCandidatesAsync(ct));

    private static async Task<Results<Ok<DocumentationDetailsResponse>, NotFound>> GetDetailsAsync(
        string type,
        long id,
        [FromServices] SelfDocumentationService service,
        CancellationToken ct)
    {
        DocumentationDetails? details = await service.GetDetailsAsync(type, id, ct);
        if (details is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new DocumentationDetailsResponse(
            details.EntityType,
            details.Id,
            details.Name,
            details.Text,
            details.Rtf));
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> DownloadRtfAsync(
        string type,
        long id,
        HttpContext httpContext,
        [FromServices] SelfDocumentationService service,
        CancellationToken ct)
    {
        DocumentationDetails? details = await service.GetDetailsAsync(type, id, ct);
        if (details is null)
        {
            return TypedResults.NotFound();
        }

        httpContext.Response.Headers.ContentDisposition = FormatAttachmentDisposition(details.Name + RtfExtension);
        return TypedResults.Bytes(Encoding.Latin1.GetBytes(details.Rtf), RtfContentType);
    }

    /// <summary>
    /// Returns <c>attachment; filename="&lt;quoted&gt;"</c> for <paramref name="fileName"/>, where the quoted name
    /// escapes <c>\</c> and <c>"</c> with a backslash and replaces each character (Unicode scalar value) outside
    /// 0x20–0x7E with <c>_</c>. When any character was replaced, appends
    /// <c>; filename*=UTF-8''&lt;percent-encoded file name&gt;</c>. The result holds printable ASCII only.
    /// </summary>
    private static string FormatAttachmentDisposition(string fileName)
    {
        var quoted = new StringBuilder(fileName.Length);
        bool replaced = false;

        foreach (Rune rune in fileName.EnumerateRunes())
        {
            int value = rune.Value;
            if (value == '\\' || value == '"')
            {
                quoted.Append('\\').Append((char)value);
            }
            else if (value is >= 0x20 and <= 0x7E)
            {
                quoted.Append((char)value);
            }
            else
            {
                quoted.Append('_');
                replaced = true;
            }
        }

        string disposition = "attachment; filename=\"" + quoted + "\"";
        return replaced
            ? disposition + "; filename*=UTF-8''" + PercentEncodeUtf8(fileName)
            : disposition;
    }

    /// <summary>
    /// Returns the UTF-8 bytes of <paramref name="value"/> with each byte that is not an ASCII letter, digit or one
    /// of <c>!#$&amp;+-.^_`|~</c> written as <c>%</c> and two upper-case hexadecimal digits.
    /// </summary>
    private static string PercentEncodeUtf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        var encoded = new StringBuilder(bytes.Length * 3);

        foreach (byte octet in bytes)
        {
            char character = (char)octet;
            if (char.IsAsciiLetterOrDigit(character) || AttrCharSymbols.Contains(character, StringComparison.Ordinal))
            {
                encoded.Append(character);
            }
            else
            {
                encoded.Append('%').Append(HexDigits[octet >> 4]).Append(HexDigits[octet & 0x0F]);
            }
        }

        return encoded.ToString();
    }
}

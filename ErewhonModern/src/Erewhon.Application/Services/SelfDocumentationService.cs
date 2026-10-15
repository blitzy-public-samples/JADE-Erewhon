using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Services;

/// <summary>
/// One row of the self-documentation candidate list: a heading at level 1 that names no entity, or an agent or
/// region entry at level 2.
/// </summary>
/// <param name="Text">
/// The row text: the heading <c>-- Agents --</c> or <c>-- Regions --</c>, an agent name, or the country name, a space
/// and the region name.
/// </param>
/// <param name="Level">1 for a heading, 2 for an agent or region entry.</param>
/// <param name="EntityType"><c>Agent</c> or <c>Region</c> for an entry; <see langword="null"/> for a heading.</param>
/// <param name="Id">The row id of the agent or region; <see langword="null"/> for a heading.</param>
public sealed record DocumentationCandidate(string Text, int Level, string? EntityType, long? Id);

/// <summary>
/// The self-documentation of one agent or region: its plain text and its Rich Text Format document.
/// </summary>
/// <param name="EntityType"><c>Agent</c> or <c>Region</c>.</param>
/// <param name="Id">The row id of the agent or region.</param>
/// <param name="Name">The agent or region name, which is also the heading of <paramref name="Rtf"/>.</param>
/// <param name="Text">
/// The developer description of the entity: <c>&lt;oid&gt; Agent</c> for an agent, or
/// <c>&lt;oid&gt; Region: &lt;name&gt; of &lt;country&gt;</c> for a region.
/// </param>
/// <param name="Rtf">
/// The RTF document <see cref="SelfDocumentationService.BuildSimpleRtf(string, string)"/> builds with
/// <paramref name="Name"/> as heading and <paramref name="Text"/> as body.
/// </param>
public sealed record DocumentationDetails(string EntityType, long Id, string Name, string Text, string Rtf);

/// <summary>
/// Lists the agents and regions of the bound company that can be documented, and returns the text and RTF
/// documentation of one of them. Agents and regions are the only documented entity types.
/// One instance serves one dependency-injection scope; its members are not safe to call from several threads at once.
/// </summary>
/// <param name="companyContext">The company whose agents and regions are listed and documented.</param>
/// <param name="db">The database the agents, countries and regions are read from.</param>
public sealed class SelfDocumentationService(ICompanyContext companyContext, IErewhonDbContext db)
{
    private const string AgentEntityType = "Agent";

    private const string RegionEntityType = "Region";

    private const int HeadingLevel = 1;

    private const int EntryLevel = 2;

    private const string PreHeader = @"{\rtf1\ansi\ansicpg1252\deff0\deflang5129{\fonttbl{\f0\fnil\fprq15\fcharset0 Arial;}{\f1\fnil\fprq8\fcharset0 Arial;}{\f2\fnil\fcharset0 MS Sans Serif;}}{\colortbl ;\red0\green0\blue128;\red0\green0\blue0;}\viewkind4\uc1\pard\cf1\b\f0\fs23 ";

    private const string PreBody = @"\par\par\cf2\b0\f1\fs18 ";

    private const string Footer = @"\f2\fs17\par}";

    private readonly ICompanyContext _companyContext = companyContext ?? throw new ArgumentNullException(nameof(companyContext));

    private readonly IErewhonDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>
    /// Returns the candidate list of the bound company, in this order: the heading <c>-- Agents --</c>; each agent
    /// in name order, as its name; the heading <c>-- Regions --</c>; then, for each country in name order, each of
    /// its regions in name order, as <c>&lt;country&gt; &lt;region&gt;</c>. A country without regions adds no row.
    /// </summary>
    /// <param name="ct">Cancels the database reads.</param>
    /// <returns>The candidate rows; the two headings are always present.</returns>
    /// <exception cref="Common.NoCompanyException">No company is bound.</exception>
    public async Task<IReadOnlyList<DocumentationCandidate>> GetCandidatesAsync(CancellationToken ct = default)
    {
        Company company = await _companyContext.GetCurrentAsync(ct);
        long companyId = company.Id;

        List<Agent> agents = await _db.Agents
            .AsNoTracking()
            .Where(agent => agent.CompanyId == companyId)
            .OrderBy(agent => agent.Name)
            .ToListAsync(ct);

        List<Region> regions = await _db.Regions
            .AsNoTracking()
            .Include(region => region.Country)
            .Where(region => region.Country!.CompanyId == companyId)
            .OrderBy(region => region.Country!.Name)
            .ThenBy(region => region.Name)
            .ToListAsync(ct);

        var candidates = new List<DocumentationCandidate>(agents.Count + regions.Count + 2)
        {
            Heading(MessageKeys.Agents),
        };

        candidates.AddRange(agents.Select(agent =>
            new DocumentationCandidate(agent.GetNameString(), EntryLevel, AgentEntityType, agent.Id)));

        candidates.Add(Heading(MessageKeys.Regions));

        candidates.AddRange(regions.Select(region =>
            new DocumentationCandidate(region.GetCountryAndRegionNames(), EntryLevel, RegionEntityType, region.Id)));

        return candidates.AsReadOnly();
    }

    /// <summary>
    /// Returns the documentation of the agent or region with row id <paramref name="id"/> in the bound company:
    /// its name, its developer description as text, and the RTF document with the name as heading and the
    /// description as body.
    /// </summary>
    /// <param name="entityType"><c>Agent</c> or <c>Region</c>, compared case-insensitively.</param>
    /// <param name="id">The row id of the agent or region.</param>
    /// <param name="ct">Cancels the database reads.</param>
    /// <returns>
    /// The documentation, or <see langword="null"/> when <paramref name="entityType"/> is neither <c>Agent</c> nor
    /// <c>Region</c>, no company is bound, or the bound company has no agent or region with that id.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="entityType"/> is <see langword="null"/>.</exception>
    public async Task<DocumentationDetails?> GetDetailsAsync(string entityType, long id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        bool isAgent = string.Equals(entityType, AgentEntityType, StringComparison.OrdinalIgnoreCase);
        bool isRegion = string.Equals(entityType, RegionEntityType, StringComparison.OrdinalIgnoreCase);
        if (!isAgent && !isRegion)
        {
            return null;
        }

        Company? company = await _companyContext.FindCurrentAsync(ct);
        if (company is null)
        {
            return null;
        }

        long companyId = company.Id;

        if (isAgent)
        {
            Agent? agent = await _db.Agents
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.CompanyId == companyId, ct);

            return agent is null
                ? null
                : Describe(AgentEntityType, agent.Id, agent.Name, agent.GetDebugString());
        }

        Region? region = await _db.Regions
            .AsNoTracking()
            .Include(candidate => candidate.Country)
            .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.Country!.CompanyId == companyId, ct);

        return region is null
            ? null
            : Describe(RegionEntityType, region.Id, region.Name, region.GetDebugString());
    }

    /// <summary>
    /// Builds a Rich Text Format document holding <paramref name="heading"/> in bold 11.5-point navy Arial, an
    /// empty line, and <paramref name="body"/> in 9-point black Arial. The result is the fixed prefix
    /// <c>{\rtf1\ansi\ansicpg1252\deff0\deflang5129{\fonttbl…}{\colortbl…}\viewkind4\uc1\pard\cf1\b\f0\fs23 </c>,
    /// the heading, <c>\par\par\cf2\b0\f1\fs18 </c>, the body and <c>\f2\fs17\par}</c>, with no trailing newline.
    /// Neither text is escaped.
    /// </summary>
    /// <param name="heading">The heading text.</param>
    /// <param name="body">The body text.</param>
    /// <returns>The RTF document.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="heading"/> or <paramref name="body"/> is <see langword="null"/>.
    /// </exception>
    public static string BuildSimpleRtf(string heading, string body)
    {
        ArgumentNullException.ThrowIfNull(heading);
        ArgumentNullException.ThrowIfNull(body);

        return string.Concat(PreHeader, heading, PreBody, body, Footer);
    }

    private static DocumentationCandidate Heading(string titleKey) =>
        new("-- " + MessageCatalog.Get(titleKey) + " --", HeadingLevel, null, null);

    private static DocumentationDetails Describe(string entityType, long id, string name, string text) =>
        new(entityType, id, name, text, BuildSimpleRtf(name, text));
}

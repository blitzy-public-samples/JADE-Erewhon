using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Sessions;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Testing;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises the self-documentation endpoints under <c>/api/app/admin/documentation</c> over HTTP in an
/// administrator session: the candidate list of agents and regions, the text and RTF documentation of one agent or
/// region, the RTF download, and the 404 answer for every other entity type and for an unknown id.
/// </summary>
public sealed class SelfDocumentationTests : ErewhonTestBase
{
    /// <summary>The route that opens an app session.</summary>
    private const string SessionsPath = "/api/app/sessions";

    /// <summary>The route group of the self-documentation endpoints.</summary>
    private const string DocumentationPath = "/api/app/admin/documentation";

    /// <summary>The route of the candidate list.</summary>
    private const string CandidatesPath = DocumentationPath + "/candidates";

    /// <summary>The entity type of a documented agent.</summary>
    private const string AgentEntityType = "Agent";

    /// <summary>The entity type of a documented region.</summary>
    private const string RegionEntityType = "Region";

    /// <summary>The level of a candidate-list heading.</summary>
    private const int HeadingLevel = 1;

    /// <summary>The level of a candidate-list agent or region entry.</summary>
    private const int EntryLevel = 2;

    /// <summary>The RTF text before the heading, ending in one space.</summary>
    private const string RtfPreHeader = @"{\rtf1\ansi\ansicpg1252\deff0\deflang5129{\fonttbl{\f0\fnil\fprq15\fcharset0 Arial;}{\f1\fnil\fprq8\fcharset0 Arial;}{\f2\fnil\fcharset0 MS Sans Serif;}}{\colortbl ;\red0\green0\blue128;\red0\green0\blue0;}\viewkind4\uc1\pard\cf1\b\f0\fs23 ";

    /// <summary>The RTF text between the heading and the body, ending in one space.</summary>
    private const string RtfPreBody = @"\par\par\cf2\b0\f1\fs18 ";

    /// <summary>The RTF text after the body.</summary>
    private const string RtfFooter = @"\f2\fs17\par}";

    /// <summary>The media type of the RTF download.</summary>
    private const string RtfMediaType = "text/rtf";

    /// <summary>The disposition type of the RTF download.</summary>
    private const string AttachmentDisposition = "attachment";

    /// <summary>The file extension of the RTF download.</summary>
    private const string RtfExtension = ".rtf";

    /// <summary>The JSON options of the app endpoints: web defaults with enums as camel-case strings.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The fixtures created by <see cref="SetUpAsync"/>.</summary>
    private Fixture? _fixture;

    /// <summary>The fixtures created by <see cref="SetUpAsync"/>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> has run.</exception>
    private Fixture Fixtures => _fixture ?? throw NotSetUp();

    /// <summary>
    /// Creates the company "Jade Software Corporation" and binds it, then in it, in this order: the agents "Zed Agent"
    /// and "Amy Agent" at 314 Nohwere Lane, Christchurch, New Zealand; the country "France" with the regions "Paris"
    /// and "Lyon"; the country "Belgium" with the region "Brussels"; the client "Clive Entworth" at the same address;
    /// the item category "Documentation" (<c>DOCU</c>) and its retail item "Desk Lamp" in Paris, sold by Amy Agent.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Address address = Builder.CreateTestAddress(
            "314 Nohwere Lane",
            "Christchurch",
            "New Zealand",
            "555 1234567",
            "FAX",
            "admin@Erewhon.nz",
            "www.jadeworld.com");

        Agent zedAgent = await Builder.CreateTestAgentAsync("Zed Agent", address);
        Agent amyAgent = await Builder.CreateTestAgentAsync("Amy Agent", address);
        Country france = await Builder.CreateTestCountryAsync("France");
        Region paris = await Builder.CreateTestRegionAsync("Paris", france);
        Region lyon = await Builder.CreateTestRegionAsync("Lyon", france);
        Country belgium = await Builder.CreateTestCountryAsync("Belgium");
        Region brussels = await Builder.CreateTestRegionAsync("Brussels", belgium);
        Client client = await Builder.CreateTestClientAsync("Clive Entworth", address);
        ItemCategory category = await Builder.CreateItemCategoryAsync(
            "Documentation",
            "DOCU",
            "Items of the self-documentation fixture");
        RetailItem retailItem = await Builder.CreateTestRetailItemAsync(
            "Desk Lamp",
            "A brass desk lamp",
            photo: null,
            paris,
            category,
            amyAgent,
            45.00m);

        _fixture = new Fixture(amyAgent, zedAgent, france, paris, lyon, brussels, client, retailItem);
    }

    /// <summary>
    /// <c>GET /candidates</c> answers 200 with exactly these rows, in this order: the heading "-- Agents --" at level 1
    /// with no entity; "Amy Agent" and "Zed Agent" at level 2 as <c>Agent</c> entries with their ids; the heading
    /// "-- Regions --" at level 1 with no entity; "Belgium Brussels", "France Lyon" and "France Paris" at level 2 as
    /// <c>Region</c> entries with their ids. No client, country, category or item is listed.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task CandidatesListAgentsThenRegionsByCountry()
    {
        Fixture fixture = Fixtures;
        DocumentationCandidate[] expected =
        [
            new("-- " + MessageCatalog.Get(MessageKeys.Agents) + " --", HeadingLevel, null, null),
            new("Amy Agent", EntryLevel, AgentEntityType, fixture.AmyAgent.Id),
            new("Zed Agent", EntryLevel, AgentEntityType, fixture.ZedAgent.Id),
            new("-- " + MessageCatalog.Get(MessageKeys.Regions) + " --", HeadingLevel, null, null),
            new("Belgium Brussels", EntryLevel, RegionEntityType, fixture.Brussels.Id),
            new("France Lyon", EntryLevel, RegionEntityType, fixture.Lyon.Id),
            new("France Paris", EntryLevel, RegionEntityType, fixture.Paris.Id),
        ];

        using HttpClient http = Api.CreateHttpClient();
        string sessionId = await OpenAdminSessionAsync(http);

        using HttpResponseMessage response = await GetAsync(http, sessionId, CandidatesPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<DocumentationCandidate>? candidates =
            await response.Content.ReadFromJsonAsync<List<DocumentationCandidate>>(Json);
        Assert.NotNull(candidates);
        Assert.Equal(expected, candidates);
        Assert.Equal("-- Agents --", candidates[0].Text);
        Assert.Equal("-- Regions --", candidates[3].Text);
    }

    /// <summary>
    /// <c>GET /{type}/{id}</c> for "Amy Agent", with <paramref name="type"/> naming the agent type in any letter case,
    /// answers 200 with entity type <c>Agent</c>, the agent's id and name, the text <c>5774.&lt;id&gt; Agent</c>, and
    /// the RTF document with "Amy Agent" as heading and that text as body, which
    /// <see cref="SelfDocumentationService.BuildSimpleRtf"/> also returns for them.
    /// </summary>
    /// <param name="type">The spelling of the agent type in the route.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData("Agent")]
    [InlineData("agent")]
    public async Task AgentDetailsTextAndRtf(string type)
    {
        Agent amyAgent = Fixtures.AmyAgent;
        string expectedText = AgentDebugString(amyAgent.Id);
        string expectedRtf = ExpectedRtf("Amy Agent", expectedText);

        using HttpClient http = Api.CreateHttpClient();
        string sessionId = await OpenAdminSessionAsync(http);

        DocumentationDetailsResponse details = await GetDetailsAsync(http, sessionId, type, amyAgent.Id);

        Assert.Equal(AgentEntityType, details.EntityType);
        Assert.Equal(amyAgent.Id, details.Id);
        Assert.Equal("Amy Agent", details.Name);
        Assert.Equal(expectedText, details.Text);
        Assert.StartsWith("5774.", details.Text, StringComparison.Ordinal);
        Assert.Equal(expectedRtf, details.Rtf);
        Assert.Equal(expectedRtf, SelfDocumentationService.BuildSimpleRtf("Amy Agent", details.Text));
    }

    /// <summary>
    /// <c>GET /{type}/{id}</c> for the region "Paris" of "France", with <paramref name="type"/> naming the region type
    /// in any letter case, answers 200 with entity type <c>Region</c>, the region's id and name, the text
    /// <c>5781.&lt;id&gt; Region: Paris of France</c>, and the RTF document with "Paris" as heading and that text as
    /// body.
    /// </summary>
    /// <param name="type">The spelling of the region type in the route.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData("Region")]
    [InlineData("REGION")]
    public async Task RegionDetailsTextAndRtf(string type)
    {
        Region paris = Fixtures.Paris;
        string expectedText = RegionDebugString(paris.Id, "Paris", "France");
        string expectedRtf = ExpectedRtf("Paris", expectedText);

        using HttpClient http = Api.CreateHttpClient();
        string sessionId = await OpenAdminSessionAsync(http);

        DocumentationDetailsResponse details = await GetDetailsAsync(http, sessionId, type, paris.Id);

        Assert.Equal(RegionEntityType, details.EntityType);
        Assert.Equal(paris.Id, details.Id);
        Assert.Equal("Paris", details.Name);
        Assert.Equal(expectedText, details.Text);
        Assert.StartsWith("5781.", details.Text, StringComparison.Ordinal);
        Assert.EndsWith(" Region: Paris of France", details.Text, StringComparison.Ordinal);
        Assert.Equal(expectedRtf, details.Rtf);
    }

    /// <summary>
    /// <c>GET /{type}/{id}/rtf</c> for "Amy Agent" and for the region "Paris" each answers 200 with media type
    /// <c>text/rtf</c>, an <c>attachment</c> disposition named "Amy Agent.rtf" or "Paris.rtf" with no
    /// <c>filename*</c> parameter, and the Latin-1 bytes of the entity's RTF document as the body.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task RtfDownloadIsAttachmentNamedAfterEntity()
    {
        Fixture fixture = Fixtures;

        using HttpClient http = Api.CreateHttpClient();
        string sessionId = await OpenAdminSessionAsync(http);

        await AssertRtfDownloadAsync(
            http,
            sessionId,
            AgentEntityType,
            fixture.AmyAgent.Id,
            "Amy Agent",
            AgentDebugString(fixture.AmyAgent.Id));
        await AssertRtfDownloadAsync(
            http,
            sessionId,
            RegionEntityType,
            fixture.Paris.Id,
            "Paris",
            RegionDebugString(fixture.Paris.Id, "Paris", "France"));
    }

    /// <summary>
    /// The details route and the RTF route each answer 404 with an empty body for the types <c>Client</c>,
    /// <c>RetailItem</c>, <c>Company</c> and <c>Country</c> with the id of the fixture client, retail item, company
    /// and country "France", and for the types <c>Agent</c> and <c>Region</c> with an id no row holds.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task UndocumentedTypeIsNotFound()
    {
        Fixture fixture = Fixtures;
        long companyId = Builder.MyCompany?.Id ?? throw NotSetUp();
        (string Type, long Id)[] targets =
        [
            ("Client", fixture.Client.Id),
            ("RetailItem", fixture.RetailItem.Id),
            ("Company", companyId),
            ("Country", fixture.France.Id),
            (AgentEntityType, long.MaxValue),
            (RegionEntityType, long.MaxValue),
        ];
        string[] paths = targets
            .SelectMany(target => new[]
            {
                $"{DocumentationPath}/{target.Type}/{target.Id}",
                $"{DocumentationPath}/{target.Type}/{target.Id}/rtf",
            })
            .ToArray();

        using HttpClient http = Api.CreateHttpClient();
        string sessionId = await OpenAdminSessionAsync(http);

        var answers = new List<(string Path, HttpStatusCode Status, int BodyLength)>(paths.Length);
        foreach (string path in paths)
        {
            using HttpResponseMessage response = await GetAsync(http, sessionId, path);
            byte[] body = await response.Content.ReadAsByteArrayAsync();
            answers.Add((path, response.StatusCode, body.Length));
        }

        Assert.Equal(paths.Select(path => (path, HttpStatusCode.NotFound, 0)), answers);
    }

    /// <summary>Returns <c>5774.&lt;agentId&gt; Agent</c>.</summary>
    private static string AgentDebugString(long agentId) =>
        ObjectIdentity.Format(ObjectIdentity.Agent, agentId) + " Agent";

    /// <summary>Returns <c>5781.&lt;regionId&gt; Region: &lt;region&gt; of &lt;country&gt;</c>.</summary>
    private static string RegionDebugString(long regionId, string region, string country) =>
        ObjectIdentity.Format(ObjectIdentity.Region, regionId)
        + " Region: " + region + " " + MessageCatalog.Get(MessageKeys.Of) + " " + country;

    /// <summary>Returns the RTF document with <paramref name="heading"/> as heading and <paramref name="body"/> as body.</summary>
    private static string ExpectedRtf(string heading, string body) => RtfPreHeader + heading + RtfPreBody + body + RtfFooter;

    /// <summary>
    /// Opens an administrator session of the admin area with <c>POST /api/app/sessions</c> and no user name, asserts
    /// that it succeeded for the company user, and returns its id.
    /// </summary>
    private static async Task<string> OpenAdminSessionAsync(HttpClient http)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            SessionsPath,
            new CreateSessionRequest(LogonArea.Admin, Administrator: true, UserName: null),
            Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CreateSessionResponse? session = await response.Content.ReadFromJsonAsync<CreateSessionResponse>(Json);
        Assert.NotNull(session);
        Assert.True(session.Succeeded, session.Message);
        Assert.Equal(SessionRole.Administrator, session.Role);
        Assert.NotNull(session.SessionId);
        Assert.NotEmpty(session.SessionId);
        return session.SessionId;
    }

    /// <summary>Sends <c>GET</c> <paramref name="path"/> in the session <paramref name="sessionId"/>.</summary>
    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, string sessionId, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(SessionMiddleware.HeaderName, sessionId);
        return await http.SendAsync(request);
    }

    /// <summary>
    /// Sends <c>GET /{type}/{id}</c> in the session <paramref name="sessionId"/>, asserts 200 and returns the
    /// documentation it answers with.
    /// </summary>
    private static async Task<DocumentationDetailsResponse> GetDetailsAsync(
        HttpClient http,
        string sessionId,
        string type,
        long id)
    {
        using HttpResponseMessage response = await GetAsync(http, sessionId, $"{DocumentationPath}/{type}/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        DocumentationDetailsResponse? details =
            await response.Content.ReadFromJsonAsync<DocumentationDetailsResponse>(Json);
        Assert.NotNull(details);
        return details;
    }

    /// <summary>
    /// Sends <c>GET /{type}/{id}/rtf</c> in the session <paramref name="sessionId"/> and asserts 200, media type
    /// <c>text/rtf</c>, an <c>attachment</c> disposition named <c>&lt;name&gt;.rtf</c> with no <c>filename*</c>
    /// parameter, and the Latin-1 bytes of the RTF document with <paramref name="name"/> as heading and
    /// <paramref name="text"/> as body.
    /// </summary>
    private static async Task AssertRtfDownloadAsync(
        HttpClient http,
        string sessionId,
        string type,
        long id,
        string name,
        string text)
    {
        using HttpResponseMessage response = await GetAsync(http, sessionId, $"{DocumentationPath}/{type}/{id}/rtf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RtfMediaType, response.Content.Headers.ContentType?.MediaType);
        ContentDispositionHeaderValue? disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal(AttachmentDisposition, disposition.DispositionType);
        Assert.Equal(name + RtfExtension, disposition.FileName?.Trim('"'));
        Assert.Null(disposition.FileNameStar);
        Assert.Equal(Encoding.Latin1.GetBytes(ExpectedRtf(name, text)), await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>Returns the exception for a fixture read before <see cref="SetUpAsync"/> has run.</summary>
    private static InvalidOperationException NotSetUp() =>
        new($"The fixtures are created by {nameof(SetUpAsync)}.");

    /// <summary>The fixture objects of one test.</summary>
    /// <param name="AmyAgent">The agent "Amy Agent", created second.</param>
    /// <param name="ZedAgent">The agent "Zed Agent", created first.</param>
    /// <param name="France">The country "France".</param>
    /// <param name="Paris">The region "Paris" of France, created first.</param>
    /// <param name="Lyon">The region "Lyon" of France, created second.</param>
    /// <param name="Brussels">The region "Brussels" of Belgium.</param>
    /// <param name="Client">The client "Clive Entworth".</param>
    /// <param name="RetailItem">The retail item "Desk Lamp".</param>
    private sealed record Fixture(
        Agent AmyAgent,
        Agent ZedAgent,
        Country France,
        Region Paris,
        Region Lyon,
        Region Brussels,
        Client Client,
        RetailItem RetailItem);
}

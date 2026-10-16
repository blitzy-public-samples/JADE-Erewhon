using System.Net.Http.Json;
using System.Text.Json;
using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Domain;
using Erewhon.Testing;

namespace Erewhon.Api.Tests.Rest;

/// <summary>
/// Tests of the REST operation <c>getAllAgents</c>, called over HTTP as <c>GET /api/rest/agents</c>. Ports the JADE
/// class <c>RestTestGetAllAgents</c>.
/// </summary>
public sealed class RestTestGetAllAgents : ErewhonTestBase
{
    private const int TotalAgents = 4;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly List<Agent> _numberedAgents = new(TotalAgents);

    /// <summary>
    /// Creates the test data of <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which binds its company and holds
    /// the agent "Test Agent", then the agents "Test Agent 1" to "Test Agent 4", each at "Street", "City", "Country",
    /// phone "Phone", fax "Fax", e-mail "agent@email.com" and web site "www.agent.com", and keeps those four agents in
    /// creation order. Ports <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the company holds five agents.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();

        for (int agentCount = 1; agentCount <= TotalAgents; agentCount++)
        {
            Address address = Builder.CreateTestAddress(
                "Street", "City", "Country", "Phone", "Fax", "agent@email.com", "www.agent.com");
            _numberedAgents.Add(await Builder.CreateTestAgentAsync("Test Agent " + agentCount, address));
        }
    }

    /// <summary>
    /// Calling <c>getAllAgents</c> answers 200 with the company's five agents: "Test Agent", then "Test Agent 1" to
    /// "Test Agent 4", each with the GUID of the agent of that name.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetAllAgents()
    {
        using var http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage response = await http.GetAsync("/api/rest/agents");
        AgentDto[]? agents = await response.Content.ReadFromJsonAsync<AgentDto[]>(Json);

        Assert.NotNull(agents);
        Assert.Equal(5, agents.Length);
        Assert.Equal(200, (int)response.StatusCode);

        (string? Name, string? Guid)[] expected =
        {
            ("Test Agent", Builder.MyAgent!.Guid),
            ("Test Agent 1", _numberedAgents[0].Guid),
            ("Test Agent 2", _numberedAgents[1].Guid),
            ("Test Agent 3", _numberedAgents[2].Guid),
            ("Test Agent 4", _numberedAgents[3].Guid),
        };
        Assert.Equal(expected, agents.Select(agent => (agent.Name, agent.Guid)));
    }

    /// <summary>
    /// After the agents "Zed Agent", "Adam Agent" and "Test Agent 0" are created in that order, at the address of
    /// <see cref="SetUpAsync"/>, calling <c>getAllAgents</c> answers 200 with the company's eight agents in name order:
    /// "Adam Agent", "Test Agent", "Test Agent 0", "Test Agent 1" to "Test Agent 4", then "Zed Agent", each with the
    /// GUID of the agent of that name.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AgentsListedInNameOrder()
    {
        // Arrange
        Agent zedAgent = await Builder.CreateTestAgentAsync("Zed Agent", CreateSetUpAddress());
        Agent adamAgent = await Builder.CreateTestAgentAsync("Adam Agent", CreateSetUpAddress());
        Agent zeroAgent = await Builder.CreateTestAgentAsync("Test Agent 0", CreateSetUpAddress());
        using var http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage response = await http.GetAsync("/api/rest/agents");
        AgentDto[]? agents = await response.Content.ReadFromJsonAsync<AgentDto[]>(Json);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.NotNull(agents);

        (string? Name, string? Guid)[] expected =
        {
            ("Adam Agent", adamAgent.Guid),
            ("Test Agent", Builder.MyAgent!.Guid),
            ("Test Agent 0", zeroAgent.Guid),
            ("Test Agent 1", _numberedAgents[0].Guid),
            ("Test Agent 2", _numberedAgents[1].Guid),
            ("Test Agent 3", _numberedAgents[2].Guid),
            ("Test Agent 4", _numberedAgents[3].Guid),
            ("Zed Agent", zedAgent.Guid),
        };
        Assert.Equal(expected, agents.Select(agent => (agent.Name, agent.Guid)));
    }

    /// <summary>
    /// Creates the address of <see cref="SetUpAsync"/>: "Street", "City", "Country", phone "Phone", fax "Fax", e-mail
    /// "agent@email.com" and web site "www.agent.com".
    /// </summary>
    /// <returns>The new, unstored address.</returns>
    private Address CreateSetUpAddress() =>
        Builder.CreateTestAddress("Street", "City", "Country", "Phone", "Fax", "agent@email.com", "www.agent.com");
}

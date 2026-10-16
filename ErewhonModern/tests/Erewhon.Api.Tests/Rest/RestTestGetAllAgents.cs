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

    /// <summary>
    /// Creates the test data of <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which binds its company and holds
    /// the agent "Test Agent", then the agents "Test Agent 1" to "Test Agent 4", each at "Street", "City", "Country",
    /// phone "Phone", fax "Fax", e-mail "agent@email.com" and web site "www.agent.com". Ports <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the company holds five agents.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();

        for (int agentCount = 1; agentCount <= TotalAgents; agentCount++)
        {
            Address address = Builder.CreateTestAddress(
                "Street", "City", "Country", "Phone", "Fax", "agent@email.com", "www.agent.com");
            await Builder.CreateTestAgentAsync("Test Agent " + agentCount, address);
        }
    }

    /// <summary>Calling <c>getAllAgents</c> answers 200 with the company's five agents.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetAllAgents()
    {
        using var http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage response = await http.GetAsync("/api/rest/agents");
        AgentDto[]? agents = await response.Content.ReadFromJsonAsync<AgentDto[]>(Json);

        // Assert
        Assert.NotNull(agents);
        Assert.Equal(5, agents.Length);
        Assert.Equal(200, (int)response.StatusCode);
    }
}

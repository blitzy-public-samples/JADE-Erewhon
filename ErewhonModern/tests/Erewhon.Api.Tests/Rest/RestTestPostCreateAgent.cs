using System.Net.Http.Json;
using System.Text.Json;
using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Tests.Rest;

/// <summary>
/// Tests of the REST operation <c>POST /api/rest/agents</c>, called over HTTP on the API of the test assembly. Each
/// test starts with the data of <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which holds one agent, and ends
/// with no company. Each test also checks the raw JSON result body: its four properties, status code, complete error
/// list, empty warnings and GUID. Ports the JADE class <c>RestTestPostCreateAgent</c>.
/// </summary>
public sealed class RestTestPostCreateAgent : ErewhonTestBase
{
    private const string AgentsRoute = "/api/rest/agents";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Creates the company "Jade Software Test Company" and its fixtures through
    /// <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which sets and binds
    /// <see cref="TestCompanyBuilder.MyCompany"/>. Ports <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
    }

    /// <summary>
    /// <c>POST /api/rest/agents</c> with no name answers 400 with <c>$AgentNameRequired</c> as the first error and
    /// leaves the company with its one agent. The raw body holds status code 400, <c>$AgentNameRequired</c> as its only
    /// error, no warnings and a null GUID.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidAgent()
    {
        // Arrange
        AgentDto dtoAgent = CreateAgentDto(name: null);
        using HttpClient http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage response = await http.PostAsJsonAsync(AgentsRoute, dtoAgent, Json);
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        Assert.NotNull(dtoResults);
        Assert.NotEmpty(dtoResults.AllErrors);
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal(1, await CountAgentsAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.AgentNameRequired), dtoResults.AllErrors[0]);
        await CheckResultBodyAsync(response, [MessageCatalog.Get(MessageKeys.AgentNameRequired)], expectedGuid: null);
    }

    /// <summary>
    /// <c>POST /api/rest/agents</c> with a new name and a valid address answers 201 with no errors and adds an agent
    /// to the company holding the posted name and the seven posted address fields. The raw body holds status code 201,
    /// no errors, no warnings and the GUID of the stored agent.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testValidAgent()
    {
        // Arrange
        AgentDto dtoAgent = CreateAgentDto(name: "Test Agent Name");
        using HttpClient http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage response = await http.PostAsJsonAsync(AgentsRoute, dtoAgent, Json);
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);
        Agent? agent = await FindAgentAsync(dtoAgent.Name!);

        Assert.NotNull(dtoResults);
        Assert.Empty(dtoResults.AllErrors);
        Assert.Equal(201, (int)response.StatusCode);
        Assert.Equal(2, await CountAgentsAsync());
        Assert.NotNull(agent);
        Assert.Equal(dtoAgent.Name, agent.Name);
        Assert.Equal(dtoAgent.Street, agent.Address.Street);
        Assert.Equal(dtoAgent.City, agent.Address.City);
        Assert.Equal(dtoAgent.Country, agent.Address.Country);
        Assert.Equal(dtoAgent.Phone, agent.Address.Phone);
        Assert.Equal(dtoAgent.Fax, agent.Address.Fax);
        Assert.Equal(dtoAgent.Email, agent.Address.Email);
        Assert.Equal(dtoAgent.Website, agent.Address.WebSite);
        await CheckResultBodyAsync(response, [], agent.Guid);
    }

    /// <summary>
    /// Returns an agent DTO with <paramref name="name"/>, street "Street", city "City", country "Country", phone
    /// "Phone", fax "Fax", e-mail "agent@email.com" and web site "www.website.com", and no GUID.
    /// </summary>
    /// <param name="name">The agent name, or <see langword="null"/> for none.</param>
    /// <returns>The new DTO.</returns>
    private static AgentDto CreateAgentDto(string? name) => new()
    {
        Name = name,
        Street = "Street",
        City = "City",
        Country = "Country",
        Phone = "Phone",
        Fax = "Fax",
        Email = "agent@email.com",
        Website = "www.website.com",
    };

    /// <summary>
    /// Checks the raw JSON body of <paramref name="response"/>: an object with exactly the properties
    /// <c>statusCode</c>, <c>allErrors</c>, <c>allWarnings</c> and <c>guid</c>, where <c>statusCode</c> is the number
    /// of the HTTP status of <paramref name="response"/>, <c>allErrors</c> holds the strings of
    /// <paramref name="expectedErrors"/> in order and nothing else, <c>allWarnings</c> is an empty array, and
    /// <c>guid</c> is null when <paramref name="expectedGuid"/> is <see langword="null"/> and otherwise the non-empty
    /// string <paramref name="expectedGuid"/>.
    /// </summary>
    /// <param name="response">The response of <c>POST /api/rest/agents</c>.</param>
    /// <param name="expectedErrors">The complete list of error messages, in order.</param>
    /// <param name="expectedGuid">The GUID of the created agent, or <see langword="null"/> for none.</param>
    /// <returns>A task that completes when the body is checked.</returns>
    private static async Task CheckResultBodyAsync(
        HttpResponseMessage response,
        IReadOnlyList<string> expectedErrors,
        string? expectedGuid)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(
            new[] { "allErrors", "allWarnings", "guid", "statusCode" },
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        JsonElement statusCode = root.GetProperty("statusCode");
        Assert.Equal(JsonValueKind.Number, statusCode.ValueKind);
        Assert.Equal((int)response.StatusCode, statusCode.GetInt32());

        JsonElement allErrors = root.GetProperty("allErrors");
        Assert.Equal(JsonValueKind.Array, allErrors.ValueKind);
        Assert.All(allErrors.EnumerateArray(), error => Assert.Equal(JsonValueKind.String, error.ValueKind));
        Assert.Equal(expectedErrors, allErrors.EnumerateArray().Select(error => error.GetString()!));

        JsonElement allWarnings = root.GetProperty("allWarnings");
        Assert.Equal(JsonValueKind.Array, allWarnings.ValueKind);
        Assert.Empty(allWarnings.EnumerateArray());

        JsonElement guid = root.GetProperty("guid");
        if (expectedGuid is null)
        {
            Assert.Equal(JsonValueKind.Null, guid.ValueKind);
        }
        else
        {
            Assert.Equal(JsonValueKind.String, guid.ValueKind);
            string? actualGuid = guid.GetString();
            Assert.NotNull(actualGuid);
            Assert.NotEmpty(actualGuid);
            Assert.Equal(expectedGuid, actualGuid);
        }
    }

    /// <summary>
    /// Returns the number of agents of <see cref="TestCompanyBuilder.MyCompany"/>, read untracked through
    /// <see cref="IErewhonDbContext.Agents"/> in a new scope of <see cref="ErewhonTestBase.Api"/>.
    /// </summary>
    /// <returns>The agent count.</returns>
    /// <exception cref="InvalidOperationException"><see cref="TestCompanyBuilder.MyCompany"/> is not set.</exception>
    private async Task<int> CountAgentsAsync()
    {
        long companyId = MyCompanyId();

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Agents
            .AsNoTracking()
            .CountAsync(agent => agent.CompanyId == companyId);
    }

    /// <summary>
    /// Returns the agent of <see cref="TestCompanyBuilder.MyCompany"/> whose name equals <paramref name="name"/>
    /// exactly, with its address, read untracked through <see cref="IErewhonDbContext.Agents"/> in a new scope of
    /// <see cref="ErewhonTestBase.Api"/>; <see langword="null"/> when the company has no such agent.
    /// </summary>
    /// <param name="name">The exact agent name.</param>
    /// <returns>The agent, or <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException"><see cref="TestCompanyBuilder.MyCompany"/> is not set.</exception>
    private async Task<Agent?> FindAgentAsync(string name)
    {
        long companyId = MyCompanyId();

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Agents
            .AsNoTracking()
            .SingleOrDefaultAsync(agent => agent.CompanyId == companyId && agent.Name == name);
    }

    /// <summary>Returns the internal id of <see cref="TestCompanyBuilder.MyCompany"/>.</summary>
    /// <returns>The company id.</returns>
    /// <exception cref="InvalidOperationException"><see cref="TestCompanyBuilder.MyCompany"/> is not set.</exception>
    private long MyCompanyId() =>
        (Builder.MyCompany ?? throw new InvalidOperationException($"{nameof(TestCompanyBuilder.MyCompany)} is not set."))
        .Id;
}

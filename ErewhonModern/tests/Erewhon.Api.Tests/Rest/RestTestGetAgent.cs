using System.Net.Http.Json;
using System.Text.Json;
using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Tests.Rest;

/// <summary>
/// Tests of the REST operation <c>getAgent</c>, called over HTTP as <c>GET /api/rest/agents/by-name?name=</c>.
/// Ports the JADE class <c>RestTestGetAgent</c>.
/// </summary>
public sealed class RestTestGetAgent : Erewhon.Testing.ErewhonTestBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Creates the test data of <see cref="Erewhon.Testing.TestCompanyBuilder.CreateTestDataAsync"/> and binds its
    /// company. Ports <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the test data exists.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
    }

    /// <summary>Looking up an agent name that no agent holds answers 404 with an empty body.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidAgent()
    {
        const string AgentName = "Some Agent That Does Not Exist";
        const int ExpectedError = 404;

        // Arrange
        using var http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage response = await http.GetAsync(ByNamePath(AgentName));

        // Assert
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(ExpectedError, (int)response.StatusCode);
    }

    /// <summary>
    /// Looking up the name of an agent created in the bound company answers 200 with the agent's name and its seven
    /// address fields.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testValidAgent()
    {
        const string AgentName = "Hank Williams";
        const string AgentStreet = "9127 Bel Air Drive";
        const string AgentCity = "Los Angeles";
        const string AgentCountry = "United States of America";
        const string AgentPhone = "1 8 2938 4756";
        const string AgentFax = "1 8 6354 7364";
        const string AgentEmail = "hank@it.com";
        const string AgentWebsite = "www.it.com";
        const int ExpectedStatus = 200;

        // Arrange
        using var http = Api.CreateHttpClient();

        Address address = Builder.CreateTestAddress(
            AgentStreet, AgentCity, AgentCountry, AgentPhone, AgentFax, AgentEmail, AgentWebsite);
        await Builder.CreateTestAgentAsync(AgentName, address);

        Assert.True(await AgentExistsInBoundCompanyAsync(AgentName));

        // Act
        using HttpResponseMessage response = await http.GetAsync(ByNamePath(AgentName));

        // Assert
        Assert.Equal(ExpectedStatus, (int)response.StatusCode);
        AgentDto? dtoAgent = await response.Content.ReadFromJsonAsync<AgentDto>(Json);
        Assert.NotNull(dtoAgent);
        Assert.Equal(AgentName, dtoAgent.Name);
        Assert.Equal(AgentStreet, dtoAgent.Street);
        Assert.Equal(AgentCity, dtoAgent.City);
        Assert.Equal(AgentCountry, dtoAgent.Country);
        Assert.Equal(AgentPhone, dtoAgent.Phone);
        Assert.Equal(AgentFax, dtoAgent.Fax);
        Assert.Equal(AgentEmail, dtoAgent.Email);
        Assert.Equal(AgentWebsite, dtoAgent.Website);
    }

    /// <summary>Returns the path of <c>getAgent</c> for <paramref name="name"/>, with the name escaped.</summary>
    private static string ByNamePath(string name) =>
        "/api/rest/agents/by-name?name=" + Uri.EscapeDataString(name);

    /// <summary>
    /// Returns whether the company of <see cref="Erewhon.Testing.TestCompanyBuilder.MyCompany"/> holds an agent named
    /// exactly <paramref name="name"/>, read in a new scope of the API.
    /// </summary>
    private async Task<bool> AgentExistsInBoundCompanyAsync(string name)
    {
        long companyId = Builder.MyCompany!.Id;

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Agents
            .AsNoTracking()
            .AnyAsync(agent => agent.CompanyId == companyId && agent.Name == name);
    }
}

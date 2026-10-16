using System.ServiceModel;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Services;
using Erewhon.SoapClient.WebService;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Soap.Tests;

/// <summary>
/// Ports the JADE test class <c>TestGetAgent</c>: the SOAP operation <c>getAgent</c> of <c>ErewhonWebService</c>,
/// called over HTTP through the generated <see cref="ErewhonWebServiceClient"/>. Each test runs over the company and
/// objects of <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which is deleted after the test.
/// </summary>
public sealed class TestGetAgent : ErewhonTestBase
{
    private ErewhonWebServiceClient? _client;

    /// <summary>The <c>ErewhonWebService</c> client of this test. Ports <c>myWebService</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> or after <see cref="TearDownAsync"/>.</exception>
    private ErewhonWebServiceClient Client =>
        _client ?? throw new InvalidOperationException(
            $"The SOAP client is available from {nameof(SetUpAsync)} until {nameof(TearDownAsync)}.");

    /// <summary>
    /// Creates and binds the test data through <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, then creates the
    /// <c>ErewhonWebService</c> client addressed to <see cref="ErewhonTestBase.Api"/>. Ports <c>beforeClass</c> and
    /// <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the test data and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
        _client = Api.CreateSoapClientFactory().CreateWebServiceClient();
    }

    /// <summary>
    /// Closes the client, aborting it when it is faulted or its close fails, then deletes the test's companies through
    /// the base implementation. Ports <c>afterTest</c> and <c>afterClass</c>.
    /// </summary>
    /// <returns>A task that completes when the client is closed and no company is left.</returns>
    protected override async Task TearDownAsync()
    {
        try
        {
            await CloseClientAsync();
        }
        finally
        {
            await base.TearDownAsync();
        }
    }

    /// <summary>
    /// <c>getAgent</c> for a name that no agent of the company holds returns <see langword="null"/>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidAgent()
    {
        const string AgentName = "Some Agent That Does Not Exist";

        // Act
        DTOAgent? dtoAgent = await Client.getAgentAsync(AgentName);

        // Assert
        Assert.Null(dtoAgent);
    }

    /// <summary>
    /// <c>getAgent</c> for an agent created through <see cref="AgentService"/> returns a <see cref="DTOAgent"/> holding
    /// the agent's name and the seven fields of its address.
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

        // Arrange
        var address = Builder.CreateTestAddress(
            AgentStreet, AgentCity, AgentCountry, AgentPhone, AgentFax, AgentEmail, AgentWebsite);
        await Builder.CreateTestAgentAsync(AgentName, address);

        Assert.True(await AgentExistsAsync(AgentName));

        // Act
        DTOAgent? dtoAgent = await Client.getAgentAsync(AgentName);

        // Assert
        Assert.NotNull(dtoAgent);
        Assert.Equal(AgentName, dtoAgent.name);
        Assert.Equal(AgentStreet, dtoAgent.street);
        Assert.Equal(AgentCity, dtoAgent.city);
        Assert.Equal(AgentCountry, dtoAgent.country);
        Assert.Equal(AgentPhone, dtoAgent.phone);
        Assert.Equal(AgentFax, dtoAgent.fax);
        Assert.Equal(AgentEmail, dtoAgent.email);
        Assert.Equal(AgentWebsite, dtoAgent.website);
    }

    /// <summary>
    /// Returns whether the company <see cref="TestCompanyBuilder.MyCompany"/> holds an agent named exactly
    /// <paramref name="name"/>, read through <see cref="AgentService.FindByNameAsync"/> in a new scope of
    /// <see cref="ErewhonTestBase.Api"/> bound to that company. Ports <c>app.myCompany.allAgents[name] &lt;&gt; null</c>.
    /// </summary>
    /// <param name="name">The agent name to look up.</param>
    /// <returns><see langword="true"/> when the agent exists.</returns>
    /// <exception cref="InvalidOperationException">Called before the test company exists.</exception>
    private async Task<bool> AgentExistsAsync(string name)
    {
        long companyId = Builder.MyCompany?.Id
            ?? throw new InvalidOperationException($"The test company is created by {nameof(SetUpAsync)}.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(companyId);

        return await scope.ServiceProvider.GetRequiredService<AgentService>().FindByNameAsync(name) is not null;
    }

    /// <summary>
    /// Aborts the client when it is faulted, otherwise closes it and aborts it when the close fails with a
    /// <see cref="CommunicationException"/> or a <see cref="TimeoutException"/>. Does nothing when no client exists;
    /// afterwards no client exists.
    /// </summary>
    /// <returns>A task that completes when the client is closed or aborted.</returns>
    private async Task CloseClientAsync()
    {
        if (_client is not { } client)
        {
            return;
        }

        _client = null;

        if (client.State == CommunicationState.Faulted)
        {
            client.Abort();
            return;
        }

        try
        {
            await client.CloseAsync();
        }
        catch (CommunicationException)
        {
            client.Abort();
        }
        catch (TimeoutException)
        {
            client.Abort();
        }
    }
}

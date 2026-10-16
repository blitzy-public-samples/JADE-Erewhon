using System.ServiceModel;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.SoapClient.Admin;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Soap.Tests;

/// <summary>
/// Port of the JADE test class <c>TestDeleteAgent</c>: the SOAP operation <c>deleteAgent</c> of
/// <c>ErewhonWebServiceAdmin</c>, called through the generated <see cref="ErewhonWebServiceAdminClient"/> against the
/// running API. Each test starts with the <c>createTestData</c> fixture and ends with its company deleted.
/// </summary>
public sealed class TestDeleteAgent : ErewhonTestBase
{
    private ErewhonWebServiceAdminClient? _client;

    /// <summary>The <c>ErewhonWebServiceAdmin</c> client of the test. Ports <c>myWebServiceAdmin</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> or after <see cref="TearDownAsync"/>.</exception>
    private ErewhonWebServiceAdminClient Client =>
        _client ?? throw new InvalidOperationException(
            $"The {nameof(ErewhonWebServiceAdminClient)} exists from {nameof(SetUpAsync)} until {nameof(TearDownAsync)}.");

    /// <summary>
    /// Deleting an agent name that no agent of the company holds returns status 404 and the single error
    /// <c>$AgentNameNotFound</c> for that name.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidAgent()
    {
        const string agentName = "Rowan Stracke";
        const int expectedError = 404;

        Assert.False(await AgentExistsAsync(agentName));

        DTOResult result = await Client.deleteAgentAsync(agentName);

        Assert.Equal(expectedError, result.statusCode);
        Assert.NotNull(result.allErrors);
        string error = Assert.Single(result.allErrors);
        Assert.Equal(MessageCatalog.Get(MessageKeys.AgentNameNotFound, agentName), error);
        Assert.Equal("Agent Rowan Stracke not found", error);
    }

    /// <summary>
    /// Deleting an existing agent returns status 200 with no GUID and no errors, and the company no longer holds an
    /// agent with that name.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testValidAgent()
    {
        const string agentName = "Rowan Stracke";
        const string agentStreet = "Rua Dom Carlos I 62";
        const string agentCity = "Funchal";
        const string agentCountry = "Portugal";
        const string agentPhone = "291 225 023";
        const string agentFax = "291 244 2464";
        const string agentEmail = "rowan@funchal.pt";
        const string agentWebsite = "www.funchal.pt";
        const int expectedStatus = 200;

        Address agentAddress = Builder.CreateTestAddress(
            agentStreet, agentCity, agentCountry, agentPhone, agentFax, agentEmail, agentWebsite);
        Agent agent = await Builder.CreateTestAgentAsync(agentName, agentAddress);

        Assert.True(await AgentExistsAsync(agent.Name));

        DTOResult result = await Client.deleteAgentAsync(agent.Name);

        Assert.NotNull(result);
        Assert.Equal(expectedStatus, result.statusCode);
        Assert.True(JadeValues.IsNull(result.guid));
        Assert.Empty(result.allErrors ?? Array.Empty<string>());
        Assert.False(await AgentExistsAsync(agent.Name));
    }

    /// <summary>
    /// Builds the <c>createTestData</c> fixture through <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which
    /// creates and binds the company "Jade Software Test Company", then creates the test's
    /// <see cref="ErewhonWebServiceAdminClient"/> for the running API. Ports <c>beforeTest</c> and <c>beforeClass</c>.
    /// </summary>
    /// <returns>A task that completes when the fixture and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
        _client = Api.CreateSoapClientFactory().CreateAdminClient();
    }

    /// <summary>
    /// Closes the test's client, aborting it when it is faulted or when closing it fails with a communication or
    /// timeout failure, then deletes the test's companies through the base implementation. Ports <c>afterClass</c>
    /// and <c>afterTest</c>.
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
    /// Returns whether the test's company holds an agent named exactly <paramref name="name"/>, read in a new scope of
    /// the API bound to <see cref="TestCompanyBuilder.MyCompany"/>. Ports <c>app.myCompany.allAgents[name] &lt;&gt; null</c>.
    /// </summary>
    /// <param name="name">The agent name to look up.</param>
    /// <returns><see langword="true"/> when the agent exists.</returns>
    /// <exception cref="InvalidOperationException">The fixture company has not been created.</exception>
    private async Task<bool> AgentExistsAsync(string name)
    {
        Company company = Builder.MyCompany
            ?? throw new InvalidOperationException("The fixture company exists once the test data has been created.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(company.Id);

        Agent? agent = await scope.ServiceProvider.GetRequiredService<AgentService>().FindByNameAsync(name);
        return agent is not null;
    }

    /// <summary>
    /// Closes and releases the test's client: aborts it when it is faulted, otherwise closes it and aborts it when the
    /// close fails with a <see cref="CommunicationException"/> or a <see cref="TimeoutException"/>. Does nothing when
    /// no client exists.
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

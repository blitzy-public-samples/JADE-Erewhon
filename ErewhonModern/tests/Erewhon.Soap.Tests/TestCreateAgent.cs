using System.ServiceModel;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.SoapClient.Admin;
using Erewhon.Testing;

namespace Erewhon.Soap.Tests;

/// <summary>
/// Ports the JADE test class <c>TestCreateAgent</c>: the SOAP operation <c>ErewhonWebServiceAdmin.createAgent</c>, called
/// through the generated <see cref="ErewhonWebServiceAdminClient"/> against the running Erewhon API. Each test starts
/// with the <c>createTestData</c> company bound and ends with no company.
/// </summary>
public sealed class TestCreateAgent : ErewhonTestBase
{
    private const string AgentStreet = "Rua Dom Carlos I 62";
    private const string AgentCity = "Funchal";
    private const string AgentCountry = "Portugal";
    private const string AgentPhone = "291 225 023";
    private const string AgentFax = "291 244 2464";
    private const string AgentEmail = "rowan@funchal.pt";
    private const string AgentWebsite = "www.funchal.pt";

    private ErewhonWebServiceAdminClient? _client;

    private ErewhonWebServiceAdminClient Client =>
        _client ?? throw new InvalidOperationException($"The SOAP client is created by {nameof(SetUpAsync)}.");

    /// <summary>
    /// Creates the <c>createTestData</c> company "Jade Software Test Company" and its fixtures through
    /// <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which binds it, then creates the
    /// <c>ErewhonWebServiceAdmin</c> client for <see cref="ErewhonTestBase.Api"/>. Ports <c>beforeTest</c> and
    /// <c>beforeClass</c>.
    /// </summary>
    /// <returns>A task that completes when the fixtures and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
        _client = Api.CreateSoapClientFactory().CreateAdminClient();
    }

    /// <summary>
    /// Closes the SOAP client, aborting it when it is faulted or its close fails, then deletes the test's companies
    /// through the base tear-down. Ports <c>afterTest</c> and <c>afterClass</c>.
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
    /// <c>createAgent</c> with the name of an agent that already exists answers 409 with exactly one error,
    /// <c>$AgentNameAlreadyExists</c> for that name.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidAgent()
    {
        const string agentName = "Peter Smallsmith";
        const int expectedError = 409;
        const int expectedErrorCount = 1;

        // Arrange
        Address address = Builder.CreateTestAddress(
            AgentStreet, AgentCity, AgentCountry, AgentPhone, AgentFax, AgentEmail, AgentWebsite);
        Agent agent = await Builder.CreateTestAgentAsync(agentName, address);

        // Act
        DTOResult result = await Client.createAgentAsync(
            agentName, AgentStreet, AgentCity, AgentCountry, AgentPhone, AgentFax, AgentEmail, AgentWebsite);

        // Assert
        Assert.Equal(expectedError, result.statusCode);
        Assert.Equal(expectedErrorCount, result.allErrors?.Length ?? 0);

        string expectedMessage = MessageCatalog.Get(MessageKeys.AgentNameAlreadyExists, agent.Name);
        Assert.Equal("Agent Peter Smallsmith already exists", expectedMessage);
        Assert.Equal(expectedMessage, result.allErrors![0]);
    }

    /// <summary>
    /// <c>createAgent</c> with a new name and a valid address answers 201 with the new agent's GUID and no errors.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testValidAgent()
    {
        const string agentName = "Rowan Stracke";
        const int expectedStatus = 201;

        // Act
        DTOResult result = await Client.createAgentAsync(
            agentName, AgentStreet, AgentCity, AgentCountry, AgentPhone, AgentFax, AgentEmail, AgentWebsite);

        // Assert
        Assert.NotNull(result);
        Assert.False(JadeValues.IsNull(result.guid));
        Assert.Equal(0, result.allErrors?.Length ?? 0);
        Assert.Equal(expectedStatus, result.statusCode);
    }

    /// <summary>
    /// Closes and releases the SOAP client when one was created: aborts it when it is faulted, or when closing it fails
    /// with a <see cref="CommunicationException"/> or a <see cref="TimeoutException"/>.
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

using System.ServiceModel;
using Erewhon.SoapClient.WebService;
using Erewhon.Testing;

namespace Erewhon.Soap.Tests;

/// <summary>
/// Port of the JADE test class <c>TestGetAgentNames</c>: calls the SOAP operation <c>getAgentNames</c> of
/// <c>ErewhonWebService</c> through the generated client against the test data of <c>createTestData</c>.
/// </summary>
public sealed class TestGetAgentNames : ErewhonTestBase
{
    private ErewhonWebServiceClient? _client;

    private ErewhonWebServiceClient Client =>
        _client ?? throw new InvalidOperationException($"{nameof(Client)} is available once {nameof(SetUpAsync)} has run.");

    /// <summary>
    /// Creates the test data through <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which binds its company,
    /// then creates the <c>ErewhonWebService</c> client addressed to <see cref="ErewhonTestBase.Api"/>.
    /// </summary>
    /// <returns>A task that completes when the test data and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
        _client = Api.CreateSoapClientFactory().CreateWebServiceClient();
    }

    /// <summary>
    /// Closes the client, aborting it when it is faulted or when closing fails with a
    /// <see cref="CommunicationException"/> or <see cref="TimeoutException"/>, then deletes the test's companies.
    /// </summary>
    /// <returns>A task that completes when the client is closed and no company is left.</returns>
    protected override async Task TearDownAsync()
    {
        try
        {
            if (_client is { } client)
            {
                _client = null;
                await CloseAsync(client);
            }
        }
        finally
        {
            await base.TearDownAsync();
        }
    }

    [Fact]
    public async Task testGetAgentNames()
    {
        const string AgentName = "Peter Smallsmith";
        const string AgentStreet = "Rua Dom Carlos I 62";
        const string AgentCity = "Funchal";
        const string AgentCountry = "Portugal";
        const string AgentPhone = "291 225 023";
        const string AgentFax = "291 244 2464";
        const string AgentEmail = "rowan@funchal.pt";
        const string AgentWebsite = "www.funchal.pt";

        // Arrange
        var address = Builder.CreateTestAddress(
            AgentStreet, AgentCity, AgentCountry, AgentPhone, AgentFax, AgentEmail, AgentWebsite);
        await Builder.CreateTestAgentAsync(AgentName, address);

        // Act
        DTOName[] names = await Client.getAgentNamesAsync();

        // Assert
        Assert.NotNull(names);
        Assert.True(names.Length > 0);
        Assert.Equal(AgentName, names[0].name);
    }

    /// <summary>
    /// Aborts <paramref name="client"/> when it is faulted; otherwise closes it, and aborts it when closing fails with a
    /// <see cref="CommunicationException"/> or <see cref="TimeoutException"/>.
    /// </summary>
    private static async Task CloseAsync(ErewhonWebServiceClient client)
    {
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

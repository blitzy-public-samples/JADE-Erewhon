using System.ServiceModel;
using Erewhon.Application.Domain;
using Erewhon.SoapClient.WebService;
using Erewhon.Testing;

namespace Erewhon.Soap.Tests;

/// <summary>
/// Ports the JADE test class <c>TestGetClientNames</c>: the SOAP operation <c>ErewhonWebService.getClientNames</c>,
/// called through the generated client against the running API.
/// </summary>
public sealed class TestGetClientNames : ErewhonTestBase
{
    private ErewhonWebServiceClient? _webService;

    private ErewhonWebServiceClient WebService =>
        _webService ?? throw new InvalidOperationException($"{nameof(WebService)} is available once {nameof(SetUpAsync)} has run.");

    /// <summary>
    /// Creates the interface test data through <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, then the
    /// <c>ErewhonWebService</c> client addressed to the shared API.
    /// </summary>
    /// <returns>A task that completes when the test data and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
        _webService = Api.CreateSoapClientFactory().CreateWebServiceClient();
    }

    /// <summary>Closes the <c>ErewhonWebService</c> client, then deletes the test's companies.</summary>
    /// <returns>A task that completes when the client is closed and no company is left.</returns>
    protected override async Task TearDownAsync()
    {
        try
        {
            await CloseWebServiceAsync();
        }
        finally
        {
            await base.TearDownAsync();
        }
    }

    [Fact]
    public async Task testGetClientNames()
    {
        const string ClientName = "Dariush Kamran";
        const string ClientStreet = "No. 13, Khorshid Trade Ctr, Ahmad Abad St";
        const string ClientCity = "Mashhad City";
        const string ClientCountry = "Iran";
        const string ClientPhone = "0511 841 4543";
        const string ClientFax = "0511 324 2464";
        const string ClientEmail = "Dariush@iran.ir";
        const string ClientWebsite = "www.iran.ir";

        // Arrange
        Address address = Builder.CreateTestAddress(
            ClientStreet,
            ClientCity,
            ClientCountry,
            ClientPhone,
            ClientFax,
            ClientEmail,
            ClientWebsite);
        await Builder.CreateTestClientAsync(ClientName, address);

        // Act
        DTOName[] names = await WebService.getClientNamesAsync();

        // Assert
        Assert.NotNull(names);
        Assert.True(names.Length > 0);
        Assert.Equal(ClientName, names[0].name);
    }

    /// <summary>
    /// Aborts the client when it is faulted; otherwise closes it, aborting it when the close fails with a
    /// <see cref="CommunicationException"/> or a <see cref="TimeoutException"/>. Does nothing when no client was created.
    /// </summary>
    private async Task CloseWebServiceAsync()
    {
        if (_webService is not { } webService)
        {
            return;
        }

        _webService = null;

        if (webService.State == CommunicationState.Faulted)
        {
            webService.Abort();
            return;
        }

        try
        {
            await webService.CloseAsync();
        }
        catch (CommunicationException)
        {
            webService.Abort();
        }
        catch (TimeoutException)
        {
            webService.Abort();
        }
    }
}

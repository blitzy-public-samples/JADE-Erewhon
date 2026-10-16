using System.ServiceModel;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Services;
using Erewhon.SoapClient.WebService;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Soap.Tests;

/// <summary>
/// Ports the JADE unit-test class <c>TestGetClient</c>: the SOAP operation <c>ErewhonWebService.getClient</c> called
/// through the generated client against the running API, over the data of <c>createTestData</c>.
/// </summary>
public sealed class TestGetClient : ErewhonTestBase
{
    private ErewhonWebServiceClient? _webService;

    private ErewhonWebServiceClient WebService =>
        _webService ?? throw new InvalidOperationException(
            $"The {nameof(ErewhonWebServiceClient)} is available from {nameof(SetUpAsync)} until {nameof(TearDownAsync)}.");

    /// <summary>
    /// Creates the <c>createTestData</c> company and its fixtures, then the <c>ErewhonWebService</c> client. Ports
    /// <c>beforeClass</c> and <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the fixtures and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
        _webService = Api.CreateSoapClientFactory().CreateWebServiceClient();
    }

    /// <summary>
    /// Closes the <c>ErewhonWebService</c> client, aborting it when it is faulted or its close fails, then deletes the
    /// test's companies. Ports <c>afterTest</c> and <c>afterClass</c>.
    /// </summary>
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
    public async Task testInvalidClient()
    {
        const string ClientName = "Some Client That Does Not Exist";

        DTOClient dtoClient = await WebService.getClientAsync(ClientName);

        Assert.Null(dtoClient);
    }

    [Fact]
    public async Task testValidClient()
    {
        const string ClientName = "Sarah Bailey";
        const string ClientStreet = "17 Lake Crescent";
        const string ClientCity = "Queenstown";
        const string ClientCountry = "New Zealand";
        const string ClientPhone = "64 3 838 2828";
        const string ClientFax = "64 3 845 1929";
        const string ClientEmail = "sarah@smallfoot.com";

        // The JADE null String.
        const string ClientWebsite = "";

        Address address = Builder.CreateTestAddress(
            ClientStreet, ClientCity, ClientCountry, ClientPhone, ClientFax, ClientEmail, ClientWebsite);
        await Builder.CreateTestClientAsync(ClientName, address);

        Assert.True(await ClientExistsAsync(ClientName));

        DTOClient dtoClient = await WebService.getClientAsync(ClientName);

        Assert.NotNull(dtoClient);
        Assert.Equal(ClientName, dtoClient.name);
        Assert.Equal(ClientStreet, dtoClient.street);
        Assert.Equal(ClientCity, dtoClient.city);
        Assert.Equal(ClientCountry, dtoClient.country);
        Assert.Equal(ClientPhone, dtoClient.phone);
        Assert.Equal(ClientFax, dtoClient.fax);
        Assert.Equal(ClientEmail, dtoClient.email);
        Assert.True(JadeValues.IsNull(dtoClient.website));
    }

    /// <summary>
    /// Returns whether a client named <paramref name="name"/> is stored in <see cref="TestCompanyBuilder.MyCompany"/>,
    /// read in a new scope of <see cref="ErewhonTestBase.Api"/>. Ports <c>app.myCompany.allClients[name] &lt;&gt; null</c>.
    /// </summary>
    /// <param name="name">The client name, matched exactly.</param>
    /// <returns><see langword="true"/> when the client exists.</returns>
    private async Task<bool> ClientExistsAsync(string name)
    {
        Company company = Builder.MyCompany
            ?? throw new InvalidOperationException($"{nameof(TestCompanyBuilder.MyCompany)} is not set.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(company.Id);

        return await scope.ServiceProvider.GetRequiredService<ClientService>().FindByNameAsync(name) is not null;
    }

    /// <summary>
    /// Closes the <c>ErewhonWebService</c> client and forgets it: aborts it when it is faulted, or when closing it
    /// raises a <see cref="CommunicationException"/> or a <see cref="TimeoutException"/>. Does nothing when no client
    /// exists.
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

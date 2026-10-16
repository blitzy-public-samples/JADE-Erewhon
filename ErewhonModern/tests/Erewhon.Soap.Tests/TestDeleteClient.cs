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
/// Ports the JADE <c>TestDeleteClient</c> tests of the SOAP operation <c>ErewhonWebServiceAdmin.deleteClient</c>,
/// called through the generated <see cref="ErewhonWebServiceAdminClient"/> against the API on Kestrel. Each test
/// starts with the <c>createTestData</c> company and ends with no company.
/// </summary>
public sealed class TestDeleteClient : ErewhonTestBase
{
    private ErewhonWebServiceAdminClient? _webServiceAdmin;

    /// <summary>The SOAP Admin client of the test. Ports <c>myWebServiceAdmin</c>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created the client.</exception>
    private ErewhonWebServiceAdminClient WebServiceAdmin =>
        _webServiceAdmin ?? throw new InvalidOperationException(
            $"{nameof(WebServiceAdmin)} is available once {nameof(SetUpAsync)} has run.");

    /// <summary>
    /// Deleting a client name that no client holds returns status 404 with the single error
    /// <c>$ClientNameNotFound</c> for that name.
    /// </summary>
    [Fact]
    public async Task testInvalidClient()
    {
        const string ClientName = "Rowan Stracke";
        const int ExpectedError = 404;

        // Arrange
        Assert.False(await ClientExistsAsync(ClientName));

        // Act
        DTOResult dtoResult = await WebServiceAdmin.deleteClientAsync(ClientName);

        // Verify
        Assert.Equal(ExpectedError, dtoResult.statusCode);
        string error = Assert.Single(dtoResult.allErrors);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ClientNameNotFound, ClientName), error);
        Assert.Equal("Client Rowan Stracke not found", error);
    }

    /// <summary>
    /// Deleting an existing client returns status 200 with no errors and no GUID, and the client's name is no longer
    /// found.
    /// </summary>
    [Fact]
    public async Task testValidClient()
    {
        const string ClientName = "Rowan Stracke";
        const string ClientStreet = "Rua Dom Carlos I 62";
        const string ClientCity = "Funchal";
        const string ClientCountry = "Portugal";
        const string ClientPhone = "291 225 023";
        const string ClientFax = "291 244 2464";
        const string ClientEmail = "rowan@funchal.pt";
        const string ClientWebsite = "www.funchal.pt";
        const int ExpectedStatus = 200;

        // Arrange
        Address address = Builder.CreateTestAddress(
            ClientStreet, ClientCity, ClientCountry, ClientPhone, ClientFax, ClientEmail, ClientWebsite);
        Client client = await Builder.CreateTestClientAsync(ClientName, address);

        Assert.True(await ClientExistsAsync(client.Name));

        // Act
        DTOResult dtoResult = await WebServiceAdmin.deleteClientAsync(client.Name);

        // Verify
        Assert.NotNull(dtoResult);
        Assert.Equal(0, dtoResult.allErrors?.Length ?? 0);
        Assert.Equal(ExpectedStatus, dtoResult.statusCode);
        Assert.True(JadeValues.IsNull(dtoResult.guid));
        Assert.False(await ClientExistsAsync(client.Name));
    }

    /// <summary>
    /// Creates the <c>createTestData</c> company, bound as the test's company, then the SOAP Admin client addressed to
    /// <see cref="ErewhonTestBase.Api"/>. Ports <c>beforeTest</c> and <c>beforeClass</c>.
    /// </summary>
    /// <returns>A task that completes when the test data and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
        _webServiceAdmin = Api.CreateSoapClientFactory().CreateAdminClient();
    }

    /// <summary>
    /// Closes the SOAP Admin client, aborting it when it is faulted or its close fails, then deletes the test's
    /// companies. Ports <c>afterTest</c> and <c>afterClass</c>.
    /// </summary>
    /// <returns>A task that completes when the client is closed and no company is left.</returns>
    protected override async Task TearDownAsync()
    {
        try
        {
            await CloseWebServiceAdminAsync();
        }
        finally
        {
            await base.TearDownAsync();
        }
    }

    /// <summary>
    /// Returns whether the test's company holds a client named <paramref name="name"/>, read in a new scope of
    /// <see cref="ErewhonTestBase.Api"/>. Ports <c>app.myCompany.allClients[name] &lt;&gt; null</c>.
    /// </summary>
    /// <param name="name">The client name, used as given.</param>
    /// <returns><see langword="true"/> when the client exists.</returns>
    /// <exception cref="InvalidOperationException">The test's company was not created.</exception>
    private async Task<bool> ClientExistsAsync(string name)
    {
        long companyId = Builder.MyCompany?.Id
            ?? throw new InvalidOperationException("The test company has not been created.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(companyId);

        Client? client = await scope.ServiceProvider.GetRequiredService<ClientService>().FindByNameAsync(name);
        return client is not null;
    }

    /// <summary>
    /// Closes the SOAP Admin client and forgets it; aborts it instead when it is faulted, or when the close raises a
    /// <see cref="CommunicationException"/> or a <see cref="TimeoutException"/>. Does nothing when no client exists.
    /// </summary>
    private async Task CloseWebServiceAdminAsync()
    {
        if (_webServiceAdmin is not { } webServiceAdmin)
        {
            return;
        }

        _webServiceAdmin = null;

        if (webServiceAdmin.State == CommunicationState.Faulted)
        {
            webServiceAdmin.Abort();
            return;
        }

        try
        {
            await webServiceAdmin.CloseAsync();
        }
        catch (CommunicationException)
        {
            webServiceAdmin.Abort();
        }
        catch (TimeoutException)
        {
            webServiceAdmin.Abort();
        }
    }
}

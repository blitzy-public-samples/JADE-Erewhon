using System.ServiceModel;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.SoapClient.WebService;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Soap.Tests;

/// <summary>
/// Ports the JADE test class <c>TestUpdateClient</c>: the SOAP operation <c>ErewhonWebService.updateClient</c>, called
/// through the generated <see cref="ErewhonWebServiceClient"/> against the test assembly's API, over the
/// <c>createTestData</c> fixture.
/// </summary>
public sealed class TestUpdateClient : ErewhonTestBase
{
    private ErewhonWebServiceClient? _webService;

    private ErewhonWebServiceClient WebService =>
        _webService ?? throw new InvalidOperationException(
            $"The {nameof(ErewhonWebServiceClient)} is available from {nameof(SetUpAsync)} until {nameof(TearDownAsync)}.");

    [Fact]
    public async Task testInvalidClient()
    {
        const string clientName = "Client Name Does Not Exist";
        const string newStreet = "1477 Lower Water Street";
        const string newCity = "Halifax";
        const string newCountry = "Canada";
        const string newPhone = "(902) 423-6266";
        const string newFax = "(902) 423-4676";
        const string newEmail = "pauline@halifax.ca";
        const string newWebsite = "www.halifax.ca";
        const int expectedError = 404;

        Client fixtureClient = Builder.MyClient
            ?? throw new InvalidOperationException($"{nameof(TestCompanyBuilder.MyClient)} is not set.");
        Client? snapshot = await ReadClientAsync(fixtureClient.Id);
        Assert.NotNull(snapshot);

        DTOResult result = await WebService.updateClientAsync(
            clientName, newStreet, newCity, newCountry, newPhone, newFax, newEmail, newWebsite);

        Assert.Equal(expectedError, result.statusCode);
        Assert.NotNull(result.allErrors);

        // Checks allErrors.size() = 1 and returns allErrors[1] (D-140).
        string error = Assert.Single(result.allErrors);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ClientNameNotFound, clientName), error);
        Assert.Equal("Client Client Name Does Not Exist not found", error);
        Assert.True(JadeValues.IsNull(result.guid));

        Assert.False(await ClientExistsAsync(clientName));

        Client? stored = await ReadClientAsync(fixtureClient.Id);
        Assert.NotNull(stored);
        Assert.Equal(snapshot.Name, stored.Name);
        Assert.Equal(snapshot.Guid, stored.Guid);
        Assert.Equal(snapshot.Address.Street, stored.Address.Street);
        Assert.Equal(snapshot.Address.City, stored.Address.City);
        Assert.Equal(snapshot.Address.Country, stored.Address.Country);
        Assert.Equal(snapshot.Address.Phone, stored.Address.Phone);
        Assert.Equal(snapshot.Address.Fax, stored.Address.Fax);
        Assert.Equal(snapshot.Address.Email, stored.Address.Email);
        Assert.Equal(snapshot.Address.WebSite, stored.Address.WebSite);
    }

    [Fact]
    public async Task testValidClient()
    {
        const string clientName = "Pauline Wild";
        const string newStreet = "1477 Lower Water Street";
        const string newCity = "Halifax";
        const string newCountry = "Canada";
        const string newPhone = "(902) 423-6266";
        const string newFax = "(902) 423-4676";
        const string newEmail = "pauline@halifax.ca";
        const string newWebsite = "www.halifax.ca";
        const int expectedStatus = 200;

        var address = Builder.CreateTestAddress(
            "555 Fake St.",
            "Dunedin",
            "New Zealand",
            "555 5555555",
            "555 4444444",
            "CliEnt@E.mail",
            "www.website.com");
        Client client = await Builder.CreateTestClientAsync(clientName, address);

        Assert.True(await ClientExistsAsync(clientName));

        DTOResult result = await WebService.updateClientAsync(
            clientName, newStreet, newCity, newCountry, newPhone, newFax, newEmail, newWebsite);

        Assert.Equal(expectedStatus, result.statusCode);
        Assert.False(JadeValues.IsNull(result.guid));
        Assert.Equal(client.Guid, result.guid);

        Client? stored = await ReadClientAsync(client.Id);
        Assert.NotNull(stored);
        Assert.Equal(clientName, stored.Name);
        Assert.Equal(client.Guid, stored.Guid);
        Assert.Equal(newStreet, stored.Address.Street);
        Assert.Equal(newCity, stored.Address.City);
        Assert.Equal(newCountry, stored.Address.Country);
        Assert.Equal(newPhone, stored.Address.Phone);
        Assert.Equal(newFax, stored.Address.Fax);
        Assert.Equal(newEmail, stored.Address.Email);
        Assert.Equal(newWebsite, stored.Address.WebSite);
    }

    /// <summary>
    /// Builds the <c>createTestData</c> fixture, binding its company, and creates the
    /// <see cref="ErewhonWebServiceClient"/> addressed to the test assembly's API. Ports <c>beforeClass</c> and
    /// <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the fixture and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
        _webService = Api.CreateSoapClientFactory().CreateWebServiceClient();
    }

    /// <summary>
    /// Closes the <see cref="ErewhonWebServiceClient"/>, or aborts it when it is faulted or cannot close, then deletes
    /// the test's companies. Ports <c>afterTest</c> and <c>afterClass</c>.
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

    /// <summary>
    /// Returns whether the company of <see cref="TestCompanyBuilder.MyCompany"/> holds a client named
    /// <paramref name="name"/>, read in a new scope of the API. Ports <c>app.myCompany.allClients[name] &lt;&gt; null</c>.
    /// </summary>
    /// <param name="name">The client name, matched exactly.</param>
    /// <returns><see langword="true"/> when the client exists.</returns>
    private async Task<bool> ClientExistsAsync(string name)
    {
        long companyId = Builder.MyCompany?.Id
            ?? throw new InvalidOperationException($"{nameof(TestCompanyBuilder.MyCompany)} is not set.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(companyId);

        return await scope.ServiceProvider.GetRequiredService<ClientService>().FindByNameAsync(name) is not null;
    }

    /// <summary>
    /// Returns the client with row id <paramref name="id"/>, with its address, read through
    /// <see cref="ClientService.FindByIdAsync"/> in a new scope of the API bound to the company of
    /// <see cref="TestCompanyBuilder.MyCompany"/>.
    /// </summary>
    /// <param name="id">The row id of the client.</param>
    /// <returns>The stored client, or <see langword="null"/> when no client has that id.</returns>
    private async Task<Client?> ReadClientAsync(long id)
    {
        long companyId = Builder.MyCompany?.Id
            ?? throw new InvalidOperationException($"{nameof(TestCompanyBuilder.MyCompany)} is not set.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(companyId);

        return await scope.ServiceProvider.GetRequiredService<ClientService>().FindByIdAsync(id);
    }

    /// <summary>
    /// Aborts the client when it is faulted; otherwise closes it, aborting it when the close fails with a
    /// <see cref="CommunicationException"/> or a <see cref="TimeoutException"/>. Does nothing when no client exists.
    /// </summary>
    /// <returns>A task that completes when the client is closed or aborted.</returns>
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

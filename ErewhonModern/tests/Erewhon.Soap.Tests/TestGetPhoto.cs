using System.ServiceModel;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Erewhon.Application.Services;
using Erewhon.SoapClient.WebService;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Soap.Tests;

/// <summary>
/// Ports the JADE <c>TestGetPhoto</c> unit tests of the SOAP <c>ErewhonWebService.getPhoto</c> operation, called over
/// HTTP through the generated <see cref="ErewhonWebServiceClient"/>.
/// </summary>
public sealed class TestGetPhoto : ErewhonTestBase
{
    private ErewhonWebServiceClient? _webService;

    private ErewhonWebServiceClient WebService =>
        _webService ?? throw new InvalidOperationException(
            $"The {nameof(ErewhonWebServiceClient)} is available from {nameof(SetUpAsync)} until {nameof(TearDownAsync)}.");

    [Fact]
    public async Task testClientWithNoPhoto()
    {
        const string Client_Name = "Dariush Kamran";
        const string Client_Street = "No. 13, Khorshid Trade Ctr, Ahmad Abad St";
        const string Client_City = "Mashhad City";
        const string Client_Country = "Iran";
        const string Client_Phone = "0511 841 4543";
        const string Client_Fax = "0511 324 2464";
        const string Client_Email = "Dariush@iran.ir";
        const string Client_Website = "www.iran.ir";

        // Arrange
        Address address = Builder.CreateTestAddress(
            Client_Street,
            Client_City,
            Client_Country,
            Client_Phone,
            Client_Fax,
            Client_Email,
            Client_Website);
        await Builder.CreateTestClientAsync(Client_Name, address);

        Assert.True(await ClientExistsAsync(Client_Name));

        // Act
        DTOPhoto dtoPhoto = await WebService.getPhotoAsync(Client_Name);

        // Verify
        Assert.NotNull(dtoPhoto);
        Assert.True(dtoPhoto.photo is null || dtoPhoto.photo.Length == 0);
    }

    [Fact]
    public async Task testClientWithPhoto()
    {
        // Arrange
        Client myClient = Builder.MyClient
            ?? throw new InvalidOperationException("The test data holds no client.");

        // Act
        DTOPhoto dtoPhoto = await WebService.getPhotoAsync(myClient.Name);

        // Verify
        Assert.NotNull(dtoPhoto);
        Assert.NotNull(dtoPhoto.photo);
        Assert.NotEmpty(dtoPhoto.photo);
        Assert.Equal(TestPhoto.ToArray(), dtoPhoto.photo);
    }

    [Fact]
    public async Task testInvalidClientName()
    {
        const string Client_Name = "Invalid Client";

        // Act
        DTOPhoto dtoPhoto = await WebService.getPhotoAsync(Client_Name);

        // Verify
        Assert.Null(dtoPhoto);
    }

    /// <summary>
    /// Creates the <c>createTestData</c> fixtures, which bind their company, then the <c>ErewhonWebService</c> client
    /// addressed to <see cref="ErewhonTestBase.Api"/>.
    /// </summary>
    /// <returns>A task that completes when the fixtures and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
        _webService = Api.CreateSoapClientFactory().CreateWebServiceClient();
    }

    /// <summary>
    /// Closes the <c>ErewhonWebService</c> client, aborting it when it is faulted or its close fails, then deletes the
    /// test's companies.
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
    /// Returns whether the company <see cref="TestCompanyBuilder.MyCompany"/> holds a client named
    /// <paramref name="name"/>, read in a new scope of <see cref="ErewhonTestBase.Api"/>. Ports
    /// <c>app.myCompany.allClients[name] &lt;&gt; null</c>.
    /// </summary>
    private async Task<bool> ClientExistsAsync(string name)
    {
        Company myCompany = Builder.MyCompany
            ?? throw new InvalidOperationException("The test data holds no company.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(myCompany.Id);

        return await scope.ServiceProvider.GetRequiredService<ClientService>().FindByNameAsync(name) is not null;
    }

    /// <summary>
    /// Closes the client and clears it; aborts it instead when it is faulted, or when the close fails with a
    /// <see cref="CommunicationException"/> or a <see cref="TimeoutException"/>. Does nothing when no client exists.
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

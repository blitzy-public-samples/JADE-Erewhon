using System.ServiceModel;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.SoapClient.WebService;
using Erewhon.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Soap.Tests;

/// <summary>
/// Ports the JADE test class <c>TestUpdateClientWithDTO</c>: the SOAP operation <c>updateClientWithDTO</c> of
/// <c>ErewhonWebService</c>, called through the generated client against the test assembly's API. Each test starts with
/// the <c>createTestData</c> company and a new web service client, and ends with the client closed and no company.
/// </summary>
public sealed class TestUpdateClientWithDTO : ErewhonTestBase
{
    private ErewhonWebServiceClient? _webService;

    private ErewhonWebServiceClient WebService =>
        _webService ?? throw new InvalidOperationException(
            $"{nameof(WebService)} is available once {nameof(SetUpAsync)} has run.");

    [Fact]
    public async Task testInvalidClient()
    {
        const string NewClientName = "Some Agent That Does Not Exist";
        const string NewStreet = "Max-Planck-Strasse 36";
        const string NewCity = "Friedrichsdorf";
        const string NewCountry = "Germany";
        const string NewPhone = "\t06172 95330";
        const string NewFax = "\t06172 01264";
        const string NewEmail = "roger@friedrichsdorf.de";
        const string NewWebsite = "www.Friedrichsdorf.de";
        const int ExpectedError = 404;
        const int ExpectedErrorCount = 1;

        var dtoClient = new DTOClient
        {
            name = NewClientName,
            street = NewStreet,
            city = NewCity,
            country = NewCountry,
            phone = NewPhone,
            fax = NewFax,
            email = NewEmail,
            website = NewWebsite,
        };

        DTOResult dtoResult = await WebService.updateClientWithDTOAsync(dtoClient);

        Assert.Equal(ExpectedError, dtoResult.statusCode);
        Assert.Equal(ExpectedErrorCount, dtoResult.allErrors?.Length ?? 0);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ClientNameNotFound, NewClientName), dtoResult.allErrors![0]);
        Assert.Equal("Client Some Agent That Does Not Exist not found", dtoResult.allErrors[0]);
    }

    [Fact]
    public async Task testValidClient()
    {
        const string ClientName = "Roger Boeing";
        const string NewStreet = "Max-Planck-Strasse 36";
        const string NewCity = "Friedrichsdorf";
        const string NewCountry = "Germany";
        const string NewPhone = "06172 95330";
        const string NewFax = "06172 01264";
        const string NewEmail = "roger@friedrichsdorf.de";
        const string NewWebsite = "www.Friedrichsdorf.de";
        const int ExpectedStatus = 200;

        await Builder.CreateTestClientAsync(
            ClientName,
            Builder.CreateTestAddress(
                "555 Fake St.",
                "Dunedin",
                "New Zealand",
                "555 5555555",
                "555 4444444",
                "CliEnt@E.mail",
                "www.website.com"));

        Assert.True(await ClientExistsAsync(ClientName));

        var dtoClient = new DTOClient
        {
            name = ClientName,
            street = NewStreet,
            city = NewCity,
            country = NewCountry,
            phone = NewPhone,
            fax = NewFax,
            email = NewEmail,
            website = NewWebsite,
        };

        DTOResult dtoResult = await WebService.updateClientWithDTOAsync(dtoClient);

        Assert.NotNull(dtoResult);
        Assert.False(JadeValues.IsNull(dtoResult.guid));
        Assert.Equal(0, dtoResult.allErrors?.Length ?? 0);
        Assert.Equal(ExpectedStatus, dtoResult.statusCode);
    }

    /// <summary>
    /// Builds the <c>createTestData</c> company through <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which sets
    /// and binds <see cref="TestCompanyBuilder.MyCompany"/>, then creates the <c>ErewhonWebService</c> client addressed to
    /// <see cref="ErewhonTestBase.Api"/>.
    /// </summary>
    /// <returns>A task that completes when the company and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
        _webService = Api.CreateSoapClientFactory().CreateWebServiceClient();
    }

    /// <summary>
    /// Closes the <c>ErewhonWebService</c> client, aborting it when it is faulted or its close fails, then deletes the
    /// test's companies through the base implementation, which runs even when the close throws.
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
    /// <paramref name="name"/>, read in a new scope of <see cref="ErewhonTestBase.Api"/> bound to that company.
    /// Ports <c>app.myCompany.allClients[name] &lt;&gt; null</c>.
    /// </summary>
    /// <param name="name">The client name, used as given.</param>
    /// <returns><see langword="true"/> when the client exists.</returns>
    /// <exception cref="InvalidOperationException"><see cref="TestCompanyBuilder.MyCompany"/> is not set.</exception>
    private async Task<bool> ClientExistsAsync(string name)
    {
        long companyId = Builder.MyCompany?.Id
            ?? throw new InvalidOperationException(
                $"{nameof(ClientExistsAsync)} reads the company of {nameof(TestCompanyBuilder.CreateTestDataAsync)}, which is not set.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(companyId);
        return await scope.ServiceProvider.GetRequiredService<ClientService>().FindByNameAsync(name) is not null;
    }

    /// <summary>
    /// Closes the <c>ErewhonWebService</c> client when one exists and forgets it. A faulted client is aborted; a
    /// <see cref="CommunicationException"/> or <see cref="TimeoutException"/> from the close aborts it.
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

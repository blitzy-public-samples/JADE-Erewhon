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
/// Tests of the SOAP operation <c>ErewhonWebService.updateAgent</c>, called through the generated
/// <see cref="ErewhonWebServiceClient"/> on the API of the test assembly. Each test starts with the data of
/// <see cref="TestCompanyBuilder.CreateTestDataAsync"/>. Ports the JADE class <c>TestUpdateAgent</c>.
/// </summary>
public sealed class TestUpdateAgent : ErewhonTestBase
{
    private ErewhonWebServiceClient? _webService;

    /// <summary>The <c>ErewhonWebService</c> client of the running test.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> or after <see cref="TearDownAsync"/>.</exception>
    private ErewhonWebServiceClient WebService =>
        _webService ?? throw new InvalidOperationException(
            $"{nameof(WebService)} is available from {nameof(SetUpAsync)} until {nameof(TearDownAsync)}.");

    /// <summary>
    /// Updates an agent that does not exist: the result has status 404 and no GUID.
    /// Ports <c>TestUpdateAgent.testInvalidAgent</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidAgent()
    {
        const string AgentName = "Some Agent That Does Not Exist";
        const string NewStreet = "1, TVS School Road";
        const string NewCity = "Tamil Nadu";
        const string NewCountry = "India";
        const string NewPhone = "044 2476 4393";
        const string NewFax = "044 2476 7364";
        const string NewEmail = "tabai@tamil.in";
        const string NewWebsite = "www.tamil.in";
        const int ExpectedError = 404;

        DTOResult result = await WebService.updateAgentAsync(
            AgentName, NewStreet, NewCity, NewCountry, NewPhone, NewFax, NewEmail, NewWebsite);

        Assert.NotNull(result);
        Assert.Equal(ExpectedError, result.statusCode);
        Assert.True(JadeValues.IsNull(result.guid));
    }

    /// <summary>
    /// Creates the agent "Tabai Tanivula" at 314 Nohwere Lane, Christchurch, then updates its address to
    /// 1, TVS School Road, Tamil Nadu: the result has status 200 and a GUID.
    /// Ports <c>TestUpdateAgent.testValidAgent</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testValidAgent()
    {
        const string AgentName = "Tabai Tanivula";
        const string NewStreet = "1, TVS School Road";
        const string NewCity = "Tamil Nadu";
        const string NewCountry = "India";
        const string NewPhone = "044 2476 4393";
        const string NewFax = "044 2476 7364";
        const string NewEmail = "tabai@tamil.in";
        const string NewWebsite = "www.tamil.in";
        const int ExpectedStatus = 200;

        Address address = Builder.CreateTestAddress(
            "314 Nohwere Lane",
            "Christchurch",
            "New Zealand",
            "555 1234567",
            "FAX",
            "admin@Erewhon.nz",
            "www.jadeworld.com");
        await Builder.CreateTestAgentAsync(AgentName, address);

        Assert.True(await AgentExistsAsync(AgentName));

        DTOResult result = await WebService.updateAgentAsync(
            AgentName, NewStreet, NewCity, NewCountry, NewPhone, NewFax, NewEmail, NewWebsite);

        Assert.NotNull(result);
        Assert.Equal(ExpectedStatus, result.statusCode);
        Assert.False(JadeValues.IsNull(result.guid));
    }

    /// <summary>
    /// Creates the test data through <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which creates and binds the
    /// company, then creates the <c>ErewhonWebService</c> client. Ports <c>beforeClass</c> and <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the test data and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
        _webService = Api.CreateSoapClientFactory().CreateWebServiceClient();
    }

    /// <summary>
    /// Closes the <c>ErewhonWebService</c> client, or aborts it when it is faulted or its close fails with a
    /// <see cref="CommunicationException"/> or <see cref="TimeoutException"/>, then deletes the test's companies.
    /// Ports <c>afterTest</c> and <c>afterClass</c>.
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
    /// Returns whether the company <see cref="TestCompanyBuilder.MyCompany"/> holds an agent named
    /// <paramref name="name"/>, read through <see cref="AgentService.FindByNameAsync"/> in a new scope of
    /// <see cref="ErewhonTestBase.Api"/> bound to that company.
    /// </summary>
    /// <param name="name">The exact agent name.</param>
    /// <returns><see langword="true"/> when the agent exists.</returns>
    /// <exception cref="InvalidOperationException"><see cref="TestCompanyBuilder.MyCompany"/> is not set.</exception>
    private async Task<bool> AgentExistsAsync(string name)
    {
        Company company = Builder.MyCompany
            ?? throw new InvalidOperationException($"{nameof(TestCompanyBuilder.MyCompany)} is not set.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(company.Id);

        return await scope.ServiceProvider.GetRequiredService<AgentService>().FindByNameAsync(name) is not null;
    }

    /// <summary>
    /// Closes the client when it was created and clears it; a faulted client, or one whose close fails with a
    /// <see cref="CommunicationException"/> or <see cref="TimeoutException"/>, is aborted instead.
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

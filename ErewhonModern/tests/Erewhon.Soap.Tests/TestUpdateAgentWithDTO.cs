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
/// Ports the JADE test class <c>TestUpdateAgentWithDTO</c>: calls the SOAP operation <c>updateAgentWithDTO</c> of
/// <c>ErewhonWebService</c> through the generated client, over the test data of <c>createTestData</c>.
/// </summary>
public sealed class TestUpdateAgentWithDTO : ErewhonTestBase
{
    private ErewhonWebServiceClient? _webService;

    private ErewhonWebServiceClient WebService =>
        _webService ?? throw new InvalidOperationException("The ErewhonWebService client exists once the set-up has run.");

    /// <summary>
    /// Creates the test data through <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, then the
    /// <c>ErewhonWebService</c> client addressed to the test host.
    /// </summary>
    /// <returns>A task that completes when the test data and the client exist.</returns>
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

    [Fact]
    public async Task testInvalidAgent()
    {
        const string NewAgentName = "Some Agent That Does Not Exist";
        const string AgentStreet = "1, TVS School Road";
        const string AgentCity = "Tamil Nadu";
        const string AgentCountry = "India";
        const string AgentPhone = "044 2476 4393";
        const string AgentFax = "044 2476 7364";
        const string AgentEmail = "tabai@tamil.in";
        const string AgentWebsite = "www.tamil.in";

        const int ExpectedError = 404;
        const int ExpectedErrorCount = 1;

        // Arrange
        var dtoAgent = new DTOAgent
        {
            name = NewAgentName,
            street = AgentStreet,
            city = AgentCity,
            country = AgentCountry,
            phone = AgentPhone,
            fax = AgentFax,
            email = AgentEmail,
            website = AgentWebsite,
        };
        Agent fixtureAgent = Builder.MyAgent
            ?? throw new InvalidOperationException("The test data holds no agent.");
        Agent? snapshot = await ReadAgentAsync(fixtureAgent.Id);
        Assert.NotNull(snapshot);

        // Act
        DTOResult dtoResult = await WebService.updateAgentWithDTOAsync(dtoAgent);

        // Verify
        Assert.Equal(ExpectedError, dtoResult.statusCode);
        Assert.Equal(ExpectedErrorCount, dtoResult.allErrors?.Length ?? 0);
        Assert.Equal(MessageCatalog.Get(MessageKeys.AgentNameNotFound, NewAgentName), dtoResult.allErrors![0]);
        Assert.Equal("Agent Some Agent That Does Not Exist not found", dtoResult.allErrors[0]);
        Assert.True(JadeValues.IsNull(dtoResult.guid));

        Assert.False(await AgentExistsAsync(NewAgentName));

        Agent? stored = await ReadAgentAsync(fixtureAgent.Id);
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
    public async Task testValidAgent()
    {
        const string NewStreet = "238 Ness Drive";
        const string NewCity = "Shaldon";
        const string NewCountry = "United Kingdon";
        const string NewPhone = "01626 873480";
        const string NewFax = "01626 476764";
        const string NewEmail = "tabai@shaldon.uk";
        const string NewWebsite = "www.shaldon.uk";
        const int ExpectedStatus = 200;

        // Arrange
        Agent agent = Builder.MyAgent
            ?? throw new InvalidOperationException("The test data holds no agent.");
        string agentName = agent.Name;
        var dtoAgent = new DTOAgent
        {
            name = agentName,
            street = NewStreet,
            city = NewCity,
            country = NewCountry,
            phone = NewPhone,
            fax = NewFax,
            email = NewEmail,
            website = NewWebsite,
        };

        // Act
        DTOResult dtoResult = await WebService.updateAgentWithDTOAsync(dtoAgent);

        // Verify
        Assert.NotNull(dtoResult);
        Assert.False(JadeValues.IsNull(dtoResult.guid));
        Assert.Equal(ExpectedStatus, dtoResult.statusCode);
        Assert.Equal(agent.Guid, dtoResult.guid);

        Agent? stored = await ReadAgentAsync(agent.Id);
        Assert.NotNull(stored);
        Assert.Equal(agentName, stored.Name);
        Assert.Equal(agent.Guid, stored.Guid);
        Assert.Equal(NewStreet, stored.Address.Street);
        Assert.Equal(NewCity, stored.Address.City);
        Assert.Equal(NewCountry, stored.Address.Country);
        Assert.Equal(NewPhone, stored.Address.Phone);
        Assert.Equal(NewFax, stored.Address.Fax);
        Assert.Equal(NewEmail, stored.Address.Email);
        Assert.Equal(NewWebsite, stored.Address.WebSite);
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
            ?? throw new InvalidOperationException("The test data holds no company.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(company.Id);

        return await scope.ServiceProvider.GetRequiredService<AgentService>().FindByNameAsync(name) is not null;
    }

    /// <summary>
    /// Returns the agent with internal id <paramref name="id"/>, with its address, read through
    /// <see cref="AgentService.FindByIdAsync"/> in a new scope of <see cref="ErewhonTestBase.Api"/> bound to the
    /// company <see cref="TestCompanyBuilder.MyCompany"/>.
    /// </summary>
    /// <param name="id">The internal id (<c>agents.id</c>) of the agent.</param>
    /// <returns>The stored agent, or <see langword="null"/> when no agent has that id.</returns>
    /// <exception cref="InvalidOperationException"><see cref="TestCompanyBuilder.MyCompany"/> is not set.</exception>
    private async Task<Agent?> ReadAgentAsync(long id)
    {
        Company company = Builder.MyCompany
            ?? throw new InvalidOperationException("The test data holds no company.");

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICompanyContext>().Bind(company.Id);

        return await scope.ServiceProvider.GetRequiredService<AgentService>().FindByIdAsync(id);
    }

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

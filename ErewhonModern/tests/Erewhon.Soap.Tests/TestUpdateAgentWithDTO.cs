using System.ServiceModel;
using Erewhon.Application.Common;
using Erewhon.Application.Messages;
using Erewhon.SoapClient.WebService;
using Erewhon.Testing;

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

        // Act
        DTOResult dtoResult = await WebService.updateAgentWithDTOAsync(dtoAgent);

        // Assert
        Assert.Equal(ExpectedError, dtoResult.statusCode);
        Assert.Equal(ExpectedErrorCount, dtoResult.allErrors?.Length ?? 0);
        Assert.Equal(MessageCatalog.Get(MessageKeys.AgentNameNotFound, NewAgentName), dtoResult.allErrors![0]);
        Assert.Equal("Agent Some Agent That Does Not Exist not found", dtoResult.allErrors[0]);
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
        string agentName = Builder.MyAgent?.Name
            ?? throw new InvalidOperationException("The test data holds no agent.");
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

        // Assert
        Assert.NotNull(dtoResult);
        Assert.False(JadeValues.IsNull(dtoResult.guid));
        Assert.Equal(ExpectedStatus, dtoResult.statusCode);
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

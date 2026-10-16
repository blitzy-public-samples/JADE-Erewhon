using System.ServiceModel;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.SoapClient.Admin;
using Erewhon.Testing;

namespace Erewhon.Soap.Tests;

/// <summary>
/// Tests of the SOAP operation <c>ErewhonWebServiceAdmin.createClient</c>, called through the generated
/// <see cref="ErewhonWebServiceAdminClient"/>. Ports the JADE class <c>TestCreateClient</c>.
/// </summary>
public sealed class TestCreateClient : ErewhonTestBase
{
    private const string ClientName = "Dariush Kamran";
    private const string ClientStreet = "No. 13, Khorshid Trade Ctr, Ahmad Abad St";
    private const string ClientCity = "Mashhad City";
    private const string ClientCountry = "Iran";
    private const string ClientPhone = "0511 841 4543";
    private const string ClientFax = "0511 324 2464";
    private const string ClientEmail = "Dariush@iran.ir";
    private const string ClientWebsite = "www.iran.ir";

    private ErewhonWebServiceAdminClient? _client;

    /// <summary>The Admin SOAP client created by <see cref="SetUpAsync"/>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> or after <see cref="TearDownAsync"/>.</exception>
    private ErewhonWebServiceAdminClient AdminClient =>
        _client ?? throw new InvalidOperationException(
            $"{nameof(AdminClient)} is available from {nameof(SetUpAsync)} until {nameof(TearDownAsync)}.");

    /// <summary>
    /// Creates the test data of <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, then the Admin SOAP client of
    /// the test host. Ports <c>beforeClass</c> and <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the test data and the client exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
        _client = Api.CreateSoapClientFactory().CreateAdminClient();
    }

    /// <summary>
    /// Closes the Admin SOAP client, aborting it when it is faulted or its close fails, then deletes the test's
    /// companies. Ports <c>afterTest</c> and <c>afterClass</c>.
    /// </summary>
    /// <returns>A task that completes when the client is closed and no company is left.</returns>
    protected override async Task TearDownAsync()
    {
        try
        {
            await CloseClientAsync();
        }
        finally
        {
            await base.TearDownAsync();
        }
    }

    /// <summary>Calling <c>createClient</c> for a client that already exists answers 409 with one error.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidClient()
    {
        const int ExpectedError = 409;

        // Arrange
        Address address = Builder.CreateTestAddress(
            ClientStreet, ClientCity, ClientCountry, ClientPhone, ClientFax, ClientEmail, ClientWebsite);
        await Builder.CreateTestClientAsync(ClientName, address);

        // Act
        DTOResult result = await AdminClient.createClientAsync(
            ClientName, ClientStreet, ClientCity, ClientCountry, ClientPhone, ClientFax, ClientEmail, ClientWebsite);

        // Assert
        Assert.Equal(ExpectedError, result.statusCode);
        string error = Assert.Single(result.allErrors ?? []);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ClientNameAlreadyExists, ClientName), error);
        Assert.Equal("Client Dariush Kamran already exists", error);
    }

    /// <summary>Calling <c>createClient</c> for a new client answers 201 with the client's GUID and no errors.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testValidClient()
    {
        const int ExpectedStatus = 201;

        // Act
        DTOResult result = await AdminClient.createClientAsync(
            ClientName, ClientStreet, ClientCity, ClientCountry, ClientPhone, ClientFax, ClientEmail, ClientWebsite);

        // Assert
        Assert.NotNull(result);
        Assert.False(JadeValues.IsNull(result.guid));
        Assert.Equal(ExpectedStatus, result.statusCode);
        Assert.False((result.allErrors?.Length ?? 0) > 0);
    }

    /// <summary>
    /// Closes the Admin SOAP client when one exists: aborts it when it is faulted, or when closing it fails with a
    /// <see cref="CommunicationException"/> or a <see cref="TimeoutException"/>. Afterwards no client is held.
    /// </summary>
    /// <returns>A task that completes when the client is closed or aborted.</returns>
    private async Task CloseClientAsync()
    {
        if (_client is not { } client)
        {
            return;
        }

        _client = null;

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

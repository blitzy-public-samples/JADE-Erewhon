using System.Net.Http.Json;
using System.Text.Json;
using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Domain;
using Erewhon.Testing;

namespace Erewhon.Api.Tests.Rest;

/// <summary>
/// Tests of the REST operation <c>getAllClients</c>, called over HTTP as <c>GET /api/rest/clients</c>. Ports the JADE
/// class <c>RestTestGetAllClients</c>.
/// </summary>
public sealed class RestTestGetAllClients : ErewhonTestBase
{
    private const int TotalClients = 9;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Creates the test data of <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which binds its company and holds
    /// the client "Test Client", then the clients "Test Client 1" to "Test Client 9", each at "Street", "City",
    /// "Country", phone "Phone", fax "Fax", e-mail "agent@email.com" and web site "www.agent.com". Ports
    /// <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the ten clients exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();

        for (var clientCount = 1; clientCount <= TotalClients; clientCount++)
        {
            Address address = Builder.CreateTestAddress(
                "Street", "City", "Country", "Phone", "Fax", "agent@email.com", "www.agent.com");
            await Builder.CreateTestClientAsync("Test Client " + clientCount, address);
        }
    }

    /// <summary><c>GET /api/rest/clients</c> answers 200 with the ten clients of the bound company.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetAllClients()
    {
        // Arrange
        using HttpClient http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage response = await http.GetAsync("/api/rest/clients");
        ClientDto[]? clients = await response.Content.ReadFromJsonAsync<ClientDto[]>(Json);

        // Assert
        Assert.NotNull(clients);
        Assert.Equal(10, clients.Length);
        Assert.Equal(200, (int)response.StatusCode);
    }
}

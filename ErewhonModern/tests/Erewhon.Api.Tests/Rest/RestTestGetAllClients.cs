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

    private readonly List<Client> _numberedClients = new(TotalClients);

    /// <summary>
    /// Creates the test data of <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which binds its company and holds
    /// the client "Test Client", then the clients "Test Client 1" to "Test Client 9", each at "Street", "City",
    /// "Country", phone "Phone", fax "Fax", e-mail "agent@email.com" and web site "www.agent.com", and keeps those nine
    /// clients in creation order. Ports <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the ten clients exist.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();

        for (var clientCount = 1; clientCount <= TotalClients; clientCount++)
        {
            Address address = Builder.CreateTestAddress(
                "Street", "City", "Country", "Phone", "Fax", "agent@email.com", "www.agent.com");
            _numberedClients.Add(await Builder.CreateTestClientAsync("Test Client " + clientCount, address));
        }
    }

    /// <summary>
    /// <c>GET /api/rest/clients</c> answers 200 with the ten clients of the bound company: "Test Client", then
    /// "Test Client 1" to "Test Client 9", each with the GUID of the client of that name.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetAllClients()
    {
        // Arrange
        using HttpClient http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage response = await http.GetAsync("/api/rest/clients");
        ClientDto[]? clients = await response.Content.ReadFromJsonAsync<ClientDto[]>(Json);

        Assert.NotNull(clients);
        Assert.Equal(10, clients.Length);
        Assert.Equal(200, (int)response.StatusCode);

        (string? Name, string? Guid)[] expected =
        {
            ("Test Client", Builder.MyClient!.Guid),
            ("Test Client 1", _numberedClients[0].Guid),
            ("Test Client 2", _numberedClients[1].Guid),
            ("Test Client 3", _numberedClients[2].Guid),
            ("Test Client 4", _numberedClients[3].Guid),
            ("Test Client 5", _numberedClients[4].Guid),
            ("Test Client 6", _numberedClients[5].Guid),
            ("Test Client 7", _numberedClients[6].Guid),
            ("Test Client 8", _numberedClients[7].Guid),
            ("Test Client 9", _numberedClients[8].Guid),
        };
        Assert.Equal(expected, clients.Select(client => (client.Name, client.Guid)));
    }

    /// <summary>
    /// After the clients "Zed Client", "Adam Client" and "Test Client 0" are created in that order, at the address of
    /// <see cref="SetUpAsync"/>, <c>GET /api/rest/clients</c> answers 200 with the company's thirteen clients in name
    /// order: "Adam Client", "Test Client", "Test Client 0", "Test Client 1" to "Test Client 9", then "Zed Client",
    /// each with the GUID of the client of that name.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ClientsListedInNameOrder()
    {
        // Arrange
        Client zedClient = await Builder.CreateTestClientAsync("Zed Client", CreateSetUpAddress());
        Client adamClient = await Builder.CreateTestClientAsync("Adam Client", CreateSetUpAddress());
        Client zeroClient = await Builder.CreateTestClientAsync("Test Client 0", CreateSetUpAddress());
        using var http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage response = await http.GetAsync("/api/rest/clients");
        ClientDto[]? clients = await response.Content.ReadFromJsonAsync<ClientDto[]>(Json);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.NotNull(clients);

        (string? Name, string? Guid)[] expected =
        {
            ("Adam Client", adamClient.Guid),
            ("Test Client", Builder.MyClient!.Guid),
            ("Test Client 0", zeroClient.Guid),
            ("Test Client 1", _numberedClients[0].Guid),
            ("Test Client 2", _numberedClients[1].Guid),
            ("Test Client 3", _numberedClients[2].Guid),
            ("Test Client 4", _numberedClients[3].Guid),
            ("Test Client 5", _numberedClients[4].Guid),
            ("Test Client 6", _numberedClients[5].Guid),
            ("Test Client 7", _numberedClients[6].Guid),
            ("Test Client 8", _numberedClients[7].Guid),
            ("Test Client 9", _numberedClients[8].Guid),
            ("Zed Client", zedClient.Guid),
        };
        Assert.Equal(expected, clients.Select(client => (client.Name, client.Guid)));
    }

    /// <summary>
    /// Creates the address of <see cref="SetUpAsync"/>: "Street", "City", "Country", phone "Phone", fax "Fax", e-mail
    /// "agent@email.com" and web site "www.agent.com".
    /// </summary>
    /// <returns>The new, unstored address.</returns>
    private Address CreateSetUpAddress() =>
        Builder.CreateTestAddress("Street", "City", "Country", "Phone", "Fax", "agent@email.com", "www.agent.com");
}

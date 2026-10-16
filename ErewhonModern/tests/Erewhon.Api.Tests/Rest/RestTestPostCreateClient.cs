using System.Net.Http.Json;
using System.Text.Json;
using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Tests.Rest;

/// <summary>
/// Tests of the REST operation <c>postCreateClient</c>, <c>POST /api/rest/clients</c>, called over HTTP on the test
/// host. Ports the JADE class <c>RestTestPostCreateClient</c>.
/// </summary>
public sealed class RestTestPostCreateClient : ErewhonTestBase
{
    private const string Route = "/api/rest/clients";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Creates the test data of <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, which binds its company and
    /// holds the one client "Test Client". Ports <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the test data exists.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
    }

    /// <summary>
    /// Posting a client with no name answers 400 with <see cref="MessageKeys.ClientNameRequired"/> as the first error
    /// and leaves the company with its one client.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidClient()
    {
        // Arrange
        ClientDto dtoClient = CreateClientDto(null);

        // Act
        using var http = Api.CreateHttpClient();
        using HttpResponseMessage response = await http.PostAsJsonAsync(Route, dtoClient, Json);
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);

        // Assert
        Assert.NotNull(dtoResults);
        Assert.NotEmpty(dtoResults.AllErrors);
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal(1, await CountClientsAsync());
        Assert.Equal(MessageCatalog.Get(MessageKeys.ClientNameRequired), dtoResults.AllErrors[0]);
    }

    /// <summary>
    /// Posting a new client answers 201 with no errors, adds the client to the company, and stores its name and the
    /// seven address fields as posted.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testValidClient()
    {
        // Arrange
        ClientDto dtoClient = CreateClientDto("Test Client Name");

        // Act
        using var http = Api.CreateHttpClient();
        using HttpResponseMessage response = await http.PostAsJsonAsync(Route, dtoClient, Json);
        ResultDto? dtoResults = await response.Content.ReadFromJsonAsync<ResultDto>(Json);
        Client? client = await FindClientAsync("Test Client Name");

        // Assert
        Assert.NotNull(dtoResults);
        Assert.Empty(dtoResults.AllErrors);
        Assert.Equal(201, (int)response.StatusCode);
        Assert.Equal(2, await CountClientsAsync());
        Assert.NotNull(client);
        Assert.Equal(dtoClient.Name, client.Name);
        Assert.Equal(dtoClient.Street, client.Address.Street);
        Assert.Equal(dtoClient.City, client.Address.City);
        Assert.Equal(dtoClient.Country, client.Address.Country);
        Assert.Equal(dtoClient.Phone, client.Address.Phone);
        Assert.Equal(dtoClient.Fax, client.Address.Fax);
        Assert.Equal(dtoClient.Email, client.Address.Email);
        Assert.Equal(dtoClient.Website, client.Address.WebSite);
    }

    /// <summary>
    /// Returns a client request named <paramref name="name"/> at "Street", "City", "Country", phone "Phone", fax "Fax",
    /// e-mail "client@email.com" and web site "www.website.com", with no GUID.
    /// </summary>
    private static ClientDto CreateClientDto(string? name) => new()
    {
        Name = name,
        Street = "Street",
        City = "City",
        Country = "Country",
        Phone = "Phone",
        Fax = "Fax",
        Email = "client@email.com",
        Website = "www.website.com",
    };

    /// <summary>Returns the number of clients of the test company, read in a new scope of the test host.</summary>
    private async Task<int> CountClientsAsync()
    {
        long companyId = Builder.MyCompany!.Id;

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Clients
            .AsNoTracking()
            .CountAsync(c => c.CompanyId == companyId);
    }

    /// <summary>
    /// Returns the client of the test company whose name is exactly <paramref name="name"/>, read untracked with its
    /// address in a new scope of the test host, or <see langword="null"/> when there is none.
    /// </summary>
    private async Task<Client?> FindClientAsync(string name)
    {
        long companyId = Builder.MyCompany!.Id;

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Clients
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.CompanyId == companyId && c.Name == name);
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Domain;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erewhon.Api.Tests.Rest;

/// <summary>
/// Tests of the REST operation <c>getClient</c>, <c>GET /api/rest/clients/by-name?name=</c>, called over HTTP on the
/// test host. Ports the JADE class <c>RestTestGetClient</c>.
/// </summary>
public sealed class RestTestGetClient : ErewhonTestBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Creates the test data of <see cref="TestCompanyBuilder.CreateTestDataAsync"/>, whose company is bound.
    /// Ports <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the test data exists.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
    }

    /// <summary>Looking up a client name that no client holds answers 404 with an empty body.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidClient()
    {
        const string ClientName = "Some Client That Does Not Exist";
        const int ExpectedError = 404;

        // Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await http.GetAsync(ByNamePath(ClientName));

        // Assert
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(ExpectedError, (int)response.StatusCode);
    }

    /// <summary>
    /// Looking up the name of a client just created answers 200 with the client's name and the seven address fields.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testValidClient()
    {
        const string ClientName = "Hank Williams";
        const string ClientStreet = "9127 Bel Air Drive";
        const string ClientCity = "Los Angeles";
        const string ClientCountry = "United States of America";
        const string ClientPhone = "1 8 2938 4756";
        const string ClientFax = "1 8 6354 7364";
        const string ClientEmail = "hank@it.com";
        const string ClientWebsite = "www.it.com";
        const int ExpectedStatus = 200;

        // Arrange
        Address address = Builder.CreateTestAddress(
            ClientStreet, ClientCity, ClientCountry, ClientPhone, ClientFax, ClientEmail, ClientWebsite);
        await Builder.CreateTestClientAsync(ClientName, address);

        Assert.NotNull(await FindCompanyClientAsync(ClientName));

        // Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await http.GetAsync(ByNamePath(ClientName));

        // Assert
        Assert.Equal(ExpectedStatus, (int)response.StatusCode);
        ClientDto? dtoClient = await response.Content.ReadFromJsonAsync<ClientDto>(Json);
        Assert.NotNull(dtoClient);

        Assert.Equal(ClientName, dtoClient.Name);
        Assert.Equal(ClientStreet, dtoClient.Street);
        Assert.Equal(ClientCity, dtoClient.City);
        Assert.Equal(ClientCountry, dtoClient.Country);
        Assert.Equal(ClientPhone, dtoClient.Phone);
        Assert.Equal(ClientFax, dtoClient.Fax);
        Assert.Equal(ClientEmail, dtoClient.Email);
        Assert.Equal(ClientWebsite, dtoClient.Website);
    }

    /// <summary>Returns the path of <c>getClient</c> for <paramref name="name"/>, escaped as a query value.</summary>
    private static string ByNamePath(string name) =>
        "/api/rest/clients/by-name?name=" + Uri.EscapeDataString(name);

    /// <summary>
    /// Returns the client of <see cref="TestCompanyBuilder.MyCompany"/> named exactly <paramref name="name"/>, read
    /// untracked in a new scope of <see cref="ErewhonTestBase.Api"/>, or <see langword="null"/> when there is none.
    /// </summary>
    private async Task<Client?> FindCompanyClientAsync(string name)
    {
        long companyId = Builder.MyCompany!.Id;

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Clients
            .AsNoTracking()
            .SingleOrDefaultAsync(client => client.CompanyId == companyId && client.Name == name);
    }
}

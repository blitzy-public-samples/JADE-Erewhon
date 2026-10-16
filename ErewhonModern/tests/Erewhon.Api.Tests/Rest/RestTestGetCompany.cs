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
/// Tests of the REST operation <c>getCompany</c> at <c>GET /api/rest/company</c>, called over HTTP.
/// Ports the JADE class <c>RestTestGetCompany</c>.
/// </summary>
public sealed class RestTestGetCompany : ErewhonTestBase
{
    private const string CompanyPath = "/api/rest/company";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Creates and binds the test data of <see cref="TestCompanyBuilder.CreateTestDataAsync"/>. Ports
    /// <c>beforeTest</c>.
    /// </summary>
    /// <returns>A task that completes when the test data exists.</returns>
    protected override async Task SetUpAsync()
    {
        await Builder.CreateTestDataAsync();
    }

    /// <summary>Calling <c>getCompany</c> when no company exists answers 500 with an empty body.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidCompany()
    {
        const int ExpectedStatus = 500;

        // Arrange
        await Builder.DeleteCompanyAsync(Builder.MyCompany!.Id);

        // Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await http.GetAsync(CompanyPath);

        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal(ExpectedStatus, (int)response.StatusCode);
    }

    /// <summary>
    /// Calling <c>getCompany</c> answers 200 with the bound company's name and the seven fields of its address.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testValidCompany()
    {
        const int ExpectedStatus = 200;

        // Act
        using HttpClient http = Api.CreateHttpClient();
        using HttpResponseMessage response = await http.GetAsync(CompanyPath);
        Assert.Equal(ExpectedStatus, (int)response.StatusCode);
        CompanyDto? dtoCompany = await response.Content.ReadFromJsonAsync<CompanyDto>(Json);

        Assert.NotNull(dtoCompany);
        Company company = await ReadBoundCompanyAsync();
        Assert.Equal(company.Name, dtoCompany.Name);
        Assert.Equal(company.Address.Street, dtoCompany.Street);
        Assert.Equal(company.Address.City, dtoCompany.City);
        Assert.Equal(company.Address.Country, dtoCompany.Country);
        Assert.Equal(company.Address.Phone, dtoCompany.Phone);
        Assert.Equal(company.Address.Fax, dtoCompany.Fax);
        Assert.Equal(company.Address.Email, dtoCompany.Email);
        Assert.Equal(company.Address.WebSite, dtoCompany.Website);
    }

    /// <summary>
    /// Reads the row of <see cref="TestCompanyBuilder.MyCompany"/>, with its address, untracked in a new scope of
    /// <see cref="ErewhonTestBase.Api"/>.
    /// </summary>
    /// <returns>The bound company as stored.</returns>
    /// <exception cref="InvalidOperationException">No company row has the id of the bound company.</exception>
    private async Task<Company> ReadBoundCompanyAsync()
    {
        long companyId = Builder.MyCompany!.Id;

        await using AsyncServiceScope scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IErewhonDbContext>()
            .Companies
            .AsNoTracking()
            .SingleAsync(company => company.Id == companyId);
    }
}

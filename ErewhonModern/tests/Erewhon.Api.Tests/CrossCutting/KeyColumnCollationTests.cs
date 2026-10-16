using System.Net.Http.Json;
using System.Text.Json;
using Erewhon.Api.Rest.Contracts;
using Erewhon.Application.Domain;
using Erewhon.Testing;
using Npgsql;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises the collation of the member-key string columns on PostgreSQL: <c>agents.name</c>,
/// <c>clients.name</c>, <c>countries.name</c>, <c>regions.name</c>, <c>item_categories.name</c> and
/// <c>item_categories.prefix</c> each have the column collation <c>C</c> in the migrated test database, and the REST
/// agent and client lists return names that differ in case and accent in <see cref="StringComparer.Ordinal"/> order.
/// </summary>
public sealed class KeyColumnCollationTests : ErewhonTestBase
{
    private const string BinaryCollation = "C";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The probe names, in the order they are created: lower case, accented, upper case.</summary>
    private static readonly string[] ProbeNames =
    {
        "aaron probe",
        "\u00C9mile probe",
        "Zed probe",
        "Bob probe",
    };

    /// <summary>
    /// The column <paramref name="column"/> of the table <paramref name="table"/> in the <c>public</c> schema of the
    /// migrated test database has the collation <c>C</c> in <c>information_schema.columns</c>.
    /// </summary>
    /// <param name="table">The table name.</param>
    /// <param name="column">The column name.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData("agents", "name")]
    [InlineData("clients", "name")]
    [InlineData("countries", "name")]
    [InlineData("regions", "name")]
    [InlineData("item_categories", "name")]
    [InlineData("item_categories", "prefix")]
    public async Task KeyColumnHasBinaryCollation(string table, string column)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand(
            "SELECT collation_name FROM information_schema.columns "
            + "WHERE table_schema = 'public' AND table_name = @table AND column_name = @column",
            connection);
        query.Parameters.AddWithValue("table", table);
        query.Parameters.AddWithValue("column", column);

        // Act
        await using NpgsqlDataReader reader = await query.ExecuteReaderAsync();

        // Assert
        Assert.True(await reader.ReadAsync(), $"The column {table}.{column} does not exist.");
        Assert.Equal(BinaryCollation, reader.IsDBNull(0) ? null : reader.GetString(0));
    }

    /// <summary>
    /// With the agents and the clients "aaron probe", "Émile probe", "Zed probe" and "Bob probe" created in that order
    /// in the bound company, <c>GET /api/rest/agents</c> and <c>GET /api/rest/clients</c> each answer 200 with exactly
    /// those names in <see cref="StringComparer.Ordinal"/> order: "Bob probe", "Zed probe", "aaron probe",
    /// "Émile probe".
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RestAgentAndClientListsReturnNamesInOrdinalOrder()
    {
        foreach (string name in ProbeNames)
        {
            Address address = Builder.CreateTestAddress(
                "Street", "City", "Country", "Phone", "Fax", "probe@email.com", "www.probe.com");
            await Builder.CreateTestAgentAsync(name, address);
            await Builder.CreateTestClientAsync(name, address);
        }

        string[] expected = ProbeNames.Order(StringComparer.Ordinal).ToArray();
        using var http = Api.CreateHttpClient();

        // Act
        using HttpResponseMessage agentsResponse = await http.GetAsync("/api/rest/agents");
        AgentDto[]? agents = await agentsResponse.Content.ReadFromJsonAsync<AgentDto[]>(Json);
        using HttpResponseMessage clientsResponse = await http.GetAsync("/api/rest/clients");
        ClientDto[]? clients = await clientsResponse.Content.ReadFromJsonAsync<ClientDto[]>(Json);

        // Assert
        Assert.Equal(new[] { "Bob probe", "Zed probe", "aaron probe", "\u00C9mile probe" }, expected);
        Assert.Equal(200, (int)agentsResponse.StatusCode);
        Assert.NotNull(agents);
        Assert.Equal(expected, agents.Select(agent => agent.Name));
        Assert.Equal(200, (int)clientsResponse.StatusCode);
        Assert.NotNull(clients);
        Assert.Equal(expected, clients.Select(client => client.Name));
    }
}

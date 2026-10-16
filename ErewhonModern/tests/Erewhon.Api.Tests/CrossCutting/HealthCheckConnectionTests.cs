using System.Net;
using Erewhon.Testing;
using Npgsql;
using Serilog.Events;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises the connection the health check <c>postgresql</c> behind <c>/healthz</c> opens. Every request after
/// PostgreSQL has terminated each backend of the API's role on its database, the idle connections of the process's
/// Npgsql pool for <c>ConnectionStrings:Erewhon</c> included, answers 200 <c>Healthy</c>. A value of
/// <c>ConnectionStrings:Erewhon</c> that Npgsql rejects answers 503 <c>Unhealthy</c>, and the health-check event
/// describes it by the exception types alone. Each case runs the real API on Kestrel over the assembly database.
/// </summary>
public sealed class HealthCheckConnectionTests
{
    private const string HealthPath = "healthz";

    private const string HealthCheckName = "postgresql";

    private const string HealthCheckNameProperty = "HealthCheckName";

    private const string HealthCheckDescriptionProperty = "HealthCheckDescription";

    private const int TerminationWaitMs = 5000;

    private const int RequestsAfterTermination = 3;

    /// <summary>
    /// <c>/healthz</c> answers 200 <c>Healthy</c>; an idle connection is then left in the process's Npgsql pool for
    /// the host's connection string, and every other backend of the role on the database is terminated, at least one
    /// of them. Each of the next three requests answers 200 <c>Healthy</c>.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task HealthyOnEveryRequestAfterServerTerminatesPooledConnections()
    {
        PostgresDatabase database = await PrepareDatabaseAsync();
        try
        {
            await using ErewhonApiHost api = await ErewhonApiHost.StartAsync(new ErewhonApiHostOptions
            {
                ConnectionString = database.ConnectionString,
            });
            using HttpClient http = api.CreateHttpClient();

            await AssertHealthAsync(http, HttpStatusCode.OK, "Healthy");

            await using (var pooled = new NpgsqlConnection(database.ConnectionString))
            {
                await pooled.OpenAsync();
            }

            Assert.True(await TerminateRoleBackendsAsync(database) > 0);

            for (var request = 0; request < RequestsAfterTermination; request++)
            {
                await AssertHealthAsync(http, HttpStatusCode.OK, "Healthy");
            }
        }
        finally
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            NpgsqlConnection.ClearPool(connection);
        }
    }

    /// <summary>
    /// With <c>ConnectionStrings:Erewhon</c> set to <paramref name="connectionString"/>, which Npgsql rejects,
    /// <c>/healthz</c> answers 503 <c>Unhealthy</c>, and the host logs one health-check event for <c>postgresql</c>
    /// whose description is <paramref name="expectedDescription"/> and holds no part of the value.
    /// </summary>
    /// <param name="connectionString">The rejected value.</param>
    /// <param name="expectedDescription">The description of the health-check event.</param>
    /// <returns>A task that completes when the case has run.</returns>
    [Theory]
    [InlineData(
        "erewhon-malformed-value",
        "Opening ConnectionStrings:Erewhon or running SELECT 1 failed with System.ArgumentException.")]
    [InlineData(
        "Port=erewhon-not-a-port",
        "Opening ConnectionStrings:Erewhon or running SELECT 1 failed with System.ArgumentException ---> "
        + "System.FormatException.")]
    public async Task UnhealthyWithTypeOnlyDescriptionForRejectedConnectionString(
        string connectionString,
        string expectedDescription)
    {
        var logSink = new CollectingLogSink();
        await using ErewhonApiHost api = await ErewhonApiHost.StartAsync(new ErewhonApiHostOptions
        {
            ConnectionString = connectionString,
            LogSink = logSink,
        });
        using HttpClient http = api.CreateHttpClient();

        await AssertHealthAsync(http, HttpStatusCode.ServiceUnavailable, "Unhealthy");

        LogEvent healthEvent = Assert.Single(
            logSink.Events,
            logEvent => ScalarText(logEvent, HealthCheckNameProperty) == HealthCheckName);
        string? description = ScalarText(healthEvent, HealthCheckDescriptionProperty);
        Assert.Equal(expectedDescription, description);
        Assert.Null(healthEvent.Exception);
        Assert.DoesNotContain("erewhon-", healthEvent.RenderMessage(), StringComparison.Ordinal);
    }

    /// <summary>Creates and migrates the test database of the assembly and returns it.</summary>
    private static async Task<PostgresDatabase> PrepareDatabaseAsync()
    {
        PostgresDatabase database = PostgresDatabase.ForAssembly(typeof(HealthCheckConnectionTests).Assembly);
        await database.EnsureCreatedAndMigratedAsync();
        return database;
    }

    /// <summary>Requests <c>/healthz</c> and asserts the status and the exact body.</summary>
    private static async Task AssertHealthAsync(HttpClient http, HttpStatusCode expectedStatus, string expectedBody)
    {
        using HttpResponseMessage response = await http.GetAsync(HealthPath);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Terminates, over an unpooled connection, every other backend of the current role on the database of
    /// <paramref name="database"/>, waiting up to <see cref="TerminationWaitMs"/> for each to exit, and returns how
    /// many were terminated.
    /// </summary>
    private static async Task<long> TerminateRoleBackendsAsync(PostgresDatabase database)
    {
        var unpooled = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(unpooled.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM (SELECT pg_terminate_backend(pid, @wait) AS terminated FROM pg_stat_activity "
            + "WHERE datname = current_database() AND usename = current_user AND pid <> pg_backend_pid()) AS ended "
            + "WHERE terminated",
            connection);
        command.Parameters.AddWithValue("wait", (long)TerminationWaitMs);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Returns the value of the scalar property <paramref name="name"/> of <paramref name="logEvent"/> as text, or
    /// <see langword="null"/> when the event has no such scalar property.
    /// </summary>
    private static string? ScalarText(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out LogEventPropertyValue? value) && value is ScalarValue scalar
            ? scalar.Value?.ToString()
            : null;
}

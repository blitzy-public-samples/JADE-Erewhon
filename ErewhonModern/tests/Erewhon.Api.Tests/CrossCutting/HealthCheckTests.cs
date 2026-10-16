using System.Net;
using System.Net.Sockets;
using Erewhon.Testing;
using Npgsql;

namespace Erewhon.Api.Tests.CrossCutting;

/// <summary>
/// Characterises <c>/healthz</c> against PostgreSQL. The API shared by the assembly, running over the assembly
/// database, answers 200 <c>Healthy</c>. A further API on Kestrel whose <c>ConnectionStrings:Erewhon</c> names a
/// loopback port with no listener answers 503 <c>Unhealthy</c>, while the shared API still answers 200
/// <c>Healthy</c>.
/// </summary>
public sealed class HealthCheckTests : ErewhonTestBase
{
    private const string HealthPath = "healthz";

    private const string ConnectionStringKey = "ConnectionStrings:Erewhon";

    private const string HealthyBody = "Healthy";

    private const string UnhealthyBody = "Unhealthy";

    /// <summary><c>GET /healthz</c> on the shared API answers 200 with the body <c>Healthy</c>.</summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task ValidConnectionIsHealthy()
    {
        using HttpClient client = Api.CreateHttpClient();

        await AssertHealthAsync(client, HttpStatusCode.OK, HealthyBody);
    }

    /// <summary>
    /// A further API starts with <c>ConnectionStrings:Erewhon</c> set to the assembly database's connection string
    /// with the host set to the IPv4 loopback address and the port set to a loopback port with no listener.
    /// <c>GET /healthz</c> on it answers 503 with the body <c>Unhealthy</c>; <c>GET /healthz</c> on the shared API
    /// then answers 200 with the body <c>Healthy</c>.
    /// </summary>
    /// <returns>A task that completes when the case has run.</returns>
    [Fact]
    public async Task ClosedPortIsUnhealthy()
    {
        string closed = ClosedPortConnectionString(Database);

        await using ErewhonApiHost host = await StartDedicatedHostAsync(
            new Dictionary<string, string?> { [ConnectionStringKey] = closed });
        using HttpClient dedicatedClient = host.CreateHttpClient();

        await AssertHealthAsync(dedicatedClient, HttpStatusCode.ServiceUnavailable, UnhealthyBody);

        using HttpClient sharedClient = Api.CreateHttpClient();

        await AssertHealthAsync(sharedClient, HttpStatusCode.OK, HealthyBody);
    }

    /// <summary>
    /// Returns the connection string of <paramref name="database"/> with <c>Host</c> set to the IPv4 loopback address,
    /// <c>Port</c> set to <see cref="UnusedLoopbackPort"/> and every other setting unchanged.
    /// </summary>
    private static string ClosedPortConnectionString(PostgresDatabase database) =>
        new NpgsqlConnectionStringBuilder(database.ForPort(UnusedLoopbackPort()))
        {
            Host = IPAddress.Loopback.ToString(),
        }.ConnectionString;

    /// <summary>
    /// Returns a port the operating system assigned on the IPv4 loopback address to a listener that is stopped before
    /// the port is returned.
    /// </summary>
    private static int UnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Requests <c>/healthz</c> and asserts the status and the body without surrounding white space.</summary>
    private static async Task AssertHealthAsync(HttpClient client, HttpStatusCode expectedStatus, string expectedBody)
    {
        using HttpResponseMessage response = await client.GetAsync(HealthPath);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedBody, (await response.Content.ReadAsStringAsync()).Trim());
    }
}

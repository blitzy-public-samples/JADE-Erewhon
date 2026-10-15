using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erewhon.Persistence;

/// <summary>
/// Creates the <see cref="ErewhonDbContext"/> that the Entity Framework Core tools (<c>dotnet ef</c>) use to add,
/// list and script migrations and to apply them with <c>database update</c>.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ErewhonDbContext>
{
    private const string ConnectionStringVariable = "ConnectionStrings__Erewhon";

    /// <summary>
    /// Creates a context on the PostgreSQL provider, connected through the connection string in the
    /// <c>ConnectionStrings__Erewhon</c> environment variable, with no change-notification interceptor.
    /// When the variable is unset, empty or whitespace, the provider is configured without a connection string:
    /// commands that open no connection, such as <c>migrations add</c> and <c>migrations script</c>, succeed, and a
    /// command that opens one, such as <c>database update</c>, fails with a provider error.
    /// </summary>
    /// <param name="args">The arguments passed by the tools; not read.</param>
    /// <returns>A new context over the <c>Erewhon.Persistence</c> model and migrations.</returns>
    public ErewhonDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        var builder = new DbContextOptionsBuilder<ErewhonDbContext>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            builder.UseNpgsql();
        }
        else
        {
            builder.UseNpgsql(connectionString);
        }

        return new ErewhonDbContext(builder.Options);
    }
}

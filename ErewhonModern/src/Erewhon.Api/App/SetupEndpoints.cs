using System.Globalization;
using Erewhon.Api.App.Contracts;
using Erewhon.Api.Errors;
using Erewhon.Application.Abstractions;
using Erewhon.Persistence.Seed;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erewhon.Api.App;

/// <summary>
/// Maps <c>POST /api/app/setup/load-data</c>, the back end of the load-data prompt shown in place of the logon screen
/// when no company exists: it loads the seed data files of the configured data directory
/// (<c>Erewhon:Seed:DataDirectory</c>) through <see cref="InitialDataLoader"/>.
/// </summary>
public static class SetupEndpoints
{
    /// <summary>
    /// Maps <c>POST /setup/load-data</c> on <paramref name="api"/>. The route takes no request body and needs no
    /// session.
    /// <list type="bullet">
    /// <item><description>
    /// When <see cref="ICompanyContext.FindCurrentAsync(CancellationToken)"/> finds a company, it returns 409 with a
    /// <see cref="LoadDataResponse"/> whose <see cref="LoadDataResponse.Loaded"/> is <see langword="false"/>,
    /// <see cref="LoadDataResponse.TenderSalesBuilt"/> is 0 and <see cref="LoadDataResponse.OutputLines"/> is empty,
    /// and runs no load.
    /// </description></item>
    /// <item><description>
    /// Otherwise it runs <see cref="InitialDataLoader.LoadConfiguredWhenNoCompanyAsync(TextWriter?, CancellationToken)"/>,
    /// which waits for the seed load lock <c>InitialDataLoader.loadData</c> and reads the companies again once it holds
    /// it. When a company exists by then, it returns the same 409 and no load runs.
    /// </description></item>
    /// <item><description>
    /// When the load runs, it returns 200 with a <see cref="LoadDataResponse"/> whose
    /// <see cref="LoadDataResponse.Loaded"/> is <see langword="true"/> and which carries the number of tender sales the
    /// post-load closure built and every line the loader wrote, in order.
    /// </description></item>
    /// </list>
    /// A failed load, such as a configured directory that is missing or invalid (error 500004), a company that
    /// survives the purge (error 500003) or a database failure, ends the request with the 500
    /// <see cref="ErrorOutcome"/> written by <see cref="ErrorOutcomeMiddleware"/>.
    /// </summary>
    /// <param name="api">The <c>/api/app</c> route group.</param>
    /// <returns><paramref name="api"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> is <see langword="null"/>.</exception>
    public static RouteGroupBuilder MapSetupEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapPost("/setup/load-data", LoadDataAsync)
            .WithName("AppLoadData")
            .WithTags("App: Setup")
            .WithSummary(
                "Loads the seed data files of the configured data directory when no company exists; refused with 409 "
                + "when a company exists.")
            .Produces<LoadDataResponse>(StatusCodes.Status200OK)
            .Produces<LoadDataResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorOutcome>(StatusCodes.Status500InternalServerError);

        return api;
    }

    /// <summary>
    /// Returns 409 with a refused load result, whose <c>loaded</c> is false, when a company exists, or when one exists
    /// once the loader holds the seed load lock; otherwise loads the configured seed data and returns 200 with the
    /// loader's result.
    /// </summary>
    /// <param name="companyContext">The company bound to the request scope.</param>
    /// <param name="loader">The seed data loader of the request scope.</param>
    /// <param name="ct">Cancels the company read, the wait for the seed load lock and the load.</param>
    /// <returns>The 200 or 409 result.</returns>
    private static async Task<Results<Ok<LoadDataResponse>, Conflict<LoadDataResponse>>> LoadDataAsync(
        [FromServices] ICompanyContext companyContext,
        [FromServices] InitialDataLoader loader,
        CancellationToken ct)
    {
        if (await companyContext.FindCurrentAsync(ct) is not null)
        {
            return Refused();
        }

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        SeedLoadResult? result = await loader.LoadConfiguredWhenNoCompanyAsync(output, ct);
        if (result is null)
        {
            return Refused();
        }

        return TypedResults.Ok(
            new LoadDataResponse(Loaded: true, TenderSalesBuilt: result.TenderSalesBuilt, OutputLines: result.OutputLines));
    }

    /// <summary>
    /// Returns 409 with a <see cref="LoadDataResponse"/> whose <see cref="LoadDataResponse.Loaded"/> is
    /// <see langword="false"/>, <see cref="LoadDataResponse.TenderSalesBuilt"/> is 0 and
    /// <see cref="LoadDataResponse.OutputLines"/> is empty.
    /// </summary>
    /// <returns>The 409 result.</returns>
    private static Conflict<LoadDataResponse> Refused() =>
        TypedResults.Conflict(
            new LoadDataResponse(Loaded: false, TenderSalesBuilt: 0, OutputLines: Array.Empty<string>()));
}

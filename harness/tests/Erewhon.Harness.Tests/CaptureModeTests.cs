using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Erewhon.Contracts;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Catalogue;
using Erewhon.Contracts.Seed;
using Erewhon.Harness.Baseline;
using Erewhon.Harness.Cases;
using Erewhon.Harness.Lifecycle;
using Erewhon.Harness.Modes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Erewhon.Harness.Tests;

/// <summary>
/// The capture mode against an in-process loopback stand-in stub, in a temporary repository root of its own
/// that holds a copy of the committed case matrix and a one-file seed directory; the repository's own
/// <c>baseline/</c> is never touched. Expected seed facts no stand-in can confirm refuse the capture before
/// the stand-in is contacted; a capture-start reset whose seed hash or reference date differs from the first
/// capture's or the manifest's refuses it before any write; an empty-state bracket reset that is not
/// confirmed stops it after the bracket's seed restore, with no manifest; and a confirmed capture publishes the
/// confirmed seed facts in a manifest written after every other file. Each refusal or stop exits 1.
/// </summary>
public sealed class CaptureModeTests : IDisposable
{
    private const string ReferenceDate = "2026-01-01";
    private const string OtherReferenceDate = "2025-06-01";
    private const string OtherSeedSha256 = "34df80d4c1b0a5e2f7d8c9b0a1e2f3d4c5b6a7980f1e2d3c4b5a69788796a5b4";
    private const string SeedSha256Field = "seedSha256";
    private const string ReferenceDateField = "referenceDate";

    private const string ManifestFile = "baseline/manifest.json";
    private const string ManifestTemporaryFile = "baseline/.manifest.json.tmp";
    private const string ConformanceFile = "baseline/conformance.json";
    private const string ContractsWsdlFile = "contracts/wsdl/ErewhonWebService.wsdl";

    // A record file no case of the matrix writes, so only a clear can remove it.
    private const string SentinelRecord = "baseline/records/rest/999-getAgent-99.json";

    private const string NotTouched = "capture cannot start; baseline/ not touched";
    private const string SeedResetRequest = "POST /admin/reset?state=seed";
    private const string EmptyResetRequest = "POST /admin/reset?state=empty";
    private const string RepoRootNotFound = "No directory above the test binaries holds harness/config.";

    // Readiness and request limits: generous for a loaded host, never reached by a passing test.
    private const int LimitSeconds = 10;

    private static readonly string[] AdminRequests = ["GET /health", "GET /admin/info", SeedResetRequest];

    private readonly string _root;
    private readonly string _seedSha256;
    private readonly StringWriter _console = new();

    public CaptureModeTests()
    {
        // The run sets TMPDIR to a per-clone directory, so parallel checkouts never share a root.
        _root = Path.Combine(Path.GetTempPath(), "erewhon-capture-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Full("harness/config"));
        File.Copy(
            Path.Combine(FindRepoRoot(), "harness", "config", "case-matrix.json"), Full("harness/config/case-matrix.json"));
        Directory.CreateDirectory(Full("Erewhon/DataFiles"));
        File.WriteAllBytes(Full("Erewhon/DataFiles/Clients.txt"), "Hank Williams\t1 Main St\tWellington\n"u8.ToArray());
        _seedSha256 = SeedFingerprint.ComputeSha256Hex(Full("Erewhon/DataFiles"));
        Directory.CreateDirectory(Full("baseline/records/rest"));
        File.WriteAllBytes(Full(SentinelRecord), "{\"sentinel\":true}\n"u8.ToArray());
    }

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _console.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("missing-seed-directory", "seedSha256 'unavailable'")]
    [InlineData("malformed-reference-date", "referenceDate 'invalid'")]
    [InlineData("placeholder-manifest", "seedSha256 'unavailable'")]
    [InlineData("blank-manifest-fact", "referenceDate blank")]
    public async Task UnconfirmableSeedFacts_RefuseBeforeTheStandInIsContacted(string setup, string unconfirmable)
    {
        await using var standIn = await CaptureStandInStub.StartAsync(
            _seedSha256, ReferenceDate, failEmptyReset: false, TestCancellation);
        var options = Options(standIn);
        switch (setup)
        {
            case "missing-seed-directory":
                options.SeedDirectory = Full("Erewhon/Missing");
                break;
            case "malformed-reference-date":
                options.ExpectedReferenceDate = "01/01/2026";
                break;
            case "placeholder-manifest":
                WriteManifest(options, "unavailable", ReferenceDate);
                break;
            case "blank-manifest-fact":
                WriteManifest(options, _seedSha256, "");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(setup), setup, null);
        }

        var before = Snapshot();

        Assert.Equal(1, await CaptureAsync(options));

        Assert.Empty(standIn.Requests);
        Assert.Equal(before, Snapshot());
        Assert.EndsWith($"seed facts cannot be confirmed (expected {unconfirmable}); {NotTouched}", LastLine());
    }

    [Theory]
    [InlineData(false, ReferenceDateField)]
    [InlineData(false, SeedSha256Field)]
    [InlineData(true, ReferenceDateField)]
    [InlineData(true, SeedSha256Field)]
    public async Task UnconfirmedCaptureStartReset_RefusesBeforeAnyWrite(bool recapture, string mismatchedField)
    {
        await using var standIn = await CaptureStandInStub.StartAsync(
            mismatchedField == SeedSha256Field ? OtherSeedSha256 : _seedSha256,
            mismatchedField == ReferenceDateField ? OtherReferenceDate : ReferenceDate,
            failEmptyReset: false,
            TestCancellation);
        var options = Options(standIn);
        if (recapture)
        {
            WriteManifest(options, _seedSha256, ReferenceDate);
        }

        var before = Snapshot();

        Assert.Equal(1, await CaptureAsync(options));

        Assert.Equal(before, Snapshot());
        Assert.Equal(recapture, File.Exists(Full(ManifestFile)));
        Assert.Single(standIn.Requests, request => request == SeedResetRequest);
        Assert.All(standIn.Requests, request => Assert.Contains(request, AdminRequests));
        Assert.EndsWith($"{ResetPoints.CaptureStart} reset not confirmed; {NotTouched}", LastLine());
    }

    [Fact]
    public async Task UnconfirmedEmptyStateBracket_StopsTheCaptureAfterTheSeedRestoreWithNoManifest()
    {
        await using var standIn = await CaptureStandInStub.StartAsync(
            _seedSha256, ReferenceDate, failEmptyReset: true, TestCancellation);
        var options = Options(standIn);
        WriteManifest(options, _seedSha256, ReferenceDate);
        var emptyStateCase = CaseMatrix.Load(options.CaseMatrixPath, OperationCatalogue.Default)
            .Cases.Single(static definition => definition.IsEmptyState).Id;

        Assert.Equal(1, await CaptureAsync(options));

        Assert.Equal(
            [SeedResetRequest, EmptyResetRequest, SeedResetRequest],
            standIn.Requests.Where(static request => request.StartsWith("POST /admin/reset", StringComparison.Ordinal)));
        Assert.False(File.Exists(Full(ManifestFile)));
        Assert.False(File.Exists(Full(ManifestTemporaryFile)));
        Assert.False(File.Exists(Full(ConformanceFile)));
        Assert.False(File.Exists(Full(ContractsWsdlFile)));
        Assert.EndsWith(
            $"reset bracket of {emptyStateCase} not confirmed; capture stopped, baseline/manifest.json not written",
            LastLine());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedCapture_PublishesTheConfirmedSeedFactsInTheManifestLast(bool recapture)
    {
        await using var standIn = await CaptureStandInStub.StartAsync(
            _seedSha256, ReferenceDate, failEmptyReset: false, TestCancellation);
        var options = Options(standIn);
        if (recapture)
        {
            WriteManifest(options, _seedSha256, ReferenceDate);
        }

        Assert.Equal(0, await CaptureAsync(options));

        Assert.True(new BaselineStore(options).TryReadManifest(out var manifest));
        Assert.Equal(_seedSha256, manifest.SeedSha256);
        Assert.Equal(ReferenceDate, manifest.ReferenceDate);
        Assert.Equal(new StandInInfo(_seedSha256, ReferenceDate, 1), manifest.StandIn);
        Assert.Equal(new ManifestCounts(22, 56, 22, 56), manifest.Counts);
        Assert.False(File.Exists(Full(ManifestTemporaryFile)));
        Assert.False(File.Exists(Full(SentinelRecord)));
        Assert.Contains("[jade-standin] baseline/manifest.json withdrawn until this capture completes", Lines());

        var published = File.GetLastWriteTimeUtc(Full(ManifestFile));
        Assert.All(
            Directory.EnumerateFiles(Full("baseline"), "*", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(Full("contracts"), "*", SearchOption.AllDirectories))
                .Where(file => file != Full(ManifestFile)),
            file => Assert.True(File.GetLastWriteTimeUtc(file) <= published, Relative(file)));
    }

    // The message is fixed: a path would differ between checkouts and runs.
    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "harness", "config")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(RepoRootNotFound);
    }

    // A manifest as an earlier capture wrote it, with the given seed facts.
    private static void WriteManifest(HarnessOptions options, string seedSha256, string referenceDate) =>
        new BaselineStore(options).WriteManifest(new BaselineManifest
        {
            StandIn = new StandInInfo(seedSha256, referenceDate, 2),
            SeedSha256 = seedSha256,
            ReferenceDate = referenceDate,
            Counts = new ManifestCounts(22, 56, 22, 56),
            NotCaptured = [],
            WsdlCaptured = true,
            StartingCommit = options.StartingCommit,
            CaptureCommit = "0123456789abcdef0123456789abcdef01234567",
            CaptureExecution = CaptureExecutionValues.LocalProcesses,
        });

    private HarnessOptions Options(CaptureStandInStub standIn) => new()
    {
        RepoRoot = _root,
        ExecutionMode = CaptureExecutionValues.LocalProcesses,
        SeedDirectory = Full("Erewhon/DataFiles"),
        ExpectedReferenceDate = ReferenceDate,
        ReadinessTimeoutSeconds = LimitSeconds,
        RequestTimeoutSeconds = LimitSeconds,
        Backend = new HarnessEndpoints
        {
            RestBaseUrl = standIn.BaseUrl,
            SoapBaseUrl = standIn.BaseUrl,
            AdminUrl = standIn.BaseUrl,
        },
    };

    private Task<int> CaptureAsync(HarnessOptions options) =>
        new CaptureMode(options, OperationCatalogue.Default, _console).RunAsync(TestCancellation);

    private string[] Lines() =>
        _console.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string LastLine() => Lines()[^1];

    private string Full(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private string Relative(string path) => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');

    // Every regular file below the temporary root, as "relative/path=hex" in ordinal order.
    private List<string> Snapshot() =>
    [
        .. Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(file => Relative(file) + "=" + Convert.ToHexStringLower(File.ReadAllBytes(file)))
            .Order(StringComparer.Ordinal),
    ];
}

/// <summary>
/// An in-process stand-in on 127.0.0.1 and an ephemeral port, as far as capture reaches it: <c>GET /health</c>
/// answers 200; <c>GET /admin/info</c> reports the source label, the given seed hash and reference date, the
/// reset generation and no mutations; <c>POST /admin/reset</c> increases the generation and answers 200, except
/// an empty-state reset when it is told to fail, which answers 500 and changes nothing; and every other request,
/// each case and the WSDL fetch, answers 200 with the JSON body <c>{}</c>. Every request is recorded as
/// <c>METHOD raw-target</c> before it is answered.
/// </summary>
internal sealed class CaptureStandInStub : IAsyncDisposable
{
    private const string JsonContentType = "application/json; charset=utf-8";
    private const string ResetPrefix = "POST " + AdminRoutes.Reset + "?" + AdminRoutes.ResetStateQueryKey + "=";

    // Bounds the graceful stop; idle keep-alive connections close at once, so it is never reached.
    private static readonly TimeSpan ShutdownLimit = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<string> _requests = new();
    private readonly string _seedSha256;
    private readonly string _referenceDate;
    private readonly bool _failEmptyReset;
    private WebApplication? _app;
    private long _generation;

    private CaptureStandInStub(string seedSha256, string referenceDate, bool failEmptyReset)
    {
        _seedSha256 = seedSha256;
        _referenceDate = referenceDate;
        _failEmptyReset = failEmptyReset;
    }

    /// <summary>The stub's base URL, <c>http://127.0.0.1:&lt;port&gt;</c>, for the REST, SOAP and admin URLs alike.</summary>
    public string BaseUrl { get; private set; } = "";

    /// <summary>Every request received so far, as <c>METHOD raw-target</c>, in arrival order.</summary>
    public IReadOnlyList<string> Requests => [.. _requests];

    /// <summary>Starts a stub reporting <paramref name="seedSha256"/> and <paramref name="referenceDate"/>.</summary>
    /// <param name="seedSha256">The seed hash <c>/admin/info</c> reports.</param>
    /// <param name="referenceDate">The reference date <c>/admin/info</c> reports.</param>
    /// <param name="failEmptyReset">Whether an empty-state reset answers 500.</param>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>The running stub, which the caller disposes.</returns>
    public static async Task<CaptureStandInStub> StartAsync(
        string seedSha256, string referenceDate, bool failEmptyReset, CancellationToken ct)
    {
        var stub = new CaptureStandInStub(seedSha256, referenceDate, failEmptyReset);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownLimit);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));

        var app = builder.Build();
        app.Run(stub.HandleAsync);
        try
        {
            await app.StartAsync(ct);
            var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
            stub.BaseUrl = new Uri(address).GetLeftPart(UriPartial.Authority);
            stub._app = app;
            return stub;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>Stops the server and releases its port.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_app is { } app)
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        var target = context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget;
        var request = $"{context.Request.Method} {target}";
        _requests.Enqueue(request);

        var (status, body) = Answer(request);
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = JsonContentType;
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private (int Status, string Body) Answer(string request)
    {
        if (request == "GET " + AdminRoutes.Health)
        {
            return (StatusCodes.Status200OK, "{}");
        }

        if (request == "GET " + AdminRoutes.Info)
        {
            var info = new JsonObject
            {
                ["source"] = SourceLabels.StandIn,
                ["seedSha256"] = _seedSha256,
                ["referenceDate"] = _referenceDate,
                ["generation"] = Interlocked.Read(ref _generation),
                ["mutations"] = 0,
            };
            return (StatusCodes.Status200OK, info.ToJsonString());
        }

        if (request.StartsWith(ResetPrefix, StringComparison.Ordinal))
        {
            if (_failEmptyReset && request[ResetPrefix.Length..] == AdminRoutes.ResetStateEmpty)
            {
                return (StatusCodes.Status500InternalServerError, "{}");
            }

            Interlocked.Increment(ref _generation);
            return (StatusCodes.Status200OK, "{}");
        }

        return (StatusCodes.Status200OK, "{}");
    }
}

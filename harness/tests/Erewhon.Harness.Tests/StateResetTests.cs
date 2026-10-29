using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Erewhon.Contracts;
using Erewhon.Contracts.Admin;
using Erewhon.Contracts.Seed;
using Erewhon.Harness.Lifecycle;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Tests;

/// <summary>
/// State-reset confirmation against scripted admin responses: after every reset the generation must be
/// proven to have increased, the mutation count must be 0 and the source must be the stand-in, and a
/// first capture always confirms the reference date it expects. Every mismatch is one
/// <c>CHK-RESET:&lt;point&gt;</c> item with a deterministic <c>mismatch:&lt;field&gt;:&lt;observed&gt;</c>
/// nature. No request leaves the process: the admin URL names a reserved <c>.invalid</c> host that only
/// the scripted handler answers.
/// </summary>
public sealed class StateResetTests : IDisposable
{
    private const string AdminUrl = "http://standin.invalid:9090";

    private const string GenerationField = "generation";
    private const string MutationsField = "mutations";
    private const string ReferenceDateField = "referenceDate";
    private const string ExpectedReferenceDateKey = "Harness:ExpectedReferenceDate";

    private const string DefaultReferenceDate = "2026-01-01";
    private const string OtherReferenceDate = "2025-06-30";
    private const long PreviousGeneration = 5;

    private const string InfoRequest = "GET " + AdminRoutes.Info;
    private const string SeedResetRequest =
        "POST " + AdminRoutes.Reset + "?" + AdminRoutes.ResetStateQueryKey + "=" + AdminRoutes.ResetStateSeed;

    private readonly string _seedDirectory;
    private readonly string _seedSha256;
    private readonly StringWriter _console = new(CultureInfo.InvariantCulture);

    /// <summary>Creates a one-file seed directory, so a first capture has a fingerprint to expect.</summary>
    public StateResetTests()
    {
        _seedDirectory = Directory.CreateTempSubdirectory("erewhon-statereset-").FullName;
        File.WriteAllText(Path.Combine(_seedDirectory, "Company.txt"), "Erewhon Investments Inc\tErewhon House\n");
        _seedSha256 = SeedFingerprint.ComputeSha256Hex(_seedDirectory);
    }

    /// <summary>How the read before the reset ends.</summary>
    public enum PreResetRead
    {
        /// <summary>HTTP 503.</summary>
        ServiceUnavailable,

        /// <summary>The connection is refused.</summary>
        ConnectionRefused,

        /// <summary>The response ends prematurely.</summary>
        ConnectionReset,

        /// <summary>A 2xx body that is not a JSON object.</summary>
        NotJson,

        /// <summary>A valid info object without a generation.</summary>
        NoGeneration,
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _console.Dispose();
        Directory.Delete(_seedDirectory, recursive: true);
    }

    // ---- Generation -----------------------------------------------------------------------------

    [Theory]
    [InlineData(PreResetRead.ServiceUnavailable, "before-503")]
    [InlineData(PreResetRead.ConnectionRefused, "before-connect")]
    [InlineData(PreResetRead.ConnectionReset, "before-reset")]
    [InlineData(PreResetRead.NotJson, "before-invalid")]
    [InlineData(PreResetRead.NoGeneration, "before-null")]
    public async Task PreResetGenerationUnknown_RecordsOneGenerationMismatch_AndStillResets(
        PreResetRead preRead,
        string observed)
    {
        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Respond(preRead))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1)));

        var result = await ResetAsync(admin, ResetExpectations.FromManifest(_seedSha256, DefaultReferenceDate));

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.ReplayStart), item.ItemId);
        Assert.Equal(FailureItemIds.Mismatch(GenerationField, observed), item.Nature);
        Assert.Contains("pre-reset generation is unavailable", item.Detail, StringComparison.Ordinal);
        Assert.Equal(new[] { InfoRequest, SeedResetRequest, InfoRequest }, admin.Requests);
        Assert.Equal(PreviousGeneration + 1, result.After?.Generation);
    }

    [Fact]
    public async Task PreResetReadFailure_StillChecksTheOtherFields()
    {
        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Respond(PreResetRead.ServiceUnavailable))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, mutations: 3)));

        var result = await ResetAsync(admin, ResetExpectations.None);

        var itemId = FailureItemIds.Reset(ResetPoints.ReplayStart);
        Assert.Equal(
            new[]
            {
                (itemId, FailureItemIds.Mismatch(GenerationField, "before-503")),
                (itemId, FailureItemIds.Mismatch(MutationsField, "3")),
            },
            result.Failures.Select(failure => (failure.ItemId, failure.Nature)));
    }

    [Theory]
    [InlineData(PreviousGeneration)]
    [InlineData(PreviousGeneration - 1)]
    [InlineData(0L)]
    public async Task GenerationNotIncreased_RecordsTheObservedGeneration(long after)
    {
        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(after)));

        var result = await ResetAsync(admin, ResetExpectations.None);

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.ReplayStart), item.ItemId);
        Assert.Equal(
            FailureItemIds.Mismatch(GenerationField, after.ToString(CultureInfo.InvariantCulture)),
            item.Nature);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PostResetGenerationMissing_RecordsOneNullMismatch(bool preReadSucceeds)
    {
        var admin = new ScriptedAdmin()
            .Then(
                InfoRequest,
                () => preReadSucceeds ? Json(Info(PreviousGeneration)) : Respond(PreResetRead.ServiceUnavailable))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(generation: null)));

        var result = await ResetAsync(admin, ResetExpectations.None);

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.ReplayStart), item.ItemId);
        Assert.Equal(FailureItemIds.Mismatch(GenerationField, "null"), item.Nature);
    }

    [Fact]
    public async Task GenerationIncreased_WithMatchingFacts_Succeeds()
    {
        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1)));

        var result = await ResetAsync(admin, ResetExpectations.FromManifest(_seedSha256, DefaultReferenceDate));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Equal(new[] { InfoRequest, SeedResetRequest, InfoRequest }, admin.Requests);
        Assert.Contains(
            $"{SourceLabels.ConsolePrefix} reset {AdminRoutes.ResetStateSeed} at {ResetPoints.ReplayStart}: generation 6",
            _console.ToString(),
            StringComparison.Ordinal);
    }

    // ---- First-capture reference date -----------------------------------------------------------

    [Theory]
    [InlineData("", DefaultReferenceDate)]
    [InlineData("   ", DefaultReferenceDate)]
    [InlineData(" 2025-06-30 ", OtherReferenceDate)]
    [InlineData("2026-1-1", "2026-1-1")]
    public void EffectiveExpectedReferenceDate_BlankIsTheDefault_OtherwiseTrimmed(string configured, string effective)
    {
        var options = new HarnessOptions { ExpectedReferenceDate = configured };

        Assert.Equal(effective, options.EffectiveExpectedReferenceDate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FirstCapture_BlankExpectedDate_ExpectsTheDefault(string configured)
    {
        var options = Options(configured);

        var expected = ResetExpectations.FirstCapture(options, _console);

        Assert.Equal(new ResetExpectations(_seedSha256, DefaultReferenceDate), expected);
        Assert.Contains(
            $"{SourceLabels.ConsolePrefix} {ExpectedReferenceDateKey} is blank; expecting the default reference date {DefaultReferenceDate}",
            _console.ToString(),
            StringComparison.Ordinal);

        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration, OtherReferenceDate)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, OtherReferenceDate)));

        var result = await ResetAsync(admin, expected, options, ResetPoints.CaptureStart);

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.CaptureStart), item.ItemId);
        Assert.Equal(FailureItemIds.Mismatch(ReferenceDateField, OtherReferenceDate), item.Nature);
    }

    [Theory]
    [InlineData("2026-1-1", DefaultReferenceDate)]
    [InlineData("01/01/2026", DefaultReferenceDate)]
    [InlineData("2026-01-01T00:00:00", DefaultReferenceDate)]
    [InlineData("not-a-date", "not-a-date")]
    public async Task FirstCapture_InvalidExpectedDate_AlwaysRecordsReferenceDateMismatch(
        string configured,
        string reported)
    {
        var options = Options(configured);

        var expected = ResetExpectations.FirstCapture(options, _console);

        Assert.Equal(_seedSha256, expected.SeedSha256);

        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration, reported)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, reported)));

        var result = await ResetAsync(admin, expected, options, ResetPoints.CaptureStart);

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.CaptureStart), item.ItemId);
        Assert.Equal(FailureItemIds.Mismatch(ReferenceDateField, reported), item.Nature);
        Assert.Contains($"{ExpectedReferenceDateKey} is not a yyyy-MM-dd date", item.Detail, StringComparison.Ordinal);
        Assert.Contains(
            $"{SourceLabels.ConsolePrefix} {ExpectedReferenceDateKey} '{configured}' is not a yyyy-MM-dd date",
            _console.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DefaultReferenceDate, DefaultReferenceDate)]
    [InlineData(OtherReferenceDate, OtherReferenceDate)]
    [InlineData(" 2025-06-30 ", OtherReferenceDate)]
    public async Task FirstCapture_ValidExpectedDate_Passes(string configured, string reported)
    {
        var options = Options(configured);

        var expected = ResetExpectations.FirstCapture(options, _console);

        Assert.Equal(new ResetExpectations(_seedSha256, reported), expected);

        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration, reported)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, reported)));

        var result = await ResetAsync(admin, expected, options, ResetPoints.CaptureStart);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.DoesNotContain(ExpectedReferenceDateKey, _console.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", "\t")]
    public async Task FromManifest_BlankValues_AreNotChecked(string? seedSha256, string? referenceDate)
    {
        var expected = ResetExpectations.FromManifest(seedSha256, referenceDate);

        Assert.Null(expected.SeedSha256);
        Assert.Null(expected.ReferenceDate);

        var otherSeed = new string('0', 64);
        var admin = new ScriptedAdmin()
            .Then(InfoRequest, () => Json(Info(PreviousGeneration, OtherReferenceDate, otherSeed)))
            .Then(SeedResetRequest, ResetAccepted)
            .Then(InfoRequest, () => Json(Info(PreviousGeneration + 1, OtherReferenceDate, otherSeed)));

        var result = await ResetAsync(admin, expected);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Failures);
    }

    // ---- Admin URL ------------------------------------------------------------------------------

    // The second URL has no scheme, so it parses with the user name as its scheme.
    [Theory]
    [InlineData("http://alice:example-pass@standin.invalid:9090")]
    [InlineData("alice:example-pass@standin.invalid:9090")]
    [InlineData("ftp://alice:example-pass@standin.invalid:9090")]
    public async Task CredentialBearingAdminUrl_IsRejectedBeforeAnyRequest_WithoutDisclosingIt(string adminUrl)
    {
        var options = Options();
        options.Backend.AdminUrl = adminUrl;
        var admin = new ScriptedAdmin();

        var result = await ResetAsync(admin, ResetExpectations.None, options);

        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.Reset(ResetPoints.ReplayStart), item.ItemId);
        Assert.Equal(FailureItemIds.Mismatch("http", "invalid-url"), item.Nature);
        Assert.Empty(admin.Requests);

        var reader = new ScriptedAdmin();
        using (var reset = new StateReset(options, reader, _console))
        {
            Assert.Null(await reset.ReadInfoAsync(adminUrl, TestContext.Current.CancellationToken));
        }

        Assert.Empty(reader.Requests);

        Assert.NotNull(item.Detail);
        foreach (var credential in new[] { "example-pass", "alice" })
        {
            Assert.DoesNotContain(credential, item.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(credential, _console.ToString(), StringComparison.Ordinal);
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static HttpResponseMessage ResetAccepted() => new(HttpStatusCode.OK);

    private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private HarnessOptions Options(string? expectedReferenceDate = null)
    {
        var options = new HarnessOptions { SeedDirectory = _seedDirectory, RequestTimeoutSeconds = 5 };
        options.Backend.AdminUrl = AdminUrl;
        if (expectedReferenceDate is not null)
        {
            options.ExpectedReferenceDate = expectedReferenceDate;
        }

        return options;
    }

    // The stand-in's /admin/info members; generation is left out when null, as a backend without one would.
    private JsonObject Info(
        long? generation,
        string referenceDate = DefaultReferenceDate,
        string? seedSha256 = null,
        long mutations = 0)
    {
        var info = new JsonObject
        {
            ["source"] = SourceLabels.StandIn,
            ["seedSha256"] = seedSha256 ?? _seedSha256,
            ["referenceDate"] = referenceDate,
            ["mutations"] = mutations,
        };
        if (generation is { } value)
        {
            info["generation"] = value;
        }

        return info;
    }

    private HttpResponseMessage Respond(PreResetRead preRead) => preRead switch
    {
        PreResetRead.ServiceUnavailable => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        PreResetRead.ConnectionRefused => throw new HttpRequestException(
            HttpRequestError.ConnectionError, "Connection refused (standin.invalid:9090)"),
        PreResetRead.ConnectionReset => throw new HttpRequestException(
            HttpRequestError.ResponseEnded, "The response ended prematurely."),
        PreResetRead.NotJson => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("starting", Encoding.UTF8, "text/plain"),
        },
        PreResetRead.NoGeneration => Json(Info(generation: null)),
        _ => throw new ArgumentOutOfRangeException(nameof(preRead), preRead, "Unknown pre-reset read."),
    };

    private async Task<StateResetResult> ResetAsync(
        ScriptedAdmin admin,
        ResetExpectations expected,
        HarnessOptions? options = null,
        string point = ResetPoints.ReplayStart)
    {
        using var reset = new StateReset(options ?? Options(), admin, _console);
        return await reset.ResetAsync(point, AdminRoutes.ResetStateSeed, expected, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Answers each admin request with the next scripted response, after checking it is the scripted
    /// method and path; an unscripted request fails the test instead of reaching the network.
    /// </summary>
    private sealed class ScriptedAdmin : HttpMessageHandler
    {
        private readonly Queue<(string Request, Func<HttpResponseMessage> Respond)> _script = new();

        /// <summary>Every request received, as <c>&lt;method&gt; &lt;path and query&gt;</c>.</summary>
        public List<string> Requests { get; } = [];

        public ScriptedAdmin Then(string request, Func<HttpResponseMessage> respond)
        {
            _script.Enqueue((request, respond));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var line = $"{request.Method} {request.RequestUri?.PathAndQuery}";
            Requests.Add(line);
            if (!_script.TryDequeue(out var step) || !string.Equals(step.Request, line, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unscripted admin request: {line}");
            }

            return Task.FromResult(step.Respond());
        }
    }
}

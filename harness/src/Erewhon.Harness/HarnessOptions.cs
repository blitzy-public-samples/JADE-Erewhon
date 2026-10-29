using Erewhon.Contracts.Admin;

namespace Erewhon.Harness;

/// <summary>
/// Base URLs of one component the harness talks to over HTTP: its REST, SOAP and admin listeners.
/// </summary>
public sealed class HarnessEndpoints
{
    /// <summary>REST listener base URL, for example <c>http://facade:65000</c>.</summary>
    public string RestBaseUrl { get; set; } = "";

    /// <summary>SOAP listener base URL, for example <c>http://facade:21001</c>.</summary>
    public string SoapBaseUrl { get; set; } = "";

    /// <summary>
    /// Admin listener base URL, for example <c>http://facade:9090</c>. Blank for an external backend,
    /// which has no admin listener.
    /// </summary>
    public string AdminUrl { get; set; } = "";
}

/// <summary>
/// Settings of the equivalence harness, bound from configuration section <see cref="SectionName"/>.
/// This class owns every harness default and every repository path the harness reads or writes, so no
/// other harness file hard-codes a path or a URL.
/// </summary>
/// <remarks>
/// <para>Sources, lowest precedence first: <c>harness/config/harness.json</c> (optional), environment
/// variables without a prefix (<c>Harness__Target__RestBaseUrl</c> is <c>Harness:Target:RestBaseUrl</c>),
/// then <c>--Harness:Key=value</c> arguments. Every default is a property initialiser, so a missing
/// <c>harness.json</c> still yields working values. Binding into <see cref="Target"/> and
/// <see cref="Backend"/> reuses their default instances, so a key that is not set keeps its default.</para>
/// <para>An empty environment value overrides a default: on .NET 10, <c>Harness__Backend__AdminUrl=</c>
/// binds <see cref="HarnessEndpoints.AdminUrl"/> to <c>""</c> (verified with
/// <c>ConfigurationBinder</c> and <c>AddEnvironmentVariables</c>), which makes
/// <see cref="IsExternalBackend"/> true without any post-processing by the caller.</para>
/// <para>No credential, token or header setting exists: no layer authenticates, and the admin listeners
/// are contained by network placement instead.</para>
/// </remarks>
public sealed class HarnessOptions
{
    /// <summary>Configuration section bound to this class.</summary>
    public const string SectionName = "Harness";

    /// <summary>
    /// Harness version recorded as the baseline manifest's <c>harnessVersion</c>. The project file carries
    /// no <c>Version</c>, so this constant is the only place it is stated.
    /// </summary>
    public const string HarnessVersion = "1.0.0";

    // Compose service names of the facade and the stand-in on the project networks.
    private const string FacadeHost = "facade";
    private const string StandInHost = "standin";

    // The ErewhonREST and WebServiceHTTP application ports of the JADE system
    // (Erewhon/ErehwhonExternalInterfacesSchema.ddx), which the facade and the stand-in both keep.
    private const int RestPort = 65000;
    private const int SoapPort = 21001;

    private const string DefaultRepoRoot = "/repo";
    private const string DefaultSeedDirectory = "Erewhon/DataFiles";
    private const string DefaultExpectedReferenceDate = "2026-01-01";
    private const string DefaultExecutionMode = "docker";
    private const int DefaultReadinessTimeoutSeconds = 120;
    private const int DefaultReadinessPollIntervalSeconds = 1;
    private const int DefaultRequestTimeoutSeconds = 60;
    private const int DefaultShutdownTimeoutSeconds = 15;
    private const int DefaultShutdownRequestTimeoutSeconds = 5;

    /// <summary>The facade, which replay sends every case to.</summary>
    public HarnessEndpoints Target { get; set; } = DefaultEndpoints(FacadeHost);

    /// <summary>
    /// The backend: the stand-in, which capture records from and replay resets, confirms and fetches the
    /// WSDL from. A blank <see cref="HarnessEndpoints.AdminUrl"/> marks an external backend.
    /// </summary>
    public HarnessEndpoints Backend { get; set; } = DefaultEndpoints(StandInHost);

    /// <summary>
    /// Repository root: the <c>/repo</c> bind mount under Compose, the working tree under
    /// <c>harness/scripts/run-local.sh</c>. Every relative path resolves against it; blank means
    /// <c>/repo</c>.
    /// </summary>
    public string RepoRoot { get; set; } = DefaultRepoRoot;

    /// <summary>
    /// Where <c>RESULTS.md</c> is written; null or blank means <c>&lt;RepoRoot&gt;/RESULTS.md</c>.
    /// Read through <see cref="EffectiveReportPath"/>.
    /// </summary>
    public string? ReportPath { get; set; }

    /// <summary><c>docker</c> or <c>local-processes</c>, as stated in the report's run facts.</summary>
    public string ExecutionMode { get; set; } = DefaultExecutionMode;

    /// <summary>
    /// The starting commit ("Add documentation to repo") that the JADE-unchanged check compares
    /// <c>Erewhon/</c> against.
    /// </summary>
    public string StartingCommit { get; set; } = "19a5189e9075349aca54de1fb5fdf2d8727403e7";

    /// <summary>
    /// Expected stand-in reference date (<c>yyyy-MM-dd</c>) at first capture; equal to the stand-in's
    /// <c>StandIn:ReferenceDate</c>. Blank means <c>2026-01-01</c>. Read through
    /// <see cref="EffectiveExpectedReferenceDate"/>.
    /// </summary>
    public string ExpectedReferenceDate { get; set; } = DefaultExpectedReferenceDate;

    /// <summary>
    /// Seed directory fingerprinted at first capture, relative to <see cref="RepoRoot"/> unless absolute;
    /// blank means <c>Erewhon/DataFiles</c>. Read through <see cref="EffectiveSeedDirectory"/>.
    /// </summary>
    public string SeedDirectory { get; set; } = DefaultSeedDirectory;

    /// <summary>How long each component's <c>GET /health</c> is polled before it counts as not ready.</summary>
    public int ReadinessTimeoutSeconds { get; set; } = DefaultReadinessTimeoutSeconds;

    /// <summary>Interval between readiness polls.</summary>
    public int ReadinessPollIntervalSeconds { get; set; } = DefaultReadinessPollIntervalSeconds;

    /// <summary>Limit for one case request.</summary>
    public int RequestTimeoutSeconds { get; set; } = DefaultRequestTimeoutSeconds;

    /// <summary>Limit for shutting down one component, from its shutdown call to its confirmed stop.</summary>
    public int ShutdownTimeoutSeconds { get; set; } = DefaultShutdownTimeoutSeconds;

    /// <summary>Limit for one <c>POST /admin/shutdown</c> request.</summary>
    public int ShutdownRequestTimeoutSeconds { get; set; } = DefaultShutdownRequestTimeoutSeconds;

    /// <summary>
    /// True when the backend has no admin URL. Replay then skips the reset and the seed confirmation,
    /// never shuts the backend down, and the report records the substitution KL-SUB-NORESET.
    /// </summary>
    public bool IsExternalBackend => string.IsNullOrWhiteSpace(Backend.AdminUrl);

    /// <summary>Report path: <see cref="ReportPath"/> resolved, or <c>&lt;RepoRoot&gt;/RESULTS.md</c>.</summary>
    public string EffectiveReportPath =>
        ResolvePath(string.IsNullOrWhiteSpace(ReportPath) ? "RESULTS.md" : ReportPath);

    /// <summary><see cref="SeedDirectory"/> resolved.</summary>
    public string EffectiveSeedDirectory =>
        ResolvePath(string.IsNullOrWhiteSpace(SeedDirectory) ? DefaultSeedDirectory : SeedDirectory);

    /// <summary>
    /// <see cref="ExpectedReferenceDate"/> trimmed, or <c>2026-01-01</c> when it is blank (for example an
    /// empty <c>Harness__ExpectedReferenceDate</c>), so the first-capture date check always has a value.
    /// The value is not validated here; the first capture requires it to be a <c>yyyy-MM-dd</c> date.
    /// </summary>
    public string EffectiveExpectedReferenceDate =>
        string.IsNullOrWhiteSpace(ExpectedReferenceDate) ? DefaultExpectedReferenceDate : ExpectedReferenceDate.Trim();

    /// <summary><c>&lt;RepoRoot&gt;/baseline</c>, the only tree capture writes records into.</summary>
    public string BaselineDirectory => ResolvePath("baseline");

    /// <summary><c>baseline/manifest.json</c>.</summary>
    public string ManifestPath => ResolvePath(Path.Combine("baseline", "manifest.json"));

    /// <summary><c>baseline/records</c>, holding the <c>rest</c> and <c>soap</c> record directories.</summary>
    public string RecordsDirectory => ResolvePath(Path.Combine("baseline", "records"));

    /// <summary><c>baseline/normalisation-rules.json</c>.</summary>
    public string NormalisationRulesPath => ResolvePath(Path.Combine("baseline", "normalisation-rules.json"));

    /// <summary><c>baseline/conformance.json</c>.</summary>
    public string ConformancePath => ResolvePath(Path.Combine("baseline", "conformance.json"));

    /// <summary><c>baseline/wsdl/standin-ErewhonWebService.wsdl</c>, the WSDL bytes the stand-in served.</summary>
    public string BaselineWsdlPath =>
        ResolvePath(Path.Combine("baseline", "wsdl", "standin-ErewhonWebService.wsdl"));

    /// <summary><c>contracts/wsdl/ErewhonWebService.wsdl</c>, the WSDL the facade publishes.</summary>
    public string ContractsWsdlPath => ResolvePath(Path.Combine("contracts", "wsdl", "ErewhonWebService.wsdl"));

    /// <summary><c>contracts/openapi/erewhon-rest.openapi.json</c>, the checked-in OpenAPI description.</summary>
    public string ContractsOpenApiPath =>
        ResolvePath(Path.Combine("contracts", "openapi", "erewhon-rest.openapi.json"));

    /// <summary><c>harness/config/case-matrix.json</c>.</summary>
    public string CaseMatrixPath => ResolvePath(Path.Combine("harness", "config", "case-matrix.json"));

    /// <summary><c>harness/config/known-limitations.json</c>.</summary>
    public string KnownLimitationsPath => ResolvePath(Path.Combine("harness", "config", "known-limitations.json"));

    /// <summary><c>harness/config/harness.json</c>.</summary>
    public string HarnessConfigPath => ResolvePath(Path.Combine("harness", "config", "harness.json"));

    /// <summary><c>artifacts</c>, the per-run output tree.</summary>
    public string ArtifactsDirectory => ResolvePath("artifacts");

    /// <summary>
    /// <c>artifacts/facade-tests</c>, where the facade tests write their TRX, their xunit XML (which records
    /// each failure's exception type) and their exit code.
    /// </summary>
    public string FacadeTestsDirectory => ResolvePath(Path.Combine("artifacts", "facade-tests"));

    /// <summary><see cref="ReadinessTimeoutSeconds"/> as a duration.</summary>
    public TimeSpan ReadinessTimeout => Seconds(ReadinessTimeoutSeconds, DefaultReadinessTimeoutSeconds);

    /// <summary><see cref="ReadinessPollIntervalSeconds"/> as a duration.</summary>
    public TimeSpan ReadinessPollInterval =>
        Seconds(ReadinessPollIntervalSeconds, DefaultReadinessPollIntervalSeconds);

    /// <summary><see cref="RequestTimeoutSeconds"/> as a duration.</summary>
    public TimeSpan RequestTimeout => Seconds(RequestTimeoutSeconds, DefaultRequestTimeoutSeconds);

    /// <summary><see cref="ShutdownTimeoutSeconds"/> as a duration.</summary>
    public TimeSpan ShutdownTimeout => Seconds(ShutdownTimeoutSeconds, DefaultShutdownTimeoutSeconds);

    /// <summary><see cref="ShutdownRequestTimeoutSeconds"/> as a duration.</summary>
    public TimeSpan ShutdownRequestTimeout =>
        Seconds(ShutdownRequestTimeoutSeconds, DefaultShutdownRequestTimeoutSeconds);

    // Blank RepoRoot (for example an empty Harness__RepoRoot) falls back to the default, so a path can
    // never silently resolve against the current directory.
    private string Root => string.IsNullOrWhiteSpace(RepoRoot) ? DefaultRepoRoot : RepoRoot;

    /// <summary>
    /// Returns <paramref name="path"/> unchanged when it is absolute; otherwise resolves it against
    /// <see cref="RepoRoot"/>.
    /// </summary>
    /// <param name="path">An absolute path, or a path relative to the repository root.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    public string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(Path.Combine(Root, path));
    }

    private static HarnessEndpoints DefaultEndpoints(string host) => new()
    {
        RestBaseUrl = $"http://{host}:{RestPort}",
        SoapBaseUrl = $"http://{host}:{SoapPort}",
        AdminUrl = $"http://{host}:{AdminRoutes.DefaultPort}",
    };

    // A zero or negative limit falls back to its default, so every readiness, request and shutdown wait
    // stays bounded and positive.
    private static TimeSpan Seconds(int configured, int fallback) =>
        TimeSpan.FromSeconds(configured > 0 ? configured : fallback);
}

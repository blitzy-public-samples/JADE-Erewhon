using System.Text.Json.Serialization;
using Erewhon.Contracts;

namespace Erewhon.Harness.Baseline;

/// <summary>
/// The values <see cref="BaselineManifest.CaptureExecution"/> takes: how the capture that produced the
/// baseline was executed.
/// </summary>
public static class CaptureExecutionValues
{
    /// <summary>Captured by <c>harness/scripts/run-local.sh capture</c> against a local stand-in process.</summary>
    public const string LocalProcesses = "local-processes";

    /// <summary>Captured with the stand-in running under Docker Compose.</summary>
    public const string Docker = "docker";

    /// <summary>
    /// Capture could not start, and the execution agent wrote the manifest by hand. Such a manifest holds
    /// planned counts, every case id in <c>notCaptured</c> and the blocking error, but no stand-in fact.
    /// </summary>
    public const string NotRun = "not-run";
}

/// <summary>
/// The stand-in's <c>/admin/info</c> values read at capture start, after the seed reset. Serialised as the
/// manifest's <c>standIn</c> object; each member is null when the info read did not supply it.
/// </summary>
/// <param name="SeedSha256">The seed fingerprint the stand-in reported, lowercase hexadecimal.</param>
/// <param name="ReferenceDate">The reference date the stand-in reported, <c>yyyy-MM-dd</c>.</param>
/// <param name="Generation">The stand-in's reset generation after the capture-start reset.</param>
public sealed record StandInInfo(string? SeedSha256, string? ReferenceDate, long? Generation);

/// <summary>
/// The case and operation counts of one capture. Serialised as the manifest's <c>counts</c> object.
/// </summary>
/// <param name="PlannedOperations">Operations the case matrix plans cases for (22).</param>
/// <param name="PlannedCases">Cases in the case matrix (56).</param>
/// <param name="CapturedOperations">Distinct operations with at least one captured record.</param>
/// <param name="CapturedCases">Records written.</param>
public sealed record ManifestCounts(int PlannedOperations, int PlannedCases, int CapturedOperations, int CapturedCases);

/// <summary>
/// <c>baseline/manifest.json</c>: where one capture came from, the seed facts every later reset is
/// confirmed against, the counts, and the cases the stand-in did not answer.
/// </summary>
/// <remarks>
/// <para>The properties serialise in declaration order under the baseline store's camel-case options, with
/// null members written, so the declaration order is the file's key order. The type holds no wall-clock
/// time, machine name, path or duration: a re-capture of an unchanged stand-in must reproduce the file byte
/// for byte, apart from <see cref="CaptureCommit"/>.</para>
/// <para>Reading is tolerant, because the execution agent hand-writes the manifest when capture cannot
/// start. Every member is optional and keeps its default when absent, and unknown members are skipped;
/// <see cref="SourceStated"/> and <see cref="LabelStated"/> record whether the provenance members were
/// stated, because their defaults alone would present an unlabelled file as labelled. A JSON <c>null</c>
/// for a member typed non-nullable is read as that member's empty value, so a consumer never meets a null
/// where the type promises none.</para>
/// <para>The compiler-generated equality compares <see cref="NotCaptured"/> by reference and includes
/// <see cref="SourceStated"/> and <see cref="LabelStated"/>; compare the contents explicitly where two
/// manifests read separately must be judged equal.</para>
/// </remarks>
public sealed record BaselineManifest
{
    private static readonly ManifestCounts Zero = new(0, 0, 0, 0);

    // An omitted source or label keeps its default, so a manifest built in code serialises labelled, but it is
    // recorded as not stated: the initialiser sets the field directly, and only setting the member (in an
    // object initializer, a with-expression or deserialisation) runs the init accessor that records it as
    // stated. A JSON null source or label is stated and reads as "" rather than the default, so a file that
    // states no label is never presented as labelled stand-in output.

    /// <summary>The source label, <see cref="SourceLabels.StandIn"/> (<c>jade-standin</c>).</summary>
    public string Source
    {
        get;
        init
        {
            field = value ?? "";
            SourceStated = true;
        }
    } = SourceLabels.StandIn;

    /// <summary>The label text, <see cref="SourceLabels.BaselineLabel"/>.</summary>
    public string Label
    {
        get;
        init
        {
            field = value ?? "";
            LabelStated = true;
        }
    } = SourceLabels.BaselineLabel;

    /// <summary>
    /// Whether <see cref="Source"/> was stated: set by an object initializer or by a <c>source</c> member in
    /// the file, a JSON <c>null</c> or empty one included. False when the member was omitted, while
    /// <see cref="Source"/> still holds its default. Never serialised.
    /// </summary>
    [JsonIgnore]
    public bool SourceStated { get; private init; }

    /// <summary>
    /// Whether <see cref="Label"/> was stated: set by an object initializer or by a <c>label</c> member in
    /// the file, a JSON <c>null</c> or empty one included. False when the member was omitted, while
    /// <see cref="Label"/> still holds its default. Never serialised.
    /// </summary>
    [JsonIgnore]
    public bool LabelStated { get; private init; }

    /// <summary>
    /// The stand-in's <c>/admin/info</c> values at capture start; null in a
    /// <see cref="CaptureExecutionValues.NotRun"/> manifest, where nothing was observed.
    /// </summary>
    public StandInInfo? StandIn { get; init; }

    /// <summary>
    /// The confirmed seed hash every re-capture and replay requires the stand-in to report, lowercase
    /// hexadecimal as produced by <c>Erewhon.Contracts.Seed.SeedFingerprint</c>.
    /// </summary>
    public string? SeedSha256 { get; init; }

    /// <summary>The confirmed reference date, <c>yyyy-MM-dd</c> (for example <c>2026-01-01</c>).</summary>
    public string? ReferenceDate { get; init; }

    /// <summary>The planned and captured counts; all zero when absent.</summary>
    public ManifestCounts Counts { get; init => field = value ?? Zero; } = Zero;

    /// <summary>
    /// Ids of the planned cases the stand-in did not answer, in case-matrix order. Replay reports each as
    /// a <c>CASE:&lt;caseId&gt;</c> item with nature <c>not-captured</c>. Never null.
    /// </summary>
    public IReadOnlyList<string> NotCaptured { get; init => field = value ?? []; } = [];

    /// <summary>Whether <c>baseline/wsdl/standin-ErewhonWebService.wsdl</c> was captured.</summary>
    public bool WsdlCaptured { get; init; }

    /// <summary>The version of the harness that wrote the manifest.</summary>
    public string? HarnessVersion { get; init; } = HarnessOptions.HarnessVersion;

    /// <summary>
    /// The starting commit the JADE-unchanged check compares against,
    /// <c>19a5189e9075349aca54de1fb5fdf2d8727403e7</c>.
    /// </summary>
    public string? StartingCommit { get; init; }

    /// <summary>The repository's <c>HEAD</c> at capture, or null when it could not be read.</summary>
    public string? CaptureCommit { get; init; }

    /// <summary>How the capture was executed, one of <see cref="CaptureExecutionValues"/>.</summary>
    public string? CaptureExecution { get; init; }

    /// <summary>
    /// The quoted error that kept capture from starting in a <see cref="CaptureExecutionValues.NotRun"/>
    /// manifest; null otherwise.
    /// </summary>
    public string? BlockingError { get; init; }

    /// <summary>
    /// Whether the manifest holds the seed facts a reset is confirmed against. When false, replay checks
    /// only the stand-in's generation, mutation count and source.
    /// </summary>
    [JsonIgnore]
    public bool HasSeedFacts =>
        SeedSha256 is not null && ReferenceDate is not null && CaptureExecution != CaptureExecutionValues.NotRun;

    /// <summary>Whether the manifest records that capture could not start.</summary>
    [JsonIgnore]
    public bool IsNotRun => CaptureExecution == CaptureExecutionValues.NotRun;
}

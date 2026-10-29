using System.Globalization;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Checks;

/// <summary>The outcome of <see cref="OrderingEvidence.RunAsync"/>.</summary>
/// <param name="Result">The check's report row: name <c>ordering</c>, outcome <c>pass</c>, <c>fail</c> or <c>not-run</c>.</param>
/// <param name="Failure">
/// The <c>CHK-ORDERING</c> item with nature <c>not-ancestor</c>, <c>no-facade-commit</c> or <c>not-run</c>;
/// null on a pass.
/// </param>
/// <param name="BaselineCommit">
/// The full id of the commit that first added <c>baseline/manifest.json</c>, for the report's run facts;
/// null when no commit adds it or git could not be asked. Kept on a failure too.
/// </param>
/// <param name="FacadeCommit">
/// The full id of the commit that first added a path under <c>facade/</c>; null when no commit adds one
/// or git could not be asked. Kept on a failure too.
/// </param>
public sealed record OrderingResult(CheckResult Result, FailureItem? Failure, string? BaselineCommit, string? FacadeCommit);

/// <summary>
/// Proves that the baseline was committed before any facade code: the commit that first added
/// <c>baseline/manifest.json</c> must be an ancestor of the commit that first added a path under
/// <c>facade/</c>.
/// </summary>
/// <remarks>
/// <para>Evaluation order, first match wins:</para>
/// <list type="number">
///   <item>No git metadata at the repository root, a shallow clone, or a git invocation that could not
///   start, timed out or exited non-zero: <c>not-run</c>, with the reason as detail.</item>
///   <item>No commit adds a <c>facade/</c> path: <c>no-facade-commit</c>. This is also the result on the
///   starting commit alone, where neither commit exists.</item>
///   <item>No commit adds the manifest, or the same commit adds both: <c>not-ancestor</c>, because the
///   baseline commit must contain no <c>facade/</c> path.</item>
///   <item><c>merge-base --is-ancestor</c> exits 0: pass; exits 1: <c>not-ancestor</c>; anything else:
///   <c>not-run</c>.</item>
/// </list>
/// <para>The check only reads. It runs <c>rev-parse</c>, <c>log</c> and <c>merge-base</c> through
/// <see cref="Git.RunAsync"/> and never a command that writes the index, the object store, the refs or
/// the working tree.</para>
/// </remarks>
/// <param name="options">The harness settings: <see cref="HarnessOptions.RepoRoot"/>.</param>
/// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
public sealed class OrderingEvidence(HarnessOptions options)
{
    private const string BaselinePathspec = "baseline/manifest.json";
    private const string FacadePathspec = "facade/";

    private const int ShortHashLength = 12;

    // `merge-base --is-ancestor` answers through its exit code; any other code is an error.
    private const int IsAncestorExitCode = 0;
    private const int IsNotAncestorExitCode = 1;

    // SHA-1 and SHA-256 object ids, as %H prints them.
    private const int Sha1HexLength = 40;
    private const int Sha256HexLength = 64;

    private const string ShallowAnswer = "true";

    private readonly HarnessOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Runs the check.</summary>
    /// <param name="ct">Cancels the check; the running git process is killed and the cancellation rethrown.</param>
    /// <returns>The report row, the failing item and the two commits found.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<OrderingResult> RunAsync(CancellationToken ct)
    {
        var repoRoot = _options.ResolvePath(".");
        if (!Git.HasGitMetadata(repoRoot))
        {
            return NotRun($"no .git metadata at {repoRoot}", baselineCommit: null, facadeCommit: null);
        }

        // In a shallow clone the boundary commit looks as if it added every file it holds, so the first
        // adding commit git reports could be an artefact of the truncation rather than evidence.
        string[] shallowArgs = ["rev-parse", "--is-shallow-repository"];
        var shallow = await Git.RunAsync(repoRoot, shallowArgs, Git.DefaultTimeout, ct).ConfigureAwait(false);
        if (!shallow.Succeeded)
        {
            return NotRun(CommandFailure(shallowArgs, shallow), baselineCommit: null, facadeCommit: null);
        }

        if (string.Equals(shallow.StdOut.Trim(), ShallowAnswer, StringComparison.Ordinal))
        {
            return NotRun(
                $"{repoRoot} is a shallow clone, so the commits that first added {BaselinePathspec} and {FacadePathspec} cannot be determined",
                baselineCommit: null,
                facadeCommit: null);
        }

        var baseline = await FirstAddingCommitAsync(repoRoot, BaselinePathspec, ct).ConfigureAwait(false);
        if (baseline.Error is { } baselineError)
        {
            return NotRun(baselineError, baselineCommit: null, facadeCommit: null);
        }

        var facade = await FirstAddingCommitAsync(repoRoot, FacadePathspec, ct).ConfigureAwait(false);
        if (facade.Error is { } facadeError)
        {
            return NotRun(facadeError, baseline.Commit, facadeCommit: null);
        }

        if (facade.Commit is not { } facadeCommit)
        {
            var detail = baseline.Commit is { } found
                ? $"no commit adds a path under {FacadePathspec}; baseline commit {Short(found)}"
                : $"no commit adds a path under {FacadePathspec} or adds {BaselinePathspec}";
            return Fail(FailureItemIds.NoFacadeCommit, detail, baseline.Commit, facadeCommit: null);
        }

        if (baseline.Commit is not { } baselineCommit)
        {
            return Fail(
                FailureItemIds.NotAncestor,
                $"no commit adds {BaselinePathspec}; first facade commit {Short(facadeCommit)}",
                baselineCommit: null,
                facadeCommit);
        }

        if (string.Equals(baselineCommit, facadeCommit, StringComparison.Ordinal))
        {
            return Fail(
                FailureItemIds.NotAncestor,
                $"baseline commit {Short(baselineCommit)} also adds the first path under {FacadePathspec}; the baseline commit must contain no {FacadePathspec} path",
                baselineCommit,
                facadeCommit);
        }

        string[] ancestorArgs = ["merge-base", "--is-ancestor", baselineCommit, facadeCommit];
        var ancestor = await Git.RunAsync(repoRoot, ancestorArgs, Git.DefaultTimeout, ct).ConfigureAwait(false);
        if (ancestor.Unavailable || ancestor.TimedOut)
        {
            return NotRun(CommandFailure(ancestorArgs, ancestor), baselineCommit, facadeCommit);
        }

        return ancestor.ExitCode switch
        {
            IsAncestorExitCode => new OrderingResult(
                new CheckResult(
                    CheckNames.Ordering,
                    CheckOutcomes.Pass,
                    1,
                    1,
                    $"baseline commit {Short(baselineCommit)} is an ancestor of the first facade commit {Short(facadeCommit)}"),
                Failure: null,
                baselineCommit,
                facadeCommit),
            IsNotAncestorExitCode => Fail(
                FailureItemIds.NotAncestor,
                $"baseline commit {Short(baselineCommit)} is not an ancestor of the first facade commit {Short(facadeCommit)}",
                baselineCommit,
                facadeCommit),
            _ => NotRun(CommandFailure(ancestorArgs, ancestor), baselineCommit, facadeCommit),
        };
    }

    // The commit that first added a path matching the pathspec, or null when none did.
    private static async Task<CommitLookup> FirstAddingCommitAsync(string repoRoot, string pathspec, CancellationToken ct)
    {
        // `--reverse` is applied after commit limiting, so `-n1` would yield the newest adding commit;
        // the first line of the full reversed list is the oldest instead.
        // `--full-history`: the default history simplification follows only one parent of a merge whose
        // tree matches it at the pathspec, which hides an earlier add of the same file on the other
        // side, and merged parallel histories routinely contain such merges.
        // `--date-order`: no commit is listed before its ancestors after reversal, even with clock skew.
        // `--root` and `--no-follow` keep the result independent of log.showRoot and log.follow.
        string[] args =
        [
            "log", "--full-history", "--date-order", "--root", "--no-follow",
            "--diff-filter=A", "--no-renames", "--reverse", "--format=%H", "--", pathspec,
        ];
        var log = await Git.RunAsync(repoRoot, args, Git.DefaultTimeout, ct).ConfigureAwait(false);
        if (!log.Succeeded)
        {
            return new CommitLookup(Commit: null, CommandFailure(args, log));
        }

        var first = log.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (first is null)
        {
            return new CommitLookup(Commit: null, Error: null);
        }

        return IsObjectId(first)
            ? new CommitLookup(first, Error: null)
            : new CommitLookup(Commit: null, $"git {string.Join(' ', args)} printed '{first}', which is not a commit id");
    }

    private static bool IsObjectId(string value) =>
        value.Length is Sha1HexLength or Sha256HexLength && value.All(char.IsAsciiHexDigitLower);

    private static string Short(string commit) =>
        commit.Length > ShortHashLength ? commit[..ShortHashLength] : commit;

    private static OrderingResult Fail(string nature, string detail, string? baselineCommit, string? facadeCommit) =>
        new(
            new CheckResult(CheckNames.Ordering, CheckOutcomes.Fail, 1, 0, detail),
            new FailureItem(FailureItemIds.Ordering, nature, Operation: null, Detail: detail),
            baselineCommit,
            facadeCommit);

    private static OrderingResult NotRun(string detail, string? baselineCommit, string? facadeCommit) =>
        new(
            new CheckResult(CheckNames.Ordering, CheckOutcomes.NotRun, 0, 0, detail),
            new FailureItem(FailureItemIds.Ordering, FailureItemIds.NotRun, Operation: null, Detail: detail),
            baselineCommit,
            facadeCommit);

    private static string CommandFailure(IReadOnlyList<string> args, GitResult result)
    {
        var outcome = result switch
        {
            { Unavailable: true } => "could not start",
            { TimedOut: true } => "timed out",
            _ => string.Create(CultureInfo.InvariantCulture, $"exited {result.ExitCode}"),
        };
        var stderr = OneLine(result.StdErr);
        return stderr.Length == 0
            ? $"git {string.Join(' ', args)} {outcome}"
            : $"git {string.Join(' ', args)} {outcome}: {stderr}";
    }

    // Report details are single lines; line breaks become spaces.
    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // Commit is the adding commit, or null when no commit matched; Error is set instead when git gave
    // no usable answer.
    private readonly record struct CommitLookup(string? Commit, string? Error);
}

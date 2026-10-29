using Erewhon.Harness.Checks;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Tests;

/// <summary>
/// The ordering-evidence check: the commit that first added <c>baseline/manifest.json</c> must be an
/// ancestor of the commit that first added a <c>facade/</c> path. Scripted git runners show that missing
/// metadata, shallow history, failing or malformed git answers and inconclusive ancestry give
/// <c>not-run</c>, and that absent or equal commits never pass; throwaway repositories in a temporary
/// directory confirm the verdicts against real git. The harness's own repository is never touched.
/// </summary>
public sealed class OrderingEvidenceTests : IDisposable
{
    private const string BaselinePathspec = "baseline/manifest.json";
    private const string FacadePathspec = "facade/";

    private const string BaselineCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FacadeCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string LaterBaselineCommit = "cccccccccccccccccccccccccccccccccccccccc";
    private const string LaterFacadeCommit = "dddddddddddddddddddddddddddddddddddddddd";

    private readonly TempDirectory _temp = new();

    /// <summary>Deletes the test's temporary directory and any repository in it.</summary>
    public void Dispose() => _temp.Dispose();

    // ---- Evidence git cannot give --------------------------------------------------------------

    [Fact]
    public async Task NoGitMetadata_IsNotRunWithoutCallingGit()
    {
        var git = Script();

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        AssertNotRun(result, "no .git metadata at ");
        Assert.Null(result.BaselineCommit);
        Assert.Null(result.FacadeCommit);
        Assert.Empty(git.Calls);
    }

    [Theory]
    [InlineData("rev-parse", "exited", 128, "fatal: not a git repository", "exited 128: fatal: not a git repository")]
    [InlineData("rev-parse", "unavailable", -1, "", "could not start")]
    [InlineData(BaselinePathspec, "exited", 128, "fatal: bad default revision 'HEAD'", "exited 128: fatal: bad default revision 'HEAD'")]
    [InlineData(BaselinePathspec, "timed-out", -1, "", "timed out")]
    [InlineData(FacadePathspec, "exited", 129, "", "exited 129")]
    public async Task FailingGitCommand_IsNotRun(string step, string failureKind, int exitCode, string stderr, string expectedReason)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var failure = ScriptedGit.Failure(failureKind, exitCode, stderr);
        var git = Script(
            shallow: step == "rev-parse" ? failure : null,
            baselineLog: step == BaselinePathspec ? failure : null,
            facadeLog: step == FacadePathspec ? failure : null);

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        var detail = AssertNotRun(result, "git ");
        Assert.Contains(expectedReason, detail, StringComparison.Ordinal);
        Assert.Equal(step == FacadePathspec ? BaselineCommit : null, result.BaselineCommit);
        Assert.Null(result.FacadeCommit);
        Assert.DoesNotContain(git.Calls, call => call.Args[0] == "merge-base");
    }

    [Fact]
    public async Task ShallowClone_IsNotRun()
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = Script(shallow: ScriptedGit.Ok("true\n"));

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        AssertNotRun(result, "is a shallow clone");
        var call = Assert.Single(git.Calls);
        string[] shallowArgs = ["rev-parse", "--is-shallow-repository"];
        Assert.Equal(shallowArgs, call.Args);
    }

    [Theory]
    [InlineData(BaselinePathspec, "not-a-hash")]
    [InlineData(BaselinePathspec, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData(BaselinePathspec, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(BaselinePathspec, "-x")]
    [InlineData(FacadePathspec, "not-a-hash")]
    [InlineData(FacadePathspec, "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    [InlineData(FacadePathspec, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData(FacadePathspec, "-x")]
    public async Task LogLineThatIsNoObjectId_IsNotRun(string pathspec, string firstLine)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var answer = ScriptedGit.Ok($"{firstLine}\n{LaterFacadeCommit}\n");
        var git = Script(
            baselineLog: pathspec == BaselinePathspec ? answer : null,
            facadeLog: pathspec == FacadePathspec ? answer : null);

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        var detail = AssertNotRun(result, $"printed '{firstLine}', which is not a commit id");
        Assert.Contains(pathspec, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(git.Calls, call => call.Args[0] == "merge-base");
    }

    // ---- Absent or equal commits ---------------------------------------------------------------

    [Theory]
    [InlineData(BaselineCommit + "\n", "no commit adds a path under facade/; baseline commit aaaaaaaaaaaa")]
    [InlineData("", "no commit adds a path under facade/ or adds baseline/manifest.json")]
    public async Task NoFacadeCommit_FailsWithNoFacadeCommit(string baselineLog, string expectedDetail)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = Script(baselineLog: ScriptedGit.Ok(baselineLog), facadeLog: ScriptedGit.Ok());

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        AssertFail(result, FailureItemIds.NoFacadeCommit, expectedDetail);
        Assert.Null(result.FacadeCommit);
        Assert.DoesNotContain(git.Calls, call => call.Args[0] == "merge-base");
    }

    [Fact]
    public async Task FacadeCommitWithoutBaselineCommit_IsNotAncestor()
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = Script(baselineLog: ScriptedGit.Ok());

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        AssertFail(result, FailureItemIds.NotAncestor, "no commit adds baseline/manifest.json; first facade commit bbbbbbbbbbbb");
        Assert.Null(result.BaselineCommit);
        Assert.Equal(FacadeCommit, result.FacadeCommit);
        Assert.DoesNotContain(git.Calls, call => call.Args[0] == "merge-base");
    }

    [Fact]
    public async Task SameCommitAddingBoth_IsNotAncestor()
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = Script(facadeLog: ScriptedGit.Ok(BaselineCommit + "\n"));

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        AssertFail(result, FailureItemIds.NotAncestor, "baseline commit aaaaaaaaaaaa also adds the first path under facade/; the baseline commit must contain no facade/ path");
        Assert.Equal(BaselineCommit, result.BaselineCommit);
        Assert.Equal(BaselineCommit, result.FacadeCommit);
        Assert.DoesNotContain(git.Calls, call => call.Args[0] == "merge-base");
    }

    // ---- Ancestry ------------------------------------------------------------------------------

    [Theory]
    [InlineData("exited", 0, CheckOutcomes.Pass, null)]
    [InlineData("exited", 1, CheckOutcomes.Fail, FailureItemIds.NotAncestor)]
    [InlineData("exited", 128, CheckOutcomes.NotRun, FailureItemIds.NotRun)]
    [InlineData("timed-out", -1, CheckOutcomes.NotRun, FailureItemIds.NotRun)]
    [InlineData("unavailable", -1, CheckOutcomes.NotRun, FailureItemIds.NotRun)]
    public async Task MergeBaseAnswer_DecidesTheVerdict(string answerKind, int exitCode, string expectedOutcome, string? expectedNature)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = Script(mergeBase: answerKind == "exited" && exitCode == 0
            ? ScriptedGit.Ok()
            : ScriptedGit.Failure(answerKind, exitCode, string.Empty));

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CheckNames.Ordering, result.Result.Name);
        Assert.Equal(expectedOutcome, result.Result.Outcome);
        Assert.Equal(expectedNature, result.Failure?.Nature);
        if (expectedNature is not null)
        {
            Assert.Equal(FailureItemIds.Ordering, result.Failure!.ItemId);
            Assert.Equal(result.Result.Detail, result.Failure.Detail);
        }
        else
        {
            Assert.Equal(new CheckResult(CheckNames.Ordering, CheckOutcomes.Pass, 1, 1, "baseline commit aaaaaaaaaaaa is an ancestor of the first facade commit bbbbbbbbbbbb"), result.Result);
        }

        Assert.Equal(BaselineCommit, result.BaselineCommit);
        Assert.Equal(FacadeCommit, result.FacadeCommit);
    }

    [Fact]
    public async Task OldestAddingCommits_AreCompared()
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = Script(
            baselineLog: ScriptedGit.Ok($"{BaselineCommit}\n{LaterBaselineCommit}\n"),
            facadeLog: ScriptedGit.Ok($"{FacadeCommit}\n{LaterFacadeCommit}\n"));

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CheckOutcomes.Pass, result.Result.Outcome);
        Assert.Equal(BaselineCommit, result.BaselineCommit);
        Assert.Equal(FacadeCommit, result.FacadeCommit);
        string[] mergeBaseArgs = ["merge-base", "--is-ancestor", BaselineCommit, FacadeCommit];
        Assert.Equal(mergeBaseArgs, git.Calls[^1].Args);
    }

    [Fact]
    public async Task GitArguments_AskForTheFirstAddingCommitOfEachPathspec()
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = Script();

        await Check(git).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, git.Calls.Count);
        Assert.All(git.Calls, call => Assert.Equal(_temp.Root, call.RepoRoot));
        Assert.All(git.Calls, call => Assert.Equal(Git.DefaultTimeout, call.Timeout));
        Assert.Equal(LogArgs(BaselinePathspec), git.Calls[1].Args);
        Assert.Equal(LogArgs(FacadePathspec), git.Calls[2].Args);
    }

    [Fact]
    public void NullGitRunner_Throws()
    {
        Assert.Throws<ArgumentNullException>("git", () => new OrderingEvidence(new HarnessOptions { RepoRoot = _temp.Root }, null!));
    }

    // ---- Real git ------------------------------------------------------------------------------

    [Fact]
    public async Task RealGit_BaselineCommittedBeforeFacade_Passes()
    {
        var ct = TestContext.Current.CancellationToken;
        await TestGit.InitAsync(_temp.Root, ct);
        _temp.Write("README.md", "start\n");
        await TestGit.CommitAllAsync(_temp.Root, "starting commit", ct);
        _temp.Write(BaselinePathspec, "{}\n");
        var baseline = await TestGit.CommitAllAsync(_temp.Root, "baseline", ct);
        _temp.Write("facade/src/Program.cs", "class Program {}\n");
        var facade = await TestGit.CommitAllAsync(_temp.Root, "facade", ct);
        _temp.Write("facade/src/Later.cs", "class Later {}\n");
        await TestGit.CommitAllAsync(_temp.Root, "more facade", ct);

        var result = await new OrderingEvidence(new HarnessOptions { RepoRoot = _temp.Root }).RunAsync(ct);

        Assert.Equal(CheckOutcomes.Pass, result.Result.Outcome);
        Assert.Null(result.Failure);
        Assert.Equal(baseline, result.BaselineCommit);
        Assert.Equal(facade, result.FacadeCommit);
    }

    [Fact]
    public async Task RealGit_FacadeCommittedBeforeBaseline_IsNotAncestor()
    {
        var ct = TestContext.Current.CancellationToken;
        await TestGit.InitAsync(_temp.Root, ct);
        _temp.Write("README.md", "start\n");
        await TestGit.CommitAllAsync(_temp.Root, "starting commit", ct);
        _temp.Write("facade/src/Program.cs", "class Program {}\n");
        var facade = await TestGit.CommitAllAsync(_temp.Root, "facade", ct);
        _temp.Write(BaselinePathspec, "{}\n");
        var baseline = await TestGit.CommitAllAsync(_temp.Root, "baseline", ct);

        var result = await new OrderingEvidence(new HarnessOptions { RepoRoot = _temp.Root }).RunAsync(ct);

        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);
        Assert.Equal(FailureItemIds.NotAncestor, result.Failure?.Nature);
        Assert.Contains("is not an ancestor of the first facade commit", result.Failure?.Detail, StringComparison.Ordinal);
        Assert.Equal(baseline, result.BaselineCommit);
        Assert.Equal(facade, result.FacadeCommit);
    }

    [Fact]
    public async Task RealGit_ShallowClone_IsNotRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = _temp.PathOf("source");
        var clone = _temp.PathOf("clone");
        Directory.CreateDirectory(source);
        await TestGit.InitAsync(source, ct);
        _temp.Write("source/" + BaselinePathspec, "{}\n");
        await TestGit.CommitAllAsync(source, "baseline", ct);
        _temp.Write("source/facade/src/Program.cs", "class Program {}\n");
        await TestGit.CommitAllAsync(source, "facade", ct);
        await TestGit.RunAsync(_temp.Root, ct, "clone", "--quiet", "--depth", "1", new Uri(source).AbsoluteUri, clone);

        var result = await new OrderingEvidence(new HarnessOptions { RepoRoot = clone }).RunAsync(ct);

        AssertNotRun(result, "is a shallow clone");
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private OrderingEvidence Check(ScriptedGit git) => new(new HarnessOptions { RepoRoot = _temp.Root }, git.Runner);

    // A history that passes unless a step's answer is replaced: not shallow, one baseline commit, one
    // later facade commit, and an ancestor relation between them.
    private static ScriptedGit Script(
        GitResult? shallow = null,
        GitResult? baselineLog = null,
        GitResult? facadeLog = null,
        GitResult? mergeBase = null) =>
        new(args => args[0] switch
        {
            "rev-parse" => shallow ?? ScriptedGit.Ok("false\n"),
            "log" when args[^1] == BaselinePathspec => baselineLog ?? ScriptedGit.Ok(BaselineCommit + "\n"),
            "log" when args[^1] == FacadePathspec => facadeLog ?? ScriptedGit.Ok(FacadeCommit + "\n"),
            "merge-base" => mergeBase ?? ScriptedGit.Ok(),
            _ => ScriptedGit.Exited(1, $"unexpected git {string.Join(' ', args)}"),
        });

    private static string[] LogArgs(string pathspec) =>
    [
        "log", "--full-history", "--date-order", "--root", "--no-follow",
        "--diff-filter=A", "--no-renames", "--reverse", "--format=%H", "--", pathspec,
    ];

    private static void AssertFail(OrderingResult result, string expectedNature, string expectedDetail)
    {
        Assert.Equal(new CheckResult(CheckNames.Ordering, CheckOutcomes.Fail, 1, 0, expectedDetail), result.Result);
        Assert.Equal(new FailureItem(FailureItemIds.Ordering, expectedNature, Operation: null, Detail: expectedDetail), result.Failure);
    }

    private static string AssertNotRun(OrderingResult result, string expectedDetail)
    {
        Assert.Equal(CheckNames.Ordering, result.Result.Name);
        Assert.Equal(CheckOutcomes.NotRun, result.Result.Outcome);
        Assert.Equal(0, result.Result.Cases);
        var item = result.Failure;
        Assert.NotNull(item);
        Assert.Equal(FailureItemIds.Ordering, item.ItemId);
        Assert.Equal(FailureItemIds.NotRun, item.Nature);
        Assert.Equal(result.Result.Detail, item.Detail);
        Assert.Contains(expectedDetail, item.Detail, StringComparison.Ordinal);
        return item.Detail!;
    }
}

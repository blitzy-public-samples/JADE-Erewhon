using Erewhon.Harness.Checks;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Tests;

/// <summary>
/// The JADE-unchanged check: no difference and no working-tree change under <c>Erewhon/</c> since the
/// starting commit. Scripted git runners drive every git outcome, so missing, failing and unattributable
/// git evidence is shown to give <c>not-run</c> rather than a pass; throwaway repositories in a temporary
/// directory confirm the same verdicts against real git. The harness's own repository is never touched.
/// </summary>
public sealed class JadeUnchangedCheckTests : IDisposable
{
    private const string StartingCommit = "19a5189e9075349aca54de1fb5fdf2d8727403e7";
    private const string ModifiedBlob = "1111111111111111111111111111111111111111";
    private const string AddedBlob = "2222222222222222222222222222222222222222";

    private readonly TempDirectory _temp = new();

    /// <summary>Deletes the test's temporary directory and any repository in it.</summary>
    public void Dispose() => _temp.Dispose();

    // ---- Inputs that stop the check before git runs ---------------------------------------------

    [Fact]
    public async Task NoGitMetadata_IsNotRunWithoutCallingGit()
    {
        var git = new ScriptedGit(_ => ScriptedGit.Ok());

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        AssertNotRun(result, "no .git metadata at ");
        Assert.Empty(git.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-x")]
    [InlineData("--output=x")]
    public async Task UnusableStartingCommit_IsNotRunWithoutCallingGit(string startingCommit)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = new ScriptedGit(_ => ScriptedGit.Ok());

        var result = await Check(git, startingCommit).RunAsync(TestContext.Current.CancellationToken);

        AssertNotRun(result, "is not a commit id");
        Assert.Empty(git.Calls);
    }

    // ---- git failures --------------------------------------------------------------------------

    [Theory]
    [InlineData("diff", "exited", 128, "fatal: bad revision '19a5189e'", "exited 128: fatal: bad revision '19a5189e'")]
    [InlineData("diff", "unavailable", -1, "", "could not start")]
    [InlineData("status", "timed-out", -1, "", "timed out")]
    [InlineData("status", "exited", 129, "", "exited 129")]
    [InlineData("ls-tree", "exited", 128, "fatal: not a tree object", "exited 128: fatal: not a tree object")]
    public async Task FailingGitCommand_IsNotRun(string command, string failureKind, int exitCode, string stderr, string expectedReason)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = new ScriptedGit(args => args[0] == command
            ? ScriptedGit.Failure(failureKind, exitCode, stderr)
            : ScriptedGit.Ok());

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        var detail = AssertNotRun(result, $"git {command} ");
        Assert.Contains(expectedReason, detail, StringComparison.Ordinal);
        Assert.Equal(command, git.Calls[^1].Args[0]);
    }

    [Theory]
    [InlineData("M\0", "")]
    [InlineData("\0\0", "")]
    [InlineData("", "??\0")]
    [InlineData("", "garbage")]
    public async Task OutputNamingNoPath_IsNotRun(string diffOutput, string statusOutput)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = new ScriptedGit(args => args[0] switch
        {
            "diff" => ScriptedGit.Ok(diffOutput),
            "status" => ScriptedGit.Ok(statusOutput),
            _ => ScriptedGit.Ok("Erewhon/a.scm\0"),
        });

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        AssertNotRun(result, "that name no path");
    }

    // ---- Verdicts from git output --------------------------------------------------------------

    [Fact]
    public async Task EmptyOutputs_PassWithTrackedPathCounts()
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = new ScriptedGit(args => args[0] == "ls-tree"
            ? ScriptedGit.Ok("Erewhon/a.scm\0Erewhon/DataFiles/b.txt\0")
            : ScriptedGit.Ok());

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new CheckResult(CheckNames.JadeUnchanged, CheckOutcomes.Pass, 2, 2, $"no change under Erewhon/ since starting commit {StartingCommit}"),
            result.Result);
        Assert.Empty(result.Failures);
        Assert.Equal(new JadeUnchangedSummary(2, 0), result.Summary);
    }

    [Fact]
    public async Task Changes_GiveOneSortedItemPerPathWithBlobIds()
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = new ScriptedGit(args => args[0] switch
        {
            "diff" => ScriptedGit.Ok("M\0Erewhon/b.scm\0D\0Erewhon/a.ddx\0"),
            "status" => ScriptedGit.Ok(" M Erewhon/b.scm\0 D Erewhon/a.ddx\0?? Erewhon/c.txt\0"),
            "ls-tree" => ScriptedGit.Ok("Erewhon/a.ddx\0Erewhon/b.scm\0Erewhon/z.mul\0"),
            "hash-object" => ScriptedGit.Ok((args[^1] == "Erewhon/b.scm" ? ModifiedBlob : AddedBlob) + "\n"),
            _ => ScriptedGit.Exited(1, "unexpected git command"),
        });

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        FailureItem[] expected =
        [
            new(FailureItemIds.JadeUnchangedPath("Erewhon/a.ddx"), FailureItemIds.Deleted),
            new(FailureItemIds.JadeUnchangedPath("Erewhon/b.scm"), FailureItemIds.Modified(ModifiedBlob)),
            new(FailureItemIds.JadeUnchangedPath("Erewhon/c.txt"), FailureItemIds.Added(AddedBlob)),
        ];
        Assert.Equal(expected, result.Failures);
        Assert.Equal(
            new CheckResult(CheckNames.JadeUnchanged, CheckOutcomes.Fail, 3, 1, $"3 path(s) under Erewhon/ changed since starting commit {StartingCommit}"),
            result.Result);
        Assert.Equal(new JadeUnchangedSummary(3, 3), result.Summary);

        // A deleted path has no content to hash; the other two are hashed once each.
        string[] hashedPaths = ["Erewhon/b.scm", "Erewhon/c.txt"];
        Assert.Equal(hashedPaths, git.Calls.Where(call => call.Args[0] == "hash-object").Select(call => call.Args[^1]));
    }

    [Theory]
    [InlineData(128, "")]
    [InlineData(0, "")]
    [InlineData(0, "1111111111111111111111111111111111111111\n2222222222222222222222222222222222222222\n")]
    public async Task UnusableHashObjectAnswer_GivesUnknownBlob(int exitCode, string stdout)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = new ScriptedGit(args => args[0] switch
        {
            "diff" => ScriptedGit.Ok("M\0Erewhon/b.scm\0"),
            "hash-object" => new GitResult(exitCode, stdout, string.Empty, TimedOut: false, Unavailable: false),
            "ls-tree" => ScriptedGit.Ok("Erewhon/b.scm\0"),
            _ => ScriptedGit.Ok(),
        });

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Failures);
        Assert.Equal(new FailureItem(FailureItemIds.JadeUnchangedPath("Erewhon/b.scm"), FailureItemIds.Modified("unknown")), item);
    }

    [Fact]
    public async Task GitArguments_CarryTheStartingCommitAndTheJadePathspec()
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = new ScriptedGit(_ => ScriptedGit.Ok());

        await Check(git, $"  {StartingCommit} ").RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, git.Calls.Count);
        Assert.All(git.Calls, call => Assert.Equal(_temp.Root, call.RepoRoot));
        Assert.All(git.Calls, call => Assert.Equal(Git.DefaultTimeout, call.Timeout));
        string[] diffArgs = ["diff", "--name-status", "--no-renames", "-z", StartingCommit, "--", "Erewhon/"];
        string[] statusArgs = ["status", "--porcelain", "--no-renames", "--untracked-files=all", "-z", "--", "Erewhon/"];
        string[] treeArgs = ["ls-tree", "-r", "--name-only", "-z", StartingCommit, "--", "Erewhon/"];
        Assert.Equal(diffArgs, git.Calls[0].Args);
        Assert.Equal(statusArgs, git.Calls[1].Args);
        Assert.Equal(treeArgs, git.Calls[2].Args);
    }

    [Fact]
    public void NullGitRunner_Throws()
    {
        Assert.Throws<ArgumentNullException>("git", () => new JadeUnchangedCheck(new HarnessOptions { RepoRoot = _temp.Root }, null!));
    }

    // ---- Real git ------------------------------------------------------------------------------

    [Fact]
    public async Task RealGit_UnchangedTree_Passes()
    {
        var ct = TestContext.Current.CancellationToken;
        var commit = await JadeRepositoryAsync(ct);

        // A change outside Erewhon/ is not the check's concern.
        _temp.Write("facade/Program.cs", "class Program {}\n");
        _temp.Write("README.md", "changed\n");

        var result = await new JadeUnchangedCheck(new HarnessOptions { RepoRoot = _temp.Root, StartingCommit = commit }).RunAsync(ct);

        Assert.Equal(CheckOutcomes.Pass, result.Result.Outcome);
        Assert.Equal(3, result.Result.Cases);
        Assert.Equal(3, result.Result.Passed);
        Assert.Empty(result.Failures);
        Assert.Equal(new JadeUnchangedSummary(3, 0), result.Summary);
    }

    [Fact]
    public async Task RealGit_ModifiedDeletedAndUntrackedPaths_GiveTheirNatures()
    {
        var ct = TestContext.Current.CancellationToken;
        var commit = await JadeRepositoryAsync(ct);
        _temp.Write("Erewhon/ErewhonModelSchema.scm", "schema changed\n");
        File.Delete(_temp.PathOf("Erewhon/ErewhonDemoSystem.mul"));
        _temp.Write("Erewhon/DataFiles/New.txt", "added\n");
        var modifiedBlob = await TestGit.HashObjectAsync(_temp.Root, "Erewhon/ErewhonModelSchema.scm", ct);
        var addedBlob = await TestGit.HashObjectAsync(_temp.Root, "Erewhon/DataFiles/New.txt", ct);

        var result = await new JadeUnchangedCheck(new HarnessOptions { RepoRoot = _temp.Root, StartingCommit = commit }).RunAsync(ct);

        FailureItem[] expected =
        [
            new(FailureItemIds.JadeUnchangedPath("Erewhon/DataFiles/New.txt"), FailureItemIds.Added(addedBlob)),
            new(FailureItemIds.JadeUnchangedPath("Erewhon/ErewhonDemoSystem.mul"), FailureItemIds.Deleted),
            new(FailureItemIds.JadeUnchangedPath("Erewhon/ErewhonModelSchema.scm"), FailureItemIds.Modified(modifiedBlob)),
        ];
        Assert.Equal(expected, result.Failures);
        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);
        Assert.Equal(3, result.Result.Cases);
        Assert.Equal(1, result.Result.Passed);
        Assert.Equal(new JadeUnchangedSummary(3, 3), result.Summary);
    }

    [Fact]
    public async Task RealGit_UnresolvableStartingCommit_IsNotRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await JadeRepositoryAsync(ct);

        var result = await new JadeUnchangedCheck(
            new HarnessOptions { RepoRoot = _temp.Root, StartingCommit = "0123456789abcdef0123456789abcdef01234567" }).RunAsync(ct);

        var detail = AssertNotRun(result, "git diff ");
        Assert.Contains("exited 128", detail, StringComparison.Ordinal);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private JadeUnchangedCheck Check(ScriptedGit git, string startingCommit = StartingCommit) =>
        new(new HarnessOptions { RepoRoot = _temp.Root, StartingCommit = startingCommit }, git.Runner);

    // A repository whose one commit holds three files under Erewhon/ and one outside it.
    private async Task<string> JadeRepositoryAsync(CancellationToken ct)
    {
        await TestGit.InitAsync(_temp.Root, ct);
        _temp.Write("Erewhon/ErewhonModelSchema.scm", "schema\n");
        _temp.Write("Erewhon/ErewhonDemoSystem.mul", "load order\n");
        _temp.Write("Erewhon/DataFiles/Clients.txt", "Pauline Wild\n");
        _temp.Write("README.md", "readme\n");
        return await TestGit.CommitAllAsync(_temp.Root, "starting commit", ct);
    }

    private static string AssertNotRun(JadeUnchangedResult result, string expectedDetail)
    {
        Assert.Equal(CheckNames.JadeUnchanged, result.Result.Name);
        Assert.Equal(CheckOutcomes.NotRun, result.Result.Outcome);
        Assert.Equal(0, result.Result.Cases);
        Assert.Null(result.Summary);
        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.JadeUnchanged, item.ItemId);
        Assert.Equal(FailureItemIds.NotRun, item.Nature);
        Assert.Equal(result.Result.Detail, item.Detail);
        Assert.Contains(expectedDetail, item.Detail, StringComparison.Ordinal);
        return item.Detail!;
    }
}

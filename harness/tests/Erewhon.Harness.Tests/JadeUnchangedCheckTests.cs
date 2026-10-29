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
    private const string Sha256Blob = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // A symbolic link's text, and the ids real git stores for a link with that text (mode 120000, read
    // back with ls-files --stage after staging it) in a SHA-1 and a SHA-256 repository.
    private const string LinkText = "target.txt";
    private const string LinkSha1Blob = "4cbb553f3f4ac2ee7b01ff6c951d6bf583c39c15";
    private const string LinkSha256Blob = "8edaa00b6a3bb3f93168959cad1a6e971a6a448c06e146992be90e1259517afd";

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
    [InlineData("modified", "unavailable", -1, "", "could not start")]
    [InlineData("modified", "timed-out", -1, "", "timed out")]
    [InlineData("modified", "exited", 128, "fatal: Unable to hash Erewhon/b.scm", "exited 128: fatal: Unable to hash Erewhon/b.scm")]
    [InlineData("added", "unavailable", -1, "", "could not start")]
    [InlineData("added", "timed-out", -1, "", "timed out")]
    [InlineData("added", "exited", 128, "fatal: Unable to hash Erewhon/c.txt", "exited 128: fatal: Unable to hash Erewhon/c.txt")]
    public async Task FailingHashObject_IsNotRun(string change, string failureKind, int exitCode, string stderr, string expectedReason)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = OneChange(change, ScriptedGit.Failure(failureKind, exitCode, stderr));

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        var path = ChangedPath(change);
        var detail = AssertNotRun(result, $"git hash-object -- {path} ");
        Assert.Contains(expectedReason, detail, StringComparison.Ordinal);
        string[] hashArgs = ["hash-object", "--", path];
        Assert.Equal(hashArgs, git.Calls[^1].Args);
    }

    [Theory]
    [InlineData("modified", "", "")]
    [InlineData("modified", "\n", "")]
    [InlineData("modified", "1111111111111111111111111111111111111111\n2222222222222222222222222222222222222222\n", "1111111111111111111111111111111111111111 2222222222222222222222222222222222222222")]
    [InlineData("modified", "ABCDEF0123456789ABCDEF0123456789ABCDEF01\n", "ABCDEF0123456789ABCDEF0123456789ABCDEF01")]
    [InlineData("modified", "111111111111111111111111111111111111111\n", "111111111111111111111111111111111111111")]
    [InlineData("modified", "11111111111111111111111111111111111111111\n", "11111111111111111111111111111111111111111")]
    [InlineData("modified", "gggggggggggggggggggggggggggggggggggggggg\n", "gggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("added", "", "")]
    [InlineData("added", "1111111111111111111111111111111111111111\n2222222222222222222222222222222222222222\n", "1111111111111111111111111111111111111111 2222222222222222222222222222222222222222")]
    public async Task HashObjectOutputThatIsNoObjectId_IsNotRun(string change, string stdout, string printed)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = OneChange(change, ScriptedGit.Ok(stdout));

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        var path = ChangedPath(change);
        var detail = AssertNotRun(result, "which is not an object id");
        Assert.Equal($"git hash-object -- {path} printed '{printed}', which is not an object id", detail);
        string[] hashArgs = ["hash-object", "--", path];
        Assert.Equal(hashArgs, git.Calls[^1].Args);
    }

    [Theory]
    [InlineData("modified")]
    [InlineData("added")]
    public async Task Sha256HashObjectAnswer_GivesItsBlobId(string change)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        var git = OneChange(change, ScriptedGit.Ok(Sha256Blob + "\n"));

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        var nature = change == "added" ? FailureItemIds.Added(Sha256Blob) : FailureItemIds.Modified(Sha256Blob);
        Assert.Equal(new FailureItem(FailureItemIds.JadeUnchangedPath(ChangedPath(change)), nature), Assert.Single(result.Failures));
        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);
        Assert.Equal(new JadeUnchangedSummary(1, 1), result.Summary);
    }

    // ---- Symbolic links ------------------------------------------------------------------------

    [Theory]
    [InlineData("modified", "sha1\n", LinkSha1Blob)]
    [InlineData("added", "sha1\n", LinkSha1Blob)]
    [InlineData("modified", "sha256\n", LinkSha256Blob)]
    [InlineData("added", "sha256\n", LinkSha256Blob)]
    public async Task SymbolicLink_IsHashedFromItsLinkTextInTheRepositoryObjectFormat(string change, string objectFormat, string expectedBlob)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        CreateSymbolicLink(ChangedPath(change), LinkText);
        var git = OneChange(change, ScriptedGit.Exited(1, "hash-object would follow the link"), ScriptedGit.Ok(objectFormat));

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        var nature = change == "added" ? FailureItemIds.Added(expectedBlob) : FailureItemIds.Modified(expectedBlob);
        Assert.Equal(new FailureItem(FailureItemIds.JadeUnchangedPath(ChangedPath(change)), nature), Assert.Single(result.Failures));
        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);
        Assert.Equal(new JadeUnchangedSummary(1, 1), result.Summary);

        // diff, status and ls-tree, then the object format lookup; hash-object never runs for a link.
        Assert.Equal(4, git.Calls.Count);
        string[] formatArgs = ["rev-parse", "--show-object-format"];
        Assert.Equal(formatArgs, git.Calls[^1].Args);
        Assert.Equal(_temp.Root, git.Calls[^1].RepoRoot);
        Assert.Equal(Git.DefaultTimeout, git.Calls[^1].Timeout);
    }

    [Theory]
    [InlineData("unavailable", -1, "", "could not start")]
    [InlineData("timed-out", -1, "", "timed out")]
    [InlineData("exited", 128, "fatal: not a git repository: .git", "exited 128: fatal: not a git repository: .git")]
    [InlineData("exited", 129, "", "exited 129")]
    public async Task FailingObjectFormatLookup_IsNotRun(string failureKind, int exitCode, string stderr, string expectedReason)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        CreateSymbolicLink(ChangedPath("added"), LinkText);
        var git = OneChange("added", ScriptedGit.Ok(AddedBlob + "\n"), ScriptedGit.Failure(failureKind, exitCode, stderr));

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        var detail = AssertNotRun(result, "git rev-parse --show-object-format ");
        Assert.Contains(expectedReason, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(git.Calls, call => call.Args[0] == "hash-object");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("\n", "")]
    [InlineData("SHA1\n", "SHA1")]
    [InlineData("md5\n", "md5")]
    [InlineData("sha1\nsha256\n", "sha1 sha256")]
    public async Task ObjectFormatOutputThatIsNoKnownFormat_IsNotRun(string stdout, string printed)
    {
        Directory.CreateDirectory(_temp.PathOf(".git"));
        CreateSymbolicLink(ChangedPath("added"), LinkText);
        var git = OneChange("added", ScriptedGit.Ok(AddedBlob + "\n"), ScriptedGit.Ok(stdout));

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        var detail = AssertNotRun(result, "which is not an object format");
        Assert.Equal($"git rev-parse --show-object-format printed '{printed}', which is not an object format", detail);
        Assert.DoesNotContain(git.Calls, call => call.Args[0] == "hash-object");
    }

    [Fact]
    public async Task SymbolicLinkTextHoldingAReplacementCharacter_IsNotRunWithoutHashing()
    {
        // The runtime decodes link text bytes that are not valid UTF-8 as U+FFFD, so their bytes, and
        // the blob git stores, can no longer be recovered from the text.
        Directory.CreateDirectory(_temp.PathOf(".git"));
        CreateSymbolicLink(ChangedPath("added"), "caf\uFFFD.scm");
        var git = OneChange("added", ScriptedGit.Ok(AddedBlob + "\n"), ScriptedGit.Ok("sha1\n"));

        var result = await Check(git).RunAsync(TestContext.Current.CancellationToken);

        AssertNotRun(result, "symbolic link Erewhon/c.txt has link text 'caf\uFFFD.scm' that may not be valid UTF-8");
        Assert.Equal(3, git.Calls.Count);
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

    [Fact]
    public async Task RealGit_UntrackedNestedRepository_IsNotRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var commit = await JadeRepositoryAsync(ct);

        // git status lists a nested repository as one untracked directory, which hash-object cannot hash.
        _temp.Write("Erewhon/nested/Inner.txt", "inner\n");
        await TestGit.InitAsync(_temp.PathOf("Erewhon/nested"), ct);

        var result = await new JadeUnchangedCheck(new HarnessOptions { RepoRoot = _temp.Root, StartingCommit = commit }).RunAsync(ct);

        AssertNotRun(result, "git hash-object -- Erewhon/nested/ exited 128");
    }

    // committedTarget, when given, is the link text the path has at the starting commit; the path is
    // then given currentTarget as its link text, replacing whatever was there (a link or a tracked file).
    [Theory]
    [InlineData("sha1", "Erewhon/Link.scm", null, "ErewhonModelSchema.scm", "added")]
    [InlineData("sha1", "Erewhon/Dangling.scm", null, "Missing.scm", "added")]
    [InlineData("sha1", "Erewhon/DataLink", null, "DataFiles", "added")]
    [InlineData("sha1", "Erewhon/Link.scm", "ErewhonModelSchema.scm", "ErewhonDemoSystem.mul", "modified")]
    [InlineData("sha1", "Erewhon/ErewhonDemoSystem.mul", null, "ErewhonModelSchema.scm", "modified")]
    [InlineData("sha256", "Erewhon/Link.scm", null, "ErewhonModelSchema.scm", "added")]
    [InlineData("sha256", "Erewhon/Link.scm", "ErewhonModelSchema.scm", "Missing.scm", "modified")]
    public async Task RealGit_SymbolicLink_GivesTheBlobGitStoresForItsLinkText(
        string objectFormat,
        string linkPath,
        string? committedTarget,
        string currentTarget,
        string change)
    {
        var ct = TestContext.Current.CancellationToken;
        var commit = await JadeRepositoryAsync(ct, objectFormat);
        if (committedTarget is not null)
        {
            CreateSymbolicLink(linkPath, committedTarget);
            commit = await TestGit.CommitAllAsync(_temp.Root, "commit the link", ct);
        }

        File.Delete(_temp.PathOf(linkPath));
        CreateSymbolicLink(linkPath, currentTarget);

        var result = await new JadeUnchangedCheck(new HarnessOptions { RepoRoot = _temp.Root, StartingCommit = commit }).RunAsync(ct);

        // The id git stores for the link once it is staged in this throwaway repository: a mode 120000
        // blob of the link text, never the target's content.
        await TestGit.RunAsync(_temp.Root, ct, "add", "--", linkPath);
        var staged = (await TestGit.RunAsync(_temp.Root, ct, "ls-files", "--stage", "--", linkPath)).Split([' ', '\t']);
        Assert.Equal("120000", staged[0]);
        var nature = change == "added" ? FailureItemIds.Added(staged[1]) : FailureItemIds.Modified(staged[1]);
        Assert.Equal(new FailureItem(FailureItemIds.JadeUnchangedPath(linkPath), nature), Assert.Single(result.Failures));
        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private JadeUnchangedCheck Check(ScriptedGit git, string startingCommit = StartingCommit) =>
        new(new HarnessOptions { RepoRoot = _temp.Root, StartingCommit = startingCommit }, git.Runner);

    // The one path a theory's change touches: "added" is the untracked Erewhon/c.txt, anything else the
    // modified Erewhon/b.scm.
    private static string ChangedPath(string change) => change == "added" ? "Erewhon/c.txt" : "Erewhon/b.scm";

    // git answers with that one change under Erewhon/, Erewhon/b.scm as the only tracked path,
    // hashAnswer for every hash-object call and formatAnswer, when given, for the object format lookup.
    private static ScriptedGit OneChange(string change, GitResult hashAnswer, GitResult? formatAnswer = null) => new(args => args[0] switch
    {
        "diff" => ScriptedGit.Ok(change == "added" ? string.Empty : "M\0Erewhon/b.scm\0"),
        "status" => ScriptedGit.Ok(change == "added" ? "?? Erewhon/c.txt\0" : " M Erewhon/b.scm\0"),
        "ls-tree" => ScriptedGit.Ok("Erewhon/b.scm\0"),
        "hash-object" => hashAnswer,
        "rev-parse" when formatAnswer is not null => formatAnswer,
        _ => ScriptedGit.Exited(1, "unexpected git command"),
    });

    // Creates a symbolic link at relativePath with linkText as its text, or skips the test where the
    // platform or file system cannot create one.
    private void CreateSymbolicLink(string relativePath, string linkText)
    {
        var path = _temp.PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.CreateSymbolicLink(path, linkText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"symbolic links cannot be created here: {ex.Message}");
        }
    }

    // A repository in objectFormat whose one commit holds three files under Erewhon/ and one outside it.
    private async Task<string> JadeRepositoryAsync(CancellationToken ct, string objectFormat = "sha1")
    {
        await TestGit.RunAsync(_temp.Root, ct, "init", "--quiet", "--initial-branch=main", $"--object-format={objectFormat}");
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

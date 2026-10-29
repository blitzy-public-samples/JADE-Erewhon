using System.Diagnostics;
using System.Text;
using Erewhon.Harness.Checks;

namespace Erewhon.Harness.Tests;

/// <summary>One invocation a <see cref="ScriptedGit"/> received.</summary>
/// <param name="RepoRoot">The repository root the check passed.</param>
/// <param name="Args">The git arguments, copied when the call was made.</param>
/// <param name="Timeout">The limit the check passed.</param>
internal sealed record GitCall(string RepoRoot, IReadOnlyList<string> Args, TimeSpan Timeout);

/// <summary>
/// A git runner that answers from a script instead of running git and records every invocation, so the
/// JADE-unchanged and ordering checks can be driven through each git outcome without a repository.
/// </summary>
internal sealed class ScriptedGit(Func<IReadOnlyList<string>, GitResult> answer)
{
    private readonly List<GitCall> _calls = [];

    /// <summary>Every invocation so far, in call order.</summary>
    public IReadOnlyList<GitCall> Calls => _calls;

    /// <summary>The runner to pass to a check.</summary>
    public GitRunner Runner => RunAsync;

    /// <summary>git ran and exited 0 with <paramref name="stdout"/>.</summary>
    public static GitResult Ok(string stdout = "") =>
        new(0, stdout, string.Empty, TimedOut: false, Unavailable: false);

    /// <summary>git ran and exited <paramref name="exitCode"/>, writing <paramref name="stderr"/>.</summary>
    public static GitResult Exited(int exitCode, string stderr = "") =>
        new(exitCode, string.Empty, stderr, TimedOut: false, Unavailable: false);

    /// <summary>git was killed at its deadline, as <see cref="Git.RunAsync"/> reports it.</summary>
    public static GitResult TimedOut() =>
        new(-1, string.Empty, "git did not finish within 30 s; its process tree was killed.", TimedOut: true, Unavailable: false);

    /// <summary>git could not be started, as <see cref="Git.RunAsync"/> reports it.</summary>
    public static GitResult Unavailable() =>
        new(-1, string.Empty, "git could not be started: No such file or directory", TimedOut: false, Unavailable: true);

    /// <summary>
    /// The failure a theory names: <c>timed-out</c>, <c>unavailable</c>, or otherwise an exit with
    /// <paramref name="exitCode"/> and <paramref name="stderr"/>.
    /// </summary>
    public static GitResult Failure(string kind, int exitCode, string stderr) => kind switch
    {
        "timed-out" => TimedOut(),
        "unavailable" => Unavailable(),
        _ => Exited(exitCode, stderr),
    };

    private Task<GitResult> RunAsync(string repoRoot, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _calls.Add(new GitCall(repoRoot, [.. args], timeout));
        return Task.FromResult(answer(args));
    }
}

/// <summary>A directory of its own under the system temp path, deleted with its contents on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>Creates the directory.</summary>
    public TempDirectory() => Root = Directory.CreateTempSubdirectory("erewhon-harness-tests-").FullName;

    /// <summary>The directory's full path.</summary>
    public string Root { get; }

    /// <summary>Returns <paramref name="relativePath"/> under <see cref="Root"/>, with '/' as separator.</summary>
    public string PathOf(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Writes <paramref name="content"/> to <paramref name="relativePath"/>, creating its directories.</summary>
    public string Write(string relativePath, string content)
    {
        var path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Deletes the directory; read-only files, such as git's object files, are made writable first.</summary>
    public void Dispose()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Root, recursive: true);
    }
}

/// <summary>
/// The git command line, for building throwaway repositories in a <see cref="TempDirectory"/>. System
/// and global configuration are ignored and the author, committer and dates are fixed, so the tests
/// depend on the git binary alone and never on the machine's or the user's settings.
/// </summary>
internal static class TestGit
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private static readonly string[] InheritedRepositoryVariables =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_COMMON_DIR", "GIT_NAMESPACE", "GIT_CONFIG", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT",
    ];

    /// <summary>Creates a repository at <paramref name="root"/> on branch <c>main</c>.</summary>
    public static Task InitAsync(string root, CancellationToken ct) =>
        RunAsync(root, ct, "init", "--quiet", "--initial-branch=main");

    /// <summary>Stages every change and commits it.</summary>
    /// <returns>The new commit's full id.</returns>
    public static async Task<string> CommitAllAsync(string root, string message, CancellationToken ct)
    {
        await RunAsync(root, ct, "add", "--all");
        await RunAsync(root, ct, "commit", "--quiet", "--no-gpg-sign", "--allow-empty", "-m", message);
        return (await RunAsync(root, ct, "rev-parse", "HEAD")).Trim();
    }

    /// <summary>The blob id git computes for the file's current content, without writing it.</summary>
    public static async Task<string> HashObjectAsync(string root, string path, CancellationToken ct) =>
        (await RunAsync(root, ct, "hash-object", "--", path)).Trim();

    /// <summary>Runs git in <paramref name="workingDirectory"/> and returns its standard output.</summary>
    /// <exception cref="InvalidOperationException">git exited non-zero or did not finish within the limit.</exception>
    public static async Task<string> RunAsync(string workingDirectory, CancellationToken ct, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var variable in InheritedRepositoryVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        startInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_AUTHOR_NAME"] = "Erewhon Harness Tests";
        startInfo.Environment["GIT_AUTHOR_EMAIL"] = "harness-tests@example.invalid";
        startInfo.Environment["GIT_AUTHOR_DATE"] = "2026-01-01T00:00:00Z";
        startInfo.Environment["GIT_COMMITTER_NAME"] = "Erewhon Harness Tests";
        startInfo.Environment["GIT_COMMITTER_EMAIL"] = "harness-tests@example.invalid";
        startInfo.Environment["GIT_COMMITTER_DATE"] = "2026-01-01T00:00:00Z";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["LC_ALL"] = "C";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"git {string.Join(' ', args)} could not be started");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Limit);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"git {string.Join(' ', args)} did not finish within {Limit.TotalSeconds} s");
        }

        var output = await stdout;
        var error = await stderr;
        return process.ExitCode == 0
            ? output
            : throw new InvalidOperationException($"git {string.Join(' ', args)} exited {process.ExitCode}: {error.Trim()}");
    }
}

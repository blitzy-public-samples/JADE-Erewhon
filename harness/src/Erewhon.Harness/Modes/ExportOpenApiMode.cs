using System.Globalization;
using System.Text.Json;
using Erewhon.Contracts.Admin;
using Erewhon.Harness.Http;
using Erewhon.Harness.Lifecycle;

namespace Erewhon.Harness.Modes;

/// <summary>
/// The <c>export-openapi</c> mode: fetches the running facade's live OpenAPI document from its admin
/// listener (<see cref="AdminRoutes.OpenApiDocument"/>) and writes it, byte for byte, to
/// <see cref="HarnessOptions.ContractsOpenApiPath"/>, the checked-in
/// <c>contracts/openapi/erewhon-rest.openapi.json</c>.
/// </summary>
/// <remarks>
/// <para>Only the execution agent runs this mode, through <c>harness/scripts/run-local.sh export-openapi</c>,
/// once after the facade commit and again for each regeneration in the correction window. Replay never
/// writes the contract; its drift check only compares the live document against it.</para>
/// <para>The document is written exactly as received, never reformatted or re-serialised, so the
/// checked-in file is what the facade serves. It is written to a temporary file in the target directory
/// and moved over the target, so a failed fetch or write leaves any existing contract untouched. No other
/// file is written, and no directory is created except <c>contracts/openapi</c> itself.</para>
/// <para><c>Harness:RepoRoot</c> must name the repository: a root with no <c>contracts</c> directory, for
/// example a mistyped root or the default <c>/repo</c> outside a container, is refused with exit code 1
/// before readiness, so no request is sent and nothing is created.</para>
/// <para>The request carries no header of its own: the admin listener is unauthenticated and contained by
/// network placement. <c>Harness:Target:AdminUrl</c> must therefore be an absolute <c>http</c> or
/// <c>https</c> URL without user information; it is checked before readiness, and no line quotes the
/// setting. Every outcome is one console line and exit code 0 (written) or 1 (anything else);
/// <see cref="RunAsync"/> never throws.</para>
/// </remarks>
/// <param name="options">The harness settings: the repository root, the facade admin URL, the request limit and the contract path.</param>
/// <param name="stdout">Where the mode's console lines go.</param>
/// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="stdout"/> is null.</exception>
public sealed class ExportOpenApiMode(HarnessOptions options, TextWriter stdout)
{
    private const string Component = "facade";
    private const string LinePrefix = "export-openapi: ";

    private readonly HarnessOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TextWriter _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));

    /// <summary>Waits for the facade, fetches its OpenAPI document and writes the contract file.</summary>
    /// <param name="ct">Cancels the readiness wait, the fetch and the write; cancellation exits 1.</param>
    /// <returns>0 when the contract file was written; 1 otherwise.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        try
        {
            return await ExportAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var line = $"{LinePrefix}failed: {ex.GetType().FullName}: {ex.Message}";
            try
            {
                _stdout.WriteLine(line);
            }
            catch (Exception writeFailure) when (writeFailure is IOException or ObjectDisposedException)
            {
                // The mode's own writer is unusable; the failure still has to reach the operator.
                try
                {
                    Console.Error.WriteLine(line);
                }
                catch (Exception errorFailure) when (errorFailure is IOException or ObjectDisposedException)
                {
                    // Standard error is unusable as well. Exit code 1 still reports the failure, and
                    // RunAsync must not throw.
                }
            }

            return 1;
        }
    }

    private async Task<int> ExportAsync(CancellationToken ct)
    {
        var adminUrl = _options.Target.AdminUrl?.Trim();

        // Readiness treats a blank URL as an external component with nothing to probe; here it means
        // there is no document to export, which is a failure.
        if (string.IsNullOrEmpty(adminUrl))
        {
            Print("Harness:Target:AdminUrl is blank; there is no facade admin listener to export from. Nothing written.");
            return 1;
        }

        // Checked before readiness so that no line ever quotes the setting, which may carry a password;
        // only the parsed URL, which then holds no user information, is printed.
        if (!CaseRunner.TryParseHttpUrl(adminUrl, out var adminUri, out var problem))
        {
            Print($"Harness:Target:AdminUrl is not usable: {problem} Nothing written.");
            return 1;
        }

        // A root without contracts/ is not the repository: a mistyped Harness:RepoRoot, or the default /repo
        // outside a container. Refused before readiness, so it fails at once, sends no request and builds no
        // contracts tree outside the repository.
        var contractsDirectory = _options.ResolvePath("contracts");
        if (!Directory.Exists(contractsDirectory))
        {
            Print($"no contracts directory at {contractsDirectory}; Harness:RepoRoot does not name the repository root. Nothing written.");
            return 1;
        }

        using (var readiness = new Readiness(_options))
        {
            var notReady = await readiness.WaitAsync(Component, adminUrl, ct).ConfigureAwait(false);
            if (notReady is not null)
            {
                Print($"facade admin not ready at {adminUri} ({notReady.ItemId} {notReady.Nature})");
                return 1;
            }
        }

        var documentUri = CaseRunner.Combine(adminUri, AdminRoutes.OpenApiDocument);
        CaseResponse response;
        using (var runner = CaseRunner.FromOptions(_options))
        {
            response = await runner
                .GetAsync(documentUri, CaseRunner.RequestTimeoutOf(_options), ct)
                .ConfigureAwait(false);
        }

        if (!response.Answered)
        {
            var detail = response.Detail is null ? "" : $"; {response.Detail}";
            Print($"no response from {documentUri} (no-response:{response.NoResponse}{detail}). Nothing written.");
            return 1;
        }

        if (response.Status != 200)
        {
            Print(string.Create(
                CultureInfo.InvariantCulture,
                $"{documentUri} answered status {response.Status}, not 200. Nothing written."));
            return 1;
        }

        if (!IsJsonObject(response.Body, out var parseError))
        {
            Print($"{documentUri} did not return a JSON object ({parseError}). Nothing written.");
            return 1;
        }

        var target = _options.ContractsOpenApiPath;
        await WriteAtomicallyAsync(target, response.Body, ct).ConfigureAwait(false);
        Print(string.Create(CultureInfo.InvariantCulture, $"wrote {response.Body.Length} bytes to {target}"));
        return 0;
    }

    // An OpenAPI description is a JSON object. The body is only parsed to check that; the bytes written
    // are the received ones, never a re-serialisation of the parsed document.
    private static bool IsJsonObject(byte[] body, out string error)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                error = "";
                return true;
            }

            error = $"root is {document.RootElement.ValueKind}";
            return false;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // The temporary file sits in the target's directory so the move is a same-volume rename, which
    // replaces the target in one step: the contract is either the old file or the complete new one.
    private static async Task WriteAtomicallyAsync(string target, byte[] bytes, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(target)
            ?? throw new IOException($"The contract path '{target}' has no directory.");
        if (!Directory.Exists(directory))
        {
            // Only the openapi/ leaf is ever created. Directory.CreateDirectory also creates missing
            // ancestors, so it is called only once the parent, contracts/, is known to exist; a parent that
            // has gone since the repository check means the root is no longer the repository.
            var parent = Path.GetDirectoryName(directory);
            if (parent is null || !Directory.Exists(parent))
            {
                throw new IOException(
                    $"The contract directory '{directory}' cannot be created: its parent '{parent}' is not a directory.");
            }

            Directory.CreateDirectory(directory);
        }

        var temporary = Path.Combine(
            directory,
            string.Create(CultureInfo.InvariantCulture, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp"));
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            // Only reached with the file still present when the write or the move failed.
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void Print(string message) => _stdout.WriteLine(LinePrefix + message);
}

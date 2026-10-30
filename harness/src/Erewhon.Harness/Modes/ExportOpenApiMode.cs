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
/// checked-in file is what the facade serves. It is published through directory handles rooted at the
/// repository (<see cref="ConfinedTree.PublishFileAsync"/>): each directory on the way is opened without
/// following a link, so a linked <c>contracts</c>, <c>contracts/openapi</c> or contract file is refused and
/// left in place, and nothing outside the repository is written. Only <c>contracts/openapi</c> may be
/// created; a <c>contracts</c> directory that is gone by the write fails the export and is not recreated.
/// The bytes go to a temporary file in <c>contracts/openapi</c> that one rename moves over the contract, so
/// a reader sees the old file or the complete new one, and a failed fetch or write leaves the previous
/// contract untouched. No other file is written.</para>
/// <para><c>Harness:RepoRoot</c> must name the repository: a root with no <c>contracts</c> directory, for
/// example a mistyped root or the default <c>/repo</c> outside a container, is refused with exit code 1
/// before readiness, so no request is sent and nothing is created.</para>
/// <para>The request carries no header of its own: the admin listener is unauthenticated and contained by
/// network placement. <c>Harness:Target:AdminUrl</c> must therefore be an absolute <c>http</c> or
/// <c>https</c> URL without user information; it is checked before readiness, and no line quotes the
/// setting. The readiness wait is announced with its limit before it starts. Every outcome is one console
/// line, and every line is printed through <see cref="LifecycleConsole.Display"/> so that no path or
/// message can break it or drive the terminal; the exit code is 0 (written) or 1 (anything else).
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
            // Escaped once, for whichever sink takes it: the message can quote paths and parser input.
            var line = LifecycleConsole.Display($"{LinePrefix}failed: {ex.GetType().FullName}: {ex.Message}");
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

        // The wait can last the whole readiness limit, so it is announced; the URL is not quoted.
        Print(string.Create(
            CultureInfo.InvariantCulture,
            $"waiting up to {_options.ReadinessTimeout.TotalSeconds:0} s for the {Component} admin listener"));
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

        // The received bytes, published through directory handles rooted at the repository: only
        // contracts/openapi may be created, a link on the way or at the target is refused, and the previous
        // contract is replaced in one rename or left as it was.
        var target = _options.ContractsOpenApiPath;
        await new ConfinedTree(_options.ResolvePath("."))
            .PublishFileAsync(target, response.Body, ct)
            .ConfigureAwait(false);
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

    // Paths, URLs and parser messages are escaped by Display, so each message stays one inert line. A
    // writer that throws reaches RunAsync's handler, which falls back to standard error.
    private void Print(string message) => _stdout.WriteLine(LifecycleConsole.Display(LinePrefix + message));
}

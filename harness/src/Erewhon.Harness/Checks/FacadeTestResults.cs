using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Checks;

/// <summary>
/// What the facade tests' TRX file says: the routing, failure-behaviour and listener-isolation counts
/// the report shows, and one failure item per failed or not-run test invocation.
/// </summary>
/// <param name="Routing">The <see cref="CheckNames.Routing"/> check, counted from <c>*RoutingTests</c> results.</param>
/// <param name="Failure">
/// The <see cref="CheckNames.Failure"/> check, counted from <c>*FailureBehaviourTests</c> results; its
/// <see cref="CheckResult.Detail"/> carries the unreachable and timeout split.
/// </param>
/// <param name="ListenerIsolation">
/// The <see cref="CheckNames.ListenerIsolation"/> check, counted from <c>*ListenerIsolationTests</c> results.
/// </param>
/// <param name="Failures">
/// <c>TEST:&lt;className&gt;/&lt;testName&gt;</c> items (<c>failed:&lt;signature&gt;</c> or <c>not-run</c>) and
/// <c>CHK-FACADE-TRX</c> <c>missing</c> items, sorted ordinally by item id and free of duplicate
/// (item id, nature) pairs.
/// </param>
/// <param name="ExitCode">
/// The <c>dotnet test</c> exit status recorded in <c>exit-code</c>; null when that file is absent or
/// does not hold an integer.
/// </param>
public sealed record FacadeTestSummary(
    CheckResult Routing,
    CheckResult Failure,
    CheckResult ListenerIsolation,
    IReadOnlyList<FailureItem> Failures,
    int? ExitCode);

/// <summary>
/// Reads the results the <c>facade-tests</c> service leaves in
/// <see cref="HarnessOptions.FacadeTestsDirectory"/>: <c>facade-tests.trx</c> (VS TeamTest TRX written
/// by xunit.v3's <c>--report-xunit-trx</c>) and <c>exit-code</c>, both written by
/// <c>facade/tests/run-tests.sh</c>.
/// </summary>
/// <remarks>
/// <para>A test result is classified by the ordinal suffix of its class name. A result of any other
/// class still yields a failure item when it fails, but is counted in no check.</para>
/// <para>A failed result's signature hashes the exception type, the file name and line of the first
/// stack frame that carries source information, and the assertion message. Only the file name is kept
/// from that frame, so the same failure gives the same signature under <c>/src/facade</c> in the image,
/// in a local checkout and on Windows.</para>
/// <para>Nothing is ever written. Instances hold no mutable state and are thread-safe.</para>
/// </remarks>
public sealed partial class FacadeTestResults(HarnessOptions options)
{
    /// <summary>The TRX file name passed to <c>--report-xunit-trx-filename</c>.</summary>
    public const string TrxFileName = "facade-tests.trx";

    /// <summary>The file <c>run-tests.sh</c> writes the <c>dotnet test</c> exit status to.</summary>
    public const string ExitCodeFileName = "exit-code";

    private const string RoutingClassSuffix = "RoutingTests";
    private const string FailureClassSuffix = "FailureBehaviourTests";
    private const string ListenerIsolationClassSuffix = "ListenerIsolationTests";

    private const string PassedOutcome = "Passed";
    private const string LineMarker = ":line ";
    private const string InMarker = " in ";

    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    // Outcomes of a result that never ran; every other outcome except Passed counts as failed.
    private static readonly string[] NotRunOutcomes = ["NotExecuted", "Inconclusive", "Pending"];

    // FailureBehaviourTests method names: RestCallToUnreachableBackend..., SoapCallToSlowBackend...AfterDeadline.
    private static readonly string[] TimeoutMethodMarkers = ["Timeout", "TimedOut", "Slow", "Deadline"];
    private const string UnreachableMethodMarker = "Unreachable";

    private readonly HarnessOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Reads <c>exit-code</c> and <c>facade-tests.trx</c> from <see cref="HarnessOptions.FacadeTestsDirectory"/>.
    /// An absent or unreadable TRX file gives one <c>CHK-FACADE-TRX</c> <c>missing</c> item and three
    /// <c>not-run</c> checks; I/O failures never escape.
    /// </summary>
    /// <returns>The counts, failure items and recorded exit status.</returns>
    public FacadeTestSummary Read()
    {
        var directory = _options.FacadeTestsDirectory;
        var exitCode = ReadExitCode(Path.Combine(directory, ExitCodeFileName));
        var trxPath = Path.Combine(directory, TrxFileName);

        if (!File.Exists(trxPath))
        {
            return Unavailable($"TRX file absent: {trxPath}", exitCode);
        }

        string trxXml;
        try
        {
            trxXml = File.ReadAllText(trxPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unavailable($"TRX file unreadable: {trxPath}: {ex.Message}", exitCode);
        }

        return Parse(trxXml, exitCode);
    }

    /// <summary>
    /// Turns TRX text into the three checks and their failure items. Text that is not well-formed XML
    /// gives one <c>CHK-FACADE-TRX</c> <c>missing</c> item and three <c>not-run</c> checks. A check whose
    /// test class has no result in the TRX is <c>not-run</c>, and one <c>CHK-FACADE-TRX</c>
    /// <c>missing</c> item names every such class.
    /// </summary>
    /// <param name="trxXml">The TRX file's text.</param>
    /// <param name="exitCode">The recorded <c>dotnet test</c> exit status, carried into the summary unchanged.</param>
    /// <returns>The counts, failure items and exit status.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="trxXml"/> is null.</exception>
    public static FacadeTestSummary Parse(string trxXml, int? exitCode)
    {
        ArgumentNullException.ThrowIfNull(trxXml);

        XDocument document;
        try
        {
            document = Load(trxXml);
        }
        catch (XmlException ex)
        {
            return Unavailable($"TRX file unparseable: {ex.Message}", exitCode);
        }

        var root = document.Root is { } run && run.Name == Trx + "TestRun" ? run : null;
        var definitions = ReadDefinitions(root);

        var routing = new Tally();
        var failure = new Tally();
        var listenerIsolation = new Tally();
        var unreachable = new Tally();
        var timeout = new Tally();
        var items = new List<FailureItem>();

        IEnumerable<XElement> results = root is null ? [] : root.Elements(Trx + "Results").Elements(Trx + "UnitTestResult");
        foreach (var result in results)
        {
            var test = Describe(result, definitions);
            var passed = string.Equals(test.Outcome, PassedOutcome, StringComparison.OrdinalIgnoreCase);

            var tally = ClassTally(test.ClassName, routing, failure, listenerIsolation);
            tally?.Count(passed);
            if (ReferenceEquals(tally, failure))
            {
                FailureKindTally(test.MethodName, unreachable, timeout)?.Count(passed);
            }

            if (!passed)
            {
                items.Add(ToFailureItem(test));
            }
        }

        var absentClasses = new List<string>();
        var routingCheck = ToCheck(CheckNames.Routing, routing, RoutingClassSuffix, absentClasses, detail: null);
        var failureCheck = ToCheck(CheckNames.Failure, failure, FailureClassSuffix, absentClasses, FailureSplit(unreachable, timeout));
        var listenerCheck = ToCheck(CheckNames.ListenerIsolation, listenerIsolation, ListenerIsolationClassSuffix, absentClasses, detail: null);

        if (absentClasses.Count > 0)
        {
            items.Add(new FailureItem(
                FailureItemIds.FacadeTrx,
                FailureItemIds.Missing,
                Detail: $"TRX file holds no results for {string.Join(", ", absentClasses)}"));
        }

        return new FacadeTestSummary(routingCheck, failureCheck, listenerCheck, Normalise(items), exitCode);
    }

    /// <summary>
    /// Returns the signature of a failed result: the first 16 hex digits of the SHA-256 of
    /// <c>exceptionType + "\n" + fileName + ":" + line + "\n" + message</c>.
    /// </summary>
    /// <remarks>
    /// The message has CRLF turned into LF and trailing white space removed. The exception type is a
    /// leading dotted type name followed by <c>:</c> in the message, as xunit.v3 writes an unexpected
    /// exception (<c>System.Net.Http.HttpRequestException : …</c>); assertion failures carry none. The
    /// file name and line come from the first stack frame of the form <c>… in &lt;path&gt;:line &lt;n&gt;</c>,
    /// and only the path's last segment is kept, after <c>\</c> becomes <c>/</c>, because
    /// <see cref="Path.GetFileName(string)"/> does not split on <c>\</c> on Linux. Both are empty when
    /// absent. No elapsed time, port or absolute path enters the text.
    /// </remarks>
    /// <param name="message">The <c>ErrorInfo/Message</c> text; null is treated as empty.</param>
    /// <param name="stackTrace">The <c>ErrorInfo/StackTrace</c> text; null is treated as empty.</param>
    /// <returns>The 16-digit lowercase hexadecimal signature.</returns>
    internal static string FailureSignature(string? message, string? stackTrace)
    {
        var text = NormaliseMessage(message);
        var match = ExceptionTypePattern().Match(text);
        var exceptionType = match.Success ? match.Groups["type"].Value : string.Empty;
        var (file, line) = FirstSourceLocation(stackTrace);
        return DiscrepancySignature.Hash16($"{exceptionType}\n{file}:{line}\n{text}");
    }

    [GeneratedRegex(@"^\s*(?<type>[A-Za-z_][\w`]*(?:\.[A-Za-z_][\w`]*)+(?:\+[A-Za-z_][\w`]*)*)\s*:", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex ExceptionTypePattern();

    private static XDocument Load(string trxXml)
    {
        // TRX never carries a DTD; refusing one keeps entity expansion out of a file the harness
        // reads from a shared bind mount.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };
        using var text = new StringReader(trxXml.TrimStart('\uFEFF'));
        using var reader = XmlReader.Create(text, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static Dictionary<string, TestDefinition> ReadDefinitions(XElement? root)
    {
        var definitions = new Dictionary<string, TestDefinition>(StringComparer.Ordinal);
        if (root is null)
        {
            return definitions;
        }

        foreach (var unitTest in root.Elements(Trx + "TestDefinitions").Elements(Trx + "UnitTest"))
        {
            var id = (string?)unitTest.Attribute("id");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var method = unitTest.Element(Trx + "TestMethod");
            definitions.TryAdd(id, new TestDefinition(
                (string?)unitTest.Attribute("name"),
                (string?)method?.Attribute("className"),
                (string?)method?.Attribute("name")));
        }

        return definitions;
    }

    private static TestResultInfo Describe(XElement result, Dictionary<string, TestDefinition> definitions)
    {
        var testId = (string?)result.Attribute("testId");
        var definition = testId is not null && definitions.TryGetValue(testId, out var found) ? found : null;

        // xunit.v3 writes the fully qualified display name, theory arguments included, so each theory
        // invocation keeps its own item id.
        var testName = NullIfEmpty((string?)result.Attribute("testName"))
            ?? NullIfEmpty(definition?.Name)
            ?? testId
            ?? string.Empty;

        var errorInfo = result.Element(Trx + "Output")?.Element(Trx + "ErrorInfo");
        return new TestResultInfo(
            NullIfBlank(definition?.ClassName) ?? ClassFromDisplayName(testName),
            testName,
            NullIfBlank(definition?.MethodName) ?? MethodFromDisplayName(testName),
            ((string?)result.Attribute("outcome"))?.Trim() ?? string.Empty,
            (string?)errorInfo?.Element(Trx + "Message"),
            (string?)errorInfo?.Element(Trx + "StackTrace"));
    }

    private static FailureItem ToFailureItem(TestResultInfo test)
    {
        var nature = IsNotRun(test.Outcome)
            ? FailureItemIds.NotRun
            : FailureItemIds.Failed(FailureSignature(test.Message, test.StackTrace));
        return new FailureItem(
            FailureItemIds.Test(test.ClassName, test.TestName),
            nature,
            Operation: null,
            Detail: FirstLine(NormaliseMessage(test.Message)));
    }

    private static bool IsNotRun(string outcome) =>
        NotRunOutcomes.Contains(outcome, StringComparer.OrdinalIgnoreCase);

    private static Tally? ClassTally(string className, Tally routing, Tally failure, Tally listenerIsolation)
    {
        if (className.EndsWith(RoutingClassSuffix, StringComparison.Ordinal))
        {
            return routing;
        }

        if (className.EndsWith(FailureClassSuffix, StringComparison.Ordinal))
        {
            return failure;
        }

        return className.EndsWith(ListenerIsolationClassSuffix, StringComparison.Ordinal) ? listenerIsolation : null;
    }

    private static Tally? FailureKindTally(string methodName, Tally unreachable, Tally timeout)
    {
        if (methodName.Contains(UnreachableMethodMarker, StringComparison.OrdinalIgnoreCase))
        {
            return unreachable;
        }

        return TimeoutMethodMarkers.Any(marker => methodName.Contains(marker, StringComparison.OrdinalIgnoreCase))
            ? timeout
            : null;
    }

    private static CheckResult ToCheck(string name, Tally tally, string classSuffix, List<string> absentClasses, string? detail)
    {
        if (tally.Cases == 0)
        {
            absentClasses.Add(classSuffix);
            return NotRunCheck(name);
        }

        var outcome = tally.Passed == tally.Cases ? CheckOutcomes.Pass : CheckOutcomes.Fail;
        return new CheckResult(name, outcome, tally.Cases, tally.Passed, detail);
    }

    private static string? FailureSplit(Tally unreachable, Tally timeout)
    {
        var parts = new List<string>(2);
        if (unreachable.Cases > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"unreachable {unreachable.Passed}/{unreachable.Cases} passed"));
        }

        if (timeout.Cases > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"timeout {timeout.Passed}/{timeout.Cases} passed"));
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static IReadOnlyList<FailureItem> Normalise(List<FailureItem> items)
    {
        var seen = new HashSet<(string ItemId, string Nature)>();
        return items
            .Where(item => seen.Add((item.ItemId, item.Nature)))
            .OrderBy(item => item.ItemId, StringComparer.Ordinal)
            .ThenBy(item => item.Nature, StringComparer.Ordinal)
            .ToArray();
    }

    private static FacadeTestSummary Unavailable(string detail, int? exitCode) => new(
        NotRunCheck(CheckNames.Routing),
        NotRunCheck(CheckNames.Failure),
        NotRunCheck(CheckNames.ListenerIsolation),
        [new FailureItem(FailureItemIds.FacadeTrx, FailureItemIds.Missing, Detail: detail)],
        exitCode);

    private static CheckResult NotRunCheck(string name) => new(name, CheckOutcomes.NotRun, 0, 0, null);

    private static int? ReadExitCode(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (string File, string Line) FirstSourceLocation(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace))
        {
            return (string.Empty, string.Empty);
        }

        foreach (var rawFrame in stackTrace.Split('\n'))
        {
            var frame = rawFrame.AsSpan().TrimEnd();
            var lineAt = frame.LastIndexOf(LineMarker, StringComparison.Ordinal);
            if (lineAt < 0)
            {
                continue;
            }

            var number = frame[(lineAt + LineMarker.Length)..];
            var pathAt = frame[..lineAt].LastIndexOf(InMarker, StringComparison.Ordinal);
            if (number.IsEmpty || number.ContainsAnyExceptInRange('0', '9') || pathAt < 0)
            {
                continue;
            }

            var path = frame[(pathAt + InMarker.Length)..lineAt].ToString().Replace('\\', '/');
            var file = path[(path.LastIndexOf('/') + 1)..];
            if (file.Length > 0)
            {
                return (file, number.ToString());
            }
        }

        return (string.Empty, string.Empty);
    }

    private static string NormaliseMessage(string? message) =>
        (message ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static string? FirstLine(string normalisedMessage)
    {
        var end = normalisedMessage.IndexOf('\n', StringComparison.Ordinal);
        var line = (end < 0 ? normalisedMessage : normalisedMessage[..end]).TrimEnd();
        return line.Length == 0 ? null : line;
    }

    // The text before the last '.' of the part preceding any '(': "Ns.Class.Method(x: 1)" gives "Ns.Class".
    private static string ClassFromDisplayName(string testName)
    {
        var head = DisplayNameHead(testName);
        var dot = head.LastIndexOf('.');
        return dot > 0 ? head[..dot] : string.Empty;
    }

    private static string MethodFromDisplayName(string testName)
    {
        var head = DisplayNameHead(testName);
        return head[(head.LastIndexOf('.') + 1)..];
    }

    private static string DisplayNameHead(string testName)
    {
        var paren = testName.IndexOf('(', StringComparison.Ordinal);
        return paren < 0 ? testName : testName[..paren];
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record TestDefinition(string? Name, string? ClassName, string? MethodName);

    private sealed record TestResultInfo(
        string ClassName,
        string TestName,
        string MethodName,
        string Outcome,
        string? Message,
        string? StackTrace);

    private sealed class Tally
    {
        public int Cases { get; private set; }

        public int Passed { get; private set; }

        public void Count(bool passed)
        {
            Cases++;
            if (passed)
            {
                Passed++;
            }
        }
    }
}

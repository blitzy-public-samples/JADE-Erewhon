using System.Xml.Linq;
using Erewhon.Harness.Checks;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Tests;

/// <summary>
/// Reading the facade tests' TRX file and <c>exit-code</c>: the routing, failure and listener-isolation
/// counts exactly as the TRX states them, one <c>TEST</c> item per failed or not-run invocation, and a
/// single <c>CHK-FACADE-TRX</c> <c>missing</c> item whenever the TRX or the run's completion evidence is
/// absent, unusable or inconsistent, so an all-passed TRX never certifies an unverified run. Files are
/// written only to a temporary directory of the test's own.
/// </summary>
public sealed class FacadeTestResultsTests : IDisposable
{
    private const string RoutingClass = "Erewhon.Facade.Tests.RoutingTests";
    private const string FailureClass = "Erewhon.Facade.Tests.FailureBehaviourTests";
    private const string ListenerClass = "Erewhon.Facade.Tests.ListenerIsolationTests";
    private const string OtherClass = "Erewhon.Facade.Tests.CatalogueTests";

    private const string RerouteMethod = "RerouteOneOperation";
    private const string RestArguments = "(operationId: \"Rest.getCompany\")";
    private const string SoapArguments = "(operationId: \"Soap.getAgentNames\")";

    private const string AssertionMessage = "Assert.Equal() Failure: Values differ\nExpected: 502\nActual:   200";
    private const int AssertionLine = 42;
    private const string FacadeTestsRelativePath = "Erewhon.Facade.Tests/RoutingTests.cs";

    // 60 digits: no int holds it, and the quoted excerpt is cut at 40 characters.
    private const string OverlongExitCode = "777777777777777777777777777777777777777777777777777777777777";
    private const string OverlongExcerpt = "'7777777777777777777777777777777777777777…'";

    private static readonly XNamespace TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    private readonly TempDirectory _temp = new();

    /// <summary>Deletes the test's temporary directory.</summary>
    public void Dispose() => _temp.Dispose();

    // ---- Positive control ----------------------------------------------------------------------

    [Fact]
    public void AllPassed_WithExitZero_GivesThreePassingChecksAndNoFailure()
    {
        var summary = FacadeTestResults.Parse(AllPassedTrx(), 0);

        Assert.Equal(new CheckResult(CheckNames.Routing, CheckOutcomes.Pass, 2, 2, null), summary.Routing);
        Assert.Equal(
            new CheckResult(CheckNames.Failure, CheckOutcomes.Pass, 2, 2, "unreachable 1/1 passed; timeout 1/1 passed"),
            summary.Failure);
        Assert.Equal(new CheckResult(CheckNames.ListenerIsolation, CheckOutcomes.Pass, 1, 1, null), summary.ListenerIsolation);
        Assert.Empty(summary.Failures);
        Assert.Equal(0, summary.ExitCode);
    }

    // ---- Completion evidence -------------------------------------------------------------------

    [Fact]
    public void AllPassed_WithoutExitStatus_GivesOneMissingItem()
    {
        var summary = FacadeTestResults.Parse(AllPassedTrx(), exitCode: null);

        AssertAllPassedCounts(summary);
        var item = AssertSingleTrxItem(summary);
        Assert.Contains("no valid dotnet test exit status", item.Detail, StringComparison.Ordinal);
        Assert.Null(summary.ExitCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(127)]
    [InlineData(-1)]
    public void AllPassed_WithNonzeroExit_GivesMissingItemNamingTheCode(int exitCode)
    {
        var summary = FacadeTestResults.Parse(AllPassedTrx(), exitCode);

        AssertAllPassedCounts(summary);
        var item = AssertSingleTrxItem(summary);
        Assert.Contains($"dotnet test exited {exitCode}", item.Detail, StringComparison.Ordinal);
        Assert.Equal(exitCode, summary.ExitCode);
    }

    [Theory]
    [InlineData(0, "dotnet test exited 0, but the TRX records 1 failed result(s)")]
    [InlineData(1, "dotnet test exited 1, so it did not complete normally")]
    [InlineData(3, "dotnet test exited 3, so it did not complete normally")]
    public void FailedResult_WithExitOtherThanTwo_GivesTestItemAndMissingItem(int exitCode, string expectedReason)
    {
        var summary = FacadeTestResults.Parse(OneFailedTrx(), exitCode);

        Assert.Equal(new CheckResult(CheckNames.Routing, CheckOutcomes.Fail, 2, 1, null), summary.Routing);
        Assert.Equal(2, summary.Failures.Count);
        var test = Assert.Single(summary.Failures, item => item.ItemId.StartsWith("TEST:", StringComparison.Ordinal));
        Assert.Equal(FailureItemIds.Test(RoutingClass, RoutingTestName(RestArguments)), test.ItemId);
        Assert.StartsWith("failed:", test.Nature, StringComparison.Ordinal);
        var trx = Assert.Single(summary.Failures, item => item.ItemId == FailureItemIds.FacadeTrx);
        Assert.Equal(FailureItemIds.Missing, trx.Nature);
        Assert.Contains(expectedReason, trx.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedResult_WithExitTwo_GivesOnlyTheTestItem()
    {
        var summary = FacadeTestResults.Parse(OneFailedTrx(), 2);

        var item = Assert.Single(summary.Failures);
        Assert.Equal(FailureItemIds.Test(RoutingClass, RoutingTestName(RestArguments)), item.ItemId);
        Assert.Equal(ExpectedNature(string.Empty, "RoutingTests.cs", AssertionLine, AssertionMessage), item.Nature);
        Assert.Equal("Assert.Equal() Failure: Values differ", item.Detail);
        Assert.Equal(2, summary.ExitCode);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, false)]
    public void FailedResultOfUncountedClass_CountsTowardsCompletion(int exitCode, bool expectMissing)
    {
        var trx = Trx([.. AllPassedResults(), new TrxResult(OtherClass, "LoadsCatalogue", "Failed", Message: "boom")]);

        var summary = FacadeTestResults.Parse(trx, exitCode);

        AssertAllPassedCounts(summary);
        Assert.Single(summary.Failures, item => item.ItemId == FailureItemIds.Test(OtherClass, $"{OtherClass}.LoadsCatalogue"));
        Assert.Equal(expectMissing, summary.Failures.Any(item => item.ItemId == FailureItemIds.FacadeTrx));
    }

    [Fact]
    public void NotRunResultOnly_WithExitTwo_GivesMissingItem()
    {
        var trx = Trx([.. AllPassedResults(), new TrxResult(RoutingClass, "Skipped", "NotExecuted")]);

        var summary = FacadeTestResults.Parse(trx, 2);

        var item = Assert.Single(summary.Failures, failure => failure.ItemId == FailureItemIds.FacadeTrx);
        Assert.Contains("dotnet test exited 2 (at least one test failed), but the TRX records no failed result", item.Detail, StringComparison.Ordinal);
        Assert.Single(summary.Failures, failure => failure.Nature == FailureItemIds.NotRun);
    }

    // ---- exit-code file ------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "exit-code file absent: ")]
    [InlineData("", "does not hold an integer: ''")]
    [InlineData("  \n", "does not hold an integer: ''")]
    [InlineData("abc", "does not hold an integer: 'abc'")]
    [InlineData("0 1", "does not hold an integer: '0 1'")]
    [InlineData("0\n1\n", "does not hold an integer: '0 1'")]
    [InlineData(OverlongExitCode, OverlongExcerpt)]
    public void Read_UnusableExitCodeFile_GivesMissingItemNamingTheProblem(string? exitCode, string expectedReason)
    {
        WriteTrx(AllPassedTrx());
        if (exitCode is not null)
        {
            WriteExitCode(exitCode);
        }

        var summary = Read();

        AssertAllPassedCounts(summary);
        var item = AssertSingleTrxItem(summary);
        Assert.Contains(expectedReason, item.Detail, StringComparison.Ordinal);
        Assert.Contains(FacadeTestResults.ExitCodeFileName, item.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", item.Detail, StringComparison.Ordinal);
        Assert.Null(summary.ExitCode);
    }

    [Fact]
    public void Read_ExitCodeDirectory_GivesMissingItemNamingTheProblem()
    {
        WriteTrx(AllPassedTrx());
        Directory.CreateDirectory(_temp.PathOf($"artifacts/facade-tests/{FacadeTestResults.ExitCodeFileName}"));

        var summary = Read();

        AssertAllPassedCounts(summary);
        var item = AssertSingleTrxItem(summary);
        Assert.Contains("exit-code file unreadable: ", item.Detail, StringComparison.Ordinal);
        Assert.EndsWith("is a directory", item.Detail, StringComparison.Ordinal);
        Assert.Null(summary.ExitCode);
    }

    [Theory]
    [InlineData("0\n", false, 0)]
    [InlineData(" 0 ", false, 0)]
    [InlineData("2\r\n", true, 2)]
    public void Read_ConsistentExitCodeFile_AddsNoTrxItem(string exitCode, bool withFailedResult, int expectedCode)
    {
        WriteTrx(withFailedResult ? OneFailedTrx() : AllPassedTrx());
        WriteExitCode(exitCode);

        var summary = Read();

        Assert.DoesNotContain(summary.Failures, item => item.ItemId == FailureItemIds.FacadeTrx);
        Assert.Equal(withFailedResult ? 1 : 0, summary.Failures.Count);
        Assert.Equal(expectedCode, summary.ExitCode);
    }

    // ---- Unusable TRX --------------------------------------------------------------------------

    [Theory]
    [InlineData("0", null)]
    [InlineData("2", null)]
    [InlineData("3", "dotnet test exited 3, so it did not complete normally")]
    [InlineData(null, "exit-code file absent: ")]
    public void Read_TrxAbsent_GivesOneMissingItemAndThreeNotRunChecks(string? exitCode, string? expectedCompletionReason)
    {
        if (exitCode is not null)
        {
            WriteExitCode(exitCode);
        }

        var summary = Read();

        AssertThreeNotRunChecks(summary);
        var item = AssertSingleTrxItem(summary);
        var trxReason = $"TRX file absent: {_temp.PathOf($"artifacts/facade-tests/{FacadeTestResults.TrxFileName}")}";
        if (expectedCompletionReason is null)
        {
            Assert.Equal(trxReason, item.Detail);
        }
        else
        {
            Assert.StartsWith(trxReason + "; ", item.Detail, StringComparison.Ordinal);
            Assert.Contains(expectedCompletionReason, item.Detail, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("<TestRun")]
    [InlineData("")]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE TestRun [<!ENTITY x "y">]><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results/></TestRun>""")]
    [InlineData("""<TestResults xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="Erewhon.Facade.Tests.RoutingTests.R" outcome="Passed"/></Results></TestResults>""")]
    [InlineData("""<TestRun xmlns="urn:not-trx"><Results><UnitTestResult testName="Erewhon.Facade.Tests.RoutingTests.R" outcome="Passed"/></Results></TestRun>""")]
    [InlineData("""<TestRun><Results><UnitTestResult testName="Erewhon.Facade.Tests.RoutingTests.R" outcome="Passed"/></Results></TestRun>""")]
    public void UnusableTrx_GivesMissingItemAndThreeNotRunChecks(string trxXml)
    {
        var summary = FacadeTestResults.Parse(trxXml, 0);

        AssertThreeNotRunChecks(summary);
        var item = AssertSingleTrxItem(summary);
        Assert.StartsWith("TRX file ", item.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NullTrx_Throws()
    {
        Assert.Throws<ArgumentNullException>("trxXml", () => FacadeTestResults.Parse(null!, 0));
    }

    // ---- Absent classes ------------------------------------------------------------------------

    [Fact]
    public void OneClassAbsent_GivesSingleMissingItemNamingTheClass()
    {
        var trx = Trx([.. AllPassedResults().Where(result => result.ClassName != ListenerClass)]);

        var summary = FacadeTestResults.Parse(trx, 0);

        Assert.Equal(CheckOutcomes.Pass, summary.Routing.Outcome);
        Assert.Equal(CheckOutcomes.Pass, summary.Failure.Outcome);
        Assert.Equal(new CheckResult(CheckNames.ListenerIsolation, CheckOutcomes.NotRun, 0, 0, null), summary.ListenerIsolation);
        var item = AssertSingleTrxItem(summary);
        Assert.Equal("TRX file holds no results for ListenerIsolationTests", item.Detail);
    }

    [Theory]
    [InlineData(null, "no valid dotnet test exit status")]
    [InlineData(3, "dotnet test exited 3")]
    public void OneClassAbsent_WithBadExitStatus_GivesOneItemNamingBoth(int? exitCode, string expectedCompletionReason)
    {
        var trx = Trx([.. AllPassedResults().Where(result => result.ClassName != ListenerClass)]);

        var summary = FacadeTestResults.Parse(trx, exitCode);

        var item = AssertSingleTrxItem(summary);
        Assert.StartsWith("TRX file holds no results for ListenerIsolationTests; ", item.Detail, StringComparison.Ordinal);
        Assert.Contains(expectedCompletionReason, item.Detail, StringComparison.Ordinal);
    }

    // ---- Test items ----------------------------------------------------------------------------

    [Theory]
    [InlineData("NotExecuted")]
    [InlineData("Inconclusive")]
    [InlineData("Pending")]
    public void NotRunOutcome_GivesNotRunTestItem(string outcome)
    {
        var trx = Trx([.. AllPassedResults(), new TrxResult(RoutingClass, "Skipped", outcome)]);

        var summary = FacadeTestResults.Parse(trx, 0);

        var item = Assert.Single(summary.Failures);
        Assert.Equal(FailureItemIds.Test(RoutingClass, $"{RoutingClass}.Skipped"), item.ItemId);
        Assert.Equal(FailureItemIds.NotRun, item.Nature);
        Assert.Equal(new CheckResult(CheckNames.Routing, CheckOutcomes.Fail, 3, 2, null), summary.Routing);
    }

    [Theory]
    [InlineData("Assert.Equal() Failure: Values differ", "")]
    [InlineData("System.Net.Http.HttpRequestException : Connection refused (127.0.0.1:1)", "System.Net.Http.HttpRequestException")]
    public void FailedResult_NatureHashesExceptionTypeFileLineAndMessage(string message, string expectedExceptionType)
    {
        var trx = Trx([.. AllPassedResults(), new TrxResult(RoutingClass, "Fails", "Failed", null, message, StackTrace("/src/facade/tests", AssertionLine))]);

        var item = Assert.Single(FacadeTestResults.Parse(trx, 2).Failures);

        Assert.Equal(ExpectedNature(expectedExceptionType, "RoutingTests.cs", AssertionLine, message), item.Nature);
    }

    [Theory]
    [InlineData("/src/facade/tests")]
    [InlineData("/home/u/repo/facade/tests")]
    [InlineData(@"C:\r\facade\tests")]
    public void FailureSignature_IgnoresThePathPrefix(string prefix)
    {
        var summary = FacadeTestResults.Parse(FailedTrx(AssertionMessage, StackTrace(prefix, AssertionLine)), 2);

        var item = Assert.Single(summary.Failures);
        Assert.Equal(ExpectedNature(string.Empty, "RoutingTests.cs", AssertionLine, AssertionMessage), item.Nature);
    }

    [Theory]
    [InlineData(AssertionMessage, AssertionLine + 1)]
    [InlineData("Assert.Equal() Failure: Values differ\nExpected: 502\nActual:   504", AssertionLine)]
    [InlineData("System.InvalidOperationException : boom", AssertionLine)]
    public void FailureSignature_DiffersForAnotherLineOrMessage(string message, int line)
    {
        var reference = Assert.Single(FacadeTestResults.Parse(FailedTrx(AssertionMessage, StackTrace("/src/facade/tests", AssertionLine)), 2).Failures);

        var other = Assert.Single(FacadeTestResults.Parse(FailedTrx(message, StackTrace("/src/facade/tests", line)), 2).Failures);

        Assert.Equal(reference.ItemId, other.ItemId);
        Assert.NotEqual(reference.Nature, other.Nature);
    }

    [Fact]
    public void TwoTheoryInvocations_GiveTwoDistinctTestItems()
    {
        var stack = StackTrace("/src/facade/tests", AssertionLine);
        var trx = Trx(
            new TrxResult(RoutingClass, RerouteMethod, "Failed", RestArguments, AssertionMessage, stack),
            new TrxResult(RoutingClass, RerouteMethod, "Failed", SoapArguments, AssertionMessage, stack),
            new TrxResult(FailureClass, "RestCallToUnreachableBackend_Returns502", "Passed"),
            new TrxResult(ListenerClass, "AdminPathsOnPublicListeners_ReachCatchAll", "Passed"));

        var summary = FacadeTestResults.Parse(trx, 2);

        string[] expectedIds =
        [
            FailureItemIds.Test(RoutingClass, RoutingTestName(RestArguments)),
            FailureItemIds.Test(RoutingClass, RoutingTestName(SoapArguments)),
        ];
        Assert.Equal(expectedIds, summary.Failures.Select(item => item.ItemId));
        Assert.Equal(summary.Failures[0].Nature, summary.Failures[1].Nature);
        Assert.Equal(new CheckResult(CheckNames.Routing, CheckOutcomes.Fail, 2, 0, null), summary.Routing);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private FacadeTestSummary Read() => new FacadeTestResults(new HarnessOptions { RepoRoot = _temp.Root }).Read();

    private void WriteTrx(string trxXml) =>
        _temp.Write($"artifacts/facade-tests/{FacadeTestResults.TrxFileName}", trxXml);

    private void WriteExitCode(string content) =>
        _temp.Write($"artifacts/facade-tests/{FacadeTestResults.ExitCodeFileName}", content);

    private static void AssertAllPassedCounts(FacadeTestSummary summary)
    {
        Assert.Equal(new CheckResult(CheckNames.Routing, CheckOutcomes.Pass, 2, 2, null), summary.Routing);
        Assert.Equal(CheckOutcomes.Pass, summary.Failure.Outcome);
        Assert.Equal(2, summary.Failure.Passed);
        Assert.Equal(new CheckResult(CheckNames.ListenerIsolation, CheckOutcomes.Pass, 1, 1, null), summary.ListenerIsolation);
    }

    private static void AssertThreeNotRunChecks(FacadeTestSummary summary)
    {
        Assert.Equal(new CheckResult(CheckNames.Routing, CheckOutcomes.NotRun, 0, 0, null), summary.Routing);
        Assert.Equal(new CheckResult(CheckNames.Failure, CheckOutcomes.NotRun, 0, 0, null), summary.Failure);
        Assert.Equal(new CheckResult(CheckNames.ListenerIsolation, CheckOutcomes.NotRun, 0, 0, null), summary.ListenerIsolation);
    }

    private static FailureItem AssertSingleTrxItem(FacadeTestSummary summary)
    {
        var item = Assert.Single(summary.Failures);
        Assert.Equal(FailureItemIds.FacadeTrx, item.ItemId);
        Assert.Equal(FailureItemIds.Missing, item.Nature);
        Assert.False(string.IsNullOrWhiteSpace(item.Detail));
        return item;
    }

    // The signature as the identity table defines it, computed independently of the parser.
    private static string ExpectedNature(string exceptionType, string file, int line, string message) =>
        FailureItemIds.Failed(DiscrepancySignature.Hash16($"{exceptionType}\n{file}:{line}\n{message}"));

    private static string RoutingTestName(string arguments) => $"{RoutingClass}.{RerouteMethod}{arguments}";

    private static string StackTrace(string prefix, int line)
    {
        var separator = prefix.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
        var file = prefix + separator + FacadeTestsRelativePath.Replace('/', separator);
        return $"   at {RoutingClass}.{RerouteMethod}(String operationId) in {file}:line {line}\n"
            + "   at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)";
    }

    private static TrxResult[] AllPassedResults() =>
    [
        new(RoutingClass, RerouteMethod, "Passed", RestArguments),
        new(RoutingClass, RerouteMethod, "Passed", SoapArguments),
        new(FailureClass, "RestCallToUnreachableBackend_Returns502", "Passed"),
        new(FailureClass, "SoapCallToSlowBackend_FaultsAfterDeadline", "Passed"),
        new(ListenerClass, "AdminPathsOnPublicListeners_ReachCatchAll", "Passed"),
    ];

    private static string AllPassedTrx() => Trx(AllPassedResults());

    // The REST routing invocation fails; everything else passes.
    private static string OneFailedTrx() => FailedTrx(AssertionMessage, StackTrace("/src/facade/tests", AssertionLine));

    private static string FailedTrx(string message, string stackTrace)
    {
        var results = AllPassedResults();
        results[0] = results[0] with { Outcome = "Failed", Message = message, StackTrace = stackTrace };
        return Trx(results);
    }

    // The shape xunit.v3 4.0.2 writes with --report-xunit-trx: results carry the fully qualified display
    // name and a test id, and the definitions carry the class and method names.
    private static string Trx(params TrxResult[] results)
    {
        var resultElements = new List<XElement>(results.Length);
        var definitions = new List<XElement>(results.Length);
        for (var i = 0; i < results.Length; i++)
        {
            var test = results[i];
            var id = $"00000000-0000-0000-0000-{i + 1:D12}";
            var testName = $"{test.ClassName}.{test.MethodName}{test.Arguments}";

            var result = new XElement(
                TrxNamespace + "UnitTestResult",
                new XAttribute("testName", testName),
                new XAttribute("outcome", test.Outcome),
                new XAttribute("testId", id),
                new XAttribute("executionId", id),
                new XAttribute("testListId", "8c84fa94-04c1-424b-9868-57a2d4851a1d"));
            if (test.Message is not null || test.StackTrace is not null)
            {
                result.Add(new XElement(
                    TrxNamespace + "Output",
                    new XElement(
                        TrxNamespace + "ErrorInfo",
                        new XElement(TrxNamespace + "Message", test.Message ?? string.Empty),
                        new XElement(TrxNamespace + "StackTrace", test.StackTrace ?? string.Empty))));
            }

            resultElements.Add(result);
            definitions.Add(new XElement(
                TrxNamespace + "UnitTest",
                new XAttribute("name", testName),
                new XAttribute("id", id),
                new XElement(TrxNamespace + "Execution", new XAttribute("id", id)),
                new XElement(
                    TrxNamespace + "TestMethod",
                    new XAttribute("className", test.ClassName),
                    new XAttribute("name", test.MethodName),
                    new XAttribute("adapterTypeName", "executor://xunit.v3/4.0.2"))));
        }

        var run = new XElement(
            TrxNamespace + "TestRun",
            new XAttribute("id", "c28ea7a4-9a87-491f-b0ea-7e7fa6538bd3"),
            new XAttribute("name", "harness-tests"),
            new XElement(TrxNamespace + "Results", resultElements),
            new XElement(TrxNamespace + "TestDefinitions", definitions),
            new XElement(
                TrxNamespace + "ResultSummary",
                new XAttribute("outcome", results.Any(test => test.Outcome == "Failed") ? "Failed" : "Completed")));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), run).ToString();
    }

    private sealed record TrxResult(
        string ClassName,
        string MethodName,
        string Outcome,
        string? Arguments = null,
        string? Message = null,
        string? StackTrace = null);
}

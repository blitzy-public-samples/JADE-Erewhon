using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Tests;

/// <summary>
/// Failure identity, discrepancy signatures and known-limitations register matching: a failing item is
/// documented only when one entry lists its exact (<c>itemId</c>, <c>nature</c>) pair, so any change to
/// what failed is a new, undocumented failure, and shutdown, report and exception items are never
/// documented. The committed <c>harness/config/known-limitations.json</c> is only ever read here.
/// </summary>
public sealed partial class KnownLimitationsRegisterTests
{
    private const string CaseId = "REST-getAgent-01";
    private const string CaseItemId = "CASE:" + CaseId;

    private const string FailureBehaviourClass = "Erewhon.Facade.Tests.FailureBehaviourTests";
    private const string FailureBehaviourTest = "UnreachableBackend_Rest_Returns502";
    private const string FailureBehaviourItemId = "TEST:" + FailureBehaviourClass + "/" + FailureBehaviourTest;
    private const string AssertionException = "Xunit.Sdk.EqualException";
    private const string AssertionFile = "FailureBehaviourTests.cs";
    private const int AssertionLine = 42;
    private const string AssertionMessage = "Expected 502";

    private const string RoutingClass = "Erewhon.Facade.Tests.RoutingTests";
    private const string RestInvocation = "RerouteOneOperation(operationId: \"Rest.getCompany\")";
    private const string SoapInvocation = "RerouteOneOperation(operationId: \"Soap.getAgentNames\")";

    private const string RepoRootNotFound = "harness/config not found above the test output directory";

    private static readonly Discrepancy Website = new(
        "$.website",
        DiscrepancyKinds.Value,
        DiscrepancyValue.Literal("www.lee.co.jp"),
        DiscrepancyValue.Literal("www.lee.co.jp "));

    private static readonly Discrepancy Fax = new(
        "$.fax",
        DiscrepancyKinds.Missing,
        DiscrepancyValue.Literal("64 3 923 1010"),
        DiscrepancyValue.Absent);

    /// <summary>
    /// The three never-documentable kinds, with both shutdown components and the bare shutdown id the
    /// item type also refuses.
    /// </summary>
    public static TheoryData<string, string> NeverDocumentablePairs { get; } = new()
    {
        { FailureItemIds.Shutdown("facade"), FailureItemIds.StillRunning },
        { FailureItemIds.Shutdown("standin"), FailureItemIds.StillRunning },
        { FailureItemIds.ShutdownRoot, FailureItemIds.StillRunning },
        { FailureItemIds.Report, FailureItemIds.WriteFailed },
        { FailureItemIds.Exception, FailureItemIds.ExceptionNature(new InvalidOperationException()) },
    };

    /// <summary>The ids the results report always lists under "Known limitations".</summary>
    public static TheoryData<string> MandatoryIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in KnownLimitationIds.Mandatory)
        {
            data.Add(id);
        }

        return data;
    }

    // \z, unlike $, also rejects a trailing newline.
    [GeneratedRegex(@"\A[0-9a-f]{16}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SignatureForm();

    // ---- Matching -------------------------------------------------------------------------------

    [Fact]
    public void Match_RequiresItemIdAndNature()
    {
        Assert.Equal(CaseItemId, FailureItemIds.Case(CaseId));

        var nature = FailureItemIds.Diff(Sig(Website));
        var entry = Entry((CaseItemId, nature));
        var register = Register(entry);

        var exact = new FailureItem(CaseItemId, nature);
        Assert.True(register.IsDocumented(exact));
        Assert.Same(entry, register.FindEntry(exact));

        // Operation and Detail are report context only.
        var withContext = new FailureItem(CaseItemId, nature, "Rest.getAgent", "status 200, body differs");
        Assert.Same(entry, register.FindEntry(withContext));

        var otherNature = new FailureItem(CaseItemId, FailureItemIds.Diff(Sig(Fax)));
        Assert.False(register.IsDocumented(otherNature));
        Assert.Null(register.FindEntry(otherNature));

        var otherItem = new FailureItem(FailureItemIds.Case("REST-getAgent-02"), nature);
        Assert.False(register.IsDocumented(otherItem));
        Assert.Null(register.FindEntry(otherItem));

        Assert.False(register.IsDocumented(null));
        Assert.Null(register.FindEntry(null));
    }

    [Fact]
    public void Match_IsOrdinal()
    {
        var nature = FailureItemIds.Diff(Sig(Website));
        var register = Register(Entry((CaseItemId, nature)));

        Assert.False(register.IsDocumented(new FailureItem("case:rest-getagent-01", nature)));
        Assert.False(register.IsDocumented(new FailureItem(CaseItemId, nature.ToUpperInvariant())));

        // Exact also means untrimmed.
        Assert.False(register.IsDocumented(new FailureItem(CaseItemId + " ", nature)));
        Assert.False(register.IsDocumented(new FailureItem(CaseItemId, nature + " ")));
    }

    [Fact]
    public void ChangedValueAtSamePath_IsNewFailure()
    {
        var register = Register(Entry((CaseItemId, FailureItemIds.Diff(Sig(Website)))));
        var changed = Website with { Observed = DiscrepancyValue.Literal("www.lee.co.jp  ") };

        Assert.NotEqual(Sig(Website), Sig(changed));
        Assert.False(register.IsDocumented(new FailureItem(CaseItemId, FailureItemIds.Diff(Sig(changed)))));
    }

    [Fact]
    public void AdditionalMismatch_IsNewFailure()
    {
        var register = Register(Entry((CaseItemId, FailureItemIds.Diff(Sig(Website)))));

        Assert.False(register.IsDocumented(new FailureItem(CaseItemId, FailureItemIds.Diff(Sig(Website, Fax)))));
    }

    [Theory]
    [InlineData(AssertionException, AssertionFile, 43, AssertionMessage)]
    [InlineData(AssertionException, AssertionFile, AssertionLine, "Expected 504")]
    [InlineData("Xunit.Sdk.TrueException", AssertionFile, AssertionLine, AssertionMessage)]
    [InlineData(AssertionException, "RoutingTests.cs", AssertionLine, AssertionMessage)]
    public void DifferentFailingAssertion_IsNewFailure(string exceptionType, string file, int line, string message)
    {
        Assert.Equal(FailureBehaviourItemId, FailureItemIds.Test(FailureBehaviourClass, FailureBehaviourTest));

        var documented = FailedNature(AssertionException, AssertionFile, AssertionLine, AssertionMessage);
        Assert.StartsWith("failed:", documented, StringComparison.Ordinal);
        var register = Register(Entry((FailureBehaviourItemId, documented)));
        Assert.True(register.IsDocumented(new FailureItem(FailureBehaviourItemId, documented)));

        var other = FailedNature(exceptionType, file, line, message);
        Assert.False(register.IsDocumented(new FailureItem(FailureBehaviourItemId, other)));
    }

    [Fact]
    public void DifferentTheoryInvocation_IsNewFailure()
    {
        var documentedId = FailureItemIds.Test(RoutingClass, RestInvocation);
        Assert.Equal("TEST:" + RoutingClass + "/" + RestInvocation, documentedId);

        var nature = FailedNature(AssertionException, "RoutingTests.cs", 57, "The rerouted operation reached the backend.");
        var register = Register(Entry((documentedId, nature)));
        Assert.True(register.IsDocumented(new FailureItem(documentedId, nature)));

        var otherInvocation = FailureItemIds.Test(RoutingClass, SoapInvocation);
        Assert.False(register.IsDocumented(new FailureItem(otherInvocation, nature)));
    }

    [Theory]
    [MemberData(nameof(NeverDocumentablePairs))]
    public void NeverDocumentableItems_AreNeverDocumented(string itemId, string nature)
    {
        // The control pair proves the register documents what it lists, so a false below is not vacuous.
        var control = new FailureItem(FailureItemIds.Ordering, FailureItemIds.NotRun);
        var entry = Entry((itemId, nature), (control.ItemId, control.Nature));
        var register = Register(entry);
        var item = new FailureItem(itemId, nature);

        Assert.True(item.IsNeverDocumentable);
        Assert.Contains(new RegisterMatch(itemId, nature), register.Entries.Single().Matches);
        Assert.False(register.IsDocumented(item));
        Assert.Null(register.FindEntry(item));
        Assert.Same(entry, register.FindEntry(control));
    }

    [Fact]
    public void ExceptionNature_IsTheFullTypeName()
    {
        Assert.Equal("System.InvalidOperationException", FailureItemIds.ExceptionNature(new InvalidOperationException()));
    }

    [Fact]
    public void EmptyRegister_DocumentsNothing()
    {
        var register = KnownLimitationsRegister.Empty;

        Assert.Empty(register.Entries);
        Assert.False(register.IsDocumented(new FailureItem(CaseItemId, FailureItemIds.Diff(Sig(Website)))));
        Assert.False(register.IsDocumented(new FailureItem(FailureItemIds.Ordering, FailureItemIds.NotRun)));
        Assert.False(register.IsDocumented(null));
        Assert.Null(register.FindById(KnownLimitationIds.RestPaths));
    }

    [Fact]
    public void Parse_RoundTripsShape()
    {
        const string json = """
            {"version":1,"entries":[{"id":"KL-X","category":"check","operation":null,"matches":[{"itemId":"CHK-ORDERING","nature":"not-run"}],"reason":"r","attempts":0,"actionTaken":"a"}]}
            """;

        var register = KnownLimitationsRegister.Parse(json);

        var entry = Assert.Single(register.Entries);
        Assert.Same(entry, register.FindById("KL-X"));
        Assert.Equal("KL-X", entry.Id);
        Assert.Equal(RegisterCategories.Check, entry.Category);
        Assert.Null(entry.Operation);
        Assert.Equal(new RegisterMatch("CHK-ORDERING", "not-run"), Assert.Single(entry.Matches));
        Assert.Equal("r", entry.Reason);
        Assert.Equal(0, entry.Attempts);
        Assert.Equal("a", entry.ActionTaken);

        var item = new FailureItem("CHK-ORDERING", "not-run");
        Assert.True(register.IsDocumented(item));
        Assert.Same(entry, register.FindEntry(item));
    }

    [Theory]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"bogus","matches":[]}]}""")]
    [InlineData("""{"version":1,"entries":{"id":"KL-X","category":"check"}}""")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check"},{"id":"KL-X","category":"check"}]}""")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","matches":[{"itemId":"CHK-ORDERING","nature":" "}]}]}""")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","matches":[],"matches":[]}]}""")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","attempts":-1}]}""")]
    [InlineData("""{"version":2,"entries":[]}""")]
    [InlineData("""{"version":1,"entries":[""")]
    public void Parse_InvalidContent_Throws(string json)
    {
        Assert.Throws<InvalidDataException>(() => KnownLimitationsRegister.Parse(json));
    }

    // ---- Signatures -----------------------------------------------------------------------------

    [Fact]
    public void Signature_IsDeterministic()
    {
        var websiteCopy = new Discrepancy(Website.Path, Website.Kind, Website.Baseline, Website.Observed);
        var faxCopy = new Discrepancy(Fax.Path, Fax.Kind, Fax.Baseline, Fax.Observed);

        Assert.Equal(Sig(Website, Fax), Sig(Website, Fax));
        Assert.Equal(Sig(Website, Fax), Sig(websiteCopy, faxCopy));
        Assert.Equal(
            DiscrepancySignature.CanonicalJson([Website, Fax]),
            DiscrepancySignature.CanonicalJson([websiteCopy, faxCopy]));
    }

    [Fact]
    public void Signature_IsOrderIndependent()
    {
        Assert.Equal(Sig(Website, Fax), Sig(Fax, Website));
    }

    [Fact]
    public void Signature_Is16LowercaseHex()
    {
        Assert.Matches(SignatureForm(), Sig());
        Assert.Matches(SignatureForm(), Sig(Website));
        Assert.Matches(SignatureForm(), Sig(Website, Fax));
        Assert.Matches(SignatureForm(), DiscrepancySignature.Hash16(AssertionMessage));
    }

    [Theory]
    [InlineData("{generated-guid}", "N1")]
    [InlineData("{timestamp}", "N2")]
    public void Signature_DistinguishesLiteralFromNormalised(string lookalike, string rule)
    {
        var literal = DiscrepancyValue.Literal(lookalike);
        var valid = DiscrepancyValue.Valid(rule);

        var literalToken = literal.ToToken().ToJsonString();
        var validToken = valid.ToToken().ToJsonString();
        Assert.Equal("{\"literal\":\"" + lookalike + "\"}", literalToken);
        Assert.Equal("{\"valid\":\"" + rule + "\"}", validToken);
        Assert.NotEqual(literalToken, validToken);

        // A baseline value missing from the observed response: once valid, once the literal rule name.
        var fromLiteral = new Discrepancy("$.guid", DiscrepancyKinds.Missing, literal, DiscrepancyValue.Absent);
        var fromValid = new Discrepancy("$.guid", DiscrepancyKinds.Missing, valid, DiscrepancyValue.Absent);

        Assert.Contains("\"baseline\":" + literalToken, DiscrepancySignature.CanonicalJson([Website, fromLiteral]), StringComparison.Ordinal);
        Assert.Contains("\"baseline\":" + validToken, DiscrepancySignature.CanonicalJson([Website, fromValid]), StringComparison.Ordinal);
        Assert.NotEqual(Sig(Website, fromLiteral), Sig(Website, fromValid));
    }

    [Fact]
    public void DiffNature_UsesSignature()
    {
        var sig = Sig(Website);

        Assert.Equal("d:" + sig, FailureItemIds.Diff(sig));
    }

    // ---- The committed register -----------------------------------------------------------------

    [Fact]
    public void CommittedRegister_Loads()
    {
        var register = LoadCommittedRegister();

        Assert.Subset(
            register.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal),
            KnownLimitationIds.Mandatory.ToHashSet(StringComparer.Ordinal));

        // Every documentable pair the file lists is found by the loaded lookups.
        foreach (var entry in register.Entries)
        {
            foreach (var match in entry.Matches)
            {
                var item = new FailureItem(match.ItemId, match.Nature);
                if (!item.IsNeverDocumentable)
                {
                    Assert.True(register.IsDocumented(item), $"Entry {entry.Id} lists {item}, which the register does not document.");
                }
            }
        }
    }

    [Fact]
    public void CommittedRegister_IdsAreUnique()
    {
        var duplicates = LoadCommittedRegister().Entries
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        Assert.Empty(duplicates);
    }

    [Fact]
    public void CommittedRegister_CategoriesAreKnown()
    {
        Assert.All(
            LoadCommittedRegister().Entries,
            entry => Assert.True(RegisterCategories.IsKnown(entry.Category), $"Entry {entry.Id} has an unknown category."));
    }

    [Theory]
    [MemberData(nameof(MandatoryIds))]
    public void CommittedRegister_HasMandatoryEntry(string id)
    {
        Assert.NotNull(LoadCommittedRegister().FindById(id));
    }

    [Fact]
    public void MandatoryIds_AreExactlyTheFiveReportedFirst()
    {
        Assert.Equal(
            new[] { "KL-BASELINE-STANDIN", "KL-REST-PATHS", "KL-SOAP-ACTIONS", "KL-SOAP-WSDL", "KL-JADE-TESTS-NOT-RUN" },
            KnownLimitationIds.Mandatory);
    }

    [Fact]
    public void CommittedRegister_MatchesAreWellFormed()
    {
        foreach (var entry in LoadCommittedRegister().Entries)
        {
            if (string.Equals(entry.Category, RegisterCategories.Assumption, StringComparison.Ordinal))
            {
                Assert.True(entry.Matches.Count == 0, $"Assumption entry {entry.Id} lists failing items.");
            }

            foreach (var match in entry.Matches)
            {
                Assert.False(string.IsNullOrWhiteSpace(match.ItemId), $"Entry {entry.Id} lists a blank item id.");
                Assert.False(string.IsNullOrWhiteSpace(match.Nature), $"Entry {entry.Id} lists a blank nature.");
                Assert.False(
                    new FailureItem(match.ItemId, match.Nature).IsNeverDocumentable,
                    $"Entry {entry.Id} lists {match.ItemId}, which can never be documented.");
            }
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static RegisterEntry Entry(params (string itemId, string nature)[] pairs) =>
        new(
            "KL-TEST-DIFFERENCE",
            RegisterCategories.Difference,
            Operation: null,
            pairs.Select(pair => new RegisterMatch(pair.itemId, pair.nature)).ToArray(),
            Reason: "The difference persisted after three facade attempts.",
            Attempts: 3,
            ActionTaken: "Registered as a documented difference.");

    private static KnownLimitationsRegister Register(params RegisterEntry[] entries) => new(entries);

    private static string Sig(params Discrepancy[] ds) => DiscrepancySignature.Compute(ds);

    // Matching depends only on the resulting nature, so any unambiguous encoding of the four parts the
    // failed-test signature covers serves here.
    private static string FailedNature(string exceptionType, string file, int line, string message) =>
        FailureItemIds.Failed(DiscrepancySignature.Hash16(JsonSerializer.Serialize(
            new[] { exceptionType, file, line.ToString(CultureInfo.InvariantCulture), message })));

    private static KnownLimitationsRegister LoadCommittedRegister() =>
        KnownLimitationsRegister.Load(Path.Combine(FindRepoRoot(), "harness", "config", "known-limitations.json"));

    // The message is fixed: a path would differ between checkouts and runs.
    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "harness", "config")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(RepoRootNotFound);
    }
}

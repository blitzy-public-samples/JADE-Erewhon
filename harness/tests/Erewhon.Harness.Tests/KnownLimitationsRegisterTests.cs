using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Cases;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Tests;

/// <summary>
/// Failure identity, discrepancy signatures and known-limitations register matching: a failing item is
/// documented only when one entry lists its exact (<c>itemId</c>, <c>nature</c>) pair, so any change to
/// what failed is a new, undocumented failure, and shutdown, report and exception items are never
/// documented. It also covers the case model and the strict loader of the committed case matrix: the
/// matrix loads as the 56 planned cases in the fixed replay order, every case agrees with
/// <see cref="OperationCatalogue.Default"/>, the id and request helpers never decode or reinterpret what
/// the matrix states, and a malformed matrix, built in memory as a mutated copy of the committed one, is
/// rejected with a message that starts <c>case-matrix: </c> and names the offending case by id, or by
/// JSON path where the text does not parse. The committed <c>harness/config/known-limitations.json</c>
/// and <c>harness/config/case-matrix.json</c> are only ever read here.
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

    // Every loader message starts with this, after the path when the register was read from a file.
    private const string RegisterErrorPrefix = "Invalid known-limitations register: ";

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
        var stored = Assert.Single(register.Entries);
        AssertCopyOf(entry, stored);

        var exact = new FailureItem(CaseItemId, nature);
        Assert.True(register.IsDocumented(exact));
        Assert.Same(stored, register.FindEntry(exact));

        // Operation and Detail are report context only.
        var withContext = new FailureItem(CaseItemId, nature, "Rest.getAgent", "status 200, body differs");
        Assert.Same(stored, register.FindEntry(withContext));

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

    [Theory]
    [InlineData("array-replace")]
    [InlineData("list-replace")]
    [InlineData("list-add")]
    [InlineData("list-clear")]
    [InlineData("read-only-wrapper-replace")]
    public void CallerMutationAfterConstruction_ChangesNeitherEntriesNorLookups(string mutation)
    {
        var original = new RegisterMatch(CaseItemId, FailureItemIds.Diff(Sig(Website)));
        var replacement = new RegisterMatch(CaseItemId, FailureItemIds.Diff(Sig(Fax)));
        var array = new[] { original };
        var list = new List<RegisterMatch> { original };
        IReadOnlyList<RegisterMatch> given = mutation switch
        {
            "array-replace" => array,
            "list-replace" or "list-add" or "list-clear" => list,
            "read-only-wrapper-replace" => list.AsReadOnly(),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
        };
        var entry = Entry() with { Matches = given };
        var register = Register(entry);
        var stored = Assert.Single(register.Entries);
        AssertCopyOf(entry, stored);

        switch (mutation)
        {
            case "array-replace":
                array[0] = replacement;
                break;
            case "list-replace" or "read-only-wrapper-replace":
                list[0] = replacement;
                break;
            case "list-add":
                list.Add(replacement);
                break;
            case "list-clear":
                list.Clear();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        // The caller's entry now shows other pairs, so the assertions below are not vacuous.
        Assert.NotEqual<RegisterMatch>([original], entry.Matches);

        Assert.Same(stored, Assert.Single(register.Entries));
        Assert.Equal<RegisterMatch>([original], stored.Matches);
        Assert.Same(stored, register.FindById(entry.Id));
        Assert.Same(stored, register.FindEntry(new FailureItem(original.ItemId, original.Nature)));
        Assert.False(register.IsDocumented(new FailureItem(replacement.ItemId, replacement.Nature)));

        // Nor can the stored snapshot be changed through a cast.
        var view = Assert.IsAssignableFrom<IList<RegisterMatch>>(stored.Matches);
        Assert.True(view.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => view[0] = replacement);
        Assert.Throws<NotSupportedException>(() => view.Add(replacement));
        Assert.Throws<NotSupportedException>(view.Clear);
        Assert.Equal<RegisterMatch>([original], stored.Matches);
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
        var stored = Assert.Single(register.Entries);
        AssertCopyOf(entry, stored);
        var item = new FailureItem(itemId, nature);

        Assert.True(item.IsNeverDocumentable);
        Assert.Contains(new RegisterMatch(itemId, nature), stored.Matches);
        Assert.False(register.IsDocumented(item));
        Assert.Null(register.FindEntry(item));
        Assert.Same(stored, register.FindEntry(control));
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

    // The JSON is valid, but each \u escape of a lone surrogate has no UTF-16 string value.
    [Theory]
    [InlineData("""{"version":1,"entries":[{"id":"KL-\uD800","category":"check"}]}""", "entries[0]: 'id' is not valid UTF-16 text: ")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check\uD800"}]}""", "entries[0] (KL-X): 'category' is not valid UTF-16 text: ")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","operation":"\uDC00"}]}""", "entries[0] (KL-X): 'operation' is not valid UTF-16 text: ")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","reason":"a\uDC00b"}]}""", "entries[0] (KL-X): 'reason' is not valid UTF-16 text: ")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","reason":"\uDC00\uD800"}]}""", "entries[0] (KL-X): 'reason' is not valid UTF-16 text: ")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","actionTaken":"\uDC00"}]}""", "entries[0] (KL-X): 'actionTaken' is not valid UTF-16 text: ")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-A","category":"check"},{"id":"KL-X","category":"check","matches":[{"itemId":"CHK-ORDERING","nature":"not-run"},{"itemId":"CHK-\uD800","nature":"not-run"}]}]}""", "entries[1] (KL-X): 'matches[1].itemId' is not valid UTF-16 text: ")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","matches":[{"itemId":"CHK-ORDERING","nature":"d:\uDC00"}]}]}""", "entries[0] (KL-X): 'matches[0].nature' is not valid UTF-16 text: ")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","matches":[{"itemId":"CHK-ORDERING","nature":"d:\uD800\u0041"}]}]}""", "entries[0] (KL-X): 'matches[0].nature' is not valid UTF-16 text: ")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","note\uD800":"x"}]}""", "a member name is not valid UTF-16 text: ")]
    public void Parse_LoneSurrogateEscape_ThrowsNamingTheMember(string json, string expected)
    {
        var exception = Assert.Throws<InvalidDataException>(() => KnownLimitationsRegister.Parse(json));

        Assert.StartsWith(RegisterErrorPrefix + expected, exception.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    // A string, unlike a file, can hold an unpaired surrogate character that has no UTF-8 encoding.
    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void Parse_UnpairedSurrogateCharacter_Throws(int codeUnit)
    {
        var json = """{"version":1,"entries":[{"id":"KL-X","category":"check","reason":"a""" + (char)codeUnit + """b"}]}""";

        var exception = Assert.Throws<InvalidDataException>(() => KnownLimitationsRegister.Parse(json));

        Assert.StartsWith(RegisterErrorPrefix + "not valid UTF-16 text: ", exception.Message, StringComparison.Ordinal);
        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    // Lenient decoding would load each of these as U+FFFD text, and two different ids or natures as one.
    [Theory]
    [InlineData("FFFE", false)]
    [InlineData("FFFE", true)]
    [InlineData("C0AF", false)]
    [InlineData("EDA080", false)]
    [InlineData("E282", false)]
    public void Load_InvalidUtf8_ThrowsNamingThePath(string invalidHex, bool byteOrderMark)
    {
        var head = Encoding.UTF8.GetBytes("""{"version":1,"entries":[{"id":"KL-""");
        var tail = Encoding.UTF8.GetBytes("\",\"category\":\"check\"}]}");
        byte[] mark = byteOrderMark ? [0xEF, 0xBB, 0xBF] : [];
        byte[] content = [.. mark, .. head, .. Convert.FromHexString(invalidHex), .. tail];

        WithRegisterFile(content, path =>
        {
            var exception = Assert.Throws<InvalidDataException>(() => KnownLimitationsRegister.Load(path));

            Assert.StartsWith(path + ": " + RegisterErrorPrefix + "not valid UTF-8: ", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\uFFFD", exception.Message, StringComparison.Ordinal);

            // The reported index is the offset of the first invalid byte in the file, mark included.
            var decoding = Assert.IsType<DecoderFallbackException>(exception.InnerException);
            Assert.Equal(mark.Length + head.Length, decoding.Index);
        });
    }

    [Fact]
    public void Load_LoneSurrogateEscape_ThrowsNamingThePathAndMember()
    {
        const string json = """{"version":1,"entries":[{"id":"KL-X","category":"check","matches":[{"itemId":"CHK-ORDERING","nature":"d:\uDC00"}]}]}""";

        WithRegisterFile(Encoding.UTF8.GetBytes(json), path =>
        {
            var exception = Assert.Throws<InvalidDataException>(() => KnownLimitationsRegister.Load(path));

            Assert.StartsWith(
                path + ": " + RegisterErrorPrefix + "entries[0] (KL-X): 'matches[0].nature' is not valid UTF-16 text: ",
                exception.Message,
                StringComparison.Ordinal);
            var parsing = Assert.IsType<InvalidDataException>(exception.InnerException);
            Assert.IsType<InvalidOperationException>(parsing.InnerException);
        });
    }

    // Strict decoding still accepts a leading byte order mark and every valid multi-byte sequence.
    [Fact]
    public void Load_ByteOrderMarkAndMultiByteText_LoadExactly()
    {
        const string reason = "caf\u00E9";
        const string actionTaken = "\U0001F600";
        const string nature = "d:\U0001F600";
        var json = $$"""{"version":1,"entries":[{"id":"KL-X","category":"check","matches":[{"itemId":"CHK-ORDERING","nature":"d:\uD83D\uDE00"}],"reason":"{{reason}}","actionTaken":"{{actionTaken}}"}]}""";
        byte[] content = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)];

        WithRegisterFile(content, path =>
        {
            var register = KnownLimitationsRegister.Load(path);

            var entry = Assert.Single(register.Entries);
            Assert.Equal("KL-X", entry.Id);
            Assert.Equal(new RegisterMatch("CHK-ORDERING", nature), Assert.Single(entry.Matches));
            Assert.Equal(reason, entry.Reason);
            Assert.Equal(actionTaken, entry.ActionTaken);
            Assert.True(register.IsDocumented(new FailureItem("CHK-ORDERING", nature)));
        });
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
    public void CanonicalOrder_OrdersTokenTiesAsTheSignatureDoes()
    {
        // Every entry ties on path and kind, so only the token texts, written with the signature's
        // relaxed encoder, order them: an escaped quote sorts after '#', an escaped control character
        // after a space, and an unescaped U+00E9 after 'z'. Comparing the raw texts would reverse the
        // first two pairs, and the default encoder, which escapes U+00E9, the third.
        static Discrepancy Tie(DiscrepancyValue baseline, DiscrepancyValue observed) =>
            new("$.name", DiscrepancyKinds.Value, baseline, observed);

        static Discrepancy[] InCanonicalOrder() =>
        [
            Tie(DiscrepancyValue.Literal("a#b"), DiscrepancyValue.Absent),
            Tie(DiscrepancyValue.Literal("a\"b"), DiscrepancyValue.Absent),
            Tie(DiscrepancyValue.Literal("z"), DiscrepancyValue.Absent),
            Tie(DiscrepancyValue.Literal("\u00E9"), DiscrepancyValue.Absent),
            Tie(DiscrepancyValue.Absent, DiscrepancyValue.Literal(" ")),
            Tie(DiscrepancyValue.Absent, DiscrepancyValue.Literal("\u0001")),
            Tie(DiscrepancyValue.Valid("N1"), DiscrepancyValue.Absent),
            Tie(DiscrepancyValue.Valid("N2"), DiscrepancyValue.Absent),
        ];

        // Every permutation holds the same instances, so later sorts reuse the keys the first computed.
        // The expected entries are never sorted, so their keys are never computed.
        var entries = InCanonicalOrder();
        var expected = InCanonicalOrder();
        var permutations = 0;
        foreach (var permutation in Permutations(entries))
        {
            Array.Sort(permutation, Discrepancy.CanonicalOrder);
            Assert.Equal(expected, permutation);
            permutations++;
        }

        Assert.Equal(40320, permutations);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(DiscrepancySignature.CanonicalJson(entries)),
            new JsonArray(expected.Select(entry => (JsonNode?)entry.ToJsonNode()).ToArray())));

        // Equal entries collapse whether or not their keys were computed, and the eight stay distinct.
        Assert.Equal(entries.Length, entries.Concat(expected).ToHashSet().Count);
    }

    [Fact]
    public void DiffNature_UsesSignature()
    {
        var sig = Sig(Website);

        Assert.Equal("d:" + sig, FailureItemIds.Diff(sig));
    }

    // Every expected text and signature below was computed outside .NET: Python json.dumps with
    // ensure_ascii=False and compact separators over the entries sorted by path, kind, baseline token
    // text and observed token text, then hashlib.sha256 of the text's UTF-8 bytes, first 16 hex digits.
    // They therefore check the canonical form and the hash rather than repeat the product's own code.
    private const string EmptyJson = "[]";
    private const string EmptySignature = "4f53cda18c2baa0c";

    private const string WebsiteFaxJson =
        """[{"path":"$.fax","kind":"missing","baseline":{"literal":"64 3 923 1010"},"observed":{"literal":null}},{"path":"$.website","kind":"value","baseline":{"literal":"www.lee.co.jp"},"observed":{"literal":"www.lee.co.jp "}}]""";

    private const string WebsiteFaxSignature = "9e3e6f848f99046b";

    // Ordinal order: U+0049 'I', U+0069 'i', then 'x', U+0130 dotted capital I, U+0131 dotless small i.
    private const string CultureJson =
        """[{"path":"$.I","kind":"value","baseline":{"literal":"1"},"observed":{"literal":"2"}},{"path":"$.i","kind":"value","baseline":{"literal":"1"},"observed":{"literal":"2"}},{"path":"$.x","kind":"value","baseline":{"literal":"I"},"observed":{"literal":"2"}},{"path":"$.x","kind":"value","baseline":{"literal":"i"},"observed":{"literal":"2"}},{"path":"$.İ","kind":"value","baseline":{"literal":"1"},"observed":{"literal":"2"}},{"path":"$.ı","kind":"value","baseline":{"literal":"1"},"observed":{"literal":"2"}}]""";

    private const string CultureSignature = "a2be1c7e6ebe0178";

    // The serialisation the canonical text uses, so an artifact's list can be compared with it as text.
    private static readonly JsonSerializerOptions CanonicalText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Each vector is checked in every input order, so the tie-breaker rows also prove that entries
    // sharing path and kind sort by baseline token text, then observed token text, whatever the input.
    [Theory]
    [InlineData("null", EmptyJson, EmptySignature)]
    [InlineData("empty", EmptyJson, EmptySignature)]
    [InlineData("website-fax", WebsiteFaxJson, WebsiteFaxSignature)]
    [InlineData(
        "valid",
        """[{"path":"$.created","kind":"missing","baseline":{"valid":"N2"},"observed":{"literal":null}},{"path":"$.guid","kind":"value","baseline":{"valid":"N1"},"observed":{"literal":"{generated-guid}"}},{"path":"$.updated","kind":"type","baseline":{"literal":"2026-01-01"},"observed":{"valid":"N2"}}]""",
        "84521f6ba4c414a4")]
    [InlineData(
        "escaping",
        """[{"path":"$.note","kind":"value","baseline":{"literal":"say \"hi\" \\ back"},"observed":{"literal":"line\nbreak\u0001 ü 日本 <>&'"}},{"path":"$.名前","kind":"extra","baseline":{"literal":null},"observed":{"literal":"\t"}}]""",
        "94a6361dbb55f3e6")]
    [InlineData(
        "tie-baseline",
        """[{"path":"$.x","kind":"value","baseline":{"literal":"a"},"observed":{"literal":"o"}},{"path":"$.x","kind":"value","baseline":{"literal":"b"},"observed":{"literal":"o"}},{"path":"$.x","kind":"value","baseline":{"literal":null},"observed":{"literal":"o"}},{"path":"$.x","kind":"value","baseline":{"valid":"N1"},"observed":{"literal":"o"}},{"path":"$.x","kind":"value","baseline":{"valid":"N2"},"observed":{"literal":"o"}}]""",
        "a36ffd5c5acc7be0")]
    [InlineData(
        "tie-observed",
        """[{"path":"$.x","kind":"value","baseline":{"literal":"x"},"observed":{"literal":""}},{"path":"$.x","kind":"value","baseline":{"literal":"x"},"observed":{"literal":"z"}},{"path":"$.x","kind":"value","baseline":{"literal":"x"},"observed":{"literal":null}},{"path":"$.x","kind":"value","baseline":{"literal":"x"},"observed":{"valid":"N1"}}]""",
        "122187aaf4576af9")]
    [InlineData(
        "tie-both",
        """[{"path":"$.x","kind":"value","baseline":{"literal":"a"},"observed":{"literal":"z"}},{"path":"$.x","kind":"value","baseline":{"literal":"a"},"observed":{"literal":null}},{"path":"$.x","kind":"value","baseline":{"literal":null},"observed":{"literal":"a"}},{"path":"$.x","kind":"value","baseline":{"valid":"N1"},"observed":{"literal":"a"}}]""",
        "2048acd6f7de69a2")]
    public void Signature_MatchesKnownVector(string vector, string expectedJson, string expectedSignature)
    {
        var input = VectorInput(vector);
        var count = input?.Length ?? 0;
        var item = new FailureItem(CaseItemId, FailureItemIds.Diff(expectedSignature));
        Assert.Equal(expectedSignature, DiscrepancySignature.Hash16(expectedJson));

        IEnumerable<Discrepancy[]?> orders = input is null ? [null] : [.. Permutations(input)];
        foreach (var order in orders)
        {
            Assert.Equal(expectedJson, DiscrepancySignature.CanonicalJson(order));
            Assert.Equal(expectedSignature, DiscrepancySignature.Compute(order));

            var prepared = DiscrepancySignature.Prepare(order);
            Assert.Equal(expectedJson, prepared.Json);
            Assert.Equal(expectedSignature, prepared.Signature);
            Assert.Equal(count, prepared.Entries.Count);

            // The artifact carries the hashed list in full and in canonical order, whichever method built it.
            var fromPrepared = prepared.ToArtifact(item);
            var fromList = DiscrepancySignature.ToArtifact(item, order);
            Assert.Equal(expectedSignature, (string?)fromPrepared["signature"]);
            var list = fromPrepared["discrepancies"]!.AsArray();
            Assert.Equal(count, list.Count);
            Assert.Equal(expectedJson, list.ToJsonString(CanonicalText));
            Assert.Equal(fromPrepared.ToJsonString(CanonicalText), fromList.ToJsonString(CanonicalText));
        }
    }

    [Theory]
    [InlineData(
        "$.a",
        DiscrepancyKinds.Value,
        """[{"path":"$.a","kind":"value","baseline":{"literal":"www.lee.co.jp"},"observed":{"literal":"www.lee.co.jp "}},{"path":"$.fax","kind":"missing","baseline":{"literal":"64 3 923 1010"},"observed":{"literal":null}}]""",
        "8d94099859586589")]
    [InlineData(
        "$.websites",
        DiscrepancyKinds.Value,
        """[{"path":"$.fax","kind":"missing","baseline":{"literal":"64 3 923 1010"},"observed":{"literal":null}},{"path":"$.websites","kind":"value","baseline":{"literal":"www.lee.co.jp"},"observed":{"literal":"www.lee.co.jp "}}]""",
        "6bc843e55a22c568")]
    [InlineData(
        "$.website",
        DiscrepancyKinds.Type,
        """[{"path":"$.fax","kind":"missing","baseline":{"literal":"64 3 923 1010"},"observed":{"literal":null}},{"path":"$.website","kind":"type","baseline":{"literal":"www.lee.co.jp"},"observed":{"literal":"www.lee.co.jp "}}]""",
        "06604ea8d1c9d13d")]
    public void Signature_ChangesWithPathOrKindAlone(string path, string kind, string expectedJson, string expectedSignature)
    {
        // Website with only its path or only its kind changed; both values stay as they were.
        Assert.True(path != Website.Path ^ kind != Website.Kind);
        var changed = new Discrepancy(path, kind, Website.Baseline, Website.Observed);

        Assert.Equal(expectedJson, DiscrepancySignature.CanonicalJson([changed, Fax]));
        Assert.Equal(expectedSignature, Sig(changed, Fax));
        Assert.NotEqual(WebsiteFaxJson, expectedJson);
        Assert.NotEqual(WebsiteFaxSignature, expectedSignature);
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("en-US")]
    [InlineData("")]
    public void Signature_IsCultureInvariant(string culture)
    {
        var input = VectorInput("culture")!;
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;

            // The culture really orders these paths otherwise, so an ordering that consulted it would show.
            var paths = input.Select(discrepancy => discrepancy.Path).Distinct().ToArray();
            Assert.NotEqual(paths.Order(StringComparer.Ordinal), paths.Order(StringComparer.CurrentCulture));

            foreach (var order in Permutations(input))
            {
                Assert.Equal(CultureJson, DiscrepancySignature.CanonicalJson(order));
                Assert.Equal(CultureSignature, DiscrepancySignature.Compute(order));
                var prepared = DiscrepancySignature.Prepare(order);
                Assert.Equal(CultureJson, prepared.Json);
                Assert.Equal(CultureSignature, prepared.Signature);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    // The whole artifact as text: member set and order, the item echoed (absent context as JSON null),
    // the signature and the full canonical list.
    [Theory]
    [InlineData(
        null,
        null,
        """{"itemId":"CASE:REST-getAgent-01","nature":"d:9e3e6f848f99046b","operation":null,"detail":null,"signature":"9e3e6f848f99046b","discrepancies":"""
            + WebsiteFaxJson + "}")]
    [InlineData(
        "Rest.getAgent",
        "status 200, body differs",
        """{"itemId":"CASE:REST-getAgent-01","nature":"d:9e3e6f848f99046b","operation":"Rest.getAgent","detail":"status 200, body differs","signature":"9e3e6f848f99046b","discrepancies":"""
            + WebsiteFaxJson + "}")]
    public void ToArtifact_EchoesItemAndListsEveryDiscrepancy(string? operation, string? detail, string expectedArtifact)
    {
        var item = new FailureItem(CaseItemId, FailureItemIds.Diff(Sig(Website, Fax)), operation, detail);

        Assert.Equal(expectedArtifact, DiscrepancySignature.ToArtifact(item, [Website, Fax]).ToJsonString(CanonicalText));
        Assert.Equal(
            expectedArtifact,
            DiscrepancySignature.Prepare([Fax, Website]).ToArtifact(item).ToJsonString(CanonicalText));
    }

    // The bare null and default literals must stay at the call sites: they only compile while the list
    // overload is the single two-argument DiscrepancySignature.ToArtifact, so another overload taking a
    // reference type would make them ambiguous and fail the build here.
    [Fact]
    public void ToArtifact_NullList_IsTheEmptyList()
    {
        var item = new FailureItem(CaseItemId, FailureItemIds.Diff(EmptySignature));

        var fromNull = DiscrepancySignature.ToArtifact(item, null);
        var fromDefault = DiscrepancySignature.ToArtifact(item, default);

        Assert.Empty(fromNull["discrepancies"]!.AsArray());
        Assert.Equal(EmptySignature, (string?)fromNull["signature"]);
        var fromEmpty = DiscrepancySignature.ToArtifact(item, []).ToJsonString(CanonicalText);
        Assert.Equal(fromEmpty, fromNull.ToJsonString(CanonicalText));
        Assert.Equal(fromEmpty, fromDefault.ToJsonString(CanonicalText));
    }

    [Fact]
    public void Prepare_KeepsItsOwnReadOnlyCopy()
    {
        var source = new List<Discrepancy> { Website, Fax };
        var prepared = DiscrepancySignature.Prepare(source);
        source.Clear();
        source.Add(Website);

        Assert.Equal<Discrepancy>([Fax, Website], prepared.Entries);
        Assert.Equal(WebsiteFaxJson, prepared.Json);
        Assert.Equal(WebsiteFaxSignature, prepared.Signature);

        var view = Assert.IsAssignableFrom<IList<Discrepancy>>(prepared.Entries);
        Assert.True(view.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => view[0] = Website);
        Assert.Throws<NotSupportedException>(() => view.Add(Website));
        Assert.Equal<Discrepancy>([Fax, Website], prepared.Entries);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("compute")]
    [InlineData("canonical-json")]
    [InlineData("to-artifact")]
    public void Signature_NullEntry_Throws(string api)
    {
        Discrepancy[] withNull = [Website, null!, Fax];
        var item = new FailureItem(CaseItemId, FailureItemIds.Diff(WebsiteFaxSignature));

        var ex = Assert.Throws<ArgumentException>(() => api switch
        {
            "prepare" => DiscrepancySignature.Prepare(withNull),
            "compute" => DiscrepancySignature.Compute(withNull),
            "canonical-json" => DiscrepancySignature.CanonicalJson(withNull),
            "to-artifact" => (object)DiscrepancySignature.ToArtifact(item, withNull),
            _ => throw new ArgumentOutOfRangeException(nameof(api), api, null),
        });
        Assert.Equal("discrepancies", ex.ParamName);
    }

    [Theory]
    [InlineData("list-overload", "item")]
    [InlineData("prepared-instance", "item")]
    public void ToArtifact_NullArgument_Throws(string call, string paramName)
    {
        var prepared = DiscrepancySignature.Prepare([Website, Fax]);

        var ex = Assert.Throws<ArgumentNullException>(() => call switch
        {
            "list-overload" => DiscrepancySignature.ToArtifact(null!, [Website, Fax]),
            "prepared-instance" => prepared.ToArtifact(null!),
            _ => throw new ArgumentOutOfRangeException(nameof(call), call, null),
        });
        Assert.Equal(paramName, ex.ParamName);
    }

    // The inputs of the vectors above, each in an order other than the canonical one.
    private static Discrepancy[]? VectorInput(string vector) => vector switch
    {
        "null" => null,
        "empty" => [],
        "website-fax" => [Website, Fax],
        "valid" =>
        [
            new("$.updated", DiscrepancyKinds.Type, DiscrepancyValue.Literal("2026-01-01"), DiscrepancyValue.Valid("N2")),
            new("$.guid", DiscrepancyKinds.Value, DiscrepancyValue.Valid("N1"), DiscrepancyValue.Literal("{generated-guid}")),
            new("$.created", DiscrepancyKinds.Missing, DiscrepancyValue.Valid("N2"), DiscrepancyValue.Absent),
        ],

        // A quote, a backslash, a tab, a newline, U+0001, non-ASCII text and the HTML-sensitive characters,
        // which the relaxed encoder leaves raw.
        "escaping" =>
        [
            new("$.\u540D\u524D", DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal("\t")),
            new(
                "$.note",
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal("say \"hi\" \\ back"),
                DiscrepancyValue.Literal("line\nbreak\u0001 \u00FC \u65E5\u672C <>&'")),
        ],

        // Paths and baseline texts that culture-aware comparison orders differently from ordinal.
        "culture" =>
        [
            new("$.\u0131", DiscrepancyKinds.Value, DiscrepancyValue.Literal("1"), DiscrepancyValue.Literal("2")),
            new("$.i", DiscrepancyKinds.Value, DiscrepancyValue.Literal("1"), DiscrepancyValue.Literal("2")),
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Literal("i"), DiscrepancyValue.Literal("2")),
            new("$.\u0130", DiscrepancyKinds.Value, DiscrepancyValue.Literal("1"), DiscrepancyValue.Literal("2")),
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Literal("I"), DiscrepancyValue.Literal("2")),
            new("$.I", DiscrepancyKinds.Value, DiscrepancyValue.Literal("1"), DiscrepancyValue.Literal("2")),
        ],

        // Same path and kind; only the baseline token differs.
        "tie-baseline" =>
        [
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Literal("b"), DiscrepancyValue.Literal("o")),
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Literal("a"), DiscrepancyValue.Literal("o")),
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Absent, DiscrepancyValue.Literal("o")),
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Valid("N2"), DiscrepancyValue.Literal("o")),
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Valid("N1"), DiscrepancyValue.Literal("o")),
        ],

        // Same path and kind; only the observed token differs: valid, absent, empty and other literal.
        "tie-observed" =>
        [
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Literal("x"), DiscrepancyValue.Valid("N1")),
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Literal("x"), DiscrepancyValue.Absent),
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Literal("x"), DiscrepancyValue.Literal("")),
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Literal("x"), DiscrepancyValue.Literal("z")),
        ],

        // Same path and kind; the baseline token decides first and the observed token breaks its ties.
        "tie-both" =>
        [
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Literal("a"), DiscrepancyValue.Literal("z")),
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Valid("N1"), DiscrepancyValue.Literal("a")),
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Absent, DiscrepancyValue.Literal("a")),
            new("$.x", DiscrepancyKinds.Value, DiscrepancyValue.Literal("a"), DiscrepancyValue.Absent),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(vector), vector, null),
    };

    private static IEnumerable<Discrepancy[]> Permutations(Discrepancy[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, index) => index != i).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
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

    // The register stores its own copy of each entry, so a test compares the values it was given.
    private static void AssertCopyOf(RegisterEntry given, RegisterEntry stored)
    {
        Assert.NotSame(given, stored);
        Assert.Equal(given.Id, stored.Id);
        Assert.Equal(given.Category, stored.Category);
        Assert.Equal(given.Operation, stored.Operation);
        Assert.Equal<RegisterMatch>(given.Matches, stored.Matches);
        Assert.Equal(given.Reason, stored.Reason);
        Assert.Equal(given.Attempts, stored.Attempts);
        Assert.Equal(given.ActionTaken, stored.ActionTaken);
    }

    private static string Sig(params Discrepancy[] ds) => DiscrepancySignature.Compute(ds);

    // Every ordering of the items, each a new array; there are n! of them.
    private static IEnumerable<T[]> Permutations<T>(IReadOnlyList<T> items)
    {
        if (items.Count <= 1)
        {
            yield return [.. items];
            yield break;
        }

        for (var first = 0; first < items.Count; first++)
        {
            var rest = items.Where((_, index) => index != first).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return [items[first], .. tail];
            }
        }
    }

    // Matching depends only on the resulting nature, so any unambiguous encoding of the four parts the
    // failed-test signature covers serves here.
    private static string FailedNature(string exceptionType, string file, int line, string message) =>
        FailureItemIds.Failed(DiscrepancySignature.Hash16(JsonSerializer.Serialize(
            new[] { exceptionType, file, line.ToString(CultureInfo.InvariantCulture), message })));

    private static KnownLimitationsRegister LoadCommittedRegister() =>
        KnownLimitationsRegister.Load(Path.Combine(FindRepoRoot(), "harness", "config", "known-limitations.json"));

    // A register file of the test's own under the temp directory, so the committed register is never
    // written; the unique name keeps parallel tests apart.
    private static void WithRegisterFile(byte[] content, Action<string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"known-limitations-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllBytes(path, content);
            test(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

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

// ---- The case matrix and the case model ---------------------------------------------------------
// These members live in this class because the harness test project keeps its fixed set of test
// classes, and the matrix's case ids are the CASE:<caseId> failure identities this class covers.
public sealed partial class KnownLimitationsRegisterTests
{
    private const string MatrixMessagePrefix = "case-matrix: ";
    private const string GroupNotFound = "The committed matrix has no case in the requested group.";
    private const string NoCaseFound = "The committed matrix has no case with the requested shape.";
    private const string CaseTextNotFound = "The case's JSON text was not found in the matrix's JSON text.";
    private const string SoapActionHeader = "SOAPAction";

    // \z, unlike $, also rejects a trailing newline (AAP 0.7.3 id grammar).
    private const string IdPattern = @"^(REST|SOAP)-[A-Za-z]+-[0-9]{2}\z";

    private static readonly OperationCatalogue Catalogue = OperationCatalogue.Default;

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // The matrix's case and request members, in declaration order.
    private static readonly string[] CaseMembers =
    [
        "id", "operation", "kind", "stateChanging", "preState", "group", "request", "expectedStatus",
        "expectedResultStatusCode", "description",
    ];

    private static readonly string[] RequestMembers = ["method", "pathAndQuery", "headers", "bodyText"];

    // ---- The committed matrix -------------------------------------------------------------------

    [Fact]
    public void CommittedMatrix_HasThePlannedCounts()
    {
        var matrix = LoadCommittedMatrix();

        Assert.Equal(CaseMatrix.SupportedVersion, matrix.Version);
        Assert.Equal(56, CaseMatrix.ExpectedCaseCount);
        Assert.Equal(CaseMatrix.ExpectedCaseCount, matrix.Cases.Count);
        Assert.Equal(CaseMatrix.ExpectedCaseCount, matrix.PlannedCaseCount);
        Assert.Equal(36, matrix.CountFor(Surface.Rest));
        Assert.Equal(20, matrix.CountFor(Surface.Soap));
        Assert.Equal(22, matrix.PlannedOperationCount);
        Assert.Equal(Catalogue.Operations.Count, matrix.PlannedOperationCount);

        foreach (var surface in new[] { Surface.Rest, Surface.Soap })
        {
            Assert.Equal(
                matrix.Cases.Count(definition => Catalogue.GetById(definition.Operation).Surface == surface),
                matrix.CountFor(surface));
        }
    }

    [Fact]
    public void CountFor_UndefinedSurface_Throws()
    {
        var matrix = LoadCommittedMatrix();

        Assert.Throws<ArgumentOutOfRangeException>("surface", () => matrix.CountFor((Surface)99));
    }

    [Fact]
    public void CommittedMatrix_ByOperation_CoversExactlyTheCatalogueOperations()
    {
        var matrix = LoadCommittedMatrix();

        Assert.Equal(
            Catalogue.Operations.Select(operation => operation.Id).Order(StringComparer.Ordinal),
            matrix.ByOperation.Keys.Order(StringComparer.Ordinal));

        // Keys enumerate in order of first appearance, and each list is that operation's cases in matrix order.
        Assert.Equal(matrix.Cases.Select(definition => definition.Operation).Distinct(StringComparer.Ordinal), matrix.ByOperation.Keys);
        Assert.All(
            matrix.ByOperation,
            entry => Assert.Equal(
                matrix.Cases.Where(definition => string.Equals(definition.Operation, entry.Key, StringComparison.Ordinal)),
                entry.Value));
        Assert.Equal(matrix.Cases.Count, matrix.ByOperation.Values.Sum(cases => cases.Count));
    }

    [Fact]
    public void CommittedMatrix_IdsAreWellFormedUniqueAndAgreeWithTheirOperation()
    {
        var matrix = LoadCommittedMatrix();

        Assert.All(matrix.Cases, definition =>
        {
            Assert.Matches(IdPattern, definition.Id);
            var operation = Catalogue.GetById(definition.Operation);
            Assert.Equal(operation.Surface, definition.Surface);
            Assert.Equal(operation.Surface == Surface.Rest ? "REST" : "SOAP", definition.SurfacePrefix);
            Assert.Equal(operation.JadeMethod, definition.JadeMethod);
        });

        var duplicates = matrix.Cases
            .GroupBy(definition => definition.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        Assert.Empty(duplicates);
    }

    [Fact]
    public void CommittedMatrix_StateChangingIsTheCatalogueFlag()
    {
        var matrix = LoadCommittedMatrix();

        Assert.All(matrix.Cases, definition =>
        {
            var operation = Catalogue.GetById(definition.Operation);
            Assert.Equal(operation.StateChanging, definition.StateChanging);
            Assert.Equal(IsWriteOperation(operation), definition.StateChanging);
        });

        // AAP 0.4.2: the only state-changing operations are the four REST postCreate* and the four SOAP update*.
        Assert.All(Catalogue.Operations, operation => Assert.Equal(IsWriteOperation(operation), operation.StateChanging));
        Assert.Equal(4, Catalogue.GetBySurface(Surface.Rest).Count(operation => operation.StateChanging));
        Assert.Equal(4, Catalogue.GetBySurface(Surface.Soap).Count(operation => operation.StateChanging));
    }

    [Fact]
    public void CommittedMatrix_GroupsRunInTheFixedReplayOrder()
    {
        var matrix = LoadCommittedMatrix();

        Assert.Equal(
            new[] { CaseGroups.RestRead, CaseGroups.SoapRead, CaseGroups.RestWrite, CaseGroups.SoapWrite, CaseGroups.EmptyState },
            CaseGroups.Order);
        Assert.Equal(new[] { 22, 11, 13, 9, 1 }, CaseGroups.Order.Select(group => matrix.GetGroup(group).Count));
        Assert.Equal(matrix.Cases, CaseGroups.Order.SelectMany(group => matrix.GetGroup(group)));

        var groupIndexes = matrix.Cases.Select(definition => CaseGroups.IndexOf(definition.Group)).ToArray();
        Assert.DoesNotContain(-1, groupIndexes);
        for (var index = 1; index < groupIndexes.Length; index++)
        {
            Assert.True(groupIndexes[index] >= groupIndexes[index - 1], $"Case #{index + 1} is in a group that runs before the group of case #{index}.");
        }

        AssertGroupHolds(matrix, CaseGroups.RestRead, Surface.Rest, stateChanging: false);
        AssertGroupHolds(matrix, CaseGroups.SoapRead, Surface.Soap, stateChanging: false);
        AssertGroupHolds(matrix, CaseGroups.RestWrite, Surface.Rest, stateChanging: true);
        AssertGroupHolds(matrix, CaseGroups.SoapWrite, Surface.Soap, stateChanging: true);

        // Every seed-state read runs before any write; the empty-state read follows the writes.
        var firstWrite = matrix.Cases.Where(definition => definition.StateChanging).Min(definition => matrix.PositionOf(definition.Id));
        var lastWrite = matrix.Cases.Where(definition => definition.StateChanging).Max(definition => matrix.PositionOf(definition.Id));
        var lastSeedRead = matrix.Cases
            .Where(definition => !definition.StateChanging && !definition.IsEmptyState)
            .Max(definition => matrix.PositionOf(definition.Id));
        var emptyState = Assert.Single(matrix.Cases, definition => definition.IsEmptyState);
        Assert.True(lastSeedRead < firstWrite, "A seed-state read runs after a write.");
        Assert.True(matrix.PositionOf(emptyState.Id) > lastWrite, "The empty-state case runs before a write.");
    }

    [Fact]
    public void CommittedMatrix_OperationsWithinEachGroupFollowCatalogueOrder()
    {
        var matrix = LoadCommittedMatrix();
        var catalogueIndex = Catalogue.Operations
            .Select((operation, index) => (operation.Id, index))
            .ToDictionary(entry => entry.Id, entry => entry.index, StringComparer.Ordinal);

        foreach (var group in CaseGroups.Order)
        {
            var indexes = matrix.GetGroup(group).Select(definition => catalogueIndex[definition.Operation]).ToArray();
            for (var index = 1; index < indexes.Length; index++)
            {
                Assert.True(indexes[index] >= indexes[index - 1], $"Group {group} lists an operation before one that precedes it in the catalogue.");
            }
        }
    }

    [Fact]
    public void CommittedMatrix_EmptyStateCaseIsTheLastCaseAndTheRestGetCompanyError()
    {
        var matrix = LoadCommittedMatrix();

        var emptyState = Assert.Single(matrix.Cases, definition => definition.IsEmptyState);
        Assert.Same(matrix.Cases[^1], emptyState);
        Assert.Same(emptyState, Assert.Single(matrix.GetGroup(CaseGroups.EmptyState)));

        var getCompany = Catalogue.Find(Surface.Rest, "getCompany");
        Assert.Equal(getCompany.Id, emptyState.Operation);
        Assert.Equal(CasePreStates.Empty, emptyState.PreState);
        Assert.Equal(CaseGroups.EmptyState, emptyState.Group);
        Assert.Equal(CaseKinds.Error, emptyState.Kind);
        Assert.False(emptyState.IsSuccess);
        Assert.False(emptyState.StateChanging);
        Assert.Equal(500, emptyState.ExpectedStatus);
        Assert.Contains(getCompany.Statuses, status => status.HttpStatus == emptyState.ExpectedStatus);

        Assert.All(matrix.Cases.Take(matrix.Cases.Count - 1), definition =>
        {
            Assert.Equal(CasePreStates.Seed, definition.PreState);
            Assert.False(definition.IsEmptyState);
        });
    }

    [Fact]
    public void CommittedMatrix_PositionsAreOneBasedInMatrixOrder()
    {
        var matrix = LoadCommittedMatrix();

        for (var index = 0; index < matrix.Cases.Count; index++)
        {
            var definition = matrix.Cases[index];
            Assert.Equal(index + 1, matrix.PositionOf(definition.Id));
            Assert.True(matrix.TryGetCase(definition.Id, out var found));
            Assert.Same(definition, found);
            Assert.Same(definition, matrix.GetCase(definition.Id));
        }

        Assert.Equal(1, matrix.PositionOf(matrix.Cases[0].Id));
        Assert.Equal(matrix.PlannedCaseCount, matrix.PositionOf(matrix.Cases[^1].Id));
    }

    // The control for the malformed-matrix theory: the in-memory copy every mutation starts from loads,
    // and loads exactly as the file does, so each rejection is caused by its mutation alone.
    [Fact]
    public void Parse_UnmutatedInMemoryCopy_LoadsAsTheFileDoes()
    {
        var loaded = LoadCommittedMatrix();
        var parsed = CaseMatrix.Parse(CommittedMatrixNode().ToJsonString(), Catalogue);

        Assert.Equal(CaseMatrix.ExpectedCaseCount, parsed.Cases.Count);
        Assert.Equal(loaded.Cases.Count, parsed.Cases.Count);
        for (var index = 0; index < loaded.Cases.Count; index++)
        {
            AssertSameCase(loaded.Cases[index], parsed.Cases[index]);
        }
    }

    // ---- Lookups --------------------------------------------------------------------------------

    /// <summary>
    /// Ids the committed matrix does not hold: synthetic ones, and near misses of its first id that only an
    /// ordinal, untrimmed comparison rejects.
    /// </summary>
    public static TheoryData<string> UnknownIds()
    {
        var id = LoadCommittedMatrix().Cases[0].Id;
        return new TheoryData<string>
        {
            string.Empty,
            "REST-noSuchMethod-01",
            "CASE:REST-noSuchMethod-01",
            id.ToLowerInvariant(),
            id.ToUpperInvariant(),
            id + " ",
            " " + id,
        };
    }

    [Theory]
    [MemberData(nameof(UnknownIds))]
    public void Lookups_UnknownId_AreNotFound(string caseId)
    {
        var matrix = LoadCommittedMatrix();

        Assert.False(matrix.TryGetCase(caseId, out var found));
        Assert.Null(found);
        var position = Assert.Throws<KeyNotFoundException>(() => matrix.PositionOf(caseId));
        Assert.Contains($"'{caseId}'", position.Message, StringComparison.Ordinal);
        var lookup = Assert.Throws<KeyNotFoundException>(() => matrix.GetCase(caseId));
        Assert.Contains($"'{caseId}'", lookup.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CaseGroups.RestRead, 0)]
    [InlineData(CaseGroups.SoapRead, 1)]
    [InlineData(CaseGroups.RestWrite, 2)]
    [InlineData(CaseGroups.SoapWrite, 3)]
    [InlineData(CaseGroups.EmptyState, 4)]
    [InlineData("REST-READ", -1)]
    [InlineData("rest-reads", -1)]
    [InlineData(" rest-read", -1)]
    [InlineData("", -1)]
    public void GroupIndexOf_IsTheOrdinalPositionInTheOrder(string group, int expected)
    {
        Assert.Equal(expected, CaseGroups.IndexOf(group));
    }

    [Theory]
    [InlineData("REST-READ")]
    [InlineData("rest-reads")]
    [InlineData(" rest-read")]
    [InlineData("")]
    public void GetGroup_UnknownGroup_IsEmpty(string group)
    {
        Assert.Empty(LoadCommittedMatrix().GetGroup(group));
    }

    [Fact]
    public void Lookups_NullArguments_Throw()
    {
        var matrix = LoadCommittedMatrix();

        Assert.Throws<ArgumentNullException>("caseId", () => matrix.PositionOf(null!));
        Assert.Throws<ArgumentNullException>("caseId", () => matrix.TryGetCase(null!, out _));
        Assert.Throws<ArgumentNullException>("caseId", () => matrix.GetCase(null!));
        Assert.Throws<ArgumentNullException>("group", () => matrix.GetGroup(null!));
    }

    // ---- Case and request helpers ---------------------------------------------------------------

    [Theory]
    [InlineData("REST-anyMethod-07", "REST", "anyMethod", "07")]
    [InlineData("SOAP-otherMethod-12", "SOAP", "otherMethod", "12")]
    [InlineData("REST-anyMethod", "REST", "", "")]
    [InlineData("REST-any-Method-07", "REST", "", "")]
    [InlineData("-anyMethod-07", "", "anyMethod", "07")]
    [InlineData("noDash", "", "", "")]
    [InlineData("", "", "", "")]
    [InlineData(null, "", "", "")]
    public void IdParts_SplitTheIdOnDashes(string? id, string surfacePrefix, string jadeMethod, string idNumber)
    {
        // A null id stands for a matrix entry that has not been validated yet; the helpers must not throw.
        var definition = Definition(id: id!);

        Assert.Equal(surfacePrefix, definition.SurfacePrefix);
        Assert.Equal(jadeMethod, definition.JadeMethod);
        Assert.Equal(idNumber, definition.IdNumber);
    }

    [Theory]
    [InlineData("REST-anyMethod-07", Surface.Rest)]
    [InlineData("SOAP-anyMethod-07", Surface.Soap)]
    [InlineData("REST-malformed", Surface.Rest)]
    public void Surface_FollowsTheIdPrefix(string id, Surface expected)
    {
        Assert.Equal(expected, Definition(id: id).Surface);
    }

    [Theory]
    [InlineData("rest-anyMethod-07")]
    [InlineData("Soap-anyMethod-07")]
    [InlineData("HTTP-anyMethod-07")]
    [InlineData("RESTanyMethod07")]
    [InlineData("")]
    [InlineData(null)]
    public void Surface_MalformedIdPrefix_Throws(string? id)
    {
        var definition = Definition(id: id!);

        var ex = Assert.Throws<InvalidOperationException>(() => definition.Surface);
        Assert.Contains($"'{id}'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CasePreStates.Empty, true)]
    [InlineData(CasePreStates.Seed, false)]
    [InlineData("Empty", false)]
    [InlineData("empty ", false)]
    public void IsEmptyState_IsOrdinalOnPreState(string preState, bool expected)
    {
        Assert.Equal(expected, Definition(preState: preState).IsEmptyState);
    }

    [Theory]
    [InlineData(CaseKinds.Success, true)]
    [InlineData(CaseKinds.Error, false)]
    [InlineData("Success", false)]
    [InlineData("success ", false)]
    public void IsSuccess_IsOrdinalOnKind(string kind, bool expected)
    {
        Assert.Equal(expected, Definition(kind: kind).IsSuccess);
    }

    [Fact]
    public void GetHeader_IgnoresCaseWhateverTheDictionaryComparer()
    {
        // An ordinal, case-sensitive dictionary: the lookup must not depend on its comparer.
        var headers = new OrderedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Content-Type"] = "text/xml; charset=utf-8",
            ["SOAPAction"] = "\"urn:example/operation\"",
            ["X-Trace"] = "first",
            ["x-trace"] = "second",
        };
        var definition = Definition(request: Request("/resource", headers));

        Assert.Equal("\"urn:example/operation\"", definition.Request.GetHeader("soapaction"));
        Assert.Equal("text/xml; charset=utf-8", definition.Request.GetHeader("CONTENT-TYPE"));
        Assert.Equal("\"urn:example/operation\"", definition.GetHeader("SOAPACTION"));
        Assert.Equal("first", definition.GetHeader("X-TRACE"));
        Assert.Null(definition.GetHeader("Accept"));
        Assert.Null(definition.GetHeader("Content-Type "));
        Assert.Throws<ArgumentNullException>("name", () => definition.GetHeader(null!));
        Assert.Throws<ArgumentNullException>("name", () => definition.Request.GetHeader(null!));
    }

    /// <summary>
    /// Raw request targets with the path and query they split into, the last the catalogue's SOAP endpoint.
    /// </summary>
    public static TheoryData<string, string, string?> RequestTargets()
    {
        var soap = OperationCatalogue.Default.Addresses.Soap;
        return new TheoryData<string, string, string?>
        {
            { "/resource/%20Pauline%20Wild%20", "/resource/%20Pauline%20Wild%20", null },
            { "/resource/a%2Fb.json", "/resource/a%2Fb.json", null },
            { "/resource?key=%3F%26&other", "/resource", "key=%3F%26&other" },
            { "/resource/%3F?a?b", "/resource/%3F", "a?b" },
            { "/resource?", "/resource", string.Empty },
            { "?only", string.Empty, "only" },
            { soap.EndpointPathAndQuery, soap.Path, soap.Query },
        };
    }

    [Theory]
    [MemberData(nameof(RequestTargets))]
    public void PathAndQueryParts_SplitAtTheFirstQuestionMarkAndKeepPercentEncoding(string pathAndQuery, string path, string? query)
    {
        var request = Request(pathAndQuery);

        Assert.Equal(path, request.PathPart);
        Assert.Equal(query, request.QueryPart);
        Assert.Equal(pathAndQuery, request.PathAndQuery);
    }

    [Fact]
    public void CamelCaseSerialisation_HasExactlyTheMatrixShape()
    {
        var matrix = LoadCommittedMatrix();
        var serialised = new JsonArray();
        foreach (var definition in matrix.Cases)
        {
            var node = JsonSerializer.SerializeToNode(definition, CamelCase)!.AsObject();
            Assert.Equal(CaseMembers, node.Select(member => member.Key));
            Assert.Equal(RequestMembers, node["request"]!.AsObject().Select(member => member.Key));
            serialised.Add(node);
        }

        // The committed file writes no member the model does not hold.
        var caseMembers = CaseMembers.ToHashSet(StringComparer.Ordinal);
        var requestMembers = RequestMembers.ToHashSet(StringComparer.Ordinal);
        Assert.All(CasesOf(CommittedMatrixNode()), node =>
        {
            var committed = node!.AsObject();
            Assert.Subset(caseMembers, committed.Select(member => member.Key).ToHashSet(StringComparer.Ordinal));
            Assert.Subset(requestMembers, RequestOf(committed).Select(member => member.Key).ToHashSet(StringComparer.Ordinal));
        });

        // The serialised cases are a matrix the loader reads back unchanged.
        var document = new JsonObject
        {
            ["version"] = CaseMatrix.SupportedVersion,
            ["description"] = "The committed cases, serialised from the case model.",
            ["cases"] = serialised,
        };
        var reparsed = CaseMatrix.Parse(document.ToJsonString(), Catalogue);
        Assert.Equal(matrix.Cases.Count, reparsed.Cases.Count);
        for (var index = 0; index < matrix.Cases.Count; index++)
        {
            AssertSameCase(matrix.Cases[index], reparsed.Cases[index]);
        }
    }

    // ---- Malformed matrices ---------------------------------------------------------------------

    [Theory]
    [InlineData("null-document")]
    [InlineData("unknown-root-member")]
    [InlineData("unknown-case-member")]
    [InlineData("unknown-request-member")]
    [InlineData("repeated-root-key")]
    [InlineData("repeated-case-key")]
    [InlineData("comment-at-root")]
    [InlineData("comment-in-case")]
    [InlineData("trailing-comma-in-case")]
    [InlineData("trailing-comma-after-last-case")]
    [InlineData("missing-version")]
    [InlineData("unsupported-version")]
    [InlineData("version-as-string")]
    [InlineData("missing-description")]
    [InlineData("blank-description")]
    [InlineData("missing-cases")]
    [InlineData("null-case")]
    [InlineData("count-55")]
    [InlineData("count-57")]
    [InlineData("uncovered-operation")]
    [InlineData("missing-id")]
    [InlineData("malformed-id")]
    [InlineData("id-with-one-digit")]
    [InlineData("id-with-trailing-newline")]
    [InlineData("duplicate-id")]
    [InlineData("missing-operation")]
    [InlineData("unknown-operation")]
    [InlineData("id-prefix-mismatch")]
    [InlineData("id-method-mismatch")]
    [InlineData("state-changing-on-read")]
    [InlineData("state-changing-off-write")]
    [InlineData("unknown-kind")]
    [InlineData("unknown-pre-state")]
    [InlineData("unknown-group")]
    [InlineData("write-before-read")]
    [InlineData("group-of-other-surface")]
    [InlineData("group-of-other-side")]
    [InlineData("empty-pre-state-outside-empty-state")]
    [InlineData("seed-pre-state-in-empty-state")]
    [InlineData("status-below-range")]
    [InlineData("status-above-range")]
    [InlineData("missing-expected-status")]
    [InlineData("result-status-on-rest")]
    [InlineData("result-status-on-soap-read")]
    [InlineData("blank-case-description")]
    [InlineData("missing-request")]
    [InlineData("missing-request-method")]
    [InlineData("accept-header")]
    [InlineData("accept-header-lower-case")]
    [InlineData("header-repeated-ignoring-case")]
    [InlineData("blank-header-name")]
    [InlineData("null-header-value")]
    [InlineData("rest-path-without-slash")]
    [InlineData("rest-path-with-query")]
    [InlineData("rest-path-of-other-operation")]
    [InlineData("rest-path-of-no-operation")]
    [InlineData("soap-method-get")]
    [InlineData("soap-method-lower-case")]
    [InlineData("soap-path-without-query")]
    [InlineData("soap-path-of-wsdl")]
    [InlineData("soap-action-missing")]
    [InlineData("soap-action-of-other-operation")]
    [InlineData("soap-action-unknown")]
    [InlineData("soap-body-null")]
    [InlineData("soap-body-blank")]
    public void Parse_MalformedMatrix_IsRejected(string mutation)
    {
        var mutated = Mutate(mutation);

        var ex = Assert.Throws<InvalidDataException>(() => CaseMatrix.Parse(mutated.Json, Catalogue));

        Assert.StartsWith(MatrixMessagePrefix, ex.Message, StringComparison.Ordinal);
        Assert.Contains(mutated.Fragment, ex.Message, StringComparison.Ordinal);
        if (mutated.CaseId is null)
        {
            Assert.DoesNotContain("case '", ex.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"case '{mutated.CaseId}'", ex.Message, StringComparison.Ordinal);
        }
    }

    // ---- Load and argument checks ---------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_ReadsTheFileAsUtf8AndLeavesItUnchanged(bool byteOrderMark)
    {
        var text = CommittedMatrixText();
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "case-matrix.json");
            File.WriteAllText(path, text, new UTF8Encoding(byteOrderMark));
            var before = File.ReadAllBytes(path);

            var matrix = CaseMatrix.Load(path, Catalogue);

            Assert.Equal(CaseMatrix.ExpectedCaseCount, matrix.Cases.Count);
            Assert.Equal(CaseMatrix.Parse(text, Catalogue).Cases.Select(definition => definition.Id), matrix.Cases.Select(definition => definition.Id));
            Assert.Equal(before, File.ReadAllBytes(path));
        });
    }

    [Fact]
    public void Load_InvalidContent_ThrowsInvalidData()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "case-matrix.json");
            File.WriteAllText(path, "{}", new UTF8Encoding(false));

            var ex = Assert.Throws<InvalidDataException>(() => CaseMatrix.Load(path, Catalogue));
            Assert.Equal(MatrixMessagePrefix + "missing 'version'", ex.Message);
        });
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFound()
    {
        WithTempDirectory(directory =>
            Assert.Throws<FileNotFoundException>(() => CaseMatrix.Load(Path.Combine(directory, "case-matrix.json"), Catalogue)));
    }

    [Fact]
    public void Load_MissingDirectory_ThrowsDirectoryNotFound()
    {
        WithTempDirectory(directory =>
            Assert.Throws<DirectoryNotFoundException>(
                () => CaseMatrix.Load(Path.Combine(directory, "absent", "case-matrix.json"), Catalogue)));
    }

    [Fact]
    public void Load_NullOrEmptyArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>("path", () => CaseMatrix.Load(null!, Catalogue));
        Assert.Throws<ArgumentException>("path", () => CaseMatrix.Load(string.Empty, Catalogue));
        Assert.Throws<ArgumentNullException>("catalogue", () => CaseMatrix.Load(CommittedMatrixPath(), null!));
    }

    [Fact]
    public void Parse_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>("json", () => CaseMatrix.Parse(null!, Catalogue));
        Assert.Throws<ArgumentNullException>("catalogue", () => CaseMatrix.Parse(CommittedMatrixText(), null!));
    }

    // ---- Mutations ------------------------------------------------------------------------------

    // Returns one rejected matrix: its JSON text, a fragment the loader's message must contain, and the
    // id of the case the message must name, or null when the fault is no named case's.
    private static (string Json, string Fragment, string? CaseId) Mutate(string key)
    {
        var root = CommittedMatrixNode();
        var cases = CasesOf(root);
        var restRead = FirstIndexIn(cases, CaseGroups.RestRead);
        var soapRead = FirstIndexIn(cases, CaseGroups.SoapRead);
        var restWrite = FirstIndexIn(cases, CaseGroups.RestWrite);
        var soapWrite = FirstIndexIn(cases, CaseGroups.SoapWrite);
        var emptyState = FirstIndexIn(cases, CaseGroups.EmptyState);
        var restCase = CaseAt(cases, restRead);
        var restId = IdOf(restCase);
        var soapCase = CaseAt(cases, soapRead);
        var soapId = IdOf(soapCase);

        switch (key)
        {
            case "null-document":
                return ("null", "document is empty", null);

            case "unknown-root-member":
                root["unexpected"] = true;
                return Mutated(root, "invalid JSON at $.unexpected", null);

            case "unknown-case-member":
                restCase["unexpected"] = true;
                return Mutated(root, $"invalid JSON at $.cases[{restRead}].unexpected", restId);

            case "unknown-request-member":
                RequestOf(restCase)["unexpected"] = true;
                return Mutated(root, $"invalid JSON at $.cases[{restRead}].request.unexpected", restId);

            case "repeated-root-key":
                return (
                    "{\"version\":" + CaseMatrix.SupportedVersion.ToString(CultureInfo.InvariantCulture) + "," + root.ToJsonString()[1..],
                    "invalid JSON at $.version",
                    null);

            case "repeated-case-key":
                return (
                    EditCaseText(root, restRead, text => "{\"kind\":" + restCase["kind"]!.ToJsonString() + "," + text[1..]),
                    $"invalid JSON at $.cases[{restRead}].kind",
                    restId);

            // A comment or trailing comma leaves no parseable document to read an id from, so the loader
            // locates the fault by its JSON path.
            case "comment-at-root":
                return ("{/* comment */" + root.ToJsonString()[1..], "invalid JSON at $:", null);

            case "comment-in-case":
                return (
                    EditCaseText(root, soapRead, text => "{/* comment */" + text[1..]),
                    $"invalid JSON at $.cases[{soapRead}]",
                    null);

            case "trailing-comma-in-case":
                return (
                    EditCaseText(root, restWrite, text => text[..^1] + ",}"),
                    $"invalid JSON at $.cases[{restWrite}]",
                    null);

            case "trailing-comma-after-last-case":
                return (
                    EditCaseText(root, emptyState, text => text + ","),
                    "invalid JSON at $.cases[",
                    null);

            case "missing-version":
                root.Remove("version");
                return Mutated(root, "missing 'version'", null);

            case "unsupported-version":
                root["version"] = CaseMatrix.SupportedVersion + 1;
                return Mutated(root, $"unsupported version {CaseMatrix.SupportedVersion + 1}", null);

            case "version-as-string":
                root["version"] = CaseMatrix.SupportedVersion.ToString(CultureInfo.InvariantCulture);
                return Mutated(root, "invalid JSON at $.version", null);

            case "missing-description":
                root.Remove("description");
                return Mutated(root, "missing 'description'", null);

            case "blank-description":
                root["description"] = " \t ";
                return Mutated(root, "'description' is empty", null);

            case "missing-cases":
                root.Remove("cases");
                return Mutated(root, "missing 'cases'", null);

            case "null-case":
                cases[soapWrite] = null;
                return Mutated(root, $"case #{soapWrite + 1} (no id): is null", null);

            case "count-55":
                {
                    cases.RemoveAt(SecondCaseOfAnOperation(cases, CaseGroups.RestRead));
                    return Mutated(
                        root,
                        $"expected {CaseMatrix.ExpectedCaseCount} cases, found {CaseMatrix.ExpectedCaseCount - 1}",
                        null);
                }

            case "count-57":
                {
                    cases.Insert(restRead + 1, WithUnusedId(restCase));
                    return Mutated(
                        root,
                        $"expected {CaseMatrix.ExpectedCaseCount} cases, found {CaseMatrix.ExpectedCaseCount + 1}",
                        null);
                }

            case "uncovered-operation":
                {
                    // The only case of one operation is replaced, in place, by a copy of a case of an operation
                    // that has more than one, so the count and the order still hold.
                    var only = OnlyCaseOfAnOperation(cases, CaseGroups.RestRead);
                    var uncovered = CaseAt(cases, only)["operation"]!.GetValue<string>();
                    cases[only] = WithUnusedId(CaseAt(cases, SecondCaseOfAnOperation(cases, CaseGroups.RestRead)));
                    return Mutated(root, $"catalogue operations with no case: {uncovered}", null);
                }

            case "missing-id":
                restCase.Remove("id");
                return Mutated(root, $"case #{restRead + 1} (no id): missing 'id'", null);

            case "malformed-id":
                restCase["id"] = restId.ToLowerInvariant();
                return Mutated(root, "id does not have the form", restId.ToLowerInvariant());

            case "id-with-one-digit":
                restCase["id"] = restId[..^1];
                return Mutated(root, "id does not have the form", restId[..^1]);

            case "id-with-trailing-newline":
                restCase["id"] = restId + "\n";
                return Mutated(root, "id does not have the form", restId + "\n");

            case "duplicate-id":
                CaseAt(cases, restRead + 1)["id"] = restId;
                return Mutated(root, $"duplicate id '{restId}', already used by case #{restRead + 1}", restId);

            case "missing-operation":
                restCase.Remove("operation");
                return Mutated(root, "missing 'operation'", restId);

            case "unknown-operation":
                {
                    const string unknown = "Rest.noSuchOperation";
                    Assert.False(Catalogue.TryGetById(unknown, out _));
                    restCase["operation"] = unknown;
                    return Mutated(root, $"operation '{unknown}' is not a catalogue operation id", restId);
                }

            case "id-prefix-mismatch":
                {
                    var renamed = "SOAP" + restId[restId.IndexOf('-', StringComparison.Ordinal)..];
                    restCase["id"] = renamed;
                    return Mutated(root, "id prefix 'SOAP' does not match", renamed);
                }

            case "id-method-mismatch":
                {
                    var other = OtherOperationInGroup(cases, restRead);
                    restCase["operation"] = other;
                    return Mutated(root, $"but catalogue operation '{other}' is", restId);
                }

            case "state-changing-on-read":
                restCase["stateChanging"] = true;
                return Mutated(root, "stateChanging is true but catalogue operation", restId);

            case "state-changing-off-write":
                CaseAt(cases, restWrite)["stateChanging"] = false;
                return Mutated(root, "stateChanging is false but catalogue operation", IdOf(CaseAt(cases, restWrite)));

            case "unknown-kind":
                restCase["kind"] = "failure";
                return Mutated(root, "kind 'failure' is not", restId);

            case "unknown-pre-state":
                restCase["preState"] = "seeded";
                return Mutated(root, "preState 'seeded' is not", restId);

            case "unknown-group":
                restCase["group"] = "rest-reads";
                return Mutated(root, "group 'rest-reads' is not one of", restId);

            case "write-before-read":
                {
                    // The first write moves to the front, so the case that was first now follows a write.
                    var moved = cases[restWrite];
                    cases.RemoveAt(restWrite);
                    cases.Insert(restRead, moved);
                    return Mutated(root, $"group '{CaseGroups.RestRead}' follows group '{CaseGroups.RestWrite}'", restId);
                }

            case "group-of-other-surface":
                soapCase["group"] = CaseGroups.RestRead;
                return Mutated(root, $"group '{CaseGroups.RestRead}' holds only {Surface.Rest} cases", soapId);

            case "group-of-other-side":
                restCase["group"] = CaseGroups.RestWrite;
                return Mutated(root, $"group '{CaseGroups.RestWrite}' holds only operations with stateChanging true", restId);

            case "empty-pre-state-outside-empty-state":
                restCase["preState"] = CasePreStates.Empty;
                return Mutated(root, $"preState '{CasePreStates.Empty}' in group '{CaseGroups.RestRead}'", restId);

            case "seed-pre-state-in-empty-state":
                CaseAt(cases, emptyState)["preState"] = CasePreStates.Seed;
                return Mutated(
                    root,
                    $"preState '{CasePreStates.Seed}' in group '{CaseGroups.EmptyState}'",
                    IdOf(CaseAt(cases, emptyState)));

            case "status-below-range":
                restCase["expectedStatus"] = 99;
                return Mutated(root, "expectedStatus 99 is not an HTTP status", restId);

            case "status-above-range":
                restCase["expectedStatus"] = 600;
                return Mutated(root, "expectedStatus 600 is not an HTTP status", restId);

            case "missing-expected-status":
                restCase.Remove("expectedStatus");
                return Mutated(root, "missing 'expectedStatus'", restId);

            // A REST write returns DTOResult too, so the rule is the surface, not only the return type.
            case "result-status-on-rest":
                CaseAt(cases, restWrite)["expectedResultStatusCode"] = 201;
                return Mutated(
                    root,
                    "expectedResultStatusCode 201 is allowed only on a SOAP operation",
                    IdOf(CaseAt(cases, restWrite)));

            case "result-status-on-soap-read":
                soapCase["expectedResultStatusCode"] = 200;
                return Mutated(root, "expectedResultStatusCode 200 is allowed only on a SOAP operation", soapId);

            case "blank-case-description":
                restCase["description"] = " ";
                return Mutated(root, "'description' is empty", restId);

            case "missing-request":
                restCase.Remove("request");
                return Mutated(root, "missing 'request'", restId);

            case "missing-request-method":
                RequestOf(restCase).Remove("method");
                return Mutated(root, "missing 'request.method'", restId);

            case "accept-header":
                HeadersOf(restCase)["Accept"] = "application/json";
                return Mutated(root, "has an 'Accept' header", restId);

            case "accept-header-lower-case":
                HeadersOf(soapCase)["accept"] = "text/xml";
                return Mutated(root, "has an 'Accept' header", soapId);

            case "header-repeated-ignoring-case":
                {
                    var headers = HeadersOf(soapCase);
                    var (name, value) = headers.First();
                    var repeated = name.ToLowerInvariant();
                    Assert.NotEqual(name, repeated);
                    headers[repeated] = value!.DeepClone();
                    return Mutated(root, $"names '{repeated}' more than once", soapId);
                }

            case "blank-header-name":
                HeadersOf(restCase)[" "] = "value";
                return Mutated(root, "empty or blank header name", restId);

            case "null-header-value":
                HeadersOf(restCase)["X-Test"] = null;
                return Mutated(root, "request.headers 'X-Test' is null", restId);

            case "rest-path-without-slash":
                RequestOf(restCase)["pathAndQuery"] = PathOf(restCase)[1..];
                return Mutated(root, "does not start with '/'", restId);

            case "rest-path-with-query":
                RequestOf(restCase)["pathAndQuery"] = PathOf(restCase) + "?format=json";
                return Mutated(root, "has a query; characterised REST requests carry none", restId);

            case "rest-path-of-other-operation":
                {
                    var other = CaseAt(cases, IndexOfOperation(cases, OtherOperationInGroup(cases, restRead)));
                    RequestOf(restCase)["pathAndQuery"] = PathOf(other);
                    return Mutated(
                        root,
                        $"matches catalogue operation '{other["operation"]!.GetValue<string>()}', not '{restCase["operation"]!.GetValue<string>()}'",
                        restId);
                }

            case "rest-path-of-no-operation":
                RequestOf(restCase)["pathAndQuery"] = "/noSuchResource";
                return Mutated(root, "matches catalogue operation none", restId);

            case "soap-method-get":
                RequestOf(soapCase)["method"] = "GET";
                return Mutated(root, "SOAP request method is 'GET', not 'POST'", soapId);

            case "soap-method-lower-case":
                RequestOf(soapCase)["method"] = "post";
                return Mutated(root, "SOAP request method is 'post', not 'POST'", soapId);

            case "soap-path-without-query":
                RequestOf(soapCase)["pathAndQuery"] = Catalogue.Addresses.Soap.Path;
                return Mutated(root, "is not the catalogue endpoint", soapId);

            case "soap-path-of-wsdl":
                RequestOf(soapCase)["pathAndQuery"] = Catalogue.Addresses.Soap.WsdlPathAndQuery;
                return Mutated(root, "is not the catalogue endpoint", soapId);

            case "soap-action-missing":
                HeadersOf(soapCase).Remove(SoapActionKey(soapCase));
                return Mutated(root, $"SOAP request has no '{SoapActionHeader}' header", soapId);

            case "soap-action-of-other-operation":
                {
                    var operation = soapCase["operation"]!.GetValue<string>();
                    var other = Catalogue.GetBySurface(Surface.Soap).First(candidate => !string.Equals(candidate.Id, operation, StringComparison.Ordinal));
                    HeadersOf(soapCase)[SoapActionKey(soapCase)] = "\"" + other.Soap!.Action + "\"";
                    return Mutated(root, $"resolves to catalogue operation '{other.Id}', not '{operation}'", soapId);
                }

            case "soap-action-unknown":
                {
                    var action = Catalogue.Addresses.Soap.SoapActionPrefix + "noSuchOperation";
                    Assert.DoesNotContain(Catalogue.GetBySurface(Surface.Soap), operation => string.Equals(operation.Soap!.Action, action, StringComparison.Ordinal));
                    HeadersOf(soapCase)[SoapActionKey(soapCase)] = "\"" + action + "\"";
                    return Mutated(root, "resolves to catalogue operation none", soapId);
                }

            case "soap-body-null":
                RequestOf(soapCase)["bodyText"] = null;
                return Mutated(root, "SOAP request has no 'bodyText'", soapId);

            case "soap-body-blank":
                RequestOf(soapCase)["bodyText"] = " \n ";
                return Mutated(root, "SOAP request has no 'bodyText'", soapId);

            default:
                throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown mutation.");
        }
    }

    private static (string Json, string Fragment, string? CaseId) Mutated(JsonObject root, string fragment, string? caseId) =>
        (root.ToJsonString(), fragment, caseId);

    // Rewrites one case's text inside the matrix's text, for faults a JsonNode cannot hold.
    private static string EditCaseText(JsonObject root, int index, Func<string, string> edit)
    {
        var text = root.ToJsonString();
        var caseText = CaseAt(CasesOf(root), index).ToJsonString();
        var at = text.IndexOf(caseText, StringComparison.Ordinal);
        if (at < 0)
        {
            throw new InvalidOperationException(CaseTextNotFound);
        }

        return string.Concat(text.AsSpan(0, at), edit(caseText), text.AsSpan(at + caseText.Length));
    }

    // A copy of a case under the unused case number 99 of its own surface and JADE method.
    private static JsonObject WithUnusedId(JsonObject definition)
    {
        var copy = definition.DeepClone().AsObject();
        var id = IdOf(definition);
        copy["id"] = id[..id.LastIndexOf('-')] + "-99";
        return copy;
    }

    private static int FirstIndexIn(JsonArray cases, string group)
    {
        for (var index = 0; index < cases.Count; index++)
        {
            if (string.Equals(CaseAt(cases, index)["group"]!.GetValue<string>(), group, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new InvalidOperationException(GroupNotFound);
    }

    // The index of a case in the group whose operation has an earlier case.
    private static int SecondCaseOfAnOperation(JsonArray cases, string group) =>
        FindInGroup(cases, group, index => Enumerable.Range(0, index).Any(earlier => OperationAt(cases, earlier) == OperationAt(cases, index)));

    // The index of a case in the group that is the only case of its operation in the matrix.
    private static int OnlyCaseOfAnOperation(JsonArray cases, string group) =>
        FindInGroup(cases, group, index => Enumerable.Range(0, cases.Count).Count(other => OperationAt(cases, other) == OperationAt(cases, index)) == 1);

    private static int FindInGroup(JsonArray cases, string group, Func<int, bool> predicate)
    {
        for (var index = 0; index < cases.Count; index++)
        {
            if (string.Equals(CaseAt(cases, index)["group"]!.GetValue<string>(), group, StringComparison.Ordinal) && predicate(index))
            {
                return index;
            }
        }

        throw new InvalidOperationException(NoCaseFound);
    }

    // An operation of another case in the same group, on the same surface and with the same request method.
    private static string OtherOperationInGroup(JsonArray cases, int index)
    {
        var definition = CaseAt(cases, index);
        var group = definition["group"]!.GetValue<string>();
        var method = RequestOf(definition)["method"]!.GetValue<string>();
        var other = FindInGroup(
            cases,
            group,
            candidate => OperationAt(cases, candidate) != OperationAt(cases, index)
                && string.Equals(RequestOf(CaseAt(cases, candidate))["method"]!.GetValue<string>(), method, StringComparison.Ordinal));
        return OperationAt(cases, other);
    }

    private static int IndexOfOperation(JsonArray cases, string operation)
    {
        for (var index = 0; index < cases.Count; index++)
        {
            if (OperationAt(cases, index) == operation)
            {
                return index;
            }
        }

        throw new InvalidOperationException(NoCaseFound);
    }

    private static string SoapActionKey(JsonObject definition) =>
        HeadersOf(definition).First(header => string.Equals(header.Key, SoapActionHeader, StringComparison.OrdinalIgnoreCase)).Key;

    private static string OperationAt(JsonArray cases, int index) => CaseAt(cases, index)["operation"]!.GetValue<string>();

    private static JsonArray CasesOf(JsonObject root) => root["cases"]!.AsArray();

    private static JsonObject CaseAt(JsonArray cases, int index) => cases[index]!.AsObject();

    private static string IdOf(JsonObject definition) => definition["id"]!.GetValue<string>();

    private static JsonObject RequestOf(JsonObject definition) => definition["request"]!.AsObject();

    private static JsonObject HeadersOf(JsonObject definition) => RequestOf(definition)["headers"]!.AsObject();

    private static string PathOf(JsonObject definition) => RequestOf(definition)["pathAndQuery"]!.GetValue<string>();

    // ---- Case matrix helpers --------------------------------------------------------------------

    private static bool IsWriteOperation(OperationDescriptor operation) =>
        operation.JadeMethod.StartsWith(operation.Surface == Surface.Rest ? "postCreate" : "update", StringComparison.Ordinal);

    private static void AssertGroupHolds(CaseMatrix matrix, string group, Surface surface, bool stateChanging) =>
        Assert.All(matrix.GetGroup(group), definition =>
        {
            Assert.Equal(group, definition.Group);
            Assert.Equal(surface, Catalogue.GetById(definition.Operation).Surface);
            Assert.Equal(stateChanging, definition.StateChanging);
            Assert.Equal(CasePreStates.Seed, definition.PreState);
        });

    private static void AssertSameCase(CaseDefinition expected, CaseDefinition actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Operation, actual.Operation);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.StateChanging, actual.StateChanging);
        Assert.Equal(expected.PreState, actual.PreState);
        Assert.Equal(expected.Group, actual.Group);
        Assert.Equal(expected.Request.Method, actual.Request.Method);
        Assert.Equal(expected.Request.PathAndQuery, actual.Request.PathAndQuery);
        Assert.Equal(expected.Request.Headers.ToArray(), actual.Request.Headers.ToArray());
        Assert.Equal(expected.Request.BodyText, actual.Request.BodyText);
        Assert.Equal(expected.ExpectedStatus, actual.ExpectedStatus);
        Assert.Equal(expected.ExpectedResultStatusCode, actual.ExpectedResultStatusCode);
        Assert.Equal(expected.Description, actual.Description);
    }

    private static CaseDefinition Definition(
        string id = "REST-anyMethod-01",
        string kind = CaseKinds.Success,
        string preState = CasePreStates.Seed,
        CaseRequest? request = null) =>
        new(
            id,
            "Rest.anyMethod",
            kind,
            StateChanging: false,
            preState,
            CaseGroups.RestRead,
            request ?? Request("/resource"),
            ExpectedStatus: 200,
            ExpectedResultStatusCode: null,
            "A case built in memory.");

    private static CaseRequest Request(string pathAndQuery, IReadOnlyDictionary<string, string>? headers = null) =>
        new("GET", pathAndQuery, headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), BodyText: null);

    private static CaseMatrix LoadCommittedMatrix() => CaseMatrix.Load(CommittedMatrixPath(), Catalogue);

    private static string CommittedMatrixText() => File.ReadAllText(CommittedMatrixPath(), Encoding.UTF8);

    private static JsonObject CommittedMatrixNode() => JsonNode.Parse(CommittedMatrixText())!.AsObject();

    private static void WithTempDirectory(Action<string> test)
    {
        var directory = Directory.CreateTempSubdirectory("erewhon-case-matrix-tests-");
        try
        {
            test(directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string CommittedMatrixPath() => Path.Combine(FindRepoRoot(), "harness", "config", "case-matrix.json");
}

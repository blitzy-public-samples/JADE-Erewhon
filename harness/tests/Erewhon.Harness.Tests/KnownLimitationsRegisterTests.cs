using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Cases;
using Erewhon.Harness.Checks;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Modes;
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
/// JSON path where the text does not parse. The results report built on the register's decisions is
/// covered from inline sources and registers, and its rendered text is checked in memory; the report is
/// written only into a temporary directory. It also covers the checks whose failure items the register
/// matches: reading the facade tests' TRX file and <c>exit-code</c>, the JADE-unchanged check and the
/// ordering-evidence check, driven by scripted git runners and confirmed against throwaway repositories in
/// a temporary directory of the test's own, so git never runs in the harness's own repository. It also
/// covers the summary mode, which prints the results report's final line: how it writes that line to the
/// descriptor behind standard output (pipes, sockets, files and a standard output closed at start), from a
/// <c>RESULTS.md</c> in a temporary directory of its own. The committed
/// <c>harness/config/known-limitations.json</c> and <c>harness/config/case-matrix.json</c> are only ever
/// read here.
/// </summary>
public sealed partial class KnownLimitationsRegisterTests : IDisposable
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

    // Stands for a register file's per-test temporary path in the messages a failure shows; no message holds it.
    private const string RegisterFileToken = "<register-file>";

    // Stands for the root of Temp, which differs in every run, in the details and settings a failure shows.
    private const string TempRootToken = "<temp-root>";

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

    // The temporary directory of the facade TRX, JADE-unchanged and ordering-evidence tests. Created on
    // first use, so the class's other tests never create it; removed, with any repository in it, in Dispose.
    private TempDirectory? _temp;

    /// <summary>This test's own temporary directory, created on first use.</summary>
    private TempDirectory Temp => _temp ??= new TempDirectory();

    /// <summary>
    /// <paramref name="text"/> with <see cref="Temp"/>'s root replaced by <see cref="TempRootToken"/>, which no
    /// check prints, so a failure message reads the same in every run. A comparison with a literal that holds
    /// neither, or of two texts redacted alike, holds exactly when the raw one does. A test that never created
    /// <see cref="Temp"/> has nothing to redact, and the call does not create it.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    private string? Redacted(string? text) =>
        _temp is null ? text : text?.Replace(_temp.Root, TempRootToken, StringComparison.Ordinal);

    /// <summary>
    /// Deletes the temporary directory and any repository in it, if this test created one, and disposes the
    /// console and deletes the report directory, where a summary mode test created them.
    /// </summary>
    public void Dispose()
    {
        _temp?.Dispose();
        _summaryConsole?.Dispose();
        _summaryConsoleBytes?.Dispose();
        _summaryRepoRoot?.Dispose();
    }

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
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"bogus","matches":[]}]}""", Label = "unknown category")]
    [InlineData("""{"version":1,"entries":{"id":"KL-X","category":"check"}}""", Label = "entries not an array")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check"},{"id":"KL-X","category":"check"}]}""", Label = "duplicate id")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","matches":[{"itemId":"CHK-ORDERING","nature":" "}]}]}""", Label = "blank nature")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","matches":[],"matches":[]}]}""", Label = "duplicate member name")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","attempts":-1}]}""", Label = "negative attempts")]
    [InlineData("""{"version":2,"entries":[]}""", Label = "unsupported version")]
    [InlineData("""{"version":1,"entries":[""", Label = "truncated document")]
    public void Parse_InvalidContent_Throws(string json)
    {
        Assert.Throws<InvalidDataException>(() => KnownLimitationsRegister.Parse(json));
    }

    // The JSON is valid, but each \u escape of a lone surrogate has no UTF-16 string value.
    [Theory]
    [InlineData("""{"version":1,"entries":[{"id":"KL-\uD800","category":"check"}]}""", "entries[0]: 'id' is not valid UTF-16 text: ", Label = "id high surrogate")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check\uD800"}]}""", "entries[0] (KL-X): 'category' is not valid UTF-16 text: ", Label = "category high surrogate")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","operation":"\uDC00"}]}""", "entries[0] (KL-X): 'operation' is not valid UTF-16 text: ", Label = "operation low surrogate")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","reason":"a\uDC00b"}]}""", "entries[0] (KL-X): 'reason' is not valid UTF-16 text: ", Label = "reason low surrogate inside text")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","reason":"\uDC00\uD800"}]}""", "entries[0] (KL-X): 'reason' is not valid UTF-16 text: ", Label = "reason low then high surrogate")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","actionTaken":"\uDC00"}]}""", "entries[0] (KL-X): 'actionTaken' is not valid UTF-16 text: ", Label = "actionTaken low surrogate")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-A","category":"check"},{"id":"KL-X","category":"check","matches":[{"itemId":"CHK-ORDERING","nature":"not-run"},{"itemId":"CHK-\uD800","nature":"not-run"}]}]}""", "entries[1] (KL-X): 'matches[1].itemId' is not valid UTF-16 text: ", Label = "second entry's second itemId high surrogate")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","matches":[{"itemId":"CHK-ORDERING","nature":"d:\uDC00"}]}]}""", "entries[0] (KL-X): 'matches[0].nature' is not valid UTF-16 text: ", Label = "nature low surrogate")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","matches":[{"itemId":"CHK-ORDERING","nature":"d:\uD800\u0041"}]}]}""", "entries[0] (KL-X): 'matches[0].nature' is not valid UTF-16 text: ", Label = "nature high surrogate then letter")]
    [InlineData("""{"version":1,"entries":[{"id":"KL-X","category":"check","note\uD800":"x"}]}""", "a member name is not valid UTF-16 text: ", Label = "member name high surrogate")]
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

            // The message is shown with the temporary path replaced by a fixed token.
            var message = exception.Message.Replace(path, RegisterFileToken, StringComparison.Ordinal);
            Assert.StartsWith(RegisterFileToken + ": " + RegisterErrorPrefix + "not valid UTF-8: ", message, StringComparison.Ordinal);
            Assert.DoesNotContain("\uFFFD", message, StringComparison.Ordinal);

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

            // The message is shown with the temporary path replaced by a fixed token.
            Assert.StartsWith(
                RegisterFileToken + ": " + RegisterErrorPrefix + "entries[0] (KL-X): 'matches[0].nature' is not valid UTF-16 text: ",
                exception.Message.Replace(path, RegisterFileToken, StringComparison.Ordinal),
                StringComparison.Ordinal);
            var parsing = Assert.IsType<InvalidDataException>(exception.InnerException);
            Assert.IsType<InvalidOperationException>(parsing.InnerException);
        });
    }

    // A FIFO nobody writes to, a link to one or to a valid register outside the root, and a sparse file over the
    // evidence limit are each refused at once, before any byte is parsed, so nothing blocks a run's start.
    [Theory]
    [InlineData("fifo")]
    [InlineData("link-to-fifo")]
    [InlineData("link-to-file")]
    [InlineData("oversized")]
    public async Task Load_RegisterThatIsSpecialLinkedOrOversized_IsRefusedPromptly(string kind)
    {
        const string relative = "harness/config/known-limitations.json";
        var ct = TestContext.Current.CancellationToken;
        var path = Temp.PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PlantRefusedFile(path, kind, HarnessOptions.EvidenceByteLimit, """{"version":1,"entries":[]}""");

        var refusal = await Assert.ThrowsAsync<IOException>(
            () => Task.Run(() => KnownLimitationsRegister.Load(path, Temp.Root), ct).WaitAsync(PromptReadLimit, ct));

        Assert.Equal(ConfinedReadRefusal(relative, kind), refusal.Message);
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

    // ---- Results report: build ------------------------------------------------------------------

    /// <summary>Each operation of <see cref="FourResultsRun"/> with the AAP 0.7.7 result it shows.</summary>
    public static TheoryData<string, string> FourResultsByOperation { get; } = new()
    {
        { "Rest.getAgent", OperationResults.Equivalent },
        { "Rest.getClient", OperationResults.DocumentedDifference },
        { "Rest.getCompany", OperationResults.UndocumentedDifference },
        { "Soap.getPhoto", OperationResults.NotCompared },
    };

    [Theory]
    [MemberData(nameof(FourResultsByOperation))]
    public void Build_ClassifiesEachOperation(string operationId, string expected)
    {
        var report = BuildReport(FourResultsRun.Source(), FourResultsRun.Entries());

        Assert.Equal(expected, OperationRowOf(report, operationId).Result);
    }

    [Fact]
    public void Build_CountsEachResult()
    {
        var report = BuildReport(FourResultsRun.Source(), FourResultsRun.Entries());

        Assert.Equal(OperationCatalogue.Default.Operations.Select(operation => operation.Id), report.Operations.Select(row => row.OperationId));
        Assert.Equal(1, report.EquivalentCount);
        Assert.Equal(1, report.DocumentedDifferenceCount);
        Assert.Equal(1, report.UndocumentedDifferenceCount);
        Assert.Equal(19, report.NotComparedCount);
        Assert.Equal(
            ResultsReport.TotalOperations,
            report.EquivalentCount + report.DocumentedDifferenceCount + report.UndocumentedDifferenceCount + report.NotComparedCount);
        Assert.Equal("1 of 22 operations equivalent, 1 documented differences", report.FinalLine);

        var client = OperationRowOf(report, "Rest.getClient");
        Assert.Equal((2, 2, 2, 1), (client.Planned, client.Captured, client.Compared, client.EquivalentCases));
        Assert.Equal("CASE:" + FourResultsRun.ClientDifferingCaseId, Assert.Single(client.FailingItems).ItemId);
    }

    [Theory]
    [InlineData(true, OperationResults.DocumentedDifference)]
    [InlineData(false, OperationResults.UndocumentedDifference)]
    public void Build_PartlyCapturedOperation_IsDocumentedOnlyWhenItsNotCapturedCaseIs(bool documented, string expected)
    {
        var source = new ReportSourceFixture
        {
            Cases = [EquivalentCase("REST-getAgent-01", "Rest.getAgent")],
            PlannedCasesByOperation = Plan(("Rest.getAgent", ["REST-getAgent-01", "REST-getAgent-02"])),
            Baseline = FixtureBaseline with { NotCaptured = ["REST-getAgent-02"] },
        };
        RegisterEntry[] entries = documented ? [Entry(NotCapturedPair("REST-getAgent-02"))] : [];

        var report = BuildReport(source, entries);

        var row = OperationRowOf(report, "Rest.getAgent");
        Assert.Equal(expected, row.Result);
        Assert.Equal((2, 1, 1, 1), (row.Planned, row.Captured, row.Compared, row.EquivalentCases));
        Assert.Equal(0, report.EquivalentCount);
        Assert.Equal(documented ? 1 : 0, report.DocumentedDifferenceCount);
        Assert.Equal(documented ? 0 : 1, report.UndocumentedDifferenceCount);

        // The uncaptured case is a failing item of its operation, never a compared case.
        var notCaptured = Assert.Single(report.Failures);
        Assert.Equal(new FailureItem("CASE:REST-getAgent-02", "not-captured", "Rest.getAgent"), notCaptured.Item);
        Assert.Equal(documented, notCaptured.Documented);
        Assert.Equal(documented, report.AllFailuresDocumented);
    }

    [Fact]
    public void Build_OperationWithNoCapturedCase_IsNotCompared()
    {
        // SOAP-getPhoto-03 is in no plan, so its operation comes from the case id alone.
        var source = new ReportSourceFixture
        {
            PlannedCasesByOperation = Plan(("Rest.getAllAgents", ["REST-getAllAgents-01"])),
            Baseline = FixtureBaseline with { NotCaptured = ["REST-getAllAgents-01", "SOAP-getPhoto-03"] },
        };

        var report = BuildReport(source);

        var row = OperationRowOf(report, "Rest.getAllAgents");
        Assert.Equal(OperationResults.NotCompared, row.Result);
        Assert.Equal((1, 0, 0), (row.Planned, row.Captured, row.Compared));
        Assert.Equal(OperationResults.NotCompared, OperationRowOf(report, "Soap.getPhoto").Result);
        Assert.Equal(
            new[]
            {
                new FailureItem("CASE:REST-getAllAgents-01", "not-captured", "Rest.getAllAgents"),
                new FailureItem("CASE:SOAP-getPhoto-03", "not-captured", "Soap.getPhoto"),
            },
            report.Failures.Select(failure => failure.Item));
        Assert.False(report.AllFailuresDocumented);
        Assert.Equal(ResultsReport.TotalOperations, report.NotComparedCount);
        Assert.Equal("0 of 22 operations equivalent, 0 documented differences", report.FinalLine);
    }

    [Theory]
    [InlineData(true, OperationResults.Equivalent)]
    [InlineData(false, OperationResults.UndocumentedDifference)]
    public void Build_Equivalent_NeedsEveryPlannedCaseCaptured(bool secondCaptured, string expected)
    {
        // No failing item either way; without the second case's result, a planned case was neither
        // captured nor listed as not captured.
        var source = new ReportSourceFixture
        {
            Cases = secondCaptured
                ? [EquivalentCase("REST-getSale-01", "Rest.getSale"), EquivalentCase("REST-getSale-02", "Rest.getSale")]
                : [EquivalentCase("REST-getSale-01", "Rest.getSale")],
            PlannedCasesByOperation = Plan(("Rest.getSale", ["REST-getSale-01", "REST-getSale-02"])),
        };

        var report = BuildReport(source);

        Assert.Empty(report.Failures);
        Assert.Equal(expected, OperationRowOf(report, "Rest.getSale").Result);
    }

    [Theory]
    [InlineData(true, OperationResults.DocumentedDifference)]
    [InlineData(false, OperationResults.UndocumentedDifference)]
    public void Build_DocumentedDifference_NeedsEveryPlannedCaseAccountedFor(bool secondCaptured, string expected)
    {
        // The documented difference is the only failing item either way; without the second case's
        // result, a planned case was neither compared nor reported as a failing item.
        var difference = DifferingCase("REST-getSale-01", "Rest.getSale", Website);
        var source = new ReportSourceFixture
        {
            Cases = secondCaptured ? [difference, EquivalentCase("REST-getSale-02", "Rest.getSale")] : [difference],
            PlannedCasesByOperation = Plan(("Rest.getSale", ["REST-getSale-01", "REST-getSale-02"])),
        };

        var report = BuildReport(source, Entry(CasePair(difference)));

        Assert.True(Assert.Single(report.Failures).Documented);
        Assert.Equal(expected, OperationRowOf(report, "Rest.getSale").Result);
    }

    [Fact]
    public void Build_AllFailuresDocumented_TracksTheRegister()
    {
        var ordering = new FailureItem(FailureItemIds.Ordering, FailureItemIds.NotRun);
        var drift = new FailureItem(FailureItemIds.OpenApiDrift, FailureItemIds.Missing);
        var entry = NamedEntry("KL-TEST-NOGIT", (ordering.ItemId, ordering.Nature));

        // Vacuously true without failing items.
        var none = BuildReport(new ReportSourceFixture(), entry);
        Assert.True(none.AllFailuresDocumented);
        Assert.Empty(none.Failures);
        Assert.Empty(none.UndocumentedFailures);

        var documented = BuildReport(new ReportSourceFixture { Failures = [ordering] }, entry);
        Assert.True(documented.AllFailuresDocumented);
        var reported = Assert.Single(documented.Failures);
        Assert.True(reported.Documented);
        Assert.Equal("KL-TEST-NOGIT", reported.Entry?.Id);
        Assert.Empty(documented.UndocumentedFailures);

        var mixed = BuildReport(new ReportSourceFixture { Failures = [ordering, drift] }, entry);
        Assert.False(mixed.AllFailuresDocumented);
        Assert.Equal(new[] { drift }, mixed.UndocumentedFailures);
        Assert.Collection(
            mixed.Failures,
            failure =>
            {
                Assert.True(failure.Documented);
                Assert.Equal("KL-TEST-NOGIT", failure.Entry?.Id);
            },
            failure =>
            {
                Assert.False(failure.Documented);
                Assert.Null(failure.Entry);
            });
    }

    [Theory]
    [MemberData(nameof(NeverDocumentablePairs))]
    public void Build_NeverDocumentableItem_IsAnUndocumentedFailure(string itemId, string nature)
    {
        // The register lists the item; the documented control shows the register is otherwise honoured.
        var control = new FailureItem(FailureItemIds.Ordering, FailureItemIds.NotRun);
        var item = new FailureItem(itemId, nature);

        var report = BuildReport(
            new ReportSourceFixture { Failures = [control, item] },
            Entry((control.ItemId, control.Nature), (itemId, nature)));

        Assert.Collection(
            report.Failures,
            failure => Assert.True(failure.Documented),
            failure =>
            {
                Assert.Equal(item, failure.Item);
                Assert.False(failure.Documented);
                Assert.Null(failure.Entry);
            });
        Assert.Equal(new[] { item }, report.UndocumentedFailures);
        Assert.False(report.AllFailuresDocumented);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Build_DifferingCaseWithoutNature_GetsItsSignatureNature(string? suppliedNature)
    {
        var result = DifferingCase("REST-getCompany-01", "Rest.getCompany", Website, Fax) with { ItemNature = suppliedNature };
        var expectedNature = "d:" + DiscrepancySignature.Compute([Website, Fax]);
        var source = new ReportSourceFixture
        {
            Cases = [result],
            PlannedCasesByOperation = Plan(("Rest.getCompany", ["REST-getCompany-01"])),
        };

        var report = BuildReport(source, Entry(("CASE:REST-getCompany-01", expectedNature)));

        var failure = Assert.Single(report.Failures);
        Assert.Equal(new FailureItem("CASE:REST-getCompany-01", expectedNature, "Rest.getCompany"), failure.Item);
        Assert.True(failure.Documented);
        Assert.Equal(OperationResults.DocumentedDifference, OperationRowOf(report, "Rest.getCompany").Result);
    }

    [Fact]
    public void Build_DeduplicatesFailuresByItemIdAndNature()
    {
        var company = DifferingCase("REST-getCompany-01", "Rest.getCompany", Website);
        var sale = DifferingCase("REST-getSale-01", "Rest.getSale", Fax);
        var drift = new FailureItem(FailureItemIds.OpenApiDrift, FailureItemIds.Missing, Detail: "first");
        var photoNotCaptured = new FailureItem("CASE:SOAP-getPhoto-01", FailureItemIds.NotCaptured);
        var companyFromSource = new FailureItem("CASE:REST-getCompany-01", company.ItemNature!, Detail: "from the source");
        var source = new ReportSourceFixture
        {
            Failures =
            [
                drift,
                photoNotCaptured,
                drift with { Detail = "second" },
                companyFromSource,
                new FailureItem(FailureItemIds.OpenApiDrift, FailureItemIds.NotRun),
                photoNotCaptured,
            ],
            Cases = [company, sale],
            PlannedCasesByOperation = Plan(
                ("Rest.getCompany", ["REST-getCompany-01"]),
                ("Rest.getSale", ["REST-getSale-01"]),
                ("Soap.getPhoto", ["SOAP-getPhoto-01", "SOAP-getPhoto-02"])),
            Baseline = FixtureBaseline with { NotCaptured = ["SOAP-getPhoto-01", "SOAP-getPhoto-02"] },
        };

        var report = BuildReport(source);

        // The source's items first, in source order and first occurrence kept, case items attributed
        // to their operation; then the not-captured and case items the source did not already hold.
        Assert.Equal(
            new[]
            {
                new FailureItem(FailureItemIds.OpenApiDrift, "missing", Detail: "first"),
                new FailureItem("CASE:SOAP-getPhoto-01", "not-captured", "Soap.getPhoto"),
                new FailureItem("CASE:REST-getCompany-01", company.ItemNature!, "Rest.getCompany", "from the source"),
                new FailureItem(FailureItemIds.OpenApiDrift, "not-run"),
                new FailureItem("CASE:SOAP-getPhoto-02", "not-captured", "Soap.getPhoto"),
                new FailureItem("CASE:REST-getSale-01", sale.ItemNature!, "Rest.getSale"),
            },
            report.Failures.Select(failure => failure.Item));
        Assert.Single(OperationRowOf(report, "Rest.getCompany").FailingItems);
    }

    [Fact]
    public void Build_NotComparedOperation_CountsTowardsNeitherNNorM()
    {
        // The only case of Soap.getPhoto is not captured, and the register documents that item.
        var report = BuildReport(
            CatalogueRun(["Soap.getPhoto"]),
            Entry(NotCapturedPair(CaseIdOf("Soap.getPhoto"))));

        Assert.Equal(OperationResults.NotCompared, OperationRowOf(report, "Soap.getPhoto").Result);
        Assert.True(Assert.Single(report.Failures).Documented);
        Assert.True(report.AllFailuresDocumented);
        Assert.Equal(
            (21, 0, 0, 1),
            (report.EquivalentCount, report.DocumentedDifferenceCount, report.UndocumentedDifferenceCount, report.NotComparedCount));
        Assert.Equal("21 of 22 operations equivalent, 0 documented differences", report.FinalLine);
        Assert.Equal(ResultsReport.FormatFinalLine(report.EquivalentCount, report.DocumentedDifferenceCount), report.FinalLine);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Build_CapturedCaseTheReplayDidNotCompare_LeavesItsOperationAnUndocumentedDifference(int comparedCases)
    {
        // Rest.getSale has three records. The replay compared the first comparedCases of them, each
        // equivalent, and reported nothing for the rest (no comparer, a cancellation or a failed case
        // step), so no failing item names them. Every other operation is captured and equivalent.
        string[] saleCases = ["REST-getSale-01", "REST-getSale-02", "REST-getSale-03"];
        var source = CatalogueRun([]);
        source.Cases =
        [
            .. source.Cases.Where(result => !string.Equals(result.Operation, "Rest.getSale", StringComparison.Ordinal)),
            .. saleCases.Take(comparedCases).Select(caseId => EquivalentCase(caseId, "Rest.getSale")),
        ];
        source.PlannedCasesByOperation = new Dictionary<string, IReadOnlyList<string>>(source.PlannedCasesByOperation, StringComparer.Ordinal)
        {
            ["Rest.getSale"] = saleCases,
        };
        source.CapturedCasesByOperation = CapturedFrom(source.Cases, [.. saleCases.Skip(comparedCases).Select(caseId => ("Rest.getSale", caseId))]);

        var report = BuildReport(source);

        var row = OperationRowOf(report, "Rest.getSale");
        Assert.Equal(OperationResults.UndocumentedDifference, row.Result);
        Assert.Equal((3, 3, comparedCases, comparedCases), (row.Planned, row.Captured, row.Compared, row.EquivalentCases));
        Assert.Equal(saleCases[comparedCases..], row.UnaccountedCases);
        Assert.Equal(saleCases[comparedCases..], report.UnaccountedCases);
        Assert.All(report.Operations.Where(other => other != row), other => Assert.Empty(other.UnaccountedCases));

        // An uncompared case is no failing item, so nothing a register entry could match exists for it,
        // and its operation counts towards neither N nor M.
        Assert.Empty(row.FailingItems);
        Assert.Empty(report.Failures);
        Assert.True(report.AllFailuresDocumented);
        Assert.Equal(
            (21, 0, 1, 0),
            (report.EquivalentCount, report.DocumentedDifferenceCount, report.UndocumentedDifferenceCount, report.NotComparedCount));
        Assert.Equal("21 of 22 operations equivalent, 0 documented differences", report.FinalLine);
    }

    [Fact]
    public void Build_CaptureEvidenceThatMatchesTheResults_ChangesNoResult()
    {
        // The records hold every case the replay compared; Rest.getAllAgents has no record (its only case
        // was not captured) and an empty capture entry.
        var source = CatalogueRun(["Rest.getAllAgents"]);
        var withoutEvidence = BuildReport(source, Entry(NotCapturedPair(CaseIdOf("Rest.getAllAgents"))));
        source.CapturedCasesByOperation = new Dictionary<string, IReadOnlyList<string>>(CapturedFrom(source.Cases), StringComparer.Ordinal)
        {
            ["Rest.getAllAgents"] = [],
        };

        var report = BuildReport(source, Entry(NotCapturedPair(CaseIdOf("Rest.getAllAgents"))));

        var row = OperationRowOf(report, "Rest.getAllAgents");
        Assert.Equal(OperationResults.NotCompared, row.Result);
        Assert.Equal((1, 0, 0), (row.Planned, row.Captured, row.Compared));
        var agent = OperationRowOf(report, "Rest.getAgent");
        Assert.Equal((OperationResults.Equivalent, 1, 1, 1), (agent.Result, agent.Planned, agent.Captured, agent.Compared));
        Assert.Empty(report.UnaccountedCases);
        Assert.Equal(
            withoutEvidence.Operations.Select(other => (other.OperationId, other.Result, other.Planned, other.Captured, other.Compared)),
            report.Operations.Select(other => (other.OperationId, other.Result, other.Planned, other.Captured, other.Compared)));
        Assert.Equal((21, 0, 0, 1), (report.EquivalentCount, report.DocumentedDifferenceCount, report.UndocumentedDifferenceCount, report.NotComparedCount));
        Assert.Equal(RenderReport(withoutEvidence), RenderReport(report));
    }

    [Theory]
    [InlineData(0, 0, "0 of 22 operations equivalent, 0 documented differences")]
    [InlineData(20, 2, "20 of 22 operations equivalent, 2 documented differences")]
    [InlineData(22, 0, "22 of 22 operations equivalent, 0 documented differences")]
    public void FormatFinalLine_StatesNAndM(int n, int m, string expected)
    {
        Assert.Equal(expected, ResultsReport.FormatFinalLine(n, m));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void FormatFinalLine_RejectsNegativeCounts(int n, int m)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ResultsReport.FormatFinalLine(n, m));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3, "N = 19, below 20")]
    public void DemoAcceptance_NeedsTwentyEquivalentOperations(int differing, params string[] expectedShortfall)
    {
        // The differing operations return the baseline status with a well-formed payload, so only the
        // number of equivalent operations decides.
        var report = BuildReport(CatalogueRunEndingInDifferences(differing));

        Assert.Equal(ResultsReport.TotalOperations - differing, report.EquivalentCount);
        Assert.Equal(expectedShortfall.Length == 0, report.DemoAcceptanceMet);
        Assert.Equal(expectedShortfall, report.DemoAcceptanceShortfall);
    }

    [Theory]
    [InlineData("Rest.getCompany", 200, 502, "Rest.getCompany: status differs in REST-getCompany-01 (baseline 200, observed 502)")]
    [InlineData("Rest.getAgent", 404, 200, "Rest.getAgent: status differs in REST-getAgent-01 (baseline 404, observed 200)")]
    [InlineData("Soap.getAgent", 200, null, "Soap.getAgent: status differs in SOAP-getAgent-01 (baseline 200, observed none)")]
    public void DemoAcceptance_StatusDifference_IsAShortfall(string operationId, int baselineStatus, int? observedStatus, string expected)
    {
        var report = BuildReport(CatalogueRun([], StatusMismatchCase(CaseIdOf(operationId), operationId, baselineStatus, observedStatus)));

        Assert.Equal(21, report.EquivalentCount);
        Assert.False(report.DemoAcceptanceMet);
        Assert.Equal(expected, Assert.Single(report.DemoAcceptanceShortfall));
    }

    [Fact]
    public void DemoAcceptance_MalformedPayload_IsAShortfall()
    {
        var report = BuildReport(CatalogueRun([], MalformedCase(CaseIdOf("Soap.getAgent"), "Soap.getAgent")));

        Assert.Equal(21, report.EquivalentCount);
        Assert.False(report.DemoAcceptanceMet);
        Assert.Equal("Soap.getAgent: malformed payload in SOAP-getAgent-01", Assert.Single(report.DemoAcceptanceShortfall));
    }

    [Fact]
    public void DemoAcceptance_ListsTheShortfallInCatalogueOrder()
    {
        var statusAndMalformed = StatusMismatchCase(CaseIdOf("Rest.getCompany"), "Rest.getCompany", 200, 502) with { WellFormed = false };

        var report = BuildReport(CatalogueRun(
            ["Rest.getAgent"],
            StatusMismatchCase(CaseIdOf("Soap.getAgent"), "Soap.getAgent", 200, null),
            statusAndMalformed));

        Assert.Equal(19, report.EquivalentCount);
        Assert.False(report.DemoAcceptanceMet);
        Assert.Equal(
            new[]
            {
                "N = 19, below 20",
                "Rest.getAgent: not compared",
                "Rest.getCompany: status differs in REST-getCompany-01 (baseline 200, observed 502)",
                "Rest.getCompany: malformed payload in REST-getCompany-01",
                "Soap.getAgent: status differs in SOAP-getAgent-01 (baseline 200, observed none)",
            },
            report.DemoAcceptanceShortfall);
    }

    [Fact]
    public void DemoAcceptance_EveryPlannedOrCapturedCaseNeedsAComparedResponse()
    {
        // 20 operations are equivalent. The other two return the baseline status with a well-formed payload
        // in every case that was compared, but Rest.getCompany's second planned case was not captured, and of
        // Rest.getSale's three planned cases the second has a record the replay never compared and the third
        // was not captured. Neither missing response is evidence of the baseline status.
        var company = DifferingCase(CaseIdOf("Rest.getCompany"), "Rest.getCompany", Website);
        var source = CatalogueRun([], company);
        source.PlannedCasesByOperation = new Dictionary<string, IReadOnlyList<string>>(source.PlannedCasesByOperation, StringComparer.Ordinal)
        {
            ["Rest.getCompany"] = ["REST-getCompany-01", "REST-getCompany-02"],
            ["Rest.getSale"] = ["REST-getSale-01", "REST-getSale-02", "REST-getSale-03"],
        };
        source.CapturedCasesByOperation = CapturedFrom(source.Cases, ("Rest.getSale", "REST-getSale-02"));
        source.Baseline = source.Baseline with { NotCaptured = ["REST-getCompany-02", "REST-getSale-03"] };

        var report = BuildReport(
            source,
            Entry(CasePair(company), NotCapturedPair("REST-getCompany-02"), NotCapturedPair("REST-getSale-03")));

        Assert.Equal(OperationResults.DocumentedDifference, OperationRowOf(report, "Rest.getCompany").Result);
        Assert.Equal(OperationResults.UndocumentedDifference, OperationRowOf(report, "Rest.getSale").Result);
        Assert.Equal(20, report.EquivalentCount);
        Assert.False(report.DemoAcceptanceMet);
        Assert.Equal(
            new[]
            {
                "Rest.getCompany: no compared response for REST-getCompany-02 (not captured)",
                "Rest.getSale: no compared response for REST-getSale-02 (captured, not compared)",
                "Rest.getSale: no compared response for REST-getSale-03 (not captured)",
            },
            report.DemoAcceptanceShortfall);
    }

    /// <summary>Each change <see cref="MutableReportRun.Change"/> makes to the source after a build.</summary>
    public static TheoryData<string> SourceChangesAfterBuild { get; } = new()
    {
        MutableReportRun.GapCountChanged,
        MutableReportRun.GapNatureAdded,
        MutableReportRun.FailuresCleared,
        MutableReportRun.FailureAppended,
        MutableReportRun.CaseReplaced,
        MutableReportRun.CasesCleared,
        MutableReportRun.DiscrepancyAppended,
        MutableReportRun.NotCapturedCaseAppended,
        MutableReportRun.PlannedOperationAdded,
        MutableReportRun.PlannedCaseAppended,
        MutableReportRun.CapturedCaseAppended,
        MutableReportRun.ChecksCleared,
    };

    /// <summary>Each collection a built report holds, as <see cref="ChangeAttempts"/> names it.</summary>
    public static TheoryData<string> ReportCollections { get; } = new()
    {
        "Failures",
        "UndocumentedFailures",
        "UnaccountedCases",
        "DemoAcceptanceShortfall",
        "Operations",
        "Operations[].FailingItems",
        "Operations[].UnaccountedCases",
        "Cases",
        "Cases[].Discrepancies",
        "NotCaptured",
        "Baseline.NotCaptured",
        "Conformance.GapsByNature",
        "Checks",
    };

    [Theory]
    [MemberData(nameof(SourceChangesAfterBuild))]
    public void Build_SnapshotsTheSource_SoALaterChangeAltersNeitherTextNorDecisions(string change)
    {
        var run = new MutableReportRun();
        var report = BuildReport(run.Source, FourResultsRun.Entries());
        var text = RenderReport(report);
        var decisions = DecisionsOf(report);
        var held = HeldCollectionsOf(report);

        run.Change(change);

        Assert.Equal(text, RenderReport(report));
        Assert.Equal(decisions, DecisionsOf(report));
        Assert.Equal(held, HeldCollectionsOf(report));

        // A new build does see the change, so the comparisons above are not vacuous.
        var rebuilt = BuildReport(run.Source, FourResultsRun.Entries());
        Assert.False(
            string.Equals(text, RenderReport(rebuilt), StringComparison.Ordinal)
                && decisions.SequenceEqual(DecisionsOf(rebuilt))
                && held.SequenceEqual(HeldCollectionsOf(rebuilt)),
            "The source change alters nothing a new build reads.");
    }

    [Theory]
    [MemberData(nameof(ReportCollections))]
    public void Build_ReportCollections_RejectChangesThroughCasts(string collection)
    {
        var report = BuildReport(new MutableReportRun().Source, FourResultsRun.Entries());
        var text = RenderReport(report);
        var decisions = DecisionsOf(report);
        var held = HeldCollectionsOf(report);

        foreach (var attempt in ChangeAttempts(report, collection))
        {
            Assert.Throws<NotSupportedException>(attempt);
        }

        Assert.Equal(text, RenderReport(report));
        Assert.Equal(decisions, DecisionsOf(report));
        Assert.Equal(held, HeldCollectionsOf(report));
    }

    /// <summary>
    /// The report's decisions as text: N, M, whether every failure is documented, the final line, every
    /// failure with its documentation, the undocumented failures, the captured cases not compared, the
    /// demo shortfall and each operation row.
    /// </summary>
    private static string[] DecisionsOf(ResultsReport report) =>
    [
        "N = " + Number(report.EquivalentCount),
        "M = " + Number(report.DocumentedDifferenceCount),
        "all failures documented: " + report.AllFailuresDocumented,
        "final line: " + report.FinalLine,
        .. report.Failures.Select(failure =>
            $"failure {failure.Item.ItemId} {failure.Item.Nature} {failure.Item.Operation} documented: {failure.Documented} by {failure.Entry?.Id}"),
        .. report.UndocumentedFailures.Select(item => $"undocumented {item.ItemId} {item.Nature}"),
        .. report.UnaccountedCases.Select(caseId => "not compared: " + caseId),
        .. report.DemoAcceptanceShortfall.Select(line => "shortfall: " + line),
        .. report.Operations.Select(row =>
            $"{row.OperationId}: {row.Result}, planned {Number(row.Planned)}, captured {Number(row.Captured)}, failing {string.Join(" ", row.FailingItems.Select(item => item.ItemId))}, not compared {string.Join(" ", row.UnaccountedCases)}"),
    ];

    /// <summary>
    /// The collections the report holds for rendering, as text: each case with its discrepancies, the
    /// not-captured ids of the report and of its baseline facts, the conformance gaps and the checks.
    /// </summary>
    private static string[] HeldCollectionsOf(ResultsReport report) =>
    [
        .. report.Cases.Select(result =>
            $"case {result.CaseId}: {string.Join(", ", result.Discrepancies.Select(discrepancy => discrepancy.Path + " " + discrepancy.Kind))}"),
        .. report.NotCaptured.Select(id => "not captured: " + id),
        .. report.Baseline.NotCaptured.Select(id => "baseline not captured: " + id),
        .. (report.Conformance?.GapsByNature ?? new Dictionary<string, int>()).Select(gap => $"gap {gap.Key}: {Number(gap.Value)}"),
        .. report.Checks.Select(check =>
            $"check {check.Key}: {check.Value.Outcome} {Number(check.Value.Passed)} of {Number(check.Value.Cases)}"),
    ];

    /// <summary>Every way of changing the named collection of <paramref name="report"/> through a cast.</summary>
    /// <exception cref="ArgumentOutOfRangeException">No collection has that name.</exception>
    private static Action[] ChangeAttempts(ResultsReport report, string collection) => collection switch
    {
        "Failures" => ListChangeAttempts(report.Failures),
        "UndocumentedFailures" => ListChangeAttempts(report.UndocumentedFailures),
        "UnaccountedCases" => ListChangeAttempts(report.UnaccountedCases),
        "DemoAcceptanceShortfall" => ListChangeAttempts(report.DemoAcceptanceShortfall),
        "Operations" => ListChangeAttempts(report.Operations),
        "Operations[].FailingItems" => ListChangeAttempts(OperationRowOf(report, "Rest.getCompany").FailingItems),
        "Operations[].UnaccountedCases" => ListChangeAttempts(OperationRowOf(report, "Rest.getCompany").UnaccountedCases),
        "Cases" => ListChangeAttempts(report.Cases),
        "Cases[].Discrepancies" => ListChangeAttempts(
            Assert.Single(report.Cases, result => string.Equals(result.CaseId, FourResultsRun.CompanyCaseId, StringComparison.Ordinal)).Discrepancies),
        "NotCaptured" => ListChangeAttempts(report.NotCaptured),
        "Baseline.NotCaptured" => ListChangeAttempts(report.Baseline.NotCaptured),
        "Conformance.GapsByNature" => DictionaryChangeAttempts(
            report.Conformance?.GapsByNature ?? throw new InvalidOperationException("The report holds no conformance counts.")),
        "Checks" => DictionaryChangeAttempts(report.Checks),
        _ => throw new ArgumentOutOfRangeException(nameof(collection), collection, "No report collection has this name."),
    };

    /// <summary>Replacing, adding, inserting, removing and clearing through <see cref="IList{T}"/>; the list must not be empty.</summary>
    private static Action[] ListChangeAttempts<T>(IReadOnlyList<T> list)
    {
        Assert.NotEmpty(list);
        var first = list[0];
        return
        [
            () => ((IList<T>)list)[0] = first,
            () => ((IList<T>)list).Add(first),
            () => ((IList<T>)list).Insert(0, first),
            () => ((IList<T>)list).Remove(first),
            () => ((IList<T>)list).RemoveAt(0),
            () => ((IList<T>)list).Clear(),
        ];
    }

    /// <summary>Replacing, adding, removing and clearing through <see cref="IDictionary{TKey, TValue}"/>; the dictionary must not be empty.</summary>
    private static Action[] DictionaryChangeAttempts<TValue>(IReadOnlyDictionary<string, TValue> map)
    {
        const string addedKey = "added-by-test";
        Assert.NotEmpty(map);
        Assert.DoesNotContain(addedKey, map.Keys);
        var (key, value) = map.First();
        return
        [
            () => ((IDictionary<string, TValue>)map)[key] = value,
            () => ((IDictionary<string, TValue>)map).Add(addedKey, value),
            () => ((IDictionary<string, TValue>)map).Remove(key),
            () => ((IDictionary<string, TValue>)map).Clear(),
        ];
    }

    /// <summary>
    /// <see cref="FourResultsRun"/> with two checks, two conformance gap natures and capture evidence for
    /// every case result plus <see cref="CompanyUncomparedCaseId"/>, a record the replay reported no result
    /// for. Every list and dictionary of the source, nested ones included, is a mutable instance this run
    /// keeps and <see cref="Change"/> can alter after a build.
    /// </summary>
    private sealed class MutableReportRun
    {
        public const string GapCountChanged = "gap count changed";
        public const string GapNatureAdded = "gap nature added";
        public const string FailuresCleared = "failures cleared";
        public const string FailureAppended = "failure appended";
        public const string CaseReplaced = "case replaced";
        public const string CasesCleared = "cases cleared";
        public const string DiscrepancyAppended = "discrepancy appended";
        public const string NotCapturedCaseAppended = "not-captured case appended";
        public const string PlannedOperationAdded = "planned operation added";
        public const string PlannedCaseAppended = "planned case appended";
        public const string CapturedCaseAppended = "captured case appended";
        public const string ChecksCleared = "checks cleared";

        /// <summary>The <c>Rest.getCompany</c> case that has a record but no case result.</summary>
        public const string CompanyUncomparedCaseId = "REST-getCompany-03";

        private const string CompanyOperation = "Rest.getCompany";
        private const string AgentOperation = "Rest.getAgent";

        private readonly List<FailureItem> failures;
        private readonly List<CaseResult> cases;
        private readonly List<Discrepancy> companyDiscrepancies;
        private readonly Dictionary<string, IReadOnlyList<string>> planned;
        private readonly List<string> companyPlan;
        private readonly List<string> agentCaptured;
        private readonly Dictionary<string, CheckResult> checks;
        private readonly List<string> notCaptured;
        private readonly Dictionary<string, int> gaps;

        public MutableReportRun()
        {
            var source = FourResultsRun.Source();

            failures = [.. source.Failures];
            cases = source.Cases.Select(result => result with { Discrepancies = result.Discrepancies.ToList() }).ToList();
            companyDiscrepancies = (List<Discrepancy>)cases
                .Single(result => string.Equals(result.CaseId, FourResultsRun.CompanyCaseId, StringComparison.Ordinal))
                .Discrepancies;
            planned = source.PlannedCasesByOperation.ToDictionary(
                operation => operation.Key,
                operation => (IReadOnlyList<string>)operation.Value.ToList(),
                StringComparer.Ordinal);
            companyPlan = (List<string>)planned[CompanyOperation];
            var captured = CapturedFrom(cases, (CompanyOperation, CompanyUncomparedCaseId)).ToDictionary(
                operation => operation.Key,
                operation => (IReadOnlyList<string>)operation.Value.ToList(),
                StringComparer.Ordinal);
            agentCaptured = (List<string>)captured[AgentOperation];
            checks = new Dictionary<string, CheckResult>(StringComparer.Ordinal)
            {
                [CheckNames.Routing] = new(CheckNames.Routing, CheckOutcomes.Pass, 2, 2, null),
                [CheckNames.OpenApiDrift] = new(CheckNames.OpenApiDrift, CheckOutcomes.Pass, 0, 0, null),
            };
            notCaptured = [.. source.Baseline.NotCaptured];
            gaps = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["unobserved"] = 1,
                ["unreachable-in-source"] = 9,
            };

            source.Failures = failures;
            source.Cases = cases;
            source.PlannedCasesByOperation = planned;
            source.CapturedCasesByOperation = captured;
            source.Checks = checks;
            source.Baseline = source.Baseline with { NotCaptured = notCaptured };
            source.Conformance = new ConformanceSummary(40, 30, gaps);
            Source = source;
        }

        /// <summary>The source, whose collections are the instances <see cref="Change"/> alters.</summary>
        public ReportSourceFixture Source { get; }

        /// <summary>Alters one collection of <see cref="Source"/> in place.</summary>
        /// <param name="change">One of this class's change names.</param>
        /// <exception cref="ArgumentOutOfRangeException">No change has that name.</exception>
        public void Change(string change)
        {
            switch (change)
            {
                case GapCountChanged:
                    gaps["unobserved"] += 1;
                    break;
                case GapNatureAdded:
                    gaps["null-only"] = 2;
                    break;
                case FailuresCleared:
                    failures.Clear();
                    break;
                case FailureAppended:
                    failures.Add(new FailureItem(FailureItemIds.OpenApiDrift, FailureItemIds.Missing));
                    break;
                case CaseReplaced:
                    cases[0] = DifferingCase(cases[0].CaseId, cases[0].Operation, Website);
                    break;
                case CasesCleared:
                    cases.Clear();
                    break;
                case DiscrepancyAppended:
                    companyDiscrepancies.Add(Website);
                    break;
                case NotCapturedCaseAppended:
                    notCaptured.Add("REST-getSale-01");
                    break;
                case PlannedOperationAdded:
                    planned.Add("Rest.getSale", new List<string> { "REST-getSale-01" });
                    break;
                case PlannedCaseAppended:
                    companyPlan.Add("REST-getCompany-02");
                    break;
                case CapturedCaseAppended:
                    agentCaptured.Add("REST-getAgent-03");
                    break;
                case ChecksCleared:
                    checks.Clear();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(change), change, "No source change has this name.");
            }
        }
    }

    // ---- Results report: rendering --------------------------------------------------------------

    [Fact]
    public void Render_EmitsEverySectionOnceInOrder()
    {
        var text = RenderReport(BuildReport(FourResultsRun.Source(), FourResultsRun.Entries()));

        // Every heading and banner line of the report, in order: nothing missing, repeated or extra.
        Assert.Equal(
            new[]
            {
                "# Erewhon strangler facade — results",
                "> Backend used: JADE stand-in (`jade-standin`), not live JADE. Baseline and stand-in responses are stand-in output.",
                "## Run facts",
                "## Counts",
                "## Per-operation results",
                "## Demo acceptance",
                "## Differences",
                "### Equivalence cases",
                "### Other failing items",
                "## Known limitations",
                "## How to run",
            },
            Lines(text).Where(line => line.StartsWith('#') || line.StartsWith('>')));
        Assert.Contains("docker compose up --build", SectionLines(text, "## How to run"));
    }

    [Fact]
    public void Render_HowToRun_StatesPrerequisitesConfigurationAndWhatEachCommandWrites()
    {
        var lines = NonBlank(SectionLines(RenderReport(BuildReport(FourResultsRun.Source(), FourResultsRun.Entries())), "## How to run"));

        // The prerequisites lead the section, before any command.
        Assert.Equal("**Prerequisites.**", lines[0]);
        Assert.StartsWith("- Container mode: Docker with Docker Compose v2.20 or later", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("- Local-process mode and the `dotnet test` commands: the .NET SDK 10.0.403 exactly", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("- Local-process mode also: Linux with Bash 4.4 or later", lines[3], StringComparison.Ordinal);

        // One configuration row per public variable, in order, with its type and both defaults.
        var rows = TableBody(lines).Select(row => row[2..^2].Split(" | ")).ToArray();
        Assert.Equal(
            new[]
            {
                ("`COMPOSE_PROFILES`", "profile list", "`standin` (from `.env`)", "not read"),
                ("`BACKEND_REST_URL`", "URL", "`http://standin:65000` (from `.env`; no Compose default)", "`http://127.0.0.1:15000`, the stand-in the script starts (also when empty)"),
                ("`BACKEND_SOAP_URL`", "URL", "`http://standin:21001` (from `.env`; no Compose default)", "`http://127.0.0.1:15001`, the stand-in the script starts (also when empty)"),
                ("`BACKEND_ADMIN_URL`", "URL, or explicitly empty", "`http://standin:9090` (from `.env`; also when unset)", "`http://127.0.0.1:15090` (when unset)"),
                ("`BACKEND_TIMEOUT_SECONDS`", "integer seconds, greater than 0", "`30` (from `.env`; also when unset or blank)", "`30` (also when unset or blank)"),
                ("`HARNESS_REPORT_PATH`", "file path", "`/repo/RESULTS.md` (Compose default; also when blank; `/repo` is the checkout's bind mount)", "`<repository>/RESULTS.md` (also when blank)"),
            },
            rows.Select(cells => (cells[0], cells[1], cells[2], cells[3])));
        var purposes = rows.ToDictionary(cells => cells[0], cells => Assert.Single(cells[4..]), StringComparer.Ordinal);
        Assert.Contains("no state reset, no seed confirmation and no backend shutdown by the harness (KL-SUB-NORESET)", purposes["`BACKEND_ADMIN_URL`"], StringComparison.Ordinal);
        Assert.Contains("REST callers get status 504 \"Backend timed out\" and SOAP callers a SOAP Fault \"Backend timed out\"", purposes["`BACKEND_TIMEOUT_SECONDS`"], StringComparison.Ordinal);
        Assert.Contains("must lie strictly below the repository root (below `/repo` under Docker): replay refuses any other path as a failed report write (CHK-REPORT) and exits 1", purposes["`HARNESS_REPORT_PATH`"], StringComparison.Ordinal);

        // Every required command, each exactly once and in this order; a bold lead stands for its paragraph.
        string[] ordered =
        [
            "docker compose up --build",
            "COMPOSE_PROFILES= BACKEND_REST_URL=http://<host>:65000 BACKEND_SOAP_URL=http://<host>:21001 BACKEND_ADMIN_URL= docker compose up --build",
            "**Configuration.**",
            "**Routine verification without Docker.**",
            "harness/scripts/run-local.sh replay",
            "**Deliberate evidence regeneration.**",
            "harness/scripts/run-local.sh capture",
            "harness/scripts/run-local.sh export-openapi",
            "(cd facade && dotnet test --project tests/Erewhon.Facade.Tests/Erewhon.Facade.Tests.csproj)",
            "(cd harness && dotnet test --project tests/Erewhon.Harness.Tests/Erewhon.Harness.Tests.csproj)",
        ];
        var positions = ordered
            .Select(expected => Assert.Single(
                Enumerable.Range(0, lines.Length),
                index => expected.StartsWith("**", StringComparison.Ordinal)
                    ? lines[index].StartsWith(expected + " ", StringComparison.Ordinal)
                    : string.Equals(lines[index], expected, StringComparison.Ordinal)))
            .ToArray();
        Assert.Equal(positions.Order(), positions);
        Assert.Contains("a value set in the shell overrides `.env`", lines[positions[2]], StringComparison.Ordinal);

        // Replay is routine and stands alone in its block; capture and export-openapi follow the warning
        // that names what each replaces.
        var replay = positions[4];
        Assert.Equal(new[] { "```sh", "```" }, new[] { lines[replay - 1], lines[replay + 1] });
        Assert.EndsWith(
            "Apart from build output under each project's `bin/` and `obj/`, and a project's `packages.lock.json`, which its unlocked restore writes only when the file is missing or a package version has changed, it writes only the report (`RESULTS.md`, or `HARNESS_REPORT_PATH`) and `artifacts/**`, never `baseline/**` or a contract file:",
            lines[positions[3]],
            StringComparison.Ordinal);
        var deliberate = lines[positions[5]];
        Assert.Contains("`capture` withdraws `baseline/manifest.json`, clears `baseline/records/**` and rewrites `baseline/**`", deliberate, StringComparison.Ordinal);
        Assert.Contains("writes `contracts/wsdl/ErewhonWebService.wsdl` only when that file is absent", deliberate, StringComparison.Ordinal);
        Assert.Contains("`export-openapi` replaces `contracts/openapi/erewhon-rest.openapi.json` with the live facade's OpenAPI document", deliberate, StringComparison.Ordinal);
        Assert.Contains("Commit or back up the current evidence first", deliberate, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_RunFactsStateTheRun()
    {
        var lines = SectionLines(RenderReport(BuildReport(new ReportSourceFixture())), "## Run facts");

        Assert.Contains(TableRow("Date (UTC)", "2026-01-02T03:04:05Z"), lines);
        Assert.Contains(TableRow("Execution mode", InlineCode("docker")), lines);
        Assert.Contains(TableRow(".NET (facade runtime)", ".NET 10.0.14"), lines);
        Assert.Contains(TableRow("Backend REST URL", InlineCode("http://standin:65000")), lines);
        Assert.Contains(TableRow("Backend admin URL", InlineCode("http://standin:9090")), lines);
        Assert.Contains(TableRow("Starting commit", InlineCode(StartingCommit)), lines);
        Assert.Contains(TableRow("Baseline commit", "not found"), lines);
    }

    [Fact]
    public void Render_UsesLineFeedsOnlyAndEndsWithTheFinalLine()
    {
        // Carriage returns in item details and register prose must not reach the report.
        var source = FourResultsRun.Source();
        source.Failures = [new FailureItem(FailureItemIds.Ordering, FailureItemIds.NotRun, Detail: "first line\r\nsecond line\rthird line")];
        var multiline = NamedEntry("KL-TEST-MULTILINE") with { Reason = "First line\r\nsecond line", ActionTaken = "Recorded\rin the register" };
        var report = BuildReport(source, [.. FourResultsRun.Entries(), multiline]);

        var text = RenderReport(report);

        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.EndsWith("\n\n" + report.FinalLine + "\n", text, StringComparison.Ordinal);
        Assert.False(text.EndsWith("\n\n", StringComparison.Ordinal), "The report ends with more than one line feed.");
        var lines = Lines(text);
        Assert.Equal(report.FinalLine, lines[^2]);
        Assert.Empty(lines[^3]);
    }

    [Fact]
    public void Render_CountsTableStatesTheBaselineFacts()
    {
        var lines = SectionLines(RenderReport(BuildReport(FourResultsRun.Source(), FourResultsRun.Entries())), "## Counts");

        Assert.Equal(
            new[]
            {
                TableRow("Baseline", "operations (planned)", "4"),
                TableRow("Baseline", "cases (planned)", "6"),
                TableRow("Baseline", "operations captured", "3"),
                TableRow("Baseline", "cases captured", "5"),
                TableRow("Baseline", "cases not captured", "1: " + InlineCode(FourResultsRun.PhotoCaseId)),
                TableRow("Baseline", "WSDL record", "captured"),
                TableRow("Baseline", "capture execution", InlineCode("local-processes")),
            },
            lines.Where(line => line.StartsWith("| Baseline |", StringComparison.Ordinal)));
    }

    [Fact]
    public void Render_CountsTableAgreesWithTheReport()
    {
        // One case per operation; every count of a kind differs, so a value in the wrong row shows.
        var source = CatalogueRun(
            ["Soap.getAgent", "Soap.getClient", "Soap.getPhoto"],
            DifferingCase(CaseIdOf("Rest.getClient"), "Rest.getClient", Website),
            DifferingCase(CaseIdOf("Rest.getCompany"), "Rest.getCompany", Fax),
            DifferingCase(CaseIdOf("Rest.getSale"), "Rest.getSale", Website));
        var documented = Assert.Single(source.Cases, result => string.Equals(result.Operation, "Rest.getClient", StringComparison.Ordinal));
        var report = BuildReport(source, Entry(CasePair(documented)));

        var lines = SectionLines(RenderReport(report), "## Counts");

        Assert.Equal(
            (16, 1, 2, 3),
            (report.EquivalentCount, report.DocumentedDifferenceCount, report.UndocumentedDifferenceCount, report.NotComparedCount));
        Assert.Contains(
            TableRow("Baseline", "cases not captured", "3: `SOAP-getAgent-01`, `SOAP-getClient-01`, `SOAP-getPhoto-01`"),
            lines);
        Assert.Equal(
            new[]
            {
                TableRow("Equivalence", "cases compared", "19"),
                TableRow("Equivalence", "cases equivalent", "16"),
                TableRow("Equivalence", "cases with documented difference", "1"),
                TableRow("Equivalence", "cases with undocumented difference", "2"),
                TableRow("Equivalence", "cases not compared", "3"),
                TableRow("Equivalence", "operations equivalent", Number(report.EquivalentCount)),
                TableRow("Equivalence", "operations with documented difference", Number(report.DocumentedDifferenceCount)),
                TableRow("Equivalence", "operations with undocumented difference", Number(report.UndocumentedDifferenceCount)),
                TableRow("Equivalence", "operations not compared", Number(report.NotComparedCount)),
            },
            lines.Where(line => line.StartsWith("| Equivalence |", StringComparison.Ordinal)));
    }

    [Fact]
    public void Render_PerOperationTableFollowsTheCatalogue()
    {
        var report = BuildReport(FourResultsRun.Source(), FourResultsRun.Entries());

        var rows = TableBody(SectionLines(RenderReport(report), "## Per-operation results"));

        var catalogue = OperationCatalogue.Default.Operations;
        Assert.Equal(ResultsReport.TotalOperations, rows.Length);
        for (var index = 0; index < catalogue.Count; index++)
        {
            var operation = catalogue[index];
            var row = report.Operations[index];
            Assert.Equal(
                TableRow(
                    InlineCode(operation.JadeMethod),
                    operation.Surface == Surface.Rest ? "REST" : "SOAP",
                    InlineCode(operation.Id),
                    Number(row.Planned),
                    Number(row.Captured),
                    Number(row.Compared),
                    row.Result),
                rows[index]);
        }

        Assert.Equal(TableRow("`getAgent`", "REST", "`Rest.getAgent`", "2", "2", "2", "equivalent"), rows[0]);
        Assert.Contains(TableRow("`getClient`", "REST", "`Rest.getClient`", "2", "2", "2", "documented difference"), rows);
        Assert.Contains(TableRow("`getCompany`", "REST", "`Rest.getCompany`", "1", "1", "1", "undocumented difference"), rows);
        Assert.Contains(TableRow("`getPhoto`", "SOAP", "`Soap.getPhoto`", "1", "0", "0", "not compared"), rows);
    }

    [Fact]
    public void Render_DifferencesListEveryFailingItem()
    {
        var text = RenderReport(BuildReport(FourResultsRun.Source(), FourResultsRun.Entries()));

        Assert.Empty(NonBlank(SectionLines(text, "## Differences")));
        Assert.Equal(
            new[]
            {
                TableRow(InlineCode(FourResultsRun.PhotoCaseId), "`Soap.getPhoto`", "`not-captured`", "no", NoValue, NoValue, NoValue),
                TableRow(
                    InlineCode(FourResultsRun.ClientDifferingCaseId),
                    "`Rest.getClient`",
                    InlineCode(FourResultsRun.ClientNature),
                    "yes",
                    InlineCode(FourResultsRun.DifferenceEntryId),
                    "3",
                    "`artifacts/discrepancies/CASE:REST-getClient-02.json`"),
                TableRow(
                    InlineCode(FourResultsRun.CompanyCaseId),
                    "`Rest.getCompany`",
                    InlineCode(FourResultsRun.CompanyNature),
                    "no",
                    NoValue,
                    NoValue,
                    "`artifacts/discrepancies/CASE:REST-getCompany-01.json`"),
            },
            TableBody(SectionLines(text, "### Equivalence cases")));
        Assert.Equal(
            TableRow("`CHK-ORDERING`", NoValue, "`not-run`", "yes", InlineCode(FourResultsRun.OrderingEntryId), "0", NoValue, FourResultsRun.OrderingDetail),
            Assert.Single(TableBody(SectionLines(text, "### Other failing items"))));
    }

    [Fact]
    public void Render_CapturedCasesNotCompared_ListsEachUnderDifferences()
    {
        // Two records the replay reported no result for: a second Rest.getSale case and a second
        // Soap.getPhoto case. Every compared case is equivalent, so no failing item exists.
        var source = CatalogueRun([]);
        source.CapturedCasesByOperation = CapturedFrom(
            source.Cases,
            ("Soap.getPhoto", "SOAP-getPhoto-02"),
            ("Rest.getSale", "REST-getSale-02"));

        var text = RenderReport(BuildReport(source));

        Assert.Equal(
            new[] { "## Differences", "### Equivalence cases", "### Captured cases not compared", "### Other failing items", "## Known limitations" },
            Lines(text).SkipWhile(line => line != "## Differences").Where(line => line.StartsWith('#')).Take(5));
        Assert.Equal("None.", Assert.Single(NonBlank(SectionLines(text, "### Equivalence cases"))));
        Assert.Equal(
            new[]
            {
                TableRow("`REST-getSale-02`", "`Rest.getSale`", UnaccountedReason),
                TableRow("`SOAP-getPhoto-02`", "`Soap.getPhoto`", UnaccountedReason),
            },
            TableBody(SectionLines(text, "### Captured cases not compared")));
        Assert.Contains(
            TableRow("`getSale`", "REST", "`Rest.getSale`", "1", "2", "1", "undocumented difference"),
            TableBody(SectionLines(text, "## Per-operation results")));
        Assert.Equal("None.", Assert.Single(NonBlank(SectionLines(text, "### Other failing items"))));
    }

    [Fact]
    public void Render_EveryCapturedCaseCompared_OmitsTheCapturedCasesNotComparedSection()
    {
        var source = CatalogueRun([]);
        var withoutEvidence = RenderReport(BuildReport(source));
        source.CapturedCasesByOperation = CapturedFrom(source.Cases);

        var text = RenderReport(BuildReport(source));

        Assert.DoesNotContain("### Captured cases not compared", Lines(text));
        Assert.Equal(withoutEvidence, text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_KnownLimitations_ListMandatoryEntriesFirst(bool externalBackend)
    {
        // The register holds one mandatory entry, between two others, and no KL-SUB-NORESET.
        var source = FourResultsRun.Source();
        source.ExternalBackend = externalBackend;
        var soapActions = new RegisterEntry(
            KnownLimitationIds.SoapActions, RegisterCategories.Assumption, Operation: null, [], "The SOAP action names are assumed", 0, "Used the JADE default form");
        var photo = new RegisterEntry(
            "KL-TEST-ASSUMPTION", RegisterCategories.Assumption, "Soap.getPhoto", [], "Photo bytes are assumed.", 0, "Used the raw file bytes.");
        var entries = FourResultsRun.Entries();

        var lines = NonBlank(SectionLines(RenderReport(BuildReport(source, entries[0], soapActions, entries[1], photo)), "## Known limitations"));

        var expectedIds = new List<string>(KnownLimitationIds.Mandatory);
        if (externalBackend)
        {
            expectedIds.Add(KnownLimitationIds.SubNoReset);
        }

        expectedIds.AddRange([FourResultsRun.DifferenceEntryId, FourResultsRun.OrderingEntryId, "KL-TEST-ASSUMPTION"]);
        Assert.Equal(expectedIds, lines.Select(LimitationIdOf));

        Assert.StartsWith(BuiltInLimitationPrefix(KnownLimitationIds.BaselineStandIn), lines[0], StringComparison.Ordinal);
        Assert.StartsWith(BuiltInLimitationPrefix(KnownLimitationIds.RestPaths), lines[1], StringComparison.Ordinal);
        Assert.Equal(
            "- **KL-SOAP-ACTIONS** (assumption) — Not done / why: The SOAP action names are assumed. Done instead: Used the JADE default form. Attempts: 0. Documents 0 failing item(s).",
            lines[2]);
        Assert.StartsWith(BuiltInLimitationPrefix(KnownLimitationIds.SoapWsdl), lines[3], StringComparison.Ordinal);
        Assert.StartsWith(BuiltInLimitationPrefix(KnownLimitationIds.JadeTestsNotRun), lines[4], StringComparison.Ordinal);
        if (externalBackend)
        {
            Assert.StartsWith(BuiltInLimitationPrefix(KnownLimitationIds.SubNoReset), lines[5], StringComparison.Ordinal);
        }

        Assert.Equal(
            "- **KL-TEST-DIFFERENCE** (difference; `Rest.getClient`) — Not done / why: The difference persisted after three facade attempts. Done instead: Registered as a documented difference. Attempts: 3. Documents 1 failing item(s).",
            lines[^3]);
        Assert.Equal(
            "- **KL-TEST-NOGIT** (check) — Not done / why: No git metadata in the checkout. Done instead: Reported the ordering check as not run. Attempts: 0. Documents 1 failing item(s).",
            lines[^2]);
        Assert.Equal(
            "- **KL-TEST-ASSUMPTION** (assumption; `Soap.getPhoto`) — Not done / why: Photo bytes are assumed. Done instead: Used the raw file bytes. Attempts: 0. Documents 0 failing item(s).",
            lines[^1]);
    }

    [Fact]
    public void Render_ExternalBackend_BannerShowsTheUnverifiedUrls()
    {
        var source = new ReportSourceFixture
        {
            ExternalBackend = true,
            Facts = FixtureFacts with
            {
                BackendRestUrl = "http://jade-host:65000",
                BackendSoapUrl = "http://jade-host:21001",
                BackendAdminUrl = null,
            },
        };

        var lines = Lines(RenderReport(BuildReport(source)));

        Assert.Equal(
            new[]
            {
                "> Backend used: external backend at `http://jade-host:65000` / `http://jade-host:21001` (unverified).",
                ">",
                "> Baseline responses are JADE stand-in (`jade-standin`) output.",
            },
            lines.Where(line => line.StartsWith('>')));
        Assert.Contains(TableRow("Backend admin URL", "none (external backend: no reset)"), lines);
    }

    /// <summary>
    /// A URL run fact, whether the backend is external (the banner then shows the backend REST and SOAP
    /// URLs), a configured URL holding an at-sign and the report's rendering of it. Every configured user
    /// name and password holds <see cref="CredentialMark"/>.
    /// </summary>
    public static TheoryData<string, bool, string, string> UrlsWithAtSigns { get; } = new()
    {
        // An http or https URL renders as the runtime parses it, without user information: a user name and
        // password, a user name or a password alone, a percent-encoded at-sign, default ports, an IPv6
        // host, surrounding white space, backslashes the runtime reads as slashes and a compatibility
        // at-sign in the path, which the runtime escapes.
        { nameof(RunFacts.TargetRestUrl), false, $"http://{UrlUser}:{UrlPassword}@facade:65000", InlineCode("http://facade:65000/") + UserInformationRemoved },
        { nameof(RunFacts.TargetSoapUrl), false, $"https://{UrlUser}:{UrlPassword}@facade:443/soap", InlineCode("https://facade/soap") + UserInformationRemoved },
        { nameof(RunFacts.BackendRestUrl), false, $"http://{UrlUser}@standin:65000", InlineCode("http://standin:65000/") + UserInformationRemoved },
        { nameof(RunFacts.BackendSoapUrl), false, $"http://:{UrlPassword}@standin:21001", InlineCode("http://standin:21001/") + UserInformationRemoved },
        { nameof(RunFacts.BackendAdminUrl), false, $"http://{UrlUser}:pass%40{CredentialMark}@standin:9090", InlineCode("http://standin:9090/") + UserInformationRemoved },
        { nameof(RunFacts.BackendAdminUrl), false, $"http://{UrlUser}:{UrlPassword}@[::1]:80", InlineCode("http://[::1]/") + UserInformationRemoved },
        { nameof(RunFacts.TargetRestUrl), false, $" http://{UrlUser}:{UrlPassword}@facade:65000\n", InlineCode("http://facade:65000/") + UserInformationRemoved },
        { nameof(RunFacts.BackendRestUrl), true, $"http:\\\\{UrlUser}:{UrlPassword}@jade-host:65000", InlineCode("http://jade-host:65000/") + UserInformationRemoved },
        { nameof(RunFacts.BackendSoapUrl), true, $"https://{UrlUser}:{UrlPassword}@jade-host:21001", InlineCode("https://jade-host:21001/") + UserInformationRemoved },
        { nameof(RunFacts.BackendAdminUrl), false, $"http://{UrlUser}:{UrlPassword}@standin:9090/a\uFF20b", InlineCode("http://standin:9090/a%EF%BC%A0b") + UserInformationRemoved },

        // Any other value shows only what follows its last at-sign: no scheme (the user name parses as the
        // scheme), several at-signs, the compatibility forms U+FF20 and U+FE6B (failing the parse at an
        // ASCII colon, or parsed into the host, lowercased), no "//", another scheme, a port out of range,
        // and nothing after the at-sign.
        { nameof(RunFacts.BackendRestUrl), true, $"{UrlUser}:{UrlPassword}@jade-host:65000", InlineCode("jade-host:65000") + UserInformationRemoved },
        { nameof(RunFacts.BackendSoapUrl), true, $"http://{UrlUser}:p@ss{CredentialMark}@jade-host:21001", InlineCode("jade-host:21001") + UserInformationRemoved },
        { nameof(RunFacts.BackendRestUrl), true, $"http://{UrlUser}:{UrlPassword}\uFF20jade-host:65000", InlineCode("jade-host:65000") + UserInformationRemoved },
        { nameof(RunFacts.BackendSoapUrl), true, $"http://{UrlUser}:{UrlPassword}\uFE6Bjade-host:21001", InlineCode("jade-host:21001") + UserInformationRemoved },
        { nameof(RunFacts.BackendRestUrl), true, $"http://{UrlUser}\uFF20jade-host:65000", InlineCode("jade-host:65000") + UserInformationRemoved },
        { nameof(RunFacts.TargetRestUrl), false, $"http://{UrlUser}\uFE6Bjade-host/x", InlineCode("jade-host/x") + UserInformationRemoved },
        { nameof(RunFacts.BackendSoapUrl), true, $"http://{UrlUser}\uFF1A{UrlPassword}\uFF20jade-host", InlineCode("jade-host") + UserInformationRemoved },
        { nameof(RunFacts.TargetSoapUrl), false, $"https://{CredentialMark.ToUpperInvariant()}\uFF20host/", InlineCode("host/") + UserInformationRemoved },
        { nameof(RunFacts.TargetSoapUrl), false, $"http:{UrlUser}:{UrlPassword}@facade:21001", InlineCode("facade:21001") + UserInformationRemoved },
        { nameof(RunFacts.BackendAdminUrl), false, $"ftp://{UrlUser}:{UrlPassword}@standin:9090", InlineCode("standin:9090") + UserInformationRemoved },
        { nameof(RunFacts.TargetRestUrl), false, $"http://{UrlUser}:{UrlPassword}@facade:99999", InlineCode("facade:99999") + UserInformationRemoved },
        { nameof(RunFacts.TargetSoapUrl), false, $"http://{UrlUser}:{UrlPassword}@", "not shown" + UserInformationRemoved },

        // An at-sign the runtime does not read as user information: the parsed URL, unmarked.
        { nameof(RunFacts.BackendAdminUrl), false, "http://@standin:9090", InlineCode("http://standin:9090/") },
        { nameof(RunFacts.BackendAdminUrl), false, "http://standin:9090/a@b", InlineCode("http://standin:9090/a@b") },
    };

    [Theory]
    [MemberData(nameof(UrlsWithAtSigns))]
    public void Render_UrlRunFacts_NeverShowUserInformation(string fact, bool externalBackend, string configured, string expected)
    {
        var (facts, label) = WithRunFactUrl(fact, configured);
        var source = new ReportSourceFixture { ExternalBackend = externalBackend, Facts = facts };

        var text = RenderReport(BuildReport(source));

        Assert.DoesNotContain(CredentialMark, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TableRow(label, expected), SectionLines(text, "## Run facts"));
        if (externalBackend)
        {
            var rest = fact == nameof(RunFacts.BackendRestUrl) ? expected : InlineCode(FixtureFacts.BackendRestUrl);
            var soap = fact == nameof(RunFacts.BackendSoapUrl) ? expected : InlineCode(FixtureFacts.BackendSoapUrl);
            Assert.Contains($"> Backend used: external backend at {rest} / {soap} (unverified).", Lines(text));
        }
    }

    [Fact]
    public void Write_CredentialedUrls_FileHoldsNoUserInformation()
    {
        var report = BuildReport(CredentialedUrlSource());
        var text = RenderReport(report);

        WithTempDirectory(directory =>
        {
            var writer = new ResultsReportWriter(directory);
            var path = Path.Combine(directory, "RESULTS.md");

            writer.Write(path, text);

            var written = File.ReadAllText(path, Encoding.UTF8);
            Assert.Equal(text, written);
            Assert.DoesNotContain(CredentialMark, written, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                $"> Backend used: external backend at `http://jade-host:65000/`{UserInformationRemoved} / `jade-host:21001`{UserInformationRemoved} (unverified).",
                Lines(written));
        });
    }

    [Fact]
    public void Write_ToADirectory_FailsAndThePrintedTextHoldsNoUserInformation()
    {
        var report = BuildReport(CredentialedUrlSource());
        var text = RenderReport(report);

        // A report path that is a directory below the repository root, as harness/ is in the exit-code
        // verification, fails the write, and the replay prints the rendered text to standard output instead
        // (AAP 0.8.4).
        WithTempDirectory(directory =>
        {
            var writer = new ResultsReportWriter(directory);
            var target = Directory.CreateDirectory(Path.Combine(directory, "harness")).FullName;

            Assert.Throws<IOException>(() => writer.Write(target, text));

            // Entries are named relative to the temporary directory, so a failure reads the same in every run.
            Assert.Empty(Directory.EnumerateFileSystemEntries(target).Select(entry => Path.GetRelativePath(directory, entry)));
            Assert.Equal(
                [Path.GetRelativePath(directory, target)],
                Directory.EnumerateFileSystemEntries(directory).Select(entry => Path.GetRelativePath(directory, entry)));
        });

        Assert.DoesNotContain(CredentialMark, text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, Lines(text).Count(line => line.Contains(UserInformationRemoved, StringComparison.Ordinal) && line.StartsWith('|')));
        Assert.EndsWith("\n\n" + report.FinalLine + "\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, "Met: at least 20 of 22 operations equivalent (20 of 22), and every other operation returns the baseline status code with a well-formed payload.")]
    [InlineData(3, "Not met.", "- N = 19, below 20")]
    public void Render_DemoAcceptance_StatesTheOutcome(int differing, params string[] expected)
    {
        var lines = NonBlank(SectionLines(RenderReport(BuildReport(CatalogueRunEndingInDifferences(differing))), "## Demo acceptance"));

        Assert.Equal(
            "Reported, not enforced: met when at least 20 of 22 operations are equivalent and every other operation returns the baseline status code with a well-formed payload.",
            lines[0]);
        Assert.Equal(expected, lines[1..]);
    }

    [Fact]
    public void Render_EmptySource_ReportsEveryOperationNotCompared()
    {
        var report = BuildReport(new ReportSourceFixture());

        var text = RenderReport(report);

        Assert.True(report.CatalogueAvailable);
        Assert.Equal(OperationCatalogue.Default.Operations.Select(operation => operation.Id), report.Operations.Select(row => row.OperationId));
        var rows = TableBody(SectionLines(text, "## Per-operation results"));
        Assert.Equal(ResultsReport.TotalOperations, rows.Length);
        Assert.All(rows, row => Assert.EndsWith(" | 0 | 0 | 0 | not compared |", row, StringComparison.Ordinal));
        Assert.Equal("None.", Assert.Single(NonBlank(SectionLines(text, "### Equivalence cases"))));
        Assert.Equal("None.", Assert.Single(NonBlank(SectionLines(text, "### Other failing items"))));
        Assert.Equal("0 of 22 operations equivalent, 0 documented differences", report.FinalLine);
        Assert.EndsWith("\n\n0 of 22 operations equivalent, 0 documented differences\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WithoutCatalogue_DerivesRowsFromThePlanAndCases()
    {
        var report = ResultsReport.Build(FourResultsRun.Source(), Register(FourResultsRun.Entries()), catalogue: null);

        var text = RenderReport(report);

        Assert.False(report.CatalogueAvailable);
        var section = SectionLines(text, "## Per-operation results");
        Assert.Contains(CatalogueUnavailableNote, section);
        Assert.Equal(
            new[]
            {
                TableRow("`getAgent`", "REST", "`Rest.getAgent`", "2", "2", "2", "equivalent"),
                TableRow("`getClient`", "REST", "`Rest.getClient`", "2", "2", "2", "documented difference"),
                TableRow("`getCompany`", "REST", "`Rest.getCompany`", "1", "1", "1", "undocumented difference"),
                TableRow("`getPhoto`", "SOAP", "`Soap.getPhoto`", "1", "0", "0", "not compared"),
            },
            TableBody(section));
        Assert.Equal("1 of 22 operations equivalent, 1 documented differences", report.FinalLine);
        Assert.EndsWith("\n\n" + report.FinalLine + "\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WithoutCatalogueOrCases_ListsNoOperations()
    {
        var report = ResultsReport.Build(new ReportSourceFixture(), KnownLimitationsRegister.Empty, catalogue: null);

        var text = RenderReport(report);

        Assert.False(report.CatalogueAvailable);
        Assert.Empty(report.Operations);
        Assert.Equal(new[] { CatalogueUnavailableNote, "None." }, NonBlank(SectionLines(text, "## Per-operation results")));
        Assert.Equal("0 of 22 operations equivalent, 0 documented differences", report.FinalLine);
        Assert.EndsWith("\n\n0 of 22 operations equivalent, 0 documented differences\n", text, StringComparison.Ordinal);
    }

    // Plain text holding every character the report escapes, and no '|', which table rows escape themselves.
    private const string MarkupFreeText = @"a\b `c` *d* _e_ ~f~ <g> &amp; [h](i)";

    // MarkupFreeText as the report must write it, so that it renders as MarkupFreeText.
    private const string EscapedMarkupFreeText = @"a\\b \`c\` \*d\* \_e\_ \~f\~ \<g\> \&amp; \[h\](i)";

    // CommonMark's ASCII punctuation: a backslash before any of these renders as that character.
    private const string CommonMarkAsciiPunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    // The characters that can open inline markup, which the report escapes in plain text.
    private const string EscapedInlineMarkup = "\\`*_~<>&[]";

    [Theory]
    [InlineData(KnownLimitationIds.RestPaths, @"/\<alias\>/jadehttp.dll/\<path\>?ErewhonREST")]
    [InlineData(KnownLimitationIds.SoapActions, @"urn:JadeWebServices/WebServiceHTTP/\<op\>")]
    [InlineData("KL-SOAP-SHAPES", @"\<op\>Response/\<op\>Result")]
    [InlineData("KL-REST-FORMAT", @"\\/Date(ms)\\/")]
    public void Render_CommittedRegister_EscapesPlaceholdersAndBackslashes(string id, string escaped)
    {
        // Unescaped, CommonMark reads <alias>, <path> and <op> as raw HTML and \/ as an escaped slash.
        var register = LoadCommittedRegister();
        var entry = register.FindById(id);
        Assert.NotNull(entry);
        Assert.Contains(DecodeBackslashEscapes(escaped).Text, entry.Reason, StringComparison.Ordinal);

        var line = Assert.Single(CommittedLimitationLines(register), candidate => string.Equals(LimitationIdOf(candidate), id, StringComparison.Ordinal));

        Assert.Contains(escaped, line, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_CommittedRegister_EachLineRendersTheRegisterText()
    {
        var register = LoadCommittedRegister();

        var lines = CommittedLimitationLines(register).Select(DecodeBackslashEscapes).ToArray();

        // Every mandatory entry is in the register, so each entry gives exactly one line and nothing else does.
        Assert.Equal(register.Entries.Count, lines.Length);
        foreach (var entry in register.Entries)
        {
            var operation = string.IsNullOrWhiteSpace(entry.Operation) ? string.Empty : "; " + InlineCode(entry.Operation);
            var line = Assert.Single(lines, candidate => candidate.Text.StartsWith($"- **{entry.Id}** (", StringComparison.Ordinal));

            Assert.Equal(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"- **{entry.Id}** ({entry.Category}{operation}) — Not done / why: {ReportSentence(entry.Reason)} Done instead: {ReportSentence(entry.ActionTaken)} Attempts: {entry.Attempts}. Documents 0 failing item(s)."),
                line.Text);

            // Only the writer's own markup stays unescaped: the bold id, and the operation's code span.
            Assert.Equal(operation.Length == 0 ? "****" : "****``", line.Markup);
        }
    }

    [Fact]
    public void Render_FreeText_RendersAsItself()
    {
        // Each kind of free text the report writes outside a code span holds every escaped character:
        // the facade runtime, a check's outcome and detail, a failing item's detail and a shortfall line.
        // The plan names the case, so its status line is the operation's only shortfall.
        var source = CatalogueRun([], StatusMismatchCase("REST-getAgent-" + MarkupFreeText, "Rest.getAgent", 200, 502));
        source.PlannedCasesByOperation = new Dictionary<string, IReadOnlyList<string>>(source.PlannedCasesByOperation, StringComparer.Ordinal)
        {
            ["Rest.getAgent"] = ["REST-getAgent-" + MarkupFreeText],
        };
        source.Facts = FixtureFacts with { FacadeFrameworkDescription = MarkupFreeText };
        source.Failures = [new FailureItem(FailureItemIds.Ordering, FailureItemIds.NotRun, Detail: MarkupFreeText)];
        source.Checks = new Dictionary<string, CheckResult>(StringComparer.Ordinal)
        {
            [CheckNames.Failure] = new(CheckNames.Failure, MarkupFreeText, Cases: 2, Passed: 1, Detail: MarkupFreeText),
            [CheckNames.OpenApiDrift] = new(CheckNames.OpenApiDrift, CheckOutcomes.Fail, Cases: 0, Passed: 0, Detail: MarkupFreeText),
        };

        var text = RenderReport(BuildReport(source, FourResultsRun.Entries()));

        Assert.Equal((MarkupFreeText, string.Empty), DecodeBackslashEscapes(EscapedMarkupFreeText));
        Assert.Contains(TableRow(".NET (facade runtime)", EscapedMarkupFreeText), SectionLines(text, "## Run facts"));
        var counts = SectionLines(text, "## Counts");
        Assert.Contains(TableRow("Failure (unreachable and timeout)", "outcome", EscapedMarkupFreeText), counts);
        Assert.Contains(TableRow("Failure (unreachable and timeout)", "unreachable / timeout", EscapedMarkupFreeText), counts);
        Assert.Contains(TableRow("Contracts", "OpenAPI drift", "fail — " + EscapedMarkupFreeText), counts);
        Assert.Equal(
            TableRow("`CHK-ORDERING`", NoValue, "`not-run`", "yes", InlineCode(FourResultsRun.OrderingEntryId), "0", NoValue, EscapedMarkupFreeText),
            Assert.Single(TableBody(SectionLines(text, "### Other failing items"))));
        Assert.Equal(
            new[] { "Not met.", "- Rest.getAgent: status differs in REST-getAgent-" + EscapedMarkupFreeText + " (baseline 200, observed 502)" },
            NonBlank(SectionLines(text, "## Demo acceptance"))[1..]);
    }

    [Theory]
    [InlineData("not-run", "`not-run`")]
    [InlineData("differs:a`b", "``differs:a`b``")]
    [InlineData("differs:a``b", "```differs:a``b```")]
    [InlineData("differs:a```b", "````differs:a```b````")]
    [InlineData("`differs", "`` `differs ``")]
    [InlineData("```differs", "```` ```differs ````")]
    [InlineData("differs``", "``` differs`` ```")]
    [InlineData("`differs:a``b`", "``` `differs:a``b` ```")]
    [InlineData(" differs ", "`  differs  `")]
    [InlineData(" `differs` ", "``  `differs`  ``")]
    [InlineData(" differs", "` differs`")]
    [InlineData("   ", "`   `")]
    public void Render_CodeSpan_HoldsTheValueExactly(string value, string expected)
    {
        // A nature is never blank, so the all-space value is checked through the item's Operation column,
        // which every value also passes through.
        var blank = string.IsNullOrWhiteSpace(value);
        var source = new ReportSourceFixture
        {
            Failures = [new FailureItem(FailureItemIds.Ordering, blank ? FailureItemIds.NotRun : value, Operation: value)],
        };

        var row = Assert.Single(TableBody(SectionLines(RenderReport(BuildReport(source)), "### Other failing items")));

        Assert.Equal(TableRow("`CHK-ORDERING`", expected, blank ? "`not-run`" : expected, "no", NoValue, NoValue, NoValue, NoValue), row);
        Assert.Equal(value, CodeSpanContent(expected));
    }

    // The artifact cell names artifacts/discrepancies/<itemId>.json with the id byte for byte; an id that cannot
    // be one file name has no artifact, so the report renders the dash instead of failing.
    [Theory]
    [InlineData(FailureItemIds.OpenApiDrift, "`artifacts/discrepancies/CHK-OPENAPI-DRIFT.json`")]
    [InlineData(FailureItemIds.WsdlContract, "`artifacts/discrepancies/CHK-WSDL-CONTRACT.json`")]
    [InlineData("CHK-OPENAPI-DRIFT/../escape", NoValue)]
    [InlineData("..", NoValue)]
    public void Render_DiscrepancyArtifact_NamesTheExactItemIdOrTheDash(string itemId, string expectedArtifact)
    {
        const string nature = "d:0123456789abcdef";
        var source = new ReportSourceFixture { Failures = [new FailureItem(itemId, nature)] };

        var row = Assert.Single(TableBody(SectionLines(RenderReport(BuildReport(source)), "### Other failing items")));

        Assert.Equal(TableRow(InlineCode(itemId), NoValue, InlineCode(nature), "no", NoValue, NoValue, expectedArtifact, NoValue), row);
    }

    /// <summary>The "Known limitations" lines of the report of an empty run against <paramref name="register"/>.</summary>
    private static string[] CommittedLimitationLines(KnownLimitationsRegister register) =>
        NonBlank(SectionLines(
            RenderReport(ResultsReport.Build(new ReportSourceFixture(), register, OperationCatalogue.Default)),
            "## Known limitations"));

    /// <summary>
    /// Register text as the report's sentence: on one line, trimmed, with a full stop unless it already
    /// ends in '.', '!' or '?'; a dash and a full stop when blank.
    /// </summary>
    private static string ReportSentence(string? text)
    {
        var line = (text ?? string.Empty)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (line.Length == 0)
        {
            return NoValue + ".";
        }

        return line[^1] is '.' or '!' or '?' ? line : line + ".";
    }

    /// <summary>
    /// The text <paramref name="markdown"/> renders as when its only inline constructs are CommonMark
    /// backslash escapes (a backslash before ASCII punctuation is that punctuation), and the characters of
    /// <see cref="EscapedInlineMarkup"/> it holds unescaped, in order.
    /// </summary>
    private static (string Text, string Markup) DecodeBackslashEscapes(string markdown)
    {
        var text = new System.Text.StringBuilder(markdown.Length);
        var markup = new System.Text.StringBuilder();
        for (var index = 0; index < markdown.Length; index++)
        {
            var character = markdown[index];
            if (character == '\\'
                && index + 1 < markdown.Length
                && CommonMarkAsciiPunctuation.Contains(markdown[index + 1], StringComparison.Ordinal))
            {
                index++;
                text.Append(markdown[index]);
                continue;
            }

            if (EscapedInlineMarkup.Contains(character, StringComparison.Ordinal))
            {
                markup.Append(character);
            }

            text.Append(character);
        }

        return (text.ToString(), markup.ToString());
    }

    /// <summary>
    /// The content of <paramref name="markdown"/> read as one CommonMark code span: an opening run of n
    /// backticks, closed by the next run of exactly n backticks, with one space removed from each side
    /// when the content both starts and ends with a space and is not all spaces. Null unless the text
    /// is exactly one such span.
    /// </summary>
    private static string? CodeSpanContent(string markdown)
    {
        var fence = markdown.Length - markdown.TrimStart('`').Length;
        var index = fence;
        while (fence > 0 && index < markdown.Length)
        {
            if (markdown[index] != '`')
            {
                index++;
                continue;
            }

            var runStart = index;
            while (index < markdown.Length && markdown[index] == '`')
            {
                index++;
            }

            if (index - runStart != fence)
            {
                continue;
            }

            if (index != markdown.Length)
            {
                return null;
            }

            var content = markdown[fence..runStart];
            return content.StartsWith(' ') && content.EndsWith(' ') && content.AsSpan().ContainsAnyExcept(' ')
                ? content[1..^1]
                : content;
        }

        return null;
    }

    // The Counts table's areas for the separate checks, as the report names them.
    private const string ConformanceArea = "Stand-in conformance";
    private const string RoutingArea = "Routing";
    private const string FailureArea = "Failure (unreachable and timeout)";
    private const string ListenerIsolationArea = "Listener isolation";
    private const string TrxArea = "Facade test results (TRX)";
    private const string ContractsArea = "Contracts";
    private const string JadeUnchangedArea = "JADE unchanged";
    private const string OrderingArea = "Ordering evidence";

    /// <summary>Each alternative of the Counts table's separate-check rows, as <see cref="SeparateCheckBranch"/> names it.</summary>
    public static TheoryData<string> SeparateCheckBranches { get; } = new()
    {
        "nothing run",
        "conformance not counted, check not run",
        "conformance without gaps",
        "test checks not run without cases",
        "test check not run with cases",
        "test check failed without cases",
        "TRX from its check",
        "TRX missing",
        "TRX read",
        "contracts and ordering not run",
        "check with a blank outcome",
        "JADE unchanged not counted, check not run",
    };

    [Fact]
    public void Render_CountsTableStatesTheSeparateChecks()
    {
        // Every rendered count differs from every other and from zero, so a value in the wrong row, a
        // swapped pair or a constant shows. The gap natures are added in reverse ordinal order.
        var source = new ReportSourceFixture
        {
            Conformance = new ConformanceSummary(
                MembersChecked: 41,
                Passed: 31,
                GapsByNature: new Dictionary<string, int>(StringComparer.Ordinal) { ["unobserved"] = 9, ["null-only"] = 1 }),
            JadeUnchanged = new JadeUnchangedSummary(PathsChecked: 123, Modified: 2),
            Checks = CheckSet(
            [
                new(CheckNames.Conformance, CheckOutcomes.Fail, Cases: 43, Passed: 33, Detail: "2 of 2 seed-token assertions hold"),
                new(CheckNames.Routing, CheckOutcomes.Fail, Cases: 4, Passed: 3, Detail: "Rest.getCompany reached backend A"),
                new(CheckNames.Failure, CheckOutcomes.Fail, Cases: 6, Passed: 5, Detail: "unreachable 3/4 passed; timeout 2/2 passed"),
                new(CheckNames.ListenerIsolation, CheckOutcomes.Fail, Cases: 8, Passed: 7, Detail: null),
                new(CheckNames.OpenApiDrift, CheckOutcomes.Pass, Cases: 1, Passed: 1, Detail: null),
                new(CheckNames.WsdlContract, CheckOutcomes.Fail, Cases: 1, Passed: 0, Detail: "soap:address missing on the facade side"),
                new(CheckNames.JadeUnchanged, CheckOutcomes.Fail, Cases: 1, Passed: 0, Detail: "2 paths modified"),
                new(CheckNames.Ordering, CheckOutcomes.Pass, Cases: 1, Passed: 1, Detail: null),
            ]),
        };

        var lines = SectionLines(RenderReport(BuildReport(source)), "## Counts");

        // Every row from the first conformance row to the end of the table, in order: nothing missing,
        // repeated or extra. The JADE-unchanged counts take the place of that check's outcome.
        Assert.Equal(
            new[]
            {
                TableRow(ConformanceArea, "members checked", "41"),
                TableRow(ConformanceArea, "members passed", "31"),
                TableRow(ConformanceArea, "gaps: " + InlineCode("null-only"), "1"),
                TableRow(ConformanceArea, "gaps: " + InlineCode("unobserved"), "9"),
                TableRow(ConformanceArea, "outcome", "fail — 2 of 2 seed-token assertions hold"),
                TableRow(RoutingArea, "cases", "4"),
                TableRow(RoutingArea, "passed", "3"),
                TableRow(RoutingArea, "outcome", "fail"),
                TableRow(RoutingArea, "detail", "Rest.getCompany reached backend A"),
                TableRow(FailureArea, "cases", "6"),
                TableRow(FailureArea, "passed", "5"),
                TableRow(FailureArea, "outcome", "fail"),
                TableRow(FailureArea, "unreachable / timeout", "unreachable 3/4 passed; timeout 2/2 passed"),
                TableRow(ListenerIsolationArea, "cases", "8"),
                TableRow(ListenerIsolationArea, "passed", "7"),
                TableRow(ListenerIsolationArea, "outcome", "fail"),
                TableRow(TrxArea, "result", "read"),
                TableRow(ContractsArea, "OpenAPI drift", "pass"),
                TableRow(ContractsArea, "WSDL contract", "fail — soap:address missing on the facade side"),
                TableRow(JadeUnchangedArea, "paths checked", "123"),
                TableRow(JadeUnchangedArea, "paths modified", "2"),
                TableRow(OrderingArea, "result", "pass"),
            },
            SeparateCheckRows(lines));
    }

    [Theory]
    [MemberData(nameof(SeparateCheckBranches))]
    public void Render_CountsTableSeparateChecks_StateEachAlternative(string branch)
    {
        var (source, expected) = SeparateCheckBranch(branch);

        var lines = SectionLines(RenderReport(BuildReport(source)), "## Counts");

        // Every row of the areas the branch states, in table order: nothing missing, repeated or extra.
        var areas = expected.Select(AreaOf).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected, lines.Where(line => areas.Contains(AreaOf(line))));
    }

    /// <summary>
    /// The source of one <see cref="SeparateCheckBranches"/> alternative, with the Counts rows its areas
    /// must show. Each source holds only the checks, failures and counts the branch names.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">No branch has that name.</exception>
    private static (ReportSourceFixture Source, string[] Rows) SeparateCheckBranch(string branch) => branch switch
    {
        "nothing run" => (
            new ReportSourceFixture(),
            [
                TableRow(ConformanceArea, "members checked", "not run"),
                TableRow(RoutingArea, "cases", "not run"),
                TableRow(FailureArea, "cases", "not run"),
                TableRow(ListenerIsolationArea, "cases", "not run"),
                TableRow(TrxArea, "result", "not run"),
                TableRow(ContractsArea, "OpenAPI drift", "not run"),
                TableRow(ContractsArea, "WSDL contract", "not run"),
                TableRow(JadeUnchangedArea, "paths checked", "not run"),
                TableRow(OrderingArea, "result", "not run"),
            ]),
        "conformance not counted, check not run" => (
            new ReportSourceFixture
            {
                Checks = CheckSet([new(CheckNames.Conformance, CheckOutcomes.NotRun, Cases: 0, Passed: 0, Detail: "baseline/conformance.json absent")]),
            },
            [
                TableRow(ConformanceArea, "members checked", "not run"),
                TableRow(ConformanceArea, "outcome", "not run — baseline/conformance.json absent"),
            ]),
        "conformance without gaps" => (
            new ReportSourceFixture
            {
                Conformance = new ConformanceSummary(MembersChecked: 12, Passed: 12, GapsByNature: new Dictionary<string, int>(StringComparer.Ordinal)),
                Checks = CheckSet([new(CheckNames.Conformance, CheckOutcomes.Pass, Cases: 14, Passed: 14, Detail: null)]),
            },
            [
                TableRow(ConformanceArea, "members checked", "12"),
                TableRow(ConformanceArea, "members passed", "12"),
                TableRow(ConformanceArea, "gaps", "0"),
                TableRow(ConformanceArea, "outcome", "pass"),
            ]),
        "test checks not run without cases" => (
            new ReportSourceFixture
            {
                Checks = CheckSet(
                [
                    new(CheckNames.Routing, CheckOutcomes.NotRun, Cases: 0, Passed: 0, Detail: null),
                    new(CheckNames.Failure, CheckOutcomes.NotRun, Cases: 0, Passed: 0, Detail: "TRX held no failure tests"),
                    new(CheckNames.ListenerIsolation, CheckOutcomes.NotRun, Cases: 0, Passed: 0, Detail: null),
                ]),
            },
            [
                TableRow(RoutingArea, "cases", "not run"),
                TableRow(FailureArea, "cases", "not run"),
                TableRow(FailureArea, "unreachable / timeout", "TRX held no failure tests"),
                TableRow(ListenerIsolationArea, "cases", "not run"),
                TableRow(TrxArea, "result", "not run"),
            ]),
        "test check not run with cases" => (
            new ReportSourceFixture
            {
                Checks = CheckSet([new(CheckNames.ListenerIsolation, CheckOutcomes.NotRun, Cases: 3, Passed: 0, Detail: "admin socket not reached")]),
            },
            [
                TableRow(ListenerIsolationArea, "cases", "3"),
                TableRow(ListenerIsolationArea, "passed", "0"),
                TableRow(ListenerIsolationArea, "outcome", "not run"),
                TableRow(ListenerIsolationArea, "detail", "admin socket not reached"),
                TableRow(TrxArea, "result", "read"),
            ]),
        "test check failed without cases" => (
            new ReportSourceFixture
            {
                Checks = CheckSet([new(CheckNames.Routing, CheckOutcomes.Fail, Cases: 0, Passed: 0, Detail: null)]),
            },
            [
                TableRow(RoutingArea, "cases", "0"),
                TableRow(RoutingArea, "passed", "0"),
                TableRow(RoutingArea, "outcome", "fail"),
                TableRow(TrxArea, "result", "not run"),
            ]),
        "TRX from its check" => (
            new ReportSourceFixture
            {
                Failures = [new FailureItem(FailureItemIds.FacadeTrx, FailureItemIds.Missing)],
                Checks = CheckSet(
                [
                    new(CheckNames.Routing, CheckOutcomes.Pass, Cases: 2, Passed: 2, Detail: null),
                    new(CheckNames.FacadeTrx, CheckOutcomes.Fail, Cases: 0, Passed: 0, Detail: "exit code 1"),
                ]),
            },
            [TableRow(TrxArea, "result", "fail — exit code 1")]),
        "TRX missing" => (
            new ReportSourceFixture
            {
                // The same item twice, which the report lists once, and a check that read cases.
                Failures =
                [
                    new FailureItem(FailureItemIds.FacadeTrx, FailureItemIds.Missing, Detail: "TRX file not found"),
                    new FailureItem(FailureItemIds.FacadeTrx, FailureItemIds.Missing, Detail: "TRX file not found"),
                ],
                Checks = CheckSet([new(CheckNames.Routing, CheckOutcomes.Pass, Cases: 2, Passed: 2, Detail: null)]),
            },
            [TableRow(TrxArea, "result", "fail (" + InlineCode(FailureItemIds.Missing) + ")")]),
        "TRX read" => (
            new ReportSourceFixture
            {
                Checks = CheckSet([new(CheckNames.Failure, CheckOutcomes.Pass, Cases: 2, Passed: 2, Detail: null)]),
            },
            [TableRow(TrxArea, "result", "read")]),
        "contracts and ordering not run" => (
            new ReportSourceFixture
            {
                Checks = CheckSet(
                [
                    new(CheckNames.OpenApiDrift, CheckOutcomes.NotRun, Cases: 0, Passed: 0, Detail: null),
                    new(CheckNames.WsdlContract, CheckOutcomes.NotRun, Cases: 0, Passed: 0, Detail: "contracts/wsdl/ErewhonWebService.wsdl absent"),
                    new(CheckNames.Ordering, CheckOutcomes.NotRun, Cases: 0, Passed: 0, Detail: "no git metadata"),
                ]),
            },
            [
                TableRow(ContractsArea, "OpenAPI drift", "not run"),
                TableRow(ContractsArea, "WSDL contract", "not run — contracts/wsdl/ErewhonWebService.wsdl absent"),
                TableRow(OrderingArea, "result", "not run — no git metadata"),
            ]),
        "check with a blank outcome" => (
            new ReportSourceFixture
            {
                Checks = CheckSet([new(CheckNames.Ordering, string.Empty, Cases: 0, Passed: 0, Detail: null)]),
            },
            [TableRow(OrderingArea, "result", NoValue)]),
        "JADE unchanged not counted, check not run" => (
            new ReportSourceFixture
            {
                Checks = CheckSet([new(CheckNames.JadeUnchanged, CheckOutcomes.NotRun, Cases: 0, Passed: 0, Detail: "no git metadata")]),
            },
            [TableRow(JadeUnchangedArea, "paths checked", "not run — no git metadata")]),
        _ => throw new ArgumentOutOfRangeException(nameof(branch), branch, "No separate-check branch has this name."),
    };

    /// <summary>Checks keyed by their names, as <see cref="IReportSource.Checks"/> holds them.</summary>
    private static Dictionary<string, CheckResult> CheckSet(IEnumerable<CheckResult> checks) =>
        checks.ToDictionary(check => check.Name, StringComparer.Ordinal);

    /// <summary>The Counts table's rows from its first stand-in conformance row to the end of the table.</summary>
    private static string[] SeparateCheckRows(IEnumerable<string> countsLines) =>
        countsLines
            .SkipWhile(line => !line.StartsWith($"| {ConformanceArea} |", StringComparison.Ordinal))
            .TakeWhile(line => line.StartsWith('|'))
            .ToArray();

    /// <summary>The first cell of a table row, <c>| area | ... |</c>; empty for any other line.</summary>
    private static string AreaOf(string line)
    {
        const string start = "| ";
        var end = line.StartsWith(start, StringComparison.Ordinal)
            ? line.IndexOf(" | ", start.Length, StringComparison.Ordinal)
            : -1;
        return end > start.Length ? line[start.Length..end] : string.Empty;
    }

    // ---- Results report: fixtures ---------------------------------------------------------------

    private const string StartingCommit = "19a5189e9075349aca54de1fb5fdf2d8727403e7";

    // The report's text for an empty table cell.
    private const string NoValue = "—";

    private const string CatalogueUnavailableNote =
        "The operation catalogue could not be loaded, so rows are derived from the planned and replayed cases and an operation with neither is not listed.";

    // The "Why" cell of each row under "### Captured cases not compared".
    private const string UnaccountedReason =
        "The case has a baseline record, but the replay did not compare it, so its operation is an undocumented difference.";

    // Held by every user name and password a test configures; the report must never contain it.
    private const string CredentialMark = "Zq7";
    private const string UrlUser = "user" + CredentialMark;
    private const string UrlPassword = "pass" + CredentialMark;

    // The report's text after a URL whose user information it removed.
    private const string UserInformationRemoved = " (user information removed)";

    /// <summary>
    /// Fixed run facts: 2026-01-02T03:04:05Z, Docker mode, the facade and stand-in URLs of the Compose
    /// network, the stand-in admin URL and the starting commit; no baseline or facade commit found.
    /// </summary>
    private static RunFacts FixtureFacts { get; } = new(
        new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        "docker",
        ".NET 10.0.14",
        "10.0.403",
        "http://facade:65000",
        "http://facade:21001",
        "http://standin:65000",
        "http://standin:21001",
        "http://standin:9090",
        StartingCommit,
        BaselineCommit: null,
        FacadeCommit: null);

    /// <summary>
    /// <see cref="FixtureFacts"/> with the URL run fact named <paramref name="fact"/> set to
    /// <paramref name="url"/>, and the label of that fact's row.
    /// </summary>
    private static (RunFacts Facts, string Label) WithRunFactUrl(string fact, string url) => fact switch
    {
        nameof(RunFacts.TargetRestUrl) => (FixtureFacts with { TargetRestUrl = url }, "Target REST URL"),
        nameof(RunFacts.TargetSoapUrl) => (FixtureFacts with { TargetSoapUrl = url }, "Target SOAP URL"),
        nameof(RunFacts.BackendRestUrl) => (FixtureFacts with { BackendRestUrl = url }, "Backend REST URL"),
        nameof(RunFacts.BackendSoapUrl) => (FixtureFacts with { BackendSoapUrl = url }, "Backend SOAP URL"),
        nameof(RunFacts.BackendAdminUrl) => (FixtureFacts with { BackendAdminUrl = url }, "Backend admin URL"),
        _ => throw new ArgumentOutOfRangeException(nameof(fact), fact, "No URL run fact has this name."),
    };

    /// <summary>
    /// An external-backend run whose five URL run facts each hold user information with
    /// <see cref="CredentialMark"/>: parsed and unparsed forms, and both banner URLs.
    /// </summary>
    private static ReportSourceFixture CredentialedUrlSource() => new()
    {
        ExternalBackend = true,
        Facts = FixtureFacts with
        {
            TargetRestUrl = $"http://{UrlUser}:{UrlPassword}@facade:65000",
            TargetSoapUrl = $"https://{UrlUser}@facade:21001",
            BackendRestUrl = $"http://{UrlUser}:pass%40{CredentialMark}@jade-host:65000",
            BackendSoapUrl = $"http://{UrlUser}:p@ss{CredentialMark}@jade-host:21001",
            BackendAdminUrl = $"{UrlUser}:{UrlPassword}@jade-host:9090",
        },
    };

    /// <summary>
    /// Baseline facts of a run whose manifest was read, captured by local processes with the WSDL:
    /// every count zero and no case listed as not captured. Tests set the counts they state with
    /// <c>with</c>.
    /// </summary>
    private static BaselineFacts FixtureBaseline { get; } = new(
        PlannedOperations: 0,
        PlannedCases: 0,
        CapturedOperations: 0,
        CapturedCases: 0,
        NotCaptured: [],
        WsdlCaptured: true,
        ManifestPresent: true,
        CaptureExecution: "local-processes");

    /// <summary>
    /// The planned case ids by operation id, as <see cref="IReportSource.PlannedCasesByOperation"/> holds them.
    /// </summary>
    /// <param name="operations">Each operation id with its planned case ids, in matrix order.</param>
    private static Dictionary<string, IReadOnlyList<string>> Plan(params (string Operation, string[] CaseIds)[] operations) =>
        operations.ToDictionary(operation => operation.Operation, operation => (IReadOnlyList<string>)operation.CaseIds, StringComparer.Ordinal);

    /// <summary>
    /// The captured case ids by operation id, as <see cref="IReportSource.CapturedCasesByOperation"/> holds
    /// them: the case of every result in <paramref name="results"/>, in their order, then each of
    /// <paramref name="recordedWithoutResult"/>, a record the replay reported no result for.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> CapturedFrom(
        IEnumerable<CaseResult> results,
        params (string Operation, string CaseId)[] recordedWithoutResult) =>
        results
            .Select(result => (result.Operation, result.CaseId))
            .Concat(recordedWithoutResult)
            .GroupBy(record => record.Operation, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(record => record.CaseId).ToArray(),
                StringComparer.Ordinal);

    /// <summary>A captured case that was compared and found equivalent: status 200 on both sides, well formed.</summary>
    private static CaseResult EquivalentCase(string caseId, string operation) =>
        new(
            caseId,
            operation,
            Order: 1,
            Compared: true,
            Equivalent: true,
            Discrepancies: [],
            WellFormed: true,
            StatusMatched: true,
            BaselineStatus: 200,
            ObservedStatus: 200,
            ItemNature: null);

    /// <summary>
    /// A captured case that was compared and differs only in <paramref name="discrepancies"/>, with the
    /// baseline status 200 and a well-formed payload. Its item nature is <c>d:</c> followed by the
    /// signature of the discrepancies.
    /// </summary>
    private static CaseResult DifferingCase(string caseId, string operation, params Discrepancy[] discrepancies) =>
        EquivalentCase(caseId, operation) with
        {
            Equivalent = false,
            Discrepancies = discrepancies,
            ItemNature = "d:" + DiscrepancySignature.Compute(discrepancies),
        };

    /// <summary>
    /// A captured, compared case whose observed status is not the baseline's, with a well-formed payload:
    /// a <c>$status</c> value discrepancy, or no response at all (<c>no-response:connect</c>) when
    /// <paramref name="observedStatus"/> is null.
    /// </summary>
    private static CaseResult StatusMismatchCase(string caseId, string operation, int baselineStatus, int? observedStatus)
    {
        var discrepancy = observedStatus is { } observed
            ? new Discrepancy(
                "$status",
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal(Number(baselineStatus)),
                DiscrepancyValue.Literal(Number(observed)))
            : new Discrepancy(
                "$response",
                DiscrepancyKinds.NoResponse(NoResponseReasons.Connect),
                DiscrepancyValue.Absent,
                DiscrepancyValue.Absent);

        return DifferingCase(caseId, operation, discrepancy) with
        {
            StatusMatched = false,
            BaselineStatus = baselineStatus,
            ObservedStatus = observedStatus,
        };
    }

    /// <summary>A captured, compared case with the baseline status whose observed body is not well formed.</summary>
    private static CaseResult MalformedCase(string caseId, string operation) =>
        DifferingCase(
            caseId,
            operation,
            new Discrepancy("$body", DiscrepancyKinds.Malformed, DiscrepancyValue.Literal("well formed"), DiscrepancyValue.Literal("truncated")))
        with
        {
            WellFormed = false,
        };

    /// <summary>The one case <see cref="CatalogueRun"/> plans for an operation: <c>REST-getAgent-01</c> for <c>Rest.getAgent</c>.</summary>
    private static string CaseIdOf(string operationId)
    {
        var dot = operationId.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == operationId.Length - 1)
        {
            throw new ArgumentException("An operation id has the form <Surface>.<method>.", nameof(operationId));
        }

        return string.Concat(operationId[..dot].ToUpperInvariant(), "-", operationId[(dot + 1)..], "-01");
    }

    /// <summary>
    /// A run of the 22 catalogue operations with one planned case each (<see cref="CaseIdOf"/>), every case
    /// captured and equivalent, except that the case of each operation in
    /// <paramref name="notCapturedOperations"/> is listed as not captured, and a case in
    /// <paramref name="replacements"/> replaces its operation's case. The baseline counts agree with the cases.
    /// </summary>
    /// <exception cref="ArgumentException">A replacement names no catalogue operation, or one listed as not captured.</exception>
    private static ReportSourceFixture CatalogueRun(string[] notCapturedOperations, params CaseResult[] replacements)
    {
        var operations = OperationCatalogue.Default.Operations;
        var notCaptured = notCapturedOperations.ToHashSet(StringComparer.Ordinal);
        foreach (var replacement in replacements)
        {
            if (notCaptured.Contains(replacement.Operation)
                || !operations.Any(operation => string.Equals(operation.Id, replacement.Operation, StringComparison.Ordinal)))
            {
                throw new ArgumentException("A replacement must name a captured catalogue operation.", nameof(replacements));
            }
        }

        var cases = new List<CaseResult>();
        foreach (var operation in operations)
        {
            if (notCaptured.Contains(operation.Id))
            {
                continue;
            }

            var replacement = replacements.SingleOrDefault(result => string.Equals(result.Operation, operation.Id, StringComparison.Ordinal));
            cases.Add((replacement ?? EquivalentCase(CaseIdOf(operation.Id), operation.Id)) with { Order = cases.Count + 1 });
        }

        return new ReportSourceFixture
        {
            Cases = cases,
            PlannedCasesByOperation = Plan(operations.Select(operation => (operation.Id, new[] { CaseIdOf(operation.Id) })).ToArray()),
            Baseline = FixtureBaseline with
            {
                PlannedOperations = operations.Count,
                PlannedCases = operations.Count,
                CapturedOperations = cases.Count,
                CapturedCases = cases.Count,
                NotCaptured = notCapturedOperations.Select(CaseIdOf).ToArray(),
            },
        };
    }

    /// <summary>
    /// A <see cref="CatalogueRun"/> whose last <paramref name="differing"/> catalogue operations differ in a
    /// body value while returning the baseline status with a well-formed payload; every other operation is
    /// equivalent.
    /// </summary>
    private static ReportSourceFixture CatalogueRunEndingInDifferences(int differing) =>
        CatalogueRun(
            [],
            OperationCatalogue.Default.Operations
                .TakeLast(differing)
                .Select(operation => DifferingCase(CaseIdOf(operation.Id), operation.Id, Website))
                .ToArray());

    /// <summary>An <see cref="Entry"/> with the id <paramref name="id"/>, for registers that need several entries.</summary>
    private static RegisterEntry NamedEntry(string id, params (string itemId, string nature)[] pairs) =>
        Entry(pairs) with { Id = id };

    /// <summary>The (<c>itemId</c>, <c>nature</c>) pair of a compared, non-equivalent case's <c>CASE:</c> item.</summary>
    /// <exception cref="ArgumentException">The case has no item nature.</exception>
    private static (string itemId, string nature) CasePair(CaseResult result) =>
        (FailureItemIds.Case(result.CaseId),
            result.ItemNature ?? throw new ArgumentException("The case has no item nature.", nameof(result)));

    /// <summary>The (<c>itemId</c>, <c>nature</c>) pair of a case listed in <see cref="BaselineFacts.NotCaptured"/>.</summary>
    private static (string itemId, string nature) NotCapturedPair(string caseId) =>
        (FailureItemIds.Case(caseId), FailureItemIds.NotCaptured);

    /// <summary>Builds the report of <paramref name="source"/> with an inline register and the embedded 22-operation catalogue.</summary>
    /// <param name="source">The run's observations.</param>
    /// <param name="entries">The register's entries, in file order.</param>
    private static ResultsReport BuildReport(IReportSource source, params RegisterEntry[] entries) =>
        ResultsReport.Build(source, Register(entries), OperationCatalogue.Default);

    /// <summary>Renders <paramref name="report"/> as the text of <c>RESULTS.md</c>, without writing a file.</summary>
    private static string RenderReport(ResultsReport report) => new ResultsReportWriter().Render(report);

    /// <summary>The report's row for <paramref name="operationId"/>; fails unless there is exactly one.</summary>
    private static OperationRow OperationRowOf(ResultsReport report, string operationId) =>
        Assert.Single(report.Operations, row => string.Equals(row.OperationId, operationId, StringComparison.Ordinal));

    /// <summary>Rendered text split at <c>\n</c>; the element after the last line feed is empty.</summary>
    private static string[] Lines(string markdown) => markdown.Split('\n');

    /// <summary>The non-blank lines of <paramref name="lines"/>, in order.</summary>
    private static string[] NonBlank(IEnumerable<string> lines) => lines.Where(line => line.Length > 0).ToArray();

    /// <summary>
    /// The lines after the heading line <paramref name="heading"/>, up to the next heading of any level or
    /// the end of the text; fails when the heading is absent.
    /// </summary>
    private static string[] SectionLines(string markdown, string heading)
    {
        var lines = Lines(markdown);
        var start = Array.IndexOf(lines, heading);
        Assert.True(start >= 0, $"The report has no heading \"{heading}\".");
        return lines.Skip(start + 1).TakeWhile(line => !line.StartsWith('#')).ToArray();
    }

    /// <summary>The body rows of the first Markdown table in <paramref name="lines"/>: without its header and separator rows.</summary>
    private static string[] TableBody(IEnumerable<string> lines) =>
        lines
            .SkipWhile(line => !line.StartsWith('|'))
            .TakeWhile(line => line.StartsWith('|'))
            .Skip(2)
            .ToArray();

    /// <summary>A Markdown table row as the report writes it, <c>| a | b |</c>.</summary>
    private static string TableRow(params string[] cells) => "| " + string.Join(" | ", cells) + " |";

    /// <summary>A Markdown code span around a value that holds no backtick and no edge space.</summary>
    private static string InlineCode(string value) => "`" + value + "`";

    /// <summary>An integer as the report writes it, in the invariant culture.</summary>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The entry id a "Known limitations" line starts with, <c>- **&lt;id&gt;**</c>; empty for any other line.</summary>
    private static string LimitationIdOf(string line)
    {
        const string start = "- **";
        var end = line.StartsWith(start, StringComparison.Ordinal)
            ? line.IndexOf("**", start.Length, StringComparison.Ordinal)
            : -1;
        return end > start.Length ? line[start.Length..end] : string.Empty;
    }

    /// <summary>The start of the line for a mandatory or KL-SUB-NORESET entry the register does not hold.</summary>
    private static string BuiltInLimitationPrefix(string id) => $"- **{id}** (built-in text; not in the register) — ";

    /// <summary>
    /// An inline <see cref="IReportSource"/>. Every member is settable and defaults to an empty but valid
    /// run: no failures, cases, plans, captured cases or checks, <see cref="FixtureFacts"/>,
    /// <see cref="FixtureBaseline"/>, no conformance or JADE-unchanged counts, and the stand-in as the
    /// backend.
    /// </summary>
    private sealed class ReportSourceFixture : IReportSource
    {
        public IReadOnlyList<FailureItem> Failures { get; set; } = [];

        public IReadOnlyList<CaseResult> Cases { get; set; } = [];

        public IReadOnlyDictionary<string, IReadOnlyList<string>> PlannedCasesByOperation { get; set; } = Plan();

        public IReadOnlyDictionary<string, IReadOnlyList<string>> CapturedCasesByOperation { get; set; } = Plan();

        public IReadOnlyDictionary<string, CheckResult> Checks { get; set; } = new Dictionary<string, CheckResult>(StringComparer.Ordinal);

        public RunFacts Facts { get; set; } = FixtureFacts;

        public BaselineFacts Baseline { get; set; } = FixtureBaseline;

        public ConformanceSummary? Conformance { get; set; }

        public JadeUnchangedSummary? JadeUnchanged { get; set; }

        public bool ExternalBackend { get; set; }
    }

    /// <summary>
    /// A run that shows each AAP 0.7.7 operation result once against <see cref="Entries"/>:
    /// <c>Rest.getAgent</c> equivalent (two equivalent cases); <c>Rest.getClient</c> a documented
    /// difference (one equivalent case, one documented differing case); <c>Rest.getCompany</c> an
    /// undocumented difference; <c>Soap.getPhoto</c> not compared (its only case not captured and
    /// undocumented). It also holds a documented <c>CHK-ORDERING</c> item. The other 18 catalogue
    /// operations have no plan and no case.
    /// </summary>
    private static class FourResultsRun
    {
        public const string AgentFirstCaseId = "REST-getAgent-01";
        public const string AgentSecondCaseId = "REST-getAgent-02";
        public const string ClientEquivalentCaseId = "REST-getClient-01";
        public const string ClientDifferingCaseId = "REST-getClient-02";
        public const string CompanyCaseId = "REST-getCompany-01";
        public const string PhotoCaseId = "SOAP-getPhoto-01";

        /// <summary>The <see cref="Entry"/> id, which documents <see cref="ClientDifferingCaseId"/>.</summary>
        public const string DifferenceEntryId = "KL-TEST-DIFFERENCE";

        /// <summary>The check entry that documents the <c>CHK-ORDERING</c> item.</summary>
        public const string OrderingEntryId = "KL-TEST-NOGIT";

        /// <summary>The <c>CHK-ORDERING</c> item's detail.</summary>
        public const string OrderingDetail = "no git metadata";

        /// <summary>The nature of the documented <see cref="ClientDifferingCaseId"/> difference.</summary>
        public static string ClientNature => "d:" + DiscrepancySignature.Compute([Website]);

        /// <summary>The nature of the undocumented <see cref="CompanyCaseId"/> difference.</summary>
        public static string CompanyNature => "d:" + DiscrepancySignature.Compute([Fax]);

        /// <summary>A new source for the run, with baseline counts that agree with its cases.</summary>
        public static ReportSourceFixture Source() => new()
        {
            Failures = [new FailureItem(FailureItemIds.Ordering, FailureItemIds.NotRun, Detail: OrderingDetail)],
            Cases =
            [
                EquivalentCase(AgentFirstCaseId, "Rest.getAgent") with { Order = 1 },
                EquivalentCase(AgentSecondCaseId, "Rest.getAgent") with { Order = 2 },
                EquivalentCase(ClientEquivalentCaseId, "Rest.getClient") with { Order = 3 },
                DifferingCase(ClientDifferingCaseId, "Rest.getClient", Website) with { Order = 4 },
                DifferingCase(CompanyCaseId, "Rest.getCompany", Fax) with { Order = 5 },
            ],
            PlannedCasesByOperation = Plan(
                ("Rest.getAgent", [AgentFirstCaseId, AgentSecondCaseId]),
                ("Rest.getClient", [ClientEquivalentCaseId, ClientDifferingCaseId]),
                ("Rest.getCompany", [CompanyCaseId]),
                ("Soap.getPhoto", [PhotoCaseId])),
            Baseline = FixtureBaseline with
            {
                PlannedOperations = 4,
                PlannedCases = 6,
                CapturedOperations = 3,
                CapturedCases = 5,
                NotCaptured = [PhotoCaseId],
            },
        };

        /// <summary>
        /// The run's register entries, in file order: <see cref="DifferenceEntryId"/> for
        /// <c>Rest.getClient</c> after three attempts, then <see cref="OrderingEntryId"/>.
        /// </summary>
        public static RegisterEntry[] Entries() =>
        [
            Entry((FailureItemIds.Case(ClientDifferingCaseId), ClientNature)) with { Operation = "Rest.getClient" },
            NamedEntry(OrderingEntryId, (FailureItemIds.Ordering, FailureItemIds.NotRun)) with
            {
                Category = RegisterCategories.Check,
                Attempts = 0,
                Reason = "No git metadata in the checkout",
                ActionTaken = "Reported the ordering check as not run",
            },
        ];
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

            // A message that named the file would show its temporary directory, so that is replaced by a fixed token.
            Assert.Equal(MatrixMessagePrefix + "missing 'version'", ex.Message.Replace(directory, "<temporary-directory>", StringComparison.Ordinal));
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

    // A FIFO nobody writes to, a link to one or to a valid matrix outside the root, and a sparse file over the
    // evidence limit are each refused at once, before any byte is parsed, so nothing blocks a run.
    [Theory]
    [InlineData("fifo")]
    [InlineData("link-to-fifo")]
    [InlineData("link-to-file")]
    [InlineData("oversized")]
    public async Task Load_MatrixThatIsSpecialLinkedOrOversized_IsRefusedPromptly(string kind)
    {
        const string relative = "harness/config/case-matrix.json";
        var ct = TestContext.Current.CancellationToken;
        var path = Temp.PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PlantRefusedFile(path, kind, HarnessOptions.EvidenceByteLimit, CommittedMatrixText());

        var refusal = await Assert.ThrowsAsync<IOException>(
            () => Task.Run(() => CaseMatrix.Load(path, Catalogue, Temp.Root), ct).WaitAsync(PromptReadLimit, ct));

        Assert.Equal(ConfinedReadRefusal(relative, kind), refusal.Message);
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

// ---- Facade test results (TRX) ------------------------------------------------------------------
// Reading the facade tests' TRX file, xunit XML file and exit-code: the routing, failure and
// listener-isolation counts exactly as the TRX states them, one TEST item per failed or not-run
// invocation whose signature carries the exception type the xunit XML (or xunit's message prefix)
// records, and a single CHK-FACADE-TRX missing item whenever the TRX or the run's completion evidence
// is absent, unusable or inconsistent, or a failed result's exception type is not recorded, so an
// all-passed TRX never certifies an unverified run and an empty type slot is never silent. Files are
// written only to a temporary directory of the test's own. These members live in this class because the
// harness test project keeps its fixed set of test classes, and the TEST:<className>/<testName> and
// CHK-FACADE-TRX items the reading produces are failure identities this class covers.
public sealed partial class KnownLimitationsRegisterTests
{
    // RoutingClass, the routing tests' class name, is the one the register tests above declare.
    private const string FailureClass = "Erewhon.Facade.Tests.FailureBehaviourTests";
    private const string ListenerClass = "Erewhon.Facade.Tests.ListenerIsolationTests";
    private const string OtherClass = "Erewhon.Facade.Tests.CatalogueTests";

    private const string RerouteMethod = "RerouteOneOperation";
    private const string RestArguments = "(operationId: \"Rest.getCompany\")";
    private const string SoapArguments = "(operationId: \"Soap.getAgentNames\")";

    private const string FacadeTrxAssertionMessage = "Assert.Equal() Failure: Values differ\nExpected: 502\nActual:   200";
    private const int FacadeTrxAssertionLine = 42;
    private const string FacadeTestsRelativePath = "Erewhon.Facade.Tests/RoutingTests.cs";

    // The exception types xunit.v3 records in its XML for a failed Assert.Equal and Assert.True.
    private const string EqualExceptionType = "Xunit.Sdk.EqualException";
    private const string TrueExceptionType = "Xunit.Sdk.TrueException";

    private const string UntypedPrefix = "exception type not recorded for 1 failed result(s): ";
    private const string NoMatchingFailure = "xunit XML results hold no unambiguous matching failure";

    // 60 digits: no int holds it, and the quoted excerpt is cut at 40 characters.
    private const string OverlongExitCode = "777777777777777777777777777777777777777777777777777777777777";
    private const string OverlongExcerpt = "'7777777777777777777777777777777777777777…'";

    // The mode of a planted FIFO: rw-rw-rw- before the umask.
    private const uint FifoMode = 0b110_110_110;

    private static readonly XNamespace TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    // How long a Read may take before it counts as blocked; a refused file is refused at once.
    private static readonly TimeSpan PromptReadLimit = TimeSpan.FromSeconds(10);

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
        var summary = FacadeTestResults.Parse(OneFailedTrx(), OneFailedXunitXml(), exitCode);

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
        var summary = FacadeTestResults.Parse(OneFailedTrx(), OneFailedXunitXml(), 2);

        var item = Assert.Single(summary.Failures);
        Assert.Equal(FailureItemIds.Test(RoutingClass, RoutingTestName(RestArguments)), item.ItemId);
        Assert.Equal(ExpectedNature(EqualExceptionType, "RoutingTests.cs", FacadeTrxAssertionLine, FacadeTrxAssertionMessage), item.Nature);
        Assert.Equal("Assert.Equal() Failure: Values differ", item.Detail);
        Assert.Equal(2, summary.ExitCode);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, false)]
    public void FailedResultOfUncountedClass_CountsTowardsCompletion(int exitCode, bool expectMissing)
    {
        TrxResult[] results = [.. AllPassedResults(), new TrxResult(OtherClass, "LoadsCatalogue", "Failed", Message: "boom", ExceptionType: TrueExceptionType)];

        var summary = FacadeTestResults.Parse(Trx(results), XunitXml(results), exitCode);

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

        // The detail names the exit-code file by its full path, which is shown with Temp's root redacted.
        Assert.Contains(expectedReason, Redacted(item.Detail), StringComparison.Ordinal);
        Assert.Contains(FacadeTestResults.ExitCodeFileName, Redacted(item.Detail), StringComparison.Ordinal);
        Assert.DoesNotContain("\n", Redacted(item.Detail), StringComparison.Ordinal);
        Assert.Null(summary.ExitCode);
    }

    [Fact]
    public void Read_ExitCodeDirectory_GivesMissingItemNamingTheProblem()
    {
        WriteTrx(AllPassedTrx());
        Directory.CreateDirectory(Temp.PathOf($"artifacts/facade-tests/{FacadeTestResults.ExitCodeFileName}"));

        var summary = Read();

        AssertAllPassedCounts(summary);
        var item = AssertSingleTrxItem(summary);

        // The detail names the exit-code file by its full path, which is shown with Temp's root redacted.
        Assert.Contains("exit-code file unreadable: ", Redacted(item.Detail), StringComparison.Ordinal);
        Assert.EndsWith("is a directory", Redacted(item.Detail), StringComparison.Ordinal);
        Assert.Null(summary.ExitCode);
    }

    [Theory]
    [InlineData("0\n", false, 0)]
    [InlineData(" 0 ", false, 0)]
    [InlineData("2\r\n", true, 2)]
    public void Read_ConsistentExitCodeFile_AddsNoTrxItem(string exitCode, bool withFailedResult, int expectedCode)
    {
        WriteTrx(withFailedResult ? OneFailedTrx() : AllPassedTrx());
        if (withFailedResult)
        {
            WriteXunitXml(OneFailedXunitXml());
        }

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
        // The detail names the TRX file by its full path, which both sides show with Temp's root redacted.
        var trxReason = Redacted($"TRX file absent: {Temp.PathOf($"artifacts/facade-tests/{FacadeTestResults.TrxFileName}")}");
        if (expectedCompletionReason is null)
        {
            Assert.Equal(trxReason, Redacted(item.Detail));
        }
        else
        {
            Assert.StartsWith(trxReason + "; ", Redacted(item.Detail), StringComparison.Ordinal);
            Assert.Contains(expectedCompletionReason, Redacted(item.Detail), StringComparison.Ordinal);
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
    [InlineData("Assert.Equal() Failure: Values differ", EqualExceptionType)]
    [InlineData("Rest.getCompany: stub B received 0 requests, expected exactly 1", TrueExceptionType)]
    [InlineData("System.Net.Http.HttpRequestException : Connection refused (127.0.0.1:1)", "System.Net.Http.HttpRequestException")]
    public void FailedResult_NatureHashesExceptionTypeFileLineAndMessage(string message, string exceptionType)
    {
        TrxResult[] results =
        [
            .. AllPassedResults(),
            new TrxResult(RoutingClass, "Fails", "Failed", null, message, StackTrace("/src/facade/tests", FacadeTrxAssertionLine), exceptionType),
        ];

        var item = Assert.Single(FacadeTestResults.Parse(Trx(results), XunitXml(results), 2).Failures);

        Assert.Equal(ExpectedNature(exceptionType, "RoutingTests.cs", FacadeTrxAssertionLine, message), item.Nature);
    }

    [Theory]
    [InlineData("/src/facade/tests")]
    [InlineData("/home/u/repo/facade/tests")]
    [InlineData(@"C:\r\facade\tests")]
    public void FailureSignature_IgnoresThePathPrefix(string prefix)
    {
        var results = FailedResults(FacadeTrxAssertionMessage, StackTrace(prefix, FacadeTrxAssertionLine), EqualExceptionType);

        var summary = FacadeTestResults.Parse(Trx(results), XunitXml(results), 2);

        var item = Assert.Single(summary.Failures);
        Assert.Equal(ExpectedNature(EqualExceptionType, "RoutingTests.cs", FacadeTrxAssertionLine, FacadeTrxAssertionMessage), item.Nature);
    }

    [Theory]
    [InlineData(EqualExceptionType, FacadeTrxAssertionMessage, FacadeTrxAssertionLine + 1)]
    [InlineData(EqualExceptionType, "Assert.Equal() Failure: Values differ\nExpected: 502\nActual:   504", FacadeTrxAssertionLine)]
    [InlineData("System.InvalidOperationException", "System.InvalidOperationException : boom", FacadeTrxAssertionLine)]
    [InlineData(TrueExceptionType, FacadeTrxAssertionMessage, FacadeTrxAssertionLine)]
    public void FailureSignature_DiffersForAnotherExceptionTypeLineOrMessage(string exceptionType, string message, int line)
    {
        var referenceResults = FailedResults(FacadeTrxAssertionMessage, StackTrace("/src/facade/tests", FacadeTrxAssertionLine), EqualExceptionType);
        var reference = Assert.Single(FacadeTestResults.Parse(Trx(referenceResults), XunitXml(referenceResults), 2).Failures);

        var otherResults = FailedResults(message, StackTrace("/src/facade/tests", line), exceptionType);
        var other = Assert.Single(FacadeTestResults.Parse(Trx(otherResults), XunitXml(otherResults), 2).Failures);

        Assert.Equal(reference.ItemId, other.ItemId);
        Assert.NotEqual(reference.Nature, other.Nature);
    }

    [Fact]
    public void TwoTheoryInvocations_GiveTwoDistinctTestItems()
    {
        var stack = StackTrace("/src/facade/tests", FacadeTrxAssertionLine);
        TrxResult[] results =
        [
            new(RoutingClass, RerouteMethod, "Failed", RestArguments, FacadeTrxAssertionMessage, stack, EqualExceptionType),
            new(RoutingClass, RerouteMethod, "Failed", SoapArguments, FacadeTrxAssertionMessage, stack, EqualExceptionType),
            new(FailureClass, "RestCallToUnreachableBackend_Returns502", "Passed"),
            new(ListenerClass, "AdminPathsOnPublicListeners_ReachCatchAll", "Passed"),
        ];

        var summary = FacadeTestResults.Parse(Trx(results), XunitXml(results), 2);

        string[] expectedIds =
        [
            FailureItemIds.Test(RoutingClass, RoutingTestName(RestArguments)),
            FailureItemIds.Test(RoutingClass, RoutingTestName(SoapArguments)),
        ];
        Assert.Equal(expectedIds, summary.Failures.Select(item => item.ItemId));
        Assert.Equal(summary.Failures[0].Nature, summary.Failures[1].Nature);
        Assert.Equal(new CheckResult(CheckNames.Routing, CheckOutcomes.Fail, 2, 0, null), summary.Routing);
    }

    // ---- Exception types -----------------------------------------------------------------------

    [Fact]
    public void Read_TakesTheExceptionTypeFromTheXunitXmlFile()
    {
        WriteTrx(OneFailedTrx());
        WriteXunitXml(OneFailedXunitXml());
        WriteExitCode("2");

        var summary = Read();

        var item = Assert.Single(summary.Failures);
        Assert.Equal(FailureItemIds.Test(RoutingClass, RoutingTestName(RestArguments)), item.ItemId);
        Assert.Equal(ExpectedNature(EqualExceptionType, "RoutingTests.cs", FacadeTrxAssertionLine, FacadeTrxAssertionMessage), item.Nature);
    }

    [Theory]
    [InlineData("absent", "xunit XML results absent: ")]
    [InlineData("directory", "xunit XML results unreadable: ")]
    [InlineData("unparseable", "xunit XML results unparseable: ")]
    public void Read_UnusableXunitXmlFile_KeepsTestItemAndNamesTheProblem(string state, string expectedReason)
    {
        WriteTrx(OneFailedTrx());
        WriteExitCode("2");
        var xmlPath = Temp.PathOf($"artifacts/facade-tests/{FacadeTestResults.XunitXmlFileName}");
        switch (state)
        {
            case "directory":
                Directory.CreateDirectory(xmlPath);
                break;
            case "unparseable":
                WriteXunitXml("<assemblies><assembly>");
                break;
        }

        var summary = Read();

        AssertUntypedFailure(summary, UntypedPrefix + expectedReason);
        var trx = Assert.Single(summary.Failures, item => item.ItemId == FailureItemIds.FacadeTrx);
        if (state != "unparseable")
        {
            Assert.Contains(Redacted(xmlPath), Redacted(trx.Detail), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("none", "no xunit XML results were supplied")]
    [InlineData("empty", "xunit XML results unparseable: ")]
    [InlineData("truncated", "xunit XML results unparseable: ")]
    [InlineData("dtd", "xunit XML results unparseable: ")]
    [InlineData("other-root", NoMatchingFailure)]
    [InlineData("other-test", NoMatchingFailure)]
    [InlineData("other-class", NoMatchingFailure)]
    [InlineData("no-exception-type", NoMatchingFailure)]
    [InlineData("conflicting-types", NoMatchingFailure)]
    public void FailedResult_WithoutRecordedExceptionType_KeepsTestItemAndNamesTheProblem(string xunitXml, string expectedReason)
    {
        var stack = StackTrace("/src/facade/tests", FacadeTrxAssertionLine);
        var failed = FailedResults(FacadeTrxAssertionMessage, stack, EqualExceptionType)[0];
        var xml = xunitXml switch
        {
            "none" => null,
            "empty" => string.Empty,
            "truncated" => "<assemblies",
            "dtd" => """<?xml version="1.0"?><!DOCTYPE assemblies [<!ENTITY x "y">]><assemblies/>""",
            "other-root" => XunitXml(failed).Replace("assemblies", "testsuites", StringComparison.Ordinal),
            "other-test" => XunitXml(failed with { Arguments = SoapArguments }),
            "other-class" => XunitXml(failed with { ClassName = FailureClass }),
            "no-exception-type" => XunitXml(failed with { ExceptionType = null }),
            "conflicting-types" => XunitXml(
                failed with { Message = "Assert.Equal() Failure: Values differ\nExpected: 502\nActual:   503" },
                failed with { ExceptionType = TrueExceptionType, Message = "Assert.True() Failure" }),
            _ => throw new ArgumentOutOfRangeException(nameof(xunitXml), xunitXml, "unknown xunit XML case"),
        };

        var summary = FacadeTestResults.Parse(FailedTrx(FacadeTrxAssertionMessage, stack), xml, 2);

        AssertUntypedFailure(summary, UntypedPrefix + expectedReason);
        Assert.Equal(new CheckResult(CheckNames.Routing, CheckOutcomes.Fail, 2, 1, null), summary.Routing);
    }

    [Fact]
    public void XunitXml_TwoFailuresForOneDisplayName_AreNarrowedByMessage()
    {
        var stack = StackTrace("/src/facade/tests", FacadeTrxAssertionLine);
        var failed = FailedResults(FacadeTrxAssertionMessage, stack, EqualExceptionType)[0];
        var xml = XunitXml(
            failed with { ExceptionType = TrueExceptionType, Message = "Assert.True() Failure" },
            failed with { Message = FacadeTrxAssertionMessage.Replace("\n", "\r\n", StringComparison.Ordinal) + "  " });

        var item = Assert.Single(FacadeTestResults.Parse(FailedTrx(FacadeTrxAssertionMessage, stack), xml, 2).Failures);

        Assert.Equal(ExpectedNature(EqualExceptionType, "RoutingTests.cs", FacadeTrxAssertionLine, FacadeTrxAssertionMessage), item.Nature);
    }

    // Each xunit form is what Xunit.Runner.Common.XmlUtility.Escape in xunit.v3 4.0.2 returns for the TRX
    // form: escapeNewlines true for display names, false for messages. Two failures share the display
    // name with different types, so the type resolves only when both the name and the message match.
    [Theory]
    [InlineData("(operationId: \"Rest.getCompany\")", "(operationId: \\\"Rest.getCompany\\\")", FacadeTrxAssertionMessage, FacadeTrxAssertionMessage)]
    [InlineData(@"(path: ""C:\\dir"")", @"(path: \""C:\\\\dir\"")", "Expected: \"B\"\tActual: \"A\"", "Expected: \\\"B\\\"\\tActual: \\\"A\\\"")]
    [InlineData(@"(value: ""tab\there"")", @"(value: \""tab\\there\"")", "line 1\r\nline 2 C:\\dir\t\"x\"", "line 1\r\nline 2 C:\\\\dir\\t\\\"x\\\"")]
    public void XunitXml_EscapedNameAndMessage_MatchTheirTrxResult(string arguments, string xunitArguments, string message, string xunitMessage)
    {
        var stack = StackTrace("/src/facade/tests", FacadeTrxAssertionLine);
        var failed = new TrxResult(RoutingClass, RerouteMethod, "Failed", arguments, message, stack, EqualExceptionType);
        var xunitName = $"{RoutingClass}.{RerouteMethod}{xunitArguments}";
        var xml = XunitXmlOf(
        [
            new XunitTest(failed with { ExceptionType = TrueExceptionType }, xunitName, "Assert.True() Failure"),
            new XunitTest(failed, xunitName, xunitMessage),
        ]);

        var summary = FacadeTestResults.Parse(Trx([failed, .. AllPassedResults().Skip(2)]), xml, 2);

        var item = Assert.Single(summary.Failures);
        Assert.Equal(FailureItemIds.Test(RoutingClass, $"{RoutingClass}.{RerouteMethod}{arguments}"), item.ItemId);
        Assert.Equal(
            ExpectedNature(EqualExceptionType, "RoutingTests.cs", FacadeTrxAssertionLine, message.Replace("\r\n", "\n", StringComparison.Ordinal)),
            item.Nature);
    }

    // Without xunit XML the type is the one xunit writes ahead of a non-assertion exception's message,
    // as "Type : message"; an assertion message that merely starts with a dotted name and a colon names none.
    [Theory]
    [InlineData("System.InvalidOperationException : boom", "System.InvalidOperationException")]
    [InlineData("System.Net.Http.HttpRequestException : Connection refused (127.0.0.1:1)", "System.Net.Http.HttpRequestException")]
    [InlineData("Erewhon.Facade.Tests.Support.StubBackend+StubFailure : stopped", "Erewhon.Facade.Tests.Support.StubBackend+StubFailure")]
    [InlineData("System.OperationCanceledException :", "System.OperationCanceledException")]
    [InlineData("Rest.getCompany: stub B received 0 requests, expected exactly 1", "")]
    [InlineData("Assert.Equal() Failure: Values differ", "")]
    public void FailedResult_WithoutXunitXml_TakesTheTypeXunitPrefixedToTheMessage(string message, string expectedExceptionType)
    {
        var summary = FacadeTestResults.Parse(FailedTrx(message, StackTrace("/src/facade/tests", FacadeTrxAssertionLine)), xunitXml: null, 2);

        var test = Assert.Single(summary.Failures, item => item.ItemId.StartsWith("TEST:", StringComparison.Ordinal));
        Assert.Equal(ExpectedNature(expectedExceptionType, "RoutingTests.cs", FacadeTrxAssertionLine, message), test.Nature);
        Assert.Equal(expectedExceptionType.Length == 0, summary.Failures.Any(item => item.ItemId == FailureItemIds.FacadeTrx));
    }

    // No failed result needs a type, so unusable xunit XML is no problem; the null case is the positive control.
    [Theory]
    [InlineData("")]
    [InlineData("<assemblies")]
    [InlineData("<assemblies/>")]
    public void AllPassed_WithUnusableXunitXml_AddsNoItem(string xunitXml)
    {
        var summary = FacadeTestResults.Parse(AllPassedTrx(), xunitXml, 0);

        AssertAllPassedCounts(summary);
        Assert.Empty(summary.Failures);
    }

    [Fact]
    public void EveryTrxProblem_IsListedInItsFixedOrder()
    {
        var results = FailedResults(FacadeTrxAssertionMessage, StackTrace("/src/facade/tests", FacadeTrxAssertionLine), EqualExceptionType)
            .Where(result => result.ClassName != ListenerClass)
            .ToArray();

        var summary = FacadeTestResults.Parse(Trx(results), xunitXml: null, 3);

        var item = Assert.Single(summary.Failures, failure => failure.ItemId == FailureItemIds.FacadeTrx);
        Assert.Equal(
            "TRX file holds no results for ListenerIsolationTests; "
                + UntypedPrefix + "no xunit XML results were supplied; "
                + "dotnet test exited 3, so it did not complete normally and its results are unverified",
            item.Detail);
    }

    // ---- Links, special files and oversized files ----------------------------------------------

    /// <summary>
    /// Each result file, as <see cref="Read_LinkSpecialOrOversizedFile_IsRefusedPromptly"/> names it, crossed
    /// with each kind of entry planted at its name.
    /// </summary>
    public static TheoryData<string, string> RefusedResultFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in new[] { "trx", "exit-code", "xunit-xml", "unneeded-xunit-xml" })
        {
            foreach (var kind in new[] { "fifo", "link-to-fifo", "link-to-file", "oversized" })
            {
                data.Add(file, kind);
            }
        }

        return data;
    }

    // A FIFO, a link to a FIFO, a link to a valid file outside the results directory, and a sparse file one
    // byte over its limit are each refused at once: never waited on, followed or read in full. The refusal is
    // the file's usual unreadable problem, and the other two files are valid, so it is the only problem. An
    // xunit XML file that no failed result needs stays no problem. Read runs on a pool thread under a
    // deadline, so a read that blocks fails the test instead of hanging it.
    [Theory]
    [MemberData(nameof(RefusedResultFiles))]
    public async Task Read_LinkSpecialOrOversizedFile_IsRefusedPromptly(string file, string kind)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the facade test results are read only on Linux");
        var ct = TestContext.Current.CancellationToken;
        var (name, byteLimit, validContent) = file switch
        {
            "trx" => (FacadeTestResults.TrxFileName, FacadeTestResults.ResultsFileByteLimit, AllPassedTrx()),
            "exit-code" => (FacadeTestResults.ExitCodeFileName, FacadeTestResults.ExitCodeByteLimit, "0"),
            "xunit-xml" => (FacadeTestResults.XunitXmlFileName, FacadeTestResults.ResultsFileByteLimit, OneFailedXunitXml()),
            "unneeded-xunit-xml" => (FacadeTestResults.XunitXmlFileName, FacadeTestResults.ResultsFileByteLimit, XunitXml(AllPassedResults())),
            _ => throw new ArgumentOutOfRangeException(nameof(file), file, "unknown result file"),
        };
        if (file != "trx")
        {
            WriteTrx(file == "xunit-xml" ? OneFailedTrx() : AllPassedTrx());
        }

        if (file != "exit-code")
        {
            WriteExitCode(file == "xunit-xml" ? "2" : "0");
        }

        var relative = $"artifacts/facade-tests/{name}";
        var path = Temp.PathOf(relative);
        PlantRefusedFile(path, kind, byteLimit, validContent);

        var summary = await Task.Run(() => Read(), ct).WaitAsync(PromptReadLimit, ct);

        // The refusal is ConfinedTree's, with that file's own byte limit, and names the planted file relative to
        // Temp's root.
        var refusal = ConfinedReadRefusal(relative, kind, byteLimit);
        switch (file)
        {
            case "trx":
                AssertThreeNotRunChecks(summary);
                Assert.Equal($"TRX file unreadable: {refusal}", Redacted(AssertSingleTrxItem(summary).Detail));
                Assert.Equal(0, summary.ExitCode);
                break;
            case "exit-code":
                AssertAllPassedCounts(summary);
                Assert.Equal($"exit-code file unreadable: {refusal}", Redacted(AssertSingleTrxItem(summary).Detail));
                Assert.Null(summary.ExitCode);
                break;
            case "xunit-xml":
                AssertUntypedFailure(summary, $"{UntypedPrefix}xunit XML results unreadable: {refusal}");
                Assert.Equal(2, summary.ExitCode);
                break;
            default:
                AssertAllPassedCounts(summary);
                Assert.Empty(summary.Failures);
                Assert.Equal(0, summary.ExitCode);
                break;
        }
    }

    // A directory at the TRX file's name is unreadable, not absent, in the wording the other two files use.
    [Fact]
    public void Read_TrxDirectory_GivesUnreadableItemAndThreeNotRunChecks()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the facade test results are read only on Linux");
        WriteExitCode("0");
        var trxPath = Temp.PathOf($"artifacts/facade-tests/{FacadeTestResults.TrxFileName}");
        Directory.CreateDirectory(trxPath);

        var summary = Read();

        AssertThreeNotRunChecks(summary);

        // The detail names the TRX file by its full path, which both sides show with Temp's root redacted.
        Assert.Equal(Redacted($"TRX file unreadable: {trxPath} is a directory"), Redacted(AssertSingleTrxItem(summary).Detail));
    }

    // The results directory, or the artifacts directory above it, replaced by a link to a directory of valid
    // results is never followed, and replaced by a file is never read, so every file is unreadable for that
    // reason and nothing from the link's target counts. expectedRefusal is ConfinedTree's refusal of each file,
    // with {0} standing for that file's path relative to Temp's root.
    [Theory]
    [InlineData(
        "artifacts/facade-tests",
        true,
        "ConfinedTree never reads through a link: refused '{0}', because 'artifacts/facade-tests' is a symbolic link.")]
    [InlineData("artifacts", true, "ConfinedTree never reads through a link: refused '{0}', because 'artifacts' is a symbolic link.")]
    [InlineData("artifacts/facade-tests", false, "ConfinedTree could not open 'artifacts/facade-tests': it is not a directory.")]
    public async Task Read_ResultsDirectoryBehindALinkOrAFile_ReadsNothing(string component, bool link, string expectedRefusal)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the facade test results are read only on Linux");
        var ct = TestContext.Current.CancellationToken;
        Temp.Write($"outside/facade-tests/{FacadeTestResults.TrxFileName}", AllPassedTrx());
        Temp.Write($"outside/facade-tests/{FacadeTestResults.ExitCodeFileName}", "0");
        Temp.Write($"outside/facade-tests/{FacadeTestResults.XunitXmlFileName}", XunitXml(AllPassedResults()));
        var componentPath = Temp.PathOf(component);
        if (link)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(componentPath)!);
            Directory.CreateSymbolicLink(componentPath, Temp.PathOf(component == "artifacts" ? "outside" : "outside/facade-tests"));
        }
        else
        {
            Temp.Write(component, "not a directory");
        }

        var summary = await Task.Run(() => Read(), ct).WaitAsync(PromptReadLimit, ct);

        AssertThreeNotRunChecks(summary);

        // ConfinedTree's refusals name their paths relative to Temp's root, so the detail reads the same in every run.
        var trxRefusal = string.Format(
            CultureInfo.InvariantCulture, expectedRefusal, $"artifacts/facade-tests/{FacadeTestResults.TrxFileName}");
        var exitCodeRefusal = string.Format(
            CultureInfo.InvariantCulture, expectedRefusal, $"artifacts/facade-tests/{FacadeTestResults.ExitCodeFileName}");
        Assert.Equal(
            $"TRX file unreadable: {trxRefusal}; exit-code file unreadable: {exitCodeRefusal}",
            Redacted(AssertSingleTrxItem(summary).Detail));
        Assert.Null(summary.ExitCode);
    }

    private FacadeTestSummary Read() => new FacadeTestResults(new HarnessOptions { RepoRoot = Temp.Root }).Read();

    // Plants at path a FIFO, a link to a FIFO or to a file holding validContent (both outside the results
    // directory), or a sparse file one byte longer than byteLimit.
    private void PlantRefusedFile(string path, string kind, int byteLimit, string validContent)
    {
        var outsideName = $"outside/{Path.GetFileName(path)}";
        switch (kind)
        {
            case "fifo":
                MakeFifo(path);
                break;
            case "link-to-fifo":
                Directory.CreateDirectory(Temp.PathOf("outside"));
                MakeFifo(Temp.PathOf(outsideName));
                File.CreateSymbolicLink(path, Temp.PathOf(outsideName));
                break;
            case "link-to-file":
                File.CreateSymbolicLink(path, Temp.Write(outsideName, validContent));
                break;
            case "oversized":
                using (var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write))
                {
                    RandomAccess.SetLength(handle, byteLimit + 1L);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown kind of refused file");
        }
    }

    // The refusal ConfinedTree gives, reading with byteLimit (the evidence limit unless the reader sets its own)
    // below Temp's root, for what PlantRefusedFile planted at relative. The message names the path relative to
    // the root, so it reads the same in every run.
    private static string ConfinedReadRefusal(string relative, string kind, int byteLimit = HarnessOptions.EvidenceByteLimit) => kind switch
    {
        "fifo" => $"ConfinedTree could not read '{relative}': it is not a regular file (a FIFO).",
        "link-to-fifo" or "link-to-file" =>
            $"ConfinedTree never reads through a link: refused '{relative}', because '{relative}' is a symbolic link.",
        "oversized" => string.Create(
            CultureInfo.InvariantCulture,
            $"ConfinedTree could not read '{relative}': it exceeds the {byteLimit}-byte limit."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown kind of refused file"),
    };

    // A path below Temp is shown in the failure with Temp's root redacted.
    private void MakeFifo(string path)
    {
        if (MkFifo(path, FifoMode) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            Assert.Fail($"mkfifo could not make {Redacted(path)}: {Marshal.GetPInvokeErrorMessage(errno)}");
        }
    }

    // mkfifo(3) from the Linux C library. The path is passed as UTF-8, the bytes the kernel reads, despite
    // CA2101, which asks for the UTF-16 of Windows APIs; DllImport, not the LibraryImport that SYSLIB1054
    // suggests, because its generated marshalling needs unsafe code, which this project does not enable.
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    private void WriteTrx(string trxXml) =>
        Temp.Write($"artifacts/facade-tests/{FacadeTestResults.TrxFileName}", trxXml);

    private void WriteExitCode(string content) =>
        Temp.Write($"artifacts/facade-tests/{FacadeTestResults.ExitCodeFileName}", content);

    private void WriteXunitXml(string xunitXml) =>
        Temp.Write($"artifacts/facade-tests/{FacadeTestResults.XunitXmlFileName}", xunitXml);

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

    // The failed REST routing invocation keeps its TEST item, with an empty exception-type slot, and the
    // CHK-FACADE-TRX item, the only other item, says why the type is not recorded. A detail read below Temp
    // names its files by their full path, so it is shown with Temp's root redacted.
    private void AssertUntypedFailure(FacadeTestSummary summary, string expectedDetailStart)
    {
        Assert.Equal(2, summary.Failures.Count);
        var test = Assert.Single(summary.Failures, item => item.ItemId == FailureItemIds.Test(RoutingClass, RoutingTestName(RestArguments)));
        Assert.Equal(ExpectedNature(string.Empty, "RoutingTests.cs", FacadeTrxAssertionLine, FacadeTrxAssertionMessage), test.Nature);
        var trx = Assert.Single(summary.Failures, item => item.ItemId == FailureItemIds.FacadeTrx);
        Assert.Equal(FailureItemIds.Missing, trx.Nature);
        Assert.StartsWith(expectedDetailStart, Redacted(trx.Detail), StringComparison.Ordinal);
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

    // The REST routing invocation fails an Assert.Equal; everything else passes.
    private static string OneFailedTrx() => FailedTrx(FacadeTrxAssertionMessage, StackTrace("/src/facade/tests", FacadeTrxAssertionLine));

    private static string OneFailedXunitXml() =>
        XunitXml(FailedResults(FacadeTrxAssertionMessage, StackTrace("/src/facade/tests", FacadeTrxAssertionLine), EqualExceptionType));

    private static string FailedTrx(string message, string stackTrace) =>
        Trx(FailedResults(message, stackTrace, exceptionType: null));

    // The REST routing invocation fails with the given message, stack trace and exception type, the
    // last recorded only in the xunit XML; everything else passes.
    private static TrxResult[] FailedResults(string message, string stackTrace, string? exceptionType)
    {
        var results = AllPassedResults();
        results[0] = results[0] with { Outcome = "Failed", Message = message, StackTrace = stackTrace, ExceptionType = exceptionType };
        return results;
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

    // The xunit XML of the same run as Trx(results). xunit escapes the display name and message it writes
    // there; the fixtures' names and messages hold no control character, so only its backslash and
    // double-quote escapes apply. The full escaping is covered by literal values in its own theory.
    private static string XunitXml(params TrxResult[] results) =>
        XunitXmlOf([.. results.Select(result => new XunitTest(
            result,
            EscapeQuotes($"{result.ClassName}.{result.MethodName}{result.Arguments}"),
            result.Message is null ? null : EscapeQuotes(result.Message)))]);

    private static string EscapeQuotes(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    // The shape xunit.v3 4.0.2 writes with --report-xunit-xml in the same run: an assemblies root, one
    // collection per test class, and test elements carrying the class name and the escaped display name
    // but ids of their own; a failed test holds a failure with its exception type, escaped message and
    // stack trace.
    private static string XunitXmlOf(IReadOnlyList<XunitTest> tests)
    {
        var collections = new List<XElement>();
        var testNumber = 0;
        foreach (var group in tests.GroupBy(test => test.Result.ClassName, StringComparer.Ordinal))
        {
            var elements = new List<XElement>();
            foreach (var (test, name, message) in group)
            {
                testNumber++;
                var element = new XElement(
                    "test",
                    new XAttribute("id", $"10000000-0000-0000-0000-{testNumber:D12}"),
                    new XAttribute("name", name),
                    new XAttribute("result", XunitResult(test.Outcome)),
                    new XAttribute("time", "0"),
                    new XAttribute("type", test.ClassName),
                    new XAttribute("method", test.MethodName));
                if (test.Outcome == "Failed")
                {
                    var failure = new XElement("failure");
                    if (test.ExceptionType is not null)
                    {
                        failure.Add(new XAttribute("exception-type", test.ExceptionType));
                    }

                    failure.Add(
                        new XElement("message", message ?? string.Empty),
                        new XElement("stack-trace", test.StackTrace ?? string.Empty));
                    element.Add(failure);
                }

                elements.Add(element);
            }

            collections.Add(new XElement(
                "collection",
                new XAttribute("name", $"Test collection for {group.Key}"),
                new XAttribute("id", $"20000000-0000-0000-0000-{collections.Count + 1:D12}"),
                new XAttribute("total", elements.Count),
                new XAttribute("failed", group.Count(test => test.Result.Outcome == "Failed")),
                elements));
        }

        var assemblies = new XElement(
            "assemblies",
            new XAttribute("schema-version", "3"),
            new XElement(
                "assembly",
                new XAttribute("name", "/src/facade/tests/Erewhon.Facade.Tests/bin/Release/net10.0/Erewhon.Facade.Tests.dll"),
                new XAttribute("test-framework", "xUnit.net v3 4.0.2"),
                new XAttribute("total", tests.Count),
                new XAttribute("failed", tests.Count(test => test.Result.Outcome == "Failed")),
                collections));
        return assemblies.ToString(SaveOptions.DisableFormatting);
    }

    private static string XunitResult(string outcome) => outcome switch
    {
        "Passed" => "Pass",
        "Failed" => "Fail",
        _ => "NotRun",
    };

    // One xunit XML test element: the result it records, and its display name and message as written.
    private sealed record XunitTest(TrxResult Result, string Name, string? Message);

    // ExceptionType is written only to the xunit XML, as xunit.v3 records it; the TRX carries none.
    private sealed record TrxResult(
        string ClassName,
        string MethodName,
        string Outcome,
        string? Arguments = null,
        string? Message = null,
        string? StackTrace = null,
        string? ExceptionType = null);
}

// ---- The JADE-unchanged check -------------------------------------------------------------------
// The JADE-unchanged check: no difference and no working-tree change under Erewhon/ since the
// starting commit. Scripted git runners drive every git outcome, so missing, failing and
// unattributable git evidence is shown to give not-run rather than a pass; throwaway repositories in
// a temporary directory confirm the same verdicts against real git. The harness's own repository is
// never touched. These members live in this class because the harness test project keeps its fixed
// set of test classes, and the CHK-JADE-UNCHANGED items the check produces are failure identities
// this class covers.
public sealed partial class KnownLimitationsRegisterTests
{
    // StartingCommit is the starting commit the results report fixtures above declare.
    private const string ModifiedBlob = "1111111111111111111111111111111111111111";
    private const string AddedBlob = "2222222222222222222222222222222222222222";
    private const string Sha256Blob = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // A symbolic link's text, and the ids real git stores for a link with that text (mode 120000, read
    // back with ls-files --stage after staging it) in a SHA-1 and a SHA-256 repository.
    private const string LinkText = "target.txt";
    private const string LinkSha1Blob = "4cbb553f3f4ac2ee7b01ff6c951d6bf583c39c15";
    private const string LinkSha256Blob = "8edaa00b6a3bb3f93168959cad1a6e971a6a448c06e146992be90e1259517afd";

    // ---- Inputs that stop the check before git runs ---------------------------------------------

    [Fact]
    public async Task JadeUnchanged_NoGitMetadata_IsNotRunWithoutCallingGit()
    {
        var git = new ScriptedGit(_ => ScriptedGit.Ok());

        var result = await ScriptedJadeUnchangedCheck(git).RunAsync(TestContext.Current.CancellationToken);

        AssertJadeUnchangedNotRun(result, "no .git metadata at ");
        Assert.Empty(git.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-x")]
    [InlineData("--output=x")]
    public async Task UnusableStartingCommit_IsNotRunWithoutCallingGit(string startingCommit)
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = new ScriptedGit(_ => ScriptedGit.Ok());

        var result = await ScriptedJadeUnchangedCheck(git, startingCommit).RunAsync(TestContext.Current.CancellationToken);

        AssertJadeUnchangedNotRun(result, "is not a commit id");
        Assert.Empty(git.Calls);
    }

    // ---- git failures --------------------------------------------------------------------------

    [Theory]
    [InlineData("diff", "exited", 128, "fatal: bad revision '19a5189e'", "exited 128: fatal: bad revision '19a5189e'")]
    [InlineData("diff", "unavailable", -1, "", "could not start")]
    [InlineData("status", "timed-out", -1, "", "timed out")]
    [InlineData("status", "exited", 129, "", "exited 129")]
    [InlineData("ls-tree", "exited", 128, "fatal: not a tree object", "exited 128: fatal: not a tree object")]
    public async Task JadeUnchanged_FailingGitCommand_IsNotRun(string command, string failureKind, int exitCode, string stderr, string expectedReason)
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = new ScriptedGit(args => args[0] == command
            ? ScriptedGit.Failure(failureKind, exitCode, stderr)
            : ScriptedGit.Ok());

        var result = await ScriptedJadeUnchangedCheck(git).RunAsync(TestContext.Current.CancellationToken);

        var detail = AssertJadeUnchangedNotRun(result, $"git {command} ");
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
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = new ScriptedGit(args => args[0] switch
        {
            "diff" => ScriptedGit.Ok(diffOutput),
            "status" => ScriptedGit.Ok(statusOutput),
            _ => ScriptedGit.Ok("Erewhon/a.scm\0"),
        });

        var result = await ScriptedJadeUnchangedCheck(git).RunAsync(TestContext.Current.CancellationToken);

        AssertJadeUnchangedNotRun(result, "that name no path");
    }

    // ---- Verdicts from git output --------------------------------------------------------------

    [Fact]
    public async Task EmptyOutputs_PassWithTrackedPathCounts()
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = new ScriptedGit(args => args[0] == "ls-tree"
            ? ScriptedGit.Ok("Erewhon/a.scm\0Erewhon/DataFiles/b.txt\0")
            : ScriptedGit.Ok());

        var result = await ScriptedJadeUnchangedCheck(git).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new CheckResult(CheckNames.JadeUnchanged, CheckOutcomes.Pass, 2, 2, $"no change under Erewhon/ since starting commit {StartingCommit}"),
            result.Result with { Detail = Redacted(result.Result.Detail) });
        Assert.Empty(result.Failures);
        Assert.Equal(new JadeUnchangedSummary(2, 0), result.Summary);
    }

    [Fact]
    public async Task Changes_GiveOneSortedItemPerPathWithBlobIds()
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = new ScriptedGit(args => args[0] switch
        {
            "diff" => ScriptedGit.Ok("M\0Erewhon/b.scm\0D\0Erewhon/a.ddx\0"),
            "status" => ScriptedGit.Ok(" M Erewhon/b.scm\0 D Erewhon/a.ddx\0?? Erewhon/c.txt\0"),
            "ls-tree" => ScriptedGit.Ok("Erewhon/a.ddx\0Erewhon/b.scm\0Erewhon/z.mul\0"),
            "hash-object" => ScriptedGit.Ok((args[^1] == "Erewhon/b.scm" ? ModifiedBlob : AddedBlob) + "\n"),
            _ => ScriptedGit.Exited(1, "unexpected git command"),
        });

        var result = await ScriptedJadeUnchangedCheck(git).RunAsync(TestContext.Current.CancellationToken);

        FailureItem[] expected =
        [
            new(FailureItemIds.JadeUnchangedPath("Erewhon/a.ddx"), FailureItemIds.Deleted),
            new(FailureItemIds.JadeUnchangedPath("Erewhon/b.scm"), FailureItemIds.Modified(ModifiedBlob)),
            new(FailureItemIds.JadeUnchangedPath("Erewhon/c.txt"), FailureItemIds.Added(AddedBlob)),
        ];
        Assert.Equal(expected, result.Failures);

        // The three tracked paths and the added c.txt are checked; only the tracked z.mul is unchanged.
        Assert.Equal(
            new CheckResult(CheckNames.JadeUnchanged, CheckOutcomes.Fail, 4, 1, $"3 path(s) under Erewhon/ changed since starting commit {StartingCommit}"),
            result.Result with { Detail = Redacted(result.Result.Detail) });
        Assert.Equal(new JadeUnchangedSummary(4, 3), result.Summary);

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
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = OneChange(change, ScriptedGit.Failure(failureKind, exitCode, stderr));

        var result = await ScriptedJadeUnchangedCheck(git).RunAsync(TestContext.Current.CancellationToken);

        var path = ChangedPath(change);
        var detail = AssertJadeUnchangedNotRun(result, $"git hash-object -- {path} ");
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
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = OneChange(change, ScriptedGit.Ok(stdout));

        var result = await ScriptedJadeUnchangedCheck(git).RunAsync(TestContext.Current.CancellationToken);

        var path = ChangedPath(change);
        var detail = AssertJadeUnchangedNotRun(result, "which is not an object id");
        Assert.Equal($"git hash-object -- {path} printed '{printed}', which is not an object id", detail);
        string[] hashArgs = ["hash-object", "--", path];
        Assert.Equal(hashArgs, git.Calls[^1].Args);
    }

    // pathsChecked counts the tracked Erewhon/b.scm, plus Erewhon/c.txt when that is the added path.
    [Theory]
    [InlineData("modified", 1)]
    [InlineData("added", 2)]
    public async Task Sha256HashObjectAnswer_GivesItsBlobId(string change, int pathsChecked)
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = OneChange(change, ScriptedGit.Ok(Sha256Blob + "\n"));

        var result = await ScriptedJadeUnchangedCheck(git).RunAsync(TestContext.Current.CancellationToken);

        var nature = change == "added" ? FailureItemIds.Added(Sha256Blob) : FailureItemIds.Modified(Sha256Blob);
        Assert.Equal(new FailureItem(FailureItemIds.JadeUnchangedPath(ChangedPath(change)), nature), Assert.Single(result.Failures));
        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);
        Assert.Equal(pathsChecked, result.Result.Cases);
        Assert.Equal(pathsChecked - 1, result.Result.Passed);
        Assert.Equal(new JadeUnchangedSummary(pathsChecked, 1), result.Summary);
    }

    // ---- Symbolic links ------------------------------------------------------------------------

    // pathsChecked counts the tracked Erewhon/b.scm, plus Erewhon/c.txt when that is the added path.
    [Theory]
    [InlineData("modified", "sha1\n", LinkSha1Blob, 1)]
    [InlineData("added", "sha1\n", LinkSha1Blob, 2)]
    [InlineData("modified", "sha256\n", LinkSha256Blob, 1)]
    [InlineData("added", "sha256\n", LinkSha256Blob, 2)]
    public async Task SymbolicLink_IsHashedFromItsLinkTextInTheRepositoryObjectFormat(string change, string objectFormat, string expectedBlob, int pathsChecked)
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        CreateSymbolicLink(ChangedPath(change), LinkText);
        var git = OneChange(change, ScriptedGit.Exited(1, "hash-object would follow the link"), ScriptedGit.Ok(objectFormat));

        var result = await ScriptedJadeUnchangedCheck(git).RunAsync(TestContext.Current.CancellationToken);

        var nature = change == "added" ? FailureItemIds.Added(expectedBlob) : FailureItemIds.Modified(expectedBlob);
        Assert.Equal(new FailureItem(FailureItemIds.JadeUnchangedPath(ChangedPath(change)), nature), Assert.Single(result.Failures));
        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);
        Assert.Equal(pathsChecked, result.Result.Cases);
        Assert.Equal(pathsChecked - 1, result.Result.Passed);
        Assert.Equal(new JadeUnchangedSummary(pathsChecked, 1), result.Summary);

        // diff, status and ls-tree, then the object format lookup; hash-object never runs for a link.
        Assert.Equal(4, git.Calls.Count);
        string[] formatArgs = ["rev-parse", "--show-object-format"];
        Assert.Equal(formatArgs, git.Calls[^1].Args);
        Assert.Equal(TempRootToken, Redacted(git.Calls[^1].RepoRoot));
        Assert.Equal(Git.DefaultTimeout, git.Calls[^1].Timeout);
    }

    [Theory]
    [InlineData("unavailable", -1, "", "could not start")]
    [InlineData("timed-out", -1, "", "timed out")]
    [InlineData("exited", 128, "fatal: not a git repository: .git", "exited 128: fatal: not a git repository: .git")]
    [InlineData("exited", 129, "", "exited 129")]
    public async Task FailingObjectFormatLookup_IsNotRun(string failureKind, int exitCode, string stderr, string expectedReason)
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        CreateSymbolicLink(ChangedPath("added"), LinkText);
        var git = OneChange("added", ScriptedGit.Ok(AddedBlob + "\n"), ScriptedGit.Failure(failureKind, exitCode, stderr));

        var result = await ScriptedJadeUnchangedCheck(git).RunAsync(TestContext.Current.CancellationToken);

        var detail = AssertJadeUnchangedNotRun(result, "git rev-parse --show-object-format ");
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
        Directory.CreateDirectory(Temp.PathOf(".git"));
        CreateSymbolicLink(ChangedPath("added"), LinkText);
        var git = OneChange("added", ScriptedGit.Ok(AddedBlob + "\n"), ScriptedGit.Ok(stdout));

        var result = await ScriptedJadeUnchangedCheck(git).RunAsync(TestContext.Current.CancellationToken);

        var detail = AssertJadeUnchangedNotRun(result, "which is not an object format");
        Assert.Equal($"git rev-parse --show-object-format printed '{printed}', which is not an object format", detail);
        Assert.DoesNotContain(git.Calls, call => call.Args[0] == "hash-object");
    }

    [Fact]
    public async Task SymbolicLinkTextHoldingAReplacementCharacter_IsNotRunWithoutHashing()
    {
        // The runtime decodes link text bytes that are not valid UTF-8 as U+FFFD, so their bytes, and
        // the blob git stores, can no longer be recovered from the text.
        Directory.CreateDirectory(Temp.PathOf(".git"));
        CreateSymbolicLink(ChangedPath("added"), "caf\uFFFD.scm");
        var git = OneChange("added", ScriptedGit.Ok(AddedBlob + "\n"), ScriptedGit.Ok("sha1\n"));

        var result = await ScriptedJadeUnchangedCheck(git).RunAsync(TestContext.Current.CancellationToken);

        AssertJadeUnchangedNotRun(result, "symbolic link Erewhon/c.txt has link text 'caf\uFFFD.scm' that may not be valid UTF-8");
        Assert.Equal(3, git.Calls.Count);
    }

    [Fact]
    public async Task GitArguments_CarryTheStartingCommitAndTheJadePathspec()
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = new ScriptedGit(_ => ScriptedGit.Ok());

        await ScriptedJadeUnchangedCheck(git, $"  {StartingCommit} ").RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, git.Calls.Count);
        Assert.All(git.Calls, call => Assert.Equal(TempRootToken, Redacted(call.RepoRoot)));
        Assert.All(git.Calls, call => Assert.Equal(Git.DefaultTimeout, call.Timeout));
        string[] diffArgs = ["diff", "--name-status", "--no-renames", "-z", StartingCommit, "--", "Erewhon/"];
        string[] statusArgs = ["status", "--porcelain", "--no-renames", "--untracked-files=all", "-z", "--", "Erewhon/"];
        string[] treeArgs = ["ls-tree", "-r", "--name-only", "-z", StartingCommit, "--", "Erewhon/"];
        Assert.Equal(diffArgs, git.Calls[0].Args);
        Assert.Equal(statusArgs, git.Calls[1].Args);
        Assert.Equal(treeArgs, git.Calls[2].Args);
    }

    [Fact]
    public void JadeUnchanged_NullGitRunner_Throws()
    {
        Assert.Throws<ArgumentNullException>("git", () => new JadeUnchangedCheck(new HarnessOptions { RepoRoot = Temp.Root }, null!));
    }

    // ---- Real git ------------------------------------------------------------------------------

    [Fact]
    public async Task RealGit_UnchangedTree_Passes()
    {
        var ct = TestContext.Current.CancellationToken;
        var commit = await JadeRepositoryAsync(ct);

        // A change outside Erewhon/ is not the check's concern.
        Temp.Write("facade/Program.cs", "class Program {}\n");
        Temp.Write("README.md", "changed\n");

        var result = await new JadeUnchangedCheck(new HarnessOptions { RepoRoot = Temp.Root, StartingCommit = commit }).RunAsync(ct);

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
        Temp.Write("Erewhon/ErewhonModelSchema.scm", "schema changed\n");
        File.Delete(Temp.PathOf("Erewhon/ErewhonDemoSystem.mul"));
        Temp.Write("Erewhon/DataFiles/New.txt", "added\n");
        var modifiedBlob = await TestGit.HashObjectAsync(Temp.Root, "Erewhon/ErewhonModelSchema.scm", ct);
        var addedBlob = await TestGit.HashObjectAsync(Temp.Root, "Erewhon/DataFiles/New.txt", ct);

        var result = await new JadeUnchangedCheck(new HarnessOptions { RepoRoot = Temp.Root, StartingCommit = commit }).RunAsync(ct);

        FailureItem[] expected =
        [
            new(FailureItemIds.JadeUnchangedPath("Erewhon/DataFiles/New.txt"), FailureItemIds.Added(addedBlob)),
            new(FailureItemIds.JadeUnchangedPath("Erewhon/ErewhonDemoSystem.mul"), FailureItemIds.Deleted),
            new(FailureItemIds.JadeUnchangedPath("Erewhon/ErewhonModelSchema.scm"), FailureItemIds.Modified(modifiedBlob)),
        ];
        Assert.Equal(expected, result.Failures);
        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);

        // The three paths of the starting commit and the untracked New.txt are checked.
        Assert.Equal(4, result.Result.Cases);
        Assert.Equal(1, result.Result.Passed);
        Assert.Equal(new JadeUnchangedSummary(4, 3), result.Summary);
    }

    // A path committed after the starting commit is absent from that commit's tree: diff reports it as
    // added while it is in the working tree, and status alone reports it as deleted once it is removed
    // from the working tree. Either way it is checked on top of the three starting paths, and fails.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealGit_PathCommittedSinceTheStartingCommit_IsCheckedAndFails(bool deletedFromWorkingTree)
    {
        var ct = TestContext.Current.CancellationToken;
        var commit = await JadeRepositoryAsync(ct);
        Temp.Write("Erewhon/DataFiles/Later.txt", "committed later\n");
        await TestGit.CommitAllAsync(Temp.Root, "a JADE file committed after the starting commit", ct);
        var nature = FailureItemIds.Added(await TestGit.HashObjectAsync(Temp.Root, "Erewhon/DataFiles/Later.txt", ct));
        if (deletedFromWorkingTree)
        {
            File.Delete(Temp.PathOf("Erewhon/DataFiles/Later.txt"));
            nature = FailureItemIds.Deleted;
        }

        var result = await new JadeUnchangedCheck(new HarnessOptions { RepoRoot = Temp.Root, StartingCommit = commit }).RunAsync(ct);

        Assert.Equal(new FailureItem(FailureItemIds.JadeUnchangedPath("Erewhon/DataFiles/Later.txt"), nature), Assert.Single(result.Failures));
        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);
        Assert.Equal(4, result.Result.Cases);
        Assert.Equal(3, result.Result.Passed);
        Assert.Equal(new JadeUnchangedSummary(4, 1), result.Summary);
    }

    [Fact]
    public async Task RealGit_UnresolvableStartingCommit_IsNotRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await JadeRepositoryAsync(ct);

        var result = await new JadeUnchangedCheck(
            new HarnessOptions { RepoRoot = Temp.Root, StartingCommit = "0123456789abcdef0123456789abcdef01234567" }).RunAsync(ct);

        var detail = AssertJadeUnchangedNotRun(result, "git diff ");
        Assert.Contains("exited 128", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealGit_UntrackedNestedRepository_IsNotRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var commit = await JadeRepositoryAsync(ct);

        // git status lists a nested repository as one untracked directory, which hash-object cannot hash.
        Temp.Write("Erewhon/nested/Inner.txt", "inner\n");
        await TestGit.InitAsync(Temp.PathOf("Erewhon/nested"), ct);

        var result = await new JadeUnchangedCheck(new HarnessOptions { RepoRoot = Temp.Root, StartingCommit = commit }).RunAsync(ct);

        AssertJadeUnchangedNotRun(result, "git hash-object -- Erewhon/nested/ exited 128");
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
            commit = await TestGit.CommitAllAsync(Temp.Root, "commit the link", ct);
        }

        File.Delete(Temp.PathOf(linkPath));
        CreateSymbolicLink(linkPath, currentTarget);

        var result = await new JadeUnchangedCheck(new HarnessOptions { RepoRoot = Temp.Root, StartingCommit = commit }).RunAsync(ct);

        // The id git stores for the link once it is staged in this throwaway repository: a mode 120000
        // blob of the link text, never the target's content.
        await TestGit.RunAsync(Temp.Root, ct, "add", "--", linkPath);
        var staged = (await TestGit.RunAsync(Temp.Root, ct, "ls-files", "--stage", "--", linkPath)).Split([' ', '\t']);
        Assert.Equal("120000", staged[0]);
        var nature = change == "added" ? FailureItemIds.Added(staged[1]) : FailureItemIds.Modified(staged[1]);
        Assert.Equal(new FailureItem(FailureItemIds.JadeUnchangedPath(linkPath), nature), Assert.Single(result.Failures));
        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);
    }

    private JadeUnchangedCheck ScriptedJadeUnchangedCheck(ScriptedGit git, string startingCommit = StartingCommit) =>
        new(new HarnessOptions { RepoRoot = Temp.Root, StartingCommit = startingCommit }, git.Runner);

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
        var path = Temp.PathOf(relativePath);
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
        await TestGit.RunAsync(Temp.Root, ct, "init", "--quiet", "--initial-branch=main", $"--object-format={objectFormat}");
        Temp.Write("Erewhon/ErewhonModelSchema.scm", "schema\n");
        Temp.Write("Erewhon/ErewhonDemoSystem.mul", "load order\n");
        Temp.Write("Erewhon/DataFiles/Clients.txt", "Pauline Wild\n");
        Temp.Write("README.md", "readme\n");
        return await TestGit.CommitAllAsync(Temp.Root, "starting commit", ct);
    }

    // A detail can name the repository root, so it is compared unshown with the check's, and checked and
    // returned with Temp's root redacted.
    private string AssertJadeUnchangedNotRun(JadeUnchangedResult result, string expectedDetail)
    {
        Assert.Equal(CheckNames.JadeUnchanged, result.Result.Name);
        Assert.Equal(CheckOutcomes.NotRun, result.Result.Outcome);
        Assert.Equal(0, result.Result.Cases);
        Assert.Null(result.Summary);
        var item = Assert.Single(result.Failures);
        Assert.Equal(FailureItemIds.JadeUnchanged, item.ItemId);
        Assert.Equal(FailureItemIds.NotRun, item.Nature);
        Assert.True(
            string.Equals(result.Result.Detail, item.Detail, StringComparison.Ordinal),
            "The failing item must carry the check's detail.");
        var detail = Redacted(item.Detail);
        Assert.Contains(expectedDetail, detail, StringComparison.Ordinal);
        return detail!;
    }
}

// ---- The ordering-evidence check ----------------------------------------------------------------
// The ordering-evidence check: the commit that first added baseline/manifest.json must be an
// ancestor of the commit that first added a facade/ path. Scripted git runners show that missing
// metadata, shallow history, failing or malformed git answers and inconclusive ancestry give
// not-run, and that absent or equal commits never pass; throwaway repositories in a temporary
// directory confirm the verdicts against real git. The harness's own repository is never touched.
// These members live in this class because the harness test project keeps its fixed set of test
// classes, and the CHK-ORDERING items the check produces are failure identities this class covers.
public sealed partial class KnownLimitationsRegisterTests
{
    private const string BaselinePathspec = "baseline/manifest.json";
    private const string FacadePathspec = "facade/";

    private const string BaselineCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FacadeCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string LaterBaselineCommit = "cccccccccccccccccccccccccccccccccccccccc";
    private const string LaterFacadeCommit = "dddddddddddddddddddddddddddddddddddddddd";

    // ---- Evidence git cannot give --------------------------------------------------------------

    [Fact]
    public async Task Ordering_NoGitMetadata_IsNotRunWithoutCallingGit()
    {
        var git = Script();

        var result = await ScriptedOrderingEvidence(git).RunAsync(TestContext.Current.CancellationToken);

        AssertOrderingNotRun(result, "no .git metadata at ");
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
    public async Task Ordering_FailingGitCommand_IsNotRun(string step, string failureKind, int exitCode, string stderr, string expectedReason)
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var failure = ScriptedGit.Failure(failureKind, exitCode, stderr);
        var git = Script(
            shallow: step == "rev-parse" ? failure : null,
            baselineLog: step == BaselinePathspec ? failure : null,
            facadeLog: step == FacadePathspec ? failure : null);

        var result = await ScriptedOrderingEvidence(git).RunAsync(TestContext.Current.CancellationToken);

        var detail = AssertOrderingNotRun(result, "git ");
        Assert.Contains(expectedReason, detail, StringComparison.Ordinal);
        Assert.Equal(step == FacadePathspec ? BaselineCommit : null, result.BaselineCommit);
        Assert.Null(result.FacadeCommit);
        Assert.DoesNotContain(git.Calls, call => call.Args[0] == "merge-base");
    }

    [Fact]
    public async Task ShallowClone_IsNotRun()
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = Script(shallow: ScriptedGit.Ok("true\n"));

        var result = await ScriptedOrderingEvidence(git).RunAsync(TestContext.Current.CancellationToken);

        AssertOrderingNotRun(result, "is a shallow clone");
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
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var answer = ScriptedGit.Ok($"{firstLine}\n{LaterFacadeCommit}\n");
        var git = Script(
            baselineLog: pathspec == BaselinePathspec ? answer : null,
            facadeLog: pathspec == FacadePathspec ? answer : null);

        var result = await ScriptedOrderingEvidence(git).RunAsync(TestContext.Current.CancellationToken);

        var detail = AssertOrderingNotRun(result, $"printed '{firstLine}', which is not a commit id");
        Assert.Contains(pathspec, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(git.Calls, call => call.Args[0] == "merge-base");
    }

    // ---- Absent or equal commits ---------------------------------------------------------------

    [Theory]
    [InlineData(BaselineCommit + "\n", "no commit adds a path under facade/; baseline commit aaaaaaaaaaaa")]
    [InlineData("", "no commit adds a path under facade/ or adds baseline/manifest.json")]
    public async Task NoFacadeCommit_FailsWithNoFacadeCommit(string baselineLog, string expectedDetail)
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = Script(baselineLog: ScriptedGit.Ok(baselineLog), facadeLog: ScriptedGit.Ok());

        var result = await ScriptedOrderingEvidence(git).RunAsync(TestContext.Current.CancellationToken);

        AssertFail(result, FailureItemIds.NoFacadeCommit, expectedDetail);
        Assert.Null(result.FacadeCommit);
        Assert.DoesNotContain(git.Calls, call => call.Args[0] == "merge-base");
    }

    [Fact]
    public async Task FacadeCommitWithoutBaselineCommit_IsNotAncestor()
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = Script(baselineLog: ScriptedGit.Ok());

        var result = await ScriptedOrderingEvidence(git).RunAsync(TestContext.Current.CancellationToken);

        AssertFail(result, FailureItemIds.NotAncestor, "no commit adds baseline/manifest.json; first facade commit bbbbbbbbbbbb");
        Assert.Null(result.BaselineCommit);
        Assert.Equal(FacadeCommit, result.FacadeCommit);
        Assert.DoesNotContain(git.Calls, call => call.Args[0] == "merge-base");
    }

    [Fact]
    public async Task SameCommitAddingBoth_IsNotAncestor()
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = Script(facadeLog: ScriptedGit.Ok(BaselineCommit + "\n"));

        var result = await ScriptedOrderingEvidence(git).RunAsync(TestContext.Current.CancellationToken);

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
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = Script(mergeBase: answerKind == "exited" && exitCode == 0
            ? ScriptedGit.Ok()
            : ScriptedGit.Failure(answerKind, exitCode, string.Empty));

        var result = await ScriptedOrderingEvidence(git).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CheckNames.Ordering, result.Result.Name);
        Assert.Equal(expectedOutcome, result.Result.Outcome);
        Assert.Equal(expectedNature, result.Failure?.Nature);
        if (expectedNature is not null)
        {
            Assert.Equal(FailureItemIds.Ordering, result.Failure!.ItemId);
            Assert.True(
                string.Equals(result.Result.Detail, result.Failure.Detail, StringComparison.Ordinal),
                "The failing item must carry the check's detail.");
        }
        else
        {
            Assert.Equal(
                new CheckResult(CheckNames.Ordering, CheckOutcomes.Pass, 1, 1, "baseline commit aaaaaaaaaaaa is an ancestor of the first facade commit bbbbbbbbbbbb"),
                result.Result with { Detail = Redacted(result.Result.Detail) });
        }

        Assert.Equal(BaselineCommit, result.BaselineCommit);
        Assert.Equal(FacadeCommit, result.FacadeCommit);
    }

    [Fact]
    public async Task OldestAddingCommits_AreCompared()
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = Script(
            baselineLog: ScriptedGit.Ok($"{BaselineCommit}\n{LaterBaselineCommit}\n"),
            facadeLog: ScriptedGit.Ok($"{FacadeCommit}\n{LaterFacadeCommit}\n"));

        var result = await ScriptedOrderingEvidence(git).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CheckOutcomes.Pass, result.Result.Outcome);
        Assert.Equal(BaselineCommit, result.BaselineCommit);
        Assert.Equal(FacadeCommit, result.FacadeCommit);
        string[] mergeBaseArgs = ["merge-base", "--is-ancestor", BaselineCommit, FacadeCommit];
        Assert.Equal(mergeBaseArgs, git.Calls[^1].Args);
    }

    [Fact]
    public async Task GitArguments_AskForTheFirstAddingCommitOfEachPathspec()
    {
        Directory.CreateDirectory(Temp.PathOf(".git"));
        var git = Script();

        await ScriptedOrderingEvidence(git).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, git.Calls.Count);
        Assert.All(git.Calls, call => Assert.Equal(TempRootToken, Redacted(call.RepoRoot)));
        Assert.All(git.Calls, call => Assert.Equal(Git.DefaultTimeout, call.Timeout));
        Assert.Equal(LogArgs(BaselinePathspec), git.Calls[1].Args);
        Assert.Equal(LogArgs(FacadePathspec), git.Calls[2].Args);
    }

    [Fact]
    public void Ordering_NullGitRunner_Throws()
    {
        Assert.Throws<ArgumentNullException>("git", () => new OrderingEvidence(new HarnessOptions { RepoRoot = Temp.Root }, null!));
    }

    // ---- Real git ------------------------------------------------------------------------------

    [Fact]
    public async Task RealGit_BaselineCommittedBeforeFacade_Passes()
    {
        var ct = TestContext.Current.CancellationToken;
        await TestGit.InitAsync(Temp.Root, ct);
        Temp.Write("README.md", "start\n");
        await TestGit.CommitAllAsync(Temp.Root, "starting commit", ct);
        Temp.Write(BaselinePathspec, "{}\n");
        var baseline = await TestGit.CommitAllAsync(Temp.Root, "baseline", ct);
        Temp.Write("facade/src/Program.cs", "class Program {}\n");
        var facade = await TestGit.CommitAllAsync(Temp.Root, "facade", ct);
        Temp.Write("facade/src/Later.cs", "class Later {}\n");
        await TestGit.CommitAllAsync(Temp.Root, "more facade", ct);

        var result = await new OrderingEvidence(new HarnessOptions { RepoRoot = Temp.Root }).RunAsync(ct);

        Assert.Equal(CheckOutcomes.Pass, result.Result.Outcome);
        Assert.Null(result.Failure);
        AssertRealCommits(result, baseline, facade);
    }

    [Fact]
    public async Task RealGit_FacadeCommittedBeforeBaseline_IsNotAncestor()
    {
        var ct = TestContext.Current.CancellationToken;
        await TestGit.InitAsync(Temp.Root, ct);
        Temp.Write("README.md", "start\n");
        await TestGit.CommitAllAsync(Temp.Root, "starting commit", ct);
        Temp.Write("facade/src/Program.cs", "class Program {}\n");
        var facade = await TestGit.CommitAllAsync(Temp.Root, "facade", ct);
        Temp.Write(BaselinePathspec, "{}\n");
        var baseline = await TestGit.CommitAllAsync(Temp.Root, "baseline", ct);

        var result = await new OrderingEvidence(new HarnessOptions { RepoRoot = Temp.Root }).RunAsync(ct);

        Assert.Equal(CheckOutcomes.Fail, result.Result.Outcome);
        Assert.Equal(FailureItemIds.NotAncestor, result.Failure?.Nature);

        // The detail also names both commits by id, a hash of each fixture commit's content, so only its fixed
        // sentence is checked and the detail is not shown.
        Assert.True(
            result.Failure?.Detail?.Contains("is not an ancestor of the first facade commit", StringComparison.Ordinal) == true,
            "The detail must state that the baseline commit is not an ancestor of the first facade commit.");
        AssertRealCommits(result, baseline, facade);
    }

    [Fact]
    public async Task RealGit_ShallowClone_IsNotRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Temp.PathOf("source");
        var clone = Temp.PathOf("clone");
        Directory.CreateDirectory(source);
        await TestGit.InitAsync(source, ct);
        Temp.Write("source/" + BaselinePathspec, "{}\n");
        await TestGit.CommitAllAsync(source, "baseline", ct);
        Temp.Write("source/facade/src/Program.cs", "class Program {}\n");
        await TestGit.CommitAllAsync(source, "facade", ct);
        await TestGit.RunAsync(Temp.Root, ct, "clone", "--quiet", "--depth", "1", new Uri(source).AbsoluteUri, clone);

        var result = await new OrderingEvidence(new HarnessOptions { RepoRoot = clone }).RunAsync(ct);

        AssertOrderingNotRun(result, "is a shallow clone");
    }

    private OrderingEvidence ScriptedOrderingEvidence(ScriptedGit git) => new(new HarnessOptions { RepoRoot = Temp.Root }, git.Runner);

    // A real commit's id hashes the fixture commit's tree, parents, message and fixed identity and dates, so
    // the ids are compared with fixed messages that name each commit's role rather than its id.
    private static void AssertRealCommits(OrderingResult result, string baseline, string facade)
    {
        Assert.True(
            string.Equals(baseline, result.BaselineCommit, StringComparison.Ordinal),
            "The baseline commit must be the commit that first added baseline/manifest.json.");
        Assert.True(
            string.Equals(facade, result.FacadeCommit, StringComparison.Ordinal),
            "The facade commit must be the first commit that added a facade/ path.");
    }

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

    // The check's row is shown with Temp's root redacted; a failing item is shown without its detail.
    private void AssertFail(OrderingResult result, string expectedNature, string expectedDetail)
    {
        Assert.Equal(
            new CheckResult(CheckNames.Ordering, CheckOutcomes.Fail, 1, 0, expectedDetail),
            result.Result with { Detail = Redacted(result.Result.Detail) });
        Assert.Equal(new FailureItem(FailureItemIds.Ordering, expectedNature, Operation: null, Detail: expectedDetail), result.Failure);
    }

    // A detail can name the repository root, so it is compared unshown with the check's, and checked and
    // returned with Temp's root redacted.
    private string AssertOrderingNotRun(OrderingResult result, string expectedDetail)
    {
        Assert.Equal(CheckNames.Ordering, result.Result.Name);
        Assert.Equal(CheckOutcomes.NotRun, result.Result.Outcome);
        Assert.Equal(0, result.Result.Cases);
        var item = result.Failure;
        Assert.NotNull(item);
        Assert.Equal(FailureItemIds.Ordering, item.ItemId);
        Assert.Equal(FailureItemIds.NotRun, item.Nature);
        Assert.True(
            string.Equals(result.Result.Detail, item.Detail, StringComparison.Ordinal),
            "The failing item must carry the check's detail.");
        var detail = Redacted(item.Detail);
        Assert.Contains(expectedDetail, detail, StringComparison.Ordinal);
        return detail!;
    }
}

// ---- The summary mode ---------------------------------------------------------------------------
// How the summary mode prints to the descriptor behind standard output: a pipe whose reader has gone
// fails the write and exits 1 instead of counting as printed; a pipe with a reader receives exactly the
// final line and its line terminator; a seekable descriptor keeps the console writer; a full non-blocking
// descriptor is waited for and never handed to the console writer, so the outcome follows what its reader
// does. A pipe end the runtime opened reads as carrying close-on-exec, which no inherited standard output
// does, and a file opened by path cannot tell. A report outside the root, behind a link, a FIFO or over the
// evidence limit is refused at once and prints nothing. The report is a RESULTS.md in a temporary directory
// of the test's own. These members live in this class because the harness test project keeps its fixed set of
// test classes, and the summary mode prints the final line of the results report this class covers.
public sealed partial class KnownLimitationsRegisterTests
{
    private const string FinalLine = "22 of 22 operations equivalent, 0 documented differences";
    private const string AfterMarker = "after\n";

    // The last line of a file outside the repository root, which no refused report may print.
    private const string OutsideFinalLine = "a line from outside the repository";

    // Each one-byte send is charged a whole buffer of some hundred bytes, so a send buffer of the default
    // size fills after a few hundred sends; the bound ends the test if the socket never fills.
    private const int MaxFillBytes = 1024 * 1024;

    private const char FillByte = 'x';

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // Gives the mode time to reach its first write. Whether it has or not, the mode cannot return before the
    // test acts, because nothing else frees the descriptor.
    private static readonly TimeSpan WaitBeforeActing = TimeSpan.FromMilliseconds(100);

    // Bounds every wait on the mode and on the reader, so a mode that never returns fails the test.
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    // Created on first use, so only the summary mode tests create the report directory or the console;
    // released in Dispose. The report directory is a TempDirectory of its own, apart from Temp.
    private TempDirectory? _summaryRepoRoot;
    private MemoryStream? _summaryConsoleBytes;
    private StreamWriter? _summaryConsole;

    /// <summary>What a test does with the reading end of a full non-blocking socket while the mode waits.</summary>
    public enum ReaderAction
    {
        /// <summary>Closes the reading end without reading from it.</summary>
        CloseWithoutDraining,

        /// <summary>Reads everything sent to the socket.</summary>
        Drain,

        /// <summary>Leaves the reading end unread and cancels the run.</summary>
        CancelRun,
    }

    /// <summary>The summary mode test's own repository root, created on first use with the report written in it.</summary>
    private TempDirectory SummaryRepoRoot
    {
        get
        {
            if (_summaryRepoRoot is null)
            {
                // Assigned before the write, so Dispose deletes the directory even if the write fails.
                _summaryRepoRoot = new TempDirectory();
                _summaryRepoRoot.Write("RESULTS.md", $"# Results\n\nDetail line\n{FinalLine}\n\n");
            }

            return _summaryRepoRoot;
        }
    }

    /// <summary>The bytes the summary mode's console writer has written.</summary>
    private MemoryStream SummaryConsoleBytes => _summaryConsoleBytes ??= new MemoryStream();

    /// <summary>A console writer that encodes as Console.Out does on Linux, over <see cref="SummaryConsoleBytes"/>.</summary>
    private StreamWriter SummaryConsole => _summaryConsole ??= new StreamWriter(SummaryConsoleBytes, Utf8, leaveOpen: true) { NewLine = "\n" };

    [Fact]
    public async Task PipeWithoutReader_Exits1_AndPrintsNothing()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out);
        pipe.ClientSafePipeHandle.Dispose();

        var exitCode = await NewMode(pipe).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(0, SummaryConsoleBytes.Length);
    }

    [Fact]
    public async Task PipeWithReader_Exits0_AndReceivesExactlyTheFinalLine()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var reader = new AnonymousPipeClientStream(PipeDirection.In, pipe.ClientSafePipeHandle);

        var exitCode = await NewMode(pipe).RunAsync(TestContext.Current.CancellationToken);

        // The descriptor is still open after the mode, so the marker follows the mode's bytes; closing the
        // write end then ends the reader's stream.
        pipe.Write(Utf8.GetBytes(AfterMarker));
        pipe.Dispose();
        using var received = new MemoryStream();
        await reader.CopyToAsync(received, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(FinalLine + "\n" + AfterMarker, Utf8.GetString(received.ToArray()));
        Assert.Equal(0, SummaryConsoleBytes.Length);
    }

    // A write at the stream's own offset would leave the offset shared with the shell unmoved, so a
    // regular file is printed to through the console writer and the descriptor is not written.
    [Fact]
    public async Task SeekableDescriptor_Exits0_AndPrintsThroughTheConsoleWriter()
    {
        using var file = File.OpenHandle(SummaryRepoRoot.PathOf("stdout.txt"), FileMode.CreateNew, FileAccess.Write);

        var exitCode = await NewMode(file.DangerousGetHandle()).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(FinalLine + "\n", Utf8.GetString(SummaryConsoleBytes.ToArray()));
        Assert.Equal(0, RandomAccess.GetLength(file));
    }

    // A non-blocking descriptor that cannot take the line fails each direct write with EAGAIN, which is not a
    // failure to print: the mode waits and writes again, and never hands the line to the console writer, which
    // would count EPIPE as printed. A reader that goes fails the write (exit 1), a reader that drains receives
    // exactly the line after the fill bytes (exit 0), and a run cancelled while waiting exits 1 having written
    // nothing. A Unix stream socket whose reader never reads stays full, because only a read frees its send
    // buffer, so the mode is still waiting when the test acts, whatever the timing.
    [Theory]
    [InlineData(ReaderAction.CloseWithoutDraining, 1)]
    [InlineData(ReaderAction.Drain, 0)]
    [InlineData(ReaderAction.CancelRun, 1)]
    public async Task FullNonBlockingDescriptor_WaitsForTheReader_AndNeverPrintsThroughTheConsoleWriter(
        ReaderAction action,
        int expectedExitCode)
    {
        Assert.SkipUnless(
            !OperatingSystem.IsWindows(),
            "the mode writes to a descriptor directly only on systems other than Windows");
        var ct = TestContext.Current.CancellationToken;
        var endPoint = new UnixDomainSocketEndPoint(SummaryRepoRoot.PathOf("stdout.sock"));
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(endPoint);
        listener.Listen(1);
        using var writer = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await writer.ConnectAsync(endPoint, ct);
        using var reader = await listener.AcceptAsync(ct);
        writer.Blocking = false;
        var filled = FillSendBuffer(writer);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var running = NewMode(writer.Handle).RunAsync(run.Token);
        await Task.Delay(WaitBeforeActing, ct);
        Assert.False(running.IsCompleted, "the mode returned while the descriptor was still full");

        Task<byte[]>? received = null;
        switch (action)
        {
            case ReaderAction.CloseWithoutDraining:
                reader.Dispose();
                break;
            case ReaderAction.Drain:
                received = ReadToEndAsync(reader, ct);
                break;
            case ReaderAction.CancelRun:
                await run.CancelAsync();
                break;
        }

        var exitCode = await running.WaitAsync(WaitLimit, ct);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(0, SummaryConsoleBytes.Length);
        if (action is not ReaderAction.CloseWithoutDraining)
        {
            // The mode has returned, so ending the sending side ends the reader's stream after the mode's bytes.
            writer.Shutdown(SocketShutdown.Send);
            var bytes = await (received ?? ReadToEndAsync(reader, ct)).WaitAsync(WaitLimit, ct);
            var expected = new string(FillByte, filled) + (expectedExitCode == 0 ? FinalLine + "\n" : string.Empty);
            Assert.Equal(expected, Utf8.GetString(bytes));
        }
    }

    // A report path outside the repository root, a report that is a link to a file outside it, a report below
    // a linked directory, a FIFO nobody writes to and a report over the evidence limit are each refused at once,
    // exit 1 and print nothing: the outside file's last line, which a followed read would print, never reaches
    // standard output.
    [Theory]
    [InlineData("outside-path")]
    [InlineData("link-to-outside")]
    [InlineData("linked-directory")]
    [InlineData("fifo")]
    [InlineData("oversized")]
    public async Task RefusedReport_Exits1_AndPrintsNothing(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        using var outside = new TempDirectory();
        var outsideReport = outside.Write("RESULTS.md", $"# Notes\n\n{OutsideFinalLine}\n");
        var reportPath = SummaryRepoRoot.PathOf("RESULTS.md");
        switch (kind)
        {
            case "outside-path":
                reportPath = outsideReport;
                break;
            case "link-to-outside":
                File.Delete(reportPath);
                File.CreateSymbolicLink(reportPath, outsideReport);
                break;
            case "linked-directory":
                Directory.CreateSymbolicLink(SummaryRepoRoot.PathOf("reports"), outside.Root);
                reportPath = SummaryRepoRoot.PathOf("reports/RESULTS.md");
                break;
            case "fifo":
                File.Delete(reportPath);
                SpecialFileSupport.CreateFifo(reportPath);
                break;
            case "oversized":
                using (var handle = File.OpenHandle(reportPath, FileMode.Truncate, FileAccess.Write))
                {
                    RandomAccess.SetLength(handle, HarnessOptions.EvidenceByteLimit + 1L);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown kind of refused report");
        }

        var mode = new SummaryMode(
            new HarnessOptions { RepoRoot = SummaryRepoRoot.Root, ReportPath = reportPath }, SummaryConsole, descriptor: null);

        var exitCode = await Task.Run(() => mode.RunAsync(ct), ct).WaitAsync(WaitLimit, ct);

        Assert.Equal(1, exitCode);
        Assert.Equal(0, SummaryConsoleBytes.Length);
    }

    // The runtime opens every descriptor of its own with close-on-exec, as it does the descriptor that takes
    // the number of a standard output closed at start. Only a pipe end made inheritable for a child process
    // is open without close-on-exec, as every descriptor inherited across exec is.
    [Theory]
    [InlineData(HandleInheritability.None, false)]
    [InlineData(HandleInheritability.Inheritable, true)]
    public void RuntimePipe_IsOpenWithoutCloseOnExec_OnlyAtAnInheritableClientEnd(
        HandleInheritability inheritability,
        bool clientEndWithoutCloseOnExec)
    {
        SkipUnlessFdInfo();
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out, inheritability);

        // Once exposed, the client end is not closed with the server stream.
        using var clientEnd = pipe.ClientSafePipeHandle;

        Assert.False(SummaryMode.IsOpenWithoutCloseOnExec(DescriptorOf(pipe.SafePipeHandle)));
        Assert.Equal(clientEndWithoutCloseOnExec, SummaryMode.IsOpenWithoutCloseOnExec(DescriptorOf(clientEnd)));
    }

    // No descriptor can have this number, because the kernel caps descriptor numbers far below it, so it reads
    // as not open, as descriptor 1 does when standard output was closed at start and the number is still free.
    [Fact]
    public void DescriptorThatIsNotOpen_IsNotOpenWithoutCloseOnExec()
    {
        SkipUnlessFdInfo();

        Assert.False(SummaryMode.IsOpenWithoutCloseOnExec(int.MaxValue));
    }

    // The runtime opened this file with close-on-exec, but a file opened by path can also keep O_CLOEXEC among
    // its open flags, as the terminal of `docker run -t` does, so the flags line cannot tell the two apart.
    [Fact]
    public void FileOpenedByPath_WithCloseOnExec_CannotTell()
    {
        SkipUnlessFdInfo();
        using var file = File.OpenHandle(SummaryRepoRoot.PathOf("descriptor.txt"), FileMode.CreateNew, FileAccess.Write);

        Assert.Null(SummaryMode.IsOpenWithoutCloseOnExec(DescriptorOf(file)));
    }

    // The links /proc/self/fd shows for each kind of file; only the first four kinds get open flags without
    // O_CLOEXEC from the kernel.
    [Theory]
    [InlineData("pipe:[640008469]", true)]
    [InlineData("socket:[4026]", true)]
    [InlineData("anon_inode:[eventpoll]", true)]
    [InlineData("/memfd:doublemapper (deleted)", true)]
    [InlineData("/dev/pts/0", false)]
    [InlineData("/dev/null", false)]
    [InlineData("/var/tmp/out.txt", false)]
    [InlineData("/var/tmp/fifo", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OpenFlagsNeverHoldCloseOnExec_OnlyForPipesSocketsAnonymousInodesAndMemfdFiles(
        string? linkTarget,
        bool expected)
    {
        Assert.Equal(expected, SummaryMode.OpenFlagsNeverHoldCloseOnExec(linkTarget));
    }

    // The flags line is octal and O_CLOEXEC is 02000000: a runtime pipe end shows 02000000 or 02000001, and a
    // /dev/null or terminal a shell opened read-write shows 0100002. 32 bits of flags take at most 12 octal
    // digits with the leading 0. Text without a flags line of such digits cannot tell, so the check is skipped.
    [Theory]
    [InlineData("pos:\t0\nflags:\t02000000\nmnt_id:\t15\nino:\t1\n", true)]
    [InlineData("pos:\t0\nflags:\t02000001\nmnt_id:\t15\nino:\t1\n", true)]
    [InlineData("pos:\t0\nflags:\t037777777777\nmnt_id:\t15\n", true)]
    [InlineData("pos:\t0\nflags:\t0100002\nmnt_id:\t26\nino:\t5\n", false)]
    [InlineData("pos:\t0\nflags:\t01\nmnt_id:\t15\nino:\t1\n", false)]
    [InlineData("pos:\t0\nflags:\t02000008\nmnt_id:\t15\n", null)]
    [InlineData("pos:\t0\nflags:\t-2000000\nmnt_id:\t15\n", null)]
    [InlineData("pos:\t0\nflags:\t0377777777777\nmnt_id:\t15\n", null)]
    [InlineData("pos:\t0\nflags:\t\nmnt_id:\t15\n", null)]
    [InlineData("pos:\t0\nmnt_id:\t15\nino:\t1\n", null)]
    [InlineData("", null)]
    public void CarriesCloseOnExec_ReadsTheOctalFlagsLine(string fdInfo, bool? expected)
    {
        Assert.Equal(expected, SummaryMode.CarriesCloseOnExec(fdInfo));
    }

    private static void SkipUnlessFdInfo() =>
        Assert.SkipUnless(
            OperatingSystem.IsLinux() && Directory.Exists("/proc/self/fdinfo"),
            "close-on-exec is read from /proc/self/fdinfo, which only Linux with procfs mounted has");

    private static int DescriptorOf(SafeHandle handle) => (int)handle.DangerousGetHandle();

    // One byte per send, so the socket is full only when it cannot take even one more byte. Returns the number
    // of bytes sent, each of them FillByte.
    private static int FillSendBuffer(Socket socket)
    {
        byte[] oneByte = [(byte)FillByte];
        for (var sent = 0; ; sent++)
        {
            Assert.True(sent < MaxFillBytes, "the socket still accepted data after the fill limit");
            socket.Send(oneByte, 0, 1, SocketFlags.None, out var error);
            if (error == SocketError.WouldBlock)
            {
                return sent;
            }

            Assert.Equal(SocketError.Success, error);
        }
    }

    private static async Task<byte[]> ReadToEndAsync(Socket socket, CancellationToken ct)
    {
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var received = new MemoryStream();
        await stream.CopyToAsync(received, ct);
        return received.ToArray();
    }

    private SummaryMode NewMode(AnonymousPipeServerStream pipe) => NewMode(pipe.SafePipeHandle.DangerousGetHandle());

    private SummaryMode NewMode(nint descriptor) =>
        new(new HarnessOptions { RepoRoot = SummaryRepoRoot.Root }, SummaryConsole, descriptor);
}

// ---- Support types of the facade TRX, JADE-unchanged and ordering-evidence tests ----------------
// Top-level support types rather than test classes: a scripted git runner, a temporary directory
// and the git command line for throwaway repositories. TempDirectory also serves the summary mode
// and replay lifecycle tests. They live in this file because the harness test project keeps its
// fixed set of test source files.

/// <summary>One invocation a <see cref="ScriptedGit"/> received.</summary>
/// <param name="RepoRoot">The repository root the check passed.</param>
/// <param name="Args">The git arguments, copied when the call was made.</param>
/// <param name="Timeout">The limit the check passed.</param>
internal sealed record GitCall(string RepoRoot, IReadOnlyList<string> Args, TimeSpan Timeout)
{
    /// <summary>
    /// The command line and limit, as a failed assertion over a list of calls shows each call. The repository root
    /// is left out: it is a temporary directory that differs in every run, and tests compare it on its own.
    /// </summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"git {string.Join(' ', Args)} (limit {Timeout.TotalSeconds:0.###} s)");
}

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

    // How long a killed git gets to exit.
    private static readonly TimeSpan WindDownLimit = TimeSpan.FromSeconds(5);

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

    /// <summary>
    /// Runs git in <paramref name="workingDirectory"/> and returns its standard output. A git that does not
    /// finish within the limit, or before <paramref name="ct"/> is cancelled, is killed with everything it
    /// spawned and its exit awaited, so none outlives the call.
    /// </summary>
    /// <remarks>
    /// A failure names git's subcommand alone, never its other arguments or its standard error, because those
    /// carry the run's temporary paths and a failure must read the same in every run. git's standard error goes
    /// to the test's output instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">git could not be started, exited non-zero or did not finish within the limit.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
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

        var command = args.Length == 0 ? "git" : "git " + args[0];
        ct.ThrowIfCancellationRequested();
        Process? started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            // Its message names the temporary working directory, so it is not passed on.
            throw new InvalidOperationException($"{command} could not be started");
        }

        using var process = started ?? throw new InvalidOperationException($"{command} could not be started");
        var finished = false;
        try
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Limit);
            string output;
            string error;
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                output = await stdout.WaitAsync(deadline.Token);
                error = await stderr.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException($"{command} did not finish within {Limit.TotalSeconds} s");
            }

            finished = true;
            if (process.ExitCode == 0)
            {
                return output;
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{command} exited {process.ExitCode}: {error.Trim()}");
            throw new InvalidOperationException($"{command} exited {process.ExitCode}");
        }
        finally
        {
            if (!finished)
            {
                Stop(process);
            }
        }
    }

    // Kills git and everything it spawned, then waits a bounded time for its exit, so it is gone before its
    // Process is released. A git that already exited needs neither.
    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // git exited before the kill: nothing is left to stop.
            return;
        }

        process.WaitForExit(WindDownLimit);
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Erewhon.Contracts;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Baseline;
using Erewhon.Harness.Compare;
using Erewhon.Harness.Report;

namespace Erewhon.Harness.Tests;

/// <summary>
/// Byte-exact comparison of raw <c>getPhoto</c> response bodies: no normalisation rule, no
/// canonicalisation and no tolerance applies, so any changed, missing or extra byte is reported. The
/// response-level cases show that <see cref="ResponseComparer"/> sends <c>getPhoto</c>, and only it,
/// to this comparer on the raw bytes, byte-order mark included. It also covers the baseline record
/// and manifest model, which holds each raw body as exact text or a <c>.body.bin</c> file with its
/// hash, and baseline persistence through <see cref="BaselineStore"/>, whose tests alone create a
/// temporary repository root.
/// </summary>
public sealed partial class BinaryComparerTests : IDisposable
{
    private const int JpegLength = 22;

    private const int JfifMarkerOffset = 6;

    // The envelope fixtures are ASCII, so each character is one UTF-8 byte and every offset can be
    // counted on the literals. The declaration is 38 bytes, and the soap:Envelope start tag is 169:
    // 14 for "<soap:Envelope", 55, 54 and 45 for its xmlns:soap, xmlns:xsi and xmlns:xsd attributes,
    // and 1 for ">". soap:Body therefore starts at offset 38 + 169 = 207.
    private const string XmlDeclaration = "<?xml version=\"1.0\" encoding=\"utf-8\"?>";

    private const string EnvelopeStartTag =
        "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\""
        + " xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\""
        + " xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">";

    // The start tag above with its xmlns:xsi and xmlns:xsd attributes swapped: the same 169 bytes,
    // first differing after the common "<soap:Envelope", xmlns:soap attribute and " xmlns:xs", at
    // offset 38 + 14 + 55 + 9 = 116, where "i" (0x69) meets "d" (0x64).
    private const string SwappedAttributesEnvelopeStartTag =
        "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\""
        + " xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\""
        + " xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">";

    // Everything after the start tag in an unindented getPhoto response; the photo is the Jpeg()
    // body in base64.
    private const string CompactPhotoEnvelopeContent =
        "<soap:Body><getPhotoResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\"><getPhotoResult>"
        + "<photo>/9j/4AAQSkZJRgABAgAAZABkAAD/2Q==</photo>"
        + "</getPhotoResult></getPhotoResponse></soap:Body></soap:Envelope>";

    // A SOAP 1.1 getPhoto response written as the stand-in writes it, with no indentation: 407 bytes.
    private const string CompactPhotoEnvelope = XmlDeclaration + EnvelopeStartTag + CompactPhotoEnvelopeContent;

    // The compact envelope with its two namespace attributes in swapped order: 407 bytes.
    private const string SwappedAttributesPhotoEnvelope =
        XmlDeclaration + SwappedAttributesEnvelopeStartTag + CompactPhotoEnvelopeContent;

    // The compact envelope indented by two spaces per level, with whitespace only between elements:
    // 8 line feeds and 2 + 4 + 6 + 8 + 6 + 4 + 2 = 32 spaces make it 447 bytes, and the first line
    // feed (0x0a) stands where the compact body has the "<" (0x3c) of soap:Body, at offset 207.
    private const string IndentedPhotoEnvelope =
        XmlDeclaration
        + EnvelopeStartTag + "\n"
        + "  <soap:Body>\n"
        + "    <getPhotoResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\">\n"
        + "      <getPhotoResult>\n"
        + "        <photo>/9j/4AAQSkZJRgABAgAAZABkAAD/2Q==</photo>\n"
        + "      </getPhotoResult>\n"
        + "    </getPhotoResponse>\n"
        + "  </soap:Body>\n"
        + "</soap:Envelope>";

    // The compact envelope with two ordinary attributes on its photo element, xsi:type="xsd:base64Binary"
    // (28 bytes with its leading space) before xsi:nil="false" (16): 407 + 44 = 451 bytes. The photo
    // element starts after the 11 bytes of "<soap:Body>", the 62 of the getPhotoResponse start tag and
    // the 16 of "<getPhotoResult>", at offset 207 + 89 = 296.
    private const string TypedPhotoEnvelope =
        XmlDeclaration + EnvelopeStartTag
        + "<soap:Body><getPhotoResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\"><getPhotoResult>"
        + "<photo xsi:type=\"xsd:base64Binary\" xsi:nil=\"false\">/9j/4AAQSkZJRgABAgAAZABkAAD/2Q==</photo>"
        + "</getPhotoResult></getPhotoResponse></soap:Body></soap:Envelope>";

    // The typed envelope with the photo's xsi:type and xsi:nil attributes swapped: the same 451 bytes,
    // first differing after the common "<photo xsi:", at offset 296 + 11 = 307, where "t" (0x74) meets
    // "n" (0x6e).
    private const string ReorderedTypedPhotoEnvelope =
        XmlDeclaration + EnvelopeStartTag
        + "<soap:Body><getPhotoResponse xmlns=\"urn:JadeWebServices/WebServiceHTTP/\"><getPhotoResult>"
        + "<photo xsi:nil=\"false\" xsi:type=\"xsd:base64Binary\">/9j/4AAQSkZJRgABAgAAZABkAAD/2Q==</photo>"
        + "</getPhotoResult></getPhotoResponse></soap:Body></soap:Envelope>";

    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    private static readonly OperationCatalogue Catalogue = OperationCatalogue.Default;
    private static readonly OperationDescriptor GetPhoto = Catalogue.Find(Surface.Soap, "getPhoto");
    private static readonly OperationDescriptor GetAgent = Catalogue.Find(Surface.Soap, "getAgent");
    private static readonly string SoapContentType = Catalogue.Addresses.Soap.ContentType;

    // Every SOAP outcome of getPhoto shares one HTTP status; business errors travel in the result.
    private static readonly int SoapStatus = GetPhoto.Statuses.Select(status => status.HttpStatus).Distinct().Single();

    private readonly ResponseComparer _responses = new(Catalogue, new Normaliser(NormalisationRules.FromCatalogue(Catalogue)));

    [Fact]
    public void IdenticalBytes_GiveNoDiscrepancy()
    {
        Assert.Empty(BinaryComparer.Compare(Jpeg(), Jpeg()));
    }

    [Fact]
    public void EmptyBodies_AreEqual()
    {
        Assert.Empty(BinaryComparer.Compare(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(JpegLength - 1)]
    public void OneByteChange_GivesValueDiscrepancyAtOffset(int offset)
    {
        var baseline = Jpeg();
        var observed = Jpeg();
        observed[offset] ^= 0xFF;

        var discrepancy = Assert.Single(BinaryComparer.Compare(baseline, observed));

        Assert.Equal(DiscrepancyKinds.Value, discrepancy.Kind);
        Assert.Equal($"$body[{offset}]", discrepancy.Path);
        Assert.Equal(Convert.ToHexStringLower(baseline, offset, 1), discrepancy.Baseline.Text);
        Assert.Equal(Convert.ToHexStringLower(observed, offset, 1), discrepancy.Observed.Text);
    }

    /// <summary>
    /// Changes to the <see cref="Jpeg"/> body, each a new byte at an offset, with the complete discrepancy list
    /// they give: one value entry per maximal run of changed bytes, holding every byte of the run.
    /// </summary>
    public static TheoryData<int[], byte[], Discrepancy[]> ChangedByteRuns() => new()
    {
        // Changes separated by unchanged bytes are separate runs, each at its own offset.
        { [3, 7], [0xE1, 0x00], [Run(3, "e0", "e1"), Run(7, "46", "00")] },

        // Adjacent changes are one run.
        { [3, 4], [0xE1, 0x01], [Run(3, "e000", "e101")] },

        // One unchanged byte between two changes ends the first run.
        { [3, 5], [0xE1, 0x11], [Run(3, "e0", "e1"), Run(5, "10", "11")] },

        // Runs at both ends of the body.
        { [0, 1, JpegLength - 1], [0x00, 0x00, 0x00], [Run(0, "ffd8", "0000"), Run(JpegLength - 1, "d9", "00")] },
    };

    [Theory]
    [MemberData(nameof(ChangedByteRuns))]
    public void ChangedBytes_AreReportedAsMaximalRuns(int[] offsets, byte[] values, Discrepancy[] expected)
    {
        var observed = Jpeg();
        for (var index = 0; index < offsets.Length; index++)
        {
            observed[offsets[index]] = values[index];
        }

        var discrepancies = BinaryComparer.Compare(Jpeg(), observed);

        Assert.Equal(expected, discrepancies);
        AssertCompleteEvidence(Jpeg(), observed, discrepancies);
    }

    // Each run holds all of its bytes, so two observations that share their first changed byte give
    // different lists; the unchanged last byte ends the run.
    [Theory]
    [InlineData("01020300", "010203")]
    [InlineData("01090800", "010908")]
    public void ChangedRun_HoldsEveryByteOfTheRun(string observedHex, string expectedObservedRun)
    {
        byte[] baseline = [0x00, 0x00, 0x00, 0x00];

        Discrepancy[] expected = [Run(0, "000000", expectedObservedRun)];
        Assert.Equal(expected, BinaryComparer.Compare(baseline, Convert.FromHexString(observedHex)));
    }

    // A later change, or a different extra byte, is a different discrepancy list and therefore a different
    // failure signature, even when the two observations share their first change and their length.
    [Theory]
    [InlineData("000000", "010203", "010908")]
    [InlineData("0000000000", "0100000002", "0100000003")]
    [InlineData("0000000000", "0100000002", "0100000200")]
    [InlineData("0000", "010000", "010001")]
    [InlineData("0000", "0000ff", "0000fe")]
    public void ObservationsSharingTheirFirstChange_GiveDifferentSignatures(string baselineHex, string firstHex, string secondHex)
    {
        var baseline = Convert.FromHexString(baselineHex);
        var first = Convert.FromHexString(firstHex);
        var second = Convert.FromHexString(secondHex);

        var firstDiscrepancies = BinaryComparer.Compare(baseline, first);
        var secondDiscrepancies = BinaryComparer.Compare(baseline, second);

        Assert.NotEqual(firstDiscrepancies, secondDiscrepancies);
        Assert.NotEqual(DiscrepancySignature.Compute(firstDiscrepancies), DiscrepancySignature.Compute(secondDiscrepancies));
        AssertCompleteEvidence(baseline, first, firstDiscrepancies);
        AssertCompleteEvidence(baseline, second, secondDiscrepancies);
    }

    // A body that is a prefix of the other gives the length entry and the baseline's bytes past the
    // observed end as one missing entry at that end.
    [Theory]
    [InlineData(2)]
    [InlineData(JpegLength)]
    public void Truncation_GivesLengthAndMissingTail(int removedBytes)
    {
        var baseline = Jpeg();
        var kept = JpegLength - removedBytes;

        Discrepancy[] expected =
        [
            new(
                "$body",
                DiscrepancyKinds.Length,
                DiscrepancyValue.Literal(JpegLength.ToString(CultureInfo.InvariantCulture)),
                DiscrepancyValue.Literal(kept.ToString(CultureInfo.InvariantCulture))),
            new(
                string.Create(CultureInfo.InvariantCulture, $"$body[{kept}]"),
                DiscrepancyKinds.Missing,
                DiscrepancyValue.Literal(Convert.ToHexStringLower(baseline, kept, removedBytes)),
                DiscrepancyValue.Absent),
        ];
        Assert.Equal(expected, BinaryComparer.Compare(baseline, baseline.AsSpan(0, kept)));
    }

    [Fact]
    public void Extension_GivesLengthAndExtraTail()
    {
        byte[] observed = [.. Jpeg(), 0x00, 0x7F];

        Discrepancy[] expected =
        [
            new("$body", DiscrepancyKinds.Length, DiscrepancyValue.Literal("22"), DiscrepancyValue.Literal("24")),
            new("$body[22]", DiscrepancyKinds.Extra, DiscrepancyValue.Absent, DiscrepancyValue.Literal("007f")),
        ];
        Assert.Equal(expected, BinaryComparer.Compare(Jpeg(), observed));
    }

    [Fact]
    public void LengthAndByteDifference_ReportLengthRunAndTail()
    {
        var observed = Jpeg().AsSpan(0, JpegLength - 1).ToArray();
        observed[2] = 0x00;

        var discrepancies = BinaryComparer.Compare(Jpeg(), observed);

        Discrepancy[] expected =
        [
            new("$body", DiscrepancyKinds.Length, DiscrepancyValue.Literal("22"), DiscrepancyValue.Literal("21")),
            Run(2, "ff", "00"),
            new("$body[21]", DiscrepancyKinds.Missing, DiscrepancyValue.Literal("d9"), DiscrepancyValue.Absent),
        ];
        Assert.Equal(expected, discrepancies);
        AssertCompleteEvidence(Jpeg(), observed, discrepancies);
    }

    [Fact]
    public void SingleByteDifference_IsNotNormalised()
    {
        var observed = Jpeg();
        observed[JfifMarkerOffset] = 0x4B;

        var discrepancy = Assert.Single(BinaryComparer.Compare(Jpeg(), observed));

        Assert.Equal(
            new Discrepancy("$body[6]", DiscrepancyKinds.Value, DiscrepancyValue.Literal("4a"), DiscrepancyValue.Literal("4b")),
            discrepancy);
    }

    // Indentation between elements, namespace-declaration order and ordinary attribute order are what
    // XML canonicalisation ignores, yet they change the bytes, so envelopes differing only in them must
    // still be reported different: from the first byte they change, with every later differing byte too.
    [Theory]
    [InlineData("indentation")]
    [InlineData("namespace-declaration-order")]
    [InlineData("ordinary-attribute-order")]
    public void XmlFormattingDifference_IsNotCanonicalised(string variant)
    {
        var (baselineText, observedText, firstRunOffset, firstBaselineByte, firstObservedByte) = variant switch
        {
            "indentation" => (CompactPhotoEnvelope, IndentedPhotoEnvelope, 207, "3c", "0a"),
            "namespace-declaration-order" => (CompactPhotoEnvelope, SwappedAttributesPhotoEnvelope, 116, "69", "64"),
            "ordinary-attribute-order" => (TypedPhotoEnvelope, ReorderedTypedPhotoEnvelope, 307, "74", "6e"),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown XML formatting variant."),
        };
        var baseline = Encoding.UTF8.GetBytes(baselineText);
        var observed = Encoding.UTF8.GetBytes(observedText);

        var discrepancies = BinaryComparer.Compare(baseline, observed);

        var firstRun = discrepancies.First(discrepancy => discrepancy.Kind == DiscrepancyKinds.Value);
        Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"$body[{firstRunOffset}]"), firstRun.Path);
        Assert.StartsWith(firstBaselineByte, firstRun.Baseline.Text, StringComparison.Ordinal);
        Assert.StartsWith(firstObservedByte, firstRun.Observed.Text, StringComparison.Ordinal);
        AssertCompleteEvidence(baseline, observed, discrepancies);
    }

    // ---- Response level: getPhoto dispatch ---------------------------------------------------------

    // Two layouts that are equal as XML differ as bytes: getPhoto reports the byte difference, while the
    // same two bodies through another SOAP operation are equivalent.
    [Fact]
    public void GetPhoto_LayoutOnlyDifference_IsAByteDifference()
    {
        var compact = NilResultEnvelope(GetPhoto, indent: false);
        var indented = NilResultEnvelope(GetPhoto, indent: true);
        var firstDifference = compact.IndexOf("<soap:Body>", StringComparison.Ordinal);
        Assert.Equal(compact[..firstDifference], indented[..firstDifference]);
        var compactBytes = Encoding.UTF8.GetBytes(compact);
        var indentedBytes = Encoding.UTF8.GetBytes(indented);

        var comparison = _responses.Compare(GetPhoto, SoapSnapshot(compactBytes), SoapSnapshot(indentedBytes));

        AssertResponse(
            comparison,
            [.. BinaryComparer.Compare(compactBytes, indentedBytes).Order(Discrepancy.CanonicalOrder)],
            wellFormed: true,
            statusMatched: true);
        Assert.Contains(
            new Discrepancy("$body", DiscrepancyKinds.Length, CountLiteral(compact.Length), CountLiteral(indented.Length)),
            comparison.Discrepancies);
        var firstRun = Assert.Single(
            comparison.Discrepancies,
            discrepancy => discrepancy.Path == string.Create(CultureInfo.InvariantCulture, $"$body[{firstDifference}]"));
        Assert.StartsWith("3c", firstRun.Baseline.Text, StringComparison.Ordinal);
        Assert.StartsWith("0a", firstRun.Observed.Text, StringComparison.Ordinal);
        AssertCompleteEvidence(compactBytes, indentedBytes, comparison.Discrepancies);
        AssertResponse(_responses.Compare(GetAgent, SoapSnapshot(compact), SoapSnapshot(indented)), [], wellFormed: true, statusMatched: true);
    }

    // A getPhoto body keeps its byte-order mark as ordinary leading bytes; an XML-compared body drops it.
    [Fact]
    public void GetPhoto_ByteOrderMark_IsNotStripped()
    {
        var plain = Encoding.UTF8.GetBytes(NilResultEnvelope(GetPhoto, indent: false));
        byte[] marked = [0xEF, 0xBB, 0xBF, .. plain];

        var comparison = _responses.Compare(GetPhoto, SoapSnapshot(plain), SoapSnapshot(marked));

        AssertResponse(
            comparison,
            [.. BinaryComparer.Compare(plain, marked).Order(Discrepancy.CanonicalOrder)],
            wellFormed: true,
            statusMatched: true);
        Assert.Contains(
            new Discrepancy("$body", DiscrepancyKinds.Length, CountLiteral(plain.Length), CountLiteral(marked.Length)),
            comparison.Discrepancies);
        var firstRun = Assert.Single(comparison.Discrepancies, discrepancy => discrepancy.Path == "$body[0]");
        Assert.StartsWith("3c", firstRun.Baseline.Text, StringComparison.Ordinal);
        Assert.StartsWith("efbbbf", firstRun.Observed.Text, StringComparison.Ordinal);
        AssertCompleteEvidence(plain, marked, comparison.Discrepancies);
        AssertResponse(_responses.Compare(GetAgent, SoapSnapshot(plain), SoapSnapshot(marked)), [], wellFormed: true, statusMatched: true);
    }

    // Replacement decoding would load the corrupt envelope as one in another wrapper namespace. Decoded
    // strictly, a getPhoto body that is not valid UTF-8 is not well formed, and its bytes still compare raw.
    [Fact]
    public void GetPhoto_InvalidUtf8_IsNotWellFormed()
    {
        var text = NilResultEnvelope(GetPhoto, indent: false);
        var baseline = Encoding.UTF8.GetBytes(text);
        var observed = baseline.ToArray();
        var offset = text.IndexOf(Catalogue.Addresses.Soap.TargetNamespace, StringComparison.Ordinal);
        observed[offset] = 0xFF;

        AssertResponse(
            _responses.Compare(GetPhoto, SoapSnapshot(baseline), SoapSnapshot(observed)),
            [
                new("$body", DiscrepancyKinds.Malformed, DiscrepancyValue.Absent, DiscrepancyValue.Absent),
                new(
                    string.Create(CultureInfo.InvariantCulture, $"$body[{offset}]"),
                    DiscrepancyKinds.Value,
                    DiscrepancyValue.Literal(Convert.ToHexStringLower(baseline, offset, 1)),
                    DiscrepancyValue.Literal("ff")),
            ],
            wellFormed: false,
            statusMatched: true);
    }

    /// <summary>A SOAP response with the catalogue status whose body is <paramref name="body"/> in UTF-8, without a byte-order mark.</summary>
    private static ResponseSnapshot SoapSnapshot(string body) => SoapSnapshot(Encoding.UTF8.GetBytes(body));

    /// <summary>A SOAP response with the catalogue status whose body is exactly <paramref name="body"/>.</summary>
    private static ResponseSnapshot SoapSnapshot(byte[] body) => new(SoapStatus, SoapContentType, body);

    private static DiscrepancyValue CountLiteral(int count) =>
        DiscrepancyValue.Literal(count.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// A SOAP 1.1 response envelope of <paramref name="operation"/> whose result is <c>xsi:nil</c>. The indented
    /// layout breaks lines only inside <c>Envelope</c> and <c>Body</c>, whose whitespace-only text every SOAP
    /// comparison ignores, so the two layouts are equal as XML for any SOAP operation.
    /// </summary>
    private static string NilResultEnvelope(OperationDescriptor operation, bool indent)
    {
        var soap = Catalogue.Addresses.Soap;
        var binding = operation.Soap!;
        string Break(int depth) => indent ? "\n" + new string(' ', 2 * depth) : string.Empty;

        return $"<soap:Envelope xmlns:soap=\"{soap.EnvelopeNamespace}\" xmlns:xsi=\"{XsiNamespace}\">"
            + $"{Break(1)}<soap:Body>"
            + $"{Break(2)}<{binding.ResponseElement} xmlns=\"{soap.TargetNamespace}\"><{binding.ResultElement} xsi:nil=\"true\"/></{binding.ResponseElement}>"
            + $"{Break(1)}</soap:Body>{Break(0)}</soap:Envelope>";
    }

    /// <summary>Asserts the complete, ordered discrepancy list and all three flags of a response comparison.</summary>
    private static void AssertResponse(ResponseComparison comparison, Discrepancy[] expected, bool wellFormed, bool statusMatched)
    {
        Assert.Equal(expected, comparison.Discrepancies);
        Assert.Equal(expected.Length == 0, comparison.Equivalent);
        Assert.Equal(wellFormed, comparison.WellFormed);
        Assert.Equal(statusMatched, comparison.StatusMatched);
    }

    /// <summary>The <c>value</c> discrepancy of a run of differing bytes at <paramref name="offset"/>, each side as lowercase hex.</summary>
    private static Discrepancy Run(int offset, string baselineHex, string observedHex) =>
        new(
            string.Create(CultureInfo.InvariantCulture, $"$body[{offset}]"),
            DiscrepancyKinds.Value,
            DiscrepancyValue.Literal(baselineHex),
            DiscrepancyValue.Literal(observedHex));

    /// <summary>
    /// Asserts that <paramref name="discrepancies"/>, in any order, are the complete byte evidence of the two
    /// bodies, which only one list can be: the length entry exactly when the lengths differ; value runs inside
    /// the shorter body in which every byte differs, no two of which touch, so each run is maximal; one
    /// missing or extra entry at the shorter body's end exactly when the lengths differ; and nothing else.
    /// Applied to either body, the runs and the tail rebuild the other.
    /// </summary>
    private static void AssertCompleteEvidence(byte[] baseline, byte[] observed, IReadOnlyList<Discrepancy> discrepancies)
    {
        var common = Math.Min(baseline.Length, observed.Length);
        var rebuiltBaseline = observed.AsSpan(0, common).ToArray();
        var rebuiltObserved = baseline.AsSpan(0, common).ToArray();
        var tails = new List<Discrepancy>();
        var runs = new List<(int Offset, byte[] Baseline, byte[] Observed)>();

        foreach (var discrepancy in discrepancies)
        {
            if (discrepancy.Path == "$body")
            {
                Assert.Equal(
                    new Discrepancy("$body", DiscrepancyKinds.Length, CountLiteral(baseline.Length), CountLiteral(observed.Length)),
                    discrepancy);
                continue;
            }

            Assert.StartsWith("$body[", discrepancy.Path, StringComparison.Ordinal);
            Assert.EndsWith("]", discrepancy.Path, StringComparison.Ordinal);
            var offset = int.Parse(discrepancy.Path.AsSpan(6, discrepancy.Path.Length - 7), NumberStyles.None, CultureInfo.InvariantCulture);
            if (discrepancy.Kind == DiscrepancyKinds.Value)
            {
                runs.Add((offset, Convert.FromHexString(discrepancy.Baseline.Text!), Convert.FromHexString(discrepancy.Observed.Text!)));
                continue;
            }

            Assert.Equal(common, offset);
            tails.Add(discrepancy);
        }

        Assert.Equal(baseline.Length != observed.Length, discrepancies.Any(discrepancy => discrepancy.Path == "$body"));

        var previousEnd = -1;
        foreach (var (offset, baselineRun, observedRun) in runs.OrderBy(run => run.Offset))
        {
            Assert.NotEmpty(baselineRun);
            Assert.Equal(baselineRun.Length, observedRun.Length);
            Assert.True(offset > previousEnd, "Two runs must be separated by at least one equal byte.");
            Assert.True(offset + baselineRun.Length <= common, "A run must lie inside the shorter body.");
            for (var index = 0; index < baselineRun.Length; index++)
            {
                Assert.NotEqual(baselineRun[index], observedRun[index]);
            }

            baselineRun.CopyTo(rebuiltBaseline, offset);
            observedRun.CopyTo(rebuiltObserved, offset);
            previousEnd = offset + baselineRun.Length;
        }

        if (baseline.Length == observed.Length)
        {
            Assert.Empty(tails);
            Assert.Equal(baseline, rebuiltBaseline);
            Assert.Equal(observed, rebuiltObserved);
            return;
        }

        var tail = Assert.Single(tails);
        if (baseline.Length > observed.Length)
        {
            Assert.Equal(DiscrepancyKinds.Missing, tail.Kind);
            Assert.True(tail.Observed.IsAbsent);
            byte[] baselineWithTail = [.. rebuiltBaseline, .. Convert.FromHexString(tail.Baseline.Text!)];
            Assert.Equal(baseline, baselineWithTail);
            Assert.Equal(observed, rebuiltObserved);
        }
        else
        {
            Assert.Equal(DiscrepancyKinds.Extra, tail.Kind);
            Assert.True(tail.Baseline.IsAbsent);
            byte[] observedWithTail = [.. rebuiltObserved, .. Convert.FromHexString(tail.Observed.Text!)];
            Assert.Equal(baseline, rebuiltBaseline);
            Assert.Equal(observed, observedWithTail);
        }
    }

    /// <summary>
    /// A fresh JPEG-like body on every call: the first 20 bytes of the JADE test constant
    /// <c>TestPhoto</c> (SOI, then the APP0 JFIF header) followed by the EOI marker.
    /// </summary>
    private static byte[] Jpeg() =>
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00,
        0x01, 0x02, 0x00, 0x00, 0x64, 0x00, 0x64, 0x00, 0x00, 0xFF, 0xD9,
    ];
}

// ---- The baseline record and manifest model -----------------------------------------------------
// The baseline record and manifest models without a file system: the surface, method and case number
// a record derives from its case id and operation, the response half built from raw body bytes, the
// body hash, the UTF-8 fidelity test that decides whether a body may be stored as text, and the
// manifest's defaults, seed-fact rule, tolerant reading and fixed key order. These members live in
// this class because the harness test project keeps its fixed set of test classes, and a baseline
// record holds the raw body bytes and body hash that the binary comparison works on.
public sealed partial class BinaryComparerTests
{
    // SHA-256 test vectors of FIPS 180-2: the empty message, "abc" and the 448-bit two-block message.
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string TwoBlockMessage = "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq";
    private const string TwoBlockSha256 = "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1";

    // The content types the baseline record fixtures state; the persistence tests below share them.
    private const string JsonContentType = "application/json; charset=utf-8";
    private const string BaselineSoapContentType = "text/xml; charset=utf-8";
    private const string SoapAction = "\"urn:JadeWebServices/WebServiceHTTP/getAgent\"";

    private const string SurfaceMessage = "starts with neither 'REST-' nor 'SOAP-'";
    private const string OperationMessage = "is not of the form '<Rest|Soap>.<jadeMethod>'";
    private const string CaseNumberMessage = "does not end in '-' followed by ASCII digits";

    // ---- Names derived from the case id and operation ---------------------------------------------

    [Theory]
    [InlineData("REST-getAgent-01", "rest")]
    [InlineData("SOAP-getPhoto-02", "soap")]
    public void Surface_FollowsCaseIdPrefix(string caseId, string expected)
    {
        Assert.Equal(expected, Record(caseId, "Rest.getAgent").Surface);
    }

    [Theory]
    [InlineData("rest-getAgent-01")]
    [InlineData("Soap-getPhoto-02")]
    [InlineData("RESTgetAgent-01")]
    [InlineData("getAgent-01")]
    [InlineData("")]
    [InlineData(null)]
    public void Surface_WithoutCaseIdPrefix_IsInvalidData(string? caseId)
    {
        var record = Record(caseId!, "Rest.getAgent");

        var ex = Assert.Throws<InvalidDataException>(() => record.Surface);

        Assert.Contains(SurfaceMessage, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Rest.getAgent", "getAgent")]
    [InlineData("Soap.getPhoto", "getPhoto")]
    [InlineData("Rest.get.Agent", "get.Agent")]
    public void JadeMethod_IsTextAfterFirstDot(string operation, string expected)
    {
        Assert.Equal(expected, Record("REST-getAgent-01", operation).JadeMethod);
    }

    [Theory]
    [InlineData("Rest.")]
    [InlineData("RestgetAgent")]
    [InlineData("")]
    [InlineData(null)]
    public void JadeMethod_WithoutDotOrMethod_IsInvalidData(string? operation)
    {
        var record = Record("REST-getAgent-01", operation!);

        var ex = Assert.Throws<InvalidDataException>(() => record.JadeMethod);

        Assert.Contains(OperationMessage, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("REST-getAgent-01", "01")]
    [InlineData("SOAP-getPhoto-123", "123")]
    [InlineData("REST-get-Agent-07", "07")]
    public void CaseNumber_IsDigitsAfterLastDash(string caseId, string expected)
    {
        Assert.Equal(expected, Record(caseId, "Rest.getAgent").CaseNumber);
    }

    [Theory]
    [InlineData("REST-getAgent-")]
    [InlineData("REST-getAgent-0a")]
    [InlineData("REST-getAgent- 1")]
    [InlineData("REST-getAgent-\u0660\u0661")]
    [InlineData("RESTgetAgent01")]
    [InlineData("")]
    [InlineData(null)]
    public void CaseNumber_NotAsciiDigitsAfterLastDash_IsInvalidData(string? caseId)
    {
        var record = Record(caseId!, "Rest.getAgent");

        var ex = Assert.Throws<InvalidDataException>(() => record.CaseNumber);

        Assert.Contains(CaseNumberMessage, ex.Message, StringComparison.Ordinal);
    }

    // ---- Response half and body hash ---------------------------------------------------------------

    [Theory]
    [InlineData("", "", EmptySha256)]
    [InlineData("616263", "abc", AbcSha256)]
    [InlineData("efbbbf616263", "\uFEFFabc", "1c28dc3f1f804a1ad9c9b4b4cf5e2658d16ad4ed08e3020d04a8d2865018947c")]
    [InlineData("e282ac", "\u20AC", "c4cc90ed3d26f12d4b08a75140970a7904035c31cbb4515a83f19b9003c00d1d")]
    public void FromRawBody_WithoutBodyFile_KeepsExactTextAndHashesRawBytes(
        string rawHex, string expectedText, string expectedSha256)
    {
        var response = BaselineResponse.FromRawBody(200, JsonContentType, Convert.FromHexString(rawHex), bodyFile: null);

        Assert.Equal(new BaselineResponse(200, JsonContentType, expectedText, expectedSha256, null), response);
    }

    [Fact]
    public void FromRawBody_WithBodyFile_HoldsNoTextAndHashesRawBytes()
    {
        var response = BaselineResponse.FromRawBody(
            500, contentType: null, Convert.FromHexString("ffd8ffe0"), "002-getPhoto-02.body.bin");

        Assert.Equal(
            new BaselineResponse(
                500,
                null,
                null,
                "ba4f25bf16ba4be6bc7d3276fafeb67f9eb3c5df042bc3a405e1af15b921eed7",
                "002-getPhoto-02.body.bin"),
            response);
    }

    [Theory]
    [InlineData("", EmptySha256)]
    [InlineData("abc", AbcSha256)]
    [InlineData(TwoBlockMessage, TwoBlockSha256)]
    public void ComputeSha256Hex_MatchesKnownVectorInLowercase(string message, string expected)
    {
        Assert.Equal(expected, BaselineResponse.ComputeSha256Hex(Encoding.ASCII.GetBytes(message)));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("7b7d", true)]
    [InlineData("efbbbf7b7d", true)]
    [InlineData("c3a96e61", true)]
    [InlineData("e282ac", true)]
    [InlineData("f09f9880", true)]
    [InlineData("ff", false)]
    [InlineData("80", false)]
    [InlineData("c3", false)]
    [InlineData("c080", false)]
    [InlineData("eda080", false)]
    [InlineData("edb080", false)]
    [InlineData("eda0bdedb880", false)]
    [InlineData("f4908080", false)]
    public void IsTextRoundTrippable_AcceptsOnlyFaithfulUtf8(string rawHex, bool expected)
    {
        Assert.Equal(expected, BaselineResponse.IsTextRoundTrippable(Convert.FromHexString(rawHex)));
    }

    // ---- Manifest ----------------------------------------------------------------------------------

    [Fact]
    public void Manifest_Defaults_AreLabelledStandInWithNoObservedFacts()
    {
        var manifest = new BaselineManifest();

        Assert.Equal("jade-standin", manifest.Source);
        Assert.Equal("Recorded from the JADE stand-in, not from live JADE", manifest.Label);
        Assert.Null(manifest.StandIn);
        Assert.Null(manifest.SeedSha256);
        Assert.Null(manifest.ReferenceDate);
        Assert.Equal(new ManifestCounts(0, 0, 0, 0), manifest.Counts);
        Assert.Empty(manifest.NotCaptured);
        Assert.False(manifest.WsdlCaptured);
        Assert.Equal(HarnessOptions.HarnessVersion, manifest.HarnessVersion);
        Assert.Null(manifest.StartingCommit);
        Assert.Null(manifest.CaptureCommit);
        Assert.Null(manifest.CaptureExecution);
        Assert.Null(manifest.BlockingError);
        Assert.False(manifest.HasSeedFacts);
        Assert.False(manifest.IsNotRun);
    }

    [Theory]
    [InlineData("ab12", "2026-01-01", CaptureExecutionValues.LocalProcesses, true, false)]
    [InlineData("ab12", "2026-01-01", CaptureExecutionValues.Docker, true, false)]
    [InlineData("ab12", "2026-01-01", null, true, false)]
    [InlineData("ab12", "2026-01-01", "Not-Run", true, false)]
    [InlineData(null, "2026-01-01", CaptureExecutionValues.LocalProcesses, false, false)]
    [InlineData("ab12", null, CaptureExecutionValues.LocalProcesses, false, false)]
    [InlineData("ab12", "2026-01-01", CaptureExecutionValues.NotRun, false, true)]
    [InlineData(null, null, CaptureExecutionValues.NotRun, false, true)]
    public void Manifest_SeedFacts_NeedHashAndDateOutsideNotRun(
        string? seedSha256, string? referenceDate, string? captureExecution, bool hasSeedFacts, bool isNotRun)
    {
        var manifest = new BaselineManifest
        {
            SeedSha256 = seedSha256,
            ReferenceDate = referenceDate,
            CaptureExecution = captureExecution,
        };

        Assert.Equal(hasSeedFacts, manifest.HasSeedFacts);
        Assert.Equal(isNotRun, manifest.IsNotRun);
    }

    [Fact]
    public void Manifest_JsonNulls_ReadAsEmptyValues()
    {
        const string json = """
            {"source":null,"label":null,"standIn":null,"counts":null,"notCaptured":null,"harnessVersion":null}
            """;

        var manifest = JsonSerializer.Deserialize<BaselineManifest>(json, BaselineStore.SerializerOptions);

        Assert.NotNull(manifest);
        Assert.Equal("", manifest.Source);
        Assert.Equal("", manifest.Label);
        Assert.Null(manifest.StandIn);
        Assert.Equal(new ManifestCounts(0, 0, 0, 0), manifest.Counts);
        Assert.Empty(manifest.NotCaptured);
        Assert.Null(manifest.HarnessVersion);
    }

    [Fact]
    public void Manifest_HandWrittenNotRun_ParsesWithUnknownMemberCommentAndAnyCase()
    {
        const string json = """
            {
              // Written by the execution agent because capture could not start.
              "Source": "jade-standin",
              "CAPTUREEXECUTION": "not-run",
              "counts": { "plannedOperations": 22, "plannedCases": 56, "capturedOperations": 0, "capturedCases": 0 },
              "notCaptured": ["REST-getAgent-01", "SOAP-getPhoto-02"],
              "wsdlCaptured": false,
              "startingCommit": "19a5189e9075349aca54de1fb5fdf2d8727403e7",
              "blockingError": "error NU1101: Unable to find package xunit.v3",
              "writtenBy": "execution agent"
            }
            """;

        var manifest = JsonSerializer.Deserialize<BaselineManifest>(json, BaselineStore.SerializerOptions);

        Assert.NotNull(manifest);
        Assert.True(manifest.IsNotRun);
        Assert.False(manifest.HasSeedFacts);
        Assert.Equal("jade-standin", manifest.Source);
        Assert.Equal(SourceLabels.BaselineLabel, manifest.Label);
        Assert.Null(manifest.StandIn);
        Assert.Null(manifest.SeedSha256);
        Assert.Null(manifest.ReferenceDate);
        Assert.Equal(new ManifestCounts(22, 56, 0, 0), manifest.Counts);
        Assert.Equal(["REST-getAgent-01", "SOAP-getPhoto-02"], manifest.NotCaptured);
        Assert.False(manifest.WsdlCaptured);
        Assert.Equal(HarnessOptions.HarnessVersion, manifest.HarnessVersion);
        Assert.Equal("19a5189e9075349aca54de1fb5fdf2d8727403e7", manifest.StartingCommit);
        Assert.Null(manifest.CaptureCommit);
        Assert.Equal("error NU1101: Unable to find package xunit.v3", manifest.BlockingError);
    }

    [Fact]
    public void Manifest_Serialises_InDeclarationOrder_WithNullsWritten()
    {
        var manifest = new BaselineManifest
        {
            StandIn = new StandInInfo(null, "2026-01-01", 3),
            ReferenceDate = "2026-01-01",
            Counts = new ManifestCounts(22, 56, 21, 55),
            NotCaptured = ["SOAP-getPhoto-02"],
            CaptureExecution = CaptureExecutionValues.LocalProcesses,
        };

        using var document = JsonDocument.Parse(BaselineStore.SerializeDocument(manifest));
        var root = document.RootElement;

        Assert.Equal(
            [
                "source", "label", "standIn", "seedSha256", "referenceDate", "counts", "notCaptured",
                "wsdlCaptured", "harnessVersion", "startingCommit", "captureCommit", "captureExecution",
                "blockingError",
            ],
            PropertyNames(root));
        Assert.Equal(["seedSha256", "referenceDate", "generation"], PropertyNames(root.GetProperty("standIn")));
        Assert.Equal(
            ["plannedOperations", "plannedCases", "capturedOperations", "capturedCases"],
            PropertyNames(root.GetProperty("counts")));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("standIn").GetProperty("seedSha256").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("seedSha256").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("startingCommit").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("blockingError").ValueKind);
    }

    // A source or label is recorded as stated only when it is set: by a member of the file the store reads,
    // whatever its value (an empty string or a JSON null included), or by an object initializer. An omitted
    // member keeps its default but is recorded as not stated. Neither fact is written, so stating a member
    // changes no byte of the serialised manifest and adds no key.
    [Theory]
    [InlineData("""{"source":"jade-standin","label":"Recorded from the JADE stand-in, not from live JADE"}""", true, SourceLabels.StandIn, true, SourceLabels.BaselineLabel)]
    [InlineData("""{"source":"","label":null}""", true, "", true, "")]
    [InlineData("""{"source":null,"label":""}""", true, "", true, "")]
    [InlineData("""{"label":"Recorded from live JADE"}""", false, SourceLabels.StandIn, true, "Recorded from live JADE")]
    [InlineData("""{"source":"live-jade"}""", true, "live-jade", false, SourceLabels.BaselineLabel)]
    [InlineData("""{"captureExecution":"not-run","notCaptured":["REST-getAgent-01"]}""", false, SourceLabels.StandIn, false, SourceLabels.BaselineLabel)]
    public async Task Manifest_SourceAndLabelStated_RecordWhetherEachWasSet(
        string json, bool sourceStated, string source, bool labelStated, string label)
    {
        await WriteRaw(ManifestFile, Encoding.UTF8.GetBytes(json));

        Assert.True(Store.TryReadManifest(out var syncManifest));
        var asyncManifest = await Store.TryReadManifestAsync(TestCancellation);
        foreach (var manifest in new[] { syncManifest, asyncManifest })
        {
            Assert.NotNull(manifest);
            Assert.Equal(sourceStated, manifest.SourceStated);
            Assert.Equal(source, manifest.Source);
            Assert.Equal(labelStated, manifest.LabelStated);
            Assert.Equal(label, manifest.Label);

            var restated = manifest with { Source = manifest.Source, Label = manifest.Label };
            Assert.True(restated.SourceStated, "setting the source states it");
            Assert.True(restated.LabelStated, "setting the label states it");
            var written = BaselineStore.SerializeDocument(manifest);
            Assert.Equal(BaselineStore.SerializeDocument(restated), written);
            using var document = JsonDocument.Parse(written);
            Assert.DoesNotContain(
                PropertyNames(document.RootElement),
                name => name.EndsWith("Stated", StringComparison.OrdinalIgnoreCase));
        }

        var initialised = new BaselineManifest { Source = source, Label = label };
        var defaults = new BaselineManifest();
        Assert.True(initialised.SourceStated, "an object initializer states the source");
        Assert.True(initialised.LabelStated, "an object initializer states the label");
        Assert.False(defaults.SourceStated, "the default source is not stated");
        Assert.False(defaults.LabelStated, "the default label is not stated");
        Assert.Equal(
            BaselineStore.SerializeDocument(new BaselineManifest { Source = SourceLabels.StandIn, Label = SourceLabels.BaselineLabel }),
            BaselineStore.SerializeDocument(defaults));
    }

    // ---- Record JSON shape -------------------------------------------------------------------------

    [Fact]
    public void Record_Serialises_InDeclarationOrder_WithHeaderNamesAsSentAndNoComputedMembers()
    {
        var headers = new Dictionary<string, string>
        {
            ["SOAPAction"] = SoapAction,
            ["Content-Type"] = BaselineSoapContentType,
        };
        var record = new BaselineRecord(
            4,
            "SOAP-getAgent-01",
            "Soap.getAgent",
            "success",
            false,
            "seed",
            SourceLabels.StandIn,
            new BaselineRequest("POST", "/jade/jadehttp.dll?WebServiceHTTP", headers, null),
            BaselineResponse.FromRawBody(200, BaselineSoapContentType, "<a/>"u8, bodyFile: null));

        using var document = JsonDocument.Parse(BaselineStore.SerializeDocument(record));
        var root = document.RootElement;
        var request = root.GetProperty("request");
        var response = root.GetProperty("response");

        Assert.Equal(
            ["order", "caseId", "operation", "kind", "stateChanging", "preState", "source", "request", "response"],
            PropertyNames(root));
        Assert.Equal(["method", "pathAndQuery", "headers", "bodyText"], PropertyNames(request));
        Assert.Equal(["SOAPAction", "Content-Type"], PropertyNames(request.GetProperty("headers")));
        Assert.Equal(SoapAction, request.GetProperty("headers").GetProperty("SOAPAction").GetString());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("bodyText").ValueKind);
        Assert.Equal(["status", "contentType", "bodyText", "bodySha256", "bodyFile"], PropertyNames(response));
        Assert.Equal("<a/>", response.GetProperty("bodyText").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("bodyFile").ValueKind);
    }

    private static BaselineRecord Record(string caseId, string operation) => new(
        1,
        caseId,
        operation,
        "success",
        false,
        "seed",
        SourceLabels.StandIn,
        new BaselineRequest("GET", "/agent/Hank%20Williams", new Dictionary<string, string>(), null),
        BaselineResponse.FromRawBody(200, JsonContentType, "{}"u8, bodyFile: null));

    private static List<string> PropertyNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(static property => property.Name)];
}

// ---- Baseline persistence -----------------------------------------------------------------------
// Baseline persistence against real files: every file-system test owns a fresh temporary repository
// root, writes and reads it only through BaselineStore, and checks file names, byte-exact and
// deterministic output, verified reads, the refusal of inconsistent, malformed and damaged records,
// the manifest's withdrawal and its atomic publication through one fixed temporary file, and
// physical containment: no read, write or delete reaches outside baseline/ through a symbolic link,
// whether the link sits on baseline/ itself, on a directory below it or at the target, dangles, or is
// swapped in for a directory while the store works; and that a FIFO or a file over the evidence limit in
// place of any baseline file refuses its read at once. Write and delete links point into a simulated
// Erewhon/DataFiles inside the temporary root, and read links into a directory of their own beside it;
// the repository's own tree is never touched. These members live in this
// class because the harness test project keeps its fixed set of test classes, and the store writes
// and reads the byte-exact .body.bin files and body hashes that the binary comparison works on.
public sealed partial class BinaryComparerTests
{
    private const string RestRecordPath = "baseline/records/rest/001-getAgent-01.json";
    private const string PhotoRecordPath = "baseline/records/soap/002-getPhoto-02.json";
    private const string PhotoBodyPath = "baseline/records/soap/002-getPhoto-02.body.bin";
    private const string ManifestFile = "baseline/manifest.json";
    private const string ManifestTemporaryFile = "baseline/.manifest.json.tmp";
    private const string RulesFile = "baseline/normalisation-rules.json";
    private const string ConformanceFile = "baseline/conformance.json";
    private const string WsdlFile = "baseline/wsdl/standin-ErewhonWebService.wsdl";

    private const string SoapPath = "/jade/jadehttp.dll?WebServiceHTTP&serviceName=ErewhonWebService&listName=WebServiceHTTP";
    private const string SeedSha256 = "5d41402abc4b2a76b9719d911017c592ae0e8f6b1a6cd0b1e6a0e7c5f8b1c9d2";
    private const string StartingCommit = "19a5189e9075349aca54de1fb5fdf2d8727403e7";

    private const string LinkRefusal = "BaselineStore never writes or deletes through a link";
    private const string ReadLinkRefusal = "BaselineStore never reads through a link";
    private const string SentinelName = "Clients.txt";
    private const string ProtectedPath = "Erewhon/DataFiles";
    private const string SentinelPath = ProtectedPath + "/" + SentinelName;

    // Rounds of every write and delete the parent-swap test runs while its directories are swapped.
    private const int SwapIterations = 300;

    // Manifest publications the cancellation-race test cancels while they run.
    private const int CancellationIterations = 200;

    // The reader that applies the record checks alone, keeps only each record's key and leaves every record and
    // its body to ReadEntryAsync.
    private const string RecordIndexReader = "ReadRecordIndexAsync";

    // The size of each large inline body the index regression writes.
    private const int LargeBodySize = 3 * 1024 * 1024;

    // One byte over the evidence limit, the length of every oversized file the refusal tests plant.
    private const long OversizedLength = HarnessOptions.EvidenceByteLimit + 1L;

    // A refused read allocates its exception and message, never a buffer the length of the file.
    private const long RefusalAllocationBound = 1024 * 1024;

    // A refused read ends well within this; one that blocked on a FIFO fails the test instead of hanging.
    private static readonly TimeSpan PromptLimit = TimeSpan.FromSeconds(10);

    private static readonly string[] DocumentNames =
        ["record", "body-file", "manifest", "normalisation-rules", "conformance", "wsdl"];

    private static readonly string[] ReaderNames = ["ReadRecords", "ReadRecordsAsync", "ReadEntries", "ReadEntriesAsync"];

    // The damages that only a body read can detect, so the record index lists their records.
    private static readonly string[] BodyDamages = ["tampered-body-file", "tampered-body-text", "missing-body-file"];

    // The directories the parent-swap test replaces with links, and the protected files an escape through
    // them would replace or delete.
    private static readonly string[] SwappedDirectories = ["baseline/records/rest", "baseline/wsdl"];

    private static readonly string[] SwapSentinelNames = ["001-getAgent-01.json", "standin-ErewhonWebService.wsdl"];

    // Created on first use, so only the persistence tests touch the file system; removed in Dispose.
    private string? _storeRoot;
    private HarnessOptions? _storeOptions;
    private BaselineStore? _store;

    /// <summary>
    /// The four record readers, which share every check, and the record index, which shares every record check.
    /// </summary>
    public static TheoryData<string> Readers { get; } = [.. ReaderNames, RecordIndexReader];

    /// <summary>
    /// Each damage to a written baseline, the file the error must name and the reason it must give, for every
    /// record reader, and for the record index when the damage is to the record rather than its body.
    /// </summary>
    public static TheoryData<string, string, string, string> DamagedRecords()
    {
        (string Damage, string File, string Reason)[] damages =
        [
            ("tampered-body-file", PhotoRecordPath, "is invalid: its body hashes to"),
            ("tampered-body-text", RestRecordPath, "is invalid: its body hashes to"),
            ("missing-body-file", PhotoRecordPath, $"is invalid: its body file {PhotoBodyPath} is missing."),
            ("malformed-json", RestRecordPath, "is not a valid BaselineRecord document"),
            ("invalid-utf8-body-text", RestRecordPath,
                "is not a valid BaselineRecord document: The JSON value could not be converted to "
                + "Erewhon.Harness.Baseline.BaselineResponse. Path: $.response.bodyText"),
            ("lone-surrogate-body-text", RestRecordPath,
                "is not a valid BaselineRecord document: The JSON value could not be converted to "
                + "Erewhon.Harness.Baseline.BaselineResponse. Path: $.response.bodyText"),
            ("json-null", RestRecordPath, "holds a JSON null instead of a BaselineRecord"),
            ("missing-case-id", RestRecordPath, "is invalid: member caseId is missing."),
            ("missing-request-headers", RestRecordPath, "is invalid: member request.headers is missing."),
            ("missing-body-sha", PhotoRecordPath, "is invalid: member response.bodySha256 is missing."),
            ("wrong-surface-directory", "baseline/records/soap/001-getAgent-01.json",
                "belongs in records/rest, not records/soap"),
            ("wrong-file-name", "baseline/records/rest/007-getAgent-01.json",
                "must be stored as 001-getAgent-01.json"),
            ("duplicate-order", "baseline/records/rest/001-getClient-01.json",
                $"repeats replay position 1 of {RestRecordPath}"),
            ("duplicate-case-id", "baseline/records/rest/003-getAgent-01.json",
                $"repeats case REST-getAgent-01 of {RestRecordPath}"),
            ("text-and-file", PhotoRecordPath, "is invalid: it holds both bodyText and bodyFile."),
            ("foreign-body-file", PhotoRecordPath, "names body file '002-getPhoto-03.body.bin'"),
        ];

        var data = new TheoryData<string, string, string, string>();
        foreach (var (damage, file, reason) in damages)
        {
            foreach (var reader in ReaderNames)
            {
                data.Add(damage, reader, file, reason);
            }

            if (!BodyDamages.Contains(damage))
            {
                data.Add(damage, RecordIndexReader, file, reason);
            }
        }

        return data;
    }

    /// <summary>Each damage <see cref="BaselineStore.ReadBodyBytes"/> must detect, in both forms.</summary>
    public static TheoryData<string, bool, string> DamagedBodies()
    {
        (string Damage, string Message)[] damages =
        [
            ("tampered-body-file", "Baseline case SOAP-getPhoto-02 is invalid: its body hashes to"),
            ("missing-body-file", $"Baseline case SOAP-getPhoto-02 is invalid: its body file {PhotoBodyPath} is missing."),
            ("tampered-body-text", "Baseline case REST-getAgent-01 is invalid: its body hashes to"),
            ("text-and-file", "Baseline case SOAP-getPhoto-02 holds both bodyText and bodyFile"),
            ("foreign-body-file", "Baseline case SOAP-getPhoto-02 names body file '002-getPhoto-03.body.bin'"),
        ];

        var data = new TheoryData<string, bool, string>();
        foreach (var (damage, message) in damages)
        {
            data.Add(damage, false, message);
            data.Add(damage, true, message);
        }

        return data;
    }

    /// <summary>
    /// Each source a record may not carry (null: the member is removed), the reason the refusal must give, for
    /// the per-case reader and every record reader.
    /// </summary>
    public static TheoryData<string?, string, string> UnlabelledRecordReads()
    {
        (string? Source, string Reason)[] labels =
        [
            ("live-jade", "it has source 'live-jade'"),
            ("", "it has source ''"),
            (null, "it has no source"),
        ];

        var data = new TheoryData<string?, string, string>();
        foreach (var (source, reason) in labels)
        {
            foreach (var reader in ReaderNames.Prepend("ReadEntryAsync"))
            {
                data.Add(source, reader, reason);
            }
        }

        return data;
    }

    /// <summary>
    /// Each reader, in its synchronous and asynchronous forms, with the baseline file it reads and each place a
    /// link can stand on that file's way: at the file itself, to a real file outside the temporary root or
    /// dangling, and at each directory from <c>baseline/</c> down to it. The record index reads no body, so a
    /// linked body file is left to the case read.
    /// </summary>
    public static TheoryData<string, string, string> LinkedReads()
    {
        string[] directories = ["baseline/records/soap", "baseline/records", "baseline"];
        (string Document, string[] Readers, string[] Placements)[] reads =
        [
            ("manifest", ["TryReadManifest", "TryReadManifestAsync"], ["file", "dangling", "baseline"]),
            ("normalisation-rules", ["ReadNormalisationRules", "ReadNormalisationRulesAsync"],
                ["file", "dangling", "baseline"]),
            ("conformance", ["TryReadConformance", "TryReadConformanceAsync"], ["file", "dangling", "baseline"]),
            ("wsdl", ["ReadWsdlBytes", "ReadWsdlBytesAsync", "WsdlExists"],
                ["file", "dangling", "baseline/wsdl", "baseline"]),
            ("record", [.. ReaderNames, RecordIndexReader, "ReadEntryAsync"], ["file", "dangling", .. directories]),
            ("body-file", [.. ReaderNames, "ReadEntryAsync"], ["file", "dangling"]),
            ("body-file", ["ReadBodyBytes", "ReadBodyBytesAsync"], ["file", "dangling", .. directories]),
        ];

        var data = new TheoryData<string, string, string>();
        foreach (var (document, readers, placements) in reads)
        {
            foreach (var placement in placements)
            {
                foreach (var reader in readers)
                {
                    data.Add(document, placement, reader);
                }
            }
        }

        return data;
    }

    /// <summary>
    /// Each reader, in its synchronous and asynchronous forms, with the baseline file it reads replaced by a
    /// FIFO nobody writes to or by a sparse file one byte over the evidence limit. <c>WsdlExists</c> opens the
    /// WSDL without reading it, so only a FIFO refuses it; the record index reads no body, so a body file is left
    /// to the case read.
    /// </summary>
    public static TheoryData<string, string, string> RefusedFileReads()
    {
        (string Document, string[] Readers)[] reads =
        [
            ("manifest", ["TryReadManifest", "TryReadManifestAsync"]),
            ("normalisation-rules", ["ReadNormalisationRules", "ReadNormalisationRulesAsync"]),
            ("conformance", ["TryReadConformance", "TryReadConformanceAsync"]),
            ("wsdl", ["ReadWsdlBytes", "ReadWsdlBytesAsync", "WsdlExists"]),
            ("record", [.. ReaderNames, RecordIndexReader, "ReadEntryAsync"]),
            ("body-file", [.. ReaderNames, "ReadEntryAsync", "ReadBodyBytes", "ReadBodyBytesAsync"]),
        ];

        var data = new TheoryData<string, string, string>();
        foreach (var (document, readers) in reads)
        {
            foreach (var kind in (string[])["fifo", "oversized"])
            {
                foreach (var reader in readers.Where(reader => kind == "fifo" || reader != "WsdlExists"))
                {
                    data.Add(document, kind, reader);
                }
            }
        }

        return data;
    }

    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    // A BOM, non-ASCII text, markup characters and a trailing blank: every byte must survive as bodyText.
    private static byte[] AgentBody =>
        [.. Encoding.UTF8.Preamble, .. """{"name":"Hank Williams","city":"Tāmaki <Makaurau>","fax":"64 3 923 1010 "}"""u8];

    // Not valid UTF-8, with a CR LF pair, so only a body file can hold it.
    private static byte[] PhotoBody =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x0D, 0x0A, 0xFF, 0xD9];

    private static byte[] SoapBody => Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body/></soap:Envelope>");

    // A BOM, CR LF and no final newline: the WSDL is stored exactly as served.
    private static byte[] WsdlBytes =>
    [
        .. Encoding.UTF8.Preamble,
        .. "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"u8,
        .. "<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\"/>"u8,
    ];

    private static byte[] SentinelBytes => "Hank Williams\t1 Main St\tWellington\n"u8.ToArray();

    private static BaselineRecord AgentRecord => NewRecord(1, "REST-getAgent-01", "Rest.getAgent", AgentBody);

    private static BaselineRecord PhotoRecord =>
        NewRecord(2, "SOAP-getPhoto-02", "Soap.getPhoto", PhotoBody, "002-getPhoto-02.body.bin");

    private static BaselineManifest Manifest => new()
    {
        StandIn = new StandInInfo(SeedSha256, "2026-01-01", 2),
        SeedSha256 = SeedSha256,
        ReferenceDate = "2026-01-01",
        Counts = new ManifestCounts(22, 56, 22, 55),
        NotCaptured = ["SOAP-getPhoto-03"],
        WsdlCaptured = true,
        StartingCommit = StartingCommit,
        CaptureCommit = "0123456789abcdef0123456789abcdef01234567",
        CaptureExecution = CaptureExecutionValues.LocalProcesses,
    };

    private string ProtectedDirectory => Full(ProtectedPath);

    // This test's own temporary repository root, made by the first member that needs it.
    private string StoreRoot => _storeRoot ??= CreateStoreRoot();

    private HarnessOptions StoreOptions => _storeOptions ??= new HarnessOptions { RepoRoot = StoreRoot };

    private BaselineStore Store => _store ??= new BaselineStore(StoreOptions);

    public void Dispose()
    {
        if (_storeRoot is not null)
        {
            Directory.Delete(_storeRoot, recursive: true);
        }
    }

    private static string CreateStoreRoot()
    {
        // The run sets TMPDIR to a per-clone directory, so parallel checkouts never share a root.
        var root = Path.Combine(Path.GetTempPath(), "erewhon-baseline-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    // ---- File names ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, "getAgent", "01", "001-getAgent-01")]
    [InlineData(42, "postCreateTender", "3", "042-postCreateTender-3")]
    [InlineData(999, "getPhoto", "12", "999-getPhoto-12")]
    public void FileNames_AreZeroPaddedPositionMethodAndCaseNumber(
        int position, string jadeMethod, string caseNumber, string stem)
    {
        Assert.Equal(stem + ".json", BaselineStore.RecordFileName(position, jadeMethod, caseNumber));
        Assert.Equal(stem + ".body.bin", BaselineStore.BodyFileName(position, jadeMethod, caseNumber));
    }

    [Theory]
    [InlineData(0, "getAgent", "01", "position")]
    [InlineData(-1, "getAgent", "01", "position")]
    [InlineData(1000, "getAgent", "01", "position")]
    [InlineData(1, "../x", "01", "jadeMethod")]
    [InlineData(1, "get Agent", "01", "jadeMethod")]
    [InlineData(1, "getAgént", "01", "jadeMethod")]
    [InlineData(1, "", "01", "jadeMethod")]
    [InlineData(1, "getAgent", "0a", "caseNumber")]
    [InlineData(1, "getAgent", "01/..", "caseNumber")]
    [InlineData(1, "getAgent", "\u0661", "caseNumber")]
    [InlineData(1, "getAgent", "", "caseNumber")]
    public void FileNames_RejectOutOfRangeOrUnsafeParts(int position, string jadeMethod, string caseNumber, string parameter)
    {
        var recordError = Assert.ThrowsAny<ArgumentException>(
            () => BaselineStore.RecordFileName(position, jadeMethod, caseNumber));
        var bodyError = Assert.ThrowsAny<ArgumentException>(
            () => BaselineStore.BodyFileName(position, jadeMethod, caseNumber));

        Assert.Equal(parameter, recordError.ParamName);
        Assert.Equal(parameter, bodyError.ParamName);
    }

    [Theory]
    [InlineData("REST-getAgent-01", "rest")]
    [InlineData("SOAP-getPhoto-02", "soap")]
    public void SurfaceDirectoryName_FollowsCaseIdPrefix(string caseId, string expected)
    {
        Assert.Equal(expected, BaselineStore.SurfaceDirectoryName(caseId));
    }

    [Theory]
    [InlineData("Rest-getAgent-01")]
    [InlineData("soap-getPhoto-02")]
    [InlineData("")]
    [InlineData(null)]
    public void SurfaceDirectoryName_RejectsAnyOtherPrefix(string? caseId)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => BaselineStore.SurfaceDirectoryName(caseId!));

        Assert.Equal("caseId", ex.ParamName);
    }

    [Fact]
    public void RecordPaths_SitInTheSurfaceDirectory()
    {
        Assert.Equal(PhotoRecordPath, Relative(Store.GetRecordPath(PhotoRecord)));
        Assert.Equal(PhotoBodyPath, Relative(Store.GetBodyFilePath(PhotoRecord)));
        Assert.Equal(RestRecordPath, Relative(Store.GetRecordPath(AgentRecord)));
        Assert.Equal("record", Assert.Throws<ArgumentException>(() => Store.GetBodyFilePath(AgentRecord)).ParamName);
    }

    // ---- Round trip and file format -----------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrittenDocuments_ReadBackUnchanged(bool useAsync)
    {
        await WriteFixture(Store, useAsync);

        var records = useAsync ? await Store.ReadRecordsAsync(TestCancellation) : Store.ReadRecords();
        var manifest = useAsync ? await Store.TryReadManifestAsync(TestCancellation) : ReadManifest();
        var rulesText = useAsync
            ? await Store.ReadNormalisationRulesAsync(TestCancellation)
            : Store.ReadNormalisationRules();
        var rules = useAsync
            ? await NormalisationRules.LoadAsync(StoreOptions.NormalisationRulesPath, TestCancellation)
            : NormalisationRules.Load(StoreOptions.NormalisationRulesPath);
        var conformance = useAsync
            ? await Store.TryReadConformanceAsync(TestCancellation)
            : Store.TryReadConformance();
        var wsdl = useAsync ? await Store.ReadWsdlBytesAsync(TestCancellation) : Store.ReadWsdlBytes();

        Assert.Equal([Text(AgentRecord), Text(PhotoRecord)], records.Select(static record => Text(record)));
        Assert.NotNull(manifest);
        Assert.Equal(Text(Manifest), Text(manifest));
        Assert.Equal(["SOAP-getPhoto-03"], manifest.NotCaptured);
        Assert.NotNull(rulesText);
        Assert.Equal(RulesNode().ToJsonString(), NormalisationRules.Parse(rulesText).ToJsonNode().ToJsonString());
        Assert.Equal(RulesNode().ToJsonString(), rules.ToJsonNode().ToJsonString());
        Assert.NotNull(conformance);
        Assert.Equal(ConformanceNode().ToJsonString(), conformance.ToJsonString());
        Assert.Equal(WsdlBytes, wsdl);
        Assert.True(Store.WsdlExists);
    }

    [Theory]
    [InlineData(RestRecordPath)]
    [InlineData(PhotoRecordPath)]
    [InlineData(ManifestFile)]
    [InlineData(RulesFile)]
    [InlineData(ConformanceFile)]
    public async Task JsonFiles_AreUtf8WithoutBomWithLfOnlyAndOneTrailingLf(string file)
    {
        await WriteFixture(Store, useAsync: false);

        var bytes = await File.ReadAllBytesAsync(Full(file), TestCancellation);

        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.True(BaselineResponse.IsTextRoundTrippable(bytes));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);
    }

    [Fact]
    public async Task RecordAndRawFiles_HoldExactlyTheirBytes()
    {
        await WriteFixture(Store, useAsync: false);

        Assert.Equal(BaselineStore.SerializeDocument(AgentRecord), await ReadBytes(RestRecordPath));
        Assert.Equal(BaselineStore.SerializeDocument(Manifest), await ReadBytes(ManifestFile));
        Assert.Equal(PhotoBody, await ReadBytes(PhotoBodyPath));
        Assert.Equal(WsdlBytes, await ReadBytes(WsdlFile));
    }

    [Fact]
    public async Task Rewrite_AndAsyncWrite_ProduceByteIdenticalTrees()
    {
        var syncRoot = Full("sync");
        var asyncRoot = Full("async");
        var syncStore = new BaselineStore(new HarnessOptions { RepoRoot = syncRoot });
        var asyncStore = new BaselineStore(new HarnessOptions { RepoRoot = asyncRoot });

        await WriteFixture(syncStore, useAsync: false);
        var first = Snapshot(syncRoot);
        await WriteFixture(syncStore, useAsync: false);
        await WriteFixture(asyncStore, useAsync: true);

        Assert.Equal(7, first.Count);
        Assert.Equal(first, Snapshot(syncRoot));
        Assert.Equal(first, Snapshot(asyncRoot));
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task Readers_ReturnReplayOrderAcrossSurfaces(string reader)
    {
        WriteCasesAcrossSurfaces();

        var keys = await ReadWith(reader);

        Assert.Equal([1, 2, 3, 4], keys.Select(static key => key.Order));
        Assert.Equal(
            ["SOAP-getPhoto-01", "REST-getAgent-02", "REST-getClient-01", "SOAP-getAgentNames-01"],
            keys.Select(static key => key.CaseId));
        Assert.Equal(
            ["Soap.getPhoto", "Rest.getAgent", "Rest.getClient", "Soap.getAgentNames"],
            keys.Select(static key => key.Operation));
    }

    // For each key of the index, the per-case reader returns exactly the record and the body bytes the
    // whole-baseline reader returns for that case: JSON text, a body file, and SOAP text.
    [Theory]
    [InlineData("SOAP-getPhoto-01")]
    [InlineData("REST-getAgent-02")]
    [InlineData("REST-getClient-01")]
    [InlineData("SOAP-getAgentNames-01")]
    public async Task ReadEntry_ReturnsTheRecordAndBodyReadEntriesReturns(string caseId)
    {
        WriteCasesAcrossSurfaces();

        var keys = await Store.ReadRecordIndexAsync(TestCancellation);
        var entries = await Store.ReadEntriesAsync(TestCancellation);

        Assert.Equal(Keys(entries.Select(static entry => entry.Record)), keys);
        var expected = Assert.Single(
            entries, candidate => string.Equals(candidate.Record.CaseId, caseId, StringComparison.Ordinal));
        var key = Assert.Single(keys, candidate => string.Equals(candidate.CaseId, caseId, StringComparison.Ordinal));
        var entry = await Store.ReadEntryAsync(key, TestCancellation);
        Assert.Equal(Text(expected.Record), Text(entry.Record));
        Assert.Equal(expected.Body.ToArray(), entry.Body.ToArray());
    }

    // Several multi-MiB inline bodies: the index lists only keys, whose type carries no request, response or
    // body, and each case read returns its whole body, byte for byte.
    [Fact]
    public async Task RecordIndex_LargeInlineBodies_AreListedAsKeysAndEachReadsWhole()
    {
        BaselineRecordKey[] written =
        [
            .. Enumerable.Range(1, 4).Select(order =>
            {
                var body = LargeJsonBody(order);
                var record = NewRecord(order, $"REST-getAgent-{order:D2}", "Rest.getAgent", body);
                Store.WriteRecord(record, body);
                return KeyOf(record);
            }),
        ];

        var keys = await Store.ReadRecordIndexAsync(TestCancellation);

        Assert.Equal(
            [("CaseId", typeof(string)), ("Operation", typeof(string)), ("Order", typeof(int))],
            typeof(BaselineRecordKey).GetProperties()
                .Select(static property => (property.Name, property.PropertyType))
                .OrderBy(static property => property.Name, StringComparer.Ordinal));
        Assert.Equal(written, keys);
        foreach (var key in keys)
        {
            var expected = LargeJsonBody(key.Order);
            var entry = await Store.ReadEntryAsync(key, TestCancellation);
            Assert.Equal(expected.Length, entry.Body.Length);
            Assert.True(expected.AsSpan().SequenceEqual(entry.Body.Span), $"{key.CaseId} read back other bytes");
            Assert.Equal(Encoding.UTF8.GetString(expected), entry.Record.Response.BodyText);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EntriesAndBodyBytes_AreTheVerifiedRawBodies(bool useAsync)
    {
        await WriteFixture(Store, useAsync: false);

        var entries = useAsync ? await Store.ReadEntriesAsync(TestCancellation) : Store.ReadEntries();

        Assert.Equal(2, entries.Count);
        var (agent, photo) = (entries[0], entries[1]);
        Assert.Equal(AgentBody, agent.Body.ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes(agent.Record.Response.BodyText!), agent.Body.ToArray());
        Assert.Equal(PhotoBody, photo.Body.ToArray());
        Assert.Null(photo.Record.Response.BodyText);
        foreach (var entry in entries)
        {
            var body = useAsync
                ? await Store.ReadBodyBytesAsync(entry.Record, TestCancellation)
                : Store.ReadBodyBytes(entry.Record);
            Assert.Equal(entry.Body.ToArray(), body);
        }
    }

    [Fact]
    public async Task AbsentBaseline_ReadsAsAbsentAndCreatesNothing()
    {
        Assert.False(Store.TryReadManifest(out var manifest));
        Assert.Null(manifest);
        Assert.Null(await Store.TryReadManifestAsync(TestCancellation));
        Assert.Null(Store.TryReadConformance());
        Assert.Null(await Store.TryReadConformanceAsync(TestCancellation));
        Assert.Null(Store.ReadNormalisationRules());
        Assert.Null(await Store.ReadNormalisationRulesAsync(TestCancellation));
        Assert.Null(Store.ReadWsdlBytes());
        Assert.Null(await Store.ReadWsdlBytesAsync(TestCancellation));
        Assert.False(Store.WsdlExists);
        Assert.Empty(Store.ReadRecords());
        Assert.Empty(await Store.ReadRecordsAsync(TestCancellation));
        Assert.Empty(Store.ReadEntries());
        Assert.Empty(await Store.ReadEntriesAsync(TestCancellation));
        Assert.Empty(await Store.ReadRecordIndexAsync(TestCancellation));
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.ReadEntryAsync(KeyOf(AgentRecord), TestCancellation));
        Assert.Empty(Directory.EnumerateFileSystemEntries(StoreRoot).Select(Relative));
    }

    // ---- Write refusals -----------------------------------------------------------------------------

    [Fact]
    public async Task WriteRecord_NotLabelledStandIn_IsRefusedAndWritesNothing()
    {
        var record = NewRecord(1, "REST-getAgent-01", "Rest.getAgent", AgentBody, source: "live-jade");

        var ex = Assert.Throws<InvalidOperationException>(() => Store.WriteRecord(record, AgentBody));
        var asyncEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Store.WriteRecordAsync(record, AgentBody, TestCancellation));

        Assert.Contains("every record must be labelled 'jade-standin'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ex.Message, asyncEx.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(StoreRoot).Select(Relative));
    }

    [Theory]
    [InlineData("sha-mismatch", "states bodySha256")]
    [InlineData("text-and-file", "holds both bodyText and bodyFile")]
    [InlineData("foreign-body-file", "names body file '001-getPhoto-02.body.bin'")]
    [InlineData("lossy-text", "bodyText does not reproduce the raw body byte for byte")]
    [InlineData("edited-text", "bodyText does not reproduce the raw body byte for byte")]
    [InlineData("surface-mismatch", "name different surfaces")]
    [InlineData("method-mismatch", "does not name the method 'getAgent'")]
    public async Task WriteRecord_InconsistentRecord_IsInvalidDataAndWritesNothing(string defect, string reason)
    {
        var (record, rawBody) = InconsistentRecord(defect);

        var ex = Assert.Throws<InvalidDataException>(() => Store.WriteRecord(record, rawBody));
        var asyncEx = await Assert.ThrowsAsync<InvalidDataException>(
            () => Store.WriteRecordAsync(record, rawBody, TestCancellation));

        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(ex.Message, asyncEx.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(StoreRoot).Select(Relative));
    }

    // A method name that climbs out of baseline/ is refused before any path is formed from it.
    [Theory]
    [InlineData("path-traversal", "jadeMethod")]
    [InlineData("order-zero", "position")]
    [InlineData("order-1000", "position")]
    [InlineData("no-request", "record")]
    [InlineData("null-record", "record")]
    public async Task WriteRecord_MalformedRecord_IsArgumentErrorAndWritesNothing(string defect, string parameter)
    {
        var record = MalformedRecord(defect);

        var ex = Assert.ThrowsAny<ArgumentException>(() => Store.WriteRecord(record, AgentBody));
        var asyncEx = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => Store.WriteRecordAsync(record, AgentBody, TestCancellation));

        Assert.Equal(parameter, ex.ParamName);
        Assert.Equal(parameter, asyncEx.ParamName);
        Assert.Empty(Directory.EnumerateFileSystemEntries(StoreRoot).Select(Relative));
    }

    // ---- Read refusals ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(DamagedRecords))]
    public async Task Readers_DamagedRecord_IsInvalidDataNamingTheFile(
        string damage, string reader, string file, string reason)
    {
        await WriteFixture(Store, useAsync: false);
        await Damage(damage);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ReadWith(reader));

        Assert.Contains(file, ex.Message, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DamagedBodies))]
    public async Task ReadBodyBytes_DamagedBody_IsInvalidData(string damage, bool useAsync, string message)
    {
        await WriteFixture(Store, useAsync: false);
        var record = await DamagedBodyRecord(damage);

        var ex = useAsync
            ? await Assert.ThrowsAsync<InvalidDataException>(() => Store.ReadBodyBytesAsync(record, TestCancellation))
            : Assert.Throws<InvalidDataException>(() => Store.ReadBodyBytes(record));

        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
    }

    // The index reads no body, so a damaged body leaves its record listed; the one verified read of that case
    // refuses it, naming its record file, and the other case still reads as written, byte for byte.
    [Theory]
    [InlineData("tampered-body-file", PhotoRecordPath, "is invalid: its body hashes to")]
    [InlineData("missing-body-file", PhotoRecordPath, $"is invalid: its body file {PhotoBodyPath} is missing.")]
    [InlineData("tampered-body-text", RestRecordPath, "is invalid: its body hashes to")]
    public async Task RecordIndex_DamagedBody_IsListedAndRefusedWhenItsCaseIsRead(
        string damage, string damagedFile, string reason)
    {
        await WriteFixture(Store, useAsync: false);
        await Damage(damage);

        var keys = await Store.ReadRecordIndexAsync(TestCancellation);

        Assert.Equal([KeyOf(AgentRecord), KeyOf(PhotoRecord)], keys);
        foreach (var key in keys)
        {
            var (file, record, body) = string.Equals(key.CaseId, AgentRecord.CaseId, StringComparison.Ordinal)
                ? (RestRecordPath, AgentRecord, AgentBody)
                : (PhotoRecordPath, PhotoRecord, PhotoBody);
            if (string.Equals(file, damagedFile, StringComparison.Ordinal))
            {
                var ex = await Assert.ThrowsAsync<InvalidDataException>(
                    () => Store.ReadEntryAsync(key, TestCancellation));
                Assert.StartsWith($"Baseline record {file} {reason}", ex.Message, StringComparison.Ordinal);
            }
            else
            {
                var entry = await Store.ReadEntryAsync(key, TestCancellation);
                Assert.Equal(Text(record), Text(entry.Record));
                Assert.Equal(body, entry.Body.ToArray());
            }
        }
    }

    // The label is checked where the body is verified, so a record labelled other than jade-standin, or with no
    // source, is still listed by the index, and every reader that verifies bodies refuses it, naming its record
    // file; the other case still reads as written.
    [Theory]
    [MemberData(nameof(UnlabelledRecordReads))]
    public async Task Readers_RecordNotLabelledStandIn_IsListedAndRefusedWhereItsBodyIsVerified(
        string? source, string reader, string reason)
    {
        await WriteFixture(Store, useAsync: false);
        await EditRecord(RestRecordPath, json =>
        {
            if (source is null)
            {
                json.Remove("source");
            }
            else
            {
                json["source"] = source;
            }
        });

        var keys = await Store.ReadRecordIndexAsync(TestCancellation);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            if (string.Equals(reader, "ReadEntryAsync", StringComparison.Ordinal))
            {
                await Store.ReadEntryAsync(keys[0], TestCancellation);
            }
            else
            {
                await ReadWith(reader);
            }
        });

        Assert.Equal([KeyOf(AgentRecord), KeyOf(PhotoRecord)], keys);
        Assert.Equal(
            $"Baseline record {RestRecordPath} is invalid: {reason}; every record must be labelled 'jade-standin'.",
            ex.Message);
        Assert.Equal(PhotoBody, (await Store.ReadEntryAsync(keys[1], TestCancellation)).Body.ToArray());
    }

    // The per-case reader reads the record file again, so a record changed or removed after it was indexed is
    // refused, naming the file, rather than replayed as the indexed case; the other case still reads.
    [Theory]
    [InlineData("deleted", $"Baseline record {RestRecordPath} of case REST-getAgent-01 is missing.")]
    [InlineData(
        "other-position",
        $"Baseline record {RestRecordPath} is invalid: case REST-getAgent-01 at position 3 must be stored as "
        + "003-getAgent-01.json.")]
    [InlineData(
        "other-case",
        $"Baseline record {RestRecordPath} is invalid: case REST-getAgent-02 at position 1 must be stored as "
        + "001-getAgent-02.json.")]
    [InlineData(
        "other-operation",
        $"Baseline record {RestRecordPath} is invalid: it holds case REST-getAgent-01 of operation REST.getAgent at "
        + "position 1, not the indexed case REST-getAgent-01 of operation Rest.getAgent at position 1.")]
    public async Task ReadEntry_RecordChangedAfterIndexing_IsInvalidDataNamingTheFile(string change, string message)
    {
        await WriteFixture(Store, useAsync: false);
        var keys = await Store.ReadRecordIndexAsync(TestCancellation);
        await ChangeAgentRecord(change);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Store.ReadEntryAsync(keys[0], TestCancellation));

        Assert.Equal(message, ex.Message);
        Assert.Equal(PhotoBody, (await Store.ReadEntryAsync(keys[1], TestCancellation)).Body.ToArray());
    }

    // A key becomes a path only through the checked file-name parts, so one that names no record file, a
    // climb out of records/ included, is an argument error.
    [Theory]
    [InlineData(1, "Rest-getAgent-01", "Rest.getAgent", "caseId")]
    [InlineData(0, "REST-getAgent-01", "Rest.getAgent", "position")]
    [InlineData(1, "REST-getAgent-01", "RestgetAgent", "jadeMethod")]
    [InlineData(1, "REST-/../../../../escaped-01", "Rest./../../../../escaped", "jadeMethod")]
    [InlineData(1, "REST-getAgent-0a", "Rest.getAgent", "caseNumber")]
    public async Task ReadEntry_KeyNamingNoRecordFile_IsArgumentError(
        int order, string caseId, string operation, string parameter)
    {
        await WriteFixture(Store, useAsync: false);

        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => Store.ReadEntryAsync(new BaselineRecordKey(order, caseId, operation), TestCancellation));

        Assert.Equal(parameter, ex.ParamName);
    }

    [Fact]
    public async Task ReadBodyBytes_RecordWithoutResponse_IsArgumentError()
    {
        var record = AgentRecord with { Response = null! };

        var ex = Assert.Throws<ArgumentException>(() => Store.ReadBodyBytes(record));
        var asyncEx = await Assert.ThrowsAsync<ArgumentException>(
            () => Store.ReadBodyBytesAsync(record, TestCancellation));

        Assert.Equal("record", ex.ParamName);
        Assert.Equal("record", asyncEx.ParamName);
    }

    [Theory]
    [InlineData("{", "is not a valid BaselineManifest document")]
    [InlineData("[]", "is not a valid BaselineManifest document")]
    [InlineData("""{"source":"jade-standin",}""", "is not a valid BaselineManifest document")]
    [InlineData("""{"counts":{"plannedCases":"56"}}""", "is not a valid BaselineManifest document")]
    [InlineData("null", "holds a JSON null instead of a BaselineManifest")]
    public async Task TryReadManifest_MalformedFile_IsInvalidDataNamingTheFile(string json, string reason)
    {
        await WriteRaw(ManifestFile, Encoding.UTF8.GetBytes(json));

        var ex = Assert.Throws<InvalidDataException>(() => Store.TryReadManifest(out _));
        var asyncEx = await Assert.ThrowsAsync<InvalidDataException>(
            () => Store.TryReadManifestAsync(TestCancellation));

        Assert.StartsWith(ManifestFile + " ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(ex.Message, asyncEx.Message);
    }

    [Fact]
    public async Task TryReadManifest_HandWrittenFileWithByteOrderMark_Parses()
    {
        const string json = """
            {
              "captureExecution": "not-run",
              "counts": { "plannedOperations": 22, "plannedCases": 56 },
              "notCaptured": ["REST-getAgent-01", "SOAP-getPhoto-02"],
              "wsdlCaptured": false,
              "startingCommit": "19a5189e9075349aca54de1fb5fdf2d8727403e7",
              "blockingError": "capture could not start"
            }
            """;
        await WriteRaw(ManifestFile, [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(json)]);

        Assert.True(Store.TryReadManifest(out var manifest));
        var asyncManifest = await Store.TryReadManifestAsync(TestCancellation);

        Assert.True(manifest.IsNotRun);
        Assert.False(manifest.HasSeedFacts);
        Assert.Equal(["REST-getAgent-01", "SOAP-getPhoto-02"], manifest.NotCaptured);
        Assert.Equal(new ManifestCounts(22, 56, 0, 0), manifest.Counts);
        Assert.Equal("capture could not start", manifest.BlockingError);
        Assert.NotNull(asyncManifest);
        Assert.Equal(Text(manifest), Text(asyncManifest));
    }

    [Theory]
    [InlineData("null", "holds a JSON null instead of a document")]
    [InlineData("{", "is not valid JSON")]
    [InlineData("""{"operations":22,}""", "is not valid JSON")]
    public async Task TryReadConformance_NullOrMalformedFile_IsInvalidDataNamingTheFile(string json, string reason)
    {
        await WriteRaw(ConformanceFile, Encoding.UTF8.GetBytes(json));

        var ex = Assert.Throws<InvalidDataException>(() => Store.TryReadConformance());
        var asyncEx = await Assert.ThrowsAsync<InvalidDataException>(
            () => Store.TryReadConformanceAsync(TestCancellation));

        Assert.StartsWith(ConformanceFile + " ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(ex.Message, asyncEx.Message);
    }

    // ---- Clearing records and deleting the WSDL -----------------------------------------------------

    [Fact]
    public async Task ClearRecords_DeletesEveryFileInBothSurfaceDirectoriesAndKeepsTheRest()
    {
        await WriteFixture(Store, useAsync: false);
        await WriteRaw("baseline/records/soap/009-getPhoto-09.body.bin", PhotoBody);
        await WriteRaw("baseline/records/rest/notes.txt", "stray"u8.ToArray());
        await WriteRaw("baseline/records/rest/kept/inner.json", "{}"u8.ToArray());
        await WriteRaw("baseline/records/index.txt", "kept"u8.ToArray());
        var kept = Snapshot(Full("baseline"))
            .Where(static entry => !IsDirectlyIn(entry, "records/rest") && !IsDirectlyIn(entry, "records/soap"))
            .ToList();

        Store.ClearRecords(TestCancellation);

        Assert.Empty(Directory.GetFiles(Full("baseline/records/rest")).Select(Relative));
        Assert.Empty(Directory.GetFiles(Full("baseline/records/soap")).Select(Relative));
        Assert.Equal(6, kept.Count);
        Assert.Equal(kept, Snapshot(Full("baseline")));
    }

    [Fact]
    public void ClearRecords_MissingSurfaceDirectories_AreCreatedEmpty()
    {
        Store.ClearRecords(TestCancellation);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Full("baseline/records/rest")).Select(Relative));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Full("baseline/records/soap")).Select(Relative));
    }

    [Fact]
    public void DeleteWsdl_RemovesTheFileAndIsANoOpWhenAbsent()
    {
        Store.DeleteWsdl();
        Assert.Empty(Directory.EnumerateFileSystemEntries(StoreRoot).Select(Relative));

        Store.WriteManifest(Manifest);
        Store.WriteWsdl(WsdlBytes);
        Store.DeleteWsdl();

        Assert.False(Store.WsdlExists);
        Assert.False(File.Exists(Full(WsdlFile)));
        Assert.True(File.Exists(Full(ManifestFile)));
    }

    [Fact]
    public async Task DeleteManifest_RemovesTheManifestAndAStaleTemporaryAndIsANoOpWhenAbsent()
    {
        Store.DeleteManifest();
        Assert.Empty(Directory.EnumerateFileSystemEntries(StoreRoot).Select(Relative));

        Directory.CreateDirectory(Full("baseline"));
        Store.DeleteManifest();
        Assert.Empty(Directory.EnumerateFileSystemEntries(Full("baseline")).Select(Relative));

        await WriteFixture(Store, useAsync: false);
        await WriteRaw(ManifestTemporaryFile, "{\"source\":\"jade-"u8.ToArray());
        var kept = Snapshot(Full("baseline"))
            .Where(static entry => !entry.StartsWith("manifest.json=", StringComparison.Ordinal)
                && !entry.StartsWith(".manifest.json.tmp=", StringComparison.Ordinal))
            .ToList();

        Store.DeleteManifest();

        Assert.Null(ReadManifest());
        Assert.False(File.Exists(Full(ManifestFile)));
        Assert.False(File.Exists(Full(ManifestTemporaryFile)));
        Assert.Equal(6, kept.Count);
        Assert.Equal(kept, Snapshot(Full("baseline")));
    }

    // ---- Containment through links ------------------------------------------------------------------

    [Fact]
    public async Task LinkedSurfaceDirectory_RefusesClearAndWrite()
    {
        CreateProtectedDirectory();
        Directory.CreateDirectory(Full("baseline/records"));
        Directory.CreateSymbolicLink(Full("baseline/records/rest"), ProtectedDirectory);
        AssertLinkReachesProtectedDirectory("baseline/records/rest");
        var before = Snapshot(ProtectedDirectory);

        AssertLinkRefusal(
            Assert.Throws<InvalidOperationException>(() => Store.ClearRecords(TestCancellation)),
            "baseline/records/rest");
        AssertLinkRefusal(
            Assert.Throws<InvalidOperationException>(() => Store.WriteRecord(AgentRecord, AgentBody)),
            "baseline/records/rest");
        AssertLinkRefusal(
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Store.WriteRecordAsync(AgentRecord, AgentBody, TestCancellation)),
            "baseline/records/rest");

        Assert.Equal(before, Snapshot(ProtectedDirectory));
    }

    [Fact]
    public async Task LinkedRecordEntry_RefusesClearAndDeletesNothing()
    {
        await WriteFixture(Store, useAsync: false);
        CreateProtectedDirectory();
        const string link = "baseline/records/rest/005-getAgent-05.json";
        var target = Path.Combine(ProtectedDirectory, SentinelName);
        File.CreateSymbolicLink(Full(link), target);
        var before = Snapshot(StoreRoot);

        AssertLinkRefusal(
            Assert.Throws<InvalidOperationException>(() => Store.ClearRecords(TestCancellation)), link);

        Assert.Equal(before, Snapshot(StoreRoot));
        Assert.Equal(SentinelPath, LinkTargetOf(link));
    }

    [Fact]
    public async Task LinkedManifest_RefusesSyncAndAsyncWrites()
    {
        CreateProtectedDirectory();
        Directory.CreateDirectory(Full("baseline"));
        var target = Path.Combine(ProtectedDirectory, SentinelName);
        File.CreateSymbolicLink(Full(ManifestFile), target);

        AssertLinkRefusal(Assert.Throws<InvalidOperationException>(() => Store.WriteManifest(Manifest)), ManifestFile);
        AssertLinkRefusal(
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Store.WriteManifestAsync(Manifest, TestCancellation)),
            ManifestFile);

        Assert.Equal(SentinelBytes, await File.ReadAllBytesAsync(target, TestCancellation));
        Assert.Equal(SentinelPath, LinkTargetOf(ManifestFile));
    }

    // Both names are checked before either is deleted, so a link at one refuses the delete and keeps the other.
    [Theory]
    [InlineData(ManifestFile)]
    [InlineData(ManifestTemporaryFile)]
    public async Task LinkAtManifestOrItsTemporary_RefusesDeleteAndDeletesNothing(string link)
    {
        await WriteFixture(Store, useAsync: false);
        await WriteRaw(ManifestTemporaryFile, "stale"u8.ToArray());
        CreateProtectedDirectory();
        File.Delete(Full(link));
        File.CreateSymbolicLink(Full(link), Path.Combine(ProtectedDirectory, SentinelName));
        var before = Snapshot(StoreRoot);

        AssertLinkRefusal(Assert.Throws<InvalidOperationException>(() => Store.DeleteManifest()), link);

        Assert.Equal(before, Snapshot(StoreRoot));
        Assert.Equal(SentinelPath, LinkTargetOf(link));
    }

    // A link at the temporary name, to a file or dangling, refuses the write before any byte is written; it is
    // never followed, it is left in place, and the published manifest stays as it was.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LinkAtManifestTemporary_RefusesTheWriteAndIsNeverFollowed(bool useAsync, bool dangling)
    {
        Store.WriteManifest(Manifest);
        var previous = await ReadBytes(ManifestFile);
        CreateProtectedDirectory();
        var target = Path.Combine(ProtectedDirectory, dangling ? "written-through-link" : SentinelName);
        File.CreateSymbolicLink(Full(ManifestTemporaryFile), target);
        var changed = Manifest with { CaptureCommit = "fedcba9876543210fedcba9876543210fedcba98" };

        var refusal = useAsync
            ? await Assert.ThrowsAsync<InvalidOperationException>(
                () => Store.WriteManifestAsync(changed, TestCancellation))
            : Assert.Throws<InvalidOperationException>(() => Store.WriteManifest(changed));

        AssertLinkRefusal(refusal, ManifestTemporaryFile);
        Assert.Equal(Relative(target), LinkTargetOf(ManifestTemporaryFile));
        Assert.Equal(previous, await ReadBytes(ManifestFile));
        if (dangling)
        {
            Assert.False(File.Exists(target));
        }
        else
        {
            Assert.Equal(SentinelBytes, await File.ReadAllBytesAsync(target, TestCancellation));
        }
    }

    [Fact]
    public async Task LinkedWsdlDirectory_RefusesWriteAndDelete()
    {
        CreateProtectedDirectory();
        await File.WriteAllBytesAsync(
            Path.Combine(ProtectedDirectory, "standin-ErewhonWebService.wsdl"), SentinelBytes, TestCancellation);
        Directory.CreateDirectory(Full("baseline"));
        Directory.CreateSymbolicLink(Full("baseline/wsdl"), ProtectedDirectory);
        AssertLinkReachesProtectedDirectory("baseline/wsdl");
        var before = Snapshot(ProtectedDirectory);

        AssertLinkRefusal(Assert.Throws<InvalidOperationException>(() => Store.WriteWsdl(WsdlBytes)), "baseline/wsdl");
        AssertLinkRefusal(
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Store.WriteWsdlAsync(WsdlBytes, TestCancellation)),
            "baseline/wsdl");
        AssertLinkRefusal(Assert.Throws<InvalidOperationException>(() => Store.DeleteWsdl()), "baseline/wsdl");

        Assert.Equal(before, Snapshot(ProtectedDirectory));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("body-file")]
    [InlineData("manifest")]
    [InlineData("normalisation-rules")]
    [InlineData("conformance")]
    [InlineData("wsdl")]
    public async Task DanglingLinkAtWriteTarget_IsRefusedAndItsTargetNeverCreated(string document)
    {
        CreateProtectedDirectory();
        var writer = Writer(document);
        var link = Full(writer.File);
        var target = Path.Combine(ProtectedDirectory, "written-through-link");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);

        AssertLinkRefusal(Assert.Throws<InvalidOperationException>(() => writer.Write(Store)), writer.File);
        AssertLinkRefusal(
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(Store, TestCancellation)),
            writer.File);

        Assert.False(File.Exists(target));
        Assert.Equal(ProtectedPath + "/written-through-link", LinkTargetOf(writer.File));
        Assert.Equal(
            [writer.File], Directory.EnumerateFiles(Full("baseline"), "*", SearchOption.AllDirectories).Select(Relative));
    }

    [Fact]
    public async Task LinkedBaselineDirectory_RefusesEveryWriteAndDelete()
    {
        CreateProtectedDirectory();
        Directory.CreateSymbolicLink(Full("baseline"), ProtectedDirectory);
        AssertLinkReachesProtectedDirectory("baseline");
        var before = Snapshot(ProtectedDirectory);

        foreach (var document in DocumentNames)
        {
            var writer = Writer(document);
            AssertLinkRefusal(Assert.Throws<InvalidOperationException>(() => writer.Write(Store)), "baseline");
            AssertLinkRefusal(
                await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(Store, TestCancellation)),
                "baseline");
        }

        AssertLinkRefusal(
            Assert.Throws<InvalidOperationException>(() => Store.ClearRecords(TestCancellation)), "baseline");
        AssertLinkRefusal(Assert.Throws<InvalidOperationException>(() => Store.DeleteWsdl()), "baseline");

        Assert.Equal(before, Snapshot(ProtectedDirectory));
    }

    // A hard link is no reparse point, so the write is allowed; it must replace the link, never write through it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HardLinkAtManifest_IsReplacedWithoutChangingItsOtherName(bool useAsync)
    {
        CreateProtectedDirectory();
        Directory.CreateDirectory(Full("baseline"));
        var target = Path.Combine(ProtectedDirectory, SentinelName);
        CreateHardLink(Full(ManifestFile), target);
        await File.AppendAllTextAsync(Full(ManifestFile), "linked\n", TestCancellation);
        var before = await File.ReadAllBytesAsync(target, TestCancellation);
        Assert.Equal([.. SentinelBytes, .. "linked\n"u8], before);

        if (useAsync)
        {
            await Store.WriteManifestAsync(Manifest, TestCancellation);
        }
        else
        {
            Store.WriteManifest(Manifest);
        }

        Assert.Equal(BaselineStore.SerializeDocument(Manifest), await ReadBytes(ManifestFile));
        Assert.Equal(before, await File.ReadAllBytesAsync(target, TestCancellation));
    }

    // ---- Atomic manifest publication ----------------------------------------------------------------

    // A temporary file a crash left, plain or a hard link to a file elsewhere, is replaced without writing
    // through it, and publication leaves only the manifest behind.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WriteManifest_ReplacesAStaleTemporaryAndLeavesNone(bool useAsync, bool hardLinked)
    {
        Store.WriteManifest(Manifest);
        CreateProtectedDirectory();
        var protectedFile = Path.Combine(ProtectedDirectory, SentinelName);
        if (hardLinked)
        {
            CreateHardLink(Full(ManifestTemporaryFile), protectedFile);
        }
        else
        {
            await WriteRaw(ManifestTemporaryFile, "{\"source\":\"jade-"u8.ToArray());
        }

        var changed = Manifest with { CaptureCommit = "fedcba9876543210fedcba9876543210fedcba98" };
        if (useAsync)
        {
            await Store.WriteManifestAsync(changed, TestCancellation);
        }
        else
        {
            Store.WriteManifest(changed);
        }

        Assert.Equal(BaselineStore.SerializeDocument(changed), await ReadBytes(ManifestFile));
        Assert.Equal(
            [ManifestFile], Directory.EnumerateFileSystemEntries(Full("baseline"), "*", SearchOption.AllDirectories).Select(Relative));
        Assert.Equal(SentinelBytes, await File.ReadAllBytesAsync(protectedFile, TestCancellation));
    }

    // A rename that fails after the bytes were written (here onto a directory at the manifest's name) removes
    // the temporary file and leaves the entry at the manifest's name as it was.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRename_RemovesTheTemporaryAndLeavesTheManifestEntryAsItWas(bool useAsync)
    {
        await WriteRaw(ManifestFile + "/kept.json", "{}"u8.ToArray());
        var before = Snapshot(Full("baseline"));

        var failure = useAsync
            ? await Assert.ThrowsAsync<IOException>(() => Store.WriteManifestAsync(Manifest, TestCancellation))
            : Assert.Throws<IOException>(() => Store.WriteManifest(Manifest));

        Assert.StartsWith($"BaselineStore could not publish '{ManifestFile}'", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Full(ManifestTemporaryFile)));
        Assert.Equal(before, Snapshot(Full("baseline")));
    }

    // A publication cancelled while it runs either completes or leaves the previous manifest, never part of
    // one, and never leaves its temporary file. Where each cancellation lands depends on timing, so only
    // that is asserted, in every round.
    [Fact]
    public async Task CancelledManifestPublication_LeavesThePreviousOrTheNewManifestAndNoTemporary()
    {
        var previous = BaselineStore.SerializeDocument(Manifest);
        var changed = Manifest with { CaptureCommit = "fedcba9876543210fedcba9876543210fedcba98" };
        var published = BaselineStore.SerializeDocument(changed);

        for (var iteration = 0; iteration < CancellationIterations; iteration++)
        {
            Store.WriteManifest(Manifest);
            using var cancellation = new CancellationTokenSource();
            var cancel = Task.Run(cancellation.Cancel, TestCancellation);
            var outcome = await Outcome(() => Store.WriteManifestAsync(changed, cancellation.Token));
            await cancel;

            if (outcome is null)
            {
                Assert.Equal(published, await ReadBytes(ManifestFile));
            }
            else
            {
                Assert.IsAssignableFrom<OperationCanceledException>(outcome);
                Assert.Equal(previous, await ReadBytes(ManifestFile));
            }

            Assert.False(File.Exists(Full(ManifestTemporaryFile)));
        }
    }

    // A directory swapped for a link while the store works must never redirect it. A background thread keeps
    // moving baseline/records/rest and baseline/wsdl aside, putting a link to the protected directory in each
    // place and moving the directory back, while every write and delete runs against the moving tree. Which
    // operations succeed and which are refused depends on timing, so only that each one succeeds or is
    // refused, and that the protected directory is unchanged, is asserted.
    [Fact]
    public async Task ConcurrentParentSwap_NeverWritesOrDeletesThroughTheSwappedInLink()
    {
        CreateProtectedDirectory();
        foreach (var name in SwapSentinelNames)
        {
            await File.WriteAllBytesAsync(Path.Combine(ProtectedDirectory, name), SentinelBytes, TestCancellation);
        }

        await WriteFixture(Store, useAsync: false);
        var before = Snapshot(ProtectedDirectory);
        Func<Task>[] operations =
        [
            () => Synchronously(() => Store.WriteRecord(AgentRecord, AgentBody)),
            () => Store.WriteRecordAsync(AgentRecord, AgentBody, TestCancellation),
            () => Synchronously(() => Store.ClearRecords(TestCancellation)),
            () => Synchronously(() => Store.WriteWsdl(WsdlBytes)),
            () => Store.WriteWsdlAsync(WsdlBytes, TestCancellation),
            () => Synchronously(Store.DeleteWsdl),
        ];

        Exception? swapFailure = null;
        using var stop = new CancellationTokenSource();
        var swapper = new Thread(() => swapFailure = SwapForLinks(stop.Token)) { IsBackground = true };
        swapper.Start();
        try
        {
            for (var iteration = 0; iteration < SwapIterations; iteration++)
            {
                foreach (var operation in operations)
                {
                    AssertSucceededOrRefused(await Outcome(operation));
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            swapper.Join();
            RemoveSwappedInLinks();
        }

        Assert.Null(swapFailure);
        Assert.Equal(3, before.Count);
        Assert.Equal(before, Snapshot(ProtectedDirectory));
        Store.WriteRecord(AgentRecord, AgentBody);
        Assert.Equal(BaselineStore.SerializeDocument(AgentRecord), await ReadBytes(RestRecordPath));
    }

    // ---- Cancellation -------------------------------------------------------------------------------

    [Theory]
    [InlineData("WriteRecordAsync")]
    [InlineData("WriteManifestAsync")]
    [InlineData("WriteNormalisationRulesAsync")]
    [InlineData("WriteConformanceAsync")]
    [InlineData("WriteWsdlAsync")]
    [InlineData("TryReadManifestAsync")]
    [InlineData("ReadRecordsAsync")]
    [InlineData("ReadEntriesAsync")]
    [InlineData(RecordIndexReader)]
    [InlineData("ReadEntryAsync")]
    [InlineData("ReadBodyBytesAsync:file")]
    [InlineData("ReadBodyBytesAsync:text")]
    [InlineData("ReadNormalisationRulesAsync")]
    [InlineData("TryReadConformanceAsync")]
    [InlineData("ReadWsdlBytesAsync")]
    [InlineData("ClearRecords")]
    [InlineData("NormalisationRules.LoadAsync")]
    public async Task PreCancelledToken_CancelsAndLeavesTheTreeUnchanged(string api)
    {
        await WriteFixture(Store, useAsync: false);
        var before = Snapshot(StoreRoot);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CancellableCall(api, cancellation.Token));

        Assert.Equal(before, Snapshot(StoreRoot));
    }

    // ---- Containment of reads through links --------------------------------------------------------

    // A link at the file or on a directory above it refuses the read, whatever a link-following read would
    // have found there: the verified getPhoto body included, since the real file keeps the bytes its record
    // hashes. The refusal names the link and a path at or below it, and the link and its target stay as they were.
    [Theory]
    [MemberData(nameof(LinkedReads))]
    public async Task LinkOnTheWay_RefusesTheReadAndIsLeftInPlace(string document, string placement, string reader)
    {
        await WriteFixture(Store, useAsync: false);
        var file = ReadFile(document);
        var link = placement is "file" or "dangling" ? file : placement;
        var outside = Path.Combine(Path.GetTempPath(), "erewhon-baseline-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var target = Path.Combine(outside, Path.GetFileName(Full(link)));
            if (placement == "dangling")
            {
                File.Delete(Full(link));
                File.CreateSymbolicLink(Full(link), target);
            }
            else if (placement == "file")
            {
                File.Move(Full(link), target);
                File.CreateSymbolicLink(Full(link), target);
                Assert.Equal(await File.ReadAllBytesAsync(target, TestCancellation), await ReadBytes(file));
            }
            else
            {
                Directory.Move(Full(link), target);
                Directory.CreateSymbolicLink(Full(link), target);
                Assert.True(File.Exists(Full(file)), $"{file} is reachable through {link}");
            }

            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadThrough(reader));

            AssertReadLinkRefusal(refusal, link);

            // The outside directory is named per run, so both sides show it as a fixed token.
            const string outsideToken = "<outside-directory>";
            Assert.Equal(
                target.Replace(outside, outsideToken, StringComparison.Ordinal),
                new FileInfo(Full(link)).LinkTarget?.Replace(outside, outsideToken, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    // A directory standing at a file's name is no file: each reader of it reads the file as absent, as File.Exists
    // reports it, and the directory is left as it is.
    [Theory]
    [InlineData("manifest", false)]
    [InlineData("manifest", true)]
    [InlineData("normalisation-rules", false)]
    [InlineData("normalisation-rules", true)]
    [InlineData("conformance", false)]
    [InlineData("conformance", true)]
    [InlineData("wsdl", false)]
    [InlineData("wsdl", true)]
    [InlineData("record", false)]
    [InlineData("record", true)]
    [InlineData("body-file", false)]
    [InlineData("body-file", true)]
    public async Task DirectoryAtFileName_ReadsAsAbsent(string document, bool useAsync)
    {
        await WriteFixture(Store, useAsync: false);
        var file = ReadFile(document);
        File.Delete(Full(file));
        Directory.CreateDirectory(Full(file));

        switch (document)
        {
            case "manifest":
                Assert.Null(useAsync ? await Store.TryReadManifestAsync(TestCancellation) : ReadManifest());
                break;
            case "normalisation-rules":
                Assert.Null(useAsync
                    ? await Store.ReadNormalisationRulesAsync(TestCancellation)
                    : Store.ReadNormalisationRules());
                break;
            case "conformance":
                Assert.Null(useAsync
                    ? await Store.TryReadConformanceAsync(TestCancellation)
                    : Store.TryReadConformance());
                break;
            case "wsdl":
                Assert.Null(useAsync ? await Store.ReadWsdlBytesAsync(TestCancellation) : Store.ReadWsdlBytes());
                Assert.False(Store.WsdlExists);
                break;
            case "record":
                Assert.Equal([KeyOf(AgentRecord)], await ReadWith(useAsync ? "ReadRecordsAsync" : "ReadRecords"));
                var missing = await Assert.ThrowsAsync<InvalidDataException>(
                    () => Store.ReadEntryAsync(KeyOf(PhotoRecord), TestCancellation));
                Assert.Equal(
                    $"Baseline record {PhotoRecordPath} of case SOAP-getPhoto-02 is missing.", missing.Message);
                break;
            case "body-file":
                var absent = useAsync
                    ? await Assert.ThrowsAsync<InvalidDataException>(
                        () => Store.ReadBodyBytesAsync(PhotoRecord, TestCancellation))
                    : Assert.Throws<InvalidDataException>(() => Store.ReadBodyBytes(PhotoRecord));
                Assert.Equal(
                    $"Baseline case SOAP-getPhoto-02 is invalid: its body file {PhotoBodyPath} is missing.",
                    absent.Message);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(document), document, null);
        }

        Assert.True(Directory.Exists(Full(file)));
    }

    // ---- Special and oversized files -----------------------------------------------------------------

    // A FIFO with no writer, which a blocking open would wait on for ever, and a sparse file over the evidence
    // limit, which a whole read would allocate for, each refuse the read at once with an I/O error naming the
    // file, and are left in place.
    [Theory]
    [MemberData(nameof(RefusedFileReads))]
    public async Task SpecialOrOversizedFile_RefusesTheReadPromptlyAndIsLeftInPlace(string document, string kind, string reader)
    {
        await WriteFixture(Store, useAsync: false);
        var file = ReadFile(document);
        File.Delete(Full(file));
        string expected;
        if (kind == "fifo")
        {
            SpecialFileSupport.CreateFifo(Full(file));
            expected = $"BaselineStore could not read '{file}': it is not a regular file (a FIFO).";
        }
        else
        {
            PlantSparseFile(file, OversizedLength);
            expected = string.Create(
                CultureInfo.InvariantCulture,
                $"BaselineStore could not read '{file}': its {OversizedLength} bytes exceed the {HarnessOptions.EvidenceByteLimit}-byte limit.");
        }

        var read = Task.Run(() => ReadThrough(reader), TestCancellation);
        var refusal = await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(PromptLimit, TestCancellation));

        Assert.Equal(expected, refusal.Message);
        Assert.True(
            kind == "fifo" ? SpecialFileSupport.IsFifo(Full(file)) : new FileInfo(Full(file)).Length == OversizedLength,
            $"The refused {kind} must be left in place.");
    }

    // The reported length decides before any buffer exists, so a sparse record far over the limit costs the
    // synchronous reader, which runs on this thread alone, far fewer bytes than the record is long.
    [Fact]
    public async Task OversizedRecord_IsRefusedWithoutAllocatingItsLength()
    {
        await WriteFixture(Store, useAsync: false);
        File.Delete(Full(PhotoRecordPath));
        PlantSparseFile(PhotoRecordPath, 4L * HarnessOptions.EvidenceByteLimit);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<IOException>(() => Store.ReadEntries());
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(allocated < RefusalAllocationBound, $"The refusal allocated {allocated} bytes, as if it had read the record.");
    }

    // A file at relative that reports length without its bytes being written, so a huge one costs no disk.
    private void PlantSparseFile(string relative, long length)
    {
        using var handle = File.OpenHandle(Full(relative), FileMode.CreateNew, FileAccess.Write);
        RandomAccess.SetLength(handle, length);
    }

    private static BaselineRecord NewRecord(
        int order,
        string caseId,
        string operation,
        byte[] body,
        string? bodyFile = null,
        string source = SourceLabels.StandIn)
    {
        var soap = operation.StartsWith("Soap.", StringComparison.Ordinal);
        var request = soap
            ? new BaselineRequest(
                "POST",
                SoapPath,
                new Dictionary<string, string>
                {
                    ["Content-Type"] = BaselineSoapContentType,
                    ["SOAPAction"] = "\"urn:JadeWebServices/WebServiceHTTP/" + operation["Soap.".Length..] + "\"",
                },
                "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body/></soap:Envelope>")
            : new BaselineRequest(
                "GET", "/agent/Hank%20Williams", new Dictionary<string, string> { ["Accept"] = "application/json" }, null);
        return new BaselineRecord(
            order,
            caseId,
            operation,
            "success",
            false,
            "seed",
            source,
            request,
            BaselineResponse.FromRawBody(200, soap ? BaselineSoapContentType : JsonContentType, body, bodyFile));
    }

    private static (BaselineRecord Record, byte[] RawBody) InconsistentRecord(string defect) => defect switch
    {
        "sha-mismatch" => (AgentRecord, SoapBody),
        "text-and-file" => (PhotoRecord with { Response = PhotoRecord.Response with { BodyText = "photo" } }, PhotoBody),
        "foreign-body-file" => (
            NewRecord(2, "SOAP-getPhoto-02", "Soap.getPhoto", PhotoBody, "001-getPhoto-02.body.bin"), PhotoBody),
        "lossy-text" => (NewRecord(1, "REST-getAgent-01", "Rest.getAgent", PhotoBody), PhotoBody),
        "edited-text" => (AgentRecord with { Response = AgentRecord.Response with { BodyText = "{}" } }, AgentBody),
        "surface-mismatch" => (NewRecord(1, "REST-getAgent-01", "Soap.getAgent", AgentBody), AgentBody),
        "method-mismatch" => (NewRecord(1, "REST-getClient-01", "Rest.getAgent", AgentBody), AgentBody),
        _ => throw new ArgumentOutOfRangeException(nameof(defect), defect, null),
    };

    // path-traversal names a method that, were it accepted, would place the record at <root>/escaped-01.json.
    private static BaselineRecord MalformedRecord(string defect) => defect switch
    {
        "path-traversal" => NewRecord(
            1, "REST-/../../../../escaped-01", "Rest./../../../../escaped", AgentBody),
        "order-zero" => AgentRecord with { Order = 0 },
        "order-1000" => AgentRecord with { Order = 1000 },
        "no-request" => AgentRecord with { Request = null! },
        "null-record" => null!,
        _ => throw new ArgumentOutOfRangeException(nameof(defect), defect, null),
    };

    private static JsonNode RulesNode() => NormalisationRules.FromCatalogue(OperationCatalogue.Default).ToJsonNode();

    private static JsonNode ConformanceNode() => JsonNode.Parse(
        """{"operations":22,"gaps":[{"class":"DTOTenderSaleDetail","property":"tenderDate"}],"note":"<é>"}""")!;

    private static DocumentWriter Writer(string document) => document switch
    {
        "record" => new(
            RestRecordPath,
            static store => store.WriteRecord(AgentRecord, AgentBody),
            static (store, cancellationToken) => store.WriteRecordAsync(AgentRecord, AgentBody, cancellationToken)),
        "body-file" => new(
            PhotoBodyPath,
            static store => store.WriteRecord(PhotoRecord, PhotoBody),
            static (store, cancellationToken) => store.WriteRecordAsync(PhotoRecord, PhotoBody, cancellationToken)),
        "manifest" => new(
            ManifestFile,
            static store => store.WriteManifest(Manifest),
            static (store, cancellationToken) => store.WriteManifestAsync(Manifest, cancellationToken)),
        "normalisation-rules" => new(
            RulesFile,
            static store => store.WriteNormalisationRules(RulesNode()),
            static (store, cancellationToken) => store.WriteNormalisationRulesAsync(RulesNode(), cancellationToken)),
        "conformance" => new(
            ConformanceFile,
            static store => store.WriteConformance(ConformanceNode()),
            static (store, cancellationToken) => store.WriteConformanceAsync(ConformanceNode(), cancellationToken)),
        "wsdl" => new(
            WsdlFile,
            static store => store.WriteWsdl(WsdlBytes),
            static (store, cancellationToken) => store.WriteWsdlAsync(WsdlBytes, cancellationToken)),
        _ => throw new ArgumentOutOfRangeException(nameof(document), document, null),
    };

    // Writes the two records (the REST one with its body as text, the getPhoto one with a body file), the
    // manifest, the normalisation rules, the conformance result and the WSDL.
    private static async Task WriteFixture(BaselineStore store, bool useAsync)
    {
        foreach (var document in DocumentNames)
        {
            var writer = Writer(document);
            if (useAsync)
            {
                await writer.WriteAsync(store, TestCancellation);
            }
            else
            {
                writer.Write(store);
            }
        }
    }

    private static string Text<T>(T document) => Encoding.UTF8.GetString(BaselineStore.SerializeDocument(document));

    private static void AssertLinkRefusal(InvalidOperationException refusal, string linkedComponent)
    {
        Assert.Contains(LinkRefusal, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"because '{linkedComponent}' is a symbolic link or reparse point", refusal.Message, StringComparison.Ordinal);
    }

    // A read refusal names the link, and as the refused path the link itself or a path below it.
    private static void AssertReadLinkRefusal(InvalidOperationException refusal, string linkedComponent)
    {
        const string refused = ReadLinkRefusal + ": refused '";
        Assert.StartsWith(refused, refusal.Message, StringComparison.Ordinal);
        var refusedPath = refusal.Message[refused.Length..refusal.Message.IndexOf('\'', refused.Length)];
        Assert.True(
            string.Equals(refusedPath, linkedComponent, StringComparison.Ordinal)
                || refusedPath.StartsWith(linkedComponent + "/", StringComparison.Ordinal),
            $"the refused path {refusedPath} is neither {linkedComponent} nor below it");
        Assert.EndsWith(
            $", because '{linkedComponent}' is a symbolic link or reparse point.",
            refusal.Message,
            StringComparison.Ordinal);
    }

    // The fixture file each read-containment case reads: the getPhoto record or its body, which sit in
    // records/soap, or the single file of any other document.
    private static string ReadFile(string document) => document switch
    {
        "record" => PhotoRecordPath,
        "body-file" => PhotoBodyPath,
        _ => Writer(document).File,
    };

    // Every regular file below a directory, as "relative/path=hex" in ordinal order.
    private static List<string> Snapshot(string directory) =>
    [
        .. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/')
                + "=" + Convert.ToHexStringLower(File.ReadAllBytes(file)))
            .Order(StringComparer.Ordinal),
    ];

    private static bool IsDirectlyIn(string entry, string directory)
    {
        var path = entry[..entry.IndexOf('=', StringComparison.Ordinal)];
        return path.StartsWith(directory + "/", StringComparison.Ordinal)
            && path.IndexOf('/', directory.Length + 1) < 0;
    }

    // .NET has no hard-link API, so ln(1) creates it, through the bounded tool runner the replay tests use.
    private static void CreateHardLink(string link, string existingFile) =>
        Assert.True(SpecialFileSupport.Run("ln", existingFile, link) == 0, "ln must create the hard link.");

    private string Full(string relative) => Path.Combine(StoreRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    // Paths are asserted relative to the temporary root, so a failure reads the same in every run.
    private string Relative(string path) => Path.GetRelativePath(StoreRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    private string LinkTargetOf(string relative)
    {
        var target = new FileInfo(Full(relative)).LinkTarget;
        Assert.NotNull(target);
        return Relative(target);
    }

    private Task<byte[]> ReadBytes(string relative) => File.ReadAllBytesAsync(Full(relative), TestCancellation);

    private async Task WriteRaw(string relative, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(relative))!);
        await File.WriteAllBytesAsync(Full(relative), bytes, TestCancellation);
    }

    private void CreateProtectedDirectory()
    {
        Directory.CreateDirectory(ProtectedDirectory);
        File.WriteAllBytes(Path.Combine(ProtectedDirectory, SentinelName), SentinelBytes);
    }

    // The setup is a real escape route: the sentinel is visible through the linked directory.
    private void AssertLinkReachesProtectedDirectory(string link) =>
        Assert.True(File.Exists(Path.Combine(Full(link), SentinelName)));

    private static Task Synchronously(Action operation)
    {
        operation();
        return Task.CompletedTask;
    }

    // The exception an operation ends with, or null when it succeeds.
    private static async Task<Exception?> Outcome(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // An operation racing a parent swap succeeds, or meets the swapped-in link and is refused, or finds the
    // entry it reached changed under it and fails with an I/O error.
    private static void AssertSucceededOrRefused(Exception? outcome)
    {
        if (outcome is InvalidOperationException refusal)
        {
            Assert.Contains(LinkRefusal, refusal.Message, StringComparison.Ordinal);
        }
        else if (outcome is not null)
        {
            // The exception's text can name the temporary root, so only its type is shown.
            Assert.True(
                outcome is IOException,
                $"The race ended with {outcome.GetType().FullName}, which is neither a link refusal nor an I/O error.");
        }
    }

    // Until stop, moves each swapped directory aside, links its path to the protected directory, removes that
    // link and moves the directory back. A step the store's own work rules out, such as linking a path where
    // the store has just recreated the directory, is skipped and leaves the directory aside inside baseline/.
    // No step reaches into the protected directory. Returns the first other failure, or null.
    private Exception? SwapForLinks(CancellationToken stop)
    {
        try
        {
            for (var cycle = 0; !stop.IsCancellationRequested; cycle++)
            {
                foreach (var directory in SwappedDirectories)
                {
                    var path = Full(directory);
                    var aside = $"{path}.aside-{cycle}";
                    if (!Attempt(() => Directory.Move(path, aside)))
                    {
                        continue;
                    }

                    if (Attempt(() => Directory.CreateSymbolicLink(path, ProtectedDirectory)))
                    {
                        File.Delete(path);
                    }

                    Attempt(() => Directory.Move(aside, path));
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    // Whether a swap step succeeded; it fails only with the I/O error of a path the store changed first.
    private static bool Attempt(Action step)
    {
        try
        {
            step();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // A link the swapper left behind is removed, so the store's final write and the cleanup see directories.
    private void RemoveSwappedInLinks()
    {
        foreach (var directory in SwappedDirectories)
        {
            var path = Full(directory);
            if (new FileInfo(path).LinkTarget is not null)
            {
                File.Delete(path);
            }
        }
    }

    private BaselineManifest? ReadManifest() => Store.TryReadManifest(out var manifest) ? manifest : null;

    // What a reader returns, as record keys, so the record readers and the record index are held to one order
    // and one set of refusals.
    private async Task<IReadOnlyList<BaselineRecordKey>> ReadWith(string reader) => reader switch
    {
        "ReadRecords" => Keys(Store.ReadRecords()),
        "ReadRecordsAsync" => Keys(await Store.ReadRecordsAsync(TestCancellation)),
        "ReadEntries" => Keys(Store.ReadEntries().Select(static entry => entry.Record)),
        "ReadEntriesAsync" => Keys((await Store.ReadEntriesAsync(TestCancellation)).Select(static entry => entry.Record)),
        RecordIndexReader => await Store.ReadRecordIndexAsync(TestCancellation),
        _ => throw new ArgumentOutOfRangeException(nameof(reader), reader, null),
    };

    // What any reader of the fixture returns; a reader of one case reads the getPhoto record or its key.
    private async Task<object?> ReadThrough(string reader) => reader switch
    {
        "TryReadManifest" => ReadManifest(),
        "TryReadManifestAsync" => await Store.TryReadManifestAsync(TestCancellation),
        "ReadNormalisationRules" => Store.ReadNormalisationRules(),
        "ReadNormalisationRulesAsync" => await Store.ReadNormalisationRulesAsync(TestCancellation),
        "TryReadConformance" => Store.TryReadConformance(),
        "TryReadConformanceAsync" => await Store.TryReadConformanceAsync(TestCancellation),
        "ReadWsdlBytes" => Store.ReadWsdlBytes(),
        "ReadWsdlBytesAsync" => await Store.ReadWsdlBytesAsync(TestCancellation),
        "WsdlExists" => Store.WsdlExists,
        "ReadEntryAsync" => await Store.ReadEntryAsync(KeyOf(PhotoRecord), TestCancellation),
        "ReadBodyBytes" => Store.ReadBodyBytes(PhotoRecord),
        "ReadBodyBytesAsync" => await Store.ReadBodyBytesAsync(PhotoRecord, TestCancellation),
        _ => await ReadWith(reader),
    };

    private static BaselineRecordKey KeyOf(BaselineRecord record) => new(record.Order, record.CaseId, record.Operation);

    private static List<BaselineRecordKey> Keys(IEnumerable<BaselineRecord> records) => [.. records.Select(KeyOf)];

    // Four records out of replay order across both surfaces: JSON text, a body file, JSON text and SOAP text.
    private void WriteCasesAcrossSurfaces()
    {
        Store.WriteRecord(NewRecord(3, "REST-getClient-01", "Rest.getClient", AgentBody), AgentBody);
        Store.WriteRecord(
            NewRecord(1, "SOAP-getPhoto-01", "Soap.getPhoto", PhotoBody, "001-getPhoto-01.body.bin"), PhotoBody);
        Store.WriteRecord(NewRecord(2, "REST-getAgent-02", "Rest.getAgent", AgentBody), AgentBody);
        Store.WriteRecord(NewRecord(4, "SOAP-getAgentNames-01", "Soap.getAgentNames", SoapBody), SoapBody);
    }

    // A JSON body of exactly LargeBodySize bytes whose content differs for every order.
    private static byte[] LargeJsonBody(int order)
    {
        var prefix = Encoding.UTF8.GetBytes($"{{\"order\":{order},\"fill\":\"");
        var suffix = "\"}"u8;
        var body = new byte[LargeBodySize];
        prefix.CopyTo(body, 0);
        body.AsSpan(prefix.Length, LargeBodySize - prefix.Length - suffix.Length).Fill((byte)('a' + order));
        suffix.CopyTo(body.AsSpan(LargeBodySize - suffix.Length));
        return body;
    }

    // Changes or removes the REST record of the fixture after it was indexed, keeping its file name.
    private async Task ChangeAgentRecord(string change)
    {
        switch (change)
        {
            case "deleted":
                File.Delete(Full(RestRecordPath));
                break;
            case "other-position":
                await EditRecord(RestRecordPath, static json => json["order"] = 3);
                break;
            case "other-case":
                await EditRecord(RestRecordPath, static json => json["caseId"] = "REST-getAgent-02");
                break;
            case "other-operation":
                await EditRecord(RestRecordPath, static json => json["operation"] = "REST.getAgent");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }
    }

    private async Task Damage(string damage)
    {
        switch (damage)
        {
            case "tampered-body-file":
                var photo = await ReadBytes(PhotoBodyPath);
                photo[^1] ^= 0x01;
                await WriteRaw(PhotoBodyPath, photo);
                break;
            case "tampered-body-text":
                await EditRecord(
                    RestRecordPath, static json => json["response"]!["bodyText"] = "{\"name\":\"Hank Williams\"}");
                break;
            case "missing-body-file":
                File.Delete(Full(PhotoBodyPath));
                break;
            case "malformed-json":
                await WriteRaw(RestRecordPath, "{\"order\":1,"u8.ToArray());
                break;
            case "invalid-utf8-body-text":
                await WriteEscapedBodyTextTail(RestRecordPath, [0xFF, 0xFE]);
                break;
            case "lone-surrogate-body-text":
                await WriteEscapedBodyTextTail(RestRecordPath, "\\uD800"u8.ToArray());
                break;
            case "json-null":
                await WriteRaw(RestRecordPath, "null"u8.ToArray());
                break;
            case "missing-case-id":
                await EditRecord(RestRecordPath, static json => json.Remove("caseId"));
                break;
            case "missing-request-headers":
                await EditRecord(RestRecordPath, static json => json["request"]!.AsObject().Remove("headers"));
                break;
            case "missing-body-sha":
                await EditRecord(PhotoRecordPath, static json => json["response"]!.AsObject().Remove("bodySha256"));
                break;
            case "wrong-surface-directory":
                File.Move(Full(RestRecordPath), Full("baseline/records/soap/001-getAgent-01.json"));
                break;
            case "wrong-file-name":
                File.Move(Full(RestRecordPath), Full("baseline/records/rest/007-getAgent-01.json"));
                break;
            case "duplicate-order":
                Store.WriteRecord(NewRecord(1, "REST-getClient-01", "Rest.getClient", AgentBody), AgentBody);
                break;
            case "duplicate-case-id":
                Store.WriteRecord(NewRecord(3, "REST-getAgent-01", "Rest.getAgent", AgentBody), AgentBody);
                break;
            case "text-and-file":
                await EditRecord(PhotoRecordPath, static json => json["response"]!["bodyText"] = "photo");
                break;
            case "foreign-body-file":
                await EditRecord(
                    PhotoRecordPath, static json => json["response"]!["bodyFile"] = "002-getPhoto-03.body.bin");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, null);
        }
    }

    // The record ReadBodyBytes is given after the damage: the written record when the damage is on disk,
    // otherwise an altered copy of it.
    private async Task<BaselineRecord> DamagedBodyRecord(string damage)
    {
        switch (damage)
        {
            case "tampered-body-file":
            case "missing-body-file":
                await Damage(damage);
                return PhotoRecord;
            case "tampered-body-text":
                return AgentRecord with { Response = AgentRecord.Response with { BodyText = "{}" } };
            case "text-and-file":
                return PhotoRecord with { Response = PhotoRecord.Response with { BodyText = "photo" } };
            case "foreign-body-file":
                return PhotoRecord with
                {
                    Response = PhotoRecord.Response with { BodyFile = "002-getPhoto-03.body.bin" },
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, null);
        }
    }

    // Gives a record a response bodyText that starts with an escaped quote, so it is read through the unescaping
    // path, and ends in tail, raw bytes no JSON writer produces: invalid UTF-8 or a lone surrogate escape.
    private async Task WriteEscapedBodyTextTail(string relative, byte[] tail)
    {
        const string marker = "TAIL";
        await EditRecord(relative, static json => json["response"]!["bodyText"] = "\"" + marker);
        var bytes = await ReadBytes(relative);
        var at = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker));
        Assert.True(at > 0, "the marker is in the record");
        await WriteRaw(relative, [.. bytes.AsSpan(0, at), .. tail, .. bytes.AsSpan(at + marker.Length)]);
    }

    private async Task EditRecord(string relative, Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(await ReadBytes(relative))!.AsObject();
        edit(json);
        await WriteRaw(relative, BaselineStore.SerializeDocument(json));
    }

    private Task CancellableCall(string api, CancellationToken cancellationToken)
    {
        switch (api)
        {
            case "WriteRecordAsync":
                return Store.WriteRecordAsync(PhotoRecord, PhotoBody, cancellationToken);
            case "WriteManifestAsync":
                return Store.WriteManifestAsync(Manifest, cancellationToken);
            case "WriteNormalisationRulesAsync":
                return Store.WriteNormalisationRulesAsync(RulesNode(), cancellationToken);
            case "WriteConformanceAsync":
                return Store.WriteConformanceAsync(ConformanceNode(), cancellationToken);
            case "WriteWsdlAsync":
                return Store.WriteWsdlAsync(WsdlBytes, cancellationToken);
            case "TryReadManifestAsync":
                return Store.TryReadManifestAsync(cancellationToken);
            case "ReadRecordsAsync":
                return Store.ReadRecordsAsync(cancellationToken);
            case "ReadEntriesAsync":
                return Store.ReadEntriesAsync(cancellationToken);
            case RecordIndexReader:
                return Store.ReadRecordIndexAsync(cancellationToken);
            case "ReadEntryAsync":
                return Store.ReadEntryAsync(KeyOf(PhotoRecord), cancellationToken);
            case "ReadBodyBytesAsync:file":
                return Store.ReadBodyBytesAsync(PhotoRecord, cancellationToken);
            case "ReadBodyBytesAsync:text":
                return Store.ReadBodyBytesAsync(AgentRecord, cancellationToken);
            case "ReadNormalisationRulesAsync":
                return Store.ReadNormalisationRulesAsync(cancellationToken);
            case "TryReadConformanceAsync":
                return Store.TryReadConformanceAsync(cancellationToken);
            case "ReadWsdlBytesAsync":
                return Store.ReadWsdlBytesAsync(cancellationToken);
            case "ClearRecords":
                Store.ClearRecords(cancellationToken);
                return Task.CompletedTask;
            case "NormalisationRules.LoadAsync":
                return NormalisationRules.LoadAsync(StoreOptions.NormalisationRulesPath, cancellationToken);
            default:
                throw new ArgumentOutOfRangeException(nameof(api), api, null);
        }
    }

    private sealed record DocumentWriter(
        string File, Action<BaselineStore> Write, Func<BaselineStore, CancellationToken, Task> WriteAsync);
}

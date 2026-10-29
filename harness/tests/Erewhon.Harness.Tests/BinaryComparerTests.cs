using System.Globalization;
using System.Text;
using Erewhon.Contracts.Catalogue;
using Erewhon.Harness.Compare;

namespace Erewhon.Harness.Tests;

/// <summary>
/// Byte-exact comparison of raw <c>getPhoto</c> response bodies: no normalisation rule, no
/// canonicalisation and no tolerance applies, so any changed, missing or extra byte is reported. The
/// response-level cases show that <see cref="ResponseComparer"/> sends <c>getPhoto</c>, and only it,
/// to this comparer on the raw bytes, byte-order mark included.
/// </summary>
public sealed class BinaryComparerTests
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

    [Fact]
    public void TwoChangedBytes_ReportFirstDifferingOffset()
    {
        var observed = Jpeg();
        observed[3] = 0xE1;
        observed[7] = 0x00;

        var discrepancy = Assert.Single(BinaryComparer.Compare(Jpeg(), observed));

        Assert.Equal(
            new Discrepancy("$body[3]", DiscrepancyKinds.Value, DiscrepancyValue.Literal("e0"), DiscrepancyValue.Literal("e1")),
            discrepancy);
    }

    // BinaryComparer documents that a body which is a prefix of the other yields the length
    // discrepancy only, so a single entry is asserted.
    [Theory]
    [InlineData(2)]
    [InlineData(JpegLength)]
    public void Truncation_GivesLengthDiscrepancyAtBody(int removedBytes)
    {
        var baseline = Jpeg();

        var discrepancy = Assert.Single(BinaryComparer.Compare(baseline, baseline.AsSpan(0, JpegLength - removedBytes)));

        Assert.Equal(
            new Discrepancy(
                "$body",
                DiscrepancyKinds.Length,
                DiscrepancyValue.Literal(JpegLength.ToString(CultureInfo.InvariantCulture)),
                DiscrepancyValue.Literal((JpegLength - removedBytes).ToString(CultureInfo.InvariantCulture))),
            discrepancy);
    }

    [Fact]
    public void Extension_GivesLengthDiscrepancyAtBody()
    {
        byte[] observed = [.. Jpeg(), 0x00];

        var discrepancy = Assert.Single(BinaryComparer.Compare(Jpeg(), observed));

        Assert.Equal(
            new Discrepancy("$body", DiscrepancyKinds.Length, DiscrepancyValue.Literal("22"), DiscrepancyValue.Literal("23")),
            discrepancy);
    }

    [Fact]
    public void LengthAndByteDifference_ReportBoth()
    {
        var observed = Jpeg().AsSpan(0, JpegLength - 1).ToArray();
        observed[2] = 0x00;

        var discrepancies = BinaryComparer.Compare(Jpeg(), observed);

        Assert.Equal(2, discrepancies.Count);
        Assert.Contains(
            new Discrepancy("$body", DiscrepancyKinds.Length, DiscrepancyValue.Literal("22"), DiscrepancyValue.Literal("21")),
            discrepancies);
        Assert.Contains(
            new Discrepancy("$body[2]", DiscrepancyKinds.Value, DiscrepancyValue.Literal("ff"), DiscrepancyValue.Literal("00")),
            discrepancies);
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
    // still be reported different.
    [Theory]
    [InlineData("indentation")]
    [InlineData("namespace-declaration-order")]
    [InlineData("ordinary-attribute-order")]
    public void XmlFormattingDifference_IsNotCanonicalised(string variant)
    {
        var (baseline, observed, expected) = variant switch
        {
            "indentation" => (CompactPhotoEnvelope, IndentedPhotoEnvelope, new[]
            {
                new Discrepancy("$body", DiscrepancyKinds.Length, DiscrepancyValue.Literal("407"), DiscrepancyValue.Literal("447")),
                new Discrepancy("$body[207]", DiscrepancyKinds.Value, DiscrepancyValue.Literal("3c"), DiscrepancyValue.Literal("0a")),
            }),
            "namespace-declaration-order" => (CompactPhotoEnvelope, SwappedAttributesPhotoEnvelope, new[]
            {
                new Discrepancy("$body[116]", DiscrepancyKinds.Value, DiscrepancyValue.Literal("69"), DiscrepancyValue.Literal("64")),
            }),
            "ordinary-attribute-order" => (TypedPhotoEnvelope, ReorderedTypedPhotoEnvelope, new[]
            {
                new Discrepancy("$body[307]", DiscrepancyKinds.Value, DiscrepancyValue.Literal("74"), DiscrepancyValue.Literal("6e")),
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown XML formatting variant."),
        };

        var discrepancies = BinaryComparer.Compare(
            Encoding.UTF8.GetBytes(baseline), Encoding.UTF8.GetBytes(observed));

        Assert.Equal(expected.Length, discrepancies.Count);
        foreach (var discrepancy in expected)
        {
            Assert.Contains(discrepancy, discrepancies);
        }
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

        AssertResponse(
            _responses.Compare(GetPhoto, SoapSnapshot(compact), SoapSnapshot(indented)),
            [
                new("$body", DiscrepancyKinds.Length, CountLiteral(compact.Length), CountLiteral(indented.Length)),
                new(
                    string.Create(CultureInfo.InvariantCulture, $"$body[{firstDifference}]"),
                    DiscrepancyKinds.Value,
                    DiscrepancyValue.Literal("3c"),
                    DiscrepancyValue.Literal("0a")),
            ],
            wellFormed: true,
            statusMatched: true);
        AssertResponse(_responses.Compare(GetAgent, SoapSnapshot(compact), SoapSnapshot(indented)), [], wellFormed: true, statusMatched: true);
    }

    // A getPhoto body keeps its byte-order mark as an ordinary leading byte; an XML-compared body drops it.
    [Fact]
    public void GetPhoto_ByteOrderMark_IsNotStripped()
    {
        var plain = Encoding.UTF8.GetBytes(NilResultEnvelope(GetPhoto, indent: false));
        byte[] marked = [0xEF, 0xBB, 0xBF, .. plain];

        AssertResponse(
            _responses.Compare(GetPhoto, SoapSnapshot(plain), SoapSnapshot(marked)),
            [
                new("$body", DiscrepancyKinds.Length, CountLiteral(plain.Length), CountLiteral(marked.Length)),
                new("$body[0]", DiscrepancyKinds.Value, DiscrepancyValue.Literal("3c"), DiscrepancyValue.Literal("ef")),
            ],
            wellFormed: true,
            statusMatched: true);
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

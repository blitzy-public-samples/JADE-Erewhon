using System.Globalization;
using Erewhon.Harness.Compare;

namespace Erewhon.Harness.Tests;

/// <summary>
/// Byte-exact comparison of raw <c>getPhoto</c> response bodies: no normalisation rule, no
/// canonicalisation and no tolerance applies, so any changed, missing or extra byte is reported.
/// </summary>
public sealed class BinaryComparerTests
{
    private const int JpegLength = 22;

    private const int JfifMarkerOffset = 6;

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

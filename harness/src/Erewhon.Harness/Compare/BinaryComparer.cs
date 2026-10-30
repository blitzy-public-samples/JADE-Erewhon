using System.Globalization;

namespace Erewhon.Harness.Compare;

/// <summary>
/// Compares two raw response bodies byte for byte, as the SOAP <c>getPhoto</c> cases require: the
/// observed bytes against the recorded <c>.body.bin</c> file, with no normalisation rule, no
/// canonicalisation, no decoding and no byte-order-mark handling.
/// </summary>
/// <remarks>
/// Every differing byte is reported, so the discrepancy list holds the complete evidence of the two
/// bodies: together with the baseline it determines the observed body exactly, and the reverse. Two
/// observations that share their first change but differ later therefore give different lists and
/// different failure signatures. Because the list depends only on the bytes, the same pair of bodies
/// always yields the same signature. The class holds no state, so concurrent calls are safe.
/// </remarks>
public static class BinaryComparer
{
    private const string BodyPath = "$body";

    /// <summary>Compares <paramref name="observed"/> with <paramref name="baseline"/> byte for byte.</summary>
    /// <param name="baseline">The recorded body bytes.</param>
    /// <param name="observed">The body bytes the replay received.</param>
    /// <returns>
    /// An empty list when the bodies are identical. Otherwise, in this order: a <c>length</c> discrepancy
    /// at <c>$body</c> holding both byte counts in invariant decimal when they differ; one <c>value</c>
    /// discrepancy per maximal run of differing bytes within the shorter body's length, in offset order, at
    /// <c>$body[&lt;offset of the run's first byte&gt;]</c> and holding each side's bytes of the run as
    /// lowercase hex, two digits per byte; and, when the lengths differ, one discrepancy for the bytes past
    /// the shorter body's end at <c>$body[&lt;shorter length&gt;]</c>, of kind <c>missing</c> with the
    /// baseline's remaining bytes as hex when the baseline is longer, or of kind <c>extra</c> with the
    /// observed body's remaining bytes as hex when the observed body is longer, the other side absent.
    /// </returns>
    public static IReadOnlyList<Discrepancy> Compare(ReadOnlySpan<byte> baseline, ReadOnlySpan<byte> observed)
    {
        if (baseline.SequenceEqual(observed))
        {
            return Array.Empty<Discrepancy>();
        }

        var discrepancies = new List<Discrepancy>();

        if (baseline.Length != observed.Length)
        {
            discrepancies.Add(new Discrepancy(
                BodyPath,
                DiscrepancyKinds.Length,
                DiscrepancyValue.Literal(baseline.Length.ToString(CultureInfo.InvariantCulture)),
                DiscrepancyValue.Literal(observed.Length.ToString(CultureInfo.InvariantCulture))));
        }

        var common = Math.Min(baseline.Length, observed.Length);
        var offset = 0;
        while (true)
        {
            // Equal bytes are skipped with the vectorised prefix search; a run then extends to the next
            // equal byte or to the end of the shorter body, so each run is maximal.
            offset += baseline[offset..common].CommonPrefixLength(observed[offset..common]);
            if (offset == common)
            {
                break;
            }

            var start = offset;
            while (offset < common && baseline[offset] != observed[offset])
            {
                offset++;
            }

            discrepancies.Add(new Discrepancy(
                OffsetPath(start),
                DiscrepancyKinds.Value,
                Hex(baseline[start..offset]),
                Hex(observed[start..offset])));
        }

        if (baseline.Length > common)
        {
            discrepancies.Add(new Discrepancy(OffsetPath(common), DiscrepancyKinds.Missing, Hex(baseline[common..]), DiscrepancyValue.Absent));
        }
        else if (observed.Length > common)
        {
            discrepancies.Add(new Discrepancy(OffsetPath(common), DiscrepancyKinds.Extra, DiscrepancyValue.Absent, Hex(observed[common..])));
        }

        return discrepancies.AsReadOnly();
    }

    private static string OffsetPath(int offset) =>
        string.Create(CultureInfo.InvariantCulture, $"{BodyPath}[{offset}]");

    private static DiscrepancyValue Hex(ReadOnlySpan<byte> bytes) =>
        DiscrepancyValue.Literal(Convert.ToHexStringLower(bytes));
}

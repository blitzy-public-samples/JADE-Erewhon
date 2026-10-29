using System.Globalization;

namespace Erewhon.Harness.Compare;

/// <summary>
/// Compares two raw response bodies byte for byte, as the SOAP <c>getPhoto</c> cases require: the
/// observed bytes against the recorded <c>.body.bin</c> file, with no normalisation rule, no
/// canonicalisation, no decoding and no byte-order-mark handling.
/// </summary>
/// <remarks>
/// At most two discrepancies are reported: <c>$body</c> of kind <c>length</c> when the byte counts
/// differ, and <c>$body[&lt;offset&gt;]</c> of kind <c>value</c> for the first differing byte within the
/// shorter body. Reporting only the first differing offset keeps the failure signature small, and
/// because it depends only on the bytes, the same pair of bodies always yields the same signature.
/// The class holds no state, so concurrent calls are safe.
/// </remarks>
public static class BinaryComparer
{
    private const string BodyPath = "$body";

    /// <summary>Compares <paramref name="observed"/> with <paramref name="baseline"/> byte for byte.</summary>
    /// <param name="baseline">The recorded body bytes.</param>
    /// <param name="observed">The body bytes the replay received.</param>
    /// <returns>
    /// An empty list when the bodies are identical. Otherwise a <c>length</c> discrepancy at <c>$body</c>
    /// holding both byte counts in invariant decimal when they differ, and a <c>value</c> discrepancy at
    /// <c>$body[&lt;offset&gt;]</c> holding both bytes as two lowercase hex digits when the bodies differ
    /// before the shorter one ends. A body that is a prefix of the other yields the <c>length</c>
    /// discrepancy only.
    /// </returns>
    public static IReadOnlyList<Discrepancy> Compare(ReadOnlySpan<byte> baseline, ReadOnlySpan<byte> observed)
    {
        if (baseline.SequenceEqual(observed))
        {
            return Array.Empty<Discrepancy>();
        }

        var discrepancies = new List<Discrepancy>(2);

        if (baseline.Length != observed.Length)
        {
            discrepancies.Add(new Discrepancy(
                BodyPath,
                DiscrepancyKinds.Length,
                DiscrepancyValue.Literal(baseline.Length.ToString(CultureInfo.InvariantCulture)),
                DiscrepancyValue.Literal(observed.Length.ToString(CultureInfo.InvariantCulture))));
        }

        var common = baseline.CommonPrefixLength(observed);
        if (common < Math.Min(baseline.Length, observed.Length))
        {
            discrepancies.Add(new Discrepancy(
                string.Create(CultureInfo.InvariantCulture, $"{BodyPath}[{common}]"),
                DiscrepancyKinds.Value,
                DiscrepancyValue.Literal(baseline[common].ToString("x2", CultureInfo.InvariantCulture)),
                DiscrepancyValue.Literal(observed[common].ToString("x2", CultureInfo.InvariantCulture))));
        }

        return discrepancies.AsReadOnly();
    }
}

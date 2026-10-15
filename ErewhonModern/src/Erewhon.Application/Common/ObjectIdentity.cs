using System.Globalization;

namespace Erewhon.Application.Common;

/// <summary>
/// An object that has OID text: a class number and a row id.
/// </summary>
public interface IJadeIdentifiable
{
    /// <summary>The class number of the object's own class, one of the <see cref="ObjectIdentity"/> constants.</summary>
    int JadeClassNumber { get; }

    /// <summary>The row id; 0 while the object is unsaved.</summary>
    long Id { get; }
}

/// <summary>
/// Builds OID text in the form <c>&lt;class number&gt;.&lt;id&gt;</c>, e.g. <c>5774.3</c> for the agent with id 3.
/// </summary>
public static class ObjectIdentity
{
    /// <summary>Class number of <c>Address</c>.</summary>
    public const int Address = 5773;

    /// <summary>Class number of <c>Agent</c>.</summary>
    public const int Agent = 5774;

    /// <summary>Class number of <c>Client</c>.</summary>
    public const int Client = 5776;

    /// <summary>Class number of <c>CommissionRate</c>.</summary>
    public const int CommissionRate = 5777;

    /// <summary>Class number of <c>Company</c>.</summary>
    public const int Company = 5778;

    /// <summary>Class number of <c>Location</c>.</summary>
    public const int Location = 5779;

    /// <summary>Class number of <c>Country</c>.</summary>
    public const int Country = 5780;

    /// <summary>Class number of <c>Region</c>.</summary>
    public const int Region = 5781;

    /// <summary>Class number of <c>Sale</c>.</summary>
    public const int Sale = 5782;

    /// <summary>Class number of <c>RetailSale</c>.</summary>
    public const int RetailSale = 5783;

    /// <summary>Class number of <c>TenderSale</c>.</summary>
    public const int TenderSale = 5784;

    /// <summary>Class number of <c>Item</c>.</summary>
    public const int Item = 5785;

    /// <summary>Class number of <c>RetailItem</c>.</summary>
    public const int RetailItem = 5786;

    /// <summary>Class number of <c>TenderItem</c>.</summary>
    public const int TenderItem = 5787;

    /// <summary>Class number of <c>ItemCategory</c>.</summary>
    public const int ItemCategory = 5788;

    /// <summary>Class number of <c>Tender</c>.</summary>
    public const int Tender = 5789;

    /// <summary>
    /// Returns <c>&lt;classNumber&gt;.&lt;id&gt;</c> in invariant digits with no grouping or padding, e.g. <c>5774.3</c>.
    /// </summary>
    /// <param name="classNumber">The class number.</param>
    /// <param name="id">The row id.</param>
    /// <returns>The OID text.</returns>
    public static string Format(int classNumber, long id) =>
        string.Create(CultureInfo.InvariantCulture, $"{classNumber}.{id}");

    /// <summary>
    /// Returns the OID text of <paramref name="entity"/>, e.g. <c>5774.3</c> for the agent with id 3
    /// and <c>5781.12</c> for the region with id 12.
    /// </summary>
    /// <param name="entity">The object whose OID text is returned.</param>
    /// <returns>The OID text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is null.</exception>
    public static string Of(IJadeIdentifiable entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return Format(entity.JadeClassNumber, entity.Id);
    }

    /// <summary>
    /// Returns the OID text of the address owned by the row <paramref name="ownerId"/>,
    /// i.e. <c>5773.&lt;ownerId&gt;</c>, e.g. <c>5773.7</c>.
    /// </summary>
    /// <param name="ownerId">The row id of the address's owner.</param>
    /// <returns>The OID text.</returns>
    public static string OfAddress(long ownerId) => Format(Address, ownerId);
}

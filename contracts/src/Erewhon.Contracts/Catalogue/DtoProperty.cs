using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace Erewhon.Contracts.Catalogue;

/// <summary>
/// How a DTO property is declared in Erewhon/ErehwhonExternalInterfacesSchema.scm. The catalogue
/// spells the values <c>attribute</c>, <c>reference</c> and <c>collection</c>; they are read as
/// strings, case-insensitively, and never as integers, so the members' numeric values carry no
/// meaning.
/// </summary>
public enum PropertyKind
{
    /// <summary>
    /// A primitive attribute from <c>attributeDefinitions</c>, whose <see cref="DtoProperty.JadeType"/>
    /// is a catalogue wire type: <c>String</c>, <c>Integer</c>, <c>Decimal</c>, <c>Date</c>,
    /// <c>TimeStamp</c> or <c>Binary</c>, for example <c>guid: String[39]</c> or
    /// <c>price: Decimal[12,2]</c>.
    /// </summary>
    Attribute,

    /// <summary>
    /// A single-valued reference from <c>referenceDefinitions</c>, whose
    /// <see cref="DtoProperty.JadeType"/> names a catalogue DTO, for example
    /// <c>DTOItem.agent: DTOAgent</c> or <c>DTOItem.sale: DTOSaleDetail</c>.
    /// </summary>
    Reference,

    /// <summary>
    /// A collection-valued property, whose <see cref="DtoProperty.JadeType"/> names a catalogue array:
    /// the <c>StringArray</c> members <c>DTOError.allErrors</c> and <c>DTOResult.allErrors</c> and
    /// <c>allWarnings</c>. The schema lists them under <c>attributeDefinitions</c> as exclusive
    /// collections (<c>subId</c>); the catalogue classes them by their array wire shape, a JSON array
    /// on REST and an <c>ArrayOfString</c> of <c>String</c> items on SOAP.
    /// </summary>
    Collection,
}

/// <summary>
/// One property of a catalogue DTO, as declared in Erewhon/ErehwhonExternalInterfacesSchema.scm:98-268.
/// A DTO's property list is flattened, so it carries every inherited property with the class that
/// declares it. Each positional parameter binds to the catalogue key of the same name in camelCase.
/// </summary>
/// <remarks>
/// The catalogue writes every key of a property, and both keys of its reachability and evidence
/// objects, in every entry, with <c>null</c> where a value does not apply. No positional parameter in
/// this file therefore has a default value: under the loader's strict options a dropped key fails the
/// load instead of reading as <c>null</c>.
/// </remarks>
/// <param name="Name">The JADE property name exactly as declared, for example <c>guid</c> or <c>listedDate</c>.</param>
/// <param name="DeclaredIn">
/// The DTO class that declares the property: the owning DTO itself or one of its ancestors. The
/// inherited <c>guid</c> carries <c>DTOObject</c> on every DTO.
/// </param>
/// <param name="Kind">Whether the property is a primitive attribute, a single reference or a collection.</param>
/// <param name="JadeType">
/// The declared type: a catalogue wire-type key for <see cref="PropertyKind.Attribute"/>
/// (<c>String</c>, <c>Decimal</c>, <c>TimeStamp</c>, ...), a DTO name for
/// <see cref="PropertyKind.Reference"/> (<c>DTOAgent</c>, <c>DTOSaleDetail</c>, ...) or an array name
/// for <see cref="PropertyKind.Collection"/> (<c>StringArray</c>).
/// </param>
/// <param name="Length">
/// The declared maximum length of a bounded <c>String</c>, for example 39 for <c>String[39]</c>; null
/// for an unbounded string and for every other type.
/// </param>
/// <param name="Precision">The declared precision of a <c>Decimal</c>, for example 12 for <c>Decimal[12,2]</c>; null otherwise.</param>
/// <param name="Scale">The declared scale of a <c>Decimal</c>, for example 2 for <c>Decimal[12,2]</c>; null otherwise.</param>
/// <param name="SchemaLine">
/// The line of the declaring class's definition of the property in
/// Erewhon/ErehwhonExternalInterfacesSchema.scm, for example 103 for <c>guid</c>.
/// </param>
/// <param name="SoapExposed">
/// True when the SOAP surface emits the property: the <c>WebServiceHTTP</c> exposure list
/// (scm:1793-1932) exposes it, or, for <c>DTOResult</c>, which that list omits, the assumed exposure of
/// KL-SOAP-DTORESULT applies. Always false for <c>guid</c>, which the list never exposes.
/// </param>
/// <param name="SoapExposureSource">
/// The evidence for <paramref name="SoapExposed"/>: the exposure-list file and line, such as
/// <c>Erewhon/ErehwhonExternalInterfacesSchema.scm:1837</c>, or the register entry
/// <c>KL-SOAP-DTORESULT</c> where the exposure is assumed.
/// </param>
/// <param name="XmlType">
/// The XML Schema type of the property's SOAP element, in Clark notation (<c>{uri}local</c>) as parsed
/// by <c>System.Xml.Linq.XName.Get</c>, for example <c>{http://www.w3.org/2001/XMLSchema}string</c>,
/// <c>{urn:JadeWebServices/WebServiceHTTP/}decimal</c>,
/// <c>{urn:JadeWebServices/WebServiceHTTP/}ArrayOfString</c> or
/// <c>{urn:JadeWebServices/WebServiceHTTP/}DTOAgent</c>.
/// </param>
/// <param name="Normalisation">
/// The normalisation rule applied at this position when two bodies are compared,
/// <see cref="NormalisationClasses.N1"/> or <see cref="NormalisationClasses.N2"/>; null when the value
/// is compared exactly.
/// </param>
/// <param name="Reachability">
/// Whether the in-scope method bodies of each surface can emit the property, for the conformance
/// inventory; null only for the entry of the abstract <c>DTOObject</c>, which no body creates.
/// </param>
/// <param name="Evidence">
/// The source lines behind <paramref name="Reachability"/>, per surface; null exactly when
/// <paramref name="Reachability"/> is null.
/// </param>
/// <param name="Note">A short explanation of a non-obvious reachability or exposure fact; null when none is needed.</param>
public sealed record DtoProperty(
    string Name,
    string DeclaredIn,
    PropertyKind Kind,
    string JadeType,
    int? Length,
    int? Precision,
    int? Scale,
    int SchemaLine,
    bool SoapExposed,
    string SoapExposureSource,
    string XmlType,
    string? Normalisation,
    SurfaceReachability? Reachability,
    SurfaceEvidence? Evidence,
    string? Note)
{
    /// <summary>
    /// True when this property declares a normalisation rule (<see cref="Normalisation"/> is not null).
    /// Whether a compared value escapes exact comparison depends on the surface, which may not emit the
    /// property (SOAP never emits <c>guid</c>; see <see cref="SoapExposed"/>), and on pairwise validity:
    /// two values that are both invalid for the rule's form are still compared exactly.
    /// </summary>
    [JsonIgnore]
    public bool IsNormalised => Normalisation is not null;
}

/// <summary>
/// A DTO property's reachability on each surface. Each value is one of the
/// <see cref="ReachabilityValues"/> constants, which <c>OperationCatalogue</c> checks at load.
/// </summary>
/// <param name="Rest">
/// The property's reachability from the REST bodies (scm:2326-2868), <see cref="ReachabilityValues.Reachable"/>
/// or <see cref="ReachabilityValues.UnreachableInSource"/>.
/// </param>
/// <param name="Soap">
/// The property's reachability from the nine public SOAP bodies (scm:6528-6942), or
/// <see cref="ReachabilityValues.NotExposed"/> when the SOAP surface does not emit it.
/// </param>
public sealed record SurfaceReachability(string Rest, string Soap)
{
    /// <summary>Returns the reachability recorded for <paramref name="surface"/>.</summary>
    /// <param name="surface">The surface to read.</param>
    /// <returns><see cref="Rest"/> or <see cref="Soap"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="surface"/> is not a defined <see cref="Surface"/>.</exception>
    public string For(Surface surface) => surface switch
    {
        Surface.Rest => Rest,
        Surface.Soap => Soap,
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown surface."),
    };
}

/// <summary>The source lines behind a DTO property's reachability, per surface.</summary>
/// <param name="Rest">
/// For a reachable property, the file and line or range that assigns it, such as
/// <c>Erewhon/ErehwhonExternalInterfacesSchema.scm:2092</c>; for an unreachable one, the bodies or
/// population lines traced without finding an assignment, such as
/// <c>Erewhon/ErehwhonExternalInterfacesSchema.scm:2326-2868</c>.
/// </param>
/// <param name="Soap">
/// The same for the SOAP surface; null when the property is
/// <see cref="ReachabilityValues.NotExposed"/> there.
/// </param>
public sealed record SurfaceEvidence(string? Rest, string? Soap)
{
    /// <summary>Returns the evidence recorded for <paramref name="surface"/>, which may be null.</summary>
    /// <param name="surface">The surface to read.</param>
    /// <returns><see cref="Rest"/> or <see cref="Soap"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="surface"/> is not a defined <see cref="Surface"/>.</exception>
    public string? For(Surface surface) => surface switch
    {
        Surface.Rest => Rest,
        Surface.Soap => Soap,
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown surface."),
    };
}

/// <summary>
/// The reachability values the catalogue records for DTO properties (per surface) and status
/// constants, for the stand-in conformance check.
/// </summary>
public static class ReachabilityValues
{
    /// <summary>
    /// An in-scope method body of the surface creates an object of the class, directly or through the
    /// population code (scm:2085-2325), and that code assigns the member; for a status constant, such a
    /// body assigns it. Conformance passes the member only when a captured response holds a non-null
    /// value of its declared wire type.
    /// </summary>
    public const string Reachable = "reachable";

    /// <summary>
    /// No in-scope method body of the surface can emit the member, for example <c>DTOError</c>,
    /// <c>DTOTenderSaleDetail.offer</c> and <c>tenderDate</c>, <c>DTOItem.categoryPrefix</c> and
    /// <c>photo</c>, and <c>DTOTenderSale.offer</c>. Such a member is a registered conformance gap and is
    /// never closed by inventing a response path.
    /// </summary>
    public const string UnreachableInSource = "unreachable-in-source";

    /// <summary>
    /// The surface does not emit the member at all, because the SOAP exposure list omits it (for
    /// example <c>guid</c>); it is not part of that surface's conformance inventory.
    /// </summary>
    public const string NotExposed = "not-exposed";

    /// <summary>Every defined reachability value, compared ordinally.</summary>
    public static IReadOnlySet<string> All { get; } =
        new[] { Reachable, UnreachableInSource, NotExposed }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>
/// The names of the only two normalisation rules (baseline/normalisation-rules.json). Each targets
/// typed positions and checks a validity form; neither rewrites a body. At a targeted position two
/// values are equal when both are valid for the rule's form, or when neither is valid and they are
/// exactly equal.
/// </summary>
public static class NormalisationClasses
{
    /// <summary>
    /// Generated identifiers: the <c>guid</c> of <c>DTOAgent</c>, <c>DTOClient</c>, <c>DTOCompany</c>,
    /// <c>DTOSale</c> and its subclasses, <c>DTOSaleDetail</c> and its subclasses, <c>DTOTender</c> and
    /// <c>DTOResult</c>. The valid form is the braced 38-character JADE GUID. Item DTO guids come from
    /// the seed files and are compared exactly.
    /// </summary>
    public const string N1 = "N1";

    /// <summary>
    /// Timestamps: every <c>TimeStamp</c> value, namely <c>DTORetailSale.timestamp</c>,
    /// <c>DTOTender.timestamp</c>, <c>DTORetailSaleDetail.soldDate</c> and
    /// <c>DTOTenderSaleDetail.tenderDate</c>. The valid form is <c>\/Date(&lt;integer&gt;[±hhmm])\/</c> in
    /// JSON or an <c>xsd:dateTime</c> in XML. <c>Date</c> values are never normalised.
    /// </summary>
    public const string N2 = "N2";

    /// <summary>Every defined normalisation rule name, compared ordinally.</summary>
    public static IReadOnlySet<string> All { get; } =
        new[] { N1, N2 }.ToFrozenSet(StringComparer.Ordinal);
}

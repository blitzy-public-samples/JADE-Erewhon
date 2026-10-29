using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Erewhon.Contracts.Catalogue;

/// <summary>
/// One DTO class of Erewhon/ErehwhonExternalInterfacesSchema.scm: its type header (scm:8-25), its
/// definition (scm:98-268) and its entry in the <c>WebServiceHTTP</c> SOAP exposure list
/// (scm:1793-1932). The comparers walk bodies by these definitions, the stand-in writes SOAP
/// properties from them and the facade builds its OpenAPI component schemas from them. Each
/// positional parameter binds to the catalogue key of the same name in camelCase.
/// </summary>
/// <remarks>
/// The catalogue writes every key of a DTO in every entry, with <c>null</c> where a value does not
/// apply, so no positional parameter has a default value: under the loader's strict options a dropped
/// key fails the load instead of reading as <c>null</c>.
/// </remarks>
/// <param name="Name">
/// The JADE class name exactly as declared, for example <c>DTOAgent</c> or <c>DTORetailSaleDetail</c>.
/// </param>
/// <param name="Superclass">
/// The declared JADE superclass: a catalogue DTO name such as <c>DTOObject</c>, <c>DTOItem</c>,
/// <c>DTOSale</c> or <c>DTOSaleDetail</c>, or the JADE root class <c>Object</c> for the abstract
/// <c>DTOObject</c>.
/// </param>
/// <param name="Abstract">
/// True when the type header declares the class <c>abstract</c>, which holds only for
/// <c>DTOObject</c>; no response ever carries an instance of an abstract class.
/// </param>
/// <param name="TypeHeaderLine">
/// The line of the class's type header in Erewhon/ErehwhonExternalInterfacesSchema.scm, for example 8
/// for <c>DTOObject</c> and 14 for <c>DTORetailItem</c>.
/// </param>
/// <param name="SchemaLine">
/// The line of the class's <c>completeDefinition</c> in Erewhon/ErehwhonExternalInterfacesSchema.scm,
/// for example 98 for <c>DTOObject</c> and 107 for <c>DTOAgent</c>.
/// </param>
/// <param name="SoapExposed">
/// True when the SOAP surface may emit the class: the <c>WebServiceHTTP</c> exposure list carries it,
/// or, for <c>DTOResult</c>, which that list omits although four SOAP operations return it, the
/// assumed exposure of KL-SOAP-DTORESULT applies.
/// </param>
/// <param name="SoapExposureLine">
/// The line of the class's entry in the <c>WebServiceHTTP</c> exposure list, for example 1795 for
/// <c>DTOAgent</c>; null exactly when the exposure is assumed rather than listed, which is the case
/// for <c>DTOResult</c> only.
/// </param>
/// <param name="Properties">
/// Every property of the class, inherited ones included, in catalogue order. The list is flattened:
/// each entry's <see cref="DtoProperty.DeclaredIn"/> names the declaring class, so <c>guid</c> appears
/// on every DTO with <c>DeclaredIn</c> <c>DTOObject</c>.
/// </param>
public sealed record DtoDefinition(
    string Name,
    string Superclass,
    bool Abstract,
    int TypeHeaderLine,
    int SchemaLine,
    bool SoapExposed,
    int? SoapExposureLine,
    IReadOnlyList<DtoProperty> Properties)
{
    /// <summary>
    /// The properties the SOAP surface writes for this class, in catalogue order: those whose
    /// <see cref="DtoProperty.SoapExposed"/> is true. <c>guid</c> is never among them.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<DtoProperty> SoapProperties => Properties.Where(p => p.SoapExposed);

    /// <summary>
    /// Finds a property of this class, inherited ones included, by its JADE name. The comparison is
    /// ordinal and case-sensitive, because JADE names are case-sensitive: <c>guid</c> matches and
    /// <c>Guid</c> does not.
    /// </summary>
    /// <param name="name">The JADE property name, for example <c>listedDate</c>.</param>
    /// <param name="property">The property when found; otherwise null.</param>
    /// <returns>True when the class has a property of that name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public bool TryGetProperty(string name, [NotNullWhen(true)] out DtoProperty? property)
    {
        ArgumentNullException.ThrowIfNull(name);

        // A linear search: a DTO has at most 13 properties, so an index would cost more than it saves.
        for (var index = 0; index < Properties.Count; index++)
        {
            var candidate = Properties[index];
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                property = candidate;
                return true;
            }
        }

        property = null;
        return false;
    }

    /// <summary>
    /// Returns a property of this class, inherited ones included, by its JADE name, compared ordinally
    /// and case-sensitively as in <see cref="TryGetProperty"/>.
    /// </summary>
    /// <param name="name">The JADE property name, for example <c>listedDate</c>.</param>
    /// <returns>The property.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="KeyNotFoundException">The class has no property of that name.</exception>
    public DtoProperty GetProperty(string name) =>
        TryGetProperty(name, out var property)
            ? property
            : throw new KeyNotFoundException($"DTO '{Name}' has no property '{name}'.");
}

/// <summary>
/// One array type a catalogue DTO or operation uses: a DTO array of
/// Erewhon/ErehwhonExternalInterfacesSchema.scm (type headers scm:61-66, member types scm:68-73), or
/// the JADE built-in <c>StringArray</c> that <c>DTOError.allErrors</c> and <c>DTOResult.allErrors</c>
/// and <c>allWarnings</c> use. The SOAP names come from the in-repo consumer
/// Erewhon/ErewhonWebServiceConsumerSchema.scm:148-150, in namespace
/// <c>urn:JadeWebServices/WebServiceHTTP/</c>. Each positional parameter binds to the catalogue key of
/// the same name in camelCase.
/// </summary>
/// <remarks>
/// The catalogue writes every key of an array in every entry, with <c>null</c> where a value does not
/// apply, so no positional parameter has a default value: under the loader's strict options a dropped
/// key fails the load instead of reading as <c>null</c>.
/// </remarks>
/// <param name="Name">
/// The JADE class name exactly as declared, for example <c>DTOAgentArray</c>, <c>DTONameArray</c> or
/// <c>StringArray</c>.
/// </param>
/// <param name="Superclass">
/// The declared JADE superclass: the built-in <c>ObjectArray</c> for <c>DTOObjectArray</c>, and
/// <c>DTOObjectArray</c> for every other DTO array; null for a built-in array such as
/// <c>StringArray</c>, whose superclass the reference schema does not declare.
/// </param>
/// <param name="ItemType">
/// The member type: a catalogue DTO name for a DTO array (<c>DTOObject</c>, <c>DTOAgent</c>,
/// <c>DTOClient</c>, <c>DTOItem</c>, <c>DTOName</c>, <c>DTOSale</c>) or a catalogue wire type for a
/// primitive array (<c>String</c> for <c>StringArray</c>).
/// </param>
/// <param name="BuiltIn">
/// True for a JADE built-in array such as <c>StringArray</c>, which has no type header or membership
/// definition in the reference schema.
/// </param>
/// <param name="TypeHeaderLine">
/// The line of the array's type header in Erewhon/ErehwhonExternalInterfacesSchema.scm, for example 61
/// for <c>DTOObjectArray</c>; null for a built-in array.
/// </param>
/// <param name="MembershipLine">
/// The line of the array's <c>membershipDefinitions</c> entry (<c>&lt;Array&gt; of &lt;ItemType&gt;</c>)
/// in Erewhon/ErehwhonExternalInterfacesSchema.scm, for example 72 for <c>DTONameArray</c>; null for a
/// built-in array.
/// </param>
/// <param name="SoapExposed">
/// True when the <c>WebServiceHTTP</c> exposure list carries the array, as it does
/// <c>DTOObjectArray</c>, <c>DTONameArray</c> and <c>StringArray</c>; false for the arrays only REST
/// returns.
/// </param>
/// <param name="SoapExposureLine">
/// The line of the array's entry in the <c>WebServiceHTTP</c> exposure list, for example 1857 for
/// <c>DTONameArray</c>; null when <paramref name="SoapExposed"/> is false.
/// </param>
/// <param name="SoapTypeName">
/// The WSDL complex type of the array, for example <c>ArrayOfDTOName</c> or <c>ArrayOfString</c>;
/// null when <paramref name="SoapExposed"/> is false.
/// </param>
/// <param name="SoapItemElement">
/// The name of the repeated item element inside the array, for example <c>DTOName</c> or
/// <c>String</c>; null when <paramref name="SoapExposed"/> is false.
/// </param>
/// <param name="SoapTypeSource">
/// The consumer line that evidences <paramref name="SoapTypeName"/> and
/// <paramref name="SoapItemElement"/>, for example
/// <c>Erewhon/ErewhonWebServiceConsumerSchema.scm:149</c>; null when <paramref name="SoapExposed"/>
/// is false.
/// </param>
public sealed record ArrayDefinition(
    string Name,
    string? Superclass,
    string ItemType,
    bool BuiltIn,
    int? TypeHeaderLine,
    int? MembershipLine,
    bool SoapExposed,
    int? SoapExposureLine,
    string? SoapTypeName,
    string? SoapItemElement,
    string? SoapTypeSource);

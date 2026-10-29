namespace Erewhon.Contracts.Catalogue;

/// <summary>
/// Where a caller carries an operation parameter on the wire. The catalogue spells the values
/// <c>path</c>, <c>body</c> and <c>element</c>; they are read as strings, case-insensitively, and
/// never as integers, so the members' numeric values carry no meaning.
/// </summary>
public enum ParameterLocation
{
    /// <summary>
    /// A REST primitive parameter carried as one URL-encoded path segment, in signature order,
    /// and named by its <c>{name}</c> placeholder in the operation's path template.
    /// </summary>
    Path,

    /// <summary>
    /// The single REST object parameter, sent as the JSON request body: <c>pAgent: DTOAgent</c> of
    /// <c>postCreateAgent</c> and <c>pClient: DTOClient</c> of <c>postCreateClient</c>
    /// (Erewhon/ErehwhonExternalInterfacesSchema.scm:317-318).
    /// </summary>
    Body,

    /// <summary>
    /// A SOAP parameter carried as a child element of the document/literal wrapped request element
    /// <c>&lt;op&gt;</c>, named as declared.
    /// </summary>
    Element,
}

/// <summary>
/// One parameter of a catalogue operation, as declared on <c>ErewhonRestService</c> or
/// <c>ErewhonWebService</c> in Erewhon/ErehwhonExternalInterfacesSchema.scm. Each positional
/// parameter binds to the catalogue key of the same name in camelCase.
/// </summary>
/// <param name="Name">
/// The JADE parameter name as declared, for example <c>pName</c>, <c>pCodePrefix</c>,
/// <c>pCodeNumber</c>, <c>pOffer</c> or <c>pDTOAgent</c>. It is also the path-template placeholder
/// of a <see cref="ParameterLocation.Path"/> parameter and the child element name of an
/// <see cref="ParameterLocation.Element"/> parameter.
/// </param>
/// <param name="JadeType">
/// The declared JADE type, for example <c>String</c>, <c>Integer</c>, <c>Decimal</c>,
/// <c>DTOAgent</c> or <c>DTOClient</c>. It names a catalogue wire type or DTO, which
/// <c>OperationCatalogue</c> checks at load.
/// </param>
/// <param name="Position">
/// The zero-based position in the JADE signature: the first declared parameter is 0.
/// </param>
/// <param name="Location">Where the parameter travels on the wire.</param>
public sealed record ParameterDescriptor(string Name, string JadeType, int Position, ParameterLocation Location);

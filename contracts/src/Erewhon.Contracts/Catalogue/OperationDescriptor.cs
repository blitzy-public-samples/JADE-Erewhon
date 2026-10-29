using System.Text.Json.Serialization;

namespace Erewhon.Contracts.Catalogue;

/// <summary>
/// The JADE service an operation belongs to. The catalogue spells the values <c>Rest</c> and
/// <c>Soap</c>; they are read as strings, case-insensitively, and never as integers, so the members'
/// numeric values carry no meaning.
/// </summary>
public enum Surface
{
    /// <summary>
    /// <c>ErewhonRestService</c>, served in the direct form by the <c>ErewhonREST</c> application on
    /// port 65000 (Erewhon/ErehwhonExternalInterfacesSchema.ddx:82).
    /// </summary>
    Rest,

    /// <summary>
    /// <c>ErewhonWebService</c>, the SOAP 1.1 document/literal service exposed through the
    /// <c>WebServiceHTTP</c> list and served by the <c>WebServiceHTTP</c> application on port 21001
    /// (Erewhon/ErehwhonExternalInterfacesSchema.ddx:158).
    /// </summary>
    Soap,
}

/// <summary>
/// One of the 22 operations carried over from Erewhon/ErehwhonExternalInterfacesSchema.scm: the 13
/// declared on <c>ErewhonRestService</c> (scm:302-327) and the 9 <c>webService</c> methods of
/// <c>ErewhonWebService</c> (scm:1148-1307). Each positional parameter binds to the catalogue key of
/// the same name in camelCase.
/// </summary>
/// <remarks>
/// The catalogue writes every key of an operation, binding and status in every entry, with
/// <c>null</c> where a value does not apply, so no positional parameter in this file has a default
/// value: under the loader's strict options a dropped key fails the load instead of reading as
/// <c>null</c>.
/// </remarks>
/// <param name="Id">
/// The unique operation id <c>&lt;Surface&gt;.&lt;JadeMethod&gt;</c>, for example
/// <c>Rest.getAgent</c> or <c>Soap.getAgent</c>.
/// </param>
/// <param name="Surface">The service that declares the operation.</param>
/// <param name="JadeClass">
/// The declaring JADE class: <c>ErewhonRestService</c> for <see cref="Surface.Rest"/>,
/// <c>ErewhonWebService</c> for <see cref="Surface.Soap"/>.
/// </param>
/// <param name="JadeMethod">The JADE method name exactly as declared, for example <c>getItemByCode</c>.</param>
/// <param name="SchemaLine">
/// The line of the method's declaration in Erewhon/ErehwhonExternalInterfacesSchema.scm, for example
/// 302 for REST <c>getAgent</c> and 1148 for SOAP <c>getAgent</c>.
/// </param>
/// <param name="BodyLines">
/// The inclusive line range of the method body in Erewhon/ErehwhonExternalInterfacesSchema.scm, written
/// <c>first-last</c>, for example <c>2328-2355</c> for REST <c>getAgent</c> and <c>6528-6557</c> for
/// SOAP <c>getAgent</c>.
/// </param>
/// <param name="ReturnType">
/// The declared return type, a catalogue DTO or array name such as <c>DTOAgent</c>,
/// <c>DTOAgentArray</c>, <c>DTOResult</c>, <c>DTOPhoto</c> or <c>DTONameArray</c>.
/// </param>
/// <param name="StateChanging">
/// True for the operations that change backend state: the four REST <c>postCreate*</c> and the four
/// SOAP <c>update*</c> operations.
/// </param>
/// <param name="Parameters">The declared parameters, in signature order.</param>
/// <param name="Rest">The REST address binding; present only for <see cref="Surface.Rest"/>, otherwise null.</param>
/// <param name="Soap">The SOAP message binding; present only for <see cref="Surface.Soap"/>, otherwise null.</param>
/// <param name="Statuses">Every status outcome the method body can produce, with its evidence lines.</param>
public sealed record OperationDescriptor(
    string Id,
    Surface Surface,
    string JadeClass,
    string JadeMethod,
    int SchemaLine,
    string BodyLines,
    string ReturnType,
    bool StateChanging,
    IReadOnlyList<ParameterDescriptor> Parameters,
    RestBinding? Rest,
    SoapBinding? Soap,
    IReadOnlyList<OperationStatus> Statuses)
{
    /// <summary>
    /// The key that names this operation under <c>Routing:Operations:&lt;Surface&gt;</c> in the facade's
    /// routing configuration, which is the JADE method name. The surface-qualified section keeps the
    /// REST and SOAP <c>getAgent</c> and <c>getClient</c> entries distinct.
    /// </summary>
    [JsonIgnore]
    public string RoutingKey => JadeMethod;
}

/// <summary>
/// The REST address of an <c>ErewhonRestService</c> operation in the direct form
/// <c>http://&lt;host&gt;:65000&lt;PathTemplate&gt;</c>: no relay segment, no application query and no
/// query string.
/// </summary>
/// <param name="Verb">The HTTP method, <c>GET</c> or <c>POST</c>.</param>
/// <param name="PathTemplate">
/// The path template, starting with <c>/</c>, for example <c>/agent/{pName}</c>,
/// <c>/itemByCode/{pCodePrefix}/{pCodeNumber}</c> or
/// <c>/createTender/{pClient}/{pCodePrefix}/{pCodeNumber}/{pOffer}</c>. Each <c>{name}</c> placeholder
/// is one URL-encoded path segment named after a <see cref="ParameterLocation.Path"/> parameter, in
/// signature order.
/// </param>
/// <param name="RequestContentType">
/// The request content type of a body-carrying operation, <c>application/json</c> for
/// <c>postCreateAgent</c> and <c>postCreateClient</c>; null for operations with no request body. The
/// catalogue writes the key in every binding, so it is required here even when its value is null.
/// </param>
public sealed record RestBinding(string Verb, string PathTemplate, string? RequestContentType);

/// <summary>
/// The document/literal wrapped message names of an <c>ErewhonWebService</c> operation, posted to
/// <c>/jade/jadehttp.dll?WebServiceHTTP&amp;serviceName=ErewhonWebService&amp;listName=WebServiceHTTP</c>
/// in namespace <c>urn:JadeWebServices/WebServiceHTTP/</c>.
/// </summary>
/// <param name="Action">
/// The <c>SOAPAction</c> value <c>urn:JadeWebServices/WebServiceHTTP/&lt;op&gt;</c>, as recorded by
/// the in-repo consumer <c>HTTP_ErewhonWebService</c>; the provider leaves <c>soapAction</c> blank, so
/// the form is the assumed JADE default (KL-SOAP-ACTIONS).
/// </param>
/// <param name="ActionSource">
/// The evidence for <paramref name="Action"/>, a file and line such as
/// <c>Erewhon/ErewhonWebServiceConsumerSchema.scm:1740</c>.
/// </param>
/// <param name="RequestElement">The request wrapper element, named <c>&lt;op&gt;</c>.</param>
/// <param name="ResponseElement">The response wrapper element, named <c>&lt;op&gt;Response</c>.</param>
/// <param name="ResultElement">The result element inside the response wrapper, named <c>&lt;op&gt;Result</c>.</param>
/// <param name="ResultNillable">
/// True when a null result is written as <c>xsi:nil="true"</c>; the consumer marks every result
/// <c>required, xmlNillable</c>.
/// </param>
/// <param name="ResultSource">
/// The evidence for the result element, a file and line such as
/// <c>Erewhon/ErewhonWebServiceConsumerSchema.scm:511</c>.
/// </param>
public sealed record SoapBinding(
    string Action,
    string ActionSource,
    string RequestElement,
    string ResponseElement,
    string ResultElement,
    bool ResultNillable,
    string ResultSource);

/// <summary>
/// One status outcome of an operation, as assigned by its method body in
/// Erewhon/ErehwhonExternalInterfacesSchema.scm.
/// </summary>
/// <param name="HttpStatus">
/// The HTTP status of the response: the REST status (200, 201, 400, 404 or 500), or 200 for every SOAP
/// outcome, because business errors travel in the result and never as a Fault.
/// </param>
/// <param name="Constant">
/// The <c>HTTP_*</c> or SOAP status constant the body assigns, for example <c>HTTP_Not_Found</c> or
/// <c>Error_Not_Found</c>; null when the outcome assigns none.
/// </param>
/// <param name="ResultStatusCode">
/// The <c>DTOResult.statusCode</c> the body carries when <paramref name="BodyType"/> is
/// <c>DTOResult</c> (REST <c>postCreate*</c> and SOAP <c>update*</c>); null otherwise.
/// </param>
/// <param name="BodyType">
/// The body's declared type: a catalogue DTO or array name, <see cref="OperationStatus.NullBody"/> for
/// the REST literal JSON <c>null</c> body, or <see cref="OperationStatus.NilBody"/> for a SOAP
/// <c>xsi:nil="true"</c> result.
/// </param>
/// <param name="RuntimeTypes">
/// The concrete subclasses the body may carry in place of <paramref name="BodyType"/>, for example
/// <c>DTORetailItem</c> and <c>DTOTenderItem</c> for <c>getItemByCode</c>; null when only the
/// declared type appears.
/// </param>
/// <param name="Condition">When the method body produces this outcome.</param>
/// <param name="SourceLines">
/// The inclusive line range in Erewhon/ErehwhonExternalInterfacesSchema.scm that produces the outcome,
/// written <c>first-last</c>, for example <c>2343-2345</c>.
/// </param>
public sealed record OperationStatus(
    int HttpStatus,
    string? Constant,
    int? ResultStatusCode,
    string BodyType,
    IReadOnlyList<string>? RuntimeTypes,
    string Condition,
    string SourceLines)
{
    /// <summary>The <see cref="BodyType"/> of a REST response whose body is the literal JSON <c>null</c>.</summary>
    public const string NullBody = "null";

    /// <summary>The <see cref="BodyType"/> of a SOAP response whose result is <c>xsi:nil="true"</c>.</summary>
    public const string NilBody = "nil";

    /// <summary>True when the response body is the literal JSON <c>null</c>.</summary>
    [JsonIgnore]
    public bool IsNullBody => string.Equals(BodyType, NullBody, StringComparison.Ordinal);

    /// <summary>True when the SOAP result is written as <c>xsi:nil="true"</c>.</summary>
    [JsonIgnore]
    public bool IsNilBody => string.Equals(BodyType, NilBody, StringComparison.Ordinal);
}

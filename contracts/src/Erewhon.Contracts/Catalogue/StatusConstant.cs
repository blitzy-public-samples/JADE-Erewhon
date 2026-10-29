using System.Text.Json.Serialization;

namespace Erewhon.Contracts.Catalogue;

/// <summary>
/// One status constant declared in Erewhon/ErehwhonExternalInterfacesSchema.scm: the eight
/// <c>HTTP_*</c> constants of <c>ErewhonRestService</c> (scm:293-300) or the eight SOAP status
/// constants of <c>ErewhonWebService</c> (scm:1139-1146). The stand-in conformance check inventories
/// every constant: a reachable one passes when some captured case returns it, and every other one is
/// registered as the gap <see cref="ConformanceItemId"/>. Each positional parameter binds to the
/// catalogue key of the same name in camelCase.
/// </summary>
/// <remarks>
/// The catalogue writes all six keys in every entry and never writes <c>null</c>, so no positional
/// parameter in this file is nullable or has a default value: under the loader's strict options a
/// dropped key fails the load instead of reading as <c>null</c>.
/// </remarks>
/// <param name="Surface">
/// The service that declares the constant: <see cref="Surface.Rest"/> for <c>ErewhonRestService</c>,
/// whose method bodies assign it to <c>httpStatusCode</c>, or <see cref="Surface.Soap"/> for
/// <c>ErewhonWebService</c>, whose bodies assign it to <c>DTOResult.statusCode</c>.
/// </param>
/// <param name="Name">
/// The constant name exactly as declared, for example <c>HTTP_Not_Found</c> or
/// <c>Error_Unauthorized</c>. Names are unique within a surface.
/// </param>
/// <param name="Value">
/// The declared integer value, never corrected: <c>Error_Unauthorized</c> is declared 403, the same
/// value as <c>Error_Forbidden</c>, and stays 403 here.
/// </param>
/// <param name="SchemaLine">
/// The line of the constant's declaration in Erewhon/ErehwhonExternalInterfacesSchema.scm, for example
/// 293 for <c>HTTP_Bad_Request</c> and 1144 for <c>Error_Unauthorized</c>.
/// </param>
/// <param name="Reachability">
/// <see cref="ReachabilityValues.Reachable"/> when an in-scope method body of the surface assigns the
/// constant, otherwise <see cref="ReachabilityValues.UnreachableInSource"/>; <c>OperationCatalogue</c>
/// rejects any other value at load. Unreachable are REST <c>HTTP_Conflict</c>, <c>HTTP_Forbidden</c>
/// and <c>HTTP_Unauthorized</c>, and SOAP <c>Error_Conflict</c>, <c>Error_Forbidden</c>,
/// <c>Error_Unauthorized</c>, <c>Error_Server_Error</c> and <c>Status_Created</c>.
/// </param>
/// <param name="Evidence">
/// One <c>&lt;path&gt;:&lt;line|first-last&gt;</c> citation. For a reachable constant it is the first
/// line that assigns it, such as <c>Erewhon/ErehwhonExternalInterfacesSchema.scm:2344</c>; for an
/// unreachable one it is the span of bodies traced without finding an assignment,
/// <c>Erewhon/ErehwhonExternalInterfacesSchema.scm:2326-2868</c> for REST or
/// <c>Erewhon/ErehwhonExternalInterfacesSchema.scm:6528-6942</c> for SOAP.
/// </param>
public sealed record StatusConstant(
    Surface Surface,
    string Name,
    int Value,
    int SchemaLine,
    string Reachability,
    string Evidence)
{
    /// <summary>
    /// True when an in-scope method body of the surface assigns the constant, so conformance expects
    /// some captured case to return it.
    /// </summary>
    [JsonIgnore]
    public bool IsReachable => Reachability == ReachabilityValues.Reachable;

    /// <summary>
    /// The conformance failure item id <c>CONF:&lt;Rest|Soap&gt;:STATUS:&lt;constant&gt;</c>, for example
    /// <c>CONF:Rest:STATUS:HTTP_Conflict</c>, which known-limitations register entries match exactly.
    /// </summary>
    [JsonIgnore]
    public string ConformanceItemId => $"CONF:{Surface}:STATUS:{Name}";
}

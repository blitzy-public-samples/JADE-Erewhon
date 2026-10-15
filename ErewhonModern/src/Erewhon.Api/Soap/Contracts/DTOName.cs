using System.Xml.Serialization;

namespace Erewhon.Api.Soap.Contracts;

/// <summary>
/// SOAP data contract holding the name of an agent or client.
/// </summary>
[XmlType(TypeName = "DTOName", Namespace = "urn:erewhon:soap")]
public sealed class DTOName
{
    /// <summary>
    /// GUID, up to 39 characters; null when not set.
    /// </summary>
    public string? guid { get; set; }

    /// <summary>
    /// Name of the agent or client, up to 81 characters.
    /// </summary>
    public string? name { get; set; }
}

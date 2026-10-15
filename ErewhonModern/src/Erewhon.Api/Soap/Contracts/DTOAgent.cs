using System.Xml.Serialization;

namespace Erewhon.Api.Soap.Contracts;

/// <summary>
/// SOAP data contract for an agent: its GUID, name and address fields.
/// </summary>
[XmlType(TypeName = "DTOAgent", Namespace = "urn:erewhon:soap")]
public sealed class DTOAgent
{
    /// <summary>
    /// GUID of the agent, up to 39 characters.
    /// </summary>
    public string? guid { get; set; }

    /// <summary>
    /// Agent name, up to 81 characters.
    /// </summary>
    public string? name { get; set; }

    /// <summary>
    /// Street address, up to 51 characters.
    /// </summary>
    public string? street { get; set; }

    /// <summary>
    /// City, up to 51 characters.
    /// </summary>
    public string? city { get; set; }

    /// <summary>
    /// Country, up to 51 characters.
    /// </summary>
    public string? country { get; set; }

    /// <summary>
    /// Phone number, up to 26 characters.
    /// </summary>
    public string? phone { get; set; }

    /// <summary>
    /// Fax number, up to 26 characters.
    /// </summary>
    public string? fax { get; set; }

    /// <summary>
    /// E-mail address, up to 61 characters.
    /// </summary>
    public string? email { get; set; }

    /// <summary>
    /// Web site address, up to 61 characters.
    /// </summary>
    public string? website { get; set; }
}

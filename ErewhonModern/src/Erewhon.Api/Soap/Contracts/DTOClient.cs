using System.Xml.Serialization;

namespace Erewhon.Api.Soap.Contracts;

/// <summary>
/// SOAP data contract for a client: its GUID, name and address fields.
/// </summary>
[XmlType(TypeName = "DTOClient", Namespace = "urn:erewhon:soap")]
public sealed class DTOClient
{
    /// <summary>
    /// GUID of the client, up to 39 characters.
    /// </summary>
    public string? guid { get; set; }

    /// <summary>
    /// Client name, up to 81 characters.
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

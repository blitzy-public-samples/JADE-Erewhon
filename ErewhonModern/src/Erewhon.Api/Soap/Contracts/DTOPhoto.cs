using System.Xml.Serialization;

namespace Erewhon.Api.Soap.Contracts;

/// <summary>
/// SOAP data contract holding a photo as image bytes.
/// </summary>
[XmlType(TypeName = "DTOPhoto", Namespace = "urn:erewhon:soap")]
public sealed class DTOPhoto
{
    /// <summary>
    /// GUID, up to 39 characters; null when not set.
    /// </summary>
    public string? guid { get; set; }

    /// <summary>
    /// Image bytes of the photo; null when there is no photo.
    /// </summary>
    public byte[]? photo { get; set; }
}

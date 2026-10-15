using System.Xml.Serialization;

namespace Erewhon.Api.Soap.Contracts;

/// <summary>SOAP outcome of a create, update or delete operation.</summary>
[XmlType(TypeName = "DTOResult", Namespace = "urn:erewhon:soap")]
public sealed class DTOResult
{
    /// <summary>GUID of the agent or client the operation created or updated, up to 39 characters; null otherwise.</summary>
    public string? guid { get; set; }

    /// <summary>Error messages of the operation, in order; empty when there are none.</summary>
    public string[] allErrors { get; set; } = [];

    /// <summary>Warning messages of the operation; empty when there are none.</summary>
    public string[] allWarnings { get; set; } = [];

    /// <summary>Status code of the operation: 200, 201, 400, 404, 409 or 500.</summary>
    public int statusCode { get; set; }
}

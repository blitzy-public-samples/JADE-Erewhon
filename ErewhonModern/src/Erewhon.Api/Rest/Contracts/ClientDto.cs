namespace Erewhon.Api.Rest.Contracts;

/// <summary>
/// A client of the company with the client's address.
/// </summary>
public sealed class ClientDto
{
    /// <summary>
    /// GUID of the client. At most 39 characters.
    /// </summary>
    public string? Guid { get; set; }

    /// <summary>
    /// Name of the client. At most 81 characters.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Street of the client's address. At most 51 characters.
    /// </summary>
    public string? Street { get; set; }

    /// <summary>
    /// City of the client's address. At most 51 characters.
    /// </summary>
    public string? City { get; set; }

    /// <summary>
    /// Country of the client's address. At most 51 characters.
    /// </summary>
    public string? Country { get; set; }

    /// <summary>
    /// Phone number of the client. At most 26 characters.
    /// </summary>
    public string? Phone { get; set; }

    /// <summary>
    /// Fax number of the client. At most 26 characters.
    /// </summary>
    public string? Fax { get; set; }

    /// <summary>
    /// Email address of the client. At most 61 characters.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Web site of the client. At most 61 characters.
    /// </summary>
    public string? Website { get; set; }
}

namespace Erewhon.Api.Rest.Contracts;

/// <summary>
/// The company with its address.
/// </summary>
public sealed class CompanyDto
{
    /// <summary>
    /// Source GUID of the company. At most 39 characters.
    /// </summary>
    public string? Guid { get; set; }

    /// <summary>
    /// Name of the company. At most 81 characters.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Street of the company's address. At most 51 characters.
    /// </summary>
    public string? Street { get; set; }

    /// <summary>
    /// City of the company's address. At most 51 characters.
    /// </summary>
    public string? City { get; set; }

    /// <summary>
    /// Country of the company's address. At most 51 characters.
    /// </summary>
    public string? Country { get; set; }

    /// <summary>
    /// Phone number of the company's address. At most 26 characters.
    /// </summary>
    public string? Phone { get; set; }

    /// <summary>
    /// Fax number of the company's address. At most 26 characters.
    /// </summary>
    public string? Fax { get; set; }

    /// <summary>
    /// E-mail address of the company's address. At most 61 characters.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Web site of the company's address. At most 61 characters.
    /// </summary>
    public string? Website { get; set; }
}

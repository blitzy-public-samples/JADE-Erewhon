using Erewhon.Application.Common;

namespace Erewhon.Application.Domain;

/// <summary>
/// The postal and contact address of an agent, client or company, stored in its owner's row.
/// An empty string is the unset value of every field.
/// </summary>
public sealed class Address
{
    /// <summary>Street; at most 51 characters.</summary>
    public string Street { get; set; } = string.Empty;

    /// <summary>City; at most 51 characters.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>Country name as entered; at most 51 characters.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Phone number; at most 26 characters.</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>Fax number; at most 26 characters.</summary>
    public string Fax { get; set; } = string.Empty;

    /// <summary>E-mail address; at most 61 characters.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Web site; at most 61 characters.</summary>
    public string WebSite { get; set; } = string.Empty;

    /// <summary>Source GUID text of the address; at most 39 characters.</summary>
    public string Guid { get; set; } = string.Empty;

    /// <summary>
    /// Returns the street, city and country separated by a comma and a space,
    /// e.g. <c>314 Nohwere Lane, Christchurch, New Zealand</c>.
    /// </summary>
    /// <returns>The formatted address.</returns>
    public string GetFormattedAddress() => Street + ", " + City + ", " + Country;

    /// <summary>
    /// Returns the developer description <c>&lt;oid&gt; Address: &lt;formatted address&gt;</c>,
    /// e.g. <c>5773.7 Address: 314 Nohwere Lane, Christchurch, New Zealand</c> for the owner with id 7.
    /// </summary>
    /// <param name="ownerId">The row id of the address's owner.</param>
    /// <returns>The debug string.</returns>
    public string GetDebugString(long ownerId) =>
        ObjectIdentity.OfAddress(ownerId) + " Address: " + GetFormattedAddress();
}

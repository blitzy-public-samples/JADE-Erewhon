namespace Erewhon.StandIn.Model;

/// <summary>
/// The postal and contact details of an agent, client or company, the counterpart of JADE
/// <c>Address</c>.
/// </summary>
/// <remarks>
/// Every attribute holds the value <c>AddressTA</c> assigned, already trimmed and validated there;
/// this class neither trims nor validates. The empty string is the JADE null string, so a seed
/// token <c>null</c> reaches here as <c>""</c>. The declared JADE lengths are documented but not
/// enforced: every seed value is well within them, and no planned case sends a longer one
/// (KL-STANDIN-STRING-LENGTHS).
/// </remarks>
public sealed class Address : PersistentModel
{
    /// <summary>Creates an address with the object number its company allocated.</summary>
    /// <param name="oid">Creation sequence number within the company graph.</param>
    internal Address(long oid)
        : base(oid)
    {
    }

    /// <summary>JADE <c>street: String[51]</c>.</summary>
    public string Street { get; internal set; } = "";

    /// <summary>JADE <c>city: String[51]</c>.</summary>
    public string City { get; internal set; } = "";

    /// <summary>JADE <c>country: String[51]</c>.</summary>
    public string Country { get; internal set; } = "";

    /// <summary>JADE <c>phone: String[26]</c>.</summary>
    public string Phone { get; internal set; } = "";

    /// <summary>JADE <c>fax: String[26]</c>.</summary>
    public string Fax { get; internal set; } = "";

    /// <summary>JADE <c>email: String[61]</c>.</summary>
    public string Email { get; internal set; } = "";

    /// <summary>JADE <c>webSite: String[61]</c>.</summary>
    public string WebSite { get; internal set; } = "";

    /// <summary>
    /// JADE <c>myModel</c>: the <see cref="Agent"/>, <see cref="Client"/> or <see cref="Company"/>
    /// that owns this address. Set once when the address is created (JADE <c>onCreate</c>) and
    /// never changed afterwards.
    /// </summary>
    public PersistentModel? MyModel { get; internal set; }

    /// <summary>
    /// JADE <c>getFormattedAddress</c>: street, city and country separated by a comma and a space.
    /// </summary>
    public string GetFormattedAddress() => Street + ", " + City + ", " + Country;
}

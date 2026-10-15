namespace Erewhon.Api.Rest.Contracts;

/// <summary>
/// An agent of the company with the agent's address.
/// </summary>
public sealed class AgentDto
{
    /// <summary>
    /// Source GUID of the agent. At most 39 characters.
    /// </summary>
    public string? Guid { get; set; }

    /// <summary>
    /// Name of the agent. At most 81 characters.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Street of the agent's address. At most 51 characters.
    /// </summary>
    public string? Street { get; set; }

    /// <summary>
    /// City of the agent's address. At most 51 characters.
    /// </summary>
    public string? City { get; set; }

    /// <summary>
    /// Country of the agent's address. At most 51 characters.
    /// </summary>
    public string? Country { get; set; }

    /// <summary>
    /// Phone number of the agent's address. At most 26 characters.
    /// </summary>
    public string? Phone { get; set; }

    /// <summary>
    /// Fax number of the agent's address. At most 26 characters.
    /// </summary>
    public string? Fax { get; set; }

    /// <summary>
    /// E-mail address of the agent's address. At most 61 characters.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Web site of the agent's address. At most 61 characters.
    /// </summary>
    public string? Website { get; set; }
}

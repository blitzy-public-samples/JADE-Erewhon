namespace Erewhon.Application.Domain;

/// <summary>
/// Base of the named locations: a <c>Country</c> and each of its <c>Region</c> entries.
/// </summary>
public abstract class Location : PersistentModel
{
    /// <summary>The location name; at most 81 characters.</summary>
    public string Name { get; set; } = string.Empty;
}

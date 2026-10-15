namespace Erewhon.Api.Rest.Contracts;

/// <summary>
/// Outcome of a REST create operation.
/// </summary>
public sealed class ResultDto
{
    /// <summary>
    /// HTTP status code of the outcome.
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// Error messages, in the order they were raised; empty when the operation succeeded.
    /// </summary>
    public List<string> AllErrors { get; set; } = [];

    /// <summary>
    /// Warning messages, in the order they were raised.
    /// </summary>
    public List<string> AllWarnings { get; set; } = [];

    /// <summary>
    /// Source GUID of the created object, or <see langword="null"/> when nothing was created.
    /// </summary>
    public string? Guid { get; set; }
}

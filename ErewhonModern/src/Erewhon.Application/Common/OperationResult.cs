using System.Text;
using System.Text.Json.Serialization;
using Erewhon.Application.Messages;

namespace Erewhon.Application.Common;

/// <summary>
/// Identifiers of the field an error or warning relates to, carried in <see cref="OperationResult.FocusField"/>.
/// </summary>
public static class FocusFields
{
    public const string Agent = "FOCUS: Agent";
    public const string AgentCommission = "FOCUS: AgentCommission";
    public const string City = "FOCUS: City";
    public const string Client = "FOCUS: Client";
    public const string ClosureDate = "FOCUS: ClosureDate";
    public const string CodeNumber = "FOCUS: CodeNumber";
    public const string CodePrefix = "FOCUS: CodePrefix";
    public const string Country = "FOCUS: Country";
    public const string Date = "FOCUS: Date";
    public const string Description = "FOCUS: Description";
    public const string Email = "FOCUS: Email";
    public const string Fax = "FOCUS: Fax";
    public const string ForSaleDate = "FOCUS: ForSaleDate";
    public const string GUID = "FOCUS: GUID";
    public const string Item = "FOCUS: Item";
    public const string ItemCategory = "FOCUS: ItemCategory";
    public const string ItemDescription = "FOCUS: ItemDescription";
    public const string ItemName = "FOCUS: ItemName";
    public const string Name = "FOCUS: Name";
    public const string Offer = "FOCUS: Offer";
    public const string Percentage = "FOCUS: Percentage";
    public const string Phone = "FOCUS: Phone";
    public const string Photo = "FOCUS: Photo";
    public const string Price = "FOCUS: Price";
    public const string Region = "FOCUS: Region";
    public const string Street = "FOCUS: Street";
    public const string Tender = "FOCUS: Tender";
    public const string TenderItem = "FOCUS: TenderItem";
    public const string TenderSale = "FOCUS: TenderSale";
    public const string Timestamp = "FOCUS: Timestamp";
    public const string WebSite = "FOCUS: WebSite";
}

/// <summary>
/// Outcome of one transaction-agent operation: the success flag, the error and warning texts in the
/// order they were added, the focus field, and the identity of the created or changed row.
/// </summary>
/// <remarks>
/// A focus field is "not set" when it is <see langword="null"/> or the empty string.
/// Instances are not thread-safe.
/// </remarks>
public sealed class OperationResult
{
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();

    /// <summary>Creates a result with no errors, no warnings, no focus field and <see cref="Succeeded"/> false.</summary>
    public OperationResult()
    {
    }

    /// <summary>Creates a result holding copies of the given lists and the given values.</summary>
    /// <param name="succeeded">The value of <see cref="Succeeded"/>.</param>
    /// <param name="errors">The error texts in order; <see langword="null"/> gives an empty list.</param>
    /// <param name="warnings">The warning texts in order; <see langword="null"/> gives an empty list.</param>
    /// <param name="focusField">The value of <see cref="FocusField"/>.</param>
    /// <param name="id">The value of <see cref="Id"/>.</param>
    /// <param name="guid">The value of <see cref="Guid"/>.</param>
    [JsonConstructor]
    public OperationResult(
        bool succeeded,
        IReadOnlyList<string>? errors,
        IReadOnlyList<string>? warnings,
        string? focusField,
        long? id,
        string? guid)
    {
        Succeeded = succeeded;
        if (errors is not null)
        {
            _errors.AddRange(errors);
        }

        if (warnings is not null)
        {
            _warnings.AddRange(warnings);
        }

        FocusField = focusField;
        Id = id;
        Guid = guid;
    }

    /// <summary>The Boolean outcome of the operation, set by the transaction agent independently of <see cref="Errors"/>.</summary>
    public bool Succeeded { get; set; }

    /// <summary>The resolved error texts in the order they were added.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>The resolved warning texts in the order they were added.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>The focus field, one of the <see cref="FocusFields"/> values, or <see langword="null"/> when none is set.</summary>
    public string? FocusField { get; private set; }

    /// <summary>
    /// The error texts joined by CR LF, a separator being added before a text only when the text built so far
    /// is not empty; the empty string when there are no errors.
    /// </summary>
    public string FullErrorText
    {
        get
        {
            var builder = new StringBuilder();
            foreach (string error in _errors)
            {
                if (builder.Length > 0)
                {
                    builder.Append("\r\n");
                }

                builder.Append(error);
            }

            return builder.ToString();
        }
    }

    /// <summary>The internal id of the created or changed row, or <see langword="null"/> when there is none.</summary>
    public long? Id { get; set; }

    /// <summary>
    /// The source GUID of the created or changed row, or <see langword="null"/> when there is none. Not written to JSON;
    /// JSON read through the constructor leaves it <see langword="null"/>.
    /// </summary>
    [JsonIgnore]
    public string? Guid { get; set; }

    /// <summary>True when at least one error has been added.</summary>
    [JsonIgnore]
    public bool HasErrors => _errors.Count > 0;

    /// <summary>True when no error has been added.</summary>
    [JsonIgnore]
    public bool HasNoErrors => _errors.Count == 0;

    /// <summary>
    /// Appends the text of the message <paramref name="messageKey"/>, formatted with <paramref name="args"/>,
    /// to <see cref="Errors"/>, and sets <see cref="FocusField"/> to <paramref name="focusField"/> only when no
    /// focus field is set.
    /// </summary>
    /// <param name="messageKey">The message key, one of the <see cref="MessageKeys"/> values.</param>
    /// <param name="focusField">The focus field; always the second argument, <see langword="null"/> for none, before any message arguments.</param>
    /// <param name="args">The message parameters in JADE declaration order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="messageKey"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">No message is defined for <paramref name="messageKey"/>.</exception>
    /// <exception cref="FormatException">The message refers to more arguments than were supplied.</exception>
    public void AddError(string messageKey, string? focusField = null, params object?[] args) =>
        AddErrorText(MessageCatalog.Get(messageKey, args), focusField);

    /// <summary>
    /// Appends <paramref name="text"/> to <see cref="Errors"/>, and sets <see cref="FocusField"/> to
    /// <paramref name="focusField"/> only when no focus field is set.
    /// </summary>
    /// <param name="text">The resolved error text.</param>
    /// <param name="focusField">The focus field, or <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public void AddErrorText(string text, string? focusField = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        _errors.Add(text);
        if (JadeValues.IsNull(FocusField))
        {
            FocusField = focusField;
        }
    }

    /// <summary>
    /// Appends the text of the message <paramref name="messageKey"/>, formatted with <paramref name="args"/>,
    /// to <see cref="Warnings"/>, and replaces <see cref="FocusField"/> with <paramref name="focusField"/> only
    /// when a focus field is already set.
    /// </summary>
    /// <param name="messageKey">The message key, one of the <see cref="MessageKeys"/> values.</param>
    /// <param name="focusField">The focus field; always the second argument, <see langword="null"/> for none, before any message arguments.</param>
    /// <param name="args">The message parameters in JADE declaration order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="messageKey"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">No message is defined for <paramref name="messageKey"/>.</exception>
    /// <exception cref="FormatException">The message refers to more arguments than were supplied.</exception>
    public void AddWarning(string messageKey, string? focusField = null, params object?[] args) =>
        AddWarningText(MessageCatalog.Get(messageKey, args), focusField);

    /// <summary>
    /// Appends <paramref name="text"/> to <see cref="Warnings"/>, and replaces <see cref="FocusField"/> with
    /// <paramref name="focusField"/> only when a focus field is already set.
    /// </summary>
    /// <param name="text">The resolved warning text.</param>
    /// <param name="focusField">The focus field, or <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public void AddWarningText(string text, string? focusField = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        _warnings.Add(text);
        if (!JadeValues.IsNull(FocusField))
        {
            FocusField = focusField;
        }
    }

    /// <summary>
    /// Appends each error text of <paramref name="other"/>, in order, as by <see cref="AddErrorText"/> with no
    /// focus field, then takes the focus field of <paramref name="other"/> when no focus field is set.
    /// </summary>
    /// <param name="other">The result to copy from; may be this result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public void CopyErrorsFrom(OperationResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (string error in other._errors.ToArray())
        {
            AddErrorText(error, null);
        }

        if (JadeValues.IsNull(FocusField))
        {
            FocusField = other.FocusField;
        }
    }

    /// <summary>
    /// Appends each warning text of <paramref name="other"/>, in order, as by <see cref="AddWarningText"/> with no
    /// focus field, then takes the focus field of <paramref name="other"/> when no focus field is set.
    /// </summary>
    /// <param name="other">The result to copy from; may be this result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public void CopyWarningsFrom(OperationResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (string warning in other._warnings.ToArray())
        {
            AddWarningText(warning, null);
        }

        if (JadeValues.IsNull(FocusField))
        {
            FocusField = other.FocusField;
        }
    }

    /// <summary>Removes every error and clears <see cref="FocusField"/>.</summary>
    public void ClearErrors()
    {
        _errors.Clear();
        FocusField = null;
    }

    /// <summary>Removes every warning and clears <see cref="FocusField"/>.</summary>
    public void ClearWarnings()
    {
        _warnings.Clear();
        FocusField = null;
    }
}

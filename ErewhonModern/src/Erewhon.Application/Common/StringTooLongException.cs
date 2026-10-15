namespace Erewhon.Application.Common;

/// <summary>
/// A string value is longer than the declared maximum length of the field it is assigned to (error 1035).
/// </summary>
public sealed class StringTooLongException : ArgumentException, IJadeException
{
    /// <summary>The error number of the exception.</summary>
    public const int Code = 1035;

    /// <summary>The exception text, which is also its <see cref="Exception.Message"/>.</summary>
    public const string Text = "String too long";

    /// <summary>
    /// Creates the exception for a value of <paramref name="actualLength"/> characters assigned to a field
    /// declared with at most <paramref name="maxLength"/> characters.
    /// </summary>
    /// <param name="fieldName">Name of the field the value was assigned to.</param>
    /// <param name="maxLength">Declared maximum length of the field.</param>
    /// <param name="actualLength">Length of the rejected value.</param>
    public StringTooLongException(string fieldName, int maxLength, int actualLength)
        : base(Text)
    {
        FieldName = fieldName;
        MaxLength = maxLength;
        ActualLength = actualLength;
    }

    /// <summary>The error number, <see cref="Code"/>.</summary>
    public int ErrorCode => Code;

    /// <summary>Name of the field the value was assigned to.</summary>
    public string FieldName { get; }

    /// <summary>Declared maximum length of the field.</summary>
    public int MaxLength { get; }

    /// <summary>Length of the rejected value.</summary>
    public int ActualLength { get; }
}

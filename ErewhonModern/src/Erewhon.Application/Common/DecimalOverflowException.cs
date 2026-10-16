namespace Erewhon.Application.Common;

/// <summary>
/// Raised when a value assigned to a decimal attribute has more integer digits than the attribute's precision and
/// scale allow.
/// </summary>
public sealed class DecimalOverflowException : OverflowException, IJadeException
{
    /// <summary>The JADE error number of the exception, 4033.</summary>
    public const int Code = 4033;

    /// <summary>The exception text.</summary>
    public const string Text = "Result of expression overflows Decimal precision";

    /// <summary>
    /// Creates the exception with <see cref="Text"/> as its message.
    /// </summary>
    public DecimalOverflowException()
        : base(Text)
    {
    }

    /// <summary>
    /// Creates the exception with <see cref="Text"/> as its message, carrying the exception that caused it.
    /// </summary>
    /// <param name="innerException">The exception that caused this one, or <see langword="null"/>.</param>
    public DecimalOverflowException(Exception? innerException)
        : base(Text, innerException)
    {
    }

    /// <summary>The JADE error number, <see cref="Code"/>.</summary>
    public int ErrorCode => Code;
}

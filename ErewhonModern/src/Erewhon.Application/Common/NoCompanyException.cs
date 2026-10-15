namespace Erewhon.Application.Common;

/// <summary>
/// Raised when the company context has no bound company.
/// </summary>
public sealed class NoCompanyException : InvalidOperationException, IJadeException
{
    /// <summary>The JADE error number of the exception, 1090.</summary>
    public const int Code = 1090;

    /// <summary>The exception text.</summary>
    public const string Text = "Null object reference";

    /// <summary>
    /// Creates the exception with <see cref="Text"/> as its message.
    /// </summary>
    public NoCompanyException()
        : base(Text)
    {
    }

    /// <summary>
    /// Creates the exception with <see cref="Text"/> as its message, carrying the exception that caused it.
    /// </summary>
    /// <param name="innerException">The exception that caused this one, or <see langword="null"/>.</param>
    public NoCompanyException(Exception? innerException)
        : base(Text, innerException)
    {
    }

    /// <summary>The JADE error number, <see cref="Code"/>.</summary>
    public int ErrorCode => Code;
}

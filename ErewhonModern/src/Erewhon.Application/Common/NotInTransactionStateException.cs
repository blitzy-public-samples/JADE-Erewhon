namespace Erewhon.Application.Common;

/// <summary>
/// Raised when an entity is written in Ambient mode with no active transaction.
/// </summary>
public sealed class NotInTransactionStateException : InvalidOperationException, IJadeException
{
    /// <summary>The error number of the exception, 1048.</summary>
    public const int Code = 1048;

    /// <summary>The message of the exception.</summary>
    public const string Text = "Update outside transaction";

    /// <summary>
    /// Creates the exception with <see cref="Text"/> as its message.
    /// </summary>
    public NotInTransactionStateException()
        : base(Text)
    {
    }

    /// <summary>
    /// Creates the exception with <see cref="Text"/> as its message, carrying the exception that caused it.
    /// </summary>
    /// <param name="innerException">The exception that caused this one, or <see langword="null"/>.</param>
    public NotInTransactionStateException(Exception? innerException)
        : base(Text, innerException)
    {
    }

    /// <summary>The error number of the exception, <see cref="Code"/>.</summary>
    public int ErrorCode => Code;
}

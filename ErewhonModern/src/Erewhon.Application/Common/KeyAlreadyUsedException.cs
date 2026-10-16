namespace Erewhon.Application.Common;

/// <summary>
/// Raised when an entry is added to a dictionary that already holds an entry with the same key.
/// </summary>
public sealed class KeyAlreadyUsedException : InvalidOperationException, IJadeException
{
    /// <summary>The JADE error number of the exception, 1310.</summary>
    public const int Code = 1310;

    /// <summary>The exception text.</summary>
    public const string Text = "Key already used in this dictionary";

    /// <summary>
    /// Creates the exception with <see cref="Text"/> as its message.
    /// </summary>
    public KeyAlreadyUsedException()
        : base(Text)
    {
    }

    /// <summary>
    /// Creates the exception with <see cref="Text"/> as its message, carrying the exception that caused it.
    /// </summary>
    /// <param name="innerException">The exception that caused this one, or <see langword="null"/>.</param>
    public KeyAlreadyUsedException(Exception? innerException)
        : base(Text, innerException)
    {
    }

    /// <summary>The JADE error number, <see cref="Code"/>.</summary>
    public int ErrorCode => Code;
}

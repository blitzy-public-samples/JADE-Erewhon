using Erewhon.Application.Messages;

namespace Erewhon.Application.Common;

/// <summary>
/// An exception that carries a JADE error number.
/// </summary>
public interface IJadeException
{
    /// <summary>The JADE error number of the exception.</summary>
    int ErrorCode { get; }
}

/// <summary>
/// An Erewhon application error, identified by one of the error codes 500001 to 500006.
/// </summary>
public sealed class ErewhonException : Exception, IJadeException
{
    /// <summary>Raised when the item-code lock was not obtained after three attempts.</summary>
    public const int LockNotObtained = 500001;

    /// <summary>Raised when an item search has a non-zero minimum price above a non-zero maximum price.</summary>
    public const int MinHigherThanMax = 500002;

    /// <summary>Raised when a company still exists at the point the seed loader creates its company.</summary>
    public const int CompanyAlreadyExists = 500003;

    /// <summary>Raised when the seed data directory path is empty, not a valid path name, or not available.</summary>
    public const int InvalidDirectoryPath = 500004;

    /// <summary>The error code for an invalid item.</summary>
    public const int InvalidItem = 500005;

    /// <summary>Raised when a logon is attempted without a selected user.</summary>
    public const int UserNotProvided = 500006;

    private const string MinHigherThanMaxText = "Min higher than max";
    private const string CompanyAlreadyExistsText = "Company already exists";
    private const string InvalidDirectoryPathText = "Invalid directory path";
    private const string InvalidItemText = "Invalid item";
    private const string UserNotProvidedText = "User not provided";

    /// <summary>
    /// Creates the exception for an Erewhon error code, with <see cref="TextFor"/> of that code as its message.
    /// </summary>
    /// <param name="errorCode">One of the error-code constants of this class.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="errorCode"/> is not an Erewhon error code.</exception>
    public ErewhonException(int errorCode)
        : this(errorCode, null)
    {
    }

    /// <summary>
    /// Creates the exception for an Erewhon error code, with <see cref="TextFor"/> of that code as its message,
    /// carrying the exception that caused it.
    /// </summary>
    /// <param name="errorCode">One of the error-code constants of this class.</param>
    /// <param name="innerException">The exception that caused this one, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="errorCode"/> is not an Erewhon error code.</exception>
    public ErewhonException(int errorCode, Exception? innerException)
        : base(TextFor(errorCode), innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The Erewhon error code, 500001 to 500006.</summary>
    public int ErrorCode { get; }

    /// <summary>Returns the exception text for an Erewhon error code.</summary>
    /// <param name="errorCode">One of the error-code constants of this class.</param>
    /// <returns>The exception text, e.g. <c>Company already exists</c> for <see cref="CompanyAlreadyExists"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="errorCode"/> is not an Erewhon error code.</exception>
    public static string TextFor(int errorCode) => errorCode switch
    {
        LockNotObtained => MessageCatalog.Get(MessageKeys.LockNotObtainedException),
        MinHigherThanMax => MinHigherThanMaxText,
        CompanyAlreadyExists => CompanyAlreadyExistsText,
        InvalidDirectoryPath => InvalidDirectoryPathText,
        InvalidItem => InvalidItemText,
        UserNotProvided => UserNotProvidedText,
        _ => throw new ArgumentOutOfRangeException(
            nameof(errorCode),
            errorCode,
            $"{errorCode} is not an Erewhon error code; expected {LockNotObtained} to {UserNotProvided}."),
    };
}

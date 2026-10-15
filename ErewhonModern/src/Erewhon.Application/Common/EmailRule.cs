using System.Text.RegularExpressions;

namespace Erewhon.Application.Common;

/// <summary>
/// E-mail address format rule applied by the address validation.
/// </summary>
public static class EmailRule
{
    /// <summary>
    /// The e-mail address pattern, matched against the whole value.
    /// </summary>
    public const string Pattern = @"^\w+([-+.']\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*$";

    private static readonly Regex EmailRegex = new(
        Pattern,
        RegexOptions.ECMAScript | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Returns true when the whole value matches <see cref="Pattern"/>, ignoring case.
    /// A null or empty value returns false. The value is matched as given, without trimming.
    /// </summary>
    /// <param name="email">The e-mail address to test.</param>
    /// <returns>True when <paramref name="email"/> is a well-formed e-mail address; otherwise false.</returns>
    /// <exception cref="RegexMatchTimeoutException">The match exceeded its one-second time-out.</exception>
    public static bool IsValid(string? email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return false;
        }

        // The match must cover the whole value; '$' also matches before a trailing line feed.
        var match = EmailRegex.Match(email);
        return match.Success && match.Length == email.Length;
    }
}

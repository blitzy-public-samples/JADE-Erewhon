namespace Erewhon.Application.Common;

/// <summary>
/// Declared maximum lengths, in characters, of the bounded string fields, and the check that enforces them.
/// </summary>
public static class FieldLengths
{
    /// <summary>Maximum length of a persistent object's GUID text.</summary>
    public const int Guid = 39;

    /// <summary>Maximum length of an agent name.</summary>
    public const int AgentName = 81;

    /// <summary>Maximum length of a client name.</summary>
    public const int ClientName = 81;

    /// <summary>Maximum length of a company name.</summary>
    public const int CompanyName = 81;

    /// <summary>Maximum length of a location (country or region) name.</summary>
    public const int LocationName = 81;

    /// <summary>Maximum length of an item name.</summary>
    public const int ItemName = 31;

    /// <summary>Maximum length of an item category name.</summary>
    public const int ItemCategoryName = 41;

    /// <summary>Maximum length of an item category prefix.</summary>
    public const int ItemCategoryPrefix = 5;

    /// <summary>Maximum length of an address street.</summary>
    public const int AddressStreet = 51;

    /// <summary>Maximum length of an address city.</summary>
    public const int AddressCity = 51;

    /// <summary>Maximum length of an address country.</summary>
    public const int AddressCountry = 51;

    /// <summary>Maximum length of an address phone number.</summary>
    public const int AddressPhone = 26;

    /// <summary>Maximum length of an address fax number.</summary>
    public const int AddressFax = 26;

    /// <summary>Maximum length of an address e-mail.</summary>
    public const int AddressEmail = 61;

    /// <summary>Maximum length of an address web site.</summary>
    public const int AddressWebSite = 61;

    /// <summary>Maximum length of a cart line's item code.</summary>
    public const int CartItemCode = 11;

    /// <summary>Maximum length of the session id held in a row's soft lock.</summary>
    public const int LockedBySession = 64;

    /// <summary>Maximum length of an operation result's focus field name.</summary>
    public const int FocusField = 31;

    /// <summary>
    /// Checks that <paramref name="value"/> is at most <paramref name="maxLength"/> characters long, counting
    /// UTF-16 code units of the value as given. A <see langword="null"/> value is not checked.
    /// Throws <see cref="StringTooLongException"/> when the value is longer than <paramref name="maxLength"/>.
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <param name="maxLength">Declared maximum length of the field, greater than 0.</param>
    /// <param name="fieldName">Name of the field the value is assigned to.</param>
    /// <exception cref="StringTooLongException">The value is longer than <paramref name="maxLength"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is 0 or less.</exception>
    public static void Check(string? value, int maxLength, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        if (value is null)
        {
            return;
        }

        if (value.Length > maxLength)
        {
            throw new StringTooLongException(fieldName, maxLength, value.Length);
        }
    }

    /// <summary>
    /// Applies <see cref="Check"/> to <paramref name="value"/> and returns the value, or an empty string when it is
    /// <see langword="null"/>.
    /// Throws <see cref="StringTooLongException"/> when the value is longer than <paramref name="maxLength"/>.
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <param name="maxLength">Declared maximum length of the field, greater than 0.</param>
    /// <param name="fieldName">Name of the field the value is assigned to.</param>
    /// <returns>The unchanged value, or <see cref="string.Empty"/> when it is <see langword="null"/>.</returns>
    /// <exception cref="StringTooLongException">The value is longer than <paramref name="maxLength"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is 0 or less.</exception>
    public static string CheckAndReturn(string? value, int maxLength, string fieldName)
    {
        Check(value, maxLength, fieldName);
        return value ?? string.Empty;
    }
}

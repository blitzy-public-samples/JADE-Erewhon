using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;

namespace Erewhon.Application.Services;

/// <summary>
/// The address values of an agent, client or company operation, before they are written to its <see cref="Address"/>.
/// </summary>
/// <remarks>Instances are not thread-safe.</remarks>
public sealed class AddressCommand
{
    /// <summary>Street; at most 51 characters.</summary>
    public string? Street { get; set; }

    /// <summary>City; at most 51 characters.</summary>
    public string? City { get; set; }

    /// <summary>Country name as entered; at most 51 characters.</summary>
    public string? Country { get; set; }

    /// <summary>Phone number; at most 26 characters.</summary>
    public string? Phone { get; set; }

    /// <summary>Fax number; at most 26 characters.</summary>
    public string? Fax { get; set; }

    /// <summary>E-mail address; at most 61 characters.</summary>
    public string? Email { get; set; }

    /// <summary>Web site; at most 61 characters.</summary>
    public string? WebSite { get; set; }

    /// <summary>
    /// GUID text given to a new address, at most 39 characters; when null or empty a new GUID is generated.
    /// Set only by the seed loader.
    /// </summary>
    public string? Guid { get; set; }

    /// <summary>Sets every value to <see langword="null"/>.</summary>
    public void Initialize()
    {
        Street = null;
        City = null;
        Country = null;
        Phone = null;
        Fax = null;
        Email = null;
        WebSite = null;
        Guid = null;
    }

    /// <summary>Returns a command holding the seven fields and the GUID of <paramref name="address"/>.</summary>
    /// <param name="address">The address to copy from.</param>
    /// <returns>The new command.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    public static AddressCommand FromAddress(Address address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new AddressCommand
        {
            Street = address.Street,
            City = address.City,
            Country = address.Country,
            Phone = address.Phone,
            Fax = address.Fax,
            Email = address.Email,
            WebSite = address.WebSite,
            Guid = address.Guid,
        };
    }
}

/// <summary>
/// Pre-validation, validation and assignment rules of the address embedded in an agent, client or company.
/// </summary>
/// <remarks>
/// An owner calls <see cref="Validate"/> on the untrimmed values in its own validation step, then
/// <see cref="PersistInTransState"/>, which trims and validates again, and <see cref="Apply"/> in its subordinate
/// step. A whitespace-only value therefore passes the first validation and fails the second.
/// </remarks>
public static class AddressRules
{
    /// <summary>
    /// Checks each value of <paramref name="address"/>, untrimmed, against its declared length: street, city and
    /// country 51; phone and fax 26; e-mail and web site 61; GUID 39. A <see langword="null"/> value is not checked.
    /// </summary>
    /// <param name="address">The address values to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    /// <exception cref="StringTooLongException">A value is longer than its declared length.</exception>
    public static void CheckFieldLengths(AddressCommand address)
    {
        ArgumentNullException.ThrowIfNull(address);
        FieldLengths.Check(address.Street, FieldLengths.AddressStreet, nameof(AddressCommand.Street));
        FieldLengths.Check(address.City, FieldLengths.AddressCity, nameof(AddressCommand.City));
        FieldLengths.Check(address.Country, FieldLengths.AddressCountry, nameof(AddressCommand.Country));
        FieldLengths.Check(address.Phone, FieldLengths.AddressPhone, nameof(AddressCommand.Phone));
        FieldLengths.Check(address.Fax, FieldLengths.AddressFax, nameof(AddressCommand.Fax));
        FieldLengths.Check(address.Email, FieldLengths.AddressEmail, nameof(AddressCommand.Email));
        FieldLengths.Check(address.WebSite, FieldLengths.AddressWebSite, nameof(AddressCommand.WebSite));
        FieldLengths.Check(address.Guid, FieldLengths.Guid, nameof(AddressCommand.Guid));
    }

    /// <summary>
    /// Replaces the city, country, e-mail, fax, phone, street and web site of <paramref name="address"/> with the
    /// value returned by <see cref="JadeValues.TrimWhiteSpace"/>; a <see langword="null"/> value becomes the empty string.
    /// The GUID is left unchanged.
    /// </summary>
    /// <param name="address">The address values to trim.</param>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    public static void PreValidate(AddressCommand address)
    {
        ArgumentNullException.ThrowIfNull(address);
        address.City = JadeValues.TrimWhiteSpace(address.City);
        address.Country = JadeValues.TrimWhiteSpace(address.Country);
        address.Email = JadeValues.TrimWhiteSpace(address.Email);
        address.Fax = JadeValues.TrimWhiteSpace(address.Fax);
        address.Phone = JadeValues.TrimWhiteSpace(address.Phone);
        address.Street = JadeValues.TrimWhiteSpace(address.Street);
        address.WebSite = JadeValues.TrimWhiteSpace(address.WebSite);
    }

    /// <summary>
    /// Validates <paramref name="address"/> as given, adding one error to <paramref name="result"/> per failed check,
    /// in this order: street required (<see cref="MessageKeys.AddressStreetRequired"/>), city required
    /// (<see cref="MessageKeys.AddressCityRequired"/>), country required (<see cref="MessageKeys.AddressCountryRequired"/>),
    /// phone required (<see cref="MessageKeys.AddressPhoneRequired"/>), and e-mail required
    /// (<see cref="MessageKeys.AddressEmailRequired"/>) or, when present, not matching <see cref="EmailRule"/>
    /// (<see cref="MessageKeys.AddressEmailInvalidFormat"/>). A <see langword="null"/> or empty value counts as missing.
    /// Fax and web site are not checked. <see cref="ValidationType.Delete"/> adds nothing and returns
    /// <see langword="true"/>.
    /// </summary>
    /// <param name="address">The address values to validate.</param>
    /// <param name="type">The kind of operation being validated.</param>
    /// <param name="result">The result that receives the errors and the focus field of the first error.</param>
    /// <returns><see langword="true"/> for <see cref="ValidationType.Delete"/>; otherwise <see cref="OperationResult.HasNoErrors"/>
    /// of <paramref name="result"/> after the checks, which includes errors it held before the call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> or <paramref name="result"/> is <see langword="null"/>.</exception>
    public static bool Validate(AddressCommand address, ValidationType type, OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(result);

        if (type == ValidationType.Delete)
        {
            return true;
        }

        if (JadeValues.IsNull(address.Street))
        {
            result.AddError(MessageKeys.AddressStreetRequired, FocusFields.Street);
        }

        if (JadeValues.IsNull(address.City))
        {
            result.AddError(MessageKeys.AddressCityRequired, FocusFields.City);
        }

        if (JadeValues.IsNull(address.Country))
        {
            result.AddError(MessageKeys.AddressCountryRequired, FocusFields.Country);
        }

        if (JadeValues.IsNull(address.Phone))
        {
            result.AddError(MessageKeys.AddressPhoneRequired, FocusFields.Phone);
        }

        if (JadeValues.IsNull(address.Email))
        {
            result.AddError(MessageKeys.AddressEmailRequired, FocusFields.Email);
        }
        else if (!EmailRule.IsValid(address.Email))
        {
            result.AddError(MessageKeys.AddressEmailInvalidFormat, FocusFields.Email);
        }

        return result.HasNoErrors;
    }

    /// <summary>
    /// Runs the address as a subordinate step of its owner's operation: <see cref="PreValidate"/> on
    /// <paramref name="address"/>, then <see cref="Validate"/> with <paramref name="type"/> into a new result, whose
    /// errors and focus field are then copied into <paramref name="ownerResult"/>. Writes nothing.
    /// </summary>
    /// <param name="address">The address values; trimmed in place.</param>
    /// <param name="type"><see cref="ValidationType.Create"/> for a new owner, <see cref="ValidationType.Update"/> for an
    /// update, <see cref="ValidationType.Modify"/> for a modification.</param>
    /// <param name="ownerResult">The owner's result that receives the address errors.</param>
    /// <returns><see langword="true"/> when the address passed validation; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> or <paramref name="ownerResult"/> is <see langword="null"/>.</exception>
    public static bool PersistInTransState(AddressCommand address, ValidationType type, OperationResult ownerResult)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(ownerResult);

        var addressResult = new OperationResult();
        PreValidate(address);
        bool valid = Validate(address, type, addressResult);
        ownerResult.CopyErrorsFrom(addressResult);
        return valid;
    }

    /// <summary>
    /// Writes the street, city, country, phone, fax, e-mail and web site of <paramref name="address"/> to
    /// <paramref name="target"/>, a <see langword="null"/> value being written as the empty string. When the GUID of
    /// <paramref name="target"/> is empty, it is set to the command's GUID when that is not empty, otherwise to
    /// <see cref="PersistentModel.GenerateGuid"/>; an existing GUID is kept.
    /// </summary>
    /// <param name="address">The address values to write.</param>
    /// <param name="target">The address that receives them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> or <paramref name="target"/> is <see langword="null"/>.</exception>
    public static void Apply(AddressCommand address, Address target)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(target);

        if (JadeValues.IsNull(target.Guid))
        {
            target.Guid = JadeValues.IsNull(address.Guid) ? PersistentModel.GenerateGuid() : address.Guid!;
        }

        target.City = address.City ?? string.Empty;
        target.Country = address.Country ?? string.Empty;
        target.Email = address.Email ?? string.Empty;
        target.Fax = address.Fax ?? string.Empty;
        target.Phone = address.Phone ?? string.Empty;
        target.Street = address.Street ?? string.Empty;
        target.WebSite = address.WebSite ?? string.Empty;
    }
}

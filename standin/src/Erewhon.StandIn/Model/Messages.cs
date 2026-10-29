using System.Globalization;

namespace Erewhon.StandIn.Model;

/// <summary>
/// The JADE translatable strings the stand-in emits as business error texts and seed constants.
/// Copied from ErewhonModelSchema.scm locale 5129; do not edit text.
/// </summary>
/// <remarks>
/// Source: the <c>translatableStringDefinitions</c> block of <c>Erewhon/ErewhonModelSchema.scm</c>
/// (lines 2624-2921, locale 5129 "English (New Zealand)", the schema default locale that every other
/// locale clones). The equivalence baseline compares these texts byte for byte, so the double spaces in
/// the "conflicted" and "updated this entity" messages and the lower-case "item" in
/// <see cref="TenderItemAlreadySold"/> are part of the contract. JADE <c>&amp;</c> is ordinal string
/// concatenation and <c>Integer.String</c> is the invariant integer text.
/// </remarks>
public static class Messages
{
    public const string AddressCityRequired = "City is required";
    public const string AddressCountryRequired = "Country is required";
    public const string AddressEmailInvalidFormat = "Email must be in a valid format e.g. user@domain.com";
    public const string AddressEmailRequired = "Email is required";
    public const string AddressPhoneRequired = "Phone number is required";
    public const string AddressStreetRequired = "Street is required";
    public const string AgentAlreadyExists = "Agent already exists";
    public const string AgentNameRequired = "Agent name is required";
    public const string AgentNotFound = "Agent not found";
    public const string AgentRequired = "Agent is required";
    public const string AgentWithSameNameExists = "An agent with the same name already exists";
    public const string AnotherUserUpdatedThisEntity = "Another user updated this entity prior to your update attempt.  Update aborted.";
    public const string ClientAlreadyExists = "Client already exists";
    public const string ClientNameRequired = "Client name is required";
    public const string ClientNotFound = "Client not found";
    public const string ClientRequired = "A Client must be provided";
    public const string ClientWithSameNameExists = "A client with the same name already exists";
    public const string CommissionRatePercentageInvalid = "Percentage must be a decimal which is greater than 0 and less than or equal to 100";
    public const string CompanyAddress1 = "Erewhon House";
    public const string CompanyAddress2 = "Christchurch";
    public const string CompanyAddress3 = "New Zealand";
    public const string CompanyEMail = "discover@jadeworld.com";
    public const string CompanyFax = "(64) 3 555 1267";
    public const string CompanyName = "Erewhon Investments Inc";
    public const string CompanyNameRequired = "Company name is required";
    public const string CompanyNotSet = "The Company has not been set";
    public const string CompanyPhone = "(64) 3 555 4519";
    public const string CompanyWebSite = "www.jadeworld.com";
    public const string CountryAlreadyExists = "The Country already exists";
    public const string CountryNameRequired = "Country name is required";
    public const string CountryRequired = "A Country must be provided";
    public const string CountryWithSameNameExists = "A country with the same name already exists";
    public const string CreateConflictedWithAnotherUser = "Create conflicted with another user's activity.  Try again shortly.";
    public const string ImageNotValid = "The image provided is not a valid picture file";
    public const string ItemCategoryDescriptionRequired = "Category description is required";
    public const string ItemCategoryHasCommissionRate = "Item Category is already using that commission rate";
    public const string ItemCategoryNameExists = "A Category with this name already exists";
    public const string ItemCategoryNameRequired = "Category name is required";
    public const string ItemCategoryPrefixExists = "Category Prefix already exists";
    public const string ItemCategoryPrefixInvalid = "Category Prefix must be exactly 4 characters";
    public const string ItemCategoryPrefixRequired = "Category Prefix is required";
    public const string ItemCategoryRequired = "Item Category is required";
    public const string ItemDescriptionRequired = "A description of the item is required";
    public const string ItemNameRequired = "A Name for the item is required";
    public const string ItemNotFound = "Item was not found";
    public const string ItemNotRetailItem = "Item is not a Retail Item";
    public const string ItemNotTenderItem = "Item is not a Tender Item";
    public const string ItemNumberRequired = "Item number must be provided";
    public const string ItemPriceNotValid = "Item price must be greater than 0";
    public const string ItemRequired = "An Item must be provided";
    public const string ItemSold = "The Item has already been sold";
    public const string RegionExists = "A Region already exists for this country";
    public const string RegionNameRequired = "The Region name is required";
    public const string RegionRequired = "Region is required";
    public const string RetailItemType = "Retail";
    public const string RetailPriceInvalid = "The retail item price must be greater than 0";
    public const string RetailSalePriceRequired = "A price for the retail item is required";
    public const string SaleAgentCommissionInvalid = "Agent commission must be greater than 0";
    public const string SoldDateNotValid = "Sold Date is Not Valid";
    public const string TenderAlreadyPlaced = "A tender has already been placed on this item";
    public const string TenderItemAlreadyClosed = "The Tender Item has already closed";
    public const string TenderItemAlreadySold = "The Tender item has already been sold";
    public const string TenderItemClosureDateNotValid = "The tender closure date is not a valid date";
    public const string TenderItemRequired = "A Tender Item is required";
    public const string TenderItemType = "Tender";
    public const string TenderOfferLessThanReserve = "The offer price cannot be less than the reserve price";
    public const string TenderOfferLessThanZero = "The offer price must be greater than 0";
    public const string TenderRequired = "A Tender must be provided";
    public const string TimeStampInvalid = "Timestamp must be a valid date/time";
    public const string UpdateConflictedWithAnotherUser = "Update conflicted with another user's activity.  Try again shortly.";

    /// <summary><c>AgentNameAlreadyExists( pAgentName )</c>: "Agent " &amp; pAgentName &amp; " already exists".</summary>
    public static string AgentNameAlreadyExists(string agentName) =>
        string.Concat("Agent ", agentName, " already exists");

    /// <summary><c>AgentNameNotFound( pAgentName )</c>: "Agent " &amp; pAgentName &amp; " not found".</summary>
    public static string AgentNameNotFound(string agentName) =>
        string.Concat("Agent ", agentName, " not found");

    /// <summary><c>ClientNameAlreadyExists( pClientName )</c>: "Client " &amp; pClientName &amp; " already exists".</summary>
    public static string ClientNameAlreadyExists(string clientName) =>
        string.Concat("Client ", clientName, " already exists");

    /// <summary><c>ClientNameNotFound( pClientName )</c>: "Client " &amp; pClientName &amp; " not found".</summary>
    public static string ClientNameNotFound(string clientName) =>
        string.Concat("Client ", clientName, " not found");

    /// <summary><c>CouldNotDeleteAgent( pAgentName )</c>: "Could not delete the Agent " &amp; pAgentName.</summary>
    public static string CouldNotDeleteAgent(string agentName) =>
        string.Concat("Could not delete the Agent ", agentName);

    /// <summary><c>CouldNotDeleteClient( pClientName )</c>: "Could not delete the Client " &amp; pClientName.</summary>
    public static string CouldNotDeleteClient(string clientName) =>
        string.Concat("Could not delete the Client ", clientName);

    /// <summary>
    /// <c>SaleWithNumberAndPrefixNotFound( pPrefix, pNumber )</c>:
    /// "The Sale for item " &amp; pPrefix &amp; pNumber.String &amp; " was not found" (no separator between prefix and number).
    /// </summary>
    public static string SaleWithNumberAndPrefixNotFound(string prefix, int number) =>
        string.Concat("The Sale for item ", prefix, number.ToString(CultureInfo.InvariantCulture), " was not found");
}

/// <summary>
/// The string members of the <c>ErewhonStatuses</c> constant category
/// (<c>Erewhon/ErewhonModelSchema.scm</c> lines 72-76), returned by <c>Tender.getStatus</c>.
/// </summary>
public static class ErewhonStatuses
{
    public const string Closed = "Closed";
    public const string Open = "Open";
    public const string Successful = "Successful";
}

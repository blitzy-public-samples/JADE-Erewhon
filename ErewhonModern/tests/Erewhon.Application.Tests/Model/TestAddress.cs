using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Tests.Model;

/// <summary>
/// Ports the JADE <c>TestAddress</c> unit tests (T-01). The fixture address is the owned address of the agent
/// "John Smith", created at <see cref="TestCompanyBuilder.NohwereLaneAddress"/> in the bound company and persisted,
/// read and updated through that agent (D-143). The validation tests call <see cref="AddressRules.Validate"/>
/// directly, as the originals call <c>AddressTA.doValidate</c>.
/// </summary>
public sealed class TestAddress : ErewhonTestBase
{
    private static readonly string[] InvalidEmails =
    [
        "@email.com",
        "invalid@email",
        "invalid@email.",
        "invalid@",
    ];

    private Agent? _myAddressOwner;

    /// <summary>The agent whose address is the fixture address. Ports <c>myAddress</c>.</summary>
    private Agent MyAddressOwner =>
        _myAddressOwner ?? throw new InvalidOperationException("The fixture address owner is created in SetUpAsync.");

    /// <summary>The internal id (<c>agents.id</c>) of <see cref="MyAddressOwner"/>.</summary>
    private long MyAddressOwnerId => MyAddressOwner.Id;

    /// <summary>
    /// Creates and binds the company, then creates the agent "John Smith" at 314 Nohwere Lane, Christchurch,
    /// New Zealand; e-mail "admin@Erewhon.nz", fax "FAX", phone "555 1234567", web site "www.jadeworld.com".
    /// Ports <c>setUp</c>; the base tear-down deletes the company with the agent, which ports <c>tearDown</c>.
    /// </summary>
    /// <returns>A task that completes when the fixture exists.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Agent owner = await Builder.CreateAgentAsync();
        Builder.MyAgent = owner;
        _myAddressOwner = owner;
    }

    /// <summary>The debug string is the address OID followed by " Address: " and the formatted address.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetDebugString()
    {
        Address address = await LoadMyAddressAsync();

        string oid = ObjectIdentity.OfAddress(MyAddressOwnerId);

        Assert.Equal(
            oid + " Address: 314 Nohwere Lane, Christchurch, New Zealand",
            address.GetDebugString(MyAddressOwnerId));
    }

    /// <summary>The formatted address is street, city and country separated by ", ".</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetFormattedAddress()
    {
        Address address = await LoadMyAddressAsync();

        Assert.Equal("314 Nohwere Lane, Christchurch, New Zealand", address.GetFormattedAddress());
    }

    /// <summary>The seven stored fields read back as the fixture values.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testProperties()
    {
        Address address = await LoadMyAddressAsync();

        Assert.Equal("314 Nohwere Lane", address.Street);
        Assert.Equal("Christchurch", address.City);
        Assert.Equal("New Zealand", address.Country);
        Assert.Equal("admin@Erewhon.nz", address.Email);
        Assert.Equal("FAX", address.Fax);
        Assert.Equal("555 1234567", address.Phone);
        Assert.Equal("www.jadeworld.com", address.WebSite);
    }

    /// <summary>A persist of the owner populated from it with seven new address values stores all seven.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdate()
    {
        AgentCommand command = CreateProxyAddressCommand();

        OperationResult result = await GetService<AgentService>()
            .ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded, result.FullErrorText);

        Address address = await LoadMyAddressAsync();
        Assert.Equal("proxyStreet", address.Street);
        Assert.Equal("proxyCity", address.City);
        Assert.Equal("proxyCountry", address.Country);
        Assert.Equal("proxyEmail@somewhere.com", address.Email);
        Assert.Equal("proxyFax", address.Fax);
        Assert.Equal("proxyNumber", address.Phone);
        Assert.Equal("ProxyWebsite", address.WebSite);
    }

    /// <summary>
    /// The same persist in the caller's transaction mode with no transaction open raises error 1048 and leaves the
    /// stored address unchanged.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdateInTransStateException()
    {
        AgentCommand command = CreateProxyAddressCommand();
        AgentService agentService = GetService<AgentService>();

        NotInTransactionStateException exception = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => agentService.ExecuteAsync(command, TransactionType.Persist, TransactionMode.Ambient));

        Assert.Equal(1048, exception.ErrorCode);

        Address address = await LoadMyAddressAsync();
        Assert.Equal("314 Nohwere Lane", address.Street);
        Assert.Equal("Christchurch", address.City);
        Assert.Equal("New Zealand", address.Country);
        Assert.Equal("admin@Erewhon.nz", address.Email);
        Assert.Equal("FAX", address.Fax);
        Assert.Equal("555 1234567", address.Phone);
        Assert.Equal("www.jadeworld.com", address.WebSite);
    }

    /// <summary>
    /// A valid e-mail passes create validation with no errors; each of four malformed e-mails fails it with exactly
    /// one error, <see cref="MessageKeys.AddressEmailInvalidFormat"/>. The errors are cleared before each malformed case.
    /// </summary>
    [Fact]
    public void testValidateEmailRegex()
    {
        var address = new AddressCommand
        {
            Street = "Street",
            City = "City",
            Country = "Country",
            Email = null,
            Fax = "Fax",
            Phone = "Phone",
            WebSite = "www.website.com",
        };
        var result = new OperationResult();

        address.Email = "valid@email.com";
        Assert.True(AddressRules.Validate(address, ValidationType.Create, result));
        Assert.Empty(result.Errors);

        foreach (string invalidEmail in InvalidEmails)
        {
            result.ClearErrors();
            address.Email = invalidEmail;

            Assert.False(AddressRules.Validate(address, ValidationType.Create, result));
            string error = Assert.Single(result.Errors);
            Assert.Equal(MessageCatalog.Get(MessageKeys.AddressEmailInvalidFormat), error);
        }
    }

    /// <summary>
    /// An address with no values fails create validation with exactly the street, city, country, phone and e-mail
    /// required errors, compared in any order.
    /// </summary>
    [Fact]
    public void testValidationFail()
    {
        string[] expectedErrors =
        [
            MessageCatalog.Get(MessageKeys.AddressStreetRequired),
            MessageCatalog.Get(MessageKeys.AddressCityRequired),
            MessageCatalog.Get(MessageKeys.AddressCountryRequired),
            MessageCatalog.Get(MessageKeys.AddressPhoneRequired),
            MessageCatalog.Get(MessageKeys.AddressEmailRequired),
        ];
        var address = new AddressCommand();
        var result = new OperationResult();

        Assert.False(AddressRules.Validate(address, ValidationType.Create, result));
        Assert.Equal(5, expectedErrors.Length);
        Assert.Equal(
            expectedErrors.Order(StringComparer.Ordinal),
            result.Errors.Order(StringComparer.Ordinal));
    }

    /// <summary>An address with street, city, country, e-mail and phone and no fax or web site passes create validation.</summary>
    [Fact]
    public void testValidationSuccess()
    {
        var address = new AddressCommand
        {
            City = "Christchurch",
            Country = "New Zealand",
            Email = "user@jadeworld.com",
            Phone = "03 365 2500",
            Street = "5 Sir Gill Simpson Drive",
        };

        bool validationSuccess = AddressRules.Validate(address, ValidationType.Create, new OperationResult());

        Assert.True(validationSuccess);
    }

    /// <summary>
    /// Returns an agent command populated from <see cref="MyAddressOwner"/> whose address holds street "proxyStreet",
    /// city "proxyCity", country "proxyCountry", e-mail "proxyEmail@somewhere.com", fax "proxyFax", phone
    /// "proxyNumber" and web site "ProxyWebsite".
    /// </summary>
    private AgentCommand CreateProxyAddressCommand()
    {
        var command = new AgentCommand();
        command.PopulateFromObject(MyAddressOwner);

        AddressCommand address = command.Address
            ?? throw new InvalidOperationException("PopulateFromObject left the agent command without an address.");
        address.Street = "proxyStreet";
        address.City = "proxyCity";
        address.Country = "proxyCountry";
        address.Email = "proxyEmail@somewhere.com";
        address.Fax = "proxyFax";
        address.Phone = "proxyNumber";
        address.WebSite = "ProxyWebsite";

        return command;
    }

    /// <summary>Reads the stored address of <see cref="MyAddressOwner"/> from the database, untracked.</summary>
    private async Task<Address> LoadMyAddressAsync()
    {
        long ownerId = MyAddressOwnerId;

        Agent owner = await GetService<IErewhonDbContext>().Agents
            .AsNoTracking()
            .SingleAsync(agent => agent.Id == ownerId);

        return owner.Address;
    }
}

using Erewhon.Application.Abstractions;
using Erewhon.Application.Cart;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Services;
using Erewhon.Application.Transactions;
using Erewhon.Testing;
using Microsoft.EntityFrameworkCore;

namespace Erewhon.Application.Tests.View;

/// <summary>
/// Tests of the session user and the session cart: <see cref="TestSessionContext"/> in place of <c>app.getClient</c>,
/// <c>app.setAgent</c>, <c>app.setClient</c> and <c>app.isCompanyUser</c>, and <see cref="ShoppingCartService"/> over
/// <see cref="ICartStore"/>. Ports the JADE class <c>TestViewApp</c>.
/// </summary>
public sealed class TestViewApp : ErewhonTestBase
{
    /// <summary>The fixture company built by <see cref="ErewhonTestBase.SetUpAsync"/>.</summary>
    /// <exception cref="InvalidOperationException">Read before the company is built.</exception>
    private Company MyCompany =>
        Builder.MyCompany ?? throw new InvalidOperationException($"{nameof(MyCompany)} is set by {nameof(SetUpAsync)}.");

    /// <summary>The fixture agent "John Smith" built by <see cref="SetUpAsync"/>.</summary>
    /// <exception cref="InvalidOperationException">Read before the agent is built.</exception>
    private Agent MyAgent =>
        Builder.MyAgent ?? throw new InvalidOperationException($"{nameof(MyAgent)} is set by {nameof(SetUpAsync)}.");

    /// <summary>The fixture client "Clive Entworth" built by <see cref="SetUpAsync"/>.</summary>
    /// <exception cref="InvalidOperationException">Read before the client is built.</exception>
    private Client MyClient =>
        Builder.MyClient ?? throw new InvalidOperationException($"{nameof(MyClient)} is set by {nameof(SetUpAsync)}.");

    /// <summary>The fixture retail item "Proxy Retail" built by <see cref="SetUpAsync"/>.</summary>
    /// <exception cref="InvalidOperationException">Read before the retail item is built.</exception>
    private RetailItem MyRetailItem =>
        Builder.MyRetailItem ?? throw new InvalidOperationException($"{nameof(MyRetailItem)} is set by {nameof(SetUpAsync)}.");

    /// <summary>
    /// Builds the company "Jade Software Corporation" and binds it, then the agent "John Smith", the client
    /// "Clive Entworth", the country "France" with its region "Paris", the item category "Category 1" (prefix
    /// <c>CAT1</c>), the retail item "Proxy Retail" and the tender item "Proxy Tender", and sets the session agent and
    /// client to the fixture agent and client. Ports <c>setUp</c>.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    /// <exception cref="InvalidOperationException">A fixture was not persisted.</exception>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Builder.MyAgent = await Builder.CreateAgentAsync();
        Builder.MyClient = await Builder.CreateClientAsync();

        Country country = await Builder.CreateCountryAsync();
        Builder.MyCountry = country;
        Region region = await Builder.CreateRegionAsync(country);
        Builder.MyRegion = region;
        ItemCategory itemCategory = await Builder.CreateItemCategoryAsync("Category 1", "CAT1", "Item Category 1");
        Builder.MyItemCategory = itemCategory;
        Builder.MyRetailItem = await Builder.CreateRetailItemAsync(region, itemCategory, MyAgent);
        Builder.MyTenderItem = await Builder.CreateTenderItemAsync(region, itemCategory, MyAgent);

        Session.SetAgent(MyAgent.Id);
        Session.SetClient(MyClient.Id);
    }

    /// <summary>The session client is the company's first client in name order.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testGetClient()
    {
        // Arrange / Act
        long expectedClientId = await FirstClientIdAsync();
        long? actualClientId = Session.ClientId;

        // Assert
        Assert.Equal(expectedClientId, actualClientId);
    }

    /// <summary>
    /// The session user is the company user only while neither an agent nor a client is set: not with both set, not
    /// with the agent cleared, then the company user with both cleared, and not again once the company's first agent
    /// is set.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testIsCompanyUser()
    {
        Assert.False(Session.IsCompanyUser);

        Session.SetAgent(null);
        Assert.False(Session.IsCompanyUser);

        Session.SetClient(null);
        Assert.True(Session.IsCompanyUser);

        Session.SetAgent(await FirstAgentIdAsync());
        Assert.False(Session.IsCompanyUser);

        Session.SetClient(await FirstClientIdAsync());
    }

    /// <summary>
    /// The session agent starts as the fixture agent; after the agent "New Agent Name" is created and set as the
    /// session agent, the session agent is that agent.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    /// <exception cref="InvalidOperationException">The new agent was not persisted.</exception>
    [Fact]
    public async Task testSetAgent()
    {
        // Assert: precheck
        Assert.Equal(MyAgent.Id, Session.AgentId);

        // Arrange
        var command = new AgentCommand
        {
            Name = "New Agent Name",
            Address = TestCompanyBuilder.NohwereLaneAddress(),
        };

        // Act
        OperationResult result = await GetService<AgentService>()
            .ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);
        Agent agent = command.ModelObject ?? throw new InvalidOperationException(result.FullErrorText);
        Session.SetAgent(agent.Id);

        // Assert
        Assert.Equal(agent.Id, Session.AgentId);
    }

    /// <summary>
    /// After the client "New Client Name" is created and set as the session client, the session client is that client.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    /// <exception cref="InvalidOperationException">The new client was not persisted.</exception>
    [Fact]
    public async Task testSetClient()
    {
        // Arrange
        var command = new ClientCommand
        {
            Name = "New Client Name",
            Address = TestCompanyBuilder.NohwereLaneAddress(),
        };

        // Act
        OperationResult result = await GetService<ClientService>()
            .ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);
        Client client = command.ModelObject ?? throw new InvalidOperationException(result.FullErrorText);
        Session.SetClient(client.Id);

        // Assert
        Assert.Equal(client.Id, Session.ClientId);
    }

    /// <summary>
    /// The session cart starts empty, holds one line after the fixture retail item is added, and is empty again after
    /// that item is removed.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testShoppingCart()
    {
        // Arrange
        string sessionId = Session.SessionId
            ?? throw new InvalidOperationException("The test session has no session id.");
        ShoppingCartService cartService = GetService<ShoppingCartService>();
        ShoppingCart cart = GetService<ICartStore>().GetCart(sessionId);

        // Assert: cart is empty
        Assert.True(cart.IsEmpty);

        // Act: add the retail item
        await cartService.AddItemAsync(sessionId, MyRetailItem.Id, 0m);

        // Assert: cart holds one line
        Assert.False(cart.IsEmpty);
        Assert.Single(cart.Items);

        // Act: remove the retail item
        cartService.RemoveItem(sessionId, MyRetailItem.Id);

        // Assert: cart is empty
        Assert.True(cart.IsEmpty);
        Assert.Empty(cart.Items);
    }

    /// <summary>Returns the internal id of the fixture company's first agent in name order, then id order.</summary>
    /// <returns>The agent's internal id (<c>agents.id</c>).</returns>
    /// <exception cref="InvalidOperationException">The company has no agent.</exception>
    private Task<long> FirstAgentIdAsync()
    {
        long companyId = MyCompany.Id;
        return GetService<IErewhonDbContext>().Agents
            .AsNoTracking()
            .Where(agent => agent.CompanyId == companyId)
            .OrderBy(agent => agent.Name)
            .ThenBy(agent => agent.Id)
            .Select(agent => agent.Id)
            .FirstAsync();
    }

    /// <summary>Returns the internal id of the fixture company's first client in name order, then id order.</summary>
    /// <returns>The client's internal id (<c>clients.id</c>).</returns>
    /// <exception cref="InvalidOperationException">The company has no client.</exception>
    private Task<long> FirstClientIdAsync()
    {
        long companyId = MyCompany.Id;
        return GetService<IErewhonDbContext>().Clients
            .AsNoTracking()
            .Where(client => client.CompanyId == companyId)
            .OrderBy(client => client.Name)
            .ThenBy(client => client.Id)
            .Select(client => client.Id)
            .FirstAsync();
    }
}

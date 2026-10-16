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
/// Ports the JADE <c>TestCommissionRate</c> unit tests: adding and clearing the agents of a commission rate, the
/// commission-rate validation outcomes, the debug string, and updates inside and outside a transaction. Each test
/// starts with the company "Jade Software Corporation", the agent "John Smith", the item category "Foosbar" and an
/// 11.00% commission rate on that category held by no agent.
/// </summary>
public sealed class TestCommissionRate : ErewhonTestBase
{
    private const int UpdateOutsideTransaction = 1048;

    private Agent MyAgent =>
        Builder.MyAgent ?? throw new InvalidOperationException("The fixture agent is created by SetUpAsync.");

    private ItemCategory MyItemCategory =>
        Builder.MyItemCategory
            ?? throw new InvalidOperationException("The fixture item category is created by SetUpAsync.");

    private CommissionRate MyCommissionRate =>
        Builder.MyCommissionRate
            ?? throw new InvalidOperationException("The fixture commission rate is created by SetUpAsync.");

    private CommissionRateService RateService => GetService<CommissionRateService>();

    private IErewhonDbContext Db => GetService<IErewhonDbContext>();

    /// <summary>
    /// Adding the fixture agent to the rate gives the rate 1 agent, found by the agent's name, and the agent 1 rate;
    /// clearing the agents of the same command leaves the rate and the agent with none.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testClearAllAgents()
    {
        long rateId = MyCommissionRate.Id;
        Assert.Empty(await AgentIdsOfRateAsync(rateId));

        var commissionRateCommand = new CommissionRateCommand();
        commissionRateCommand.PopulateFromObject(await LoadMyCommissionRateAsync());
        commissionRateCommand.AgentIds.Add(MyAgent.Id);
        await RateService.ExecuteAsync(commissionRateCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.Single(await AgentIdsOfRateAsync(rateId));
        Assert.Single(await RateIdsOfAgentAsync(MyAgent.Id));
        Assert.Equal(MyAgent.Id, Assert.Single(await AgentIdsOfRateByNameAsync(rateId, MyAgent.Name)));

        commissionRateCommand.AgentIds.Clear();
        await RateService.ExecuteAsync(commissionRateCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.Empty(await AgentIdsOfRateAsync(rateId));
        Assert.Empty(await RateIdsOfAgentAsync(MyAgent.Id));
    }

    /// <summary>
    /// A new rate with the fixture rate's percentage on the same category is not persisted and gives the single error
    /// <see cref="MessageKeys.ItemCategoryHasCommissionRate"/>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testDuplicateRateError()
    {
        Assert.NotNull(Builder.MyCommissionRate);

        var commissionRateCommand = new CommissionRateCommand
        {
            Percentage = MyCommissionRate.Percentage,
            ItemCategoryId = MyCommissionRate.ItemCategoryId,
        };

        OperationResult result =
            await RateService.ExecuteAsync(commissionRateCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.False(result.Succeeded);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemCategoryHasCommissionRate), Assert.Single(result.Errors));
    }

    /// <summary>The fixture rate's debug string is its OID text followed by " CommissionRate: 11.00%".</summary>
    [Fact]
    public void testGetDebugString()
    {
        Assert.Equal(
            ObjectIdentity.Of(MyCommissionRate) + " CommissionRate: 11.00%",
            MyCommissionRate.GetDebugString());
    }

    /// <summary>
    /// A new rate of 12.00% with no item category is not persisted and gives the single error
    /// <see cref="MessageKeys.ItemCategoryRequired"/>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testNullCategory()
    {
        Assert.NotNull(Builder.MyCommissionRate);

        var commissionRateCommand = new CommissionRateCommand
        {
            Percentage = MyCommissionRate.Percentage + 1m,
            ItemCategoryId = null,
        };

        OperationResult result =
            await RateService.ExecuteAsync(commissionRateCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.False(result.Succeeded);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemCategoryRequired), Assert.Single(result.Errors));
    }

    /// <summary>
    /// A new rate of 101% on the fixture category is not persisted and gives the single error
    /// <see cref="MessageKeys.CommissionRatePercentageInvalid"/>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testPercentageHigh()
    {
        Assert.NotNull(Builder.MyCommissionRate);

        var commissionRateCommand = new CommissionRateCommand
        {
            Percentage = 101m,
            ItemCategoryId = MyCommissionRate.ItemCategoryId,
        };

        OperationResult result =
            await RateService.ExecuteAsync(commissionRateCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.False(result.Succeeded);
        Assert.Equal(MessageCatalog.Get(MessageKeys.CommissionRatePercentageInvalid), Assert.Single(result.Errors));
    }

    /// <summary>
    /// A new rate of 0% on the fixture category is not persisted and gives the single error
    /// <see cref="MessageKeys.CommissionRatePercentageInvalid"/>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testPercentageLow()
    {
        Assert.NotNull(Builder.MyCommissionRate);

        var commissionRateCommand = new CommissionRateCommand
        {
            Percentage = 0m,
            ItemCategoryId = MyCommissionRate.ItemCategoryId,
        };

        OperationResult result =
            await RateService.ExecuteAsync(commissionRateCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.False(result.Succeeded);
        Assert.Equal(MessageCatalog.Get(MessageKeys.CommissionRatePercentageInvalid), Assert.Single(result.Errors));
    }

    /// <summary>
    /// Updating the fixture rate to 21.00% in its own transaction succeeds with no errors and stores 21.00%.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdate()
    {
        Assert.NotNull(Builder.MyCommissionRate);

        decimal percentage = MyCommissionRate.Percentage + 10m;

        var commissionRateCommand = new CommissionRateCommand();
        commissionRateCommand.PopulateFromObject(await LoadMyCommissionRateAsync());
        commissionRateCommand.Percentage = percentage;

        OperationResult result =
            await RateService.ExecuteAsync(commissionRateCommand, TransactionType.Persist, TransactionMode.Own);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Errors);
        Assert.Equal(percentage, await StoredPercentageAsync(MyCommissionRate.Id));
    }

    /// <summary>
    /// Updating the fixture rate to 21.00% in the caller's transaction when none is active raises
    /// <see cref="NotInTransactionStateException"/> with error code 1048 and leaves 11.00% stored.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdateOutsideTransStateException()
    {
        Assert.NotNull(Builder.MyCommissionRate);

        decimal percentage = MyCommissionRate.Percentage + 10m;

        var commissionRateCommand = new CommissionRateCommand();
        commissionRateCommand.PopulateFromObject(await LoadMyCommissionRateAsync());
        commissionRateCommand.Percentage = percentage;

        NotInTransactionStateException exception = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => RateService.ExecuteAsync(commissionRateCommand, TransactionType.Persist, TransactionMode.Ambient));

        Assert.Equal(UpdateOutsideTransaction, exception.ErrorCode);
        Assert.Equal(11.00m, await StoredPercentageAsync(MyCommissionRate.Id));
    }

    /// <summary>
    /// Creates the agent "John Smith", the item category "Foosbar" (prefix "Foos") and an 11.00% commission rate on
    /// that category held by no agent, in the company the base set-up creates and binds.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();

        Builder.MyAgent = await Builder.CreateAgentAsync();
        Builder.MyItemCategory = await Builder.CreateItemCategoryAsync(
            "Foosbar",
            "Foos",
            "Finest quality Bars of delux Foo");
        Builder.MyCommissionRate = await Builder.CreateTestCommissionRateAsync(MyItemCategory, 11.00m);
    }

    /// <summary>Returns the fixture rate with its item category and agent links loaded.</summary>
    private async Task<CommissionRate> LoadMyCommissionRateAsync() =>
        await RateService.FindByIdAsync(MyCommissionRate.Id)
            ?? throw new InvalidOperationException($"Commission rate {MyCommissionRate.Id} does not exist.");

    /// <summary>Returns the stored ids of the agents holding the rate <paramref name="rateId"/>.</summary>
    private Task<List<long>> AgentIdsOfRateAsync(long rateId) =>
        Db.AgentCommissionRates
            .AsNoTracking()
            .Where(link => link.CommissionRateId == rateId)
            .Select(link => link.AgentId)
            .ToListAsync();

    /// <summary>Returns the stored ids of the agents named <paramref name="agentName"/> holding the rate <paramref name="rateId"/>.</summary>
    private Task<List<long>> AgentIdsOfRateByNameAsync(long rateId, string agentName) =>
        Db.AgentCommissionRates
            .AsNoTracking()
            .Where(link => link.CommissionRateId == rateId && link.Agent!.Name == agentName)
            .Select(link => link.AgentId)
            .ToListAsync();

    /// <summary>Returns the stored ids of the commission rates held by the agent <paramref name="agentId"/>.</summary>
    private Task<List<long>> RateIdsOfAgentAsync(long agentId) =>
        Db.AgentCommissionRates
            .AsNoTracking()
            .Where(link => link.AgentId == agentId)
            .Select(link => link.CommissionRateId)
            .ToListAsync();

    /// <summary>Returns the stored percentage of the rate <paramref name="rateId"/>.</summary>
    private Task<decimal> StoredPercentageAsync(long rateId) =>
        Db.CommissionRates
            .AsNoTracking()
            .Where(rate => rate.Id == rateId)
            .Select(rate => rate.Percentage)
            .SingleAsync();
}

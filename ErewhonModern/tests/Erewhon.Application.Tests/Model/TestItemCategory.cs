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
/// Ports the JADE <c>TestItemCategory</c> unit tests: item-category create and update validation, the commission rates
/// a category holds, the debug string, and an update outside a transaction. Every operation runs through the production
/// services against the assembly's PostgreSQL database, on the category "Tests" (prefix <c>TEST</c>) of a fresh
/// company built before each test.
/// </summary>
public sealed class TestItemCategory : ErewhonTestBase
{
    private const string FixtureName = "Tests";
    private const string FixturePrefix = "TEST";
    private const string FixtureDescription = "Pre-used Tests, may contain wrong answers.";
    private const int UpdateOutsideTransaction = 1048;

    /// <summary>The fixture category of the test, <see cref="TestCompanyBuilder.MyItemCategory"/>.</summary>
    /// <exception cref="InvalidOperationException">Read before <see cref="SetUpAsync"/> created the category.</exception>
    private ItemCategory MyItemCategory =>
        Builder.MyItemCategory
        ?? throw new InvalidOperationException($"The fixture item category exists once {nameof(SetUpAsync)} has run.");

    /// <summary>
    /// Creates and binds the company, then creates the category "Tests", prefix <c>TEST</c>, description
    /// "Pre-used Tests, may contain wrong answers." as <see cref="TestCompanyBuilder.MyItemCategory"/>.
    /// Ports <c>TestItemCategory.setUp</c>.
    /// </summary>
    /// <returns>A task that completes when the fixtures exist.</returns>
    protected override async Task SetUpAsync()
    {
        await base.SetUpAsync();
        Builder.MyItemCategory = await Builder.CreateItemCategoryAsync(FixtureName, FixturePrefix, FixtureDescription);
    }

    /// <summary>
    /// A create with the fixture's name, prefix and description fails with no model object and the errors
    /// <c>$ItemCategoryPrefixExists</c> then <c>$ItemCategoryNameExists</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testAlreadyExists()
    {
        Assert.NotNull(Builder.MyItemCategory);

        var command = new ItemCategoryCommand
        {
            Name = MyItemCategory.Name,
            Prefix = MyItemCategory.Prefix,
            Description = MyItemCategory.Description,
        };

        OperationResult result = await PersistAsync(command, TransactionMode.Own);

        Assert.Null(command.ModelObject);
        Assert.False(result.Succeeded);
        Assert.Equal(
            new[]
            {
                MessageCatalog.Get(MessageKeys.ItemCategoryPrefixExists),
                MessageCatalog.Get(MessageKeys.ItemCategoryNameExists),
            },
            result.Errors);
    }

    /// <summary>
    /// A commission rate of 21.55% created on the fixture category persists, and the category then holds exactly that
    /// one rate, found under its percentage.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testCreateCommissionRate()
    {
        const decimal percentage = 21.55m;

        var command = new CommissionRateCommand
        {
            Percentage = percentage,
            ItemCategoryId = MyItemCategory.Id,
        };

        OperationResult result = await GetService<CommissionRateService>()
            .ExecuteAsync(command, TransactionType.Persist, TransactionMode.Own);
        CommissionRate? commissionRate = command.ModelObject;

        Assert.True(result.Succeeded);
        Assert.NotNull(commissionRate);

        IReadOnlyList<CommissionRate> categoryRates = await ReadCommissionRatesAsync(MyItemCategory.Id);
        Assert.Single(categoryRates);
        CommissionRate rateAtPercentage = Assert.Single(categoryRates, rate => rate.Percentage == percentage);
        Assert.Equal(commissionRate.Id, rateAtPercentage.Id);
        Assert.Equal(percentage, rateAtPercentage.Percentage);
    }

    /// <summary>
    /// The debug string of the fixture category is <c>&lt;oid&gt; ItemCategory: &lt;name&gt;, &lt;description&gt;</c>.
    /// </summary>
    [Fact]
    public void testGetDebugString()
    {
        string name = MyItemCategory.Name;
        string description = MyItemCategory.Description;
        string oid = ObjectIdentity.Of(MyItemCategory);
        string expected = oid + " ItemCategory: " + name + ", " + description;

        string actual = MyItemCategory.GetDebugString();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// A create named "Sports" with the fixture's prefix and no description fails with no model object and the errors
    /// <c>$ItemCategoryPrefixExists</c> then <c>$ItemCategoryDescriptionRequired</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidDescription()
    {
        var command = new ItemCategoryCommand
        {
            Name = "Sports",
            Prefix = MyItemCategory.Prefix,
            Description = null,
        };

        OperationResult result = await PersistAsync(command, TransactionMode.Own);

        Assert.Null(command.ModelObject);
        Assert.False(result.Succeeded);
        Assert.Equal(
            new[]
            {
                MessageCatalog.Get(MessageKeys.ItemCategoryPrefixExists),
                MessageCatalog.Get(MessageKeys.ItemCategoryDescriptionRequired),
            },
            result.Errors);
    }

    /// <summary>
    /// A create with no name, the fixture's prefix and the description "Category Description" fails with no model
    /// object and the errors <c>$ItemCategoryPrefixExists</c> then <c>$ItemCategoryNameRequired</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidName()
    {
        var command = new ItemCategoryCommand
        {
            Name = null,
            Prefix = MyItemCategory.Prefix,
            Description = "Category Description",
        };

        OperationResult result = await PersistAsync(command, TransactionMode.Own);

        Assert.Null(command.ModelObject);
        Assert.False(result.Succeeded);
        Assert.Equal(
            new[]
            {
                MessageCatalog.Get(MessageKeys.ItemCategoryPrefixExists),
                MessageCatalog.Get(MessageKeys.ItemCategoryNameRequired),
            },
            result.Errors);
    }

    /// <summary>
    /// A create named "CATG" with the three-character prefix "CTG" fails with no model object and the single error
    /// <c>$ItemCategoryPrefixInvalid</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testInvalidPrefix()
    {
        var command = new ItemCategoryCommand
        {
            Name = "CATG",
            Prefix = "CTG",
            Description = "Category Description",
        };

        OperationResult result = await PersistAsync(command, TransactionMode.Own);

        Assert.Null(command.ModelObject);
        Assert.False(result.Succeeded);
        string error = Assert.Single(result.Errors);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemCategoryPrefixInvalid), error);
    }

    /// <summary>
    /// A create named "CATG" with no prefix fails with no model object and the single error
    /// <c>$ItemCategoryPrefixRequired</c>.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testNullPrefix()
    {
        var command = new ItemCategoryCommand
        {
            Name = "CATG",
            Prefix = null,
            Description = "Category Description",
        };

        OperationResult result = await PersistAsync(command, TransactionMode.Own);

        Assert.Null(command.ModelObject);
        Assert.False(result.Succeeded);
        string error = Assert.Single(result.Errors);
        Assert.Equal(MessageCatalog.Get(MessageKeys.ItemCategoryPrefixRequired), error);
    }

    /// <summary>
    /// An update of the fixture category to "New Name", prefix <c>NPFX</c>, description "New Category Description"
    /// succeeds with no errors, and the stored category holds the new name and description.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdate()
    {
        const string name = "New Name";
        const string prefix = "NPFX";
        const string description = "New Category Description";

        var command = new ItemCategoryCommand();
        command.PopulateFromObject(MyItemCategory);
        command.Name = name;
        command.Prefix = prefix;
        command.Description = description;

        OperationResult result = await PersistAsync(command, TransactionMode.Own);

        Assert.NotNull(command.ModelObject);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Errors);

        ItemCategory storedCategory = await ReadItemCategoryAsync(MyItemCategory.Id);
        Assert.Equal(name, storedCategory.Name);
        Assert.Equal(description, storedCategory.Description);
    }

    /// <summary>
    /// An update of the fixture category to "New Name" in <see cref="TransactionMode.Ambient"/> mode with no active
    /// transaction raises <see cref="NotInTransactionStateException"/> with error code 1048, and the stored category
    /// keeps the name "Tests".
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task testUpdateOutsideTransStateException()
    {
        Assert.NotNull(Builder.MyItemCategory);

        var command = new ItemCategoryCommand();
        command.PopulateFromObject(MyItemCategory);
        command.Name = "New Name";
        command.Prefix = MyItemCategory.Prefix;
        command.Description = "New Category Description";

        NotInTransactionStateException exception = await Assert.ThrowsAsync<NotInTransactionStateException>(
            () => PersistAsync(command, TransactionMode.Ambient));

        Assert.Equal(UpdateOutsideTransaction, exception.ErrorCode);

        ItemCategory storedCategory = await ReadItemCategoryAsync(MyItemCategory.Id);
        Assert.Equal(FixtureName, storedCategory.Name);
    }

    /// <summary>
    /// Runs <paramref name="command"/> through <see cref="ItemCategoryService"/> as a
    /// <see cref="TransactionType.Persist"/> operation in <paramref name="mode"/>.
    /// </summary>
    private Task<OperationResult> PersistAsync(ItemCategoryCommand command, TransactionMode mode) =>
        GetService<ItemCategoryService>().ExecuteAsync(command, TransactionType.Persist, mode);

    /// <summary>Reads the stored category with internal id <paramref name="itemCategoryId"/>, untracked.</summary>
    private Task<ItemCategory> ReadItemCategoryAsync(long itemCategoryId) =>
        GetService<IErewhonDbContext>().ItemCategories
            .AsNoTracking()
            .SingleAsync(category => category.Id == itemCategoryId);

    /// <summary>
    /// Reads the stored commission rates of the category with internal id <paramref name="itemCategoryId"/>, untracked,
    /// in percentage order.
    /// </summary>
    private async Task<IReadOnlyList<CommissionRate>> ReadCommissionRatesAsync(long itemCategoryId) =>
        await GetService<IErewhonDbContext>().CommissionRates
            .AsNoTracking()
            .Where(rate => rate.ItemCategoryId == itemCategoryId)
            .OrderBy(rate => rate.Percentage)
            .ToListAsync();
}

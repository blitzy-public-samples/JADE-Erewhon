using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erewhon.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Item"/>, <see cref="RetailItem"/> and <see cref="TenderItem"/> to the single table <c>items</c>,
/// distinguished by the <c>item_type</c> column (<c>R</c> retail, <c>T</c> tender): the row identity, the GUID and
/// soft-lock columns, the item details, the code number unique within the company, and the foreign keys to
/// <c>companies</c>, <c>item_categories</c>, <c>agents</c> and <c>regions</c>.
/// </summary>
public sealed class ItemConfiguration :
    IEntityTypeConfiguration<Item>,
    IEntityTypeConfiguration<RetailItem>,
    IEntityTypeConfiguration<TenderItem>
{
    /// <summary>
    /// Configures the <c>items</c> table, its <c>item_type</c> discriminator, the columns shared by both item types,
    /// the unique <c>(company_id, code_number)</c> index, the indexes on category, agent and region, the non-unique
    /// <c>(company_id, price)</c> index, the cascading foreign keys to <c>companies</c> and <c>item_categories</c>,
    /// and the set-null foreign keys to <c>agents</c> and <c>regions</c>.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="Item"/> entity type.</param>
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.ToTable("items");

        builder.HasKey(x => x.Id).HasName("pk_items");

        builder.HasDiscriminator<string>("ItemType")
            .HasValue<RetailItem>("R")
            .HasValue<TenderItem>("T");

        builder.Property<string>("ItemType")
            .HasColumnName("item_type")
            .HasMaxLength(1)
            .IsRequired();

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(x => x.Guid)
            .HasColumnName("guid")
            .HasMaxLength(FieldLengths.Guid)
            .IsRequired();

        builder.Property(x => x.LockedBySession)
            .HasColumnName("locked_by_session")
            .HasMaxLength(FieldLengths.LockedBySession)
            .IsRequired(false);

        builder.Property(x => x.LockedTimestamp)
            .HasColumnName("locked_timestamp")
            .HasColumnType("timestamp(3) without time zone")
            .IsRequired(false);

        builder.Property(x => x.CompanyId)
            .HasColumnName("company_id")
            .IsRequired();

        builder.Property(x => x.CodeNumber)
            .HasColumnName("code_number")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(FieldLengths.ItemName)
            .IsRequired();

        builder.Property(x => x.Description)
            .HasColumnName("description")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(x => x.ListedDate)
            .HasColumnName("listed_date")
            .HasColumnType("date")
            .IsRequired();

        builder.Property(x => x.Photo)
            .HasColumnName("photo")
            .HasColumnType("bytea")
            .IsRequired(false);

        builder.Property(x => x.Price)
            .HasColumnName("price")
            .HasPrecision(12, 2)
            .IsRequired();

        builder.Property(x => x.AgentId)
            .HasColumnName("agent_id")
            .IsRequired(false);

        builder.Property(x => x.ItemCategoryId)
            .HasColumnName("item_category_id")
            .IsRequired();

        builder.Property(x => x.RegionId)
            .HasColumnName("region_id")
            .IsRequired(false);

        builder.Ignore(x => x.JadeClassNumber);
        builder.Ignore(x => x.IsSoftLocked);
        builder.Ignore(x => x.IsSold);

        builder.HasIndex(x => new { x.CompanyId, x.CodeNumber })
            .IsUnique()
            .HasDatabaseName("ix_items_company_id_code_number");

        builder.HasIndex(x => new { x.ItemCategoryId, x.CodeNumber })
            .HasDatabaseName("ix_items_item_category_id_code_number");

        builder.HasIndex(x => new { x.AgentId, x.ItemCategoryId, x.CodeNumber })
            .HasDatabaseName("ix_items_agent_id_item_category_id_code_number");

        builder.HasIndex(x => new { x.RegionId, x.CodeNumber })
            .HasDatabaseName("ix_items_region_id_code_number");

        builder.HasIndex(x => new { x.CompanyId, x.Price })
            .HasDatabaseName("ix_items_company_id_price");

        builder.HasOne(x => x.Company)
            .WithMany(c => c.Items)
            .HasForeignKey(x => x.CompanyId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_items_companies_company_id");

        builder.HasOne(x => x.ItemCategory)
            .WithMany(c => c.Items)
            .HasForeignKey(x => x.ItemCategoryId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_items_item_categories_item_category_id");

        builder.HasOne(x => x.Agent)
            .WithMany(a => a.Items)
            .HasForeignKey(x => x.AgentId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_items_agents_agent_id");

        builder.HasOne(x => x.Region)
            .WithMany(r => r.Items)
            .HasForeignKey(x => x.RegionId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_items_regions_region_id");
    }

    /// <summary>
    /// Configures <see cref="RetailItem"/> as the <c>items</c> rows with <c>item_type</c> <c>R</c>, and the unique
    /// <c>(company_id, guid)</c> index over those rows.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="RetailItem"/> entity type.</param>
    public void Configure(EntityTypeBuilder<RetailItem> builder)
    {
        builder.HasBaseType<Item>();

        builder.HasIndex(x => new { x.CompanyId, x.Guid })
            .IsUnique()
            .HasFilter("item_type = 'R'")
            .HasDatabaseName("ix_items_company_id_guid_retail");
    }

    /// <summary>
    /// Configures <see cref="TenderItem"/> as the <c>items</c> rows with <c>item_type</c> <c>T</c>: the
    /// <c>closure_date</c> column, the unique <c>(company_id, guid)</c> index and the <c>(company_id, closure_date)</c>
    /// index over those rows.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="TenderItem"/> entity type.</param>
    public void Configure(EntityTypeBuilder<TenderItem> builder)
    {
        builder.HasBaseType<Item>();

        builder.Property(x => x.ClosureDate)
            .HasColumnName("closure_date")
            .HasColumnType("date");

        builder.HasIndex(x => new { x.CompanyId, x.Guid })
            .IsUnique()
            .HasFilter("item_type = 'T'")
            .HasDatabaseName("ix_items_company_id_guid_tender");

        builder.HasIndex(x => new { x.CompanyId, x.ClosureDate })
            .HasFilter("item_type = 'T'")
            .HasDatabaseName("ix_items_company_id_closure_date_tender");
    }
}

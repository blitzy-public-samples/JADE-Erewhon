using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erewhon.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Sale"/>, <see cref="RetailSale"/> and <see cref="TenderSale"/> to the single table <c>sales</c>,
/// told apart by the <c>sale_type</c> discriminator: <c>R</c> for a retail sale, <c>T</c> for a tender sale.
/// Each item has at most one sale; a sale is deleted with its company, client or item.
/// </summary>
public sealed class SaleConfiguration :
    IEntityTypeConfiguration<Sale>,
    IEntityTypeConfiguration<RetailSale>,
    IEntityTypeConfiguration<TenderSale>
{
    /// <summary>
    /// Configures the <c>sales</c> table for the root type <see cref="Sale"/>: the row identity, the GUID and
    /// soft-lock columns, the <c>sale_type</c> discriminator (<c>R</c>, <c>T</c>), the company, client and item
    /// columns, the agent commission, the unique <c>item_id</c> index, the <c>company_id</c> and <c>client_id</c>
    /// indexes, and the cascading foreign keys to <c>companies</c>, <c>clients</c> and <c>items</c>.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="Sale"/> entity type.</param>
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        builder.ToTable("sales");

        builder.HasKey(x => x.Id).HasName("pk_sales");

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

        builder.Ignore(x => x.JadeClassNumber);
        builder.Ignore(x => x.IsSoftLocked);

        builder.HasDiscriminator<string>("SaleType")
            .HasValue<RetailSale>("R")
            .HasValue<TenderSale>("T");

        builder.Property<string>("SaleType")
            .HasColumnName("sale_type")
            .HasMaxLength(1)
            .IsRequired();

        builder.Property(x => x.CompanyId)
            .HasColumnName("company_id")
            .IsRequired();

        builder.Property(x => x.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(x => x.ItemId)
            .HasColumnName("item_id")
            .IsRequired();

        builder.Property(x => x.AgentCommission)
            .HasColumnName("agent_commission")
            .HasPrecision(12, 2)
            .IsRequired();

        builder.HasIndex(x => x.ItemId)
            .IsUnique()
            .HasDatabaseName("ix_sales_item_id");

        builder.HasIndex(x => x.CompanyId)
            .HasDatabaseName("ix_sales_company_id");

        builder.HasIndex(x => x.ClientId)
            .HasDatabaseName("ix_sales_client_id");

        builder.HasOne(x => x.Company)
            .WithMany(c => c.Sales)
            .HasForeignKey(x => x.CompanyId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_sales_companies_company_id");

        builder.HasOne(x => x.Client)
            .WithMany(c => c.Sales)
            .HasForeignKey(x => x.ClientId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_sales_clients_client_id");

        builder.HasOne(x => x.Item)
            .WithOne(i => i.Sale)
            .HasForeignKey<Sale>(x => x.ItemId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_sales_items_item_id");
    }

    /// <summary>
    /// Configures <see cref="RetailSale"/> as the <c>sales</c> rows with <c>sale_type</c> <c>R</c>: the
    /// <c>sale_price</c> and <c>date_sold</c> columns and the <c>(client_id, date_sold)</c> index over retail rows.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="RetailSale"/> entity type.</param>
    public void Configure(EntityTypeBuilder<RetailSale> builder)
    {
        builder.HasBaseType<Sale>();

        builder.Property(x => x.SalePrice)
            .HasColumnName("sale_price")
            .HasPrecision(12, 2);

        builder.Property(x => x.DateSold)
            .HasColumnName("date_sold")
            .HasColumnType("timestamp(3) without time zone");

        builder.HasIndex(x => new { x.ClientId, x.DateSold })
            .HasFilter("sale_type = 'R'")
            .HasDatabaseName("ix_sales_client_id_date_sold_retail");
    }

    /// <summary>
    /// Configures <see cref="TenderSale"/> as the <c>sales</c> rows with <c>sale_type</c> <c>T</c>: the nullable
    /// <c>tender_id</c> column, the <c>(client_id, tender_id)</c> index over tender rows, the unique
    /// <c>tender_id</c> index, and the foreign key to <c>tenders</c> that sets <c>tender_id</c> to null when the
    /// tender is deleted.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="TenderSale"/> entity type.</param>
    public void Configure(EntityTypeBuilder<TenderSale> builder)
    {
        builder.HasBaseType<Sale>();

        builder.Property(x => x.TenderId)
            .HasColumnName("tender_id")
            .IsRequired(false);

        builder.HasIndex(x => new { x.ClientId, x.TenderId })
            .HasFilter("sale_type = 'T'")
            .HasDatabaseName("ix_sales_client_id_tender_id_tender");

        builder.HasIndex(x => x.TenderId)
            .IsUnique()
            .HasDatabaseName("ix_sales_tender_id");

        builder.HasOne(x => x.Tender)
            .WithOne(t => t.TenderSale)
            .HasForeignKey<TenderSale>(x => x.TenderId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_sales_tenders_tender_id");
    }
}

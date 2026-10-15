using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erewhon.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Tender"/> to the table <c>tenders</c>: one row per offer by a client on a tender item, owned by its
/// company, deleted with its company, its client or its tender item.
/// </summary>
public sealed class TenderConfiguration : IEntityTypeConfiguration<Tender>
{
    /// <summary>
    /// Configures the <c>tenders</c> table, its columns, the unique indexes <c>(company_id, date_tendered, offer DESC)</c>,
    /// <c>(tender_item_id, offer DESC, date_tendered)</c> and <c>(client_id, tender_item_id, date_tendered, offer DESC)</c>,
    /// and the cascading foreign keys to <c>companies</c>, <c>clients</c> and <c>items</c>.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="Tender"/> entity type.</param>
    public void Configure(EntityTypeBuilder<Tender> builder)
    {
        builder.ToTable("tenders");

        builder.HasKey(x => x.Id).HasName("pk_tenders");

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

        builder.Property(x => x.CompanyId)
            .HasColumnName("company_id")
            .IsRequired();

        builder.Property(x => x.ClientId)
            .HasColumnName("client_id")
            .IsRequired();

        builder.Property(x => x.TenderItemId)
            .HasColumnName("tender_item_id")
            .IsRequired();

        builder.Property(x => x.Offer)
            .HasColumnName("offer")
            .HasPrecision(12, 2)
            .IsRequired();

        builder.Property(x => x.DateTendered)
            .HasColumnName("date_tendered")
            .HasColumnType("timestamp(3) without time zone")
            .IsRequired();

        builder.HasIndex(x => new { x.CompanyId, x.DateTendered, x.Offer })
            .IsUnique()
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_tenders_company_id_date_tendered_offer");

        builder.HasIndex(x => new { x.TenderItemId, x.Offer, x.DateTendered })
            .IsUnique()
            .IsDescending(false, true, false)
            .HasDatabaseName("ix_tenders_tender_item_id_offer_date_tendered");

        builder.HasIndex(x => new { x.ClientId, x.TenderItemId, x.DateTendered, x.Offer })
            .IsUnique()
            .IsDescending(false, false, false, true)
            .HasDatabaseName("ix_tenders_client_id_tender_item_id_date_tendered_offer");

        builder.HasOne(x => x.Company)
            .WithMany(c => c.Tenders)
            .HasForeignKey(x => x.CompanyId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_tenders_companies_company_id");

        builder.HasOne(x => x.Client)
            .WithMany(c => c.Tenders)
            .HasForeignKey(x => x.ClientId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_tenders_clients_client_id");

        builder.HasOne(x => x.TenderItem)
            .WithMany(i => i.Tenders)
            .HasForeignKey(x => x.TenderItemId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_tenders_items_tender_item_id");
    }
}

using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erewhon.Persistence.Configurations;

/// <summary>
/// Maps <see cref="CommissionRate"/> to the table <c>commission_rates</c>: one row per percentage offered on an
/// item category, unique per category and percentage, deleted with its category.
/// </summary>
public sealed class CommissionRateConfiguration : IEntityTypeConfiguration<CommissionRate>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CommissionRate> builder)
    {
        builder.ToTable("commission_rates");

        builder.HasKey(x => x.Id).HasName("pk_commission_rates");

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

        builder.Property(x => x.ItemCategoryId)
            .HasColumnName("item_category_id")
            .IsRequired();

        builder.Property(x => x.Percentage)
            .HasColumnName("percentage")
            .HasPrecision(5, 2)
            .IsRequired();

        builder.HasIndex(x => new { x.ItemCategoryId, x.Percentage })
            .IsUnique()
            .HasDatabaseName("ix_commission_rates_item_category_id_percentage");

        builder.HasOne(x => x.ItemCategory)
            .WithMany(c => c.CommissionRates)
            .HasForeignKey(x => x.ItemCategoryId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_commission_rates_item_categories_item_category_id");
    }
}

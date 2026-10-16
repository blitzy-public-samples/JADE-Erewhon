using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erewhon.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ItemCategory"/> to the <c>item_categories</c> table: the row identity, the GUID and soft-lock
/// columns, the owning company, the name, unique within the company, the prefix and the description. The name and the
/// prefix are compared and ordered under the binary collation <c>C</c>.
/// The unique index <c>ix_item_categories_company_id_upper_prefix</c> on <c>(company_id, upper(prefix))</c> is
/// created by the <c>InitialSchema</c> migration SQL and is not part of this mapping (D-66).
/// </summary>
public sealed class ItemCategoryConfiguration : IEntityTypeConfiguration<ItemCategory>
{
    /// <summary>
    /// Configures the <c>item_categories</c> table, its columns, the unique <c>(company_id, name)</c> index and the
    /// cascading foreign key to <c>companies</c>.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="ItemCategory"/> entity type.</param>
    public void Configure(EntityTypeBuilder<ItemCategory> builder)
    {
        builder.ToTable("item_categories");

        builder.HasKey(x => x.Id).HasName("pk_item_categories");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(x => x.Guid)
            .HasColumnName("guid")
            .HasMaxLength(FieldLengths.Guid)
            .IsRequired();

        builder.Property(x => x.LockedBySession)
            .HasColumnName("locked_by_session")
            .HasMaxLength(FieldLengths.LockedBySession);

        builder.Property(x => x.LockedTimestamp)
            .HasColumnName("locked_timestamp")
            .HasColumnType("timestamp(3) without time zone");

        builder.Property(x => x.CompanyId)
            .HasColumnName("company_id")
            .IsRequired();

        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(FieldLengths.ItemCategoryName)
            .UseCollation("C")
            .IsRequired();

        builder.Property(x => x.Prefix)
            .HasColumnName("prefix")
            .HasMaxLength(FieldLengths.ItemCategoryPrefix)
            .UseCollation("C")
            .IsRequired();

        builder.Property(x => x.Description)
            .HasColumnName("description")
            .HasColumnType("text")
            .IsRequired();

        builder.Ignore(x => x.JadeClassNumber);
        builder.Ignore(x => x.IsSoftLocked);

        builder.HasIndex(x => new { x.CompanyId, x.Name })
            .IsUnique()
            .HasDatabaseName("ix_item_categories_company_id_name");

        builder.HasOne(x => x.Company)
            .WithMany(c => c.ItemCategories)
            .HasForeignKey(x => x.CompanyId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_item_categories_companies_company_id");
    }
}

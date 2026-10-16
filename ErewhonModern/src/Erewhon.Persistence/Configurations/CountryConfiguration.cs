using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erewhon.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Country"/> to the <c>countries</c> table: the row identity, the GUID and soft-lock columns,
/// the owning company and the name, unique within the company and compared and ordered under the binary collation
/// <c>C</c>.
/// </summary>
public sealed class CountryConfiguration : IEntityTypeConfiguration<Country>
{
    /// <summary>
    /// Configures the <c>countries</c> table, its columns, the unique <c>(company_id, name)</c> index and the
    /// cascading foreign key to <c>companies</c>.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="Country"/> entity type.</param>
    public void Configure(EntityTypeBuilder<Country> builder)
    {
        builder.ToTable("countries");

        builder.HasKey(x => x.Id).HasName("pk_countries");

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

        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(FieldLengths.LocationName)
            .UseCollation("C")
            .IsRequired();

        builder.Ignore(x => x.JadeClassNumber);
        builder.Ignore(x => x.IsSoftLocked);

        builder.HasIndex(x => new { x.CompanyId, x.Name })
            .IsUnique()
            .HasDatabaseName("ix_countries_company_id_name");

        builder.HasOne(x => x.Company)
            .WithMany(c => c.Countries)
            .HasForeignKey(x => x.CompanyId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_countries_companies_company_id");
    }
}

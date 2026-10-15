using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erewhon.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Region"/> to the <c>regions</c> table: the row identity, the GUID and soft-lock columns,
/// the owning country and the name, unique within the country.
/// </summary>
public sealed class RegionConfiguration : IEntityTypeConfiguration<Region>
{
    /// <summary>
    /// Configures the <c>regions</c> table, its columns, the unique <c>(country_id, name)</c> index and the
    /// cascading foreign key to <c>countries</c>.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="Region"/> entity type.</param>
    public void Configure(EntityTypeBuilder<Region> builder)
    {
        builder.ToTable("regions");

        builder.HasKey(x => x.Id).HasName("pk_regions");

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

        builder.Property(x => x.CountryId)
            .HasColumnName("country_id")
            .IsRequired();

        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(FieldLengths.LocationName)
            .IsRequired();

        builder.Ignore(x => x.JadeClassNumber);
        builder.Ignore(x => x.IsSoftLocked);

        builder.HasIndex(x => new { x.CountryId, x.Name })
            .IsUnique()
            .HasDatabaseName("ix_regions_country_id_name");

        builder.HasOne(x => x.Country)
            .WithMany(c => c.Regions)
            .HasForeignKey(x => x.CountryId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_regions_countries_country_id");
    }
}

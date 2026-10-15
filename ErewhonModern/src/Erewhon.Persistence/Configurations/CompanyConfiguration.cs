using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erewhon.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Company"/> to the <c>companies</c> table: the row identity, the GUID and soft-lock columns,
/// the name and the address stored in the company's own row.
/// </summary>
public sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    /// <summary>
    /// Configures the <c>companies</c> table, its columns and the owned <see cref="Address"/> columns.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="Company"/> entity type.</param>
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        builder.ToTable("companies");

        builder.HasKey(x => x.Id).HasName("pk_companies");

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

        builder.Property(x => x.Name)
            .HasColumnName("name")
            .HasMaxLength(FieldLengths.CompanyName)
            .IsRequired();

        builder.OwnsOne(x => x.Address, address =>
        {
            address.Property(a => a.Street)
                .HasColumnName("address_street")
                .HasMaxLength(FieldLengths.AddressStreet)
                .IsRequired();

            address.Property(a => a.City)
                .HasColumnName("address_city")
                .HasMaxLength(FieldLengths.AddressCity)
                .IsRequired();

            address.Property(a => a.Country)
                .HasColumnName("address_country")
                .HasMaxLength(FieldLengths.AddressCountry)
                .IsRequired();

            address.Property(a => a.Phone)
                .HasColumnName("address_phone")
                .HasMaxLength(FieldLengths.AddressPhone)
                .IsRequired();

            address.Property(a => a.Fax)
                .HasColumnName("address_fax")
                .HasMaxLength(FieldLengths.AddressFax)
                .IsRequired();

            address.Property(a => a.Email)
                .HasColumnName("address_email")
                .HasMaxLength(FieldLengths.AddressEmail)
                .IsRequired();

            address.Property(a => a.WebSite)
                .HasColumnName("address_web_site")
                .HasMaxLength(FieldLengths.AddressWebSite)
                .IsRequired();

            address.Property(a => a.Guid)
                .HasColumnName("address_guid")
                .HasMaxLength(FieldLengths.Guid)
                .IsRequired();
        });

        builder.Navigation(x => x.Address).IsRequired();
    }
}

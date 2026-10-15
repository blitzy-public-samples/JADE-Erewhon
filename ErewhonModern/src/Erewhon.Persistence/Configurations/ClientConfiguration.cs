using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erewhon.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Client"/> to the <c>clients</c> table: the row identity, the GUID and soft-lock columns,
/// the owning company, the name, unique within the company, and the address stored in the client's row.
/// </summary>
public sealed class ClientConfiguration : IEntityTypeConfiguration<Client>
{
    /// <summary>
    /// Configures the <c>clients</c> table, its columns, the owned <see cref="Address"/> columns, the unique
    /// <c>(company_id, name)</c> index and the cascading foreign key to <c>companies</c>.
    /// </summary>
    /// <param name="builder">The builder for the <see cref="Client"/> entity type.</param>
    public void Configure(EntityTypeBuilder<Client> builder)
    {
        builder.ToTable("clients");

        builder.HasKey(x => x.Id).HasName("pk_clients");

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
            .HasMaxLength(FieldLengths.ClientName)
            .IsRequired();

        builder.Ignore(x => x.JadeClassNumber);
        builder.Ignore(x => x.IsSoftLocked);

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

        builder.HasIndex(x => new { x.CompanyId, x.Name })
            .IsUnique()
            .HasDatabaseName("ix_clients_company_id_name");

        builder.HasOne(x => x.Company)
            .WithMany(c => c.Clients)
            .HasForeignKey(x => x.CompanyId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_clients_companies_company_id");
    }
}

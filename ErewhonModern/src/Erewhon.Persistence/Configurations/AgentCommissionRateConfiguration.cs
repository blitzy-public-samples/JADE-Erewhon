using Erewhon.Application.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erewhon.Persistence.Configurations;

/// <summary>
/// Maps <see cref="AgentCommissionRate"/> to the table <c>agent_commission_rates</c>: one row per agent and commission
/// rate, at most one rate per item category for each agent, deleted with its agent or its rate.
/// </summary>
public sealed class AgentCommissionRateConfiguration : IEntityTypeConfiguration<AgentCommissionRate>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AgentCommissionRate> builder)
    {
        builder.ToTable("agent_commission_rates");

        builder.HasKey(x => new { x.AgentId, x.CommissionRateId }).HasName("pk_agent_commission_rates");

        builder.Property(x => x.AgentId)
            .HasColumnName("agent_id")
            .IsRequired();

        builder.Property(x => x.CommissionRateId)
            .HasColumnName("commission_rate_id")
            .IsRequired();

        builder.Property(x => x.ItemCategoryId)
            .HasColumnName("item_category_id")
            .IsRequired();

        builder.HasIndex(x => new { x.AgentId, x.ItemCategoryId })
            .IsUnique()
            .HasDatabaseName("ix_agent_commission_rates_agent_id_item_category_id");

        builder.HasIndex(x => x.CommissionRateId)
            .HasDatabaseName("ix_agent_commission_rates_commission_rate_id");

        builder.HasOne(x => x.Agent)
            .WithMany(a => a.AgentCommissionRates)
            .HasForeignKey(x => x.AgentId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_agent_commission_rates_agents_agent_id");

        builder.HasOne(x => x.CommissionRate)
            .WithMany(r => r.AgentCommissionRates)
            .HasForeignKey(x => x.CommissionRateId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_agent_commission_rates_commission_rates_commission_rate_id");
    }
}
